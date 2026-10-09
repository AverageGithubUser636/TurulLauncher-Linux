using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class ModsView : UserControl
{
    private readonly ModsViewModel _vm;

    public ModsView()
    {
        // Generált InitializeComponent: betölti a XAML-t ÉS hozzárendeli az
        // x:Name mezőket (a kézi AvaloniaXamlLoader.Load a mezőket NULL-ná hagyná).
        InitializeComponent();
        _vm = new ModsViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private void OnShowBrowser(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = true;

    private void OnShowInstalled(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = false;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync("Mod hozzáadása", new[] { "*.jar" });
        if (files is null || files.Length == 0) return;
        await _vm.AddAsync(files);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
        => await _vm.DeleteAsync(ModList.SelectedItems?.OfType<ModRow>().ToList() ?? new());

    private async void OnEnableAll(object? sender, RoutedEventArgs e)
        => await _vm.SetAllEnabledAsync(true);

    private async void OnDisableAll(object? sender, RoutedEventArgs e)
        => await _vm.SetAllEnabledAsync(false);

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
        => await _vm.CheckUpdatesAsync();

    private async void OnUpdateSelected(object? sender, RoutedEventArgs e)
        => await _vm.UpdateRowAsync(_vm.SelectedMod);

    private async void OnUpdateAll(object? sender, RoutedEventArgs e)
        => await _vm.UpdateAllAsync();

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => _vm.Refresh();

    private async void OnModChecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, true);

    private async void OnModUnchecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, false);

    private async Task ToggleFromSenderAsync(object? sender, bool enabled)
    {
        if ((sender as CheckBox)?.DataContext is ModRow row)
            await _vm.SetEnabledAsync(row, enabled);
    }

    private async void OnSearch(object? sender, RoutedEventArgs e)
        => await _vm.SearchAsync();

    private async void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await _vm.SearchAsync();
    }

    private async void OnInstallSearch(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SearchRow row)
            await _vm.InstallAsync(row);
    }

    private async Task<string[]?> PickFilesAsync(string title, string[] patterns)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Minecraft mod") { Patterns = patterns }
                }
            });
            return files.Select(f => f.Path.LocalPath).ToArray();
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Fájlválasztási hiba: " + ex.Message;
            return null;
        }
    }
}

public sealed class ModRow : PropertyChangedBase
{
    private bool _enabled;
    private string? _iconPath;

    public required string FileName { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public required string Loader { get; init; }
    public required string SizeText { get; init; }
    public string? Error { get; init; }

    /// <summary>Modrinth projekt-azonosító a metából (frissítés-ellenőrzéshez).</summary>
    public string ProjectId { get; set; } = "";

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>OneWay kötéshez: az esemény végzi a kapcsolást, nem a setter.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Raise(ref _enabled, value);
    }

    public string StateText => Enabled ? "BE" : "KI";

    /// <summary>Pill-háttér a BE/KI jelzéshez (zöld vs. semleges).</summary>
    public string StateBg => Enabled ? "#16281D" : "#1A1D22";

    /// <summary>Pill-szövegszín a BE/KI jelzéshez.</summary>
    public string StateFg => Enabled ? "#46A758" : "#8A8D93";

    /// <summary>Helyi ikonfájl-útvonal (Modrinth-gyorsítótárból), vagy null.</summary>
    public string? IconPath
    {
        get => _iconPath;
        set => Raise(ref _iconPath, value);
    }

    private string _updateText = "";
    private bool _hasUpdate;
    private bool _updating;

    /// <summary>Elérhető frissítés jelzése (pl. "↑ 1.1").</summary>
    public string UpdateText
    {
        get => _updateText;
        set => Raise(ref _updateText, value);
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        set => Raise(ref _hasUpdate, value);
    }

    public bool Updating
    {
        get => _updating;
        set => Raise(ref _updating, value);
    }

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBg));
        OnPropertyChanged(nameof(StateFg));
    }
}

/// <summary>Egy Modrinth találati sor a böngészőben.</summary>
public sealed class SearchRow : PropertyChangedBase
{
    private string? _iconPath;
    private bool _installed;
    private bool _installing;

    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Author { get; init; }
    public required string DownloadsText { get; init; }
    public string? IconUrl { get; init; }

