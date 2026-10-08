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
        => await _vm.DeleteSelectedAsync();

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
    public bool IsEmpty => Mods.Count == 0;

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

    public void Refresh()
    {
        Mods.Clear();
        SelectedMod = null;
        if (SelectedInstance is null || string.IsNullOrEmpty(ModsDir)) { UpdateCount(); return; }

        try
        {
            foreach (var m in _services.Mods.ListMods(ModsDir))
            {
                Mods.Add(new ModRow
                {
                    FileName = m.FileName,
                    Title = m.Title,
                    Version = string.IsNullOrWhiteSpace(m.Version) ? "?" : m.Version,
                    Loader = m.Loader,
                    SizeText = FormatBytes(m.SizeBytes),
                    Error = m.Error,
                    Enabled = m.Enabled
                });
            }
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Mod lista hiba: " + ex.Message);
        }
        UpdateCount();
        _ = ResolveIconsAsync();
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
            row.Enabled = enabled;
            OnModStateChanged();
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

    public async Task DeleteSelectedAsync()
    {
        var row = SelectedMod;
        if (row is null || SelectedInstance is null)
        {
            StatusText = "Válassz ki egy modot a törléshez.";
            return;
        }
        try
        {
            _services.Mods.RemoveMod(ModsDir, row.FileName);
            StatusText = $"Törölve: {row.Title}";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = "Törlési hiba: " + ex.Message;
            LauncherLogger.Error("Mod törlés hiba: " + ex.Message);
        }
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

    private void OnModStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        UpdateCount();
        foreach (var m in Mods) m.NotifyStateChanged();
    }

    private void UpdateCount()
    {
        var on = Mods.Count(m => m.Enabled);
        CountText = Mods.Count == 0 ? "" : $"{on}/{Mods.Count} bekapcsolva";
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
