namespace TurulMC.Core.Mods;

/// <summary>
/// Mod-verziók összehasonlítása. A Modrinth <c>version_number</c> mezője gyakran nem tiszta
/// szemverzió: <c>mc26.3-0.9.3-alpha.1-neoforge</c>, <c>0.9.2-fabric</c>, <c>1.2.3+1.21.4</c>.
/// A numerikus magot kiemelve mégis összehasonlíthatóvá tesszük őket — enélkül a
/// „ne lépj vissza régebbi verzióra” védelem néma lenne a valós Modrinth adatokon.
/// </summary>
public static class ModVersionOrder
{
    /// <summary>
    /// Az összehasonlítható mag: ha a teljes szöveg értelmezhető verzió, akkor az; különben az
    /// első olyan <c>-</c> utáni szegmens, amely számmal kezdődik (a platform-előtag, pl.
    /// <c>mc26.3-</c>, <c>fabric-</c>, <c>neoforge-</c> levágásra kerül, az utótag megmarad).
    /// </summary>
    public static string ExtractComparableCore(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var text = version.Trim();
        if (MinecraftVersionOrder.IsComparable(text)) return text;

        var segments = text.Split('-');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();
            if (segment.Length == 0 || !char.IsDigit(segment[0])) continue;
            return string.Join('-', segments.Skip(i).Select(x => x.Trim()));
        }

        return "";
    }

    /// <summary>
    /// Két mod-verzió összehasonlítása a numerikus mag és a kiadás-előtti utótag alapján.
    /// <c>null</c>, ha egyik sem értelmezhető.
    /// </summary>
    public static int? TryCompare(string? left, string? right)
    {
        var leftCore = ExtractComparableCore(left);
        var rightCore = ExtractComparableCore(right);
        if (leftCore.Length == 0 || rightCore.Length == 0) return null;
        return MinecraftVersionOrder.TryCompare(leftCore, rightCore);
    }

    /// <summary>Igaz, ha <paramref name="candidate"/> bizonyítottan régebbi a <paramref name="installed"/>nél.</summary>
    public static bool IsOlder(string? candidate, string? installed)
        => TryCompare(candidate, installed) is < 0;

    /// <summary>Igaz, ha <paramref name="candidate"/> bizonyítottan újabb a <paramref name="installed"/>nél.</summary>
    public static bool IsNewer(string? candidate, string? installed)
        => TryCompare(candidate, installed) is > 0;
}
