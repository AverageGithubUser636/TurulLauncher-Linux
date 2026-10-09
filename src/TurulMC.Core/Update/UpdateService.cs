using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Security;

namespace TurulMC.Core.Update;

/// <summary>
/// Frissítés-ellenőrzés és hordozható (portable) telepítés Linuxon.
/// <list type="bullet">
/// <item>Csatornák: <c>stable</c> és <c>beta</c> (a Windows-verzióval azonos
/// manifest-URL-ek).</item>
/// <item>Telepítés csak hordozható módban: a bináris melletti
/// <c>TurulMC.portable</c> jelölőfájl megléte esetén. Ilyenkor a ZIP
/// SHA-256 ellenőrzéssel letöltődik, kicsomagolódik a telepítési
/// könyvtárra, és a launcher újraindul. Linuxon a futó bináris
/// cseréje biztonságos (az inode a kilépésig él).</item>
/// <item>Jelölő nélkül (csomagos telepítés, <c>dotnet run</c> fejlesztés)
/// csak ellenőrzés + kézi letöltési link jár — az updater sosem nyúl
/// idegen telepítéshez.</item>
/// </list>
/// </summary>
public sealed class UpdateService
{
    public const string StableManifestUrl = "https://turulnetwork.hu/launcher/update/stable.json";
    public const string BetaManifestUrl = "https://turulnetwork.hu/launcher/update/beta.json";

