namespace TurulMC.Core.Mods;

/// <summary>Egy mod kompatibilitási állapota az Instance-hez képest.</summary>
public enum ModCompatibilityStatus
{
    /// <summary>Rendben: a mod támogatja az Instance Minecraft-verzióját és a loadert.</summary>
    Compatible,

    /// <summary>A mod nem támogatja az Instance Minecraft-verzióját.</summary>
    IncompatibleGame,

    /// <summary>A mod újabb Fabric Loadert igényel, mint a telepített.</summary>
    IncompatibleLoader,

    /// <summary>Nem Fabric mod (Forge/NeoForge/quilt), így Fabric Instance-ban nem indul el.</summary>
    NotFabric,

    /// <summary>A JAR-ból nem olvasható ki a leíró (sérült, üres vagy ismeretlen formátum).</summary>
    UnknownMetadata,

    /// <summary>Ugyanaz a mod kétszer szerepel (több verzió egyszerre).</summary>
    Duplicate
}

/// <summary>Egy mod fájl vizsgálatának eredménye.</summary>
public sealed record ModScanEntry
{
    public required string FileName { get; init; }
    public string Title { get; init; } = "";
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Enabled { get; init; }
    public ModCompatibilityStatus Status { get; init; } = ModCompatibilityStatus.Compatible;
    public string ReasonHu { get; init; } = "";
    public string MinecraftRange { get; init; } = "";
    public string LoaderRange { get; init; } = "";

    public bool IsBlocking => Status != ModCompatibilityStatus.Compatible;
}

/// <summary>A teljes mod-mappa vizsgálatának eredménye.</summary>
public sealed record ModScanReport
{
    public int Total { get; init; }
    public int Enabled { get; init; }
    public int Disabled { get; init; }
    public int Compatible { get; init; }
    public int Incompatible { get; init; }
    public int Unknown { get; init; }
    public int Duplicates { get; init; }
    public IReadOnlyList<ModScanEntry> Entries { get; init; } = Array.Empty<ModScanEntry>();

    /// <summary>Csak az aktív (nem letiltott) és problémás modok.</summary>
    public IReadOnlyList<ModScanEntry> Problems { get; init; } = Array.Empty<ModScanEntry>();

    /// <summary>Nem blokkoló megjegyzések (pl. loader nélküli Instance).</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    public string MinecrafVersion { get; init; } = "";
    public string Loader { get; init; } = "";

    public string SummaryHu
    {
        get
        {
            if (Total == 0) return "Nincs mod a mods mappában.";
            var parts = new List<string> { $"{Total} mod" };
            parts.Add($"{Compatible} rendben");
            if (Incompatible > 0) parts.Add($"{Incompatible} inkompatibilis");
            if (Unknown > 0) parts.Add($"{Unknown} nem ellenőrizhető");
            if (Duplicates > 0) parts.Add($"{Duplicates} duplikált");
            if (Disabled > 0) parts.Add($"{Disabled} letiltva");
            return string.Join(", ", parts) + $" (Minecraft {MinecrafVersion})";
        }
    }
}

/// <summary>
/// Offline mod-kompatibilitás vizsgálat: a mod JAR-ok <c>fabric.mod.json</c> leírója
/// alapján eldönti, hogy a mod támogatja-e az Instance Minecraft-verzióját és a
/// telepített Fabric Loadert. Hálózat nélkül is működik, ezért a Smart Repair és az
/// Instance-másolás is ezt használja.
/// </summary>
public static class ModCompatibilityScanner
{
    /// <summary>Ennél több JAR-t nem vizsgálunk egy mappában (időkorlát).</summary>
    public const int MaxMods = 2000;

    /// <summary>Üres vagy hiányzó loader érték egységesen "none" (vanilla).</summary>
    private static string NormalizeLoader(string? loader)
        => string.IsNullOrWhiteSpace(loader) ? "none" : loader.Trim().ToLowerInvariant();

    public static ModScanReport Scan(
        string modsDirectory,
        string minecraftVersion,
        string loader,
        string? loaderVersion = null)
    {
        var notices = new List<string>();
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return new ModScanReport
            {
                MinecrafVersion = minecraftVersion ?? "",
                Loader = NormalizeLoader(loader),
                Notices = notices
            };
        }

