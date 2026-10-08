using System.Globalization;

namespace TurulMC.Core.Mods;

/// <summary>Egy verzió-tartomány illeszkedésének eredménye.</summary>
public enum RangeMatch
{
    /// <summary>A verzió biztosan illeszkedik.</summary>
    Yes,

    /// <summary>A verzió biztosan nem illeszkedik.</summary>
    No,

    /// <summary>Nem eldönthető (ismeretlen tartomány-formátum vagy pillanatkép verzió).</summary>
    Unknown
}

/// <summary>
/// A <c>fabric.mod.json</c> <c>depends</c> mezőiben használt verzió-tartományok
/// (npm/semver stílus) kiértékelése: <c>*</c>, pontos verzió, <c>&gt;=</c>, <c>&gt;</c>,
/// <c>&lt;=</c>, <c>&lt;</c>, <c>~</c>, <c>^</c>, <c>1.21.x</c>, szóközzel elválasztott
/// ÉS kapcsolat, <c>||</c> VAGY kapcsolat.
/// </summary>
public static class FabricVersionRange
{
    /// <summary>
    /// Kiértékeli, hogy <paramref name="version"/> megfelel-e a <paramref name="range"/>
    /// tartománynak. Üres vagy <c>*</c> tartomány mindig illeszkedik; az értelmezhetetlen
    /// tartomány és a nem összehasonlítható verzió eredménye <see cref="RangeMatch.Unknown"/>,
    /// hogy soha ne jelentsünk hamis inkompatibilitást.
    /// </summary>
    public static RangeMatch Satisfies(string? range, string? version)
    {
        var text = NormalizeSeparators(range ?? "").Trim();
        if (text.Length == 0 || text is "*" or "x" or "X" or "any") return RangeMatch.Yes;

        var sawUnknown = false;
        var sawAlternative = false;
        foreach (var alternative in text.Split("||", StringSplitOptions.None))
        {
            var tokens = Tokenize(alternative);
            if (tokens.Length == 0) continue;
            sawAlternative = true;

            var result = EvaluateAll(tokens, version);
            if (result == RangeMatch.Yes) return RangeMatch.Yes;
            if (result == RangeMatch.Unknown) sawUnknown = true;
        }

        // Ha egyetlen értelmezhető összehasonlító sem volt (pl. "||" vagy "| |"), akkor a
        // tartomány értelmezhetetlen — nem pedig "nem illeszkedik".
        if (!sawAlternative) return RangeMatch.Unknown;
        return sawUnknown ? RangeMatch.Unknown : RangeMatch.No;
    }

    private static RangeMatch EvaluateAll(string[] rawTokens, string? version)
    {
        var tokens = ExpandHyphenRange(rawTokens);
        if (tokens.Count == 0) return RangeMatch.Unknown;

        var sawUnknown = false;
        var evaluated = 0;
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;

            // Írásjelből álló "token" (pl. a ">=1.0 - 2.0" hibás alak kötőjele): a tartomány
            // értelmezhetetlen — ilyenkor nem mondjuk, hogy "nem illeszkedik".
            if (token.All(ch => !char.IsLetterOrDigit(ch))) return RangeMatch.Unknown;

            evaluated++;
            var result = EvaluateToken(token, version);
            if (result == RangeMatch.No) return RangeMatch.No;
            if (result == RangeMatch.Unknown) sawUnknown = true;
        }

