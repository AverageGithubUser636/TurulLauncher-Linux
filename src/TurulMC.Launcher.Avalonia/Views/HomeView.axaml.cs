using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _vm;

    public HomeView()
    {
        // Generált InitializeComponent: betölti a XAML-t ÉS hozzárendeli az
        // x:Name mezőket (a kézi AvaloniaXamlLoader.Load a mezőket NULL-ná hagyná).
        InitializeComponent();
        _vm = new HomeViewModel();
        DataContext = _vm;

        // Az instance lista és a profil betöltése induláskor. Ez az a hívás,
        // ami nélkül a nézet üres marad (sem a profil sor, sem az üres állapot
        // szövege nem jelenik meg) — a SettingsView már így csinálta.
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Az instance lista betöltése nem sikerült: " + ex.Message;
            LauncherLogger.Error("Home inicializálás sikertelen: " + ex);
        }
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
        => await _vm.PlayAsync(TopLevel.GetTopLevel(this) as Window);

    private async void OnCreateInstance(object? sender, RoutedEventArgs e)
    {
        var template = new LauncherInstance
        {
            Name = _vm.Instances.Count == 0 ? "Alap Instance" : $"Instance {_vm.Instances.Count + 1}",
            MinecraftVersion = _vm.SettingsMinecraftVersion,
            Loader = _vm.SettingsLoader,
            RamMb = Math.Max(1024, _vm.SettingsRamMb),
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new InstanceEditWindow(template, isNew: true);
        var result = owner is null ? null : await dialog.ShowDialog<bool?>(owner);
        if (result != true) return;

        await _vm.AddInstanceAsync(dialog.Result);
    }

    private async void OnEditInstance(object? sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedInstance;
        if (selected is null)
        {
            _vm.StatusText = "Válassz ki egy Instance-t a szerkesztéshez.";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new InstanceEditWindow(selected, isNew: false);
        var result = owner is null ? null : await dialog.ShowDialog<bool?>(owner);
        if (result != true) return;

        await _vm.SaveEditedAsync();
    }

    private async void OnDeleteInstance(object? sender, RoutedEventArgs e)
        => await _vm.DeleteSelectedAsync();

    private void OnGotoUpdate(object? sender, RoutedEventArgs e)
        => _vm.GoToUpdate();
}

public sealed class HomeViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private string _statusText = "Kész.";
    private string _profileLine = "";
    private string _emptyText = "";
    private double _progress;
    private bool _progressVisible;
    private bool _busy;
    private string _activeId = "";

    public ObservableCollection<LauncherInstance> Instances { get; } = new();

    public LauncherInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!Raise(ref _selectedInstance, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            RefreshHero();
        }
    }

    /// <summary>Megjelenített állapotszöveg. Az setter belső, hogy a nézet
    /// (másik osztály) is jelezheti a betöltési hibát.</summary>
    public string StatusText { get => _statusText; internal set => Raise(ref _statusText, value); }
    public string ProfileLine { get => _profileLine; private set => Raise(ref _profileLine, value); }
    public string EmptyText { get => _emptyText; private set => Raise(ref _emptyText, value); }

    // HERO-kötések az aktív Instance-ről.
    public string HeroName => SelectedInstance?.Name ?? "Nincs Instance";
    public string HeroVersion => SelectedInstance is null ? "—" : $"MC {SelectedInstance.MinecraftVersion}";
    public string HeroLoader => SelectedInstance is null
        ? "—"
        : string.Equals(SelectedInstance.Loader, "fabric", StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(SelectedInstance.LoaderVersion)
                ? "Fabric" : $"Fabric {SelectedInstance.LoaderVersion}"
            : "Vanilla";
    public string HeroMods => _heroMods;
    public bool HasMods => _heroModsCount > 0;
    public string HeroLastPlayed => SelectedInstance is null
        ? ""
        : SelectedInstance.LastUsed == default
            ? "Még sosem indítva"
            : $"Utoljára játszva: {SelectedInstance.LastUsed.ToLocalTime():yyyy.MM.dd. HH:mm}";

    private string _heroMods = "";
    private int _heroModsCount;
    private bool _updateBannerVisible;
    private string _updateBannerTitle = "";
    private string _updateBannerText = "";
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public bool IsEmpty => Instances.Count == 0;
    public bool CanPlay => !_busy && SelectedInstance is not null;

    public bool UpdateBannerVisible { get => _updateBannerVisible; private set => Raise(ref _updateBannerVisible, value); }
    public string UpdateBannerTitle { get => _updateBannerTitle; private set => Raise(ref _updateBannerTitle, value); }
    public string UpdateBannerText { get => _updateBannerText; private set => Raise(ref _updateBannerText, value); }

    /// <summary>Ugrás a Frissítés-lapra (a shell végzi a váltást).</summary>
    public void GoToUpdate() => _services.RequestNavigate(7);

    /// <summary>Az új-Instance dialógus sablon-értékei a globális beállításokból.</summary>
    public string SettingsMinecraftVersion => _services.Settings.MinecraftVersion;
    public string SettingsLoader => _services.Settings.Loader;
    public int SettingsRamMb => _services.Settings.DefaultRamMb;

    public async Task InitializeAsync()
    {
        await _services.LoadSettingsAsync();
        ReloadInstances();
        await RefreshProfileAsync();
        RefreshUpdateBanner(_services.LastUpdateCheck);

        // Induláskori auto-check később futhat le — akkor frissítünk.
        _services.UpdateCheckCompleted -= OnUpdateCheckCompleted;
        _services.UpdateCheckCompleted += OnUpdateCheckCompleted;

        // Másik nézet (pl. modpack-telepítés) miatti változásra újratöltünk.
        _services.InstancesChanged -= OnInstancesChanged;
        _services.InstancesChanged += OnInstancesChanged;
    }

    private void OnInstancesChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReloadInstances();
            _ = RefreshProfileAsync();
        });
    }

    private void OnUpdateCheckCompleted()
    {
        Dispatcher.UIThread.Post(() => RefreshUpdateBanner(_services.LastUpdateCheck));
    }

    private void RefreshUpdateBanner(Core.Update.UpdateCheckResult? check)
    {
        if (check?.Available == true && check.Manifest is not null)
        {
            UpdateBannerTitle = $"Új verzió elérhető: v{check.Manifest.Version}";
            UpdateBannerText = "Kattints a részletekért és a telepítésért.";
            UpdateBannerVisible = true;
        }
        else
        {
            UpdateBannerVisible = false;
        }
    }

    /// <summary>Lista újratöltése a lemezről (külső változás után is).</summary>
    public void ReloadInstances()
    {
        var (active, list) = _services.Instances.Load();
        _activeId = active;
        Instances.Clear();
        foreach (var i in list) Instances.Add(i);

        SelectedInstance = Instances.FirstOrDefault(x => x.Id == active) ?? Instances.FirstOrDefault();
        EmptyText = Instances.Count == 0
            ? "Még nincs Instance. Kattints az „Új Instance” gombra a kezdéshez."
            : "";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanPlay));
        RefreshHero();
    }

    /// <summary>HERO-frissítés: név, chipek, modszám az aktív Instance-ről.</summary>
    private void RefreshHero()
    {
        var count = 0;
        if (SelectedInstance is not null)
        {
            try
            {
                var modsDir = Path.Combine(
                    _services.Instances.GetInstanceDirectory(SelectedInstance.Id), "mods");
                if (Directory.Exists(modsDir))
                    count = Directory.GetFiles(modsDir, "*.jar*").Length;
            }
            catch { }
        }
        _heroModsCount = count;
        _heroMods = count == 0 ? "" : count == 1 ? "1 mod" : $"{count} mod";
        OnPropertyChanged(nameof(HeroName));
        OnPropertyChanged(nameof(HeroVersion));
        OnPropertyChanged(nameof(HeroLoader));
        OnPropertyChanged(nameof(HeroMods));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(HeroLastPlayed));
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var profile = await _services.Auth.GetCurrentProfileAsync();
            ProfileLine = profile is null
                ? "Nincs profil — az offline játékhoz hozz létre egyet a Beállításokban."
                : $"{profile.Username} · offline profil";
        }
        catch (Exception ex)
        {
            ProfileLine = "Profil nem olvasható: " + ex.Message;
        }
    }

    public async Task PlayAsync(Window? owner)
    {
        var instance = SelectedInstance;
        if (instance is null || _busy) return;

        _busy = true;
        OnPropertyChanged(nameof(CanPlay));
        ProgressVisible = true;

        try
        {
            var progress = new Progress<LaunchProgress>(p => SetProgress(p.Text, p.Percent));
            var message = await _services.LaunchInstanceAsync(instance, progress);

            // A VM saját listáját szinkronizáljuk (LastUsed változott).
            _activeId = instance.Id;
            _services.Instances.Save(Instances.ToList(), _activeId);
            StatusText = message;

            // Betöltőfigyelő: élő napló + státusz a játék ablakáig.
            var loaderLabel = string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase)
                ? $"Fabric {instance.LoaderVersion}".Trim()
                : "Vanilla";
            GameLoading.Show(owner, instance.Name,
                $"MC {instance.MinecraftVersion} · {loaderLabel}",
                _services.CurrentLauncher,
                _services.Settings.GameStartBehavior);
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Játékindítás sikertelen: " + ex);
            StatusText = "Indítási hiba: " + ex.Message;
            Progress = 0;
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            OnPropertyChanged(nameof(CanPlay));
        }
    }

    public async Task AddInstanceAsync(LauncherInstance created)
    {
        Instances.Add(created);
        _activeId = created.Id;
        _services.Instances.Save(Instances.ToList(), _activeId);
        SelectedInstance = created;
        StatusText = $"Létrehozva: {created.Name}";
        OnPropertyChanged(nameof(IsEmpty));
        await Task.CompletedTask;
    }

    /// <summary>Szerkesztés után: a kiválasztott példány már módosítva van
    /// (a dialógus visszaírta), csak menteni és frissíteni kell.
    /// A lista-sort kivesszük és visszatesszük, mert a
    /// <see cref="LauncherInstance"/> nem értesít a változásról.</summary>
    public async Task SaveEditedAsync()
    {
        var current = SelectedInstance;
        _services.Instances.Save(Instances.ToList(), _activeId);
        if (current is not null)
        {
            var index = Instances.IndexOf(current);
            if (index >= 0)
            {
                Instances.RemoveAt(index);
                Instances.Insert(Math.Min(index, Instances.Count), current);
                SelectedInstance = current;
            }
            StatusText = $"Mentve: {current.Name}";
        }
        else
        {
            StatusText = "Mentve.";
        }
        OnPropertyChanged(nameof(IsEmpty));
        await Task.CompletedTask;
    }

    public async Task DeleteSelectedAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;
        Instances.Remove(instance);
        _services.Instances.Save(Instances.ToList(), _activeId);
        SelectedInstance = Instances.FirstOrDefault();
        EmptyText = Instances.Count == 0
            ? "Még nincs Instance. Kattints az „Új Instance” gombra a kezdéshez."
            : "";
        OnPropertyChanged(nameof(IsEmpty));
        StatusText = $"Törölve: {instance.Name}";
        await Task.CompletedTask;
    }

    private void SetProgress(string text, double percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = text;
            Progress = percent;
            ProgressVisible = true;
        });
    }
}
