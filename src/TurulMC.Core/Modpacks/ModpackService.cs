using System.Security.Cryptography;
using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Http;
using TurulMC.Core.Logging;
using TurulMC.Core.Security;

namespace TurulMC.Core.Modpacks;

public class ModpackService : IModpackService
{
    private readonly LauncherHttpClient _httpClient;
    private readonly string _instanceDirectory;
    private const string ManagedFilesIndexName = ".turul-managed-files.json";

    public ModpackService(string? instanceDirectory = null)
    {
        _httpClient = new LauncherHttpClient();
        _instanceDirectory = string.IsNullOrWhiteSpace(instanceDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TurulMC", "instances", "_unassigned")
            : Path.GetFullPath(instanceDirectory);
        Directory.CreateDirectory(_instanceDirectory);
    }

    public async Task<ModpackManifest> GetManifestAsync(string manifestUrl)
    {
#if DEBUG
        const bool allowLocalhost = true;
#else
        const bool allowLocalhost = false;
#endif
        string json;
        if (manifestUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || File.Exists(manifestUrl))
        {
#if !DEBUG
            throw new InvalidOperationException("Release-ben csak https:// manifest engedélyezett.");
#else
            var path = manifestUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? manifestUrl[7..] : manifestUrl;
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("Manifest nem található.", full);
            json = await File.ReadAllTextAsync(full);
#endif
        }
        else
        {
            Security.PathSecurity.EnsureSafeHttpsUrl(manifestUrl, allowLocalhost);
            json = await _httpClient.GetStringAsync(manifestUrl);
        }
        var manifest = JsonSerializer.Deserialize<ModpackManifest>(json);
        return manifest ?? throw new InvalidOperationException("Failed to deserialize modpack manifest.");
    }

    public async Task SyncModpackAsync(ModpackManifest manifest, IProgress<OverallProgress>? progress = null)
    {
        foreach (var f in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Sha256))
                throw new InvalidOperationException($"Kötelező SHA hiányzik: {f.Path} — sync blokkolva.");
            if (string.IsNullOrWhiteSpace(f.Url))
                throw new InvalidOperationException($"Hiányzó URL: {f.Path}.");
            Security.PathSecurity.EnsureSafeHttpsUrl(f.Url);
        }
        var diff = await GetDiffAsync(manifest);
        var totalFiles = diff.FilesToAdd.Count + diff.FilesToUpdate.Count + diff.FilesToRemove.Count;
        var overall = new OverallProgress { CurrentTask = "Syncing modpack", TotalFiles = totalFiles };
        progress?.Report(overall);

        foreach (var filePath in diff.FilesToRemove)
        {
            var fullPath = Security.PathSecurity.ResolveInsideRoot(_instanceDirectory, filePath);
            if (File.Exists(fullPath))
            {
                overall.CurrentTask = "Removing " + filePath;
                overall.FilesCompleted++;
                progress?.Report(overall);
                Security.PathSecurity.SafeDeleteFile(_instanceDirectory, fullPath);
            }
        }

