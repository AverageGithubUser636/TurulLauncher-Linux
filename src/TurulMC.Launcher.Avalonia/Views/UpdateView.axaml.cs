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
    private double _progress;
    private bool _progressVisible;
    private string _modeText = "";
    private Core.Update.LauncherUpdateManifest? _pendingManifest;

    public ObservableCollection<string> Changelog { get; } = new();

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
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public string ModeText { get => _modeText; private set => Raise(ref _modeText, value); }
    public bool HasChangelog => Changelog.Count > 0;

    public UpdateViewModel()
    {
        Changelog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChangelog));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            OnPropertyChanged(nameof(ChannelText));
            Subtitle = $"TurulLauncher Linux {CurrentVersionText} · {ChannelText}";
            ModeText = Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory)
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

        if (!Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory))
        {
            StatusText = "Letöltési oldal megnyitása…";
            var error = await openUrl(manifest.Url);
            StatusText = error is null
                ? "A böngészőben folytasd a letöltést."
                : "Megnyitási hiba: " + error;
            return;
        }

        _busy = true;
        OnPropertyChanged(nameof(CanCheck));
        ProgressVisible = true;
        try
        {
            var progress = new Progress<Core.Models.DownloadProgress>(p =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"Letöltés: {p.FileName}";
                    Progress = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
                }));

            StatusText = $"Telepítés: v{manifest.Version}…";
            await _services.Updates.InstallPortableAsync(
                manifest, AppContext.BaseDirectory, progress);

            StatusText = "Telepítve — újraindítás…";
            await Task.Delay(800);
            RestartApp();
        }
        catch (Exception ex)
        {
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error("Hordozható frissítés hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanCheck));
        }
    }

    private static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
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
