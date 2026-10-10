using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Minecraft;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Játékbetöltő-figyelő: élő napló, <see cref="GameLoadingStatus"/>-alapú
/// státusz, eltelt idő. A játékablak megjelenésekor (X11-érzékeléssel) vagy
/// tiszta kilépéskor magától záródik; hibás kilépéskor nyitva marad a
/// farok-naplóval. A Windows-verzió GameLoadingWindow megfelelője.
/// </summary>
public partial class GameLoadingWindow : Window
{
    private const int MaxLines = 200;

    private readonly IMinecraftLauncherService _launcher;
    private readonly string _startBehavior;
    private readonly Window? _owner;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly List<string> _lines = new();
    private readonly DispatcherTimer _timer;

    private EventHandler<string>? _outputHandler;
    private EventHandler<MinecraftProcessExitedEventArgs>? _exitHandler;
    private bool _finished;
    private bool _readySeen;

    public GameLoadingWindow(
        Window? owner,
        string instanceName,
        string detail,
        IMinecraftLauncherService launcher,
        string startBehavior)
    {
        InitializeComponent();
        _owner = owner;
        _launcher = launcher;
        _startBehavior = (startBehavior ?? "keep").ToLowerInvariant();

        InstanceNameText.Text = string.IsNullOrWhiteSpace(instanceName) ? "Minecraft" : instanceName;
        DetailText.Text = detail ?? "";
        RefreshLogo();
        Services.ThemeService.Current.Changed += OnThemeChanged;

        _outputHandler = (_, line) => AppendLog(line);
        _exitHandler = (_, e) => OnGameExited(e);
        launcher.ProcessOutput += _outputHandler;
        launcher.MinecraftExited += _exitHandler;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        Closed += (_, _) => Cleanup();
    }

    private void OnThemeChanged() => Dispatcher.UIThread.Post(RefreshLogo);

    private void RefreshLogo()
    {
        try
        {
            using var stream = global::Avalonia.Platform.AssetLoader.Open(
                new Uri(Services.ThemeService.Current.LogoUri));
            LogoImage.Source = new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch { }
    }

    private void OnHide(object? sender, RoutedEventArgs e)
    {
        // A játék fut tovább — csak az ablakot rejtjük el.
        Hide();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
            return;
        }

        var trimmed = GameLoadingStatus.TrimForLog(line);
        _lines.Add(trimmed);
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        LogBox.Text = string.Join(Environment.NewLine, _lines);
        LogCountText.Text = $"{_lines.Count} sor";
        try { LogScroller.ScrollToEnd(); } catch { }

        var friendly = GameLoadingStatus.MapLogLine(line);
        if (friendly is not null && !_finished)
            StatusText.Text = friendly;
    }

    private void OnTick()
    {
        if (_finished) return;
        ElapsedText.Text = FormatElapsed(DateTime.UtcNow - _startedUtc);

        var status = SafeStatus();
        if (status is null || !status.Running) return;

        // Ablak-érzékelés háttérben (eszközhívás blokkolhat).
        if (!_readySeen && status.ProcessId.HasValue)
        {
            var pid = status.ProcessId.Value;
            _ = Task.Run(() =>
            {
                if (Services.GameWindowDetector.HasVisibleWindow(pid))
                    Dispatcher.UIThread.Post(MarkLoaded);
            });
        }
    }

    private MinecraftProcessStatus? SafeStatus()
    {
        try { return _launcher.GetStatus(); }
        catch { return null; }
    }

    private void OnGameExited(MinecraftProcessExitedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_finished) return;
            _finished = true;
            _timer.Stop();
            LoadingBar.IsIndeterminate = false;
            LoadingBar.Value = 100;

