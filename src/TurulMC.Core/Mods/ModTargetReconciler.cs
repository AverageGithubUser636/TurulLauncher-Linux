namespace TurulMC.Core.Mods;

/// <summary>Egy mod JAR a cél Instance-ban (mod-azonosító, verzió és kompatibilitás a célverzióval).</summary>
public sealed record ModTargetFile(
    string FileName,
    string ModId,
    string Version,
    ModCompatibilityStatus Status = ModCompatibilityStatus.Compatible);

/// <summary>Egy cél-Instance mod-készletének vizsgálata egy telepítendő modhoz képest.</summary>
public sealed record ModTargetInspection
{
    /// <summary>Igaz, ha a cél Instance-ban már van ÚJABB, a célverzióval működő verzió ebből a modból.</summary>
    public bool HasNewerVersion { get; init; }

    /// <summary>A cél Instance-ban talált legújabb, a célverzióval kompatibilis verzió.</summary>
    public string NewerVersion { get; init; } = "";

    public string NewerFileName { get; init; } = "";

    /// <summary>Ugyanahhoz a mod-azonosítóhoz tartozó, lecserélendő fájlok a cél Instance-ban.</summary>
    public IReadOnlyList<string> SameModFiles { get; init; } = Array.Empty<string>();

    public string ReasonHu { get; init; } = "";
}

/// <summary>
/// A cél Instance mod-mappájának összevetése egy telepítendő moddal. Ez zárja ki, hogy egy
/// másolás/frissítés során (a) a cél Instance-ban lévő, a célverzióval működő ÚJABB verziót
/// felülírjuk egy régebbivel, vagy (b) ugyanaz a mod két fájlnévvel egyszerre maradjon a
/// mappában (duplikált mod id = biztos összeomlás).
/// </summary>
/// <remarks>
/// A „ne lépj vissza" szabály szándékosan csak akkor tilt, ha a célban lévő újabb példány a
/// cél-Minecraft-verzióval <b>kompatibilis</b>. Ha a célban csak egy másik Minecraft-verzióhoz
/// készült (szám szerint újabb) példány van, a csere kívánatos — pont ez a másolás
/// „frissítem a modokat a célverzióra" stratégiájának a lényege.
/// </remarks>
public static class ModTargetReconciler
{
    /// <summary>
    /// A mod-mappa egyszeri beolvasása (mod-azonosító, verzió és célverzió-kompatibilitás).
    /// Több mod telepítésekor ezt érdemes egyszer megépíteni, mert minden JAR megnyitása drága.
    /// </summary>
    public static IReadOnlyList<ModTargetFile> BuildIndex(
        string? modsDirectory,
        string? targetMinecraftVersion = null,
        string? loader = null,
        string? loaderVersion = null)
    {
        var index = new List<ModTargetFile>();
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory)) return index;

        var canEvaluate = !string.IsNullOrWhiteSpace(targetMinecraftVersion);
        try
        {
            foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var enabled = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (canEvaluate)
                {
                    var entry = ModCompatibilityScanner.EvaluateJar(
                        path, name, enabled, targetMinecraftVersion!, loader ?? "none", loaderVersion);
                    if (string.IsNullOrWhiteSpace(entry.ModId)) continue;
                    index.Add(new ModTargetFile(name, entry.ModId, entry.Version, entry.Status));
                    continue;
                }

                var info = ModFileReader.TryRead(path, name, out _);
                if (info is null || string.IsNullOrWhiteSpace(info.Id)) continue;
                index.Add(new ModTargetFile(name, info.Id, info.Version));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Olvashatatlan mappa: üres index (a hívó így nem jelöl semmit cserére).
        }

        return index;
    }

    public static ModTargetInspection Inspect(
        string? modsDirectory,
        string? modId,
        string? newVersionNumber,
        string? excludeFileName = null,
        string? targetMinecraftVersion = null,
        string? loader = null,
        string? loaderVersion = null)
        => Inspect(BuildIndex(modsDirectory, targetMinecraftVersion, loader, loaderVersion),
            modId, newVersionNumber, excludeFileName);

    /// <summary>Egy már beolvasott index alapján végzi el ugyanazt a vizsgálatot.</summary>
    public static ModTargetInspection Inspect(
        IReadOnlyList<ModTargetFile>? index,
        string? modId,
        string? newVersionNumber,
        string? excludeFileName = null)
    {
        if (index is null || index.Count == 0 || string.IsNullOrWhiteSpace(modId))
            return new ModTargetInspection();

        var all = new List<ModTargetFile>();
        foreach (var file in index)
        {
            if (!file.ModId.Equals(modId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(excludeFileName) &&
                file.FileName.Equals(excludeFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            all.Add(file);
        }

        if (all.Count == 0) return new ModTargetInspection();

        // Csak azok a példányok számítanak „megtartandónak", amelyek a cél-Minecraft-verzióval
        // kompatibilisek. A kompatibilitást nem ismerő (Unknown) állapotot nem tekintjük
        // blokkolónak, mert ilyenkor nem tudjuk, hogy a fájl egyáltalán működik-e.
        var usable = all.Where(x => x.Status == ModCompatibilityStatus.Compatible).ToList();
        var pool = usable.Count > 0 ? usable : new List<ModTargetFile>();

        var newestVersion = "";
        var newestFile = "";
        foreach (var file in pool)
        {
            if (newestVersion.Length == 0 || ModVersionOrder.IsNewer(file.Version, newestVersion))
            {
                newestVersion = file.Version;
                newestFile = file.FileName;
            }
        }

        var newer = newestVersion.Length > 0 && ModVersionOrder.IsOlder(newVersionNumber, newestVersion);

        return new ModTargetInspection
        {
            HasNewerVersion = newer,
            NewerVersion = newestVersion,
            NewerFileName = newestFile,
            SameModFiles = all.Select(x => x.FileName).ToList(),
            ReasonHu = newer
                ? $"A cél Instance-ban már újabb, a célverzióval működő verzió van ebből a modból ({newestVersion}, {newestFile}) — a régebbi verzió nem kerül a helyére."
                : all.Count > 1 || (pool.Count == 0 && all.Count > 0)
                    ? $"A cél Instance-ban ugyanez a mod más fájlnévvel is megtalálható: {string.Join(", ", all.Select(x => x.FileName))}. " +
                      "A duplikált példány biztonsági mentésbe kerül, különben a játék összeomlana."
                    : ""
        };
    }
}
