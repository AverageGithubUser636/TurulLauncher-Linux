using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class RepairView : UserControl
{
    private readonly RepairViewModel _vm;

    public RepairView()
    {
        InitializeComponent();
        _vm = new RepairViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
        => await _vm.ScanAsync();

    private async void OnFix(object? sender, RoutedEventArgs e)
        => await _vm.FixAsync();
}

public sealed class ProblemRow : PropertyChangedBase
{
    private bool _selected = true;

    public required string FileName { get; init; }
    public required string Title { get; init; }
    public required string Reason { get; init; }
    public required string BadgeText { get; init; }
    public required string BadgeBg { get; init; }
    public required string BadgeFg { get; init; }

    public bool Selected
    {
        get => _selected;
        set => Raise(ref _selected, value);
    }
}

public sealed class RepairViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private string _statusText = "";
    private string _summaryText = "";
    private string _emptyTitle = "Még nincs vizsgálat";
    private string _emptyText = "Válassz Instance-t és kattints a Vizsgálat gombra.";
    private string _fixMode = "Kikapcsolás (.jar.disabled)";
    private double _progress;
    private bool _progressVisible;
    private bool _busy;

    public ObservableCollection<LauncherInstance> Instances { get; } = new();
    public ObservableCollection<ProblemRow> Problems { get; } = new();

    public List<string> FixModes { get; } = new()
    {
        "Kikapcsolás (.jar.disabled)",
        "Karanténba (mods-quarantine)"
    };

    public LauncherInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!Raise(ref _selectedInstance, value)) return;
            ClearResults("Másik Instance — futtass új vizsgálatot.");
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string SummaryText { get => _summaryText; private set => Raise(ref _summaryText, value); }
    public string EmptyTitle { get => _emptyTitle; private set => Raise(ref _emptyTitle, value); }
    public string EmptyText { get => _emptyText; private set => Raise(ref _emptyText, value); }

    public string FixMode
    {
        get => _fixMode;
        set => Raise(ref _fixMode, value);
    }

    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public bool CanScan => !_busy && SelectedInstance is not null;
    public bool CanFix => !_busy && Problems.Any(p => p.Selected);
    public bool IsEmpty => Problems.Count == 0;

    public RepairViewModel()
    {
        Problems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanFix));
        };
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
            if (SelectedInstance is null)
                StatusText = "Nincs Instance — hozz létre egyet a Játék lapon.";
            _services.InstancesChanged -= OnInstancesChanged;
            _services.InstancesChanged += OnInstancesChanged;
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Repair init hiba: " + ex.Message);
        }
    }

    private void OnInstancesChanged()
        => Dispatcher.UIThread.Post(() =>
        {
            var (activeId, list) = _services.Instances.Load();
            var keepId = SelectedInstance?.Id ?? activeId;
            Instances.Clear();
            foreach (var i in list) Instances.Add(i);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == keepId)
                ?? Instances.FirstOrDefault();
        });

    private string ModsDir => SelectedInstance is null
        ? ""
        : Path.Combine(_services.Instances.GetInstanceDirectory(SelectedInstance.Id), "mods");

    public async Task ScanAsync()
    {
        if (SelectedInstance is null || _busy) return;
        _busy = true;
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanFix));
        ProgressVisible = true;
        try
        {
            SummaryText = "Vizsgálat…";
            var instance = SelectedInstance;
            var report = await Task.Run(() => Core.Mods.ModCompatibilityScanner.Scan(
                ModsDir, instance.MinecraftVersion, instance.Loader, instance.LoaderVersion));

            Problems.Clear();
            foreach (var p in report.Problems)
            {
                Problems.Add(new ProblemRow
                {
                    FileName = p.FileName,
                    Title = string.IsNullOrWhiteSpace(p.Title) ? p.FileName : p.Title,
                    Reason = string.IsNullOrWhiteSpace(p.ReasonHu) ? p.Status.ToString() : p.ReasonHu,
                    BadgeText = BadgeFor(p.Status),
                    BadgeBg = "#1A1D22",
                    BadgeFg = p.Status == Core.Mods.ModCompatibilityStatus.UnknownMetadata ? "#8A8D93" : "#E5484D"
                });
            }

            SummaryText = report.SummaryHu;
            if (report.Notices.Count > 0)
                SummaryText += " · " + string.Join(" ", report.Notices.Take(2));

            if (Problems.Count == 0)
            {
                EmptyTitle = "Minden rendben ✓";
                EmptyText = report.SummaryHu;
            }
            StatusText = Problems.Count == 0
                ? "Nincs javítandó mod."
                : $"{Problems.Count} problémás mod — válaszd ki és kattints a Javításra.";
        }
        catch (Exception ex)
        {
            StatusText = "Vizsgálati hiba: " + ex.Message;
            LauncherLogger.Error("Repair scan hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanFix));
        }
    }

    public async Task FixAsync()
    {
        if (SelectedInstance is null || _busy) return;
        var targets = Problems.Where(p => p.Selected).Select(p => p.FileName).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Válassz ki legalább egy modot a javításhoz.";
            return;
        }

        _busy = true;
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanFix));
        ProgressVisible = true;
        try
        {
            var mode = _fixMode.StartsWith("Karantén", StringComparison.OrdinalIgnoreCase)
                ? "quarantine" : "disable";

            StatusText = "Mentés készítése…";
            var backup = await _services.Repairs.BackupModsAsync(
                ModsDir, MakeProgress(), default);

            var fixed_ = await _services.Repairs.ApplyFixAsync(
                ModsDir, targets, mode, MakeProgress(), default);

            StatusText = $"{fixed_} mod javítva ({(mode == "quarantine" ? "karantén" : "kikapcsolva")}). " +
                         $"Backup: {backup}";
            await ScanAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Javítási hiba: " + ex.Message;
            LauncherLogger.Error("Repair fix hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanFix));
        }
    }

    private IProgress<(string Text, double Percent)> MakeProgress()
        => new Progress<(string Text, double Percent)>(p =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = p.Text;
                Progress = p.Percent;
            }));

    private void ClearResults(string status)
    {
        Problems.Clear();
        SummaryText = "";
        StatusText = status;
        EmptyTitle = "Még nincs vizsgálat";
        EmptyText = "Válassz Instance-t és kattints a Vizsgálat gombra.";
    }

    private static string BadgeFor(Core.Mods.ModCompatibilityStatus status) => status switch
    {
        Core.Mods.ModCompatibilityStatus.UnknownMetadata => "?",
        Core.Mods.ModCompatibilityStatus.Duplicate => "DUPLIKÁTUM",
        Core.Mods.ModCompatibilityStatus.NotFabric => "NEM FABRIC",
        _ => "HIBÁS"
    };
}
