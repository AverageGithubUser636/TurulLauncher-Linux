using System.Text.Json;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Minecraft;

/// <summary>
/// Mojang verzió-manifest (`version_manifest_v2.json`) olvasása gyorsítótárral:
/// kiadás + pillanatkép lista az instance-szerkesztő legördülőjéhez.
/// A Windows-verzió ugyaninnen építette a listát (kódban, nem tesztelhetően);
/// itt a HTTP-réteg injektálható, a gyorsítótár-fájl útvonala megadható.
/// </summary>
public sealed class MinecraftVersionCatalog
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const int MaxEntries = 200;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string? _cachePath;

    public MinecraftVersionCatalog(HttpClient? http = null, string? cachePath = null)
    {
        _http = http ?? CreateClient();
        _cachePath = cachePath;
    }

    /// <summary>Verziók legfrissebb elöl (release + snapshot + old_beta...).</summary>
    /// <param name="includeSnapshots">Pillanatképek/béták is.</param>
    public async Task<IReadOnlyList<MinecraftVersionEntry>> ListAsync(
        bool includeSnapshots = false,
        CancellationToken cancellationToken = default)
    {
        var json = await LoadJsonAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MinecraftVersionEntry>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return Array.Empty<MinecraftVersionEntry>();

            var result = new List<MinecraftVersionEntry>();
            foreach (var item in list.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var type = GetString(item, "type") ?? "";
                if (!includeSnapshots && !type.Equals("release", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new MinecraftVersionEntry(
                    Id: id,
                    Type: type,
                    ReleaseTime: GetString(item, "releaseTime") ?? ""));
                if (result.Count >= MaxEntries) break;
            }
            return result;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Verziólista feldolgozási hiba: " + ex.Message);
            return Array.Empty<MinecraftVersionEntry>();
        }
    }

    private async Task<string?> LoadJsonAsync(CancellationToken cancellationToken)
    {
        // Friss gyorsítótár előnyben (offline is működik a lista).
        if (!string.IsNullOrWhiteSpace(_cachePath) && File.Exists(_cachePath))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
                if (age < CacheTtl)
                    return await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLogger.Debug("Verzió-gyorsítótár olvasási hiba: " + ex.Message);
            }
        }

        try
        {
            using var response = await _http.GetAsync(ManifestUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(_cachePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_cachePath);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(_cachePath, json, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LauncherLogger.Debug("Verzió-gyorsítótár írási hiba: " + ex.Message);
                }
            }
            return json;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Verziólista letöltési hiba: " + ex.Message);
            // Lejárt gyorsítótár még mindig jobb a semminél.
            if (!string.IsNullOrWhiteSpace(_cachePath) && File.Exists(_cachePath))
            {
                try { return await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false); }
                catch { }
            }
            return null;
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TurulMC-Launcher/4.6.0");
        return http;
    }
}

/// <summary>Egy Minecraft-verzió a manifestből.</summary>
public sealed record MinecraftVersionEntry(string Id, string Type, string ReleaseTime);