        var normalizedLoader = NormalizeLoader(loader);
        var files = new List<(string Path, string FileName, bool Enabled)>();
        var truncated = false;
        try
        {
            // Determinisztikus sorrend: a cap miatt ne az enumerálás sorrendje döntse el,
            // melyik modok kerülnek vizsgálatra (és melyik duplikált példány marad meg).
            var names = new List<string>();
            foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;
                names.Add(name);
                if (names.Count > MaxMods)
                {
                    truncated = true;
                    break;
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names.Take(MaxMods))
            {
                files.Add((Path.Combine(modsDirectory, name), name,
                    name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notices.Add("A mods mappa nem olvasható: " + ex.Message);
            return new ModScanReport { MinecrafVersion = minecraftVersion ?? "", Loader = normalizedLoader, Notices = notices };
        }

        if (truncated)
            notices.Add($"Több mint {MaxMods} JAR van a mods mappában — csak az első {MaxMods} betűrend szerinti mod lett átvizsgálva.");

        var entries = new List<ModScanEntry>(files.Count);
        foreach (var (path, fileName, enabled) in files)
            entries.Add(EvaluateJar(path, fileName, enabled, minecraftVersion, normalizedLoader, loaderVersion));

        entries = MarkDuplicates(entries);

        if (normalizedLoader is "none" && entries.Any(x => x.Enabled))
            notices.Add("Az Instance loader nélküli (vanilla), ezért a mods mappában lévő modok nem töltődnek be.");

        var problems = entries
            .Where(x => x.Enabled && x.IsBlocking)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ModScanReport
        {
            Total = entries.Count,
            Enabled = entries.Count(x => x.Enabled),
            Disabled = entries.Count(x => !x.Enabled),
            Compatible = entries.Count(x => x.Status == ModCompatibilityStatus.Compatible),
            Incompatible = entries.Count(x => x.Status is ModCompatibilityStatus.IncompatibleGame
                or ModCompatibilityStatus.IncompatibleLoader
                or ModCompatibilityStatus.NotFabric),
            Unknown = entries.Count(x => x.Status == ModCompatibilityStatus.UnknownMetadata),
            Duplicates = entries.Count(x => x.Status == ModCompatibilityStatus.Duplicate),
            Entries = entries,
            Problems = problems,
            Notices = notices,
            MinecrafVersion = minecraftVersion ?? "",
            Loader = normalizedLoader
        };
    }

    /// <summary>Egyetlen mod JAR kiértékelése (másolás előnézetnél a célverzióra).</summary>
    public static ModScanEntry EvaluateJar(
        string jarPath,
        string? fileName,
        bool enabled,
        string minecraftVersion,
        string loader,
        string? loaderVersion = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(jarPath) : fileName;
        var normalizedLoader = NormalizeLoader(loader);

        var info = ModFileReader.TryRead(jarPath, name, out var error);
        if (info is null)
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = Path.GetFileNameWithoutExtension(name),
                Enabled = enabled,
                Status = ModCompatibilityStatus.UnknownMetadata,
                ReasonHu = error.Length > 0 ? error : "A mod leírója nem olvasható."
            };
        }

        if (info.Kind is ModFileKind.Forge or ModFileKind.NeoForge)
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = info.Id,
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.NotFabric,
                ReasonHu = $"Nem Fabric mod ({info.Kind}), ezért Fabric Instance-ban nem indul el."
            };
        }

        if (info.Kind == ModFileKind.Quilt && normalizedLoader is "fabric")
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = info.Id,
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.NotFabric,
                ReasonHu = "Quilt mod: Fabric Loaderrel általában nem indul el."
            };
        }

        if (info.Kind == ModFileKind.Quilt && normalizedLoader is "none")
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = info.Id,
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.NotFabric,
                ReasonHu = "Quilt mod, de az Instance loader nélküli."
            };
        }

        // Quilt mod nem Fabric/none loaderrel (pl. kézzel átírt loader érték): a Quilt
        // függőségi tartományokat nem olvassuk, ezért nem állíthatjuk, hogy kompatibilis.
        if (info.Kind == ModFileKind.Quilt)
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = info.Id,
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.UnknownMetadata,
                ReasonHu = $"Quilt mod, de a launcher a(z) {normalizedLoader} loadert nem támogatja — a kompatibilitás nem ellenőrizhető."
            };
        }

        if (string.IsNullOrWhiteSpace(info.Id))
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = "",
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.UnknownMetadata,
                ReasonHu = "A fabric.mod.json nem tartalmaz mod azonosítót (id)."
            };
        }

        // 1) Minecraft-verzió egyezés.
        var gameMatch = FabricVersionRange.Satisfies(info.MinecraftRange, minecraftVersion);
        if (gameMatch == RangeMatch.No)
        {
            return new ModScanEntry
            {
                FileName = name,
                Title = info.DisplayTitle,
                ModId = info.Id,
                Version = info.Version,
                Enabled = enabled,
                Status = ModCompatibilityStatus.IncompatibleGame,
                MinecraftRange = info.MinecraftRange,
                LoaderRange = info.LoaderRange,
                ReasonHu = $"A mod Minecraft-igénye: {info.MinecraftRange} — az Instance verziója {minecraftVersion}. " +
                           "A mod valószínűleg összeomlik vagy nem töltődik be."
            };
        }

        // 2) Fabric Loader verzió egyezés (csak ha van telepített loaderverzió).
        if (normalizedLoader is "fabric" && info.RequiresFabricLoader && !string.IsNullOrWhiteSpace(loaderVersion))
        {
            var loaderMatch = FabricVersionRange.Satisfies(info.LoaderRange, loaderVersion);
            if (loaderMatch == RangeMatch.No)
            {
                return new ModScanEntry
                {
                    FileName = name,
                    Title = info.DisplayTitle,
                    ModId = info.Id,
                    Version = info.Version,
                    Enabled = enabled,
                    Status = ModCompatibilityStatus.IncompatibleLoader,
                    MinecraftRange = info.MinecraftRange,
                    LoaderRange = info.LoaderRange,
                    ReasonHu = $"A mod Fabric Loader igénye: {info.LoaderRange} — a telepített: {loaderVersion}. " +
                               "Frissítsd a Fabric Loadert."
                };
            }
        }

        var reason = gameMatch == RangeMatch.Unknown && !string.IsNullOrWhiteSpace(info.MinecraftRange)
            ? $"A Minecraft-igény ({info.MinecraftRange}) nem ellenőrizhető automatikusan a(z) {minecraftVersion} verzióval."
            : "";
        return new ModScanEntry
        {
            FileName = name,
            Title = info.DisplayTitle,
            ModId = info.Id,
            Version = info.Version,
            Enabled = enabled,
            Status = ModCompatibilityStatus.Compatible,
            MinecraftRange = info.MinecraftRange,
            LoaderRange = info.LoaderRange,
            ReasonHu = reason
        };
    }

    /// <summary>
    /// Duplikált mod azonosítók megjelölése: minden modból a legújabb verzió marad,
    /// a többi "Duplicate" állapotot kap (ez tipikus összeomlás-ok a Minecraftban).
    /// </summary>
    private static List<ModScanEntry> MarkDuplicates(List<ModScanEntry> entries)
    {
        var groups = entries
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToArray();
        if (groups.Length == 0) return entries;

        var duplicates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(x => x.Enabled)
                .ThenByDescending(x => ParseVersionRank(x.Version))
                .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var keep = ordered[0];
            foreach (var extra in ordered.Skip(1))
                duplicates[extra.FileName] = keep.FileName;
        }

        if (duplicates.Count == 0) return entries;

        return entries
            .Select(entry => duplicates.TryGetValue(entry.FileName, out var keeper)
                ? entry with
                {
                    Status = ModCompatibilityStatus.Duplicate,
                    ReasonHu = $"Ugyanaz a mod ({entry.ModId}) kétszer szerepel. A megtartott példány: {keeper}. " +
                               "A duplikált modok összeomlasztják a játékot."
                }
                : entry)
            .ToList();
    }

    private static double ParseVersionRank(string? version)
    {
        var components = MinecraftVersionOrder.ParseComponents(version);
        if (components.Count == 0) return -1;
        var rank = 0d;
        foreach (var component in components.Take(4))
            rank = rank * 1000 + Math.Clamp(component, 0, 999);
        return rank;
    }
}
