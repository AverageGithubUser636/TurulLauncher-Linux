using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurulMC.Core.Models;
using Windows.Storage.Pickers;

namespace TurulMC.Launcher;

public sealed partial class MainWindow
{
    private async System.Threading.Tasks.Task<object?> HandleSearchResourcePacks(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var query = obj.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var offset = obj.TryGetProperty("offset", out var off) && off.TryGetInt32(out var parsedOffset)
            ? Math.Max(0, parsedOffset) : 0;
        var limit = obj.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 50) : 12;

        var instance = GetActiveModInstance();
        var mcVersion = instance.MinecraftVersion;
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:resourcepack" },
            new[] { $"versions:{mcVersion}" }
        });

        var url = $"search?query={Uri.EscapeDataString(query)}" +
                  $"&facets={Uri.EscapeDataString(facets)}&index=relevance&limit={limit}&offset={offset}";
        using var response = await ModrinthHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var hits = new List<object>();
        if (doc.RootElement.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                hits.Add(new
                {
                    projectId = GetString(hit, "project_id"),
                    title = GetString(hit, "title"),
                    description = GetString(hit, "description"),
                    author = GetString(hit, "author"),
                    iconUrl = GetString(hit, "icon_url"),
                    downloads = hit.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var n) ? n : 0L
                });
            }
        }

        var total = doc.RootElement.TryGetProperty("total_hits", out var totalEl) && totalEl.TryGetInt32(out var totalValue)
            ? totalValue : hits.Count;
        return new { mcVersion, offset, limit, totalHits = total, hits };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstallResourcePack(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        var requestedVersionId = obj.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(requestedVersionId))
            throw new InvalidOperationException("Hiányzik a resource pack azonosítója.");

        var instance = GetActiveModInstance();
        JsonElement version;
        JsonDocument? versionDoc = null;

        if (!string.IsNullOrWhiteSpace(requestedVersionId))
        {
            using var response = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(requestedVersionId)}");
            response.EnsureSuccessStatusCode();
            versionDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            version = versionDoc.RootElement;
        }
        else
        {
            var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.MinecraftVersion }));
            using var response = await ModrinthHttpClient.GetAsync(
                $"project/{Uri.EscapeDataString(projectId)}/version?game_versions={gameVersions}&include_changelog=false");
            response.EnsureSuccessStatusCode();
            versionDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var versions = versionDoc.RootElement;
            if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
                throw new InvalidOperationException($"Nincs {instance.MinecraftVersion} verzióval kompatibilis texture/resource pack kiadás.");
            version = versions[0];
        }

        try
        {
            var file = SelectPrimaryFile(version, ".zip");
            if (file.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("A resource pack kiadáshoz nem tartozik letölthető ZIP.");

            var fileName = Path.GetFileName(GetString(file, "filename"));
            var downloadUrl = GetString(file, "url");
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Érvénytelen resource pack fájlnév.");
            EnsureSafeDownloadUrl(downloadUrl);

            var root = Path.Combine(GetInstanceDirectory(instance.Id), "resourcepacks");
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, fileName);
            await DownloadWithOptionalHashesAsync(downloadUrl, destination,
                file.TryGetProperty("hashes", out var hashes) ? hashes : default,
                "resourcepack", fileName, 0, 1);

            var projectTitle = projectId;
            var iconUrl = "";
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                try
                {
                    using var projectResponse = await ModrinthHttpClient.GetAsync($"project/{Uri.EscapeDataString(projectId)}");
                    if (projectResponse.IsSuccessStatusCode)
                    {
                        using var projectDoc = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
                        projectTitle = GetString(projectDoc.RootElement, "title");
                        iconUrl = GetString(projectDoc.RootElement, "icon_url");
                    }
                }
                catch { }
            }

            var metaDir = Path.Combine(root, ".turul-meta");
            Directory.CreateDirectory(metaDir);
            await File.WriteAllTextAsync(Path.Combine(metaDir, fileName + ".json"), JsonSerializer.Serialize(new
            {
                title = string.IsNullOrWhiteSpace(projectTitle) ? fileName : projectTitle,
                projectId,
                versionId = GetString(version, "id"),
                iconUrl
            }));

            return new { success = true, filename = fileName, projectId, versionId = GetString(version, "id") };
        }
        finally
        {
            versionDoc?.Dispose();
        }
    }

    private System.Threading.Tasks.Task<object?> HandleListResourcePacks()
    {
        var instance = GetActiveModInstance();
        var root = Path.Combine(GetInstanceDirectory(instance.Id), "resourcepacks");
        Directory.CreateDirectory(root);
        var metaRoot = Path.Combine(root, ".turul-meta");

        var list = Directory.EnumerateFileSystemEntries(root)
            .Where(p => !Path.GetFileName(p).Equals(".turul-meta", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var title = name;
                var iconUrl = "";
                var projectId = "";
                try
                {
                    var meta = Path.Combine(metaRoot, name + ".json");
                    if (File.Exists(meta))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                        title = GetString(doc.RootElement, "title");
                        iconUrl = GetString(doc.RootElement, "iconUrl");
                        projectId = GetString(doc.RootElement, "projectId");
                    }
                }
                catch { }

                long size = 0;
                if (File.Exists(path))
                {
                    try { size = new FileInfo(path).Length; } catch { }
                }
                var iconDataUrl = TryReadResourcePackIconDataUrl(path);
                return (object)new { name, title = string.IsNullOrWhiteSpace(title) ? name : title, iconUrl, iconDataUrl, projectId, size };
            })
            .ToArray();

        return System.Threading.Tasks.Task.FromResult<object?>(list);
    }

    private static string TryReadResourcePackIconDataUrl(string packPath)
    {
        const int maxIconBytes = 2 * 1024 * 1024;
        try
        {
            byte[]? bytes = null;
            if (Directory.Exists(packPath))
            {
                var iconPath = Path.Combine(packPath, "pack.png");
                if (File.Exists(iconPath))
                {
                    var info = new FileInfo(iconPath);
                    if (info.Length > 0 && info.Length <= maxIconBytes)
                        bytes = File.ReadAllBytes(iconPath);
                }
            }
            else if (File.Exists(packPath) && packPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(packPath);
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals("pack.png", StringComparison.OrdinalIgnoreCase));
                if (entry is not null && entry.Length > 0 && entry.Length <= maxIconBytes)
                {
                    using var stream = entry.Open();
                    using var memory = new MemoryStream((int)entry.Length);
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
            }

            return bytes is { Length: > 0 }
                ? "data:image/png;base64," + Convert.ToBase64String(bytes)
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadPngFileDataUrl(string path)
    {
        const int maxIconBytes = 2 * 1024 * 1024;
        try
        {
            if (!File.Exists(path)) return "";
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > maxIconBytes) return "";
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { return ""; }
    }

    private static string TryReadModIconDataUrl(string jarPath)
    {
        const int maxIconBytes = 2 * 1024 * 1024;
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            string iconPath = "";
            var fabric = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
            if (fabric is not null)
            {
                using var stream = fabric.Open();
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("icon", out var icon))
                {
                    if (icon.ValueKind == JsonValueKind.String)
                        iconPath = icon.GetString() ?? "";
                    else if (icon.ValueKind == JsonValueKind.Object)
                    {
                        iconPath = icon.EnumerateObject()
                            .Select(x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : "")
                            .LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                    }
                }
            }

            ZipArchiveEntry? entry = null;
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                var normalized = iconPath.Replace('\\', '/').TrimStart('/');
                entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
            }
            entry ??= archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("icon.png", StringComparison.OrdinalIgnoreCase));
            entry ??= archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("/icon.png", StringComparison.OrdinalIgnoreCase) && e.Length <= maxIconBytes);

            if (entry is null || entry.Length <= 0 || entry.Length > maxIconBytes) return "";
            using var iconStream = entry.Open();
            using var memory = new MemoryStream((int)entry.Length);
            iconStream.CopyTo(memory);
            return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
        }
        catch { return ""; }
    }

    private System.Threading.Tasks.Task<object?> HandleRemoveResourcePack(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var name = Path.GetFileName(obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Hiányzik a resource pack neve.");

        var instance = GetActiveModInstance();
        var root = Path.Combine(GetInstanceDirectory(instance.Id), "resourcepacks");
        var path = Path.Combine(root, name);
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
        var meta = Path.Combine(root, ".turul-meta", name + ".json");
        if (File.Exists(meta)) File.Delete(meta);
        return System.Threading.Tasks.Task.FromResult<object?>(new { success = true });
    }

    private async System.Threading.Tasks.Task<object?> HandleSearchModpacks(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var query = obj.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var offset = obj.TryGetProperty("offset", out var off) && off.TryGetInt32(out var parsedOffset)
            ? Math.Max(0, parsedOffset) : 0;
        var limit = obj.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 50) : 12;
        var facets = JsonSerializer.Serialize(new[] { new[] { "project_type:modpack" } });
        var url = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}" +
                  $"&index=relevance&limit={limit}&offset={offset}";
        using var response = await ModrinthHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var hits = new List<object>();
        if (doc.RootElement.TryGetProperty("hits", out var hitArray))
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                var versions = hit.TryGetProperty("versions", out var vers) && vers.ValueKind == JsonValueKind.Array
                    ? vers.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(4).ToArray()
                    : Array.Empty<string>();
                hits.Add(new
                {
                    projectId = GetString(hit, "project_id"),
                    title = GetString(hit, "title"),
                    description = GetString(hit, "description"),
                    author = GetString(hit, "author"),
                    iconUrl = GetString(hit, "icon_url"),
                    downloads = hit.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var n) ? n : 0L,
                    versions
                });
            }
        }

        var total = doc.RootElement.TryGetProperty("total_hits", out var totalEl) && totalEl.TryGetInt32(out var totalValue)
            ? totalValue : hits.Count;
        return new { offset, limit, totalHits = total, hits };
    }

    private async System.Threading.Tasks.Task<object?> HandleModpackVersions(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Hiányzik a modpack azonosítója.");

        using var response = await ModrinthHttpClient.GetAsync(
            $"project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var versions = new List<object>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var version in doc.RootElement.EnumerateArray())
            {
                if (!VersionAdvertisesSupportedLoader(version)) continue;
                if (SelectPrimaryFile(version, ".mrpack").ValueKind == JsonValueKind.Undefined) continue;

                var gameVersions = version.TryGetProperty("game_versions", out var gv) && gv.ValueKind == JsonValueKind.Array
                    ? gv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                var loaders = version.TryGetProperty("loaders", out var ld) && ld.ValueKind == JsonValueKind.Array
                    ? ld.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();

                versions.Add(new
                {
                    id = GetString(version, "id"),
                    name = GetString(version, "name"),
                    versionNumber = GetString(version, "version_number"),
                    versionType = GetString(version, "version_type"),
                    datePublished = GetString(version, "date_published"),
                    gameVersions,
                    loaders,
                    featured = version.TryGetProperty("featured", out var featured) && featured.ValueKind == JsonValueKind.True
                });
            }
        }

        return new { projectId, versions };
    }

    private async System.Threading.Tasks.Task<object?> HandleModpackInstall(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        var requestedVersionId = obj.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(requestedVersionId))
            throw new InvalidOperationException("Hiányzik a modpack azonosítója.");

        string downloadUrl = "";
        string versionId = requestedVersionId;
        JsonElement fileInfo = default;
        JsonDocument? versionDoc = null;

        if (!string.IsNullOrWhiteSpace(requestedVersionId))
        {
            using var response = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(requestedVersionId)}");
            response.EnsureSuccessStatusCode();
            versionDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            fileInfo = SelectPrimaryFile(versionDoc.RootElement, ".mrpack");
            if (string.IsNullOrWhiteSpace(projectId)) projectId = GetString(versionDoc.RootElement, "project_id");
        }
        else
        {
            using var response = await ModrinthHttpClient.GetAsync($"project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false");
            response.EnsureSuccessStatusCode();
            versionDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var version in versionDoc.RootElement.EnumerateArray())
            {
                if (!VersionAdvertisesSupportedLoader(version)) continue;
                var candidate = SelectPrimaryFile(version, ".mrpack");
                if (candidate.ValueKind == JsonValueKind.Undefined) continue;
                fileInfo = candidate;
                versionId = GetString(version, "id");
                break;
            }
        }

        try
        {
            if (fileInfo.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Nem található támogatott Fabric/vanilla .mrpack kiadás ehhez a modpackhoz.");
            downloadUrl = GetString(fileInfo, "url");
            EnsureSafeDownloadUrl(downloadUrl);

            var temp = Path.Combine(Path.GetTempPath(), $"TurulLauncher-{Guid.NewGuid():N}.mrpack");
            try
            {
                await DownloadWithOptionalHashesAsync(downloadUrl, temp,
                    fileInfo.TryGetProperty("hashes", out var hashes) ? hashes : default,
                    "modpack", GetString(fileInfo, "filename"), 0, 1);
                return await InstallMrpackAsync(temp, projectId, versionId);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        finally
        {
            versionDoc?.Dispose();
        }
    }

    private async System.Threading.Tasks.Task<object?> HandleModpackImport(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var path = obj.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path))
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add(".mrpack");
            picker.FileTypeFilter.Add(".zip");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return new { success = false, cancelled = true };
            path = file.Path;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("A kiválasztott modpack fájl nem található.", path);
        return await InstallMrpackAsync(path, "local-import", "");
    }

    private async System.Threading.Tasks.Task<object?> InstallMrpackAsync(string mrpackPath, string projectId, string versionId)
    {
        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("Ez nem érvényes Modrinth .mrpack: hiányzik a modrinth.index.json.");

        JsonDocument indexDoc;
        await using (var stream = indexEntry.Open())
            indexDoc = await JsonDocument.ParseAsync(stream);
        using (indexDoc)
        {
            var root = indexDoc.RootElement;
            var formatVersion = root.TryGetProperty("formatVersion", out var formatEl) && formatEl.TryGetInt32(out var parsedFormat)
                ? parsedFormat : 0;
            if (formatVersion != 1)
                throw new InvalidOperationException($"Nem támogatott .mrpack formátumverzió: {formatVersion}.");
            var game = GetString(root, "game");
            if (!string.IsNullOrWhiteSpace(game) && !game.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ez a .mrpack nem Minecraft modpack.");

            var name = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "Importált Modpack";
            name = MakeUniqueInstanceName(name);

            if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A modpack nem tartalmaz Minecraft dependency adatokat.");
            var minecraftVersion = deps.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(minecraftVersion))
                throw new InvalidOperationException("A modpack Minecraft-verziója hiányzik.");

            var loader = "none";
            var loaderVersion = "";
            if (deps.TryGetProperty("fabric-loader", out var fabric))
            {
                loader = "fabric";
                loaderVersion = fabric.GetString() ?? "";
            }
            foreach (var unsupported in new[] { "forge", "neoforge", "quilt-loader" })
            {
                if (deps.TryGetProperty(unsupported, out _))
                    throw new InvalidOperationException($"Ez a modpack {unsupported} loadert használ. A TurulLauncher jelenleg Fabric/vanilla modpackot támogat.");
            }

            var instance = new LauncherInstance
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Length > 48 ? name[..48].Trim() : name,
                MinecraftVersion = minecraftVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                RamMb = 4096,
                ModpackProjectId = projectId,
                ModpackVersionId = versionId,
                ModpackName = name,
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow
            };
            var instanceRoot = GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(instanceRoot);

            try
            {
                var files = root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array
                    ? filesEl.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
                var downloadable = files.Where(IsClientPackFile).ToArray();
                for (var i = 0; i < downloadable.Length; i++)
                {
                    var packFile = downloadable[i];
                    var relativePath = SanitizePackPath(GetString(packFile, "path"));
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;
                    if (!packFile.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"Nincs letöltési URL: {relativePath}");
                    var downloadUrl = downloads.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? "")
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                    EnsureSafeDownloadUrl(downloadUrl);
                    var destination = SafeCombine(instanceRoot, relativePath);
                    var hashes = packFile.TryGetProperty("hashes", out var hashEl) ? hashEl : default;
                    await DownloadWithOptionalHashesAsync(downloadUrl, destination, hashes,
                        "modpack", relativePath, i, downloadable.Length);
                    await CacheModpackModMetadataAsync(instanceRoot, relativePath, downloadUrl);
                }

                await ExtractMrpackOverridesAsync(archive, instanceRoot);
                var managedFiles = Directory.EnumerateFiles(instanceRoot, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(instanceRoot, path).Replace('\\', '/'))
                    .Where(path => !path.Equals(".turul-modpack.json", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await File.WriteAllTextAsync(Path.Combine(instanceRoot, ".turul-modpack.json"), JsonSerializer.Serialize(new
                {
                    name,
                    projectId,
                    versionId,
                    minecraftVersion,
                    loader,
                    loaderVersion,
                    managedFiles,
                    installedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));

                _instances.Add(instance);
                _activeInstanceId = instance.Id;
                SaveInstances();
                ConfigureInstanceServices(instance.Id);
                PostPackProgress("modpack", "Modpack telepítve", 100, "", 0, 0, 0, downloadable.Length, downloadable.Length);

                return new
                {
                    success = true,
                    instance = new
                    {
                        id = instance.Id,
                        name = instance.Name,
                        minecraftVersion = instance.MinecraftVersion,
                        loader = instance.Loader,
                        loaderVersion = instance.LoaderVersion,
                        ramMb = instance.RamMb,
                        path = instanceRoot
                    }
                };
            }
            catch
            {
                try { if (Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, true); } catch { }
                throw;
            }
        }
    }

    private async System.Threading.Tasks.Task<object?> HandleModpackSwitchVersion(string? data)
    {
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Modpack-verzió váltása előtt zárd be a Minecraftot.");

        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instanceId = obj.TryGetProperty("instanceId", out var instanceEl)
            ? instanceEl.GetString() ?? _activeInstanceId
            : _activeInstanceId;
        var versionId = obj.TryGetProperty("versionId", out var versionEl)
            ? versionEl.GetString()?.Trim() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("Válassz modpack-verziót.");

        var instance = _instances.FirstOrDefault(x => x.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az Instance nem található.");
        if (string.IsNullOrWhiteSpace(instance.ModpackProjectId))
            throw new InvalidOperationException("Ez az Instance nincs Modrinth modpackhoz kötve.");

        using var versionResponse = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
        versionResponse.EnsureSuccessStatusCode();
        using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
        var version = versionDoc.RootElement;
        var projectId = GetString(version, "project_id");
        if (!string.Equals(projectId, instance.ModpackProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A kiválasztott verzió nem ehhez a modpackhoz tartozik.");
        if (!VersionAdvertisesSupportedLoader(version))
            throw new InvalidOperationException("A kiválasztott modpack-verzió nem támogatott loadert használ.");

        var fileInfo = SelectPrimaryFile(version, ".mrpack");
        if (fileInfo.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Ehhez a modpack-verzióhoz nincs .mrpack fájl.");
        var downloadUrl = GetString(fileInfo, "url");
        EnsureSafeDownloadUrl(downloadUrl);

        var tempPack = Path.Combine(Path.GetTempPath(), $"TurulLauncher-switch-{Guid.NewGuid():N}.mrpack");
        try
        {
            await DownloadWithOptionalHashesAsync(downloadUrl, tempPack,
                fileInfo.TryGetProperty("hashes", out var hashes) ? hashes : default,
                "modpack", GetString(fileInfo, "filename"), 0, 1);
            return await ApplyMrpackVersionToExistingInstanceAsync(tempPack, instance, projectId, versionId);
        }
        finally
        {
            try { if (File.Exists(tempPack)) File.Delete(tempPack); } catch { }
        }
    }

    private async System.Threading.Tasks.Task<object?> ApplyMrpackVersionToExistingInstanceAsync(
        string mrpackPath, LauncherInstance instance, string projectId, string versionId)
    {
        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("Ez nem érvényes Modrinth .mrpack: hiányzik a modrinth.index.json.");

        JsonDocument indexDoc;
        await using (var stream = indexEntry.Open())
            indexDoc = await JsonDocument.ParseAsync(stream);
        using (indexDoc)
        {
            var root = indexDoc.RootElement;
            if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A modpack nem tartalmaz Minecraft dependency adatokat.");

            var minecraftVersion = deps.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(minecraftVersion))
                throw new InvalidOperationException("A modpack Minecraft-verziója hiányzik.");

            var loader = "none";
            var loaderVersion = "";
            if (deps.TryGetProperty("fabric-loader", out var fabric))
            {
                loader = "fabric";
                loaderVersion = fabric.GetString() ?? "";
            }
            foreach (var unsupported in new[] { "forge", "neoforge", "quilt-loader" })
            {
                if (deps.TryGetProperty(unsupported, out _))
                    throw new InvalidOperationException($"Ez a modpack {unsupported} loadert használ. A TurulLauncher jelenleg Fabric/vanilla modpackot támogat.");
            }

            var stagingRoot = Path.Combine(Path.GetTempPath(), $"TurulLauncher-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);
            try
            {
                var files = root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array
                    ? filesEl.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
                var downloadable = files.Where(IsClientPackFile).ToArray();
                for (var i = 0; i < downloadable.Length; i++)
                {
                    var packFile = downloadable[i];
                    var relativePath = SanitizePackPath(GetString(packFile, "path"));
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;
                    if (!packFile.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"Nincs letöltési URL: {relativePath}");
                    var fileUrl = downloads.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? "")
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                    EnsureSafeDownloadUrl(fileUrl);
                    var destination = SafeCombine(stagingRoot, relativePath);
                    var hashes = packFile.TryGetProperty("hashes", out var hashEl) ? hashEl : default;
                    await DownloadWithOptionalHashesAsync(fileUrl, destination, hashes,
                        "modpack", relativePath, i, downloadable.Length);
                    await CacheModpackModMetadataAsync(stagingRoot, relativePath, fileUrl);
                }

                await ExtractMrpackOverridesAsync(archive, stagingRoot);
                var managedFiles = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(stagingRoot, path).Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var instanceRoot = GetInstanceDirectory(instance.Id);
                Directory.CreateDirectory(instanceRoot);
                RemovePreviousManagedModpackFiles(instanceRoot);

                foreach (var source in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(stagingRoot, source);
                    var destination = SafeCombine(instanceRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, true);
                }

                var name = GetString(root, "name");
                if (string.IsNullOrWhiteSpace(name)) name = instance.ModpackName;
                await File.WriteAllTextAsync(Path.Combine(instanceRoot, ".turul-modpack.json"), JsonSerializer.Serialize(new
                {
                    name,
                    projectId,
                    versionId,
                    minecraftVersion,
                    loader,
                    loaderVersion,
                    managedFiles,
                    installedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));

                instance.MinecraftVersion = minecraftVersion;
                instance.Loader = loader;
                instance.LoaderVersion = loaderVersion;
                instance.ModpackProjectId = projectId;
                instance.ModpackVersionId = versionId;
                instance.ModpackName = string.IsNullOrWhiteSpace(name) ? instance.Name : name;
                instance.LastUsed = DateTime.UtcNow;
                _activeInstanceId = instance.Id;
                SaveInstances();
                ConfigureInstanceServices(instance.Id);
                PostPackProgress("modpack", "Modpack verzió frissítve", 100, "", 0, 0, 0, downloadable.Length, downloadable.Length);

                return new
                {
                    success = true,
                    instanceId = instance.Id,
                    minecraftVersion = instance.MinecraftVersion,
                    loader = instance.Loader,
                    loaderVersion = instance.LoaderVersion,
                    modpackProjectId = instance.ModpackProjectId,
                    modpackVersionId = instance.ModpackVersionId,
                    modpackName = instance.ModpackName
                };
            }
            finally
            {
                try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); } catch { }
            }
        }
    }

    private static void RemovePreviousManagedModpackFiles(string instanceRoot)
    {
        var metadataPath = Path.Combine(instanceRoot, ".turul-modpack.json");
        var removedAny = false;
        if (File.Exists(metadataPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
                if (doc.RootElement.TryGetProperty("managedFiles", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in files.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var relative = SanitizePackPath(item.GetString() ?? "");
                        if (string.IsNullOrWhiteSpace(relative)) continue;
                        var path = SafeCombine(instanceRoot, relative);
                        try { if (File.Exists(path)) { File.Delete(path); removedAny = true; } } catch { }
                    }
                }
            }
            catch { }
        }

        // Compatibility path for modpacks installed before managedFiles existed.
        if (!removedAny)
        {
            var metaRoot = Path.Combine(instanceRoot, "mods", ".turul-meta");
            if (Directory.Exists(metaRoot))
            {
                foreach (var meta in Directory.EnumerateFiles(metaRoot, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                        var source = doc.RootElement.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() ?? "" : "";
                        if (!source.Equals("modpack", StringComparison.OrdinalIgnoreCase)) continue;
                        var filename = Path.GetFileName(meta);
                        if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            filename = filename[..^5];
                        var mod = Path.Combine(instanceRoot, "mods", filename);
                        if (File.Exists(mod)) File.Delete(mod);
                        if (File.Exists(mod + ".disabled")) File.Delete(mod + ".disabled");
                        File.Delete(meta);
                    }
                    catch { }
                }
            }
        }
    }

    private static async System.Threading.Tasks.Task CacheModpackModMetadataAsync(
        string instanceRoot, string relativePath, string downloadUrl)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/');
        if (!normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return;

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            return;

        // Standard Modrinth CDN path:
        // /data/{projectId}/versions/{versionId}/{filename}
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase))
            return;
        var versionsIndex = Array.FindIndex(parts, x => x.Equals("versions", StringComparison.OrdinalIgnoreCase));
        if (versionsIndex < 2 || versionsIndex + 1 >= parts.Length) return;
        var projectId = parts[versionsIndex - 1];
        var versionId = parts[versionsIndex + 1];
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId)) return;

        var filename = Path.GetFileName(normalized);
        var metadataDirectory = Path.Combine(instanceRoot, "mods", ".turul-meta");
        Directory.CreateDirectory(metadataDirectory);
        var metadataPath = Path.Combine(metadataDirectory, filename + ".json");

        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
        {
            title = Path.GetFileNameWithoutExtension(filename),
            iconUrl = "",
            projectId,
            versionId,
            source = "modpack",
            cachedAt = DateTime.UtcNow
        }));
    }

    private static bool IsClientPackFile(JsonElement file)
    {
        if (!file.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object) return true;
        if (!env.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.String) return true;
        return !string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task ExtractMrpackOverridesAsync(ZipArchive archive, string instanceRoot)
    {
        foreach (var entry in archive.Entries)
        {
            var full = entry.FullName.Replace('\\', '/');
            string relative;
            if (full.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                relative = full["overrides/".Length..];
            else if (full.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                relative = full["client-overrides/".Length..];
            else
                continue;
            if (string.IsNullOrWhiteSpace(relative) || full.EndsWith('/')) continue;
            relative = SanitizePackPath(relative);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var destination = SafeCombine(instanceRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(target);
        }
    }

    private async System.Threading.Tasks.Task DownloadWithOptionalHashesAsync(
        string url, string destination, JsonElement hashes, string scope, string displayName,
        int completedBefore, int totalFiles)
    {
        const long cap = 512L * 1024L * 1024L;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".part";
        try
        {
            using var response = await ModrinthHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0L;
            if (total is <= 0 or > cap) throw new InvalidOperationException($"Pack fájl méret érvénytelen / túl nagy (512 MB cap): {displayName}");
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long received = 0;
            var sw = Stopwatch.StartNew();
            long lastReport = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (received > cap) throw new InvalidOperationException($"Pack fájl túl nagy (512 MB cap): {displayName}");
                if (received - lastReport >= 512 * 1024 || (total > 0 && received >= total))
                {
                    lastReport = received;
                    var speed = received / Math.Max(.05, sw.Elapsed.TotalSeconds);
                    var filePct = total > 0 ? received * 100d / total : 0;
                    var overall = totalFiles > 0 ? ((completedBefore + filePct / 100d) / totalFiles) * 100d : filePct;
                    PostPackProgress(scope, $"Letöltés: {displayName}", overall, displayName, received, total, speed, completedBefore, totalFiles);
                }
            }
            await output.FlushAsync();
            output.Close();

            await VerifyPackHashAsync(temporary, hashes);
            File.Move(temporary, destination, true);
            PostPackProgress(scope, $"Kész: {displayName}", totalFiles > 0 ? ((completedBefore + 1d) / totalFiles) * 100d : 100,
                displayName, received, total, received / Math.Max(.05, sw.Elapsed.TotalSeconds), completedBefore + 1, totalFiles);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static async System.Threading.Tasks.Task VerifyPackHashAsync(string file, JsonElement hashes)
    {
        // Kötelező hash: hiány = kivétel (a .part-ot a hívó törli).
        if (hashes.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Hiányzó hash: {Path.GetFileName(file)}");
        string algorithm = "";
        string expected = "";
        if (hashes.TryGetProperty("sha512", out var sha512) && sha512.ValueKind == JsonValueKind.String)
        {
            algorithm = "sha512";
            expected = sha512.GetString() ?? "";
        }
        else if (hashes.TryGetProperty("sha1", out var sha1) && sha1.ValueKind == JsonValueKind.String)
        {
            algorithm = "sha1";
            expected = sha1.GetString() ?? "";
        }
        else if (hashes.TryGetProperty("sha256", out var sha256) && sha256.ValueKind == JsonValueKind.String)
        {
            algorithm = "sha256";
            expected = sha256.GetString() ?? "";
        }
        if (string.IsNullOrWhiteSpace(expected)) throw new InvalidOperationException($"Hiányzó hash: {Path.GetFileName(file)}");

        await using var stream = File.OpenRead(file);
        byte[] hash = algorithm switch
        {
            "sha512" => await SHA512.HashDataAsync(stream),
            "sha1" => await SHA1.HashDataAsync(stream),
            _ => await SHA256.HashDataAsync(stream)
        };
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!actual.Equals(expected.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A letöltött fájl ellenőrzőösszege hibás: {Path.GetFileName(file)}");
    }

    private static JsonElement SelectPrimaryFile(JsonElement version, string extension)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return default;
        JsonElement? first = null;
        foreach (var file in files.EnumerateArray())
        {
            var filename = GetString(file, "filename");
            if (!filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            first ??= file;
            if (file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                return file;
        }
        return first ?? default;
    }

    private static bool VersionAdvertisesSupportedLoader(JsonElement version)
    {
        if (!version.TryGetProperty("loaders", out var loaders) || loaders.ValueKind != JsonValueKind.Array)
            return true;
        var values = loaders.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? "").ToLowerInvariant()).ToArray();
        if (values.Length == 0) return true;
        if (values.Contains("fabric") || values.Contains("minecraft")) return true;
        return !values.Any(x => x is "forge" or "neoforge" or "quilt");
    }

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private string MakeUniqueInstanceName(string name)
    {
        name = Regex.Replace(name.Trim(), @"\s+", " ");
        if (string.IsNullOrWhiteSpace(name)) name = "Modpack";
        if (name.Length > 44) name = name[..44].Trim();
        var candidate = name;
        var i = 2;
        while (_instances.Any(x => x.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{name}-{i++}";
        return candidate;
    }

    private static string SanitizePackPath(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or ".."))
            throw new InvalidOperationException("A modpack tiltott fájlútvonalat tartalmaz.");
        if (parts.Any(x => x.Contains(':') || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("A modpack érvénytelen fájlútvonalat tartalmaz.");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A modpack megpróbált az Instance mappán kívülre írni.");
        return full;
    }

    private static void EnsureSafeDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("A csomag csak HTTPS letöltési URL-t használhat.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Helyi letöltési cím nem engedélyezett modpackban.");
        if (IPAddress.TryParse(uri.Host, out var ip) && IsPrivateAddress(ip))
            throw new InvalidOperationException("Privát hálózati letöltési cím nem engedélyezett modpackban.");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private void PostPackProgress(string scope, string task, double percent, string fileName,
        long bytesReceived, long totalBytes, double bytesPerSecond, int filesCompleted, int totalFiles)
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return;
            TryPostWebMessage(JsonSerializer.Serialize(new
            {
                type = "pack.progress",
                scope,
                task,
                percent = Math.Round(Math.Clamp(percent, 0, 100), 1),
                fileName,
                bytesReceived,
                totalBytes,
                bytesPerSecond,
                filesCompleted,
                totalFiles
            }));
        }
        catch { }
    }
}
