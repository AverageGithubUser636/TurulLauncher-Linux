using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurulMC.Core.Http;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Java;

/// <summary>
/// Eclipse Adoptium (Temurin) API kliens: a megadott főverzióhoz tartozó legfrissebb
/// Windows JRE csomag felderítése. A hálózati hívás mellett a JSON feldolgozás
/// külön, tiszta (pure) statikus metódusban él, hogy külön tesztelhető legyen.
/// </summary>
public sealed class AdoptiumReleaseClient
{
    private const long MaxResponseBytes = 2L * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly LauncherHttpClient _httpClient;

    /// <summary>
    /// Létrehozza a klienst. Ha nincs megadva HTTP kliens, sajátot példányosít.
    /// </summary>
    public AdoptiumReleaseClient(LauncherHttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new LauncherHttpClient();
    }

    /// <summary>
    /// Az Adoptium "latest assets" végpontja a kért főverzióhoz és architektúrához.
    /// Az <c>os</c> szűrő a futtatási platformhoz igazodik, így Linuxon Linux JRE-t
    /// kérünk (különben a launcher egy Windows .zip-et telepítene).
    /// </summary>
    public static string BuildLatestJreUrl(int featureVersion, string architecture)
        => BuildLatestJreUrl(featureVersion, architecture, CurrentOsToken());

    /// <summary>
    /// Ugyanaz, de explicit megadott <c>os</c> szűrővel (tesztekhez).
    /// </summary>
    /// <param name="osToken">Adoptium os érték: <c>windows</c>, <c>linux</c> vagy <c>mac</c>.</param>
    public static string BuildLatestJreUrl(int featureVersion, string architecture, string osToken)
        => $"https://api.adoptium.net/v3/assets/latest/{featureVersion}/hotspot" +
           $"?architecture={Uri.EscapeDataString(architecture)}" +
           $"&heap_size=normal&image_type=jre&jvm_impl=hotspot" +
           $"&os={Uri.EscapeDataString(osToken)}&vendor=eclipse";

    /// <summary>
    /// A futó platform Adoptiumnak megfelelő <c>os</c> értéke.
    /// </summary>
    public static string CurrentOsToken()
        => OperatingSystem.IsWindows() ? "windows"
         : OperatingSystem.IsMacOS() ? "mac"
         : "linux";

    /// <summary>
    /// A platformhoz tartozó csomagkiterjesztés: Windowson <c>.zip</c>,
    /// Linuxon és macOS-en a Temurin <c>.tar.gz</c>-t ad.
    /// </summary>
    public static string CurrentArchiveExtension()
        => OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    /// <summary>
    /// Ellenőrzi és normalizálja az architektúra nevet (x64, x86, aarch64).
    /// </summary>
    /// <exception cref="ArgumentException">Nem támogatott architektúra.</exception>
    public static string NormalizeArchitecture(string? architecture)
    {
        var value = (architecture ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "x64" or "amd64" or "x86_64" or "x86-64" => "x64",
            "x86" or "i386" or "i686" => "x86",
            "aarch64" or "arm64" => "aarch64",
            _ => throw new ArgumentException("Támogatott architektúrák: x64, x86, aarch64.", nameof(architecture))
        };
    }

    /// <summary>
    /// Lekéri a legfrissebb, adott főverziójú JRE csomagot az Adoptiumtól —
    /// a futó platformhoz illő csomagot.
    /// </summary>
    /// <param name="featureVersion">Java főverzió (8–99).</param>
    /// <param name="architecture">Architektúra: x64, x86 vagy aarch64.</param>
    /// <param name="cancellationToken">Megszakítási token.</param>
    /// <exception cref="ArgumentException">Érvénytelen főverzió vagy architektúra.</exception>
    /// <exception cref="InvalidOperationException">Hálózati hiba, túl nagy válasz, vagy nincs elérhető letöltés.</exception>
    public async Task<AdoptiumRelease> GetLatestJreAsync(int featureVersion, string architecture = "x64", CancellationToken cancellationToken = default)
    {
        if (featureVersion is < 8 or > 99)
            throw new ArgumentException("A Java főverziónak 8 és 99 között kell lennie.", nameof(featureVersion));

        var normalizedArchitecture = NormalizeArchitecture(architecture);
        var url = BuildLatestJreUrl(featureVersion, normalizedArchitecture);

        string json;
        try
        {
            json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error($"Adoptium API hiba (Java {featureVersion}, {normalizedArchitecture}).", ex);
            throw new InvalidOperationException($"Nem sikerült elérni az Adoptium API-t (Java {featureVersion}).", ex);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
        {
            LauncherLogger.Error($"Az Adoptium válasz túl nagy (Java {featureVersion}).");
            throw new InvalidOperationException($"Az Adoptium válasz túl nagy (Java {featureVersion}).");
        }

        var release = ParseLatestRelease(json, featureVersion, normalizedArchitecture);
        if (release is null)
        {
            LauncherLogger.Warning($"Nem található Java {featureVersion} letöltés az Adoptiumnál ({normalizedArchitecture}).");
            throw new InvalidOperationException($"Nem található Java {featureVersion} letöltés az Adoptiumnál.");
        }

        LauncherLogger.Info($"Adoptium JRE kiválasztva: {release.FullVersion} ({release.Architecture}) — {release.FileName}");
        return release;
    }

    /// <summary>
    /// Feldolgozza az Adoptium válasz JSON-t. Tiszta és determinisztikus: hiba esetén
    /// mindig <c>null</c>-t ad vissza, kivétel nem szökhet ki.
    /// </summary>
    /// <param name="json">A válasz törzse. Elsődlegesen JSON tömb a gyökér (az Adoptium így válaszol),
    /// de egy <c>assets</c> tömböt tartalmazó burkoló objektum is elfogadott.</param>
    /// <param name="expectedFeatureVersion">A kért Java főverzió.</param>
    /// <param name="architecture">A kért architektúra; ha egyetlen jelölt sem egyezik, az első érvényes jelölt nyer.</param>
    /// <returns>A kiválasztott csomag, vagy <c>null</c>, ha nincs érvényes jelölt.</returns>
    public static AdoptiumRelease? ParseLatestRelease(string json, int expectedFeatureVersion, string architecture = "x64")
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var requestedArchitecture = (architecture ?? string.Empty).Trim();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return null;
                root = assets;
            }

