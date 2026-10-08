using System.Globalization;
using System.Text.RegularExpressions;

namespace TurulMC.Core.Java;

/// <summary>
/// Minecraft verzió → szükséges Java főverzió leképezés, valamint a meglévő
/// Java telepítések kompatibilitási rangsorolása (automatikus Java telepítéshez).
/// </summary>
public static class JavaVersionMap
{
    /// <summary>Az "1.x" klasszikus Minecraft verzióséma fő száma.</summary>
    private const int LegacySchemeMajor = 1;

    /// <summary>Ettől a főverziótól felfelé engedett a "legalább ennyi" kompatibilitás.</summary>
    private const int ForwardCompatibleFrom = 17;

    private static readonly Regex VersionPattern = new(
        @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Megadja a Minecraft verzióhoz szükséges Java főverziót.
    /// </summary>
    /// <param name="minecraftVersion">Minecraft verzió, pl. "1.21.4", "26.1.2", "24w14a".</param>
    /// <param name="manifestMajor">A verzió JSON <c>javaVersion.majorVersion</c> értéke (0, ha nincs ilyen).</param>
    /// <returns>A szükséges Java főverzió (8, 16, 17, 21 vagy 25).</returns>
    public static int ResolveRequiredMajor(string? minecraftVersion, int manifestMajor = 0)
    {
        // 1. szabály: a verzió JSON javaVersion.majorVersion mezője a mérvadó, ha értelmes (>= 8).
        if (manifestMajor >= 8) return manifestMajor;

        var (major, minor, patch) = ParseVersion(minecraftVersion);

        // 2. szabály: az új verzióséma (26.x és felette) Java 25-öt igényel.
        if (major >= 26) return 25;

        // 3. szabály: a 22–25 közötti sémák (és az ezekhez tartozó pillanatképek) Java 21-et igényelnek.
        if (major >= 22) return 21;

        // 4. szabály: minden más major érték (pl. pillanatkép "11w47a", értelmezhetetlen verzió) → modern alapérték.
        if (major != LegacySchemeMajor) return 21;

        // 5. szabály: az 1.21.x és minden annál újabb 1.x verzió Java 21-et igényel.
        if (minor >= 21) return 21;

        // 6. szabály: az 1.20.5-től Java 21 kell; az 1.20.5 alatti 1.20.x még Java 17-tel fut.
        if (minor == 20) return patch >= 5 ? 21 : 17;

        // 7. szabály: az 1.18.x és 1.19.x verziók Java 17-et igényelnek.
        if (minor is 18 or 19) return 17;

        // 8. szabály: az 1.17.x az első Java 16-ot igénylő verzió.
        if (minor == 17) return 16;

        // 9. szabály: az 1.16.x és minden régebbi verzió Java 8-cal fut.
        if (minor <= 16) return 8;

        // Biztonsági alapérték (ide elvileg nem jutunk el).
        return 21;
    }

    /// <summary>
    /// Igaz, ha a meglévő <paramref name="actualMajor"/> Java verzió megfelel a
    /// <paramref name="requiredMajor"/> igénynek. Pontos egyezés mindig jó; ennél
    /// újabb Java csak 17-es (vagy újabb) igény esetén elfogadható.
    /// </summary>
    public static bool IsCompatible(int actualMajor, int requiredMajor)
        => actualMajor == requiredMajor || (actualMajor > requiredMajor && requiredMajor >= ForwardCompatibleFrom);

    /// <summary>
    /// Kiválasztja a legjobban illeszkedő elérhető Java főverziót: először pontos
    /// egyezés, különben a legkisebb kompatibilis főverzió, egyébként 0.
    /// </summary>
    public static int PickBest(IEnumerable<int> availableMajors, int requiredMajor)
    {
        if (availableMajors is null) return 0;

        var candidates = availableMajors.Where(major => major > 0).Distinct().ToList();
        if (candidates.Count == 0) return 0;

        if (candidates.Contains(requiredMajor)) return requiredMajor;

        var compatible = candidates.Where(major => IsCompatible(major, requiredMajor)).OrderBy(major => major).ToList();
        return compatible.Count > 0 ? compatible[0] : 0;
    }

    /// <summary>
    /// A verziószám első három numerikus komponense ("v" előtag és utótagok nélkül).
    /// </summary>
    private static (int Major, int Minor, int Patch) ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (0, 0, 0);

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var match = VersionPattern.Match(text);
        if (!match.Success) return (0, 0, 0);

        return (
            ParseComponent(match.Groups[1].Value),
            ParseComponent(match.Groups[2].Value),
            ParseComponent(match.Groups[3].Value));
    }

    private static int ParseComponent(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
