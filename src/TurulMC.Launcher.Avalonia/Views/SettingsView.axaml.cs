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

    public string NewUsername { get => _newUsername; set => Raise(ref _newUsername, value); }
    public string ProfileText { get => _profileText; private set => Raise(ref _profileText, value); }
    public string SaveText { get => _saveText; private set => Raise(ref _saveText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public string DataRoot { get; } = Core.Storage.LauncherPaths.DataRoot;

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
