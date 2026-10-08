namespace TurulMC.Core.Java;

/// <summary>
/// Egy letölthető Adoptium (Eclipse Temurin) JRE csomag leírása.
/// </summary>
public sealed class AdoptiumRelease
{
    /// <summary>Java főverzió, pl. 21.</summary>
    public int FeatureVersion { get; set; }

    /// <summary>Teljes verziószám, pl. "21.0.5+11".</summary>
    public string FullVersion { get; set; } = string.Empty;

    /// <summary>HTTPS letöltési cím (github.com vagy *.githubusercontent.com).</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>A csomag SHA-256 lenyomata (64 hexadecimális karakter).</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>A csomag mérete bájtban.</summary>
    public long SizeBytes { get; set; }

    /// <summary>A csomag fájlneve (csak név, elérési út nélkül, .zip végződéssel).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Architektúra: x64, x86 vagy aarch64.</summary>
    public string Architecture { get; set; } = "x64";

    /// <summary>A csomag szállítója (alapértelmezés: Eclipse Temurin).</summary>
    public string Vendor { get; set; } = "Eclipse Temurin";
}

/// <summary>
/// Az engedélyezett Adoptium/Temurin letöltési hosztok (fail-closed allowlista).
/// </summary>
public static class AdoptiumHosts
{
    /// <summary>Engedélyezett letöltési hosztok.</summary>
    public static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "api.adoptium.net"
    };

    /// <summary>
    /// Igaz, ha az URL abszolút HTTPS cím, és a hoszt az allowlistán szerepel,
    /// vagy a <c>*.githubusercontent.com</c> alá tartozik.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;

        foreach (var allowed in AllowedDownloadHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
