using System.Globalization;
using System.Text.RegularExpressions;

namespace TurulMC.Core.Mods;

/// <summary>
/// Minecraft-verziók összehasonlítása. A séma 2026-tól <c>26.1.2</c> formátumú, korábban
/// <c>1.21.11</c>; ezek numerikusan összehasonlíthatók. A pillanatképek (<c>25w14a</c>)
/// és a kiadás-előtti azonosítók nem sorolhatók be egyértelműen, ezért ott a
/// összehasonlítás eredménye "nem eldönthető" (null), nem pedig hamis egyezés.
/// </summary>
public static class MinecraftVersionOrder
{
    private static readonly Regex SnapshotPattern = new(
        @"^\d{2}w\d{2}[a-z]$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Igaz, ha a verzió pillanatkép (pl. <c>25w14a</c>).</summary>
    public static bool IsSnapshot(string? version)
        => !string.IsNullOrWhiteSpace(version) && SnapshotPattern.IsMatch(version.Trim());

    /// <summary>
    /// A verzió numerikus komponensei. A "v" előtag, a záró "-pre1"/"-rc1" utótag és a
    /// "release"/"snapshot" szöveg eltávolításra kerül; ha egy komponens nem szám,
    /// a numerikus előtagját vesszük (pl. <c>1.21.4-pre1</c> → 1.21.4).
    /// </summary>
    public static IReadOnlyList<int> ParseComponents(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (IsSnapshot(text)) return Array.Empty<int>();

        var result = new List<int>();
        foreach (var rawPart in text.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(rawPart.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0)
            {
                // Pl. "1.21.4-pre1" → az utolsó komponensnél a "-" utáni rész nem szám.
                if (rawPart.Contains('-')) break;
                return Array.Empty<int>();
            }

            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return Array.Empty<int>();
            result.Add(parsed);

            if (digits.Length != rawPart.Length && !rawPart.Contains('-')) break;
            if (rawPart.Contains('-')) break;
        }

        return result;
    }

    /// <summary>Igaz, ha a verzió numerikusan összehasonlítható (nem pillanatkép, van számainak).</summary>
    public static bool IsComparable(string? version) => ParseComponents(version).Count > 0;

    /// <summary>
    /// Numerikus összehasonlítás. <c>null</c>, ha valamelyik verzió nem értelmezhető
    /// (pillanatkép, üres, szöveges azonosító). Azonos számoknál a kiadás-előtti utótag
    /// dönt (semver: <c>1.21.4-pre1 &lt; 1.21.4</c>).
    /// </summary>
    public static int? TryCompare(string? left, string? right)
    {
        var a = ParseComponents(left);
        var b = ParseComponents(right);
        if (a.Count == 0 || b.Count == 0) return null;

        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var leftValue = i < a.Count ? a[i] : 0;
            var rightValue = i < b.Count ? b[i] : 0;
            if (leftValue != rightValue) return leftValue < rightValue ? -1 : 1;
        }

        return CompareSuffixes(ExtractSuffix(left), ExtractSuffix(right));
    }

    /// <summary>
    /// A számok utáni utótag (pl. <c>-pre1</c>, <c>-beta.2</c>). Kiadásnál üres, és a
    /// semver szerint a kiadás az utótagos verziónál mindig újabb.
    /// </summary>
    public static string ExtractSuffix(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var index = text.IndexOf('-');
        if (index < 0) index = text.IndexOf('+');
        return index < 0 ? "" : text[(index + 1)..].Trim();
    }

    private static int CompareSuffixes(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;   // kiadás > előzetes
        if (right.Length == 0) return -1;
        var comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return comparison == 0 ? 0 : comparison < 0 ? -1 : 1;
    }

    /// <summary>
    /// Igaz, ha a két verzió ugyanaz (pl. <c>26.2</c> és <c>26.2.0</c>, vagy két azonos
    /// pillanatkép, amit numerikusan nem lehet összehasonlítani).
    /// </summary>
    public static bool IsSame(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return TryCompare(left, right) == 0;
    }

    /// <summary>
    /// "26.x" sémájú (2026 utáni) verzió. A launcher ezeknél Java 25-öt és nagyobb
    /// alap RAM-ot használ.
    /// </summary>
    public static bool IsNewScheme(string? version)
    {
        var components = ParseComponents(version);
        return components.Count > 0 && components[0] >= 26;
    }

    /// <summary>A verzió "családja" (major.minor), pl. <c>1.21.11</c> → <c>1.21</c>, <c>26.2</c> → <c>26.2</c>.</summary>
    public static string Family(string? version)
    {
        var components = ParseComponents(version);
        if (components.Count == 0) return (version ?? "").Trim();
        if (components.Count == 1) return components[0].ToString(CultureInfo.InvariantCulture);
        return $"{components[0]}.{components[1]}";
    }
}