    /// <summary>Extra sor (pl. modpack támogatott MC-verziói). Üresen rejtve marad.</summary>
    public string ExtraInfo { get; set; } = "";

    public string? IconPath
    {
        get => _iconPath;
        set => Raise(ref _iconPath, value);
    }

    public bool Installed
    {
        get => _installed;
        set
        {
            if (!Raise(ref _installed, value)) return;
            OnPropertyChanged(nameof(InstalledText));
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    public bool Installing
    {
        get => _installing;
        set
        {
            if (!Raise(ref _installing, value)) return;
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    public string InstalledText => Installed ? "telepítve ✓" : "MC-verzióra szűrve ✓";
    public string ActionText => Installing ? "Telepítés…" : Installed ? "Telepítve" : "Telepítés";
    public bool CanInstall => !Installed && !Installing;
}

public sealed class ModsViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private ModRow? _selectedMod;
    private string _statusText = "";
    private string _countText = "";
    private string _subtitle = "";
    private bool _browserMode;
    private string _searchQuery = "";
    private string _searchInfo = "";
    private bool _searching;
    private double _progress;
    private bool _progressVisible;

    // Munkamenet-szintű projekt-gyorsítótár: egy Refresh legfeljebb egy
    // tömeges projekt-lekérést indít, nem modonként egyet.
    private readonly Dictionary<string, string?> _iconUrlByProject =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LauncherInstance> Instances { get; } = new();
    public ObservableCollection<ModRow> Mods { get; } = new();
    public ObservableCollection<SearchRow> SearchResults { get; } = new();

    public LauncherInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!Raise(ref _selectedInstance, value)) return;
            UpdateSubtitle();
            Refresh();
        }
    }

    public ModRow? SelectedMod
    {
        get => _selectedMod;
        set => Raise(ref _selectedMod, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string CountText { get => _countText; private set => Raise(ref _countText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }

    public bool IsBrowserMode
    {
        get => _browserMode;
        set
        {
            if (!Raise(ref _browserMode, value)) return;
            OnPropertyChanged(nameof(IsInstalledMode));
            if (value && SearchResults.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery))
                _ = SearchAsync();
        }
    }

    public bool IsInstalledMode => !IsBrowserMode;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!Raise(ref _searchQuery, value)) return;
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    public string SearchInfo { get => _searchInfo; private set => Raise(ref _searchInfo, value); }
    public bool CanSearch => !_searching;
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }

