using System.Text.RegularExpressions;

namespace TurulMC.Core.Security;

/// <summary>
/// Central path + deletion + download guards (4.4.6 audit fixes).
/// </summary>
public static class PathSecurity
{
    public static string ResolveInsideRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sanitized = SanitizeRelative(relative);
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new InvalidOperationException("Érvénytelen fájlútvonal.");
        var full = Path.GetFullPath(Path.Combine(fullRoot, sanitized));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A fájl az Instance mappán kívülre mutat (traversal blokkolva).");
        return full;
    }

    public static string SanitizeRelative(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part is "." or "..") continue;
            if (part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"Érvénytelen útvonalszegmens: {part}");
            result.Add(part);
        }
        return string.Join(Path.DirectorySeparatorChar, result);
    }

    public static void SafeDeleteFile(string root, string relativeOrFull)
    {
        string full = Path.IsPathRooted(relativeOrFull)
            ? Path.GetFullPath(relativeOrFull)
            : ResolveInsideRoot(root, relativeOrFull);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Törlés blokkolva: kilépne a gyökérből.");
        if (!File.Exists(full)) return;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Törlés blokkolva (symlink): {Path.GetFileName(full)}");
        File.Delete(full);
    }

    public static void SafeDeleteDirectory(string root, string relativeOrFull)
    {
        string full = Path.IsPathRooted(relativeOrFull)
            ? Path.GetFullPath(relativeOrFull)
            : ResolveInsideRoot(root, relativeOrFull);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || full.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Könyvtár törlése blokkolva.");
        if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Törlés blokkolva (symlink a fában): {file}");
        }
        Directory.Delete(full, true);
    }

    public static void EnsureSafeHttpsUrl(string url, bool allowLocalhost = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Csak HTTPS URL engedélyezett.");
        if (!allowLocalhost && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Helyi cím nem engedélyezett.");
    }
}
