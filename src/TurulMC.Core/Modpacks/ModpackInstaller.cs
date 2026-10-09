using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Security;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Modpacks;

/// <summary>
/// Modrinth .mrpack telepítés és export-manifest építés.
/// A Windows-launcher Packs-code-behind logikájának Core-ba emelt,
/// közvetlenül tesztelhető változata:
/// <list type="bullet">
/// <item>csak Fabric/vanilla packok (forge/neoforge/quilt elutasítva,
/// mint a Windows-verzióban),</item>
/// <item>szerver-oldali fájlok kihagyása (<c>env.client == "unsupported"</c>),</item>
/// <item>HTTPS + nem-privát hoszt ellenőrzés, méretkorlát, SHA-512/SHA-1
/// ellenőrzés ha a pack ad hasht,</item>
/// <item>új Instance készül, <c>.turul-modpack.json</c> követéssel;
/// hiba esetén a félig kész mappa törlődik.</item>
/// </list>
/// </summary>
public sealed class ModpackInstaller
{
    public const long MaxMrpackBytes = 2L * 1024L * 1024L * 1024L;
    public const long MaxPackFileBytes = 512L * 1024L * 1024L;

    private readonly Mods.ModrinthClient _client;
    private readonly HttpClient _download;

    public ModpackInstaller(Mods.ModrinthClient? client = null, HttpClient? downloadHttp = null)
    {
        _client = client ?? new Mods.ModrinthClient();
        _download = downloadHttp ?? CreateDownloadClient();
    }

