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

        // A lapváltás: a nézetek állandóak, itt csak a láthatoságot állítjuk.
        // A sorrendnek egyeznie kell a NavItems sorrendjével.
        vm.SelectionChanged += index =>
        {
            HomePage.IsVisible = index == 0;
            ModsPage.IsVisible = index == 1;
            PacksPage.IsVisible = index == 2;
            ModpacksPage.IsVisible = index == 3;
            ServersPage.IsVisible = index == 4;
            JavaPage.IsVisible = index == 5;
            SettingsPage.IsVisible = index == 6;
            DoctorPage.IsVisible = index == 7;
        };

        Opened += (_, _) => vm.OnOpened();
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

public sealed class NavItem
{
    public required string Icon { get; init; }
    public required string Label { get; init; }
}

public sealed class ShellViewModel : PropertyChangedBase
{
    private int _selectedNavIndex;
    private string _title = "TurulLauncher";

    public string VersionText { get; } = "v" + AppInfo.Version;
    public string DataRootText { get; } = Core.Storage.LauncherPaths.DataRoot;
    public string Title
    {
        get => _title;
        private set => Raise(ref _title, value);
    }

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new() { Icon = "▶", Label = "Játék" },
        new() { Icon = "◈", Label = "Modok" },
        new() { Icon = "▣", Label = "Textúrák" },
        new() { Icon = "📦", Label = "Modpackok" },
        new() { Icon = "●", Label = "Szerverek" },
        new() { Icon = "☕", Label = "Java" },
        new() { Icon = "⚙", Label = "Beállítások" },
        new() { Icon = "✚", Label = "Doctor" },
    ];

    /// <summary>A kijelölt lap váltásakor fut — a nézet erre kapcsolja a láthatóságot.</summary>
    public event Action<int>? SelectionChanged;

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            Raise(ref _selectedNavIndex, value);
            ApplySelection();
        }
    }

    /// <summary>
    /// A kijelölt oldal alkalmazása. Külön metódus, mert induláskor az index
    /// már 0, és a <c>Raise</c> ilyenkor „nem változott"-ot jelez — enélkül a
    /// legelső lap sosem jelenne meg (ez volt a fekete tartalom oka).
    /// </summary>
    private void ApplySelection()
    {
        if (_selectedNavIndex < 0 || _selectedNavIndex >= NavItems.Count) return;
        var item = NavItems[_selectedNavIndex];
        Title = item.Label + " — TurulLauncher";
        SelectionChanged?.Invoke(_selectedNavIndex);
    }

    public void OnOpened()
    {
        StartupLog.Trace("ShellWindow megnyitva; DataRoot=" + Core.Storage.LauncherPaths.DataRoot);
        ApplySelection();
    }
}
