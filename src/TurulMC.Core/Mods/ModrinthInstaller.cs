using System.Security.Cryptography;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Security;

namespace TurulMC.Core.Mods;

/// <summary>
/// Modrinth mod/resourcepack telepítés függőség-feloldással.
/// A Windows-launcher WebView-logikájának Core-ba emelt változata:
/// <list type="bullet">
/// <item>a verzióválasztás az Instance MC-verziójához és loaderéhez igazodik
/// (szerver oldali szűrés + release preferencia),</item>
/// <item>a <c>required</c> függőségek rekurzívan települnek
/// (látogatott-halmaz + 25-ös mélységi korlát a körök ellen),</item>
/// <item>minden telepítéshez <c>.turul-meta/&lt;fájlnév&gt;.json</c> készül,
/// így a lista tudja, melyik fájl melyik projekthez tartozik (ikon, frissítés),</item>
/// <item>a letöltés CDN-allowlistes, méretkorlátos és SHA-512-ellenőrzött.</item>
/// </list>
/// </summary>
public sealed class ModrinthInstaller
{
    public const int MaxDepth = 25;
    public const long MaxFileBytes = 512L * 1024L * 1024L;

    private readonly ModrinthClient _client;
    private readonly HttpClient _download;

    public ModrinthInstaller(ModrinthClient? client = null, HttpClient? downloadHttp = null)
    {
        _client = client ?? new ModrinthClient();
        _download = downloadHttp ?? CreateDownloadClient();
    }

    /// <summary>
    /// Egy projekt legfrissebb kompatibilis verziójának telepítése.
    /// </summary>
    /// <returns>A telepített fájlok (első a kért mod, utána a függőségek).</returns>
    public async Task<IReadOnlyList<InstalledModFile>> InstallProjectAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        string targetDir,
        string projectType = "mod",
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(projectId, minecraftVersion, loader, targetDir);

        var versions = await _client.GetVersionsAsync(
            projectId,
            new[] { loader.ToLowerInvariant() },
            new[] { minecraftVersion },
            cancellationToken).ConfigureAwait(false);

        if (versions.Count == 0)
            throw new InvalidOperationException(
                $"Nincs kompatibilis verzió ehhez: MC {minecraftVersion} / {loader}.");

        var selected = PickBest(versions)
            ?? throw new InvalidOperationException("Nem választható verzió a listából.");

        // Kliens oldali ellenőrzés is: a szerver-szűrésre nem hagyatkozunk vakon
        // (itt bukott meg a teszt egy „hazug" stubbal — élesben sem árthat).
        EnsureCompatible(selected, minecraftVersion, loader);

