using TurulMC.Core.Models;

namespace TurulMC.Core.Validation;

/// <summary>
/// Central strict validation for launcher settings (4.4.6).
/// Closes javaPath RCE: only java.exe/javaw.exe, must exist, no args/metachars.
/// </summary>
public static class LauncherSettingsValidator
{
    private static readonly string[] DeniedJvmFragments =
    {
        "-agentlib", "-agentpath", "-javaagent", "-Xbootclasspath",
        "-Djava.security", "-Dcom.sun", "-XX:+DisableExplicitGC=false",
        ";", "|", "&", "$(", "`", "\n", "\r"
    };

    public static void Validate(LauncherSettings s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        if (s.DefaultRamMb is < 1024 or > 65536)
            throw new ArgumentException("RAM 1024–65536 MB között lehet.");
        if (s.TestServerPort is < 1 or > 65535)
            throw new ArgumentException("Port 1–65535 között lehet.");
        if (!string.IsNullOrWhiteSpace(s.TestServerHost) && (s.TestServerHost.Length > 253 || s.TestServerHost.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Érvénytelen szerver host.");
        if (s.Loader is not ("none" or "fabric"))
            throw new ArgumentException("Loader csak: none | fabric.");
        if (!string.IsNullOrWhiteSpace(s.JavaPathOverride))
            ValidateJavaPath(s.JavaPathOverride);
        if (!string.IsNullOrWhiteSpace(s.ModpackManifestUrl))
        {
            if (!Uri.TryCreate(s.ModpackManifestUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "file"))
                throw new ArgumentException("Manifest URL csak https:// vagy file:// lehet.");
        }
    }

    public static void ValidateJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Üres javaPath.");
        if (path.Any(c => c is '"' or ';' or '|' or '&' or '$' or '`' or '\n' or '\r'))
            throw new ArgumentException("javaPath nem tartalmazhat parancs-metacharaktereket.");
        if (path.Contains(" -", StringComparison.Ordinal) || path.Contains(".exe ", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("javaPath nem tartalmazhat argumentumokat (RCE zárás).");

        var file = path.Trim().Trim('"');
        if (!HasValidJavaFileName(file))
            throw new ArgumentException(OperatingSystem.IsWindows()
                ? "javaPath csak java.exe / javaw.exe lehet."
                : "javaPath csak java (vagy java.exe) lehet.");

        if (!File.Exists(file))
            throw new FileNotFoundException("javaPath nem létezik.", file);
    }

    /// <summary>
    /// Elfogadja a platformon érvényes Java futtatható neveket.
    /// Windows: <c>java.exe</c>, <c>javaw.exe</c>; Linux/macOS: a <c>java</c>.
    /// A fájlnévnek pontosan egyeznie kell (ezért a <c>notjava</c> nem megy át).
    /// </summary>
    private static bool HasValidJavaFileName(string file)
    {
        var name = Path.GetFileName(file);
        if (string.IsNullOrEmpty(name)) return false;

        if (name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        // A "java" név csak nem-Windowson elfogadott, hogy a RCE-zár
        // (argumentumok elválasztása) továbbra is zárva maradjon.
        return !OperatingSystem.IsWindows() &&
               name.Equals("java", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateJvmArgs(IEnumerable<string> args)
    {
        foreach (var a in args ?? Enumerable.Empty<string>())
        {
            var lower = (a ?? "").ToLowerInvariant();
            if (DeniedJvmFragments.Any(f => lower.Contains(f.ToLowerInvariant())))
                throw new ArgumentException($"Tiltott JVM argumentum: {a}");
        }
    }
}
