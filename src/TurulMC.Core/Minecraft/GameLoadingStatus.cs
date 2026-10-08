namespace TurulMC.Core.Minecraft;

/// <summary>
/// Barátságos magyar státusz a Minecraft processz stdout/stderr soraiból.
/// Sok mod = perces betöltés; a launcher ezalatt ezt mutatja a betöltőablakban.
/// </summary>
public static class GameLoadingStatus
{
    private static readonly (string Fragment, string Text)[] Rules =
    {
        ("setting user", "Profil beállítása…"),
        ("starting minecraft", "Minecraft indítása…"),
        ("lwjgl version", "Grafikus motor (LWJGL) betöltése…"),
        ("backend library", "Grafikus backend betöltése…"),
        ("opengl", "OpenGL inicializálása…"),
        ("sound engine", "Hangrendszer betöltése…"),
        ("reloading resourcemanager", "Erőforrások újratöltése…"),
        ("finished reloading", "Erőforrások kész…"),
        ("building optimized datafixer", "Adatjavító építése (elsőre lassú)…"),
        ("environment[client]", "Kliens környezet indul…"),
        ("loading minecraft", "Minecraft betöltése…"),
        ("loading ", "Modok betöltése…"),
        ("fabric loader", "Fabric Loader indul…"),
        ("mapping resolver", "Mod-leképezések feloldása…"),
        ("entrypoint", "Mod-belpési pontok indítása…"),
        ("mixin", "Mixin-ek alkalmazása…"),
        ("created:", "Textúrák készítése…"),
        ("atlas", "Textúra-atlasz építése…"),
        ("narrator", "Narrátor betöltése…"),
        ("bootstrapping", "Rendszer indítása…"),
        ("starting integrated server", "Belső szerver indítása…"),
        ("preparing start region", "Világ előkészítése…"),
    };

    /// <summary>Érdekes sor → rövid státusz, vagy null ha nem mutatnivaló.</summary>
    public static string? MapLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var lower = line.ToLowerInvariant();
        if (lower.Length > 300) lower = lower[^300..];
        foreach (var (fragment, text) in Rules)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                return text;
        }
        return null;
    }

    /// <summary>Nyers sor rövidítve a log-nézetbe (max ~160 karakter).</summary>
    public static string TrimForLog(string line, int max = 160)
    {
        line = (line ?? "").Trim();
        if (line.Length <= max) return line;
        return "…" + line[^max..];
    }
}
