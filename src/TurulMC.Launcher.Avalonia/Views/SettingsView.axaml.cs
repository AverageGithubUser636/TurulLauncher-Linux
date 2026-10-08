using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TurulMC.Core.Logging;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView()
    {
        // Generált InitializeComponent: betölti a XAML-t ÉS hozzárendeli az
        // x:Name mezőket (a kézi AvaloniaXamlLoader.Load a mezőket NULL-ná hagyná).
        InitializeComponent();
        _vm = new SettingsViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private async void OnCreateProfile(object? sender, RoutedEventArgs e)
        => await _vm.CreateProfileAsync();

    private void OnOpenDataRoot(object? sender, RoutedEventArgs e)
        => _vm.OpenDataRoot();

    private async void OnSave(object? sender, RoutedEventArgs e)
        => await _vm.SaveAsync();

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
        => await _vm.CheckUpdateAsync();

    private async void OnUpdateAction(object? sender, RoutedEventArgs e)
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

public sealed class ThemeOption
{
    public ThemeOption(string name, string label, string accent)
    {
        Name = name;
        Label = label;
        Swatch = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(accent));
    }

    public string Name { get; }
    public string Label { get; }
    public global::Avalonia.Media.IBrush Swatch { get; }
}

public sealed class SettingsViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private string _newUsername = "";
    private string _profileText = "";
    private string _saveText = "";
    private string _subtitle = "";
    private double _ramMb;
    private string _minecraftVersion = "";
    private string _javaPath = "";
    private bool _autoInstallJava = true;
    private string _theme = "yellow";
    private string _updateStatus = "";
    private string _updateActionText = "Telepítés";
    private bool _updateAvailable;
    private bool _updateBusy;
    private double _updateProgress;
    private bool _updateProgressVisible;
    private Core.Update.LauncherUpdateManifest? _pendingManifest;

    public string NewUsername { get => _newUsername; set => Raise(ref _newUsername, value); }
    public string ProfileText { get => _profileText; private set => Raise(ref _profileText, value); }
    public string SaveText { get => _saveText; private set => Raise(ref _saveText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public string DataRoot { get; } = Core.Storage.LauncherPaths.DataRoot;

    public string CurrentVersionText { get; } = "v" + AppInfo.Version;
    public string UpdateChannelText => _services.Settings.UpdateChannel == "beta" ? "beta" : "stable";
    public string UpdateStatus { get => _updateStatus; private set => Raise(ref _updateStatus, value); }
    public string UpdateActionText { get => _updateActionText; private set => Raise(ref _updateActionText, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set => Raise(ref _updateAvailable, value); }
    public bool CanCheckUpdate => !_updateBusy;
    public double UpdateProgress { get => _updateProgress; private set => Raise(ref _updateProgress, value); }
    public bool UpdateProgressVisible { get => _updateProgressVisible; private set => Raise(ref _updateProgressVisible, value); }
    public System.Collections.ObjectModel.ObservableCollection<string> Changelog { get; } = new();
    public bool HasChangelog => Changelog.Count > 0;

    public double RamMb
    {
        get => _ramMb;
        set { if (Raise(ref _ramMb, value)) _dirty = true; }
    }

    public string MinecraftVersion
    {
        get => _minecraftVersion;
        set { if (Raise(ref _minecraftVersion, value)) _dirty = true; }
    }

    public string JavaPath
    {
        get => _javaPath;
        set { if (Raise(ref _javaPath, value)) _dirty = true; }
    }

    public bool AutoInstallJava
    {
        get => _autoInstallJava;
        set { if (Raise(ref _autoInstallJava, value)) _dirty = true; }
    }

    /// <summary>Témaválaszték a legördülőhöz (színmintával).</summary>
    public List<ThemeOption> ThemeOptions { get; } =
        Services.ThemeService.Themes
            .Select(t => new ThemeOption(t.Name, t.LabelHu, t.Accent))
            .ToList();

    public ThemeOption Theme
    {
        get => ThemeOptions.FirstOrDefault(t => t.Name == _theme) ?? ThemeOptions[0];
        set
        {
            if (value is null || value.Name == _theme) return;
            _theme = value.Name;
            _dirty = true;
            OnPropertyChanged();
            // Azonnali visszajelzés: a mentés a Mentés gombbal történik.
            Services.ThemeService.Current.Apply(_theme);
        }
    }

    private bool _dirty;

    public async Task CheckUpdateAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        OnPropertyChanged(nameof(CanCheckUpdate));
        UpdateStatus = "Frissítés ellenőrzése…";
        Changelog.Clear();
        OnPropertyChanged(nameof(HasChangelog));
        UpdateAvailable = false;
        _pendingManifest = null;

        try
        {
            var channel = _services.Settings.UpdateChannel;
            var result = await _services.Updates.CheckAsync(AppInfo.Version, channel);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                UpdateStatus = result.Error;
                return;
            }
            if (!result.Available || result.Manifest is null)
            {
                UpdateStatus = $"Naprakész (v{AppInfo.Version}, {result.Channel}).";
                return;
            }

            _pendingManifest = result.Manifest;
            foreach (var line in result.Manifest.Changelog.Take(10))
                Changelog.Add("• " + line);
            OnPropertyChanged(nameof(HasChangelog));

            var portable = Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory);
            UpdateActionText = portable ? "Telepítés és újraindítás" : "Letöltés megnyitása";
            UpdateAvailable = true;
            UpdateStatus = result.Manifest.Required
                ? $"KÖTELEZŐ frissítés: v{result.Manifest.Version} (jelenlegi: v{AppInfo.Version})."
                : $"Új verzió: v{result.Manifest.Version} (jelenlegi: v{AppInfo.Version}).";
        }
        catch (Exception ex)
        {
            UpdateStatus = "Ellenőrzési hiba: " + ex.Message;
            LauncherLogger.Error("Frissítés-ellenőrzés hiba: " + ex.Message);
        }
        finally
        {
            _updateBusy = false;
            OnPropertyChanged(nameof(CanCheckUpdate));
        }
    }

    /// <summary>
    /// Hordozható módban telepít + újraindít, egyébként a letöltési
    /// oldalt nyitja meg. Az <c>openUrl</c> a nézet dolga (tesztelhetőség).
    /// </summary>
    public async Task UpdateActionAsync(Func<string, Task<string?>> openUrl)
    {
        var manifest = _pendingManifest;
        if (manifest is null || _updateBusy) return;

        if (!Core.Update.UpdateService.IsPortable(AppContext.BaseDirectory))
        {
            UpdateStatus = "Letöltési oldal megnyitása…";
            var error = await openUrl(manifest.Url);
            UpdateStatus = error is null
                ? "A böngészőben folytasd a letöltést."
                : "Megnyitási hiba: " + error;
            return;
        }

        _updateBusy = true;
        OnPropertyChanged(nameof(CanCheckUpdate));
        UpdateProgressVisible = true;
        try
        {
            var progress = new Progress<Core.Models.DownloadProgress>(p =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UpdateStatus = $"Letöltés: {p.FileName}";
                    UpdateProgress = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
                }));

            UpdateStatus = $"Telepítés: v{manifest.Version}…";
            await _services.Updates.InstallPortableAsync(
                manifest, AppContext.BaseDirectory, progress);

            UpdateStatus = "Telepítve — újraindítás…";
            await Task.Delay(800);
            RestartApp();
        }
        catch (Exception ex)
        {
            UpdateStatus = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error("Hordozható frissítés hiba: " + ex.Message);
        }
        finally
        {
            _updateBusy = false;
            UpdateProgressVisible = false;
            UpdateProgress = 0;
            OnPropertyChanged(nameof(CanCheckUpdate));
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

    public async Task InitializeAsync()
    {
        var s = await _services.LoadSettingsAsync();
        _ramMb = s.DefaultRamMb;
        _minecraftVersion = s.MinecraftVersion;
        _javaPath = s.JavaPathOverride ?? "";
        _autoInstallJava = s.AutoInstallJava;
        _theme = string.IsNullOrWhiteSpace(s.Theme) ? "yellow" : s.Theme;
        Services.ThemeService.Current.Apply(_theme);
        OnPropertyChanged(nameof(RamMb));
        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(JavaPath));
        OnPropertyChanged(nameof(AutoInstallJava));
        OnPropertyChanged(nameof(Theme));

        Subtitle = $"Adatkönyvtár (XDG): {DataRoot}";
        await RefreshProfileAsync();
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var profile = await _services.Auth.GetCurrentProfileAsync();
            ProfileText = profile is null
                ? "Nincs aktív profil."
                : $"Aktív: {profile.Username} (offline) · UUID {profile.Uuid}";
        }
        catch (Exception ex)
        {
            ProfileText = "Profil olvasási hiba: " + ex.Message;
        }
    }

    public async Task CreateProfileAsync()
    {
        var name = NewUsername.Trim();
        if (name.Length == 0)
        {
            ProfileText = "Adj meg egy felhasználónevet.";
            return;
        }
        try
        {
            var result = await _services.Auth.CreateLocalProfileAsync(name);
            if (!result.Success)
            {
                ProfileText = result.ErrorMessage ?? "A profil létrehozása nem sikerült.";
                return;
            }
            NewUsername = "";
            await RefreshProfileAsync();
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Profil létrehozás hiba: " + ex.Message);
            ProfileText = "Hiba: " + ex.Message;
        }
    }

    public async Task SaveAsync()
    {
        if (!_dirty) { SaveText = "Nincs változtatás."; return; }
        try
        {
            var s = _services.Settings;
            s.DefaultRamMb = (int)Math.Clamp(RamMb, 1024, 65536);
            s.MinecraftVersion = MinecraftVersion.Trim();
            s.JavaPathOverride = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
            s.AutoInstallJava = AutoInstallJava;
            s.Theme = _theme;
            await _services.SaveSettingsAsync();
            _dirty = false;
            SaveText = "Mentve ✓";
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Beállítás mentés hiba: " + ex.Message);
            SaveText = "Mentési hiba: " + ex.Message;
        }
    }

    public void OpenDataRoot()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SaveText = "Nem sikerült megnyitni: " + ex.Message;
        }
    }
}
