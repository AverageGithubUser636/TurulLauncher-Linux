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

public partial class ShadersView : UserControl
{
    private readonly ShadersViewModel _vm;

    public ShadersView()
    {
        InitializeComponent();
        _vm = new ShadersViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private void OnShowBrowser(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = true;

    private void OnShowInstalled(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = false;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync("Shader hozzáadása", new[] { "*.zip" });
        if (files is null || files.Length == 0) return;
        await _vm.AddAsync(files);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
        => await _vm.DeleteSelectedAsync();

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => _vm.Refresh();

    private async void OnShaderChecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, true);

    private async void OnShaderUnchecked(object? sender, RoutedEventArgs e)
        => await ToggleFromSenderAsync(sender, false);

    private async Task ToggleFromSenderAsync(object? sender, bool enabled)
    {
        if ((sender as CheckBox)?.DataContext is ShaderRow row)
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
                    new FilePickerFileType("Shader") { Patterns = patterns }
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

public sealed class ShaderRow : PropertyChangedBase
{
    private bool _enabled;
    private string? _iconPath;

    public required string FileName { get; init; }
    public required string SizeText { get; init; }

    public bool Enabled
    {
        get => _enabled;
        set => Raise(ref _enabled, value);
    }

    public string StateText => Enabled ? "BE" : "KI";
    public string StateBg => Enabled ? "#16281D" : "#1A1D22";
    public string StateFg => Enabled ? "#46A758" : "#8A8D93";

    public string? IconPath
    {
        get => _iconPath;
        set => Raise(ref _iconPath, value);
    }
}

public sealed class ShadersViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private LauncherInstance? _selectedInstance;
    private ShaderRow? _selectedShader;
    private string _statusText = "";
    private string _countText = "";
    private string _subtitle = "";
    private bool _browserMode;
    private string _searchQuery = "";
    private string _searchInfo = "";
    private bool _searching;
    private double _progress;
    private bool _progressVisible;
    private bool _busy;

    private readonly Dictionary<string, string?> _iconUrlByProject =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LauncherInstance> Instances { get; } = new();
    public ObservableCollection<ShaderRow> Shaders { get; } = new();
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

    public ShaderRow? SelectedShader
    {
        get => _selectedShader;
        set => Raise(ref _selectedShader, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string CountText { get => _countText; private set => Raise(ref _countText, value); }
    public string Subtitle { get => _subtitle; private set => Raise(ref _subtitle, value); }
    public bool IsEmpty => Shaders.Count == 0;

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
    public bool CanSearch => !_searching && !_busy;
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }

    public ShadersViewModel()
    {
        Shaders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            LoadInstanceList();
            _services.InstancesChanged -= OnInstancesChanged;
            _services.InstancesChanged += OnInstancesChanged;
            if (SelectedInstance is null)
                StatusText = "Nincs Instance — hozz létre egyet a Játék lapon.";
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Shader lista init hiba: " + ex.Message);
        }
    }

    private void OnInstancesChanged()
        => Dispatcher.UIThread.Post(LoadInstanceList);

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

    private string ShadersDir => string.IsNullOrEmpty(GameDir)
        ? "" : Path.Combine(GameDir, "shaderpacks");

    private void UpdateSubtitle()
    {
        Subtitle = SelectedInstance is null
            ? "Shaderpackek instance-onként (Iris-kompatibilis .zip-ek)."
            : $"MC {SelectedInstance.MinecraftVersion} · {SelectedInstance.Name} — a jelölő átnevezéssel kapcsol";
    }

    public void Refresh()
    {
        Shaders.Clear();
        SelectedShader = null;
        if (SelectedInstance is null || string.IsNullOrEmpty(GameDir)) { UpdateCount(); return; }

        try
        {
            foreach (var s in _services.Mods.ListShaders(GameDir))
            {
                Shaders.Add(new ShaderRow
                {
                    FileName = s.FileName,
                    SizeText = FormatBytes(s.SizeBytes),
                    Enabled = s.Enabled
                });
            }
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Shader lista hiba: " + ex.Message);
        }
        UpdateCount();
        _ = ResolveIconsAsync();
    }

    private async Task ResolveIconsAsync()
    {
        if (string.IsNullOrEmpty(ShadersDir) || Shaders.Count == 0) return;
        var snapshot = Shaders.ToList();

        try
        {
            var projectByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in snapshot)
            {
                var meta = Core.Mods.ModrinthInstaller.ReadMeta(ShadersDir, row.FileName);
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
                var target = Shaders.FirstOrDefault(m => m.FileName == row.FileName);
                if (target is null) continue;
                Dispatcher.UIThread.Post(() => target.IconPath = local);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Shader ikon hiba: " + ex.Message);
        }
    }

    public async Task SetEnabledAsync(ShaderRow row, bool enabled)
    {
        if (SelectedInstance is null) return;
        try
        {
            _services.Mods.SetShaderEnabled(GameDir, row.FileName, enabled);
            var newName = enabled
                ? row.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? row.FileName[..^".disabled".Length]
                    : row.FileName
                : row.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? row.FileName
                    : row.FileName + ".disabled";
            Refresh();
            SelectedShader = Shaders.FirstOrDefault(m => m.FileName == newName);
            StatusText = enabled ? $"{row.FileName} bekapcsolva." : $"{row.FileName} kikapcsolva.";
        }
        catch (Exception ex)
        {
            StatusText = "Kapcsolási hiba: " + ex.Message;
            LauncherLogger.Error("Shader kapcsolás hiba: " + ex.Message);
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
                var name = _services.Mods.AddShaderPack(GameDir, src);
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
            StatusText = $"{ok} shader hozzáadva: {SelectedInstance.Name}.";
        await Task.CompletedTask;
    }

    public async Task DeleteSelectedAsync()
    {
        var row = SelectedShader;
        if (row is null || SelectedInstance is null)
        {
            StatusText = "Válassz ki egy shadert a törléshez.";
            return;
        }
        try
        {
            _services.Mods.RemoveShaderPack(GameDir, row.FileName);
            StatusText = $"Törölve: {row.FileName}";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = "Törlési hiba: " + ex.Message;
            LauncherLogger.Error("Shader törlés hiba: " + ex.Message);
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
        if (_searching || _busy) return;
        if (!RequireTarget(out var mcVersion)) return;

        _searching = true;
        OnPropertyChanged(nameof(CanSearch));
        SearchInfo = "Keresés…";
        try
        {
            var result = await _services.Modrinth.SearchAsync(
                SearchQuery.Trim(), null, new[] { mcVersion }, "shader", 20, 0);

            var installedMap = Core.Mods.ModrinthInstaller.BuildProjectMap(ShadersDir);
            SearchResults.Clear();
            foreach (var hit in result.Hits)
            {
                SearchResults.Add(new SearchRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    Description = Core.Text.DisplayText.CleanModrinth(hit.Description),
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
            LauncherLogger.Error("Modrinth shader-keresés hiba: " + ex.Message);
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
        if (row is null || !row.CanInstall || _busy) return;
        if (!RequireTarget(out var mcVersion)) return;

        _busy = true;
        OnPropertyChanged(nameof(CanSearch));
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

            var installed = await InstallShaderAsync(row.ProjectId, mcVersion, progress);

            row.Installed = true;
            row.Installing = false;
            StatusText = $"Telepítve: {row.Title}.";

            Refresh();
            if (row.IconPath is not null)
            {
                var fileName = installed.FirstOrDefault(i => !i.WasDependency)?.FileName;
                var target = fileName is null ? null : Shaders.FirstOrDefault(m => m.FileName == fileName);
                if (target is not null) target.IconPath = row.IconPath;
            }
        }
        catch (Exception ex)
        {
            row.Installing = false;
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error($"Modrinth shader-telepítés hiba ({row.ProjectId}): " + ex.Message);
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanSearch));
            ProgressVisible = false;
            Progress = 0;
        }
    }

    /// <summary>
    /// Shader-telepítés loader nélkül (lásd a packeknél): MC-szűrés +
    /// release-preferencia, a verzió saját loaderével ellenőrizve.
    /// </summary>
    private async Task<IReadOnlyList<Core.Mods.InstalledModFile>> InstallShaderAsync(
        string projectId, string mcVersion, IProgress<DownloadProgress> progress)
    {
        var versions = await _services.Modrinth.GetVersionsAsync(
            projectId, null, new[] { mcVersion });
        var compatible = versions
            .Where(v => v.GameVersions.Any(g =>
                g.Equals(mcVersion, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (compatible.Count == 0)
            throw new InvalidOperationException($"Nincs kompatibilis shader-verzió: MC {mcVersion}.");

        var best = compatible.FirstOrDefault(v =>
                v.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase))
            ?? compatible[0];

        Directory.CreateDirectory(ShadersDir);
        // A .zip kiterjesztés ellenőrzése az installerben történik;
        // shader-fájlok .zip-ek, a "mod" típus itt csak a mappát jelöli.
        var loader = best.Loaders.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "minecraft";
        return await _services.ModrinthInstall.InstallVersionAsync(
            best.VersionId, mcVersion, loader, ShadersDir, "resourcepack", progress);
    }

    // ------------------------------------------------------------------

    private void UpdateCount()
    {
        var on = Shaders.Count(p => p.Enabled);
        CountText = Shaders.Count == 0 ? "" : $"{on}/{Shaders.Count} bekapcsolva";
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