        var filesToSync = diff.FilesToAdd.Concat(diff.FilesToUpdate).ToList();
        var dlSemaphore = new System.Threading.SemaphoreSlim(8);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var dlTasks = filesToSync.Select(async file =>
        {
            var fullPath = Security.PathSecurity.ResolveInsideRoot(_instanceDirectory, file.Path);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null) Directory.CreateDirectory(dir);

            await dlSemaphore.WaitAsync();
            try
            {
                overall.CurrentTask = "Downloading " + Path.GetFileName(file.Path);
                progress?.Report(overall);
                await _httpClient.DownloadFileAsync(file.Url, fullPath);

                var hash = await Sha256Service.ComputeHashAsync(fullPath);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { Security.PathSecurity.SafeDeleteFile(_instanceDirectory, fullPath); } catch { try { File.Delete(fullPath); } catch { } }
                    var msg = $"Hash mismatch (törölve): {file.Path}";
                    LauncherLogger.Error(msg);
                    errors.Add(msg);
                }
            }
            catch (Exception ex)
            {
                LauncherLogger.Error("Failed to download " + file.Path, ex);
                errors.Add(file.Path + ": " + ex.Message);
            }
            finally
            {
                dlSemaphore.Release();
                overall.FilesCompleted++;
            }
        });
        await Task.WhenAll(dlTasks);
        if (!errors.IsEmpty)
            throw new InvalidOperationException("Modpack sync hibák:\n" + string.Join("\n", errors));

        // Remember only files managed by this manifest. Future syncs may remove
        // obsolete managed files, but never a user's own mods/config/worlds.
        var managedPaths = manifest.Files
            .Select(file => Security.PathSecurity.SanitizeRelative(file.Path).Replace('\\', '/'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(_instanceDirectory, ManagedFilesIndexName),
            JsonSerializer.Serialize(managedPaths, new JsonSerializerOptions { WriteIndented = true }));

        overall.CurrentTask = "Modpack sync complete";
        progress?.Report(overall);
    }

    public async Task<bool> ValidateModpackAsync(ModpackManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Sha256)) return false;
            var fullPath = Security.PathSecurity.ResolveInsideRoot(_instanceDirectory, file.Path);
            if (!File.Exists(fullPath)) return false;

            var valid = await Sha256Service.VerifyFileAsync(fullPath, file.Sha256);
            if (!valid) return false;
        }
        await Task.CompletedTask;
        return true;
    }

    public async Task<ModpackDiff> GetDiffAsync(ModpackManifest manifest)
    {
        var diff = new ModpackDiff();

        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var sanitized = Security.PathSecurity.SanitizeRelative(file.Path);
            manifestPaths.Add(sanitized);

            var fullPath = Security.PathSecurity.ResolveInsideRoot(_instanceDirectory, sanitized);
            if (!File.Exists(fullPath))
            {
                diff.FilesToAdd.Add(file);
            }
            else if (await IsFileModifiedAsync(fullPath, file.Sha256))
            {
                diff.FilesToUpdate.Add(file);
            }
        }

        // Never infer "obsolete" from every local file in the Instance. That
        // would delete user-added mods/config/resourcepacks. Only files that a
        // previous Turul manifest explicitly managed are eligible for removal.
        var managedIndex = Path.Combine(_instanceDirectory, ManagedFilesIndexName);
        if (File.Exists(managedIndex))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(managedIndex))
                    ?? Array.Empty<string>();
                foreach (var previousPath in previous)
                {
                    var normalized = Security.PathSecurity.SanitizeRelative(previousPath).Replace('\\', '/');
                    if (!string.IsNullOrWhiteSpace(normalized) && !manifestPaths.Contains(normalized))
                        diff.FilesToRemove.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning("Managed modpack index could not be read: " + ex.Message);
            }
        }

        return diff;
    }

    public async Task RemoveObsoleteFilesAsync(ModpackManifest manifest)
    {
        var diff = await GetDiffAsync(manifest);
        foreach (var filePath in diff.FilesToRemove)
        {
            var fullPath = Security.PathSecurity.ResolveInsideRoot(_instanceDirectory, filePath);
            if (File.Exists(fullPath))
                Security.PathSecurity.SafeDeleteFile(_instanceDirectory, fullPath);
        }
    }

    private async Task<bool> IsFileModifiedAsync(string filePath, string expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash)) return true;
        return !await Sha256Service.VerifyFileAsync(filePath, expectedHash);
    }

    private static string SanitizePath(string path) => Security.PathSecurity.SanitizeRelative(path).Replace(Path.DirectorySeparatorChar, '/');

    private List<string> GetAllFiles(string directory)
    {
        var files = new List<string>();
        if (!Directory.Exists(directory)) return files;

        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            files.Add(file);
        }

        var versionsDir = Path.Combine(directory, "versions");
        var assetsDir = Path.Combine(directory, "assets");
        var librariesDir = Path.Combine(directory, "libraries");

        files.RemoveAll(f => f.StartsWith(versionsDir));
        files.RemoveAll(f => f.StartsWith(assetsDir));
        files.RemoveAll(f => f.StartsWith(librariesDir));

        return files;
    }
}