        if (evaluated == 0) return RangeMatch.Unknown;
        return sawUnknown ? RangeMatch.Unknown : RangeMatch.Yes;
    }

    /// <summary>
    /// "1.20.1 - 1.21.4" (npm hyphen range) átalakítása "&gt;=1.20.1 &lt;=1.21.4" alakba.
    /// Csak akkor, ha a két oldal operátor nélküli verzió — így a "&gt;=1.0 - 2.0" hibás
    /// írásmód nem lesz belőle hamis egyezés.
    /// </summary>
    private static List<string> ExpandHyphenRange(string[] tokens)
    {
        var result = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "-" && result.Count > 0 && i + 1 < tokens.Length)
            {
                var left = result[^1];
                var right = tokens[i + 1];
                if (IsPlainVersion(left) && IsPlainVersion(right))
                {
                    result.RemoveAt(result.Count - 1);
                    result.Add(">=" + left);
                    result.Add("<=" + right);
                    i++;
                    continue;
                }
            }

            result.Add(tokens[i]);
        }

        return result;
    }

    private static bool IsPlainVersion(string token)
        => token.Length > 0 && token[0] != '<' && token[0] != '>' && token[0] != '=' &&
           token[0] != '~' && token[0] != '^' && token != "-" && token != "||";

    private static string[] Tokenize(string alternative)
    {
        var raw = NormalizeSeparators(alternative)
            .Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token != "|")
            .ToArray();

        // ">= 1.20.1": az operátor és az érték külön tokenre esett — összevonjuk.
        var merged = new List<string>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] is ">=" or "<=" or ">" or "<" or "=" or "~" or "^" && i + 1 < raw.Length)
            {
                merged.Add(raw[i] + raw[i + 1]);
                i++;
                continue;
            }

            // "1.20.1-1.21.4" (szóköz nélküli hyphen range): ">=1.20.1 <=1.21.4".
            var single = raw[i];
            var dash = single.IndexOf('-');
            if (dash > 0 && dash < single.Length - 1 &&
                char.IsDigit(single[0]) && char.IsDigit(single[dash + 1]))
            {
                merged.Add(">=" + single[..dash]);
                merged.Add("<=" + single[(dash + 1)..]);
                continue;
            }

            merged.Add(single);
        }

        return merged.ToArray();
    }

    /// <summary>
    /// Egységesíti az elválasztókat: a nem törhető szóköz (U+00A0) és társai sima szóközzé
    /// alakulnak, és a szám után közvetlenül álló operátor elé szóköz kerül — így a
    /// "&gt;=1.21&lt;1.22" alak felső korlátja nem veszik el csendben.
    /// </summary>
    private static string NormalizeSeparators(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            var current = ch switch
            {
                '\u00A0' or '\u2007' or '\u202F' or '\u200B' => ' ',
                _ => ch
            };

            if (current is '<' or '>' or '=' or '~' or '^' or ',')
            {
                var previous = builder.Length > 0 ? builder[^1] : ' ';
                if (previous is not (' ' or '<' or '>' or '=' or '|' or ','))
                    builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static RangeMatch EvaluateToken(string token, string? version)
    {
        var value = token.Trim();
        if (value.Length == 0) return RangeMatch.Yes;
        if (value is "*" or "x" or "X") return RangeMatch.Yes;

        // Csupasz írásjel (pl. a ">=1.0 - 2.0" hibás alak kötőjele): a tartomány
        // értelmezhetetlen, nem pedig "nem illeszkedik".
        if (value.All(ch => !char.IsLetterOrDigit(ch))) return RangeMatch.Unknown;

        if (value.StartsWith(">=", StringComparison.Ordinal))
            return Compare(value[2..], version, comparison => comparison >= 0);
        if (value.StartsWith("<=", StringComparison.Ordinal))
            return Compare(value[2..], version, comparison => comparison <= 0);
        if (value.StartsWith(">", StringComparison.Ordinal))
            return Compare(value[1..], version, comparison => comparison > 0);
        if (value.StartsWith("<", StringComparison.Ordinal))
            return Compare(value[1..], version, comparison => comparison < 0);
        if (value.StartsWith("=", StringComparison.Ordinal))
            return Exact(value[1..], version, allowPrefix: false);
        if (value.StartsWith("~", StringComparison.Ordinal))
            return Tilde(value[1..], version);
        if (value.StartsWith("^", StringComparison.Ordinal))
            return Caret(value[1..], version);

        return Exact(value, version, allowPrefix: true);
    }

    private static RangeMatch Compare(string bound, string? version, Func<int, bool> accepts)
    {
        var normalized = NormalizeBound(bound);
        if (normalized.Length == 0) return RangeMatch.Unknown;

        // A ">=1.21.9-" alakú felső korlát a nyitott pre-release jelölés; a "-" levágása után
        // a numerikus összehasonlítás a helyes eredményt adja.
        var comparison = MinecraftVersionOrder.TryCompare(version, normalized);
        return comparison is null ? RangeMatch.Unknown : accepts(comparison.Value) ? RangeMatch.Yes : RangeMatch.No;
    }

    private static RangeMatch Tilde(string bound, string? version)
    {
        var normalized = NormalizeBound(bound);
        var components = MinecraftVersionOrder.ParseComponents(normalized);
        if (components.Count == 0) return RangeMatch.Unknown;

        // ~1.21.4 → >=1.21.4 <1.22.0 ; ~1.21 → >=1.21.0 <1.22.0 ; ~1 → >=1.0.0 <2.0.0
        var lower = MinecraftVersionOrder.TryCompare(version, normalized);
        if (lower is null) return RangeMatch.Unknown;
        if (lower < 0) return RangeMatch.No;

        var upperMajor = components[0];
        var upperMinor = components.Count >= 3 ? components[1] + 1 : components.Count == 2 ? components[1] + 1 : 0;
        var upper = components.Count >= 2 ? $"{upperMajor}.{upperMinor}.0" : $"{upperMajor + 1}.0.0";
        var upperComparison = MinecraftVersionOrder.TryCompare(version, upper);
        if (upperComparison is null) return RangeMatch.Unknown;
        return upperComparison < 0 ? RangeMatch.Yes : RangeMatch.No;
    }

    private static RangeMatch Caret(string bound, string? version)
    {
        var normalized = NormalizeBound(bound);
        var components = MinecraftVersionOrder.ParseComponents(normalized);
        if (components.Count == 0) return RangeMatch.Unknown;

        // ^1.21.4 → >=1.21.4 <2.0.0 (a major 0 esetén <0.(minor+1).0)
        var lower = MinecraftVersionOrder.TryCompare(version, normalized);
        if (lower is null) return RangeMatch.Unknown;
        if (lower < 0) return RangeMatch.No;

        var upper = components[0] == 0 && components.Count >= 2
            ? $"0.{components[1] + 1}.0"
            : $"{components[0] + 1}.0.0";
        var upperComparison = MinecraftVersionOrder.TryCompare(version, upper);
        if (upperComparison is null) return RangeMatch.Unknown;
        return upperComparison < 0 ? RangeMatch.Yes : RangeMatch.No;
    }

    private static RangeMatch Exact(string bound, string? version, bool allowPrefix)
    {
        var normalized = NormalizeBound(bound);
        if (normalized.Length == 0) return RangeMatch.Unknown;

        // Helyettesítő karakter: 1.21.x / 1.21.* → a "család" egyezése.
        if (normalized.EndsWith(".x", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".*", StringComparison.Ordinal))
        {
            var family = normalized[..^2];
            return Prefix(family, version);
        }

        var target = MinecraftVersionOrder.ParseComponents(version);
        var expected = MinecraftVersionOrder.ParseComponents(normalized);
        if (target.Count == 0 || expected.Count == 0) return RangeMatch.Unknown;

        // A rövidebb, de teljes "1.21" alakot a modok többsége verziócsaládként írja le
        // (a Fabric ezt pontosan értelmezné), ezért prefix-egyezésnek vesszük — így nem
        // jelzünk hamis inkompatibilitást egy 1.21.4-es Instance-nál.
        if (allowPrefix && expected.Count < 3)
            return Prefix(normalized, version);

        var comparison = MinecraftVersionOrder.TryCompare(version, normalized);
        return comparison is null ? RangeMatch.Unknown : comparison == 0 ? RangeMatch.Yes : RangeMatch.No;
    }

    private static RangeMatch Prefix(string family, string? version)
    {
        var expected = MinecraftVersionOrder.ParseComponents(family);
        var target = MinecraftVersionOrder.ParseComponents(version);
        if (expected.Count == 0 || target.Count == 0) return RangeMatch.Unknown;
        if (target.Count < expected.Count) return RangeMatch.No;

        for (var i = 0; i < expected.Count; i++)
        {
            if (target[i] != expected[i]) return RangeMatch.No;
        }

        return RangeMatch.Yes;
    }

    private static string NormalizeBound(string bound)
        => (bound ?? "").Trim().TrimEnd('-').TrimEnd('+').Trim();
}
