using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Mods;
using TurulMC.Core.Minecraft;

namespace TurulMC.Launcher;

/// <summary>
/// Smart Repair (4.6.0): Minecraft fájlok, Fabric Loader, mappastruktúra és — ami eddig
/// hiányzott — a <b>modok verzióinak</b> ellenőrzése. A modokat offline, a JAR-ok
/// <c>fabric.mod.json</c> leírója alapján vizsgáljuk (Minecraft-verzió, Fabric Loader
/// igény, duplikált modok), és kérésre javítunk is: az inkompatibilis modokat
/// letiltjuk (<c>.jar.disabled</c>) vagy karantén mappába tesszük.
/// </summary>
public sealed partial class MainWindow
{
    private const string ModQuarantineFolder = "mods-quarantine";

    private sealed record RepairRequest(
        LauncherInstance Instance,
        bool ScanMinecraft,
        string FixMode,
        bool HasExplicitFix,
        bool ModsOnly);

    private async System.Threading.Tasks.Task<object?> HandleRepairInstance(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instance = ResolveInstance(obj.TryGetProperty("instanceId", out var idEl) ? idEl.GetString() ?? "" : "");
        var fixMode = obj.TryGetProperty("fix", out var fixEl) && fixEl.ValueKind == JsonValueKind.String
            ? (fixEl.GetString() ?? "").Trim().ToLowerInvariant()
            : "";
        if (fixMode is not ("none" or "disable" or "quarantine")) fixMode = "";
        // modsOnly: csak az offline mod-ellenőrzés fusson (verzióváltás utáni gyors ellenőrzés),
        // ilyenkor nem indítunk Minecraft/Fabric letöltést.
        var modsOnly = obj.TryGetProperty("modsOnly", out var modsOnlyEl) && modsOnlyEl.ValueKind == JsonValueKind.True;
        var request = new RepairRequest(
            instance,
            !modsOnly && (!obj.TryGetProperty("scanMinecraft", out var scanEl) || scanEl.ValueKind != JsonValueKind.False),
            fixMode,
            // Csak a ténylegesen módosító javítás számít "explicit javításnak" — a fix:"none"
            // nem az, különben a takarítás automatikus modmentés nélkül törölne (a
            // MainWindow.Recovery.HasRepairFix is csak a disable|quarantine esetén ment).
            fixMode is "disable" or "quarantine",
            modsOnly);

        var repaired = new List<string>();
        var issues = new List<object>();
        var progress = new Progress<OverallProgress>(p => SendProgress("Smart Repair: " + p.CurrentTask, p.OverallPercentage));

        var previousInstanceId = _activeInstanceId;
        try
        {
            ConfigureInstanceServices(instance.Id);
            var instanceRoot = GetInstanceDirectory(instance.Id);
            var minecraftVersion = string.IsNullOrWhiteSpace(instance.MinecraftVersion) ? "" : instance.MinecraftVersion.Trim();
            if (minecraftVersion.Length == 0)
                throw new InvalidOperationException("Az Instance-hez nincs Minecraft-verzió beállítva.");

            var loader = (instance.Loader ?? "none").Trim().ToLowerInvariant();

            // 1) Minecraft fájlok (kliens, könyvtárak, assetek) — hálózatot igényel.
            if (request.ScanMinecraft)
            {
                try
                {
                    await _installService.EnsureVersionDownloadedAsync(minecraftVersion, progress);
                    repaired.Add($"Minecraft {minecraftVersion} fájlok ellenőrizve");
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Smart Repair: Minecraft fájlok ellenőrzése sikertelen: {ex.Message}");
                    issues.Add(new
                    {
                        title = "Minecraft fájlok",
                        status = "unavailable",
                        statusLabel = "Nem ellenőrizhető",
                        reason = "A Minecraft fájlok ellenőrzése nem sikerült (nincs hálózat vagy Mojang hiba): " + ex.Message,
                        fileName = ""
                    });
                }
            }

            // 2) Fabric Loader.
            if (loader is "fabric" && !request.ModsOnly)
            {
                try
                {
                    var loaderVersion = instance.LoaderVersion;
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        loaderVersion = (await _fabricVersionResolver.ResolveRecommendedAsync(minecraftVersion)).LoaderVersion;
                    await _fabricService.InstallFabricLoaderAsync(minecraftVersion, loaderVersion, progress);
                    if (!string.Equals(loaderVersion, instance.LoaderVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        instance.LoaderVersion = loaderVersion;
                        SaveInstances();
                    }

                    repaired.Add($"Fabric Loader {loaderVersion} ellenőrizve");
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Smart Repair: Fabric ellenőrzés sikertelen: {ex.Message}");
                    issues.Add(new
                    {
                        title = "Fabric Loader",
                        status = "unavailable",
                        statusLabel = "Nem ellenőrizhető",
                        reason = "A Fabric Loader ellenőrzése nem sikerült: " + ex.Message,
                        fileName = ""
                    });
                }
            }

            // 3) Mappastruktúra.
            foreach (var folder in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks", "screenshots" })
                Directory.CreateDirectory(Path.Combine(instanceRoot, folder));
            repaired.Add("Instance mappastruktúra ellenőrizve");

            // 4) Modok verzió-ellenőrzése (offline).
            var modsDirectory = Path.Combine(instanceRoot, "mods");
            var scan = ModCompatibilityScanner.Scan(modsDirectory, minecraftVersion, loader, instance.LoaderVersion);
            repaired.Add($"Modok ellenőrizve: {scan.SummaryHu}");

            foreach (var problem in scan.Problems)
            {
                issues.Add(new
                {
                    title = problem.Title,
                    status = problem.Status.ToString(),
                    statusLabel = StatusLabelHu(problem.Status),
                    reason = problem.ReasonHu,
                    fileName = problem.FileName
                });
            }

            // 5) Kárba ment letöltések és elavult metaadatok felmérése. TÖRLÉS csak akkor
            //    történik, ha a felhasználó javítást kért — a puszta ellenőrzés (pl. a
            //    verzióváltás utáni automatikus futás) nem módosít semmit, mert ilyenkor
            //    nincs automatikus modmentés sem.
            var leftovers = CountModLeftovers(modsDirectory, out var staleMeta);
            var cleanedParts = 0;
            var cleanedMeta = 0;
            if (request.HasExplicitFix)
            {
                cleanedParts = CleanModLeftovers(modsDirectory, out cleanedMeta);
                if (cleanedParts > 0) repaired.Add($"{cleanedParts} félbemaradt letöltés (.part) törölve");
                if (cleanedMeta > 0) repaired.Add($"{cleanedMeta} elavult mod-metaadat törölve");
            }
            else if (leftovers > 0 || staleMeta > 0)
            {
                var pending = new List<string>();
                if (leftovers > 0) pending.Add($"{leftovers} félbemaradt letöltés (.part)");
                if (staleMeta > 0) pending.Add($"{staleMeta} elavult mod-metaadat");
                repaired.Add("Takarításra vár (javításkor törlődik): " + string.Join(", ", pending));
            }

            // 6) Javítás: inkompatibilis / duplikált modok letiltása vagy karanténba helyezése.
            var disabledFiles = new List<string>();
            var quarantinedFiles = new List<string>();
            var quarantinePath = "";
            if (request.HasExplicitFix && request.FixMode is "disable" or "quarantine" && scan.Problems.Count > 0)
            {
                var targets = scan.Problems
                    .Where(x => x.Enabled)
                    // A "nem ellenőrizhető" fájlokat (pl. idegen library JAR, sérült ZIP) nem
                    // tiltjuk le automatikusan: ahhoz a felhasználó döntése kell.
                    .Where(x => x.Status != ModCompatibilityStatus.UnknownMetadata)
                    .Select(x => x.FileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var unverifiable = scan.Problems
                    .Where(x => x.Enabled && x.Status == ModCompatibilityStatus.UnknownMetadata)
                    .Select(x => x.FileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (unverifiable.Length > 0)
                    repaired.Add($"{unverifiable.Length} fájl nem ellenőrizhető (nem mod vagy sérült) — ezeket nem tiltottam le: " +
                                 string.Join(", ", unverifiable.Take(5)));
                if (request.FixMode == "disable")
                {
                    var backupDirectory = Path.Combine(instanceRoot, "mods-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    foreach (var fileName in targets)
                    {
                        var source = Path.Combine(modsDirectory, fileName);
                        if (!File.Exists(source)) continue;
                        var destination = source + ".disabled";

                        // Ha már van letiltott példány (esetleg egy másik verzió, amit a
                        // felhasználó félretett), nem írjuk felül: biztonsági mentésbe kerül.
                        if (File.Exists(destination))
                        {
                            try
                            {
                                Directory.CreateDirectory(backupDirectory);
                                File.Copy(destination, Path.Combine(backupDirectory, Path.GetFileName(destination)), true);
                                File.Delete(destination);
                            }
                            catch (Exception ex)
                            {
                                LauncherLogger.Warning($"A meglévő letiltott mod félretétele nem sikerült ({fileName}): {ex.Message}");
                                continue;
                            }
                        }

                        // Egy zárolt (futó játék által használt) JAR nem szakíthatja meg a
                        // teljes javítást: csak ezt a fájlt hagyjuk ki, a többit letiltjuk.
                        try
                        {
                            File.Move(source, destination);
                        }
                        catch (Exception ex)
                        {
                            LauncherLogger.Warning($"Smart Repair: letiltás sikertelen ({fileName}): {ex.Message}");
                            continue;
                        }

                        disabledFiles.Add(fileName);
                    }
                    if (disabledFiles.Count > 0)
                        repaired.Add($"{disabledFiles.Count} inkompatibilis mod letiltva (Modok oldalon visszakapcsolható)");
                }
                else
                {
                    quarantinePath = Path.Combine(instanceRoot, ModQuarantineFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(quarantinePath);
                    var manifest = new List<object>();
                    foreach (var fileName in targets)
                    {
                        var source = Path.Combine(modsDirectory, fileName);
                        if (!File.Exists(source)) continue;
                        var destination = Path.Combine(quarantinePath, fileName);
                        File.Move(source, destination, true);
                        quarantinedFiles.Add(fileName);
                        var problem = scan.Problems.First(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                        manifest.Add(new
                        {
                            fileName,
                            title = problem.Title,
                            modId = problem.ModId,
                            version = problem.Version,
                            status = problem.Status.ToString(),
                            reason = problem.ReasonHu
                        });

                        var metaPath = Path.Combine(modsDirectory, ".turul-meta", NormalizeModFileName(fileName) + ".json");
                        if (File.Exists(metaPath))
                        {
                            var metaDestination = Path.Combine(quarantinePath, ".turul-meta");
                            Directory.CreateDirectory(metaDestination);
                            File.Move(metaPath, Path.Combine(metaDestination, Path.GetFileName(metaPath)), true);
                        }
                    }

                    await File.WriteAllTextAsync(Path.Combine(quarantinePath, "quarantine.json"), JsonSerializer.Serialize(new
                    {
                        instanceId = instance.Id,
                        instanceName = instance.Name,
                        minecraftVersion,
                        loader,
                        createdAt = DateTime.UtcNow,
                        files = manifest
                    }, new JsonSerializerOptions { WriteIndented = true }));

                    if (quarantinedFiles.Count > 0)
                        repaired.Add($"{quarantinedFiles.Count} inkompatibilis mod karanténba került: {Path.GetFileName(quarantinePath)}");
                }
            }

            if (issues.Count == 0)
                repaired.Add("Nem találtam mod-kompatibilitási problémát");

            LauncherLogger.Info(
                $"Smart Repair kész: {instance.Name} (MC {minecraftVersion}), modok={scan.Total}, " +
                $"problémák={scan.Problems.Count}, letiltva={disabledFiles.Count}, karantén={quarantinedFiles.Count}");

            return new
            {
                success = true,
                instanceId = instance.Id,
                instanceName = instance.Name,
                minecraftVersion,
                loader,
                repaired = repaired.ToArray(),
                issues = issues.Take(60).ToArray(),
                issueCount = issues.Count,
                mods = BuildModScanPayload(instance.Id, scan),
                fixes = new
                {
                    applied = request.HasExplicitFix ? request.FixMode : "none",
                    disabled = disabledFiles.Count,
                    quarantined = quarantinedFiles.Count,
                    removedPartFiles = cleanedParts,
                    removedStaleMeta = cleanedMeta,
                    pendingPartFiles = request.HasExplicitFix ? 0 : leftovers,
                    pendingStaleMeta = request.HasExplicitFix ? 0 : staleMeta,
                    quarantinePath
                }
            };
        }
        finally
        {
            ConfigureInstanceServices(previousInstanceId);
        }
    }

    /// <summary>
    /// Félbemaradt <c>.part</c> letöltések és a már nem létező modokhoz tartozó
    /// <c>.turul-meta</c> bejegyzések megszámlálása (törlés nélkül).
    /// </summary>
    private static int CountModLeftovers(string modsDirectory, out int staleMeta)
    {
        staleMeta = 0;
        if (!Directory.Exists(modsDirectory)) return 0;

        var parts = EnumerateStaleParts(modsDirectory).Count();
        staleMeta = EnumerateOrphanMeta(modsDirectory).Count();
        return parts;
    }

    /// <summary>
    /// A takarítás végrehajtása (csak akkor hívjuk, ha a felhasználó javítást kért, mert
    /// ilyenkor automatikus modmentés is készül). Visszatérés: törölt .part fájlok száma.
    /// </summary>
    private static int CleanModLeftovers(string modsDirectory, out int removedStaleMeta)
    {
        removedStaleMeta = 0;
        if (!Directory.Exists(modsDirectory)) return 0;

        var removedParts = 0;
        foreach (var part in EnumerateStaleParts(modsDirectory))
        {
            try
            {
                File.Delete(part);
                removedParts++;
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Smart Repair: .part törlés sikertelen ({Path.GetFileName(part)}): {ex.Message}");
            }
        }

        foreach (var meta in EnumerateOrphanMeta(modsDirectory))
        {
            try
            {
                File.Delete(meta);
                removedStaleMeta++;
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Smart Repair: metaadat törlés sikertelen ({Path.GetFileName(meta)}): {ex.Message}");
            }
        }

        return removedParts;
    }

    /// <summary>Egy óránál régebbi <c>.part</c> fájlok (éppen futó letöltést nem bántunk).</summary>
    private static IEnumerable<string> EnumerateStaleParts(string modsDirectory)
    {
        foreach (var part in Directory.EnumerateFiles(modsDirectory, "*.part", SearchOption.TopDirectoryOnly))
        {
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(part); }
            catch { continue; }
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromHours(1)) continue;
            yield return part;
        }
    }

    /// <summary>Olyan <c>.turul-meta</c> bejegyzések, amelyekhez nem tartozik mod JAR.</summary>
    private static IEnumerable<string> EnumerateOrphanMeta(string modsDirectory)
    {
        var metaDirectory = Path.Combine(modsDirectory, ".turul-meta");
        if (!Directory.Exists(metaDirectory)) yield break;

        foreach (var meta in Directory.EnumerateFiles(metaDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var baseName = Path.GetFileName(meta);
            if (baseName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".json".Length];
            var jar = Path.Combine(modsDirectory, baseName);
            if (File.Exists(jar) || File.Exists(jar + ".disabled")) continue;
            yield return meta;
        }
    }
}
