using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        // A generált InitializeComponent tölti be a XAML-t ÉS rendeli hozzá az
        // x:Name mezőket. A kézi AvaloniaXamlLoader.Load(this) csak a fát építi
        // meg, a mezők így NULL maradnának (FindControl még működne, de a
        // kódban hivatkozott mezők nem).
        InitializeComponent();
        var vm = new ShellViewModel();
        DataContext = vm;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Title))
                Title = vm.Title;
        };

        // Téma-logó: induláskor és váltáskor is frissül.
        RefreshLogo();
        Services.ThemeService.Current.Changed += RefreshLogo;

        // Nézetek közti navigáció (pl. Home-banner → Frissítés-lap).
        // Lapindex → nav-index (fejlécek miatt eltolva).
        Services.LauncherServices.Current.NavigateRequested += page =>
        {
            var nav = page switch { 0=>0, 1=>2, 2=>3, 3=>4, 4=>5, 5=>6, 6=>8, 7=>9, 8=>10, 9=>11, 10=>12, _=>-1 };
            if (nav >= 0)
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.SelectedNavIndex = nav);
        };

        // A lapváltás: a nézetek állandóak, itt csak a láthatoságot állítjuk.
        // A sorrendnek egyeznie kell a NavItems sorrendjével.
        vm.SelectionChanged += index =>
        {
            HomePage.IsVisible = index == 0;
            ModsPage.IsVisible = index == 1;
            PacksPage.IsVisible = index == 2;
            ShadersPage.IsVisible = index == 3;
            ModpacksPage.IsVisible = index == 4;
            ServersPage.IsVisible = index == 5;
            JavaPage.IsVisible = index == 6;
            SettingsPage.IsVisible = index == 7;
            UpdatePage.IsVisible = index == 8;
            DoctorPage.IsVisible = index == 9;
            LogsPage.IsVisible = index == 10;
        };

        Opened += (_, _) =>
        {
            vm.OnOpened();
            // A splash-folyamat eddigre betöltötte a beállításokat —
            // most már a mentett háttér/átlátszóság/skálával nyitunk.
            ApplyAppearance();
        };
        ApplyAppearance();
    }

    /// <summary>
    /// Megjelenés alkalmazása a beállításokból: háttérkép, átlátszóság,
    /// betűméret-skála. A Beállítások-nézet is innen frissít (azonnali hatás).
    /// </summary>
    public void ApplyAppearance()
    {
        try
        {
            var settings = Services.LauncherServices.Current.Settings;

            // Háttérkép (DataRoot/backgrounds/<fájl>), sötétítéssel.
            var bgFile = (settings.BackgroundImage ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(bgFile))
            {
                var full = System.IO.Path.Combine(
                    Core.Storage.LauncherPaths.DataRoot, "backgrounds",
                    System.IO.Path.GetFileName(bgFile));
                if (System.IO.File.Exists(full))
                {
                    try
                    {
                        using var stream = System.IO.File.OpenRead(full);
                        BackgroundImage.Source =
                            new global::Avalonia.Media.Imaging.Bitmap(stream);
                        BackgroundImage.IsVisible = true;
                        BackgroundDim.IsVisible = true;
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.LauncherLogger.Warning("Háttérkép betöltési hiba: " + ex.Message);
                        BackgroundImage.IsVisible = false;
                        BackgroundDim.IsVisible = false;
                    }
                }
                else
                {
                    BackgroundImage.IsVisible = false;
                    BackgroundDim.IsVisible = false;
                }
            }
            else
            {
                BackgroundImage.IsVisible = false;
                BackgroundDim.IsVisible = false;
            }

            // Ablak-átlátszóság (0.4–1.0).
            Opacity = Math.Clamp(settings.WindowOpacity is <= 0 ? 1.0 : settings.WindowOpacity, 0.4, 1.0);

            // Betűméret-skála (UiScalePercent, 50–150).
            var scale = Math.Clamp(settings.UiScalePercent <= 0 ? 100 : settings.UiScalePercent, 50, 150) / 100.0;
            ContentScaler.LayoutTransform = new global::Avalonia.Media.ScaleTransform(scale, scale);
        }
        catch (Exception ex)
        {
            Core.Logging.LauncherLogger.Warning("Megjelenés-alkalmazási hiba: " + ex.Message);
        }
    }

    private void RefreshLogo()
    {
        try
        {
            using var stream = global::Avalonia.Platform.AssetLoader.Open(
                new Uri(Services.ThemeService.Current.LogoUri));
            LogoImage.Source = new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            Core.Logging.LauncherLogger.Warning("Logó betöltési hiba: " + ex.Message);
        }
    }
}

public sealed class NavItem : PropertyChangedBase
{
    public required string Icon { get; init; }
    public required string Label { get; init; }
    public bool IsHeader { get; init; }
    public bool IsItem => !IsHeader;

    private bool _hasBadge;
    /// <summary>Arany pötty a címke mellett (pl. elérhető frissítés).</summary>
    public bool HasBadge
    {
        get => _hasBadge;
        set => Raise(ref _hasBadge, value);
    }
}

