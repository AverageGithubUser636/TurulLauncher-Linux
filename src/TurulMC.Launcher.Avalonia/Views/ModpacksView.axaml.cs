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

public partial class ModpacksView : UserControl
{
    private readonly ModpacksViewModel _vm;

    public ModpacksView()
    {
        InitializeComponent();
        _vm = new ModpacksViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private void OnShowBrowser(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = true;

    private void OnShowInstalled(object? sender, RoutedEventArgs e)
        => _vm.IsBrowserMode = false;

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

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync("Modpack importálása", new[] { "*.mrpack", "*.zip" });
        if (string.IsNullOrWhiteSpace(path)) return;
        await _vm.ImportAsync(path);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
        => await _vm.ExportActiveAsync(PickSaveAsync);

    private async void OnExportRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is PackInstanceRow row)
            await _vm.ExportInstanceAsync(row.InstanceId, PickSaveAsync);
    }

    private async Task<string?> PickOpenAsync(string title, string[] patterns)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Modrinth modpack") { Patterns = patterns }
                }
            });
            return files.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Fájlválasztási hiba: " + ex.Message;
            return null;
        }
    }

    private async Task<string?> PickSaveAsync(string suggestedName)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Modpack exportálása",
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Modrinth modpack") { Patterns = new[] { "*.mrpack" } }
                }
            });
            return file?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Fájlválasztási hiba: " + ex.Message;
            return null;
        }
    }
}

/// <summary>Modpack-alapú Instance a telepített listában.</summary>
public sealed class PackInstanceRow : PropertyChangedBase
{
    private string? _iconPath;

    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string Loader { get; init; }
    public required string PackLine { get; init; }
    public string? ProjectId { get; init; }

    public string? IconPath
    {
        get => _iconPath;
        set => Raise(ref _iconPath, value);
    }
}

