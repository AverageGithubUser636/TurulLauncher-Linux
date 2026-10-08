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

public partial class ResourcePacksView : UserControl
{
    private readonly ResourcePacksViewModel _vm;

    public ResourcePacksView()
    {
        InitializeComponent();
        _vm = new ResourcePacksViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private void OnShowBrowser(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = true;

    private void OnShowInstalled(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = false;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync("Resource pack hozzáadása", new[] { "*.zip" });
        if (files is null || files.Length == 0) return;
        await _vm.AddAsync(files);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
        => await _vm.DeleteSelectedAsync();

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => _vm.Refresh();

    private async void OnPackChecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, true);

    private async void OnPackUnchecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, false);

    private async Task ToggleFromSenderAsync(object? sender, bool active)
    {
        if ((sender as CheckBox)?.DataContext is PackRow row)
            await _vm.SetActiveAsync(row, active);
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
                    new FilePickerFileType("Resource pack") { Patterns = patterns }
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

public sealed class PackRow : PropertyChangedBase
{
    private bool _active;
    private string? _iconPath;

    public required string FileName { get; init; }
    public required string Description { get; init; }
    public required string FormatText { get; init; }
    public required string SizeText { get; init; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool Active
    {
        get => _active;
        set => Raise(ref _active, value);
    }

    public string StateText => Active ? "AKTÍV" : "KI";

    public string? IconPath
    {
        get => _iconPath;
        set => Raise(ref _iconPath, value);
    }

    public void NotifyStateChanged() => OnPropertyChanged(nameof(StateText));
}

public sealed class ResourcePacksViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private PackRow? _selectedPack;
    private string _statusText = "";
    private string _countText = "";
    private string _subtitle = "";
    private bool _browserMode;
    private string _searchQuery = "";
    private string _searchInfo = "";
    private bool _searching;
    private double _progress;
    private bool _progressVisible;

    private readonly Dictionary<string, string?> _iconUrlByProject =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LauncherInstance> Instances { get; } = new();
    public ObservableCollection<PackRow> Packs { get; } = new();
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

    public PackRow? SelectedPack
    {
        get => _selectedPack;
        set => Raise(ref _selectedPack, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string CountText { get => _countText; private set => Raise(ref _countText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public bool IsEmpty => Packs.Count == 0;

    public bool IsBrowserMode
    {
        get => _browserMode;
        set
        {
            if (!Raise(ref _browserMode, value)) return;
            OnPropertyChanged(nameof(IsInstalledMode));
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

    public ResourcePacksViewModel()
    {
        Packs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
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
            LauncherLogger.Error("Pack lista init hiba: " + ex.Message);
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

    private string GameDir => SelectedInstance is null
        ? ""
        : _services.Instances.GetInstanceDirectory(SelectedInstance.Id);

    private string PacksDir => string.IsNullOrEmpty(GameDir)
        ? "" : Path.Combine(GameDir, "resourcepacks");

    private void UpdateSubtitle()
    {
        Subtitle = SelectedInstance is null
            ? "Resource packok instance-onként."
            : $"MC {SelectedInstance.MinecraftVersion} · {SelectedInstance.Name} — a jelölő az options.txt alapján kapcsol";
    }

    // ------------------------------------------------------------- telepített

    public void Refresh()
    {
        Packs.Clear();
        SelectedPack = null;
        if (SelectedInstance is null || string.IsNullOrEmpty(GameDir)) { UpdateCount(); return; }

        try
        {
            foreach (var p in _services.Mods.ListResourcePacks(GameDir))
            {
                Packs.Add(new PackRow
                {
                    FileName = p.FileName,
                    Description = p.Description,
                    FormatText = p.PackFormat?.ToString() ?? "?",
                    SizeText = FormatBytes(p.SizeBytes),
                    Active = p.Active
                });
            }
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Pack lista hiba: " + ex.Message);
        }
        UpdateCount();
        _ = ResolveIconsAsync();
    }

    private async Task ResolveIconsAsync()
    {
        if (string.IsNullOrEmpty(PacksDir) || Packs.Count == 0) return;
        var snapshot = Packs.ToList();

        try
        {
            var projectByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in snapshot)
            {
                var meta = Core.Mods.ModrinthInstaller.ReadMeta(PacksDir, row.FileName);
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
                var target = Packs.FirstOrDefault(m => m.FileName == row.FileName);
                if (target is null) continue;
                Dispatcher.UIThread.Post(() => target.IconPath = local);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Pack ikon feloldási hiba: " + ex.Message);
        }
    }

    public async Task SetActiveAsync(PackRow row, bool active)
    {
        if (SelectedInstance is null) return;
        try
        {
            _services.Mods.SetResourcePackActive(GameDir, row.FileName, active);
            row.Active = active;
            row.NotifyStateChanged();
            UpdateCount();
            StatusText = active
                ? $"{row.FileName} bekapcsolva (options.txt frissítve)."
                : $"{row.FileName} kikapcsolva.";
        }
        catch (Exception ex)
        {
            StatusText = "Kapcsolási hiba: " + ex.Message;
            LauncherLogger.Error("Pack kapcsolás hiba: " + ex.Message);
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
                var name = _services.Mods.AddResourcePack(GameDir, src);
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
            StatusText = $"{ok} pack hozzáadva: {SelectedInstance.Name}.";
        await Task.CompletedTask;
    }

    public async Task DeleteSelectedAsync()
    {
        var row = SelectedPack;
        if (row is null || SelectedInstance is null)
        {
            StatusText = "Válassz ki egy packot a törléshez.";
            return;
        }
        try
        {
            _services.Mods.RemoveResourcePack(GameDir, row.FileName);
            StatusText = $"Törölve: {row.FileName}";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = "Törlési hiba: " + ex.Message;
            LauncherLogger.Error("Pack törlés hiba: " + ex.Message);
        }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------- böngésző

    private bool RequireTarget(out string mcVersion)
    {
        mcVersion = "";
        if (SelectedInstance is null)
        {
            SearchInfo = "Nincs Instance — hozz létre egyet a Játék lapon.";
            return false;
        }
        mcVersion = SelectedInstance.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            SearchInfo = "Az Instance Minecraft-verziója nincs beállítva.";
            return false;
        }
        return true;
    }

    public async Task SearchAsync()
    {
        if (_searching) return;
        if (!RequireTarget(out var mcVersion)) return;

        _searching = true;
        OnPropertyChanged(nameof(CanSearch));
        SearchInfo = "Keresés…";
        try
        {
            // A resource packek MC-verzióra szűrünk; loader nincs (mindenhol mennek).
            var result = await _services.Modrinth.SearchAsync(
                SearchQuery.Trim(), null, new[] { mcVersion }, "resourcepack", 20, 0);

            var installedMap = Core.Mods.ModrinthInstaller.BuildProjectMap(PacksDir);
            SearchResults.Clear();
            foreach (var hit in result.Hits)
            {
                SearchResults.Add(new SearchRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    Description = string.IsNullOrWhiteSpace(hit.Description)
                        ? "Nincs leírás." : hit.Description,
                    Author = hit.Author,
                    DownloadsText = FormatDownloads(hit.Downloads),
                    IconUrl = hit.IconUrl,
                    Installed = installedMap.ContainsKey(hit.ProjectId)
                });
            }

            SearchInfo = result.Hits.Count == 0
                ? $"Nincs találat (MC {mcVersion})."
                : $"{result.TotalHits} találat · MC {mcVersion} — csak kompatibilis verziók.";

            _ = PrefetchSearchIconsAsync();
        }
        catch (Exception ex)
        {
            SearchInfo = "Keresési hiba: " + ex.Message;
            LauncherLogger.Error("Modrinth pack-keresés hiba: " + ex.Message);
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
        if (!RequireTarget(out var mcVersion)) return;

        // A packeknél a loader-szűrés üres: a verziólista szűretlen,
        // a kliens oldali ellenőrzés az MC-verzióra vonatkozik.
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

            // Resource packhez nincs loader — közvetlen verziófeloldás:
            // a legfrissebb MC-kompatibilis kiadás.
            var installed = await InstallPackAsync(row.ProjectId, mcVersion, progress);

            var deps = installed.Count(i => i.WasDependency);
            row.Installed = true;
            row.Installing = false;
            StatusText = deps > 0
                ? $"Telepítve: {row.Title} (+{deps} függőség)."
                : $"Telepítve: {row.Title}.";

            Refresh();
            if (row.IconPath is not null)
            {
                var fileName = installed.FirstOrDefault(i => !i.WasDependency)?.FileName;
                var target = fileName is null ? null : Packs.FirstOrDefault(m => m.FileName == fileName);
                if (target is not null) target.IconPath = row.IconPath;
            }
        }
        catch (Exception ex)
        {
            row.Installing = false;
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error($"Modrinth pack-telepítés hiba ({row.ProjectId}): " + ex.Message);
        }
        finally
        {
            ProgressVisible = false;
            Progress = 0;
        }
    }

    /// <summary>
    /// Resource pack telepítés loader nélkül: a verziókat csak MC-re szűrjük,
    /// és a release-preferenciával választunk. (A <see cref="Core.Mods.ModrinthInstaller"/>
    /// loadert követelne — packeknél annak nincs értelme.)
    /// </summary>
    private async Task<IReadOnlyList<Core.Mods.InstalledModFile>> InstallPackAsync(
        string projectId, string mcVersion, IProgress<DownloadProgress> progress)
    {
        var versions = await _services.Modrinth.GetVersionsAsync(
            projectId, null, new[] { mcVersion });
        if (versions.Count == 0)
            throw new InvalidOperationException($"Nincs kompatibilis pack-verzió: MC {mcVersion}.");

        var compatible = versions
            .Where(v => v.GameVersions.Any(g =>
                g.Equals(mcVersion, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (compatible.Count == 0)
            throw new InvalidOperationException($"Nincs kompatibilis pack-verzió: MC {mcVersion}.");

        var best = compatible.FirstOrDefault(v =>
                v.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase))
            ?? compatible[0];

        Directory.CreateDirectory(PacksDir);
        return await _services.ModrinthInstall.InstallVersionAsync(
            best.VersionId, mcVersion, LoaderForPacks(best), PacksDir, "resourcepack", progress);
    }

    /// <summary>
    /// A pack verzió első loaderét adjuk át az installernek (az csak a
    /// kompatibilitás-ellenőrzéshez kell); ha nincs, "minecraft" helyőrző.
    /// </summary>
    private static string LoaderForPacks(Core.Mods.ModrinthVersion version)
        => version.Loaders.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "minecraft";

    // ------------------------------------------------------------------

    private void UpdateCount()
    {
        var on = Packs.Count(p => p.Active);
        CountText = Packs.Count == 0 ? "" : $"{on}/{Packs.Count} aktív";
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