            if (e.ExitCode == 0)
            {
                StatusText.Text = "A játék szabályosan bezárult.";
                RestoreOwner();
                CloseAfter(TimeSpan.FromSeconds(2));
            }
            else
            {
                // Crash-magyarázat a naplófark + kilépési kód alapján
                // (Core-szabályok, magyarul + teendőkkel).
                var explanation = Core.Recovery.CrashExplanationService.Analyze(
                    string.IsNullOrWhiteSpace(e.OutputTail) ? null : e.OutputTail,
                    e.ExitCode);
                var detail = string.IsNullOrWhiteSpace(e.OutputTail)
                    ? $"Kilépési kód: {e.ExitCode}"
                    : e.OutputTail;
                if (explanation.Recognized)
                    MarkExplained(explanation.Title, explanation.Summary, explanation.Steps, detail);
                else
                    MarkFailed(detail);
            }
        });
    }

    /// <summary>A játékablak megjelent — kész, hamarosan zárunk.</summary>
    public void MarkLoaded()
    {
        if (_finished || _readySeen) return;
        _readySeen = true;
        StatusText.Text = "A játék elindult — jó játékot!";
        LoadingBar.IsIndeterminate = false;
        LoadingBar.Value = 100;
        CloseAfter(TimeSpan.FromSeconds(3));
        ApplyStartBehavior();
    }

    public void MarkFailed(string error)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        LoadingBar.IsIndeterminate = false;
        LoadingBar.Value = 100;
        StatusText.Text = "A játék hibával leállt: " + error;
        ApplyFailedStyle();
    }

    /// <summary>Felismert összeomlás: cím + magyarázat + teendők + naplófarok.</summary>
    public void MarkExplained(string title, string summary, string[] steps, string tail)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        LoadingBar.IsIndeterminate = false;
        LoadingBar.Value = 100;
        StatusText.Text = title;
        ExplainTitle.Text = title;
        ExplainTitle.IsVisible = true;
        var lines = new List<string> { summary };
        if (steps is { Length: > 0 })
        {
            lines.Add("");
            foreach (var step in steps.Take(4))
                lines.Add("• " + step);
        }
        ExplainBody.Text = string.Join(Environment.NewLine, lines);
        ExplainBody.IsVisible = true;
        AppendLog("──── naplófarok ────");
        AppendLog(tail);
        ApplyFailedStyle();
    }

    private void ApplyFailedStyle()
    {
        try
        {
            StatusText.Foreground =
                new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#E5484D"));
        }
        catch { }
        HideButton.IsVisible = false;
        CloseButton.IsVisible = true;
        RestoreOwner();
    }

    private void ApplyStartBehavior()
    {
        try
        {
            switch (_startBehavior)
            {
                case "exit":
                    // A bezárás a 3 mp-es CloseAfter lejártakor történik
                    // (lásd ott) — itt csak a minimalizálható esetek vannak.
                    break;
                case "minimize":
                    if (_owner is not null)
                        _owner.WindowState = WindowState.Minimized;
                    break;
                case "tray":
                    // Tálcára játék közben; ha nincs tálca, minimalizál.
                    if (_owner is not null && !Services.TrayManager.HideToTray(_owner))
                        _owner.WindowState = WindowState.Minimized;
                    break;
                case "keep":
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Start behavior hiba: " + ex.Message);
        }
    }

    private void RestoreOwner()
    {
        try
        {
            // Tálcáról és minimalizálásból is visszaállít.
            Services.TrayManager.Restore(_owner);
        }
        catch { }
    }

    private void CloseAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { Close(); } catch { }

            // "exit" viselkedés: a játék betöltött, a launcher bezár.
            // (Hibás kilépéskor idáig el sem jutunk — az ablak nyitva marad.)
            if (_startBehavior == "exit" && _readySeen)
            {
                try
                {
                    if (global::Avalonia.Application.Current?.ApplicationLifetime
                        is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown();
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning("Leállítás hiba: " + ex.Message);
                }
            }
        };
        timer.Start();
    }

    private void Cleanup()
    {
        _finished = true;
        try { _timer.Stop(); } catch { }
        try
        {
            if (_outputHandler is not null) _launcher.ProcessOutput -= _outputHandler;
            if (_exitHandler is not null) _launcher.MinecraftExited -= _exitHandler;
        }
        catch { }
        try { Services.ThemeService.Current.Changed -= OnThemeChanged; } catch { }
    }

    private static string FormatElapsed(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
}
