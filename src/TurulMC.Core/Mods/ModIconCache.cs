using TurulMC.Core.Logging;
using TurulMC.Core.Security;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Mods;

/// <summary>
/// Modrinth projekt-ikonok letöltése és helyi gyorsítótárazása
/// (<c>DataRoot/cache/icons/&lt;projectId&gt;.&lt;ext&gt;</c>).
/// Csak a Modrinth CDN-ről tölt, méretkorláttal (2 MB), és ellenőrzi,
/// hogy a fájl valóban kép (PNG/JPEG/WebP fejléc).
/// Hiba esetén <c>null</c>-t ad — az ikon hiánya sosem töri el a listát.
/// </summary>
public sealed class ModIconCache
{
    public const long MaxIconBytes = 2L * 1024 * 1024;

    private readonly string _cacheDir;
    private readonly HttpClient _http;

    public ModIconCache(string? cacheDir = null, HttpClient? http = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(LauncherPaths.DataRoot, "cache", "icons");
        _http = http ?? CreateClient();
    }

    /// <returns>A helyi ikonfájl útvonala, vagy <c>null</c>.</returns>
    public async Task<string?> GetIconPathAsync(
        string projectId,
        string? iconUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(iconUrl))
            return null;

        var safeId = Sanitize(projectId);
        if (string.IsNullOrEmpty(safeId)) return null;

        // Már letöltött? Bármilyen kiterjesztéssel elfogadjuk.
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var existing = Path.Combine(_cacheDir, safeId + ext);
            if (File.Exists(existing)) return existing;
        }

        if (!IsAllowedIconUrl(iconUrl)) return null;

        try
        {
            Directory.CreateDirectory(_cacheDir);
            using var response = await _http.GetAsync(
                iconUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared is <= 0 or > MaxIconBytes) return null;

            var ext = GuessExtension(
                response.Content.Headers.ContentType?.MediaType, iconUrl);
            var destination = PathSecurity.ResolveInsideRoot(_cacheDir, safeId + ext);
            var temporary = destination + ".download";

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[32 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxIconBytes)
                    {
                        try { File.Delete(temporary); } catch { }
                        return null;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!LooksLikeImage(temporary))
            {
                try { File.Delete(temporary); } catch { }
                return null;
            }

            // Régi kiterjesztésű maradék törlése (ha az URL változott).
            // A .download átmeneti fájlt kihagyjuk — a glob (safeId + ".*")
            // arra is illeszkedne, és a Move előtt törölné (ezt találta meg a teszt).
            foreach (var old in Directory.EnumerateFiles(_cacheDir, safeId + ".*"))
            {
                if (old.EndsWith(".download", StringComparison.OrdinalIgnoreCase)) continue;
                if (!old.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { }
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);
            return destination;
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Ikon letöltési hiba ({projectId}): {ex.Message}");
            return null;
        }
    }

    internal static bool IsAllowedIconUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessExtension(string? mediaType, string url)
    {
        var byType = mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null
        };
        if (byType is not null) return byType;

        var lower = url.ToLowerInvariant();
        if (lower.Contains(".png")) return ".png";
        if (lower.Contains(".webp")) return ".webp";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return ".jpg";
        return ".png";
    }

    internal static bool LooksLikeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            var read = stream.Read(header);
            if (read < 4) return false;

            // PNG: 89 50 4E 47 · JPEG: FF D8 FF · WebP: RIFF....WEBP · GIF: 47 49 46 38
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
            if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P') return true;
            if (header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8') return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Sanitize(string projectId)
    {
        var clean = new string(projectId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length > 64 ? clean[..64] : clean;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        return http;
    }
}
