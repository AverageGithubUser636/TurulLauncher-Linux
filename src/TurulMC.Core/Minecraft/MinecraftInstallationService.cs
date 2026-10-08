using System.IO.Compression;
using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Http;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Minecraft;

public class MinecraftInstallationService : IMinecraftInstallationService
{
    private const string MojangVersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string MojangMavenBase = "https://libraries.minecraft.net/";

    private readonly LauncherHttpClient _httpClient;
    private readonly string _instanceDirectory;
    private readonly string _versionsDirectory;
    private readonly string _librariesDirectory;
    private readonly string _assetsDirectory;
    private MinecraftVersionManifest? _versionManifest;

    public MinecraftInstallationService(string? instanceDirectory = null)
    {
        _httpClient = new LauncherHttpClient();
        _instanceDirectory = string.IsNullOrWhiteSpace(instanceDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TurulMC", "instances", "_unassigned")
            : Path.GetFullPath(instanceDirectory);
        _versionsDirectory = Path.Combine(_instanceDirectory, "versions");
        _librariesDirectory = Path.Combine(_instanceDirectory, "libraries");
        _assetsDirectory = Path.Combine(_instanceDirectory, "assets");

        Directory.CreateDirectory(_instanceDirectory);
        Directory.CreateDirectory(_versionsDirectory);
        Directory.CreateDirectory(_librariesDirectory);
        Directory.CreateDirectory(_assetsDirectory);
    }

    public string GetInstanceDirectory() => _instanceDirectory;
    public string GetVersionDirectory(string versionId) => Path.Combine(_versionsDirectory, versionId);
    public string GetLibrariesDirectory() => _librariesDirectory;
    public string GetAssetsDirectory() => _assetsDirectory;

    public async Task<MinecraftVersionDetail> GetVersionDetailAsync(string versionId)
    {
        var versionUrl = await GetVersionUrlFromManifestAsync(versionId);
        if (string.IsNullOrEmpty(versionUrl))
            throw new InvalidOperationException($"Version {versionId} not found in Mojang manifest.");

        var json = await _httpClient.GetStringAsync(versionUrl);
        var detail = JsonSerializer.Deserialize<MinecraftVersionDetail>(json);
        return detail ?? throw new InvalidOperationException($"Failed to deserialize version detail for {versionId}.");
    }

    public async Task<string> GetClientJarPathAsync(string versionId)
    {
        var versionDir = GetVersionDirectory(versionId);
        return Path.Combine(versionDir, $"{versionId}.jar");
    }

    public async Task EnsureVersionDownloadedAsync(string versionId, IProgress<OverallProgress>? progress = null)
    {
        var versionDir = GetVersionDirectory(versionId);
        Directory.CreateDirectory(versionDir);

        var detail = await GetVersionDetailAsync(versionId);

        var overall = new OverallProgress { CurrentTask = "Downloading Minecraft files" };

        // Download client jar
        if (detail.Downloads?.Client != null)
        {
            var clientJarPath = await GetClientJarPathAsync(versionId);
            if (!File.Exists(clientJarPath))
            {
                overall.CurrentTask = $"Downloading client jar ({versionId})";
                progress?.Report(overall);

                await DownloadTrackedAsync(
                    detail.Downloads.Client.Url, clientJarPath,
                    $"Minecraft kliens · {versionId}", overall, progress);
            }
        }

        // Download libraries
        overall.CurrentTask = "Downloading libraries";
        overall.TotalFiles = detail.Libraries.Count;
        progress?.Report(overall);

        var librariesToDownload = new List<MinecraftLibrary>();
        foreach (var lib in detail.Libraries)
        {
            if (!IsLibraryApplicable(lib)) continue;

            var libPath = GetLibraryPath(lib);
            if (!File.Exists(libPath))
            {
                librariesToDownload.Add(lib);
            }
        }

        foreach (var lib in librariesToDownload)
        {
            var libPath = GetLibraryPath(lib);
            var dir = Path.GetDirectoryName(libPath);
            if (dir != null) Directory.CreateDirectory(dir);
        }

        var libSemaphore = new System.Threading.SemaphoreSlim(8);
        var libTasks = librariesToDownload.Select(async lib =>
        {
            var libPath = GetLibraryPath(lib);
            var url = GetLibraryDownloadUrl(lib);
            if (string.IsNullOrEmpty(url)) return;

            await libSemaphore.WaitAsync();
            try
            {
                overall.CurrentTask = $"Könyvtárak · {lib.Name}";
                progress?.Report(overall);
                await DownloadTrackedAsync(url, libPath, overall.CurrentTask, overall, progress);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to download library {lib.Name}: {ex.Message}");
            }
            finally
            {
                libSemaphore.Release();
                overall.FilesCompleted++;
            }
        });
        await Task.WhenAll(libTasks);

        // Extract native libraries
        overall.CurrentTask = "Extracting native libraries";
        progress?.Report(overall);
        var nativesDir = Path.Combine(_instanceDirectory, "natives");
        Directory.CreateDirectory(nativesDir);
        await ExtractNativesAsync(detail, nativesDir);

        // Download asset index
        if (detail.AssetIndex != null)
        {
            var assetIndexPath = Path.Combine(_assetsDirectory, "indexes", $"{detail.AssetIndex.Id}.json");
            if (!File.Exists(assetIndexPath))
            {
                overall.CurrentTask = "Downloading asset index";
                progress?.Report(overall);

                var dir = Path.GetDirectoryName(assetIndexPath);
                if (dir != null) Directory.CreateDirectory(dir);
                await DownloadTrackedAsync(
                    detail.AssetIndex.Url, assetIndexPath,
                    "Minecraft asset index", overall, progress);
            }

            // Download assets
            await DownloadAssetsAsync(detail.AssetIndex.Id, progress);
        }

        overall.CurrentTask = "Installation complete";
        progress?.Report(overall);
    }

    public async Task<bool> ValidateVersionFilesAsync(string versionId)
    {
        var clientJarPath = await GetClientJarPathAsync(versionId);
        if (!File.Exists(clientJarPath)) return false;

        try
        {
            var detail = await GetVersionDetailAsync(versionId);
            if (detail.Downloads?.Client?.Sha1 == null) return true;

            using var sha1 = System.Security.Cryptography.SHA1.Create();
            await using var stream = File.OpenRead(clientJarPath);
            var hash = await sha1.ComputeHashAsync(stream);
            var hashString = Convert.ToHexString(hash).ToLowerInvariant();
            return hashString == detail.Downloads.Client.Sha1;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IEnumerable<string>> GetInstalledVersionsAsync()
    {
        await Task.CompletedTask;

        if (!Directory.Exists(_versionsDirectory))
            return Enumerable.Empty<string>();

        return Directory.GetDirectories(_versionsDirectory)
            .Select(d => Path.GetFileName(d))
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name)!;
    }

    private async Task<string> GetVersionUrlFromManifestAsync(string versionId)
    {
        if (_versionManifest == null)
        {
            var json = await _httpClient.GetStringAsync(MojangVersionManifestUrl);
            _versionManifest = JsonSerializer.Deserialize<MinecraftVersionManifest>(json);
        }

        return _versionManifest?.Versions
            .FirstOrDefault(v => v.Id == versionId)?.Url ?? string.Empty;
    }

    private async Task DownloadAssetsAsync(string assetIndexId, IProgress<OverallProgress>? progress)
    {
        var indexPath = Path.Combine(_assetsDirectory, "indexes", $"{assetIndexId}.json");
        if (!File.Exists(indexPath)) return;

        var json = await File.ReadAllTextAsync(indexPath);
        var index = JsonSerializer.Deserialize<JsonElement>(json);

            var objects = index.GetProperty("objects");
            var objectCount = objects.EnumerateObject().Count();
            var overall = new OverallProgress
            {
                CurrentTask = "Downloading assets",
                TotalFiles = objectCount
            };

        var assetSemaphore = new System.Threading.SemaphoreSlim(16);
        var assetTasks = objects.EnumerateObject().Select(async prop =>
        {
            var obj = prop.Value;
            var hash = obj.GetProperty("hash").GetString()!;
            var hashPrefix = hash[..2];
            var assetDir = Path.Combine(_assetsDirectory, "objects", hashPrefix);
            var assetPath = Path.Combine(assetDir, hash);

            if (File.Exists(assetPath)) return;

            Directory.CreateDirectory(assetDir);
            var url = $"https://resources.download.minecraft.net/{hashPrefix}/{hash}";

            await assetSemaphore.WaitAsync();
            try
            {
                overall.CurrentTask = $"Assetek · {prop.Name}";
                progress?.Report(overall);
                await DownloadTrackedAsync(url, assetPath, overall.CurrentTask, overall, progress);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to download asset {prop.Name}: {ex.Message}");
            }
            finally
            {
                assetSemaphore.Release();
                overall.FilesCompleted++;
            }
        });
        await Task.WhenAll(assetTasks);
    }

    private async Task DownloadTrackedAsync(
        string url,
        string destinationPath,
        string task,
        OverallProgress? overall,
        IProgress<OverallProgress>? progress)
    {
        var fileProgress = new Progress<DownloadProgress>(file =>
        {
            if (overall == null || progress == null) return;
            overall.CurrentTask = task;
            overall.CurrentFile = file;
            progress.Report(new OverallProgress
            {
                CurrentTask = overall.CurrentTask,
                FilesCompleted = overall.FilesCompleted,
                TotalFiles = overall.TotalFiles,
                CurrentFile = file
            });
        });

        await _httpClient.DownloadFileAsync(url, destinationPath, fileProgress);
        if (overall != null)
            overall.CurrentFile = null;
    }

    private string GetLibraryPath(MinecraftLibrary lib)
    {
        if (lib.Downloads?.Artifact != null && !string.IsNullOrEmpty(lib.Downloads.Artifact.Url))
        {
            var parts = lib.Name.Split(':');
            if (parts.Length >= 4)
            {
                var group = parts[0].Replace('.', '/');
                var artifact = parts[1];
                var version = parts[2];
                var classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
                return Path.Combine(_librariesDirectory,
                    $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar");
            }
        }

        var nameParts = lib.Name.Split(':');
        if (nameParts.Length >= 3)
        {
            var group = nameParts[0].Replace('.', '/');
            var artifact = nameParts[1];
            var version = nameParts[2];
            return Path.Combine(_librariesDirectory,
                $"{group}/{artifact}/{version}/{artifact}-{version}.jar");
        }

        return Path.Combine(_librariesDirectory, lib.Name.Replace(':', '/') + ".jar");
    }

    private string GetLibraryDownloadUrl(MinecraftLibrary lib)
    {
        if (lib.Downloads?.Artifact?.Url != null)
            return lib.Downloads.Artifact.Url;

        if (!string.IsNullOrEmpty(lib.Url))
        {
            var parts = lib.Name.Split(':');
            if (parts.Length >= 3)
            {
                var group = parts[0].Replace('.', '/');
                var artifact = parts[1];
                var version = parts[2];
                return $"{lib.Url}{group}/{artifact}/{version}/{artifact}-{version}.jar";
            }
        }

        var nameParts = lib.Name.Split(':');
        if (nameParts.Length >= 3)
        {
            var group = nameParts[0].Replace('.', '/');
            var artifact = nameParts[1];
            var version = nameParts[2];
            return $"{MojangMavenBase}{group}/{artifact}/{version}/{artifact}-{version}.jar";
        }

        return string.Empty;
    }

    private bool IsLibraryApplicable(MinecraftLibrary lib)
    {
        if (lib.Rules == null || lib.Rules.Count == 0) return true;

        bool allowed = false;
        foreach (var rule in lib.Rules)
        {
            if (rule.Os != null && rule.Os.Name != null)
            {
                // A szabály akkor érvényes, ha a megadott OS a futó platformra illik.
                // (Korábban csak "windows" volt elfogadva — Linuxon így minden
                // platformfüggő könyvtár kihagyásra került.)
                if (Platform.LauncherPlatform.OsMatches(rule.Os.Name))
                    allowed = rule.Action == "allow";
            }
            else
            {
                allowed = rule.Action == "allow";
            }
        }

        return allowed;
    }

    private async Task ExtractNativesAsync(MinecraftVersionDetail detail, string nativesDir)
    {
        // A natives klasszifikátor kulcsa platformfüggő: windows / linux / osx.
        var osKey = Platform.LauncherPlatform.MinecraftOsName;

        foreach (var lib in detail.Libraries)
        {
            if (lib.Natives == null || !lib.Natives.ContainsKey(osKey)) continue;
            if (!IsLibraryApplicable(lib)) continue;

            var classifier = lib.Natives[osKey].Replace("${arch}", Platform.LauncherPlatform.NativesArch);
            var nativeName = lib.Name;
            var parts = nativeName.Split(':');
            if (parts.Length >= 3)
            {
                var group = parts[0].Replace('.', '/');
                var artifact = parts[1];
                var version = parts[2];
                var nativeJarPath = Path.Combine(_librariesDirectory,
                    $"{group}/{artifact}/{version}/{artifact}-{version}-{classifier}.jar");

                if (!File.Exists(nativeJarPath))
                {
                    var url = GetNativeDownloadUrl(lib, classifier);
                    if (!string.IsNullOrEmpty(url))
                    {
                        var dir = Path.GetDirectoryName(nativeJarPath);
                        if (dir != null) Directory.CreateDirectory(dir);
                        try
                        {
                            await DownloadTrackedAsync(url, nativeJarPath, $"Natív könyvtár · {Path.GetFileName(nativeJarPath)}", overall: null, progress: null);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to download native library {lib.Name}: {ex.Message}");
                            continue;
                        }
                    }
                }

                if (File.Exists(nativeJarPath))
                {
                    try
                    {
                        using var archive = System.IO.Compression.ZipFile.OpenRead(nativeJarPath);
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.EndsWith(".jnilib", StringComparison.OrdinalIgnoreCase))
                            {
                                var extractPath = Path.Combine(nativesDir, entry.Name);
                                entry.ExtractToFile(extractPath, overwrite: true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to extract natives from {nativeJarPath}: {ex.Message}");
                    }
                }
            }
        }
    }

    private string GetNativeDownloadUrl(MinecraftLibrary lib, string classifier)
    {
        if (lib.Downloads?.Classifiers != null &&
            lib.Downloads.Classifiers.TryGetValue(classifier, out var classifierEntry) &&
            !string.IsNullOrEmpty(classifierEntry.Url))
        {
            return classifierEntry.Url;
        }

        if (!string.IsNullOrEmpty(lib.Url))
        {
            var parts = lib.Name.Split(':');
            if (parts.Length >= 3)
            {
                var group = parts[0].Replace('.', '/');
                var artifact = parts[1];
                var version = parts[2];
                return $"{lib.Url}{group}/{artifact}/{version}/{artifact}-{version}-{classifier}.jar";
            }
        }

        return string.Empty;
    }

    private static class Logger
    {
        public static void Warning(string msg) => Core.Logging.LauncherLogger.Warning(msg);
    }
}