            if (root.ValueKind != JsonValueKind.Array) return null;

            AdoptiumRelease? fallback = null;
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var candidate = TryReadCandidate(element, expectedFeatureVersion, requestedArchitecture);
                if (candidate is null) continue;

                if (requestedArchitecture.Length == 0 ||
                    candidate.Architecture.Equals(requestedArchitecture, StringComparison.OrdinalIgnoreCase))
                    return candidate;

                fallback ??= candidate;
            }

            return fallback;
        }
        catch (Exception)
        {
            // Szándékosan "pure" parser: minden hiba (érvénytelen JSON, formátumhiba) null-t ad.
            return null;
        }
    }

    private static AdoptiumRelease? TryReadCandidate(JsonElement element, int expectedFeatureVersion, string requestedArchitecture)
    {
        if (!element.TryGetProperty("binary", out var binary) || binary.ValueKind != JsonValueKind.Object) return null;
        if (!binary.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object) return null;

        var link = GetString(package, "link");
        if (string.IsNullOrWhiteSpace(link) || !AdoptiumHosts.IsAllowedDownloadUrl(link)) return null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;

        var checksum = GetString(package, "checksum");
        if (string.IsNullOrWhiteSpace(checksum) || !Sha256Pattern.IsMatch(checksum)) return null;

        var size = GetLong(package, "size");
        if (size <= 0 || size > MaxPackageBytes) return null;

        var fileName = ResolveFileName(uri, GetString(package, "name"));
        if (fileName is null) return null;

        var major = 0;
        string? semver = null;
        string? openJdkVersion = null;
        if (element.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
        {
            var majorValue = GetLong(version, "major");
            major = majorValue is > 0 and <= 1000 ? (int)majorValue : 0;
            semver = GetString(version, "semver");
            openJdkVersion = GetString(version, "openjdk_version");
        }

        // Ha a válasz megadja a főverziót, annak egyeznie kell a kéréssel.
        if (major > 0 && major != expectedFeatureVersion) return null;

        var releaseName = GetString(element, "release_name");
        var fullVersion = FirstNonEmpty(semver, openJdkVersion, releaseName)
            ?? (major > 0 ? major.ToString(CultureInfo.InvariantCulture) : string.Empty);

        var architecture = GetString(binary, "architecture");
        if (string.IsNullOrWhiteSpace(architecture))
            architecture = requestedArchitecture;

        return new AdoptiumRelease
        {
            FeatureVersion = major > 0 ? major : expectedFeatureVersion,
            FullVersion = fullVersion,
            DownloadUrl = link,
            // A checksumot változatlanul adjuk tovább; az összehasonlítás kis/nagybetűtől független.
            Sha256 = checksum,
            SizeBytes = size,
            FileName = fileName,
            Architecture = string.IsNullOrWhiteSpace(architecture) ? "unknown" : architecture,
            Vendor = "Eclipse Temurin"
        };
    }

    private static string? ResolveFileName(Uri uri, string? declaredName)
    {
        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(declaredName))
            fileName = Path.GetFileName(Uri.UnescapeDataString(declaredName));

        if (string.IsNullOrWhiteSpace(fileName)) return null;

        // Windowson .zip, Linux/macOS-on .tar.gz a Temurin csomagformátum.
        if (!HasSupportedArchiveExtension(fileName)) return null;
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return fileName;
    }

    /// <summary>
    /// Elfogad <c>.zip</c>-et és <c>.tar.gz</c>-t (utóbbi a Linux/macOS csomag).
    /// </summary>
    public static bool HasSupportedArchiveExtension(string fileName)
        => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out var number) ? number : 0;
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            default:
                return 0;
        }
    }
}