    public ModsViewModel()
    {
        Mods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            LoadInstanceList();
            // Másik nézet (pl. modpack-telepítés) miatti változásra újratöltünk.
            _services.InstancesChanged -= OnInstancesChanged;
            _services.InstancesChanged += OnInstancesChanged;
            if (SelectedInstance is null)
                StatusText = "Nincs Instance — hozz létre egyet a Játék lapon.";
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Mod lista init hiba: " + ex.Message);
        }
    }

    private void OnInstancesChanged()
    {
        Dispatcher.UIThread.Post(() => LoadInstanceList());
    }

    private void LoadInstanceList()
    {
        var (activeId, list) = _services.Instances.Load();
        var keepId = SelectedInstance?.Id ?? activeId;
        Instances.Clear();
        foreach (var i in list) Instances.Add(i);
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == keepId)
            ?? Instances.FirstOrDefault(x => x.Id == activeId)
            ?? Instances.FirstOrDefault();
    }

    private string ModsDir => SelectedInstance is null
        ? ""
        : Path.Combine(_services.Instances.GetInstanceDirectory(SelectedInstance.Id), "mods");

    private void UpdateSubtitle()
    {
        Subtitle = SelectedInstance is null
            ? "Fabric / Forge modok instance-onként."
            : $"MC {SelectedInstance.MinecraftVersion} · {SelectedInstance.Loader} · {SelectedInstance.Name}";
    }

    // ------------------------------------------------------------- telepített

    // ------------------------------------------------------------- telepített

    private readonly List<ModRow> _allMods = new();
    private string _filterText = "";
    private string _filterState = "Mind";
    private bool _modsBusy;

    public List<string> FilterStates { get; } = new() { "Mind", "Bekapcsolt", "Kikapcsolt", "Hibás" };

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Raise(ref _filterText, value)) return;
            ApplyFilter();
        }
    }

    public string FilterState
    {
        get => _filterState;
        set
        {
            if (!Raise(ref _filterState, value)) return;
            ApplyFilter();
        }
    }

    public bool IsEmpty => _allMods.Count == 0;

    public void Refresh()
    {
        var keepFile = SelectedMod?.FileName;
        _allMods.Clear();
        Mods.Clear();
        SelectedMod = null;
        if (SelectedInstance is null || string.IsNullOrEmpty(ModsDir)) { UpdateCount(); return; }

        try
        {
            foreach (var m in _services.Mods.ListMods(ModsDir))
            {
                var meta = Core.Mods.ModrinthInstaller.ReadMeta(ModsDir, m.FileName);
                _allMods.Add(new ModRow
                {
                    FileName = m.FileName,
                    Title = m.Title,
                    Version = string.IsNullOrWhiteSpace(m.Version) ? "?" : m.Version,
                    Loader = m.Loader,
                    SizeText = FormatBytes(m.SizeBytes),
                    Error = m.Error,
                    Enabled = m.Enabled,
                    ProjectId = meta?.ProjectId ?? ""
                });
            }
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Mod lista hiba: " + ex.Message);
        }
        ApplyFilter();
        if (keepFile is not null)
            SelectedMod = Mods.FirstOrDefault(m => m.FileName == keepFile);
        UpdateCount();
        _ = ResolveIconsAsync();
    }

    /// <summary>Szűrő alkalmazása a mesterlista alapján (keresés + állapot).</summary>
    private void ApplyFilter()
    {
        var keepFile = SelectedMod?.FileName;
        Mods.Clear();
        var query = (_filterText ?? "").Trim();
        foreach (var row in _allMods)
        {
            if (query.Length > 0 &&
                !row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            switch (_filterState)
            {
                case "Bekapcsolt" when !row.Enabled:
                case "Kikapcsolt" when row.Enabled:
                case "Hibás" when !row.HasError:
                    continue;
            }
            Mods.Add(row);
        }
        if (keepFile is not null)
            SelectedMod = Mods.FirstOrDefault(m => m.FileName == keepFile);
        OnPropertyChanged(nameof(IsEmpty));
        UpdateCount();
    }

    /// <summary>Tömeges ki/bekapcsolás a teljes mesterlistán (nem csak a szűrten).</summary>
    public async Task SetAllEnabledAsync(bool enabled)
    {
        if (SelectedInstance is null || _modsBusy) return;
        _modsBusy = true;
        try
        {
            var ok = 0;
            var errors = new List<string>();
            foreach (var row in _allMods.ToList())
            {
                if (row.Enabled == enabled) continue;
                try
                {
                    _services.Mods.SetEnabled(ModsDir, row.FileName, enabled);
                    // Átnevezés történt: a sort újra kell kötni az új névre.
                    var updated = _services.Mods.ListMods(ModsDir)
                        .FirstOrDefault(m => m.Title == row.Title);
                    row.Enabled = enabled;
                    if (updated is not null)
                    {
                        var idx = _allMods.IndexOf(row);
                        var fresh = new ModRow
                        {
                            FileName = updated.FileName,
                            Title = updated.Title,
                            Version = string.IsNullOrWhiteSpace(updated.Version) ? "?" : updated.Version,
                            Loader = updated.Loader,
                            SizeText = FormatBytes(updated.SizeBytes),
                            Error = updated.Error,
                            Enabled = updated.Enabled,
                            ProjectId = row.ProjectId,
                            IconPath = row.IconPath
                        };
                        if (idx >= 0) _allMods[idx] = fresh;
                    }
                    row.NotifyStateChanged();
                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.FileName}: {ex.Message}");
                }
            }
            ApplyFilter();
            StatusText = errors.Count == 0
                ? $"{ok} mod {(enabled ? "bekapcsolva" : "kikapcsolva")}."
                : $"{ok} átkapcsolva, {errors.Count} hiba: " + string.Join("; ", errors.Take(2));
        }
        finally
        {
            _modsBusy = false;
        }
        await Task.CompletedTask;
    }

    /// <summary>Több kijelölt sor törlése egyszerre.</summary>
    public async Task DeleteAsync(IReadOnlyList<ModRow> rows)
    {
        if (SelectedInstance is null) return;
        var targets = (rows ?? Array.Empty<ModRow>()).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Válassz ki legalább egy modot a törléshez.";
            return;
        }
        var ok = 0;
        var errors = new List<string>();
        foreach (var row in targets)
        {
            try
            {
                _services.Mods.RemoveMod(ModsDir, row.FileName);
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{row.FileName}: {ex.Message}");
            }
        }
        Refresh();
        StatusText = errors.Count == 0
            ? $"{ok} mod törölve."
            : $"{ok} törölve, {errors.Count} hiba: " + string.Join("; ", errors.Take(2));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Frissítés-ellenőrzés minden olyan sorra, aminek van Modrinth-metája.
    /// Hálózati művelet: tömeges projekt-lekérés helyett verziólisták
    /// mennek (projektenként egy hívás, párhuzamosítva).
    /// </summary>
    public async Task CheckUpdatesAsync()
    {
        if (SelectedInstance is null || _modsBusy) return;
        if (!RequireLoader(out var mcVersion, out var loader)) return;
        var targets = _allMods.Where(m => !string.IsNullOrWhiteSpace(m.ProjectId)).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Egyik modhoz sincs Modrinth-meta (kézzel bemásolt modok nem ellenőrizhetők).";
            return;
        }

        _modsBusy = true;
        try
        {
            StatusText = $"Frissítés-ellenőrzés ({targets.Count} mod)…";
            var found = 0;
            await Task.WhenAll(targets.Select(async row =>
            {
                try
                {
                    var info = await _services.ModrinthInstall.CheckForUpdateAsync(
                        ModsDir, row.FileName, mcVersion, loader);
                    if (info is not null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            row.UpdateText = $"↑ {info.NewVersion}";
                            row.HasUpdate = true;
                        });
                        System.Threading.Interlocked.Increment(ref found);
                    }
                }
                catch (Exception ex)
                {
                    LauncherLogger.Debug($"Frissítés-ellenőrzés hiba ({row.FileName}): {ex.Message}");
                }
            }));
            StatusText = found == 0
                ? "Minden ellenőrzött mod naprakész."
                : $"{found} modhoz érhető el frissítés.";
        }
        finally
        {
            _modsBusy = false;
        }
    }

    /// <summary>Egy sor frissítése (ha van rá ajánlat).</summary>
    public async Task UpdateRowAsync(ModRow? row)
    {
        if (row is null || !row.HasUpdate || SelectedInstance is null || _modsBusy) return;
        if (!RequireLoader(out var mcVersion, out var loader)) return;
        await UpdateRowsAsync(new List<ModRow> { row }, mcVersion, loader);
    }

    /// <summary>Minden frissíthető sor frissítése.</summary>
    public async Task UpdateAllAsync()
    {
        if (SelectedInstance is null || _modsBusy) return;
        if (!RequireLoader(out var mcVersion, out var loader)) return;
        var targets = _allMods.Where(m => m.HasUpdate && !m.Updating).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Nincs frissíthető mod — futtasd előbb a Frissítések keresését.";
            return;
        }
        await UpdateRowsAsync(targets, mcVersion, loader);
    }

    private async Task UpdateRowsAsync(List<ModRow> targets, string mcVersion, string loader)
    {
        _modsBusy = true;
        var ok = 0;
        var errors = new List<string>();
        try
        {
            foreach (var row in targets)
            {
                row.Updating = true;
                StatusText = $"Frissítés: {row.Title}…";
                try
                {
                    var updated = await _services.ModrinthInstall.UpdateModAsync(
                        ModsDir, row.FileName, mcVersion, loader);
                    if (updated is not null) ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.Title}: {ex.Message}");
                    LauncherLogger.Error($"Mod-frissítés hiba ({row.FileName}): " + ex.Message);
                }
                finally
                {
                    row.Updating = false;
                }
            }
        }
        finally
        {
            _modsBusy = false;
        }
        Refresh();
        StatusText = errors.Count == 0
            ? $"{ok} mod frissítve."
            : $"{ok} frissítve, {errors.Count} hiba: " + string.Join("; ", errors.Take(2));
    }

    private bool RequireLoader(out string mcVersion, out string loader)
    {
        mcVersion = "";
        loader = "";
        if (SelectedInstance is null) return false;
        mcVersion = SelectedInstance.MinecraftVersion;
        loader = (SelectedInstance.Loader ?? "none").ToLowerInvariant();
        if (loader is "none" or "")
        {
            StatusText = "Frissítéshez előbb válassz loadert az Instance szerkesztésében.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Ikonok feloldása a háttérben: meta → tömeges projekt-lekérés (egyetlen
    /// API-hívás) → gyorsítótárazott letöltés. A sorok érkezéskor frissülnek.
    /// </summary>
    private async Task ResolveIconsAsync()
    {
        if (string.IsNullOrEmpty(ModsDir) || Mods.Count == 0) return;
        var snapshot = Mods.ToList();

        try
        {
            var projectByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in snapshot)
            {
                var meta = Core.Mods.ModrinthInstaller.ReadMeta(ModsDir, row.FileName);
                if (meta is not null && !string.IsNullOrWhiteSpace(meta.ProjectId))
                    projectByFile[row.FileName] = meta.ProjectId;
            }
            if (projectByFile.Count == 0) return;

            var unknown = projectByFile.Values
                .Where(p => !_iconUrlByProject.ContainsKey(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknown.Length > 0)
            {
                var projects = await _services.Modrinth.GetProjectsAsync(unknown);
                foreach (var p in projects)
                    _iconUrlByProject[p.ProjectId] = p.IconUrl;
                foreach (var id in unknown)
                    _iconUrlByProject.TryAdd(id, null);
            }

            foreach (var row in snapshot)
            {
                if (row.IconPath is not null) continue;
                if (!projectByFile.TryGetValue(row.FileName, out var projectId)) continue;
                if (!_iconUrlByProject.TryGetValue(projectId, out var iconUrl)) continue;
                if (string.IsNullOrWhiteSpace(iconUrl)) continue;

                var local = await _services.Icons.GetIconPathAsync(projectId, iconUrl);
                if (local is null) continue;
                var target = Mods.FirstOrDefault(m => m.FileName == row.FileName);
                if (target is null) continue;
                Dispatcher.UIThread.Post(() => target.IconPath = local);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Mod ikon feloldási hiba: " + ex.Message);
        }
    }

    public async Task SetEnabledAsync(ModRow row, bool enabled)
    {
        if (SelectedInstance is null) return;
        try
        {
            _services.Mods.SetEnabled(ModsDir, row.FileName, enabled);
            // A fájl átneveződött (.jar ↔ .jar.disabled): a sort az új néven
            // kell újrakötni, különben a következő művelet rossz néven futna.
            var newName = enabled
                ? row.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? row.FileName[..^".disabled".Length]
                    : row.FileName
                : row.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? row.FileName
                    : row.FileName + ".disabled";
            Refresh();
            SelectedMod = Mods.FirstOrDefault(m => m.FileName == newName);
            StatusText = enabled
                ? $"{row.Title} bekapcsolva."
                : $"{row.Title} kikapcsolva (következő indításkor nem töltődik be).";
        }
        catch (Exception ex)
        {
            StatusText = "Kapcsolási hiba: " + ex.Message;
            LauncherLogger.Error("Mod kapcsolás hiba: " + ex.Message);
        }
        await Task.CompletedTask;
    }

    public async Task AddAsync(string[] sourcePaths)
    {
        if (SelectedInstance is null) return;
        var ok = 0;
        var errors = new List<string>();
        foreach (var src in sourcePaths)
        {
            try
            {
                var name = _services.Mods.AddMod(ModsDir, src);
                ok++;
                StatusText = $"Hozzáadva: {name}";
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
            }
        }
        Refresh();
        if (errors.Count > 0)
            StatusText = $"{ok} hozzáadva, {errors.Count} hiba: " + string.Join("; ", errors.Take(3));
        else if (ok > 0)
            StatusText = $"{ok} mod hozzáadva: {SelectedInstance.Name}.";
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------- böngésző

    private bool RequireTarget(out string mcVersion, out string loader)
    {
        mcVersion = "";
        loader = "";
        if (SelectedInstance is null)
        {
            SearchInfo = "Nincs Instance — hozz létre egyet a Játék lapon.";
            return false;
        }
        mcVersion = SelectedInstance.MinecraftVersion;
        loader = (SelectedInstance.Loader ?? "none").ToLowerInvariant();
        if (loader is "none" or "")
        {
            SearchInfo = $"A(z) {SelectedInstance.Name} loaderje nincs beállítva — a Modrinth-telepítéshez válassz loadert (pl. Fabric) az Instance szerkesztésében.";
            return false;
        }
        return true;
    }

    public async Task SearchAsync()
    {
        if (_searching) return;
        if (!RequireTarget(out var mcVersion, out var loader)) return;

        _searching = true;
        OnPropertyChanged(nameof(CanSearch));
        SearchInfo = "Keresés…";
        try
        {
            var result = await _services.Modrinth.SearchAsync(
                SearchQuery.Trim(), new[] { loader }, new[] { mcVersion }, "mod", 20, 0);

            var installedMap = Core.Mods.ModrinthInstaller.BuildProjectMap(ModsDir);
            SearchResults.Clear();
            foreach (var hit in result.Hits)
            {
                SearchResults.Add(new SearchRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    // HTML-entitás/markdown mentesítve (DisplayText) —
                    // különben "furán" jelenne meg a kártyán.
                    Description = Core.Text.DisplayText.CleanModrinth(hit.Description),
                    Author = hit.Author,
                    DownloadsText = FormatDownloads(hit.Downloads),
                    IconUrl = hit.IconUrl,
                    Installed = installedMap.ContainsKey(hit.ProjectId)
                });
            }

            SearchInfo = result.Hits.Count == 0
                ? $"Nincs találat (MC {mcVersion} · {loader})."
                : $"{result.TotalHits} találat · MC {mcVersion} · {loader} — csak kompatibilis verziók.";

            _ = PrefetchSearchIconsAsync();
        }
        catch (Exception ex)
        {
            SearchInfo = "Keresési hiba: " + ex.Message;
            LauncherLogger.Error("Modrinth keresés hiba: " + ex.Message);
        }
        finally
        {
            _searching = false;
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    private async Task PrefetchSearchIconsAsync()
    {
        var snapshot = SearchResults.ToList();
        try
        {
            await Task.WhenAll(snapshot.Select(async row =>
            {
                if (string.IsNullOrWhiteSpace(row.IconUrl) || row.IconPath is not null) return;
                var local = await _services.Icons.GetIconPathAsync(row.ProjectId, row.IconUrl);
                if (local is null) return;
                if (!SearchResults.Contains(row)) return;
                Dispatcher.UIThread.Post(() => row.IconPath = local);
            }));
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Keresési ikon hiba: " + ex.Message);
        }
    }

    public async Task InstallAsync(SearchRow row)
    {
        if (row is null || !row.CanInstall) return;
        if (!RequireTarget(out var mcVersion, out var loader)) return;

        row.Installing = true;
        ProgressVisible = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"Letöltés: {p.FileName}";
                    Progress = p.TotalBytes > 0
                        ? Math.Clamp(p.Percentage, 0, 100) : 0;
                }));

            var installed = await _services.ModrinthInstall.InstallProjectAsync(
                row.ProjectId, mcVersion, loader, ModsDir, "mod", progress);

            var deps = installed.Count(i => i.WasDependency);
            row.Installed = true;
            row.Installing = false;
            StatusText = deps > 0
                ? $"Telepítve: {row.Title} (+{deps} függőség: {string.Join(", ", installed.Where(i => i.WasDependency).Select(i => i.FileName))})."
                : $"Telepítve: {row.Title}.";

            // Az ikon máris megvan a keresésből — vigyük át a telepített sorra.
            Refresh();
            if (row.IconPath is not null)
            {
                var fileName = installed.FirstOrDefault(i => !i.WasDependency)?.FileName;
                var target = fileName is null ? null : Mods.FirstOrDefault(m => m.FileName == fileName);
                if (target is not null) target.IconPath = row.IconPath;
            }
        }
        catch (Exception ex)
        {
            row.Installing = false;
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error($"Modrinth telepítés hiba ({row.ProjectId}): " + ex.Message);
        }
        finally
        {
            ProgressVisible = false;
            Progress = 0;
        }
    }

    // ------------------------------------------------------------------

    private void UpdateCount()
    {
        var on = _allMods.Count(m => m.Enabled);
        CountText = _allMods.Count == 0 ? "" : $"{on}/{_allMods.Count} bekapcsolva";
    }

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };

    private static string FormatDownloads(long downloads)
        => downloads switch
        {
            >= 1_000_000 => $"{downloads / 1_000_000.0:F1}M letöltés",
            >= 1_000 => $"{downloads / 1_000.0:F0}E letöltés",
            _ => $"{downloads} letöltés"
        };
}
