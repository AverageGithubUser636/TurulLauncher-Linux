using System.Text.Json;

namespace TurulMC.Launcher;

public sealed partial class MainWindow
{
    private async System.Threading.Tasks.Task<object?> HandleMinecraftVersions()
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        using var response = await NewsHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var versions = new List<object>();
        if (doc.RootElement.TryGetProperty("versions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
                var releaseTime = item.TryGetProperty("releaseTime", out var timeEl) ? timeEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                // Keep releases plus snapshots/pre-releases: advanced users may intentionally target them.
                versions.Add(new { id, type, releaseTime });
                if (versions.Count >= 180) break;
            }
        }

        return new { versions };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstanceCompatibility(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instanceId = obj.TryGetProperty("instanceId", out var instanceEl)
            ? instanceEl.GetString() ?? _activeInstanceId
            : _activeInstanceId;
        var instance = _instances.FirstOrDefault(x => x.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az Instance nem található.");

        var minecraftVersion = obj.TryGetProperty("minecraftVersion", out var mcEl) && mcEl.ValueKind == JsonValueKind.String
            ? mcEl.GetString()?.Trim() ?? instance.MinecraftVersion
            : instance.MinecraftVersion;
        var loader = obj.TryGetProperty("loader", out var loaderEl) && loaderEl.ValueKind == JsonValueKind.String
            ? loaderEl.GetString()?.Trim().ToLowerInvariant() ?? instance.Loader
            : instance.Loader;
        var modpackVersionId = obj.TryGetProperty("modpackVersionId", out var mpEl) && mpEl.ValueKind == JsonValueKind.String
            ? mpEl.GetString()?.Trim() ?? instance.ModpackVersionId
            : instance.ModpackVersionId;

        var issues = new List<string>();
        var warnings = new List<string>();
        var checkedMods = 0;

        if (loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var loaders = await _fabricService.GetAvailableLoaderVersionsAsync(minecraftVersion);
                if (!loaders.Any())
                    issues.Add($"Ehhez a Minecraft-verzióhoz nincs elérhető Fabric Loader: {minecraftVersion}.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Fabric kompatibilitás nem ellenőrizhető: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(instance.ModpackProjectId))
        {
            if (string.IsNullOrWhiteSpace(modpackVersionId))
            {
                issues.Add("A modpackhoz nincs kiválasztott verzió.");
            }
            else
            {
                try
                {
                    using var response = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(modpackVersionId)}");
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var version = doc.RootElement;
                    var gameOk = version.TryGetProperty("game_versions", out var games) && games.ValueKind == JsonValueKind.Array &&
                                 games.EnumerateArray().Any(x => string.Equals(x.GetString(), minecraftVersion, StringComparison.OrdinalIgnoreCase));
                    var loaderOk = !version.TryGetProperty("loaders", out var loadersEl) || loadersEl.ValueKind != JsonValueKind.Array ||
                                   loadersEl.EnumerateArray().Any(x =>
                                       string.Equals(x.GetString(), loader, StringComparison.OrdinalIgnoreCase) ||
                                       (loader.Equals("none", StringComparison.OrdinalIgnoreCase) && string.Equals(x.GetString(), "minecraft", StringComparison.OrdinalIgnoreCase)));
                    if (!gameOk) issues.Add($"A kiválasztott modpack-verzió nem támogatja a Minecraft {minecraftVersion} verziót.");
                    if (!loaderOk) issues.Add($"A kiválasztott modpack-verzió nem kompatibilis ezzel a loaderrel: {loader}.");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Modpack kompatibilitás nem ellenőrizhető: {ex.Message}");
                }
            }
        }

        // Resolve/caches metadata for manually copied JARs too, then verify the
        // installed Modrinth versions against the proposed game version + loader.
        // When switching a modpack version the managed mod set will be replaced,
        // so checking the old pack's mods would only produce false warnings.
        var switchingModpack = !string.IsNullOrWhiteSpace(instance.ModpackProjectId) &&
            !string.Equals(modpackVersionId, instance.ModpackVersionId, StringComparison.OrdinalIgnoreCase);
        try
        {
            ConfigureInstanceServices(instance.Id);
            await GetInstalledModProjectMapAsync();
            var metaDirectory = Path.Combine(GetInstanceDirectory(instance.Id), "mods", ".turul-meta");
            if (Directory.Exists(metaDirectory))
            {
                foreach (var metadataPath in Directory.EnumerateFiles(metaDirectory, "*.json").Take(60))
                {
                    string versionId = "";
                    string source = "";
                    string title = Path.GetFileNameWithoutExtension(metadataPath);
                    try
                    {
                        using var metaDoc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
                        var meta = metaDoc.RootElement;
                        versionId = meta.TryGetProperty("versionId", out var versionEl) ? versionEl.GetString() ?? "" : "";
                        title = meta.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? title : title;
                        source = meta.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() ?? "" : "";
                    }
                    catch { continue; }
                    if (switchingModpack && source.Equals("modpack", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(versionId)) continue;

                    try
                    {
                        using var versionResponse = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
                        if (!versionResponse.IsSuccessStatusCode) continue;
                        using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                        var version = versionDoc.RootElement;
                        var gameOk = version.TryGetProperty("game_versions", out var games) && games.ValueKind == JsonValueKind.Array &&
                                     games.EnumerateArray().Any(x => string.Equals(x.GetString(), minecraftVersion, StringComparison.OrdinalIgnoreCase));
                        var loaderOk = version.TryGetProperty("loaders", out var modLoaders) && modLoaders.ValueKind == JsonValueKind.Array &&
                                       modLoaders.EnumerateArray().Any(x => string.Equals(x.GetString(), loader, StringComparison.OrdinalIgnoreCase));
                        checkedMods++;
                        if (!gameOk || !loaderOk)
                            issues.Add($"Mod nem kompatibilis: {title}");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"{title} ellenőrzése kimaradt: {ex.Message}");
                    }
                }
            }

            var resourceMeta = Path.Combine(GetInstanceDirectory(instance.Id), "resourcepacks", ".turul-meta");
            if (Directory.Exists(resourceMeta))
            {
                foreach (var metadataPath in Directory.EnumerateFiles(resourceMeta, "*.json").Take(30))
                {
                    try
                    {
                        using var metaDoc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
                        var meta = metaDoc.RootElement;
                        var versionId = meta.TryGetProperty("versionId", out var versionEl) ? versionEl.GetString() ?? "" : "";
                        var title = meta.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? Path.GetFileNameWithoutExtension(metadataPath) : Path.GetFileNameWithoutExtension(metadataPath);
                        if (string.IsNullOrWhiteSpace(versionId)) continue;
                        using var versionResponse = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
                        if (!versionResponse.IsSuccessStatusCode) continue;
                        using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                        var version = versionDoc.RootElement;
                        var gameOk = version.TryGetProperty("game_versions", out var games) && games.ValueKind == JsonValueKind.Array &&
                                     games.EnumerateArray().Any(x => string.Equals(x.GetString(), minecraftVersion, StringComparison.OrdinalIgnoreCase));
                        if (!gameOk) issues.Add($"Resource pack nem kompatibilis: {title}");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Resource pack ellenőrzése kimaradt: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            ConfigureInstanceServices(_activeInstanceId);
        }

        return new
        {
            compatible = issues.Count == 0,
            minecraftVersion,
            loader,
            modpackVersionId,
            checkedMods,
            issues = issues.Take(12).ToArray(),
            moreIssues = Math.Max(0, issues.Count - 12),
            warnings = warnings.Take(4).ToArray()
        };
    }
}
