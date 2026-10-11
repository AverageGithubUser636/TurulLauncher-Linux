using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TurulMC.Core.Logging;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Önálló, kirakat-szerű frissítőoldal: verzió-hero, változásnapló-idővonal,
/// nagy telepítőgomb. A logikát a Beállítások-kártyáról örökölte (az megszűnt).
/// </summary>
public partial class UpdateView : UserControl
{
    private readonly UpdateViewModel _vm;

    public UpdateView()
    {
        InitializeComponent();
        _vm = new UpdateViewModel();
        DataContext = _vm;
        RefreshLogo();
        ThemeService.Current.Changed += OnThemeChanged;
        _ = _vm.InitializeAsync();
    }

    private void OnThemeChanged()
        => global::Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLogo);

    private void RefreshLogo()
    {
        try
        {
            using var stream = global::Avalonia.Platform.AssetLoader.Open(
                new Uri(ThemeService.Current.LogoUri));
            LogoImage.Source = new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch { }
    }

    private async void OnCheck(object? sender, RoutedEventArgs e)
        => await _vm.CheckUpdateAsync();

    private async void OnAction(object? sender, RoutedEventArgs e)
        => await _vm.UpdateActionAsync(OpenUrl);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => _vm.Cancel();

    private void OnToggleLog(object? sender, RoutedEventArgs e)
        => _vm.LogVisible = !_vm.LogVisible;

    private static async Task<string?> OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            await Task.CompletedTask;
        }
    }
}