public sealed class ModpacksViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private string _statusText = "";
    private bool _browserMode;
    private string _searchQuery = "";
    private string _searchInfo = "";
    private bool _searching;
    private double _progress;
    private bool _progressVisible;
    private bool _busy;

    public ObservableCollection<PackInstanceRow> Packs { get; } = new();
    public ObservableCollection<SearchRow> SearchResults { get; } = new();

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

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
    public bool CanSearch => !_searching && !_busy;
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }

    public ModpacksViewModel()
    {
        Packs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            ReloadPacks();
            _services.InstancesChanged -= OnInstancesChanged;
            _services.InstancesChanged += OnInstancesChanged;
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Modpack nézet init hiba: " + ex.Message);
        }
    }

    private void OnInstancesChanged()
        => Dispatcher.UIThread.Post(ReloadPacks);

    private void ReloadPacks()
    {
        Packs.Clear();
        try
        {
            var (_, list) = _services.Instances.Load();
            foreach (var i in list.Where(x => !string.IsNullOrWhiteSpace(x.ModpackName)))
            {
                Packs.Add(new PackInstanceRow
                {
                    InstanceId = i.Id,
                    InstanceName = i.Name,
                    MinecraftVersion = i.MinecraftVersion,
                    Loader = i.Loader,
                    PackLine = string.IsNullOrWhiteSpace(i.ModpackVersionId)
                        ? i.ModpackName
                        : $"{i.ModpackName}",
                    ProjectId = string.IsNullOrWhiteSpace(i.ModpackProjectId) ||
                        i.ModpackProjectId == "local-import" ? null : i.ModpackProjectId
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Modpack lista hiba: " + ex.Message);
        }
        _ = ResolvePackIconsAsync();
    }

    private async Task ResolvePackIconsAsync()
    {
        var snapshot = Packs.ToList();
        var ids = snapshot
            .Select(p => p.ProjectId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return;

        try
        {
            var projects = await _services.Modrinth.GetProjectsAsync(ids!);
            var icons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects)
                icons[p.ProjectId] = p.IconUrl;

            foreach (var row in snapshot)
            {
                if (row.ProjectId is null || row.IconPath is not null) continue;
                if (!icons.TryGetValue(row.ProjectId, out var url) ||
                    string.IsNullOrWhiteSpace(url)) continue;
                var local = await _services.Icons.GetIconPathAsync(row.ProjectId, url);
                if (local is null) continue;
                var target = Packs.FirstOrDefault(p => p.InstanceId == row.InstanceId);
                if (target is null) continue;
                Dispatcher.UIThread.Post(() => target.IconPath = local);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Pack ikon hiba: " + ex.Message);
        }
    }

    // ------------------------------------------------------------- böngésző

    public async Task SearchAsync()
    {
        if (_searching || _busy) return;
        _searching = true;
        OnPropertyChanged(nameof(CanSearch));
        SearchInfo = "Keresés…";
        try
        {
            // Modpacknél nincs loader/MC szűrés: a pack indexe dönti el,
            // a találatnál a támogatott MC-verziókat mutatjuk.
            var result = await _services.Modrinth.SearchAsync(
                SearchQuery.Trim(), null, null, "modpack", 20, 0);

            // "Telepítve" = van olyan Instance, ami ehhez a pack-projekthez kötött.
            var (_, allInstances) = _services.Instances.Load();
            var installedProjects = new HashSet<string>(
                allInstances
                    .Select(i => i.ModpackProjectId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);

            SearchResults.Clear();
            foreach (var hit in result.Hits)
            {
                // A keresési találat nem tartalmaz MC-verziókat (az API nem
                // adja vissza packeknél) — a kompatibilitás a telepítéskor,
                // az index alapján dől el. Ezt ki is írjuk, ne "?" álljon ott.
                var mcLine = hit.GameVersions.Length == 0
                    ? "kompatibilis MC: telepítéskor derül ki"
                    : "MC: " + string.Join(", ", hit.GameVersions.Take(4)) +
                      (hit.GameVersions.Length > 4 ? $" +{hit.GameVersions.Length - 4}" : "");
                SearchResults.Add(new SearchRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    Description = string.IsNullOrWhiteSpace(hit.Description)
                        ? "Nincs leírás." : hit.Description,
                    Author = hit.Author,
                    DownloadsText = FormatDownloads(hit.Downloads),
                    IconUrl = hit.IconUrl,
                    ExtraInfo = mcLine,
                    Installed = installedProjects.Contains(hit.ProjectId)
                });
            }

            SearchInfo = result.Hits.Count == 0
                ? "Nincs találat."
                : $"{result.TotalHits} találat — telepítés új Instance-t hoz létre.";

            _ = PrefetchSearchIconsAsync();
        }
        catch (Exception ex)
        {
            SearchInfo = "Keresési hiba: " + ex.Message;
            LauncherLogger.Error("Modpack keresés hiba: " + ex.Message);
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
        _busy = true;
        OnPropertyChanged(nameof(CanSearch));
        row.Installing = true;
        ProgressVisible = true;
        string? temp = null;
        try
        {
            StatusText = $"Modpack letöltése: {row.Title}…";
            var progress = new Progress<Core.Modpacks.ModpackProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Text;
                    Progress = p.Percent;
                }));

            temp = await _services.PackInstaller.DownloadMrpackAsync(row.ProjectId, "", null);
            var (activeId, list) = _services.Instances.Load();
            var result = await _services.PackInstaller.InstallMrpackAsync(
                temp, row.ProjectId, "", _services.Instances, list, progress);

            row.Installed = true;
            StatusText = $"Telepítve: {result.Instance.Name} ({result.FileCount} fájl) — megjelent a Játék lapon.";
            _services.NotifyInstancesChanged();
            ReloadPacks();
        }
        catch (Exception ex)
        {
            StatusText = "Telepítési hiba: " + ex.Message;
            LauncherLogger.Error($"Modpack telepítés hiba ({row.ProjectId}): " + ex.Message);
        }
        finally
        {
            row.Installing = false;
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
            OnPropertyChanged(nameof(CanSearch));
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // ------------------------------------------------------------- import/export

    public async Task ImportAsync(string mrpackPath)
    {
        if (_busy) return;
        _busy = true;
        ProgressVisible = true;
        try
        {
            var progress = new Progress<Core.Modpacks.ModpackProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Text;
                    Progress = p.Percent;
                }));

            var (_, list) = _services.Instances.Load();
            var result = await _services.PackInstaller.InstallMrpackAsync(
                mrpackPath, "local-import", "", _services.Instances, list, progress);

            StatusText = $"Importálva: {result.Instance.Name} ({result.FileCount} fájl).";
            _services.NotifyInstancesChanged();
            ReloadPacks();
        }
        catch (Exception ex)
        {
            StatusText = "Importálási hiba: " + ex.Message;
            LauncherLogger.Error("Modpack import hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
        }
    }

    public async Task ExportActiveAsync(Func<string, Task<string?>> pickSaveAsync)
    {
        var (activeId, list) = _services.Instances.Load();
        var instance = list.FirstOrDefault(x => x.Id == activeId) ?? list.FirstOrDefault();
        if (instance is null)
        {
            StatusText = "Nincs Instance az exporthoz.";
            return;
        }
        await ExportInstanceAsync(instance.Id, pickSaveAsync);
    }

    public async Task ExportInstanceAsync(string instanceId, Func<string, Task<string?>> pickSaveAsync)
    {
        if (_busy) return;
        var (_, list) = _services.Instances.Load();
        var instance = list.FirstOrDefault(x => x.Id == instanceId);
        if (instance is null)
        {
            StatusText = "Az Instance nem található.";
            return;
        }

        _busy = true;
        ProgressVisible = true;
        try
        {
            var suggested = SanitizeFileName(instance.Name) + ".mrpack";
            var output = await pickSaveAsync(suggested);
            if (string.IsNullOrWhiteSpace(output))
            {
                StatusText = "Export megszakítva.";
                return;
            }

            var progress = new Progress<Core.Modpacks.ModpackProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Text;
                    Progress = p.Percent;
                }));

            var instanceDir = _services.Instances.GetInstanceDirectory(instance.Id);
            var manifest = await Core.Modpacks.ModpackInstaller.BuildExportManifestAsync(
                instanceDir,
                SanitizeFileName(instance.Name),
                "1.0.0",
                instance.MinecraftVersion,
                instance.Loader,
                instance.LoaderVersion,
                progress);
            await Core.Modpacks.ModpackPackager.ExportAsync(manifest, instanceDir, output);

            StatusText = $"Exportálva ({manifest.Files.Count} fájl): {output}";
        }
        catch (Exception ex)
        {
            StatusText = "Exportálási hiba: " + ex.Message;
            LauncherLogger.Error("Modpack export hiba: " + ex.Message);
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
        }
    }

    // ------------------------------------------------------------------

    private static string SanitizeFileName(string name)
    {
        var clean = string.Join("_", (name ?? "modpack").Split(Path.GetInvalidFileNameChars()));
        clean = clean.Trim();
        return string.IsNullOrWhiteSpace(clean) ? "modpack" : clean;
    }

    private static string FormatDownloads(long downloads)
        => downloads switch
        {
            >= 1_000_000 => $"{downloads / 1_000_000.0:F1}M letöltés",
            >= 1_000 => $"{downloads / 1_000.0:F0}E letöltés",
            _ => $"{downloads} letöltés"
        };
}