    /// <summary>
    /// A Linux-fork saját manifestjei. A fork önálló verziószámozást használ
    /// (1.0.0-tól), ezért NEM a Windows-manifestet olvassa — különben a
    /// Windows 4.x „frissítésként" jelentkezne. A weboldalon ehhez a
    /// <c>launcher/linux/update/stable.json</c> (és <c>beta.json</c>) fájlt
    /// kell publikálni (a <c>publishall.sh</c> legenerálja a tartalmát).
    /// </summary>
    public const string LinuxStableManifestUrl = "https://turulnetwork.hu/launcher/linux/update/stable.json";
    public const string LinuxBetaManifestUrl = "https://turulnetwork.hu/launcher/linux/update/beta.json";
    public const string PortableMarkerFile = "TurulMC.portable";
    public const long MaxPackageBytes = 1024L * 1024L * 1024L;

    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? CreateClient();
    }

    public static string NormalizeChannel(string? channel)
        => string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    public static string ManifestUrlFor(string channel)
        => NormalizeChannel(channel) == "beta" ? BetaManifestUrl : StableManifestUrl;

    /// <summary>Aktuális verzió vs. manifest összevetése.</summary>
    /// <param name="linuxFork">Igaz (alapértelmezett) esetén a Linux-fork
    /// saját manifestjét olvassa; hamis esetén a Windows-URL-eket.</param>
    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        string channel,
        bool linuxFork = true,
        CancellationToken cancellationToken = default)
    {
        channel = NormalizeChannel(channel);
        var url = linuxFork
            ? (channel == "beta" ? LinuxBetaManifestUrl : LinuxStableManifestUrl)
            : ManifestUrlFor(channel);
        LauncherUpdateManifest manifest;
        try
        {
            manifest = await GetManifestFromUrlAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Frissítés-ellenőrzés hiba ({channel}): {ex.Message}");
            return new UpdateCheckResult(
                Channel: channel,
                CurrentVersion: currentVersion,
                Available: false,
                Manifest: null,
                Error: "A frissítés-szerver nem elérhető: " + ex.Message);
        }

        var available = CompareVersions(manifest.Version, currentVersion) > 0;
        return new UpdateCheckResult(
            Channel: channel,
            CurrentVersion: currentVersion,
            Available: available,
            Manifest: manifest,
            Error: null);
    }

    /// <summary>Manifest tetszőleges URL-ről (a Linux-fork saját URL-jeihez).</summary>
    public async Task<LauncherUpdateManifest> GetManifestFromUrlAsync(
        string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;

        var version = GetString(root, "version") ?? "";
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("A frissítési manifestben nincs verzió.");

        var changelog = new List<string>();
        if (root.TryGetProperty("changelog", out var log) && log.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in log.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    changelog.Add(item.GetString()!);
            }
        }

        return new LauncherUpdateManifest(
            Version: version,
            Required: root.TryGetProperty("required", out var req) &&
                (req.ValueKind == JsonValueKind.True ||
                 (req.ValueKind == JsonValueKind.String &&
                  bool.TryParse(req.GetString(), out var b) && b)),
            Url: GetString(root, "url") ?? "",
            Sha256: GetString(root, "sha256") ?? "",
            PublishedAt: GetString(root, "publishedAt") ?? "",
            Changelog: changelog);
    }

    /// <summary>Szemantikus verzió-összehasonlítás (4.6.0 vs. 4.6.0, előtag-tűrő).</summary>
    /// <returns>Pozitív, ha <c>left</c> újabb.</returns>
    public static int CompareVersions(string? left, string? right)
    {
        var l = Parse(left);
        var r = Parse(right);
        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            var a = i < l.Length ? l[i] : 0;
            var b = i < r.Length ? r[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    internal static int[] Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();
        var clean = version.Trim().TrimStart('v', 'V');
        // Előtagok (pl. "4.6.0-linux", "4.6.0 PREVIEW") levágása.
        var end = clean.IndexOfAny(new[] { '-', ' ', '+' });
        if (end >= 0) clean = clean[..end];
        return clean.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? Math.Max(0, n) : 0)
            .ToArray();
    }

    // ------------------------------------------------------------- telepítés

    /// <summary>Hordozható módban vagyunk? (Jelölőfájl a bináris mellett.)</summary>
    public static bool IsPortable(string? baseDirectory = null)
    {
        try
        {
            var dir = baseDirectory ?? AppContext.BaseDirectory;
            return File.Exists(Path.Combine(dir, PortableMarkerFile));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hordozható frissítés: ZIP letöltés SHA-256 ellenőrzéssel, kicsomagolás
    /// a telepítési könyvtárra (zip-slip védelemmel), majd újraindulás.
    /// Csak <see cref="IsPortable"/> esetén hívható — egyébként kivételt dob,
    /// hogy fejlesztői/csopagos telepítést sose írjunk felül.
    /// </summary>
    public async Task InstallPortableAsync(
        LauncherUpdateManifest manifest,
        string installDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Url))
            throw new InvalidOperationException("A manifestben nincs letöltési URL.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new InvalidOperationException("A manifestben nincs SHA-256 — ellenőrzés nélkül nem telepítünk.");
        if (!IsPortable(installDir) && !IsPortable())
            throw new InvalidOperationException(
                "Nem hordozható telepítés — használd a csomagkezelőt vagy a kézi letöltést.");
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            throw new DirectoryNotFoundException("Telepítési könyvtár nem található: " + installDir);

        EnsureSafeUpdateUrl(manifest.Url);

        var temp = Path.Combine(Path.GetTempPath(), $"TurulUpdate-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadToFileAsync(manifest.Url, temp, progress, cancellationToken)
                .ConfigureAwait(false);
            VerifySha256(temp, manifest.Sha256);
            ExtractPackage(temp, installDir);
            LauncherLogger.Info($"Hordozható frissítés telepítve: {manifest.Version}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Frissítési URL engedélyezése (HTTPS + turulnetwork.hu). Nyilvános a tesztelhetőségért.</summary>
    public static void EnsureSafeUpdateUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A frissítés csak HTTPS URL-ről telepíthető.");
        if (!uri.Host.Equals("turulnetwork.hu", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".turulnetwork.hu", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ismeretlen frissítés-forrás: " + uri.Host);
    }

    private async Task DownloadToFileAsync(
        string url,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared is <= 0 or > MaxPackageBytes)
            throw new InvalidOperationException("A frissítési csomag méret érvénytelen / túl nagy.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var output = File.Create(destination))
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                    throw new InvalidOperationException("A frissítési csomag túl nagy.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new DownloadProgress
                {
                    FileName = "frissítés",
                    BytesReceived = total,
                    TotalBytes = declared ?? total,
                    Status = "downloading"
                });
            }
        }
    }

    /// <summary>SHA-256 ellenőrzés. Nyilvános a tesztelhetőségért.</summary>
    public static void VerifySha256(string path, string expectedHex)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "A letöltött csomag hash-e nem egyezik — a telepítés megszakítva.");
    }

    /// <summary>ZIP kicsomagolás zip-slip védelemmel. Nyilvános a tesztelhetőségért.</summary>
    public static void ExtractPackage(string zipPath, string installDir)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            // A ResolveInsideRoot a ".."-t csendben szűrné (átnevezett
            // telepítés lenne) — itt inkább dobunk, mint a modpacknél:
            // egy traversal-bejegyzés rosszindulatú vagy hibás csomagot jelez.
            var segments = entry.FullName.Replace('\\', '/').Split('/');
            if (segments.Any(s => s is "." or ".."))
                throw new InvalidOperationException(
                    "A frissítési csomag tiltott fájlútvonalat tartalmaz.");
            var destination = PathSecurity.ResolveInsideRoot(installDir, entry.FullName);
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            using var src = entry.Open();
            using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: false);
            src.CopyTo(dst);
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Mods.ModrinthClient.UserAgent);
        return http;
    }
}

/// <summary>Frissítési manifest (stable.json / beta.json).</summary>
public sealed record LauncherUpdateManifest(
    string Version,
    bool Required,
    string Url,
    string Sha256,
    string PublishedAt,
    IReadOnlyList<string> Changelog);

/// <summary>Ellenőrzés eredménye.</summary>
public sealed record UpdateCheckResult(
    string Channel,
    string CurrentVersion,
    bool Available,
    LauncherUpdateManifest? Manifest,
    string? Error);
