using System.Text.Json;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Minecraft;

/// <summary>
/// Játékidő olvasása instance-onként (a launcher a <c>.turul-playtime.json</c>
/// fájlba gyűjti indításkor/kilépéskor). Csak olvas — írást továbbra is a
/// <c>MinecraftLauncherService</c> végez.
/// </summary>
public static class GamePlaytime
{
    /// <returns>Összesített másodpercek, vagy 0 ha nincs adat.</returns>
    public static long TryGetTotalSeconds(string instanceDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(instanceDir)) return 0;
            var path = Path.Combine(instanceDir, ".turul-playtime.json");
            if (!File.Exists(path)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("TotalPlaytimeSeconds", out var v) &&
                v.TryGetInt64(out var seconds) && seconds > 0)
                return seconds;
            // Régi séma: kisbetűs kulcs tolerálása.
            if (doc.RootElement.TryGetProperty("totalPlaytimeSeconds", out var v2) &&
                v2.TryGetInt64(out var s2) && s2 > 0)
                return s2;
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("Playtime olvasási hiba: " + ex.Message);
        }
        return 0;
    }

    /// <summary>Emberileg olvasható forma: „12ó 30p", „45p" vagy „3p".</summary>
    public static string FormatHu(long totalSeconds)
    {
        if (totalSeconds < 60) return $"{totalSeconds} mp";
        var minutes = totalSeconds / 60;
        if (minutes < 60) return $"{minutes}p";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours}ó" : $"{hours}ó {rest}p";
    }
}