public sealed class ShellViewModel : PropertyChangedBase
{
    private int _selectedNavIndex;
    private int _lastValidIndex;
    private string _title = "TurulLauncher";
    private string _profileName = "…";
    private string _profileInitial = "?";

    public string VersionText { get; } = "v" + AppInfo.Version;
    public string DataRootText { get; } = Core.Storage.LauncherPaths.DataRoot;
    public string Title
    {
        get => _title;
        private set => Raise(ref _title, value);
    }

    public string ProfileName { get => _profileName; private set => Raise(ref _profileName, value); }
    public string ProfileInitial { get => _profileInitial; private set => Raise(ref _profileInitial, value); }

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new() { Icon = "🎮", Label = "Játék" },
        new() { Icon = "", Label = "Tartalom", IsHeader = true },
        new() { Icon = "🧩", Label = "Modok" },
        new() { Icon = "🗺", Label = "Textúrák" },
        new() { Icon = "✨", Label = "Shaderek" },
        new() { Icon = "📦", Label = "Modpackok" },
        new() { Icon = "🌐", Label = "Szerverek" },
        new() { Icon = "", Label = "Rendszer", IsHeader = true },
        new() { Icon = "☕", Label = "Java" },
        new() { Icon = "⚙", Label = "Beállítások" },
        new() { Icon = "🔄", Label = "Frissítés" },
        new() { Icon = "⚕", Label = "Doctor" },
        new() { Icon = "📋", Label = "Naplók" },
    ];

    /// <summary>A kijelölt lap váltásakor fut — a nézet erre kapcsolja a láthatóságot.</summary>
    public event Action<int>? SelectionChanged;

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            // Fejléc nem választható: vissza az utolsó érvényesre.
            if (value >= 0 && value < NavItems.Count && NavItems[value].IsHeader)
                value = _lastValidIndex;
            Raise(ref _selectedNavIndex, value);
            _lastValidIndex = _selectedNavIndex;
            ApplySelection();
        }
    }

    /// <summary>
    /// Nav-index → lapindex leképezés (a fejlécek eltolják: a SelectionChanged
    /// már a tiszta lapindexet kapja, nem a nyers nav-indexet).
    /// </summary>
    private static int PageIndexOf(int navIndex) => navIndex switch
    {
        0 => 0, // Játék
        2 => 1, // Modok
        3 => 2, // Textúrák
        4 => 3, // Shaderek
        5 => 4, // Modpackok
        6 => 5, // Szerverek
        8 => 6, // Java
        9 => 7, // Beállítások
        10 => 8, // Frissítés
        11 => 9, // Doctor
        12 => 10, // Naplók
        _ => -1
    };

    /// <summary>
    /// A kijelölt oldal alkalmazása. Külön metódus, mert induláskor az index
    /// már 0, és a <c>Raise</c> ilyenkor „nem változott"-ot jelez — enélkül a
    /// legelső lap sosem jelenne meg (ez volt a fekete tartalom oka).
    /// </summary>
    private void ApplySelection()
    {
        if (_selectedNavIndex < 0 || _selectedNavIndex >= NavItems.Count) return;
        var item = NavItems[_selectedNavIndex];
        if (item.IsHeader) return;
        Title = item.Label + " — TurulLauncher";
        var page = PageIndexOf(_selectedNavIndex);
        if (page < 0) return;
        // A Frissítés-lap megnyitásakor a jelvény okafogyottá válik.
        if (page == 7)
        {
            foreach (var n in NavItems) n.HasBadge = false;
        }
        SelectionChanged?.Invoke(page);
    }

    public void OnOpened()
    {
        StartupLog.Trace("ShellWindow megnyitva; DataRoot=" + Core.Storage.LauncherPaths.DataRoot);
        ApplySelection();
        _ = RefreshProfileAsync();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Induláskori néma frissítés-ellenőrzés (ha a beállítás engedi).
    /// Találatnál arany pötty a Frissítés-menüre — a részletek a lapon.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var services = Services.LauncherServices.Current;
            await services.LoadSettingsAsync();
            if (!services.Settings.UpdateChecksEnabled) return;

            var result = await services.Updates.CheckAsync(
                AppInfo.Version, services.Settings.UpdateChannel);
            services.LastUpdateCheck = result;
            services.NotifyUpdateCheckCompleted();
            if (result.Available && result.Manifest is not null)
            {
                StartupLog.Trace($"Frissítés elérhető: v{result.Manifest.Version}");
                var item = NavItems.FirstOrDefault(n => n.Label == "Frissítés");
                if (item is not null) item.HasBadge = true;
            }
        }
        catch (Exception ex)
        {
            StartupLog.Trace("Induláskori frissítés-ellenőrzés hiba: " + ex.Message);
        }
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var profile = await Services.LauncherServices.Current.Auth.GetCurrentProfileAsync();
            var name = profile?.Username;
            ProfileName = string.IsNullOrWhiteSpace(name) ? "Nincs profil" : name;
            ProfileInitial = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        }
        catch
        {
            ProfileName = "Nincs profil";
            ProfileInitial = "?";
        }
    }
}
