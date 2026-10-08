using System.Text;

namespace TurulMC.Core.Recovery;

public sealed record CrashExplanation(string Code, string Title, string Summary, string[] Steps,
    string Evidence, bool Recognized, int? ExitCode);

/// <summary>Offline rules based on diagnostic evidence; exit code 1 alone is never a diagnosis.</summary>
public static class CrashExplanationService
{
    private const int MaxCharacters = 256 * 1024;

    public static CrashExplanation Analyze(string? text, int? exitCode = null)
    {
        text ??= "";
        if (text.Length > MaxCharacters) text = text[^MaxCharacters..];
        var lower = text.ToLowerInvariant();
        bool Has(params string[] patterns) => patterns.Any(lower.Contains);
        CrashExplanation Explain(string code, string title, string summary, string[] steps, params string[] evidencePatterns)
        {
            var line = text.Split('\n').FirstOrDefault(l => evidencePatterns.Any(p => l.Contains(p, StringComparison.OrdinalIgnoreCase)))?.Trim() ?? "";
            if (line.Length > 400) line = line[..400] + "…";
            return new(code, title, summary, steps, line, true, exitCode);
        }

        if (Has("outofmemoryerror", "java heap space", "gc overhead limit exceeded"))
            return Explain("memory", "Elfogyott a Minecraft memóriája",
                "A Java naplója memóriahiányt jelzett. Nagy modpack vagy túl magas grafikai beállítás is okozhatja.",
                ["Zárd be a felesleges programokat.", "Ellenőrizd az Instance RAM-beállítását; hagyj memóriát a Windowsnak is.", "Próbálj kisebb látótávolságot vagy kevesebb modot."],
                "OutOfMemoryError", "Java heap space", "GC overhead limit");
        if (Has("could not reserve enough space", "native memory allocation (malloc) failed", "there is insufficient memory for the java runtime"))
            return Explain("memory-reservation", "A Java nem tudta lefoglalni a memóriát",
                "A kért memóriamennyiség nem foglalható le. A további RAM-emelés itt ronthat a helyzeten.",
                ["Csökkentsd az Instance számára beállított RAM-ot.", "Zárj be más alkalmazásokat, majd indítsd újra a játékot."],
                "Could not reserve", "Native memory allocation", "insufficient memory");
        if (Has("unsupportedclassversionerror", "compiled by a more recent version of the java runtime", "requires java", "incompatible java version"))
            return Explain("java-version", "Nem megfelelő Java-verzió",
                "A játék vagy valamelyik mod más Java-verziót igényel, mint amellyel most elindult.",
                ["Az indítási beállításokban válassz automatikus Java-felismerést.", "Ellenőrizd a naplóban jelzett Java-követelményt."],
                "UnsupportedClassVersionError", "more recent version", "requires Java", "incompatible Java version");
        if (Has("duplicate mods", "duplicate mod", "found duplicate", "duplicate versions for mod"))
            return Explain("duplicate-mod", "Egy mod többször van telepítve",
                "A loader duplán telepített modot talált. Gyakran két különböző verzió JAR-ja maradt a mods mappában.",
                ["A Telepített modok listájában kapcsold ki a felesleges példányt.", "Ha frissítés után történt, használhatod a mod-visszaállítási pontot."],
                "duplicate mods", "duplicate mod", "found duplicate", "duplicate versions");
        if (Has("which is missing", "requires fabric-api", "requires any version of", "missing mandatory dependencies", "missing required mod", "mod resolution encountered an incompatible mod set"))
            return Explain("mod-dependency", "Hiányzó vagy nem megfelelő modfüggőség",
                "A loader egy szükséges mod vagy annak megfelelő verziójának hiányát jelezte. A naplórészlet segít azonosítani.",
                ["A Modok oldalon keresd meg a naplóban kért függőséget a kiválasztott Minecraft-verzióhoz.", "Ha egy frissítés után jelentkezett, állítsd vissza a módosítás előtti modmentést."],
                "which is missing", "requires fabric-api", "requires any version", "missing mandatory", "missing required", "incompatible mod set");
        if (Has("incompatible mods found", "incompatible mod set", "requires version", "requires minecraft", "depends on minecraft"))
            return Explain("mod-version", "A modok verziói nem illeszkednek",
                "A loader verzióütközést jelzett a Minecraft, a loader vagy a modok között.",
                ["Ellenőrizd az aktív Instance Minecraft-verzióját és loaderét.", "A naplóban megnevezett modból a kompatibilis verziót telepítsd, vagy állítsd vissza a modmentést."],
                "incompatible mods", "incompatible mod set", "requires version", "requires minecraft", "depends on minecraft");
        if (Has("glfw error 65542", "glfw error 65543", "does not support opengl", "failed to create window", "wgl: the driver"))
            return Explain("graphics", "Grafikus inicializálási hiba",
                "A játék nem tudta létrehozni az OpenGL-ablakot. A grafikus driver vagy a GPU támogatása lehet az ok.",
                ["Ellenőrizd, hogy a videokártyád támogatja-e ezt a Minecraft-verziót.", "A videokártya gyártójának megfelelő driverét használd.", "Próbáld shader és grafikai modok nélkül."],
                "GLFW error", "does not support OpenGL", "Failed to create window", "WGL: the driver");
        if (Has("zipexception", "zip end header not found", "invalid or corrupt jarfile"))
            return Explain("corrupt-file", "Sérült JAR vagy ZIP fájl",
                "A Java egy archívumot nem tudott beolvasni. Megszakadt letöltés is okozhatja.",
                ["A naplóban megnevezett modot töltsd le újra.", "Ha Minecraft-fájl sérült, futtasd a Smart Repair funkciót."],
                "ZipException", "zip END header", "Invalid or corrupt jarfile");
        if (Has("mixinapplyerror", "mixintransformererror", "invalidmixinexception", "mixin apply failed"))
            return Explain("mixin", "Valószínű modütközés",
                "A napló Mixin-hibát tartalmaz. Ez utalhat inkompatibilis modokra, de önmagában nem bizonyítja, melyik mod a hibás.",
                ["Próbáld visszaállítani a legutóbbi módosítás előtti modmentést.", "Ellenőrizd a frissen telepített modok kompatibilitását.", "Ha megmarad a hiba, készíts Support ZIP-et a teljes naplóhoz."],
                "MixinApplyError", "MixinTransformerError", "InvalidMixinException", "Mixin apply failed");
        var availableEvidence = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";
        if (availableEvidence.Length > 400) availableEvidence = availableEvidence[..400] + "…";
        return new("unknown", "A leállás oka nem azonosítható biztosan",
            "A rendelkezésre álló napló nem tartalmaz egyértelműen felismert hibát. A kilépési kód önmagában nem mondja meg az okot.",
            ["Ha modmódosítás után kezdődött, próbáld a módosítás előtti visszaállítási pontot.", "A Support ZIP segítségével a teljes napló is megvizsgálható."], availableEvidence, false, exitCode);
    }

    public static string ReadSessionLogs(string gameDirectory, DateTime sessionStartedUtc)
    {
        var parts = new List<string>();
        var candidates = new List<string> { Path.Combine(gameDirectory, "logs", "latest.log") };
        try
        {
            var crashes = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashes))
                candidates.AddRange(Directory.EnumerateFiles(crashes, "*.txt")
                    .Where(p => File.GetLastWriteTimeUtc(p) >= sessionStartedUtc.AddSeconds(-2))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
            foreach (var path in candidates)
            {
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < sessionStartedUtc.AddSeconds(-2)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 128 * 1024) stream.Seek(-128 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                parts.Add(reader.ReadToEnd());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return string.Join("\n", parts);
    }
}
