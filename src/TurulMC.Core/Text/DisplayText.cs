using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TurulMC.Core.Text;

/// <summary>
/// Felhasználónak mutatott szövegek tisztítása. Három helyen szivárgott
/// formátum-jelölés a felületre („furán írja ki a szöveget + JSON-t is kiír"):
/// <list type="bullet">
/// <item>Minecraft chat-komponens (<c>pack.mcmeta</c> description, MOTD):
/// nyers <c>{"text":"..."}</c> JSON jelent meg a leírás helyett,</item>
/// <item>Modrinth-leírások: HTML-entitások (<c>&amp;#39;</c>) és markdown
/// (<c>**félkövér**</c>, linkek) nyersen látszottak,</item>
/// <item>játéknapló: <c>§a</c>-típusú színkódok a sorokban.</item>
/// </list>
/// </summary>
public static partial class DisplayText
{
    /// <summary>Modrinth-leírás olvashatóvá tétele (entitás + markdown + térköz).</summary>
    public static string CleanModrinth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Nincs leírás.";
        var decoded = WebUtility.HtmlDecode(text);
        var stripped = StripMarkdown(decoded);
        // Többszörös sortörés/szóköz összevonása: a kártyán 2 sor fér el.
        stripped = Regex.Replace(stripped, @"[ \t]+", " ");
        stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
        return stripped.Trim();
    }

    /// <summary>Minecraft chat-komponens (string / objektum / extra-tömb)
    /// sima szöveggé lapítása. Ismeretlen alakra "" (sose nyers JSON).</summary>
    public static string FlattenChatComponent(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.ToString();
            case JsonValueKind.Object:
            {
                var sb = new System.Text.StringBuilder();
                if (element.TryGetProperty("text", out var text))
                    sb.Append(FlattenChatComponent(text));
                if (element.TryGetProperty("extra", out var extra) &&
                    extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in extra.EnumerateArray())
                        sb.Append(FlattenChatComponent(item));
                }
                // "translate" kulcsot nem fejtünk: az nyelvi kulcs, nem szöveg.
                return sb.ToString();
            }
            case JsonValueKind.Array:
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in element.EnumerateArray())
                    sb.Append(FlattenChatComponent(item));
                return sb.ToString();
            }
            default:
                return "";
        }
    }

    /// <summary><c>§</c> szín-/formátumkódok vágása (játéknapló, MOTD).</summary>
    public static string StripSectionCodes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return SectionCodePattern().Replace(text, "");
    }

    internal static string StripMarkdown(string text)
    {
        // Képek ![alt](url) → alt ; linkek [szöveg](url) → szöveg.
        var s = MarkdownImagePattern().Replace(text, "$1");
        s = MarkdownLinkPattern().Replace(s, "$1");
        // Félkövér/dőlt/áthúzott: **x**, __x__, *x*, _x_, ~~x~~ → x.
        // (A tartalom a 2. csoport — az 1. a határolójel.)
        s = MarkdownBoldPattern().Replace(s, "$2");
        s = MarkdownItalicPattern().Replace(s, "$2");
        // Kód: `x` és ```blokk``` → x (a blokk tartalmát egy sorba).
        s = MarkdownCodeBlockPattern().Replace(s, m =>
            m.Groups[1].Value.Replace('\n', ' '));
        s = MarkdownInlineCodePattern().Replace(s, "$1");
        // Címsorok (# x), idézetek (> x), listajelek (- / * / 1. ) sor elején.
        s = Regex.Replace(s, @"(?m)^\s{0,3}#{1,6}\s+", "");
        s = Regex.Replace(s, @"(?m)^\s{0,3}>\s?", "");
        s = Regex.Replace(s, @"(?m)^\s{0,3}(?:[-*+]|\d+[.)])\s+", "• ");
        return s;
    }

    [GeneratedRegex("§[0-9a-fk-orA-FK-OR]")]
    private static partial Regex SectionCodePattern();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownImagePattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex MarkdownBoldPattern();

    [GeneratedRegex(@"(?<!\w)([*_])(.+?)\1(?!\w)")]
    private static partial Regex MarkdownItalicPattern();

    [GeneratedRegex(@"```(?:\w*\n)?([\s\S]*?)```")]
    private static partial Regex MarkdownCodeBlockPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex MarkdownInlineCodePattern();
}