        var installed = new List<InstalledModFile>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await InstallVersionCoreAsync(selected, minecraftVersion, loader, targetDir,
            projectType, progress, visited, installed, isDependency: false, cancellationToken)
            .ConfigureAwait(false);
        return installed;
    }

    /// <summary>Konkret verzió telepítése (függőségeivel).</summary>
    public async Task<IReadOnlyList<InstalledModFile>> InstallVersionAsync(
        string versionId,
        string minecraftVersion,
        string loader,
        string targetDir,
        string projectType = "mod",
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("Üres Modrinth verzióazonosító.", nameof(versionId));
        ValidateTarget("x", minecraftVersion, loader, targetDir);

        var version = await _client.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Modrinth verzió nem található.");

        EnsureCompatible(version, minecraftVersion, loader);

        var installed = new List<InstalledModFile>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await InstallVersionCoreAsync(version, minecraftVersion, loader, targetDir,
            projectType, progress, visited, installed, isDependency: false, cancellationToken)
            .ConfigureAwait(false);
        return installed;
    }

    // ------------------------------------------------------------------ mag

    /// <summary>
    /// Frissítés-ellenőrzés egy telepített modhoz (meta alapján).
    /// Szabály: csak akkor ajánl, ha a legjobb kompatibilis verzió más
    /// <c>versionId</c>-jú ÉS (üres a telepített verziószám VAGY bizonyítottan
    /// újabb) — visszalépés (downgrade) soha, értelmezhetetlen számozásnál
    /// inkább csend.
    /// </summary>
    /// <returns>A frissítési lehetőség, vagy <c>null</c>.</returns>
    public async Task<ModUpdateInfo?> CheckForUpdateAsync(
        string modsDir,
        string fileName,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var meta = ReadMeta(modsDir, fileName);
        if (meta is null || string.IsNullOrWhiteSpace(meta.ProjectId)) return null;

        var versions = await _client.GetVersionsAsync(
            meta.ProjectId,
            new[] { loader.ToLowerInvariant() },
            new[] { minecraftVersion },
            cancellationToken).ConfigureAwait(false);
        var best = PickBest(versions);
        if (best is null) return null;

        if (best.VersionId.Equals(meta.VersionId, StringComparison.OrdinalIgnoreCase))
            return null; // naprakész

        if (!string.IsNullOrWhiteSpace(meta.VersionNumber) &&
            !ModVersionOrder.IsNewer(best.VersionNumber, meta.VersionNumber))
            return null; // nem bizonyítottan újabb (downgrade-védelem)

        return new ModUpdateInfo(
            ProjectId: meta.ProjectId,
            CurrentVersion: meta.VersionNumber,
            NewVersion: best.VersionNumber,
            NewVersionId: best.VersionId,
            NewFileName: best.Files.FirstOrDefault(f => f.Primary)?.FileName
                ?? best.Files.FirstOrDefault()?.FileName ?? "");
    }

    /// <summary>
    /// Egy mod frissítése a legjobb kompatibilis verzióra. A tiltott állapot
    /// megmarad (a `.disabled` az új fájlra költözik), a régi fájl + metája
    /// törlődik. Nincs frissítés → <c>null</c>.
    /// </summary>
    public async Task<InstalledModFile?> UpdateModAsync(
        string modsDir,
        string fileName,
        string minecraftVersion,
        string loader,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await CheckForUpdateAsync(modsDir, fileName, minecraftVersion, loader, cancellationToken)
            .ConfigureAwait(false);
        if (info is null) return null;

        var version = await _client.GetVersionAsync(info.NewVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A kiválasztott verzió már nem elérhető.");
        EnsureCompatible(version, minecraftVersion, loader);

        Directory.CreateDirectory(modsDir);
        var wasDisabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var baseName = wasDisabled ? fileName[..^".disabled".Length] : fileName;

        var newFileName = await DownloadVersionFileAsync(
            version, modsDir, "mod", progress, cancellationToken).ConfigureAwait(false);
        WriteMeta(modsDir, newFileName, version);

        // Régi fájl + régi meta takarítása (ha eltér az új névtől).
        if (!baseName.Equals(newFileName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var oldPath = PathSecurity.ResolveInsideRoot(modsDir, Path.GetFileName(fileName));
                PathSecurity.SafeDeleteFile(modsDir, oldPath);
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Régi modfájl törlése nem sikerült ({fileName}): {ex.Message}");
            }
            try
            {
                var oldMeta = Path.Combine(modsDir, ".turul-meta", baseName + ".json");
                var newMeta = Path.Combine(modsDir, ".turul-meta", newFileName + ".json");
                if (File.Exists(oldMeta) &&
                    !oldMeta.Equals(newMeta, StringComparison.OrdinalIgnoreCase))
                    File.Delete(oldMeta);
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Régi mod-meta törlése nem sikerült ({fileName}): {ex.Message}");
            }
        }

        // Tiltott állapot átvitele az új fájlra.
        if (wasDisabled)
        {
            var dest = PathSecurity.ResolveInsideRoot(modsDir, newFileName);
            if (File.Exists(dest) && !newFileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var disabled = dest + ".disabled";
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(dest, disabled);
            }
        }

        LauncherLogger.Info($"Mod frissítve: {fileName} → {newFileName} ({info.NewVersion})");
        return new InstalledModFile(
            ProjectId: version.ProjectId,
            VersionId: version.VersionId,
            VersionNumber: version.VersionNumber,
            FileName: newFileName,
            WasDependency: false);
    }

    private async Task InstallVersionCoreAsync(
        ModrinthVersion version,
        string minecraftVersion,
        string loader,
        string targetDir,
        string projectType,
        IProgress<DownloadProgress>? progress,
        HashSet<string> visited,
        List<InstalledModFile> installed,
        bool isDependency,
        CancellationToken cancellationToken)
    {
        if (visited.Count >= MaxDepth)
            throw new InvalidOperationException("Modrinth függőség-mélység túllépve (25).");

        var projectKey = string.IsNullOrWhiteSpace(version.ProjectId) ? version.VersionId : version.ProjectId;
        if (!visited.Add(projectKey))
            return; // kör vagy duplikált függőség — egyszer elég telepíteni

        // Előbb a függőségek (mélységi bejárás), hogy hiba esetén a fő mod
        // ne maradjon magára hiányzó könyvtárral.
        foreach (var dep in version.Dependencies)
        {
            if (!dep.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(dep.VersionId))
            {
                var depVersion = await _client.GetVersionAsync(dep.VersionId, cancellationToken)
                    .ConfigureAwait(false);
                if (depVersion is null)
                    throw new InvalidOperationException(
                        $"Egy szükséges függőség verziója nem található: {dep.VersionId}");
                EnsureCompatible(depVersion, minecraftVersion, loader);
                await InstallVersionCoreAsync(depVersion, minecraftVersion, loader, targetDir,
                    projectType, progress, visited, installed, isDependency: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(dep.ProjectId))
            {
                var depVersions = await _client.GetVersionsAsync(
                    dep.ProjectId,
                    new[] { loader.ToLowerInvariant() },
                    new[] { minecraftVersion },
                    cancellationToken).ConfigureAwait(false);
                var best = PickBest(depVersions)
                    ?? throw new InvalidOperationException(
                        $"Egy szükséges függőség nem kompatibilis: MC {minecraftVersion} / {loader}.");
                await InstallVersionCoreAsync(best, minecraftVersion, loader, targetDir,
                    projectType, progress, visited, installed, isDependency: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var fileName = await DownloadVersionFileAsync(
            version, targetDir, projectType, progress, cancellationToken).ConfigureAwait(false);

        WriteMeta(targetDir, fileName, version);
        installed.Add(new InstalledModFile(
            ProjectId: version.ProjectId,
            VersionId: version.VersionId,
            VersionNumber: version.VersionNumber,
            FileName: fileName,
            WasDependency: isDependency));
    }

    /// <summary>Release preferencia, különben a legfrissebb. Nyilvános a tesztelhetőségért.</summary>
    public static ModrinthVersion? PickBest(IReadOnlyList<ModrinthVersion> versions)
    {
        if (versions.Count == 0) return null;
        foreach (var v in versions)
        {
            if (v.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return versions[0];
    }

    internal static void EnsureCompatible(ModrinthVersion version, string minecraftVersion, string loader)
    {
        var gameOk = version.GameVersions.Any(g =>
            g.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase));
        var loaderOk = version.Loaders.Any(l =>
            l.Equals(loader, StringComparison.OrdinalIgnoreCase));
        if (!gameOk || !loaderOk)
            throw new InvalidOperationException(
                $"A(z) {version.VersionNumber} verzió nem kompatibilis: MC {minecraftVersion} / {loader}.");
    }

    private async Task<string> DownloadVersionFileAsync(
        ModrinthVersion version,
        string targetDir,
        string projectType,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (version.Files.Count == 0)
            throw new InvalidOperationException("A Modrinth-verzióhoz nem tartozik letölthető fájl.");

        var selected = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files[0];
        var fileName = Path.GetFileName(selected.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Érvénytelen Modrinth fájlnév.");

        var expectedExt = projectType.Equals("resourcepack", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".jar";
        if (!fileName.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A kiválasztott Modrinth-fájl nem {expectedExt}: {fileName}");

        if (!IsAllowedDownloadUrl(selected.Url))
            throw new InvalidOperationException("Érvénytelen Modrinth letöltési URL.");

        if (selected.Size is <= 0 or > MaxFileBytes)
            throw new InvalidOperationException($"Modrinth fájlméret érvénytelen / túl nagy (512 MB cap): {fileName}");

        Directory.CreateDirectory(targetDir);
        var destination = PathSecurity.ResolveInsideRoot(targetDir, fileName);
        var temporary = destination + ".download";

        try
        {
            using var response = await _download.GetAsync(
                selected.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared is <= 0 or > MaxFileBytes)
                throw new InvalidOperationException($"Modrinth fájlméret érvénytelen / túl nagy: {fileName}");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxFileBytes)
                        throw new InvalidOperationException($"Modrinth fájl túl nagy (512 MB cap): {fileName}");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new DownloadProgress
                    {
                        FileName = fileName,
                        BytesReceived = total,
                        TotalBytes = declared ?? total,
                        Status = "downloading"
                    });
                }
            }

            // SHA-512 ellenőrzés, ha a Modrinth adott hasht.
            if (!string.IsNullOrWhiteSpace(selected.Sha512))
            {
                using var sha = SHA512.Create();
                await using var check = File.OpenRead(temporary);
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(check, cancellationToken)
                    .ConfigureAwait(false));
                if (!actual.Equals(selected.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(temporary); } catch { }
                    throw new InvalidOperationException(
                        $"A letöltött fájl hash-e nem egyezik (sérült letöltés?): {fileName}");
                }
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }

        progress?.Report(new DownloadProgress
        {
            FileName = fileName,
            BytesReceived = selected.Size,
            TotalBytes = selected.Size,
            Status = "done"
        });
        LauncherLogger.Info($"Modrinth telepítve: {fileName} ({version.VersionNumber})");
        return fileName;
    }

    internal static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ meta

    /// <summary>
    /// <c>.turul-meta/&lt;fájlnév&gt;.json</c> írása: projectId, versionId,
    /// verziószám, telepítési idő. A lista ebből tudja az ikont és a
    /// frissíthetőséget — a Windows-launcher ugyanezt a konvenciót használja.
    /// </summary>
    internal static void WriteMeta(string targetDir, string fileName, ModrinthVersion version)
    {
        try
        {
            var metaDir = Path.Combine(targetDir, ".turul-meta");
            Directory.CreateDirectory(metaDir);
            var metaPath = Path.Combine(metaDir, fileName + ".json");
            var json = System.Text.Json.JsonSerializer.Serialize(new ModInstallMeta(
                version.ProjectId, version.VersionId, version.VersionNumber,
                fileName, DateTime.UtcNow));
            var write = Storage.AtomicFile.TryWriteAllText(metaPath, json);
            if (!write.Success)
                LauncherLogger.Warning($"Meta mentése nem sikerült ({fileName}): {write.Error}");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Meta mentése nem sikerült ({fileName}): {ex.Message}");
        }
    }

    /// <summary>Fájlnév → meta feloldás (ikonhoz, frissítés-ellenőrzéshez).</summary>
    public static ModInstallMeta? ReadMeta(string targetDir, string fileName)
    {
        try
        {
            // A tiltott (.disabled) fájl metája az eredeti néven van.
            var baseName = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".disabled".Length]
                : fileName;
            var metaPath = Path.Combine(targetDir, ".turul-meta", baseName + ".json");
            if (!File.Exists(metaPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<ModInstallMeta>(
                File.ReadAllText(metaPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Projekt → telepített fájlnév térkép egy mappára (keresési jelzéshez).</summary>
    public static Dictionary<string, string> BuildProjectMap(string targetDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var metaDir = Path.Combine(targetDir, ".turul-meta");
            if (!Directory.Exists(metaDir)) return map;
            foreach (var path in Directory.EnumerateFiles(metaDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<ModInstallMeta>(
                        File.ReadAllText(path));
                    if (meta is not null && !string.IsNullOrWhiteSpace(meta.ProjectId))
                        map[meta.ProjectId] = meta.FileName;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    private static void ValidateTarget(string projectId, string minecraftVersion, string loader, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Üres Modrinth projektazonosító.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Hiányzik a Minecraft verzió.", nameof(minecraftVersion));
        if (string.IsNullOrWhiteSpace(loader) || loader.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod telepítéséhez előbb válassz modloadert (pl. Fabric).");
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new ArgumentException("Üres célmappa.", nameof(targetDir));
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        return http;
    }
}

/// <summary>Egy telepített modfájl adatai.</summary>
public sealed record InstalledModFile(
    string ProjectId,
    string VersionId,
    string VersionNumber,
    string FileName,
    bool WasDependency);

/// <summary>Felajánlható mod-frissítés adatai.</summary>
public sealed record ModUpdateInfo(
    string ProjectId,
    string CurrentVersion,
    string NewVersion,
    string NewVersionId,
    string NewFileName);

/// <summary>A <c>.turul-meta</c> fájlok tartalma.</summary>
public sealed record ModInstallMeta(
    string ProjectId,
    string VersionId,
    string VersionNumber,
    string FileName,
    DateTime InstalledAt);
