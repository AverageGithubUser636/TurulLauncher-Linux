using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class LogsView : UserControl
{
    private readonly LogsViewModel _vm;

    public LogsView()
    {
        InitializeComponent();
        _vm = new LogsViewModel();
        DataContext = _vm;
        // A nézet a shell-lel együtt él (csak a láthatósága vált),
        // ezért elég egyszer feliratkozni.
        _vm.ContentLoaded += OnContentLoaded;
        _ = _vm.InitializeAsync();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => _vm.Refresh();

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
        => _vm.OpenFolder();

    private void OnContentLoaded()
    {
        try
        {
            var view = this.FindControl<ScrollViewer>("ContentScroller");
            view?.ScrollToEnd();
        }
        catch { }
    }
}
public sealed class LogFileRow : PropertyChangedBase
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string SizeText { get; init; }
    public required string ModifiedText { get; init; }
}

public sealed class LogsViewModel : PropertyChangedBase
{
    private const long MaxReadBytes = 256L * 1024;
    private const int MaxLines = 400;

    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private LogFileRow? _selectedFile;
    private string _statusText = "";
    private string _countText = "";
    private string _content = "";
    private string _contentHint = "";
    private string _selectedSource = "Játéknapló";

    public List<string> Sources { get; } = new() { "Játéknapló", "Összeomlás-jelentések", "Launcher-naplók" };
    public ObservableCollection<LauncherInstance> Instances { get; } = new();
    public ObservableCollection<LogFileRow> Files { get; } = new();

    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Raise(ref _selectedSource, value)) return;
            OnPropertyChanged(nameof(ShowInstancePicker));
            Refresh();
        }
    }

    public bool ShowInstancePicker => SelectedSource != "Launcher-naplók";

    public LauncherInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!Raise(ref _selectedInstance, value)) return;
            Refresh();
        }
    }

    public LogFileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Raise(ref _selectedFile, value)) return;
            LoadContent();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string CountText { get => _countText; private set => Raise(ref _countText, value); }
    public string Content { get => _content; private set => Raise(ref _content, value); }
    public string ContentHint { get => _contentHint; private set => Raise(ref _contentHint, value); }
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Új tartalom töltődött — a nézet a végére görget.</summary>
    public event Action? ContentLoaded;

    public LogsViewModel()
    {
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            var (activeId, list) = _services.Instances.Load();
            Instances.Clear();
            foreach (var i in list) Instances.Add(i);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == activeId)
                ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Napló nézet init hiba: " + ex.Message);
        }
    }

    public void Refresh()
    {
        Files.Clear();
        SelectedFile = null;
        Content = "";
        ContentHint = "";

        try
        {
            foreach (var (name, full) in EnumerateLogFiles())
            {
                var info = new FileInfo(full);
                Files.Add(new LogFileRow
                {
                    Name = name,
                    FullPath = full,
                    SizeText = FormatBytes(info.Length),
                    ModifiedText = info.LastWriteTime.ToString("MM.dd. HH:mm")
                });
            }
            CountText = Files.Count == 0 ? "" : $"{Files.Count} fájl";
            SelectedFile = Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Napló lista hiba: " + ex.Message);
        }
    }

    private IEnumerable<(string Name, string FullPath)> EnumerateLogFiles()
    {
        if (SelectedSource == "Launcher-naplók")
        {
            var dir = Core.Storage.LauncherPaths.LogsRoot;
            if (!Directory.Exists(dir)) yield break;
            foreach (var file in Directory.GetFiles(dir, "*.log")
                         .OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
                yield return (Path.GetFileName(file), file);
            yield break;
        }

        if (SelectedInstance is null) yield break;
        var gameDir = _services.Instances.GetInstanceDirectory(SelectedInstance.Id);
        var sub = SelectedSource == "Összeomlás-jelentések" ? "crash-reports" : "logs";
        var pattern = SelectedSource == "Összeomlás-jelentések" ? "*.txt" : "*.log";
        var dir2 = Path.Combine(gameDir, sub);
        if (!Directory.Exists(dir2)) yield break;
        foreach (var file in Directory.GetFiles(dir2, pattern)
                     .OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
            yield return (Path.GetFileName(file), file);
    }

    private void LoadContent()
    {
        var row = SelectedFile;
        if (row is null)
        {
            Content = "";
            ContentHint = "";
            return;
        }

        try
        {
            using var stream = File.Open(row.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var truncated = false;
            string text;
            if (stream.Length > MaxReadBytes)
            {
                truncated = true;
                stream.Seek(-MaxReadBytes, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            var lines = text.Split('\n');
            if (lines.Length > MaxLines)
            {
                truncated = true;
                lines = lines[^MaxLines..];
            }
            // Félbeszakadt első sor eldobása (középen nyitottunk).
            if (truncated && lines.Length > 0 && !text.StartsWith(lines[0]))
                lines = lines[1..];
            Content = string.Join('\n', lines).TrimEnd();
            ContentHint = truncated
                ? $"{row.Name} — csak az utolsó {MaxLines} sor (fájl vége)"
                : $"{row.Name} — teljes fájl";
            ContentLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            Content = "";
            ContentHint = "";
            StatusText = "Olvasási hiba: " + ex.Message;
            LauncherLogger.Error("Napló olvasási hiba: " + ex.Message);
        }
    }

    public void OpenFolder()
    {
        try
        {
            string? dir = SelectedSource == "Launcher-naplók"
                ? Core.Storage.LauncherPaths.LogsRoot
                : SelectedInstance is null
                    ? null
                    : Path.Combine(
                        _services.Instances.GetInstanceDirectory(SelectedInstance.Id),
                        SelectedSource == "Összeomlás-jelentések" ? "crash-reports" : "logs");
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = "Nem sikerült megnyitni: " + ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };
}
