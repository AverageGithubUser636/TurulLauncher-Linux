using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using TurulMC.Core.Diagnostics;
using TurulMC.Core.Logging;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class DoctorView : UserControl
{
    private readonly DoctorViewModel _vm;

    public DoctorView()
    {
        // Generált InitializeComponent: betölti a XAML-t ÉS hozzárendeli az
        // x:Name mezőket (a kézi AvaloniaXamlLoader.Load a mezőket NULL-ná hagyná).
        InitializeComponent();
        _vm = new DoctorViewModel();
        DataContext = _vm;
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
        => await _vm.RunAsync();

    private async void OnBundle(object? sender, RoutedEventArgs e)
        => await _vm.CreateBundleAsync();
}

public sealed class CheckRow
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Badge { get; init; }
    public required IBrush BadgeBrush { get; init; }
    public required string DurationText { get; init; }
}

public sealed class DoctorViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private string _summaryText = "Nyomd meg a „Futtatás” gombot az ellenőrzésekhez.";
    private string _okText = "OK 0";
    private string _failText = "HIBA 0";
    private bool _running;

    public ObservableCollection<CheckRow> Checks { get; } = new();

    public string SummaryText { get => _summaryText; private set => Raise(ref _summaryText, value); }
    public string OkText { get => _okText; private set => Raise(ref _okText, value); }
    public string FailText { get => _failText; private set => Raise(ref _failText, value); }

    /// <summary>Nincs még eredmény — a lista helyét egy üres állapotú képernyő tölti ki.</summary>
    public bool IsEmpty => Checks.Count == 0;

    /// <summary>Van eredmény — a lista jelenjen meg.</summary>
    public bool IsNotEmpty => Checks.Count > 0;

    public DoctorViewModel()
    {
        // A lista tartalmának változásakor frissítjük az üres/nem üres jelzést,
        // hogy a nézet váltani tudjon a hint és a lista között.
        Checks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsNotEmpty));
        };
    }

    public async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        Checks.Clear();
        SummaryText = "Ellenőrzés folyamatban…";

        try
        {
            await _services.LoadSettingsAsync();
            var (activeId, instances) = _services.Instances.Load();
            var active = instances.FirstOrDefault(x => x.Id == activeId) ?? instances.FirstOrDefault();

            var instanceDir = active is null
                ? null
                : _services.Instances.GetInstanceDirectory(active.Id);

            var options = _services.BuildDoctorOptions(
                _services.Settings.JavaPathOverride,
                instanceDir,
                _services.Settings.TestServerHost,
                _services.Settings.TestServerPort);

            var doctor = new LauncherDoctor(
                options,
                _services.Java,
                _services.ServerStatus);

            var report = await doctor.RunAsync();

            foreach (var c in report.Checks)
                Checks.Add(ToRow(c));

            OkText = $"OK {report.OkCount}";
            FailText = $"HIBA {report.FailedCount}";
            SummaryText = report.SummaryHu;
        }
        catch (Exception ex)
        {
            SummaryText = "A Doctor futtatása nem sikerült: " + ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Support-csomag (logok + crash reportok + Doctor-jelentés).</summary>
    public async Task CreateBundleAsync()
    {
        if (_running) return;
        _running = true;
        SummaryText = "Support csomag készítése… (Doctor-jelentéssel)";
        try
        {
            await _services.LoadSettingsAsync();
            var (activeId, instances) = _services.Instances.Load();
            var active = instances.FirstOrDefault(x => x.Id == activeId) ?? instances.FirstOrDefault();
            var instanceDir = active is null
                ? null
                : _services.Instances.GetInstanceDirectory(active.Id);

            var options = _services.BuildDoctorOptions(
                _services.Settings.JavaPathOverride,
                instanceDir,
                _services.Settings.TestServerHost,
                _services.Settings.TestServerPort);
            var doctor = new LauncherDoctor(
                options,
                _services.Java,
                _services.ServerStatus);
            var bundle = await new Core.Diagnostics.SupportBundleService(options, doctor)
                .CreateAsync(includeDoctorReport: true);

            SummaryText = bundle.Success
                ? $"Support csomag kész: {bundle.FullPath} ({bundle.IncludedFiles.Count} fájl)"
                : "A csomag nem készült el: " + bundle.Error;
        }
        catch (Exception ex)
        {
            SummaryText = "Support csomag hiba: " + ex.Message;
            LauncherLogger.Error("Support csomag hiba: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    private static CheckRow ToRow(DoctorCheck c) => new()
    {
        Title = c.TitleHu,
        Detail = string.IsNullOrWhiteSpace(c.Detail)
            ? (c.FixHintHu ?? "")
            : c.Detail + (string.IsNullOrWhiteSpace(c.FixHintHu) ? "" : "  ·  " + c.FixHintHu),
        Badge = c.Status switch
        {
            DoctorStatus.Ok => "✓",
            DoctorStatus.Warning => "!",
            DoctorStatus.Failed => "✕",
            _ => "·"
        },
        BadgeBrush = c.Status switch
        {
            DoctorStatus.Ok => Brushes.LimeGreen,
            DoctorStatus.Warning => Brushes.Gold,
            DoctorStatus.Failed => Brushes.IndianRed,
            _ => new SolidColorBrush(Color.Parse("#565A62"))
        },
        DurationText = c.DurationMs > 0 ? $"{c.DurationMs} ms" : ""
    };
}
