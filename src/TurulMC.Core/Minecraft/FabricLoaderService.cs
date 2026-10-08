using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Http;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Minecraft;

public class FabricLoaderService : IFabricLoaderService
{
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private const string FabricMavenBase = "https://maven.fabricmc.net/";

    private readonly LauncherHttpClient _httpClient;
    private readonly string _instanceDirectory;
    private readonly string _versionsDirectory;
    private readonly string _librariesDirectory;

    public FabricLoaderService(string? instanceDirectory = null)
    {
        _httpClient = new LauncherHttpClient();

        _instanceDirectory = string.IsNullOrWhiteSpace(instanceDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TurulMC", "instances", "_unassigned")
            : Path.GetFullPath(instanceDirectory);
        _versionsDirectory = Path.Combine(_instanceDirectory, "versions");
        _librariesDirectory = Path.Combine(_instanceDirectory, "libraries");
    }

    public async Task InstallFabricLoaderAsync(string minecraftVersion, string loaderVersion, IProgress<OverallProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException("A Fabric Loader verzió nem lehet üres.", nameof(loaderVersion));

        LauncherLogger.Info($"Fabric telepítés: Minecraft {minecraftVersion}, Loader {loaderVersion}");

        var overall = new OverallProgress { CurrentTask = $"Fabric Loader {loaderVersion} telepítése" };
        progress?.Report(overall);

        var fabricVersionId = $"fabric-loader-{loaderVersion}-{minecraftVersion}";
        var versionDir = Path.Combine(_versionsDirectory, fabricVersionId);
        Directory.CreateDirectory(versionDir);

        var fabricJsonPath = Path.Combine(versionDir, $"{fabricVersionId}.json");

        overall.CurrentTask = "Downloading Fabric profile manifest";
        progress?.Report(overall);

        // Use the correct Fabric meta API to get the profile JSON
        var profileUrl = $"{FabricMetaBase}/versions/loader/{minecraftVersion}/{loaderVersion}/profile/json";
        var profileJson = await _httpClient.GetStringAsync(profileUrl);

        // Parse and fix the version ID in the profile JSON
        var doc = JsonDocument.Parse(profileJson);
        var root = doc.RootElement.Clone();

        // Build a corrected JSON with proper version ID
        var correctedJson = profileJson;

        // Replace the "id" field with our fabricVersionId
        if (root.TryGetProperty("id", out _))
        {
            correctedJson = FixProfileJsonId(profileJson, fabricVersionId);
        }

        await File.WriteAllTextAsync(fabricJsonPath, correctedJson);

        // Parse the profile JSON to get libraries
        var profileDoc = JsonDocument.Parse(correctedJson);
        var profileRoot = profileDoc.RootElement;

        var libsToDownload = new List<(string Name, string Url)>();
        if (profileRoot.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateArray())
            {
                var name = lib.TryGetProperty("name", out var n) ? n.GetString()! : "";
                var url = lib.TryGetProperty("url", out var u) ? u.GetString() : FabricMavenBase;

                if (string.IsNullOrEmpty(name)) continue;

                var libPath = ResolveLibraryPath(name, _librariesDirectory);
                if (!File.Exists(libPath))
                {
                    libsToDownload.Add((name, url ?? FabricMavenBase));
                }
            }
        }

        overall.TotalFiles = libsToDownload.Count;

        var libSemaphore = new System.Threading.SemaphoreSlim(8);
        var libTasks = libsToDownload.Select(async lib =>
        {
            var libPath = ResolveLibraryPath(lib.Name, _librariesDirectory);
            var dir = Path.GetDirectoryName(libPath);
            if (dir != null) Directory.CreateDirectory(dir);

            var baseUrl = string.IsNullOrWhiteSpace(lib.Url) 
                    ? FabricMavenBase 
                    : lib.Url.TrimEnd('/');

                var parts = lib.Name.Split(':');
                if (parts.Length != 3)
                    throw new InvalidOperationException($"Érvénytelen Fabric library név: {lib.Name}");

                var downloadUrl = $"{baseUrl}/{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}.jar";

            await libSemaphore.WaitAsync();
            try
            {
                overall.CurrentTask = $"Downloading {lib.Name}";
                progress?.Report(overall);
                await _httpClient.DownloadFileAsync(downloadUrl, libPath);
            }
            catch (Exception ex)
            {
                LauncherLogger.Error($"Failed to download Fabric library {lib.Name}", ex);
                throw;
            }
            finally
            {
                libSemaphore.Release();
                overall.FilesCompleted++;
            }
        });
        await Task.WhenAll(libTasks);

        overall.CurrentTask = "Fabric installation complete";
        progress?.Report(overall);
    }

    private static string FixProfileJsonId(string json, string newId)
    {
        // Replace "id":"<anything>" with "id":"<newId>"
        var idPattern = "\"id\"";
        var idx = json.IndexOf(idPattern, StringComparison.Ordinal);
        if (idx < 0) return json;

        var colonIdx = json.IndexOf(':', idx + idPattern.Length);
        if (colonIdx < 0) return json;

        var valueStart = json.IndexOf('"', colonIdx + 1);
        if (valueStart < 0) return json;

        var valueEnd = json.IndexOf('"', valueStart + 1);
        if (valueEnd < 0) return json;

        return json[..(valueStart + 1)] + newId + json[valueEnd..];
    }

    public async Task<bool> IsFabricInstalledAsync(string minecraftVersion, string loaderVersion)
    {
        var fabricVersionId = $"fabric-loader-{loaderVersion}-{minecraftVersion}";
        var versionDir = Path.Combine(_versionsDirectory, fabricVersionId);
        var jsonPath = Path.Combine(versionDir, $"{fabricVersionId}.json");
        return File.Exists(jsonPath);
    }

    public async Task<string> GetFabricJarPathAsync(string minecraftVersion, string loaderVersion)
    {
        var fabricVersionId = $"fabric-loader-{loaderVersion}-{minecraftVersion}";
        await Task.CompletedTask;
        return Path.Combine(_versionsDirectory, fabricVersionId, $"{fabricVersionId}.json");
    }

    public async Task<List<FabricLoaderVersion>> GetAvailableLoaderVersionsAsync(string minecraftVersion)
    {
        try
        {
            var json = await _httpClient.GetStringAsync($"{FabricMetaBase}/versions/loader/{minecraftVersion}");
            var entries = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(json) ?? new List<FabricLoaderEntry>();
            return entries
                .Where(e => e.Loader != null)
                .Select(e => new FabricLoaderVersion
                {
                    Version = e.Loader!.Version,
                    Stable = e.Loader.Stable
                })
                .ToList();
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Failed to fetch Fabric loader versions", ex);
            return new List<FabricLoaderVersion>();
        }
    }

    private static string ResolveLibraryPath(string mavenName, string librariesDir)
    {
        var parts = mavenName.Split(':');
        if (parts.Length >= 3)
        {
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
            return Path.Combine(librariesDir, $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar");
        }
        return Path.Combine(librariesDir, mavenName.Replace(':', '/') + ".jar");
    }
}
