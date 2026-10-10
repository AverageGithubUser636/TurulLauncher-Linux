using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using global::Avalonia.VisualTree;
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
        // Csúszkák élő hatása: a VM írja a settings-et, a shell alkalmazza.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.WindowOpacityPct)
                or nameof(SettingsViewModel.UiScalePct))
                RefreshShell();
        };
        // A csúszka-pöcök kék maradna: a Fluent sablon helyi értékei a
        // stílus-beállítást felülírják, ezért kódból színezzük (lokális érték).
        // A sablonok csak az első layout után léteznek — ezért legfeljebb
        // 10× próbálkozunk (200 ms-enként), az első sikeres színezésig.
        this.AttachedToVisualTree += (_, _) =>
        {
            var tries = 0;
            var timer = new global::Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            timer.Tick += (_, _) =>
            {
                tries++;
                if (StyleSliderThumbs() > 0 || tries >= 10)
                    timer.Stop();
            };
            timer.Start();
        };
        Services.ThemeService.Current.Changed += OnThemeChangedForThumbs;
        _ = _vm.InitializeAsync();
    }

    private void OnThemeChangedForThumbs()
    {
        StyleSliderThumbs();
    }

    /// <returns>A színezett pöcök-részek száma (0 = még nincs sablon).</returns>
    private int StyleSliderThumbs()
    {
        var styled = 0;
        try
        {
            var accent = new global::Avalonia.Media.SolidColorBrush(
                Services.ThemeService.Current.AccentColor());
            foreach (var thumb in this.GetVisualDescendants()
                         .OfType<global::Avalonia.Controls.Primitives.Thumb>())
            {
                thumb.Background = accent;
                thumb.BorderBrush = accent;
                // A Thumb sablonja egy névtelen Bordert rajzol, ami nem köti
                // a Thumb.Background-et — azt közvetlenül színezzük.
                foreach (var border in thumb.GetVisualDescendants()
                             .OfType<global::Avalonia.Controls.Border>())
                {
                    border.Background = accent;
                    border.BorderBrush = accent;
                    styled++;
                }
            }
        }
        catch { }
        return styled;
    }

    private async void OnCreateProfile(object? sender, RoutedEventArgs e)
        => await _vm.CreateProfileAsync();

    private void OnOpenDataRoot(object? sender, RoutedEventArgs e)
        => _vm.OpenDataRoot();

    private async void OnSave(object? sender, RoutedEventArgs e)
        => await _vm.SaveAsync();

    private void OnApplyAccent(object? sender, RoutedEventArgs e)
        => _vm.ApplyCustomAccent();

    private void OnResetAccent(object? sender, RoutedEventArgs e)
        => _vm.ResetAccent();

    private async void OnPickBackground(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(
                new global::Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Háttérkép választása",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new global::Avalonia.Platform.Storage.FilePickerFileType("Kép") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } }
                    }
                });
            var picked = files.FirstOrDefault()?.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(picked)) return;
            await _vm.SetBackgroundAsync(picked);
            RefreshShell();
        }
        catch (Exception ex)
        {
            _vm.BackgroundStatus = "Fájlválasztási hiba: " + ex.Message;
        }
    }

    private void OnClearBackground(object? sender, RoutedEventArgs e)
    {
        _vm.ClearBackground();
        RefreshShell();
    }

    /// <summary>Azonnali hatás a shellre (a Mentés csak perzisztál).</summary>
    private void RefreshShell()
    {
        try
        {
            (TopLevel.GetTopLevel(this) as ShellWindow)?.ApplyAppearance();
        }
        catch { }
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

/// <summary>Kód + magyar felirat páros legördülőkhöz (viselkedés, csatorna).</summary>
public sealed class ChoiceOption
{
    public ChoiceOption(string name, string label)
    {
        Name = name;
        Label = label;
    }

    public string Name { get; }
    public string Label { get; }
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
    private string _customAccentText = "";
    private string _backgroundStatus = "";
    private double _windowOpacityPct = 100;
    private double _uiScalePct = 100;
    private bool _animationsEnabled = true;

    public string NewUsername { get => _newUsername; set => Raise(ref _newUsername, value); }
    public string ProfileText { get => _profileText; private set => Raise(ref _profileText, value); }
    public string SaveText { get => _saveText; private set => Raise(ref _saveText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public string DataRoot { get; } = Core.Storage.LauncherPaths.DataRoot;

    public List<ChoiceOption> GameStartOptions { get; } = new()
    {
        new("tray", "Tálcára (minimalizál)"),
        new("minimize", "Minimalizál"),
        new("keep", "Megtart (változatlan)"),
        new("exit", "Launcher bezárása"),
    };

    public List<ChoiceOption> CloseOptions { get; } = new()
    {
        new("ask", "Rákérdezés"),
        new("minimize", "Minimalizálás"),
        new("exit", "Kilépés"),
    };

    public List<ChoiceOption> ChannelOptions { get; } = new()
    {
        new("stable", "Stabil"),
        new("beta", "Béta (esetleg törött!)"),
    };

    private ChoiceOption ByName(List<ChoiceOption> options, string? name, string fallback)
        => options.FirstOrDefault(o => o.Name == name) ?? options.First(o => o.Name == fallback);

    private string _gameStart = "tray";
    private string _closeBehavior = "ask";
    private string _updateChannel = "stable";

    public ChoiceOption GameStartBehavior
    {
        get => ByName(GameStartOptions, _gameStart, "tray");
        set
        {
            if (value is null || value.Name == _gameStart) return;
            _gameStart = value.Name;
            _dirty = true;
            OnPropertyChanged();
        }
    }

    public ChoiceOption CloseBehavior
    {
        get => ByName(CloseOptions, _closeBehavior, "ask");
        set
        {
            if (value is null || value.Name == _closeBehavior) return;
            _closeBehavior = value.Name;
            _dirty = true;
            OnPropertyChanged();
        }
    }

    public ChoiceOption UpdateChannel
    {
        get => ByName(ChannelOptions, _updateChannel, "stable");
        set
        {
            if (value is null || value.Name == _updateChannel) return;
            _updateChannel = value.Name;
            _dirty = true;
            OnPropertyChanged();
        }
    }

    public string CustomAccentText
    {
        get => _customAccentText;
        set => Raise(ref _customAccentText, value);
    }

    public string BackgroundStatus
    {
        get => _backgroundStatus;
        internal set => Raise(ref _backgroundStatus, value);
    }

    public double WindowOpacityPct
    {
        get => _windowOpacityPct;
        set
        {
            var clamped = Math.Clamp(value, 40, 100);
            if (!Raise(ref _windowOpacityPct, clamped)) return;
            OnPropertyChanged(nameof(WindowOpacityText));
            _services.Settings.WindowOpacity = clamped / 100.0;
            _dirty = true;
        }
    }

    public string WindowOpacityText => $"{_windowOpacityPct:F0}%";

    public double UiScalePct
    {
        get => _uiScalePct;
        set
        {
            var clamped = Math.Clamp(value, 85, 120);
            if (!Raise(ref _uiScalePct, clamped)) return;
            OnPropertyChanged(nameof(UiScaleText));
            _services.Settings.UiScalePercent = (int)Math.Round(clamped);
            _dirty = true;
        }
    }

    public string UiScaleText => $"{_uiScalePct:F0}%";

    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (!Raise(ref _animationsEnabled, value)) return;
            _dirty = true;
            _services.Settings.AnimationsEnabled = value;
            Services.ThemeService.Current.SetAnimationsEnabled(value);
        }
    }

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
        _gameStart = string.IsNullOrWhiteSpace(s.GameStartBehavior) ? "tray" : s.GameStartBehavior;
        _closeBehavior = string.IsNullOrWhiteSpace(s.CloseBehavior) ? "ask" : s.CloseBehavior;
        _updateChannel = string.IsNullOrWhiteSpace(s.UpdateChannel) ? "stable" : s.UpdateChannel;
        Services.ThemeService.Current.Apply(_theme);
        if (!string.IsNullOrWhiteSpace(s.CustomAccent))
            Services.ThemeService.Current.ApplyCustomAccent(s.CustomAccent);
        Services.ThemeService.Current.SetAnimationsEnabled(s.AnimationsEnabled);
        _customAccentText = s.CustomAccent ?? "";
        _windowOpacityPct = Math.Clamp(s.WindowOpacity is <= 0 ? 1.0 : s.WindowOpacity, 0.4, 1.0) * 100.0;
        _uiScalePct = Math.Clamp(s.UiScalePercent <= 0 ? 100 : s.UiScalePercent, 85, 120);
        _animationsEnabled = s.AnimationsEnabled;
        _backgroundStatus = string.IsNullOrWhiteSpace(s.BackgroundImage)
            ? "Nincs háttérkép."
            : "Beállítva: " + s.BackgroundImage;
        OnPropertyChanged(nameof(RamMb));
        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(JavaPath));
        OnPropertyChanged(nameof(AutoInstallJava));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(GameStartBehavior));
        OnPropertyChanged(nameof(CloseBehavior));
        OnPropertyChanged(nameof(UpdateChannel));
        OnPropertyChanged(nameof(CustomAccentText));
        OnPropertyChanged(nameof(CustomAccentText));
        OnPropertyChanged(nameof(BackgroundStatus));
        OnPropertyChanged(nameof(WindowOpacityPct));
        OnPropertyChanged(nameof(WindowOpacityText));
        OnPropertyChanged(nameof(UiScalePct));
        OnPropertyChanged(nameof(UiScaleText));
        OnPropertyChanged(nameof(AnimationsEnabled));

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

    /// <summary>Egyedi akcentus alkalmazása (azonnal + mentésre).</summary>
    public void ApplyCustomAccent()
    {
        var hex = (CustomAccentText ?? "").Trim();
        if (Services.ThemeService.Current.ApplyCustomAccent(hex))
        {
            _services.Settings.CustomAccent = hex.StartsWith('#') ? hex : "#" + hex;
            _dirty = true;
            SaveText = "Egyedi akcentus alkalmazva.";
        }
        else
        {
            SaveText = "Érvénytelen színkód (pl. #D4AF37).";
        }
    }

    /// <summary>Vissza a téma saját akcentusához.</summary>
    public void ResetAccent()
    {
        CustomAccentText = "";
        _services.Settings.CustomAccent = "";
        Services.ThemeService.Current.Apply(_theme);
        _dirty = true;
        SaveText = "Téma-akcentus visszaállítva.";
    }

    /// <summary>Háttérkép beállítása (másolva a backgrounds mappába).</summary>
    public async Task SetBackgroundAsync(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                BackgroundStatus = "A fájl nem található.";
                return;
            }
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
            {
                BackgroundStatus = "Csak képfájl lehet (png/jpg/webp/bmp).";
                return;
            }
            var size = new FileInfo(sourcePath).Length;
            if (size is <= 0 or > 15L * 1024 * 1024)
            {
                BackgroundStatus = "Érvénytelen méret (max. 15 MB).";
                return;
            }

            var dir = Path.Combine(DataRoot, "backgrounds");
            Directory.CreateDirectory(dir);
            DeleteBackgroundFile(silent: true);
            var target = Path.Combine(dir, "háttér-" + Guid.NewGuid().ToString("N") + ext);
            File.Copy(sourcePath, target);
            _services.Settings.BackgroundImage = Path.GetFileName(target);
            BackgroundStatus = "Beállítva: " + Path.GetFileName(sourcePath);
            _dirty = true;
        }
        catch (Exception ex)
        {
            BackgroundStatus = "Hiba: " + ex.Message;
            LauncherLogger.Error("Háttérkép hiba: " + ex.Message);
        }
        await Task.CompletedTask;
    }

    /// <summary>Háttérkép törlése (fájllal együtt).</summary>
    public void ClearBackground()
    {
        DeleteBackgroundFile(silent: true);
        _services.Settings.BackgroundImage = "";
        BackgroundStatus = "Nincs háttérkép.";
        _dirty = true;
    }

    private void DeleteBackgroundFile(bool silent)
    {
        try
        {
            var current = _services.Settings.BackgroundImage;
            if (string.IsNullOrWhiteSpace(current)) return;
            var full = Path.Combine(DataRoot, "backgrounds", Path.GetFileName(current));
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex)
        {
            if (!silent) BackgroundStatus = "Hiba: " + ex.Message;
            LauncherLogger.Warning("Háttérkép törlési hiba: " + ex.Message);
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
            s.GameStartBehavior = _gameStart;
            s.CloseBehavior = _closeBehavior;
            s.UpdateChannel = _updateChannel;
            // A megjelenés-mezők (téma/akcentus/háttér/átlátszóság/skala/animáció)
            // már a _services.Settings-be írnak azonnali hatással —
            // itt csak perzisztáljuk őket.
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
