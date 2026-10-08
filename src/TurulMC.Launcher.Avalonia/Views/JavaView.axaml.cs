using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class JavaView : UserControl
{
    private readonly JavaViewModel _vm;

    public JavaView()
    {
        InitializeComponent();
        _vm = new JavaViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
        => await _vm.RefreshAsync();

    private async void OnInstall(object? sender, RoutedEventArgs e)
        => await _vm.InstallAsync();

    private async void OnRemove(object? sender, RoutedEventArgs e)
        => await _vm.RemoveSelectedAsync();
}

public sealed class JavaRow : PropertyChangedBase
{
    public required string MajorText { get; init; }
    public required string Version { get; init; }
    public required string Path { get; init; }
    public required string SourceText { get; init; }

    /// <summary>Csak a launcher által telepített sor törölhető.</summary>
    public bool CanRemove { get; init; }

    public string? ProvisionedId { get; init; }
}

public sealed class JavaViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private JavaRow? _selectedRuntime;
    private string _statusText = "";
    private string _subtitle = "";
    private string _recommendedText = "";
    private int _selectedMajor = 21;
    private double _progress;
    private bool _progressVisible;
    private bool _busy;

    public ObservableCollection<JavaRow> Runtimes { get; } = new();
    public List<int> MajorOptions { get; } = new() { 8, 17, 21, 25 };

    public JavaRow? SelectedRuntime
    {
        get => _selectedRuntime;
        set => Raise(ref _selectedRuntime, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public string RecommendedText { get => _recommendedText; private set => Raise(ref _recommendedText, value); }
    public int SelectedMajor { get => _selectedMajor; set => Raise(ref _selectedMajor, value); }
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public bool CanInstall => !_busy;
    public bool IsEmpty => Runtimes.Count == 0;

    public JavaViewModel()
    {
        Runtimes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            UpdateSubtitle();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Java nézet init hiba: " + ex.Message);
        }
    }

    private void UpdateSubtitle()
    {
        var (activeId, list) = _services.Instances.Load();
        var active = list.FirstOrDefault(x => x.Id == activeId) ?? list.FirstOrDefault();
        if (active is null)
        {
            Subtitle = "Telepített és észlelt Java runtime-ok.";
            return;
        }
        var required = Core.Java.JavaVersionMap.ResolveRequiredMajor(active.MinecraftVersion);
        Subtitle = $"Az aktív Instance ({active.Name}, MC {active.MinecraftVersion}) Java {required}-et igényel.";
        if (!MajorOptions.Contains(required))
        {
            MajorOptions.Add(required);
            MajorOptions.Sort();
        }
        SelectedMajor = required;
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        OnPropertyChanged(nameof(CanInstall));
        StatusText = "Java runtime-ok felmérése… (elsőre eltarthat egy ideig)";
        try
        {
            // A felmérés lassú lehet (java -version próbák) — háttérben fut.
            var detected = await Task.Run(() => _services.Java.DetectInstalledRuntimesAsync());
            var provisioned = await _services.JavaProvisioner.ListProvisionedRuntimesAsync();

            Runtimes.Clear();
            SelectedRuntime = null;

            var provisionedIds = new HashSet<string>(
                provisioned.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var r in detected.OrderByDescending(x => x.Major).ThenBy(x => x.Path))
            {
                var isProvisioned = !string.IsNullOrWhiteSpace(r.Id) && provisionedIds.Contains(r.Id);
                Runtimes.Add(new JavaRow
                {
                    MajorText = r.Major > 0 ? r.Major.ToString() : "?",
                    Version = string.IsNullOrWhiteSpace(r.Version) ? "(ismeretlen verzió)" : r.Version,
                    Path = r.Path,
                    SourceText = SourceLabel(r.Source, isProvisioned),
                    CanRemove = isProvisioned,
                    ProvisionedId = isProvisioned ? r.Id : null
                });
            }

            await UpdateRecommendedAsync(detected);
            StatusText = Runtimes.Count == 0
                ? "Nincs felismert Java — telepíts jobboldalt."
                : $"{Runtimes.Count} runtime találva.";
        }
        catch (Exception ex)
        {
            StatusText = "Felmérési hiba: " + ex.Message;
            LauncherLogger.Error("Java felmérés hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    private async Task UpdateRecommendedAsync(List<JavaRuntime> detected)
    {
        try
        {
            var (activeId, list) = _services.Instances.Load();
            var active = list.FirstOrDefault(x => x.Id == activeId) ?? list.FirstOrDefault();
            if (active is null)
            {
                RecommendedText = "Nincs Instance — az ajánlás az aktív Instance alapján készül.";
                return;
            }
            var required = Core.Java.JavaVersionMap.ResolveRequiredMajor(active.MinecraftVersion);
            var recommended = await _services.Java.GetRecommendedRuntimeAsync(required.ToString());
            RecommendedText = recommended is null
                ? $"MC {active.MinecraftVersion} Java {required}-et kér, de nincs megfelelő. Telepíts jobboldalt."
                : $"MC {active.MinecraftVersion} → Java {recommended.Major}: {recommended.Path}";
        }
        catch (Exception ex)
        {
            RecommendedText = "Ajánlás nem készíthető: " + ex.Message;
        }
    }

    public async Task InstallAsync()
    {
        if (_busy) return;
        _busy = true;
        OnPropertyChanged(nameof(CanInstall));
        ProgressVisible = true;
        try
        {
            var major = SelectedMajor;
            var progress = new Progress<DownloadProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = string.IsNullOrWhiteSpace(p.FileName)
                        ? $"Java {major} telepítése…" : $"Java {major}: {p.FileName}";
                    Progress = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
                }));

            var runtime = await _services.Java.EnsureRuntimeAsync(major, allowInstall: true, progress);
            StatusText = $"Kész: Java {runtime.Major} — {runtime.Path}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error("Java telepítés hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    public async Task RemoveSelectedAsync()
    {
        var row = SelectedRuntime;
        if (row is null)
        {
            StatusText = "Válassz ki egy runtime-ot a törléshez.";
            return;
        }
        if (!row.CanRemove || string.IsNullOrWhiteSpace(row.ProvisionedId))
        {
            StatusText = "Csak a launcher által telepített runtime törölhető (a rendszer-Javához nem nyúlunk).";
            return;
        }
        if (_busy) return;
        _busy = true;
        OnPropertyChanged(nameof(CanInstall));
        try
        {
            var ok = await _services.JavaProvisioner.RemoveProvisionedRuntimeAsync(row.ProvisionedId);
            StatusText = ok ? $"Törölve: {row.ProvisionedId}" : "A törlés nem sikerült.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Törlési hiba: " + ex.Message;
            LauncherLogger.Error("Java törlés hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    private static string SourceLabel(string source, bool isProvisioned)
        => source switch
        {
            "override" => "felülírás",
            "provisioned" => "telepített",
            _ => isProvisioned ? "telepített" : "rendszer"
        };
}