public sealed class UpdateViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private string _subtitle = "";
    private string _statusText = "Még nem futott ellenőrzés — vagy automatikusan lefutott induláskor.";
    private string _newVersionText = "";
    private string _actionText = "Telepítés";
    private bool _updateAvailable;
    private bool _upToDate;
    private bool _required;
    private bool _busy;
    private bool _installing;
    private double _progress;
    private bool _progressVisible;
    private bool _logVisible;
    private string _modeText = "";
    private string _phaseText = "";
    private System.Threading.CancellationTokenSource? _installCts;
    private Core.Update.LauncherUpdateManifest? _pendingManifest;

    public ObservableCollection<string> Changelog { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public string CurrentVersionText { get; } = "v" + AppInfo.Version;
    public string NewVersionText { get => _newVersionText; private set => Raise(ref _newVersionText, value); }
    public string ChannelText => "csatorna: " + (_services.Settings.UpdateChannel == "beta" ? "beta" : "stable");
    public string StatusText { get => _statusText; private set => Raise(ref _statusText, value); }
    public string ActionText { get => _actionText; private set => Raise(ref _actionText, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set => Raise(ref _updateAvailable, value); }
    public bool IsUpToDate { get => _upToDate; private set => Raise(ref _upToDate, value); }
    public bool IsRequired { get => _required; private set => Raise(ref _required, value); }
    public bool CanCheck => !_busy;
    public bool CanCancel => _busy && _installing;
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public bool LogVisible { get => _logVisible; set => Raise(ref _logVisible, value); }
    public string LogToggleText => LogVisible ? "Napló elrejtése" : $"Napló ({LogLines.Count} sor)";
    public string PhaseText { get => _phaseText; private set => Raise(ref _phaseText, value); }
    public string ModeText { get => _modeText; private set => Raise(ref _modeText, value); }
    public bool HasChangelog => Changelog.Count > 0;

    public UpdateViewModel()
    {
        Changelog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChangelog));
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogToggleText));
    }

    /// <summary>Folyamatban lévő telepítés megszakítása (Mégse gomb).</summary>
    public void Cancel()
    {
        try { _installCts?.Cancel(); }
        catch { }
    }

    private void AddLog(string line)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            while (LogLines.Count > 200) LogLines.RemoveAt(0);
        });
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            OnPropertyChanged(nameof(ChannelText));
            Subtitle = $"TurulLauncher Linux {CurrentVersionText} · {ChannelText}";
            ModeText = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE"))
                ? "AppImage-ből futsz: a telepítés egy kattintás — az új AppImage SHA-256 ellenőrzéssel átveszi a mostani helyét, utána a launcher újraindul."
                : Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory)
                ? "Hordozható módban futsz: a telepítés egy kattintás, SHA-256 ellenőrzéssel, utána a launcher újraindul."
                : "Nem hordozható módban futsz (csomagos/dev telepítés): a frissítés a letöltési oldalt nyitja meg — a csomagkezelőddel vagy kézzel telepíts.";

            // Ha az induláskori auto-check már talált valamit, azt mutatjuk.
            var cached = _services.LastUpdateCheck;
            if (cached?.Available == true && cached.Manifest is not null)
                ApplyResult(cached);
            else if (cached is not null && string.IsNullOrWhiteSpace(cached.Error))
                ApplyUpToDate(cached.Channel);
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Frissítés nézet init hiba: " + ex.Message);
        }
    }

    public async Task CheckUpdateAsync()
    {
        if (_busy) return;
        _busy = true;
        OnPropertyChanged(nameof(CanCheck));
        StatusText = "Frissítés ellenőrzése…";
        Changelog.Clear();
        UpdateAvailable = false;
        IsUpToDate = false;
        IsRequired = false;
        NewVersionText = "";
        _pendingManifest = null;

        try
        {
            var channel = _services.Settings.UpdateChannel;
            var result = await _services.Updates.CheckAsync(AppInfo.Version, channel);
            _services.LastUpdateCheck = result;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                StatusText = result.Error;
                return;
            }
            if (!result.Available || result.Manifest is null)
            {
                ApplyUpToDate(result.Channel);
                return;
            }
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            StatusText = "Ellenőrzési hiba: " + ex.Message;
            LauncherLogger.Error("Frissítés-ellenőrzés hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanCheck));
        }
    }

    private void ApplyUpToDate(string channel)
    {
        IsUpToDate = true;
        StatusText = $"Naprakész vagy ({CurrentVersionText}, {channel}).";
    }

    private void ApplyResult(Core.Update.UpdateCheckResult result)
    {
        var manifest = result.Manifest!;
        _pendingManifest = manifest;
        NewVersionText = "v" + manifest.Version;
        foreach (var line in manifest.Changelog.Take(12))
            Changelog.Add(line);

        var portable = Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory);
        ActionText = portable ? "Telepítés és újraindítás" : "Letöltés megnyitása";
        UpdateAvailable = true;
        IsRequired = manifest.Required;
        StatusText = manifest.Required
            ? $"KÖTELEZŐ frissítés érhető el (jelenlegi: {CurrentVersionText})."
            : $"Új verzió érhető el (jelenlegi: {CurrentVersionText}).";
    }

    public async Task UpdateActionAsync(Func<string, Task<string?>> openUrl)
    {
        var manifest = _pendingManifest;
        if (manifest is null || _busy) return;

        // 1) AppImage: egykattintásos öncsere a $APPIMAGE útvonalon
        //    (nincs portable-jelölő, mégis biztonságos: atomi átnevezés).
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
        {
            await RunInstallAsync(
                $"AppImage csere: v{manifest.Version}",
                (progress, ct) => _services.Updates.InstallAppImageAsync(
                    manifest, appImage, progress, ct));
            return;
        }

        if (!Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory))
        {
            StatusText = "Letöltési oldal megnyitása…";
            var error = await openUrl(manifest.Url);
            StatusText = error is null
                ? "A böngészőben folytasd a letöltést."
                : "Megnyitási hiba: " + error;
            return;
        }

        await RunInstallAsync(
            $"Telepítés: v{manifest.Version}",
            (progress, ct) => _services.Updates.InstallPortableAsync(
                manifest, AppContext.BaseDirectory, progress, ct));
    }

    /// <summary>
    /// Telepítés fázisokkal (Letöltés → Ellenőrzés → Telepítés → Kész),
    /// megszakíthatóan, naplózva — a Windows-updater mintájára.
    /// </summary>
    private async Task RunInstallAsync(
        string title,
        Func<IProgress<Core.Models.DownloadProgress>,
            System.Threading.CancellationToken,
            Task> install)
    {
        _busy = true;
        _installing = true;
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanCancel));
        ProgressVisible = true;
        Progress = 0;
        _installCts = new System.Threading.CancellationTokenSource();
        AddLog(title);

        try
        {
            PhaseText = "1/3 · Letöltés…";
            StatusText = title + "…";
            AddLog("Letöltés indul.");
            var progress = new Progress<Core.Models.DownloadProgress>(p =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Progress = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
                    if (p.TotalBytes > 0 && p.BytesReceived >= p.TotalBytes)
                        PhaseText = "2/3 · Ellenőrzés, telepítés…";
                    StatusText = Core.Update.UpdateProgressText.FormatDownload(
                        p.BytesReceived, p.TotalBytes, p.BytesPerSecond);
                }));

            await install(progress, _installCts.Token);

            PhaseText = "3/3 · Telepítve ✓";
            StatusText = "Telepítve — újraindítás…";
            AddLog("Telepítve, újraindítás.");
            await Task.Delay(800);
            RestartApp();
        }
        catch (OperationCanceledException)
        {
            PhaseText = "";
            StatusText = "Telepítés megszakítva — a launcher változatlan.";
            AddLog("Megszakítva a felhasználó kérésére.");
            LauncherLogger.Info("Frissítés megszakítva.");
        }
        catch (Exception ex)
        {
            PhaseText = "";
            StatusText = "Telepítési hiba: " + ex.Message;
            AddLog("Hiba: " + ex.Message);
            LauncherLogger.Error("Frissítés hiba: " + ex.Message);
        }
        finally
        {
            try { _installCts?.Dispose(); } catch { }
            _installCts = null;
            _busy = false;
            _installing = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    private static void RestartApp()
    {
        try
        {
            // AppImage-ből futva az APPIMAGE-útvonal a biztos (a ProcessPath
            // a FUSE-csatolásra is mutathat).
            var exe = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Újraindítás hiba: " + ex.Message);
            return;
        }
        Environment.Exit(0);
    }
}