    /// <summary>
    /// .mrpack letöltése Modrinth-ről (projekt legfrissebb támogatott kiadása
    /// vagy konkrét verzió). A visszaadott temp fájlt a hívó törli.
    /// </summary>
    public async Task<string> DownloadMrpackAsync(
        string projectId,
        string versionId = "",
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Mods.ModrinthVersion version;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            version = await _client.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A Modrinth verzió nem található.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Üres Modrinth projektazonosító.", nameof(projectId));
            var versions = await _client.GetVersionsAsync(projectId, null, null, cancellationToken)
                .ConfigureAwait(false);
            // Sorrend: .mrpack-fájlos release → .mrpack-fájlos bármi →
            // release → legfrissebb. (A lista eleje a legfrissebb.)
            version = versions
                .OrderByDescending(v => v.Files.Any(f =>
                    f.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(v => v.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Nem található támogatott Fabric/vanilla .mrpack kiadás ehhez a modpackhoz.");
        }

        var file = version.Files.FirstOrDefault(f =>
                f.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("A kiválasztott verzióhoz nem tartozik .mrpack fájl.");

        EnsureSafeDownloadUrl(file.Url);
        if (file.Size is <= 0 or > MaxMrpackBytes)
            throw new InvalidOperationException("A .mrpack méret érvénytelen / túl nagy (2 GB cap).");

        var temp = Path.Combine(Path.GetTempPath(), $"TurulLauncher-{Guid.NewGuid():N}.mrpack");
        try
        {
            await DownloadToFileAsync(file.Url, temp, file.Sha512, file.Sha1,
                Path.GetFileName(file.FileName), progress, cancellationToken).ConfigureAwait(false);
            return temp;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Helyi .mrpack telepítése új Instance-ba. A listához hozzáadja az új
    /// példányt és menti (aktívra állítva); hiba esetén a mappa törlődik
    /// és a lista változatlan marad.
    /// </summary>
    public async Task<ModpackInstallResult> InstallMrpackAsync(
        string mrpackPath,
        string projectId,
        string versionId,
        InstanceStore store,
        List<LauncherInstance> instances,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(instances);
        if (string.IsNullOrWhiteSpace(mrpackPath) || !File.Exists(mrpackPath))
            throw new FileNotFoundException("A modpack fájl nem található.", mrpackPath);

        using var archive = System.IO.Compression.ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Ez nem érvényes Modrinth .mrpack: hiányzik a modrinth.index.json.");

        using var indexDoc = await JsonDocument.ParseAsync(
            indexEntry.Open(), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = indexDoc.RootElement;

        var formatVersion = root.TryGetProperty("formatVersion", out var formatEl) &&
            formatEl.TryGetInt32(out var parsedFormat) ? parsedFormat : 0;
        if (formatVersion != 1)
            throw new InvalidOperationException($"Nem támogatott .mrpack formátumverzió: {formatVersion}.");

        var game = GetString(root, "game");
        if (!string.IsNullOrWhiteSpace(game) && !game.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ez a .mrpack nem Minecraft modpack.");

        if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("A modpack nem tartalmaz Minecraft dependency adatokat.");
        var minecraftVersion = deps.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new InvalidOperationException("A modpack Minecraft-verziója hiányzik.");

        var (loader, loaderVersion) = ParseLoader(deps);

        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name)) name = "Importált Modpack";
        name = MakeUniqueInstanceName(name, instances);

        var instance = new LauncherInstance
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name.Length > 48 ? name[..48].Trim() : name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            RamMb = 4096,
            ModpackProjectId = projectId ?? "",
            ModpackVersionId = versionId ?? "",
            ModpackName = name,
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        var instanceRoot = store.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);

        try
        {
            var files = root.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array
                    ? filesEl.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
            var downloadable = files.Where(IsClientPackFile).ToArray();

            for (var i = 0; i < downloadable.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packFile = downloadable[i];
                var relativePath = SanitizePackPath(GetString(packFile, "path"));
                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                if (!packFile.TryGetProperty("downloads", out var downloads) ||
                    downloads.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"Nincs letöltési URL: {relativePath}");
                var downloadUrl = downloads.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                EnsureSafeDownloadUrl(downloadUrl);

                var destination = PathSecurity.ResolveInsideRoot(instanceRoot, relativePath);
                var destDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                string? sha512 = null, sha1 = null;
                if (packFile.TryGetProperty("hashes", out var hashes) &&
                    hashes.ValueKind == JsonValueKind.Object)
                {
                    sha512 = GetString(hashes, "sha512");
                    sha1 = GetString(hashes, "sha1");
                }

                progress?.Report(new ModpackProgress(
                    $"Letöltés ({i + 1}/{downloadable.Length}): {relativePath}",
                    downloadable.Length == 0 ? 100 : (i * 100.0 / downloadable.Length)));
                await DownloadToFileAsync(downloadUrl, destination, sha512, sha1,
                    relativePath, null, cancellationToken).ConfigureAwait(false);
            }

            ExtractOverrides(archive, instanceRoot);

            var managedFiles = Directory.EnumerateFiles(instanceRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(instanceRoot, path).Replace('\\', '/'))
                .Where(path => !path.Equals(".turul-modpack.json", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await File.WriteAllTextAsync(
                Path.Combine(instanceRoot, ".turul-modpack.json"),
                JsonSerializer.Serialize(new
                {
                    name,
                    projectId,
                    versionId,
                    minecraftVersion,
                    loader,
                    loaderVersion,
                    managedFiles,
                    installedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new ModpackProgress("Modpack telepítve.", 100));
            LauncherLogger.Info($"Modpack telepítve: {name} ({downloadable.Length} fájl)");

            var newList = new List<LauncherInstance>(instances) { instance };
            var save = store.Save(newList, instance.Id);
            if (!save.Success)
                throw new IOException("Instance mentése nem sikerült: " + save.Error);
            instances.Add(instance);

            return new ModpackInstallResult(instance, instanceRoot, downloadable.Length);
        }
        catch
        {
            try { if (Directory.Exists(instanceRoot)) Directory.Delete(instanceRoot, true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Export-manifest építése egy Instance fájljaiból (mods, resourcepacks,
    /// config, stb. — a technikai fájlok kivételével). A fájlok SHA-256-ot
    /// kapnak, így a <see cref="ModpackPackager.ExportAsync"/> elfogadja.
    /// </summary>
    public static async Task<ModpackManifest> BuildExportManifestAsync(
        string instanceDir,
        string id,
        string version,
        string minecraftVersion,
        string loader,
        string loaderVersion,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceDir) || !Directory.Exists(instanceDir))
            throw new DirectoryNotFoundException("Az Instance mappa nem található: " + instanceDir);

        // Technikai fájlok/mappák, amik nem részei a packnak.
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "logs", "crash-reports", "screenshots", ".turul-meta", "cache" };
        var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".turul-modpack.json", "options.txt", "optionsof.txt" };

        var allFiles = Directory.EnumerateFiles(instanceDir, "*", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(instanceDir, full).Replace('\\', '/'))
            .Where(rel =>
            {
                var top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : rel;
                if (skipDirs.Contains(top)) return false;
                if (skipFiles.Contains(Path.GetFileName(rel))) return false;
                // A letöltött Minecraft-verziófájlok nem részei a packnak.
                if (rel.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) return false;
                if (rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)) return false;
                if (rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return false;
                if (rel.StartsWith("runtime/", StringComparison.OrdinalIgnoreCase)) return false;
                if (rel.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            })
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allFiles.Length == 0)
            throw new InvalidOperationException("Nincs exportálható fájl az Instance-ban.");

        var manifest = new ModpackManifest
        {
            Id = id,
            Version = version,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion
        };

        using var sha = SHA256.Create();
        for (var i = 0; i < allFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = allFiles[i];
            var full = PathSecurity.ResolveInsideRoot(instanceDir, rel);
            progress?.Report(new ModpackProgress(
                $"Hash ({i + 1}/{allFiles.Length}): {rel}",
                allFiles.Length == 0 ? 100 : (i * 100.0 / allFiles.Length)));

            await using var stream = File.OpenRead(full);
            var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            manifest.Files.Add(new ModpackFile
            {
                Path = rel,
                Sha256 = hash,
                Size = new FileInfo(full).Length
            });
        }

        progress?.Report(new ModpackProgress("Manifest kész.", 100));
        return manifest;
    }

    // ------------------------------------------------------------------ segédek

    internal static (string Loader, string LoaderVersion) ParseLoader(JsonElement deps)
    {
        if (deps.TryGetProperty("fabric-loader", out var fabric))
            return ("fabric", fabric.GetString() ?? "");

        foreach (var unsupported in new[] { "forge", "neoforge", "quilt-loader" })
        {
            if (deps.TryGetProperty(unsupported, out _))
                throw new InvalidOperationException(
                    $"Ez a modpack {unsupported} loadert használ. A TurulLauncher jelenleg Fabric/vanilla modpackot támogat.");
        }
        return ("none", "");
    }

    internal static bool IsClientPackFile(JsonElement file)
    {
        if (!file.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object) return true;
        if (!env.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.String) return true;
        return !string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pack-beli útvonal tisztítása. A <c>..</c>/<c>.</c> szegmensre és az
    /// érvénytelen karakterekre a Windows-verzióval egyezően kivételt dob —
    /// egy rosszindulatú bejegyzés az egész telepítést meghiúsítja (ilyenkor
    /// a félig kész mappa törlődik), nem pedig átnevezve landol.
    /// </summary>
    internal static string SanitizePackPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or ".."))
            throw new InvalidOperationException("A modpack tiltott fájlútvonalat tartalmaz.");
        if (parts.Any(x => x.Contains(':') || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("A modpack érvénytelen fájlútvonalat tartalmaz.");
        return string.Join('/', parts);
    }

    internal static void EnsureSafeDownloadUrl(string? url)
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
        if (bytes.Length != 4) return true; // IPv6-ot a pack-letöltésnél nem engedjük
        return (bytes[0] == 10) ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static void ExtractOverrides(
        System.IO.Compression.ZipArchive archive, string instanceRoot)
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
            if (string.IsNullOrEmpty(relative) || full.EndsWith('/')) continue;

            relative = SanitizePackPath(relative);
            if (string.IsNullOrEmpty(relative)) continue;
            var dest = PathSecurity.ResolveInsideRoot(instanceRoot, relative); // zip-slip guard
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: false);
            src.CopyTo(dst);
        }
    }

    private static string MakeUniqueInstanceName(string wanted, List<LauncherInstance> instances)
    {
        var names = new HashSet<string>(
            instances.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted)) return wanted;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (!names.Contains(candidate)) return candidate;
        }
        return $"{wanted} {Guid.NewGuid():N}";
    }

    private async Task DownloadToFileAsync(
        string url,
        string destination,
        string? expectedSha512,
        string? expectedSha1,
        string displayName,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".download";
        long? declared = null;
        try
        {
            using var response = await _download.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            declared = response.Content.Headers.ContentLength;
            var cap = destination.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                ? MaxMrpackBytes : MaxPackFileBytes;
            if (declared is <= 0 or > MaxMrpackBytes)
                throw new InvalidOperationException($"Fájlméret érvénytelen / túl nagy: {displayName}");
            if (declared > cap)
                throw new InvalidOperationException($"Fájlméret túl nagy: {displayName}");

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
                    if (total > cap)
                        throw new InvalidOperationException($"Fájl túl nagy: {displayName}");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedSha512))
                await VerifyHashAsync(temporary, expectedSha512, SHA512.Create(), displayName, cancellationToken)
                    .ConfigureAwait(false);
            else if (!string.IsNullOrWhiteSpace(expectedSha1))
                await VerifyHashAsync(temporary, expectedSha1, SHA1.Create(), displayName, cancellationToken)
                    .ConfigureAwait(false);

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }

        progress?.Report(new DownloadProgress
        {
            FileName = displayName,
            BytesReceived = declared ?? 0,
            TotalBytes = declared ?? 0,
            Status = "done"
        });
    }

    private static async Task VerifyHashAsync(
        string path, string expectedHex, HashAlgorithm algorithm,
        string displayName, CancellationToken cancellationToken)
    {
        using (algorithm)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(
                await algorithm.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"A letöltött fájl hash-e nem egyezik (sérült letöltés?): {displayName}");
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Mods.ModrinthClient.UserAgent);
        return http;
    }
}

/// <summary>Modpack-művelet folyamatjelzés (szöveg + százalék).</summary>
public sealed record ModpackProgress(string Text, double Percent);

/// <summary>Sikeres .mrpack-telepítés eredménye.</summary>
public sealed record ModpackInstallResult(
    LauncherInstance Instance,
    string InstanceDir,
    int FileCount);
