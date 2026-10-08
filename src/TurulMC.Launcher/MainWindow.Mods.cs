using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Mods;
using TurulMC.Core.Validation;

namespace TurulMC.Launcher;

/// <summary>
/// Mod-kompatibilitás és mod-frissítés (4.6.0). A Smart Repair, az Instance-másolás és a
/// Modok oldal is ezeket a műveleteket használja:
/// <list type="bullet">
/// <item><c>mods.scan</c> — offline vizsgálat a JAR-ok fabric.mod.json leírója alapján.</item>
/// <item><c>mods.updatePlan</c> — Modrinthön elérhető, célverzióhoz illő frissítések terve.</item>
/// <item><c>mods.update</c> — a kiválasztott frissítések letöltése és cseréje (hash-ellenőrzéssel).</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private const int ModUpdatePlanMaxMods = 150;
    private const int ModUpdateConcurrency = 4;

    private sealed record ModMetaEntry(string Title, string ProjectId, string VersionId, string Source);

    private sealed record ModPlanItem(
        string FileName,
        string Title,
        string CurrentVersionId,
        string CurrentVersionNumber,
        string Status,
        string StatusLabel,
        string Reason,
        string NewVersionId,
        string NewVersionNumber,
        string DownloadFileName,
        string ProjectId);

    private string GetModsDirectory(string instanceId)
        => Path.Combine(GetInstanceDirectory(instanceId), "mods");

    private LauncherInstance ResolveInstance(string? instanceId)
    {
        var id = string.IsNullOrWhiteSpace(instanceId) ? _activeInstanceId : instanceId;
        return _instances.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az Instance nem található.");
    }

    /// <summary>Mod metaadat (Modrinth project/version azonosító) beolvasása.</summary>
    private ModMetaEntry? ReadModMeta(string instanceId, string normalizedFileName)
    {
        try
        {
            var metaPath = Path.Combine(GetModsDirectory(instanceId), ".turul-meta", normalizedFileName + ".json");
            if (!File.Exists(metaPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
            var title = Get("title");
            var projectId = Get("projectId");
            var versionId = Get("versionId");
            var source = Get("source");
            if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(versionId)) return null;
            return new ModMetaEntry(
                string.IsNullOrWhiteSpace(title) ? normalizedFileName : title,
                projectId,
                versionId,
                source);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Mod metaadat olvasási hiba ({normalizedFileName}): {ex.Message}");
            return null;
        }
    }

    private static string NormalizeModFileName(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    /// <summary>A mod JAR-jából kiolvasott verziószám (a visszalépés-védelemhez).</summary>
    private string ReadModDescriptorVersion(string instanceId, string fileName)
    {
        try
        {
            var name = Path.GetFileName(fileName);
            var path = Path.Combine(GetModsDirectory(instanceId), name);
            if (!File.Exists(path)) return "";
            var info = ModFileReader.TryRead(path, name, out _);
            return info?.Version ?? "";
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Mod verzió olvasási hiba ({fileName}): {ex.Message}");
            return "";
        }
    }

    private static object BuildModScanPayload(string instanceId, ModScanReport report) => new
    {
        instanceId,
        minecraftVersion = report.MinecrafVersion,
        loader = report.Loader,
        total = report.Total,
        enabled = report.Enabled,
        disabled = report.Disabled,
        compatible = report.Compatible,
        incompatible = report.Incompatible,
        unknown = report.Unknown,
        duplicates = report.Duplicates,
        summary = report.SummaryHu,
        notices = report.Notices.ToArray(),
        problems = report.Problems.Take(40).Select(entry => new
        {
            fileName = entry.FileName,
            title = entry.Title,
            modId = entry.ModId,
            version = entry.Version,
            status = entry.Status.ToString(),
            statusLabel = StatusLabelHu(entry.Status),
            reason = entry.ReasonHu,
            minecraftRange = entry.MinecraftRange,
            loaderRange = entry.LoaderRange
        }).ToArray()
    };

    private static string StatusLabelHu(ModCompatibilityStatus status) => status switch
    {
        ModCompatibilityStatus.Compatible => "Rendben",
        ModCompatibilityStatus.IncompatibleGame => "Nem támogatja a verziót",
        ModCompatibilityStatus.IncompatibleLoader => "Újabb Fabric Loader kell",
        ModCompatibilityStatus.NotFabric => "Nem Fabric mod",
        ModCompatibilityStatus.UnknownMetadata => "Nem ellenőrizhető",
        ModCompatibilityStatus.Duplicate => "Duplikált mod",
        _ => status.ToString()
    };

    /// <summary>Offline mod-vizsgálat egy Instance-re (Smart Repair és másolás is ezt hívja).</summary>
    private System.Threading.Tasks.Task<object?> HandleModScan(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instance = ResolveInstance(obj.TryGetProperty("instanceId", out var idEl) ? idEl.GetString() ?? "" : "");
        var targetVersion = obj.TryGetProperty("minecraftVersion", out var mcEl) && mcEl.ValueKind == JsonValueKind.String
            ? mcEl.GetString()?.Trim() ?? instance.MinecraftVersion
            : instance.MinecraftVersion;
        var loader = obj.TryGetProperty("loader", out var loaderEl) && loaderEl.ValueKind == JsonValueKind.String
            ? loaderEl.GetString()?.Trim().ToLowerInvariant() ?? instance.Loader
            : instance.Loader;

        var report = ModCompatibilityScanner.Scan(
            GetModsDirectory(instance.Id), targetVersion, loader, instance.LoaderVersion);
        return System.Threading.Tasks.Task.FromResult<object?>(BuildModScanPayload(instance.Id, report));
    }

    /// <summary>
    /// Modrinthön elérhető, a cél-Minecraft-verzióhoz illő mod-frissítések terve.
    /// Csak jelzi, mit lehet telepíteni — nem módosít semmit.
    /// </summary>
    private async System.Threading.Tasks.Task<object?> HandleModUpdatePlan(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instance = ResolveInstance(obj.TryGetProperty("instanceId", out var idEl) ? idEl.GetString() ?? "" : "");
        var targetVersion = obj.TryGetProperty("minecraftVersion", out var mcEl) && mcEl.ValueKind == JsonValueKind.String
            ? mcEl.GetString()?.Trim() ?? instance.MinecraftVersion
            : instance.MinecraftVersion;
        var loader = (obj.TryGetProperty("loader", out var loaderEl) && loaderEl.ValueKind == JsonValueKind.String
            ? loaderEl.GetString()?.Trim().ToLowerInvariant()
            : instance.Loader) ?? "none";
        if (string.IsNullOrWhiteSpace(targetVersion))
            throw new InvalidOperationException("Nincs cél Minecraft-verzió a mod-ellenőrzéshez.");

        var onlyProblems = !obj.TryGetProperty("onlyProblems", out var onlyEl) || onlyEl.ValueKind != JsonValueKind.False;
        var modsDirectory = GetModsDirectory(instance.Id);
        var scan = ModCompatibilityScanner.Scan(modsDirectory, targetVersion, loader, instance.LoaderVersion);
        var problemFiles = scan.Problems.Select(x => x.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (obj.TryGetProperty("fileNames", out var namesEl) && namesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var nameEl in namesEl.EnumerateArray())
            {
                if (nameEl.ValueKind != JsonValueKind.String) continue;
                var name = Path.GetFileName(nameEl.GetString() ?? "");
                if (name.Length > 0) allowList.Add(name);
            }
        }

        var files = Directory.Exists(modsDirectory)
            ? Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(name => allowList.Count == 0 || allowList.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(ModUpdatePlanMaxMods)
                .ToArray()
            : Array.Empty<string>();

        var entries = new List<ModPlanItem>();
        var sync = new object();
        var gate = new SemaphoreSlim(ModUpdateConcurrency);
        var tasks = files.Select(async fileName =>
        {
            await gate.WaitAsync();
            try
            {
                var normalized = NormalizeModFileName(fileName);
                var meta = ReadModMeta(instance.Id, normalized);
                var scanEntry = scan.Entries.FirstOrDefault(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                var isProblem = problemFiles.Contains(fileName);
                if (onlyProblems && !isProblem) return;

                var title = meta?.Title is { Length: > 0 } metaTitle && !metaTitle.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    ? metaTitle
                    : scanEntry?.Title ?? Path.GetFileNameWithoutExtension(normalized);

                if (meta is null || string.IsNullOrWhiteSpace(meta.ProjectId))
                {
                    Add(new ModPlanItem(fileName, title, meta?.VersionId ?? "", scanEntry?.Version ?? "",
                        "unknown", "Nincs Modrinth azonosító",
                        "Ehhez a modhoz nincs Modrinth metaadat (kézzel másolt vagy modpack fájl), ezért nem tudjuk automatikusan frissíteni.",
                        "", "", "", ""));
                    return;
                }

                // Szándékosan szűretlen verziólista: így a telepített verzió is benne van, és
                // a tervező látja a kiadási dátumát (nincs visszalépés egy régebbi kiadásra).
                var versionsJson = await ModrinthHttpClient.GetStringAsync(
                    $"project/{Uri.EscapeDataString(meta.ProjectId)}/version?include_changelog=false");

                var installedVersion = scanEntry?.Version ?? "";
                var candidate = ModUpdatePlanner.PickBest(versionsJson, targetVersion, loader, meta.VersionId, installedVersion);
                if (candidate is null)
                {
                    // Ha a telepített verzió maga támogatja a célverziót, akkor nincs mit
                    // frissíteni — a jelzett probléma másból (duplikáció, loader) ered.
                    var installedCompatible = ModUpdatePlanner.IsInstalledVersionCompatible(
                        versionsJson, targetVersion, loader, meta.VersionId);
                    Add(new ModPlanItem(fileName, title, meta.VersionId, installedVersion,
                        installedCompatible ? "uptodate" : "unavailable",
                        installedCompatible ? "Ez a legfrissebb kompatibilis kiadás" : "Nincs kompatibilis verzió",
                        installedCompatible
                            ? $"A telepített verzió a legfrissebb, amely a Minecraft {targetVersion} verziót támogatja. " +
                              "A jelzett probléma másból ered (pl. duplikált példány vagy loader-igény) — tiltsd le, vagy keress alternatívát."
                            : $"A Modrinthön nincs a telepítettnél újabb, a Minecraft {targetVersion} verziót és a(z) {loader} loadert támogató kiadás.",
                        "", "", "", meta.ProjectId));
                    return;
                }

                // Verziócsalád-egyezés: tájékoztató jellegű, nem telepítjük automatikusan,
                // mert a mod a Minecraft egy MÁSIK pontverzióját deklarálja.
                if (!ModUpdatePlanner.IsInstallable(candidate))
                {
                    var supported = candidate.GameVersions.Length > 0
                        ? string.Join(", ", candidate.GameVersions.Take(4))
                        : "ismeretlen";
                    Add(new ModPlanItem(fileName, title, meta.VersionId, installedVersion,
                        "family", "Csak verziócsalád-egyezés",
                        $"A legközelebbi kiadás ({candidate.VersionNumber}) a Minecraft {supported} verziót támogatja, " +
                        $"az Instance-od viszont {targetVersion}. Pontos egyezés nélkül nem telepítjük automatikusan — " +
                        "ellenőrizd kézzel a mod oldalán.",
                        "", candidate.VersionNumber, candidate.FileName, meta.ProjectId));
                    return;
                }

                Add(new ModPlanItem(fileName, title, meta.VersionId, installedVersion,
                    "update", "Frissíthető",
                    $"Új verzió érhető el a Minecraft {targetVersion} verzióhoz: {candidate.VersionNumber} ({candidate.VersionType}).",
                    candidate.VersionId, candidate.VersionNumber, candidate.FileName, meta.ProjectId));
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Mod frissítés-terv hiba ({fileName}): {ex.Message}");
                Add(new ModPlanItem(fileName, Path.GetFileNameWithoutExtension(NormalizeModFileName(fileName)),
                    "", "", "error", "Ellenőrzés sikertelen",
                    "A Modrinth nem válaszolt: " + ex.Message, "", "", "", ""));
            }
            finally
            {
                gate.Release();
            }

            void Add(ModPlanItem item)
            {
                lock (sync) entries.Add(item);
            }
        }).ToArray();

        await System.Threading.Tasks.Task.WhenAll(tasks);

        var ordered = entries
            .OrderBy(x => x.Status switch
            {
                "update" => 0,
                "family" => 1,
                "uptodate" => 2,
                "unavailable" => 3,
                "unknown" => 4,
                _ => 5
            })
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            success = true,
            instanceId = instance.Id,
            minecraftVersion = targetVersion,
            loader,
            scanned = scan.Total,
            problems = scan.Problems.Count,
            updateCount = ordered.Count(x => x.Status == "update"),
            entries = ordered.Select(x => new
            {
                fileName = x.FileName,
                title = x.Title,
                projectId = x.ProjectId,
                currentVersionId = x.CurrentVersionId,
                currentVersionNumber = x.CurrentVersionNumber,
                status = x.Status,
                statusLabel = x.StatusLabel,
                reason = x.Reason,
                newVersionId = x.NewVersionId,
                newVersionNumber = x.NewVersionNumber,
                downloadFileName = x.DownloadFileName
            }).ToArray()
        };
    }

    /// <summary>
    /// A kiválasztott mod-frissítések letöltése és cseréje. A verziót mindig a szerver
    /// oldalon oldjuk fel újra (a kliens csak azonosítókat küld), a letöltött fájl
    /// SHA-ellenőrzése kötelező, a régi JAR pedig biztonsági mentésbe kerül.
    /// </summary>
    private async System.Threading.Tasks.Task<object?> HandleModUpdateApply(string? data)
    {
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Modfrissítés előtt zárd be a Minecraftot.");

        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var instance = ResolveInstance(obj.TryGetProperty("instanceId", out var idEl) ? idEl.GetString() ?? "" : "");
        // A célverziót nem vesszük át vakon a felülettől: az Instance saját verziója a mérvadó
        // (a másolási folyamat a saját, szerver oldali útvonalát használja ehhez).
        var targetVersion = obj.TryGetProperty("minecraftVersion", out var mcEl) && mcEl.ValueKind == JsonValueKind.String
            ? mcEl.GetString()?.Trim() ?? instance.MinecraftVersion
            : instance.MinecraftVersion;
        if (!string.IsNullOrWhiteSpace(targetVersion) &&
            !TurulMC.Core.Mods.MinecraftVersionOrder.IsSame(targetVersion, instance.MinecraftVersion))
            throw new InvalidOperationException(
                $"A kért Minecraft-verzió ({targetVersion}) nem egyezik az Instance verziójával ({instance.MinecraftVersion}).");
        targetVersion = instance.MinecraftVersion;
        var loader = (obj.TryGetProperty("loader", out var loaderEl) && loaderEl.ValueKind == JsonValueKind.String
            ? loaderEl.GetString()?.Trim().ToLowerInvariant()
            : instance.Loader) ?? "none";
        if (string.IsNullOrWhiteSpace(targetVersion))
            throw new InvalidOperationException("Nincs cél Minecraft-verzió a modfrissítéshez.");

        var requested = new List<(string FileName, string ProjectId, string VersionId)>();
        if (obj.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fileName = Path.GetFileName(item.TryGetProperty("fileName", out var f) ? f.GetString() ?? "" : "");
                var projectId = item.TryGetProperty("projectId", out var p) ? p.GetString()?.Trim() ?? "" : "";
                var versionId = item.TryGetProperty("versionId", out var v) ? v.GetString()?.Trim() ?? "" : "";
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(versionId)) continue;
                requested.Add((fileName, projectId, versionId));
                if (requested.Count >= ModUpdatePlanMaxMods) break;
            }
        }

        if (requested.Count == 0)
            throw new InvalidOperationException("Nincs kijelölt modfrissítés.");

        var backupDirectory = Path.Combine(GetInstanceDirectory(instance.Id), "mods-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var result = await InstallModUpdatesAsync(
            metaSourceInstanceId: instance.Id,
            targetInstanceId: instance.Id,
            targetVersion,
            loader,
            requested,
            backupDirectory,
            progressScope: "mods");

        return new
        {
            success = result.Failed == 0,
            instanceId = instance.Id,
            minecraftVersion = targetVersion,
            updated = result.Updated,
            failed = result.Failed,
            updatedFiles = result.UpdatedFiles,
            failures = result.Failures,
            backupDirectory = Directory.Exists(backupDirectory) ? backupDirectory : ""
        };
    }

    /// <summary>
    /// A megadott mod fájlokhoz megkeresi a Modrinthön a cél-Minecraft-verzióhoz illő
    /// verziót. Kiszolgáló oldali feloldás: a kliens csak fájlneveket küld.
    /// </summary>
    private async System.Threading.Tasks.Task<List<(string FileName, string ProjectId, string VersionId, string Title, string VersionNumber)>>
        ResolveModUpdateTargetsAsync(
            string metaSourceInstanceId,
            string targetVersion,
            string loader,
            IReadOnlyCollection<string> fileNames)
    {
        var results = new List<(string, string, string, string, string)>();
        var sync = new object();
        var gate = new SemaphoreSlim(ModUpdateConcurrency);
        var tasks = fileNames.Select(async fileName =>
        {
            await gate.WaitAsync();
            try
            {
                var normalized = NormalizeModFileName(fileName);
                var meta = ReadModMeta(metaSourceInstanceId, normalized);
                if (meta is null || string.IsNullOrWhiteSpace(meta.ProjectId)) return;

                // Szándékosan szűretlen verziólista: így a telepített verzió is benne van, és
                // a tervező látja a kiadási dátumát (nincs visszalépés egy régebbi kiadásra).
                var versionsJson = await ModrinthHttpClient.GetStringAsync(
                    $"project/{Uri.EscapeDataString(meta.ProjectId)}/version?include_changelog=false");

                var installedVersion = ReadModDescriptorVersion(metaSourceInstanceId, fileName);
                var candidate = ModUpdatePlanner.PickBest(versionsJson, targetVersion, loader, meta.VersionId, installedVersion);
                // Csak a pontos verzióegyezés telepíthető (a "family" csak tájékoztató).
                if (candidate is null || !ModUpdatePlanner.IsInstallable(candidate)) return;

                lock (sync)
                {
                    results.Add((fileName, meta.ProjectId, candidate.VersionId, meta.Title, candidate.VersionNumber));
                }
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Mod frissítés feloldás hiba ({fileName}): {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await System.Threading.Tasks.Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Modverziók letöltése és cseréje a cél Instance mods mappájában. A verziókat mindig
    /// újra ellenőrizzük (projekt-egyezés, célverzió + loader támogatás), a letöltött
    /// fájl SHA-ellenőrzése kötelező, a lecserélt régi JAR biztonsági mentésbe kerül.
    /// </summary>
    private async System.Threading.Tasks.Task<(int Updated, int Failed, List<object> UpdatedFiles, List<object> Failures)>
        InstallModUpdatesAsync(
            string metaSourceInstanceId,
            string targetInstanceId,
            string targetVersion,
            string loader,
            IReadOnlyList<(string FileName, string ProjectId, string VersionId)> items,
            string backupDirectory,
            string progressScope)
    {
        var modsDirectory = GetModsDirectory(targetInstanceId);
        Directory.CreateDirectory(modsDirectory);
        var metaDirectory = Path.Combine(modsDirectory, ".turul-meta");
        Directory.CreateDirectory(metaDirectory);

        var updated = new List<object>();
        var failures = new List<object>();

        // A cél Instance mod-mappáját egyszer olvassuk be (minden JAR megnyitása drága), és a
        // művelet során karbantartjuk, hogy a következő modnál is pontos legyen. A célverziót
        // is átadjuk, hogy tudjuk: a célban lévő példány egyáltalán működik-e ezzel a verzióval.
        var targetIndex = new List<ModTargetFile>(
            ModTargetReconciler.BuildIndex(modsDirectory, targetVersion, loader));

        for (var i = 0; i < items.Count; i++)
        {
            var (fileName, projectId, versionId) = items[i];
            var normalized = NormalizeModFileName(fileName);
            PostPackProgress(progressScope, $"Mod frissítése: {normalized}", i * 100d / items.Count, normalized, 0, 0, 0, i, items.Count);
            try
            {
                using var versionResponse = await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
                versionResponse.EnsureSuccessStatusCode();
                using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                var version = versionDoc.RootElement;

                var resolvedProjectId = version.TryGetProperty("project_id", out var projectEl) ? projectEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(projectId) &&
                    !resolvedProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A kiválasztott verzió nem ehhez a modhoz tartozik.");

                if (!ModUpdatePlanner.Supports(version, targetVersion, loader))
                    throw new InvalidOperationException($"A verzió nem támogatja a Minecraft {targetVersion} verziót és a(z) {loader} loadert.");

                var fileElement = SelectJarFileElement(version)
                    ?? throw new InvalidOperationException("A verzióhoz nincs letölthető JAR.");
                var downloadUrl = fileElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                EnsureSafeDownloadUrl(downloadUrl);
                var newFileName = Path.GetFileName(fileElement.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? "" : "");
                if (string.IsNullOrWhiteSpace(newFileName) || !newFileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Érvénytelen mod fájlnév a Modrinth válaszban.");

                var destination = Path.Combine(modsDirectory, newFileName);
                var hashes = fileElement.TryGetProperty("hashes", out var hashesEl) ? hashesEl : default;
                var newVersionNumber = version.TryGetProperty("version_number", out var numberEl) ? numberEl.GetString() ?? "" : "";

                // 1) Döntés a letöltés ELŐTT: ha a célban ugyanazon a fájlnéven már van újabb,
                //    a célverzióval működő példány, nem írjuk felül (a letöltés a fájlt cserélné).
                var sameNameEntry = targetIndex.FirstOrDefault(x =>
                    x.FileName.Equals(newFileName, StringComparison.OrdinalIgnoreCase));
                if (sameNameEntry is not null && !string.IsNullOrWhiteSpace(sameNameEntry.ModId))
                {
                    var preInspection = ModTargetReconciler.Inspect(targetIndex, sameNameEntry.ModId, newVersionNumber);
                    if (preInspection.HasNewerVersion)
                    {
                        failures.Add(new
                        {
                            fileName = normalized,
                            error = preInspection.ReasonHu,
                            skippedNewer = true
                        });
                        LauncherLogger.Info($"Modfrissítés kihagyva letöltés előtt (célban újabb verzió): {normalized} — {preInspection.NewerVersion}");
                        continue;
                    }
                }

                // Ha a cél fájlnév már létezik (azonos nevű új verzió), a régit előbb
                // biztonsági mentésbe tesszük, mert a letöltés felülírja.
                if (File.Exists(destination))
                {
                    try
                    {
                        Directory.CreateDirectory(backupDirectory);
                        File.Copy(destination, Path.Combine(backupDirectory, newFileName), true);
                    }
                    catch (Exception backupError)
                    {
                        LauncherLogger.Warning($"A felülírandó mod mentése nem sikerült ({newFileName}): {backupError.Message}");
                    }
                }

                await DownloadWithOptionalHashesAsync(downloadUrl, destination, hashes,
                    progressScope, newFileName, i, items.Count);

                // 2) A cél Instance saját mod-készletének ellenőrzése a letöltött JAR valódi
                //    mod-azonosítója alapján: ha ott már ÚJABB, működő verzió van, nem lépünk
                //    vissza; a más fájlnéven lévő duplikátumot pedig biztonsági mentésbe
                //    tesszük (különben két azonos mod id = összeomlás).
                var newDescriptor = ModFileReader.TryRead(destination, newFileName, out _);
                var inspection = ModTargetReconciler.Inspect(targetIndex, newDescriptor?.Id, newVersionNumber);

                if (inspection.HasNewerVersion)
                {
                    try { File.Delete(destination); } catch (Exception deleteError) { LauncherLogger.Warning($"A letöltött régebbi mod nem törölhető: {deleteError.Message}"); }
                    failures.Add(new
                    {
                        fileName = normalized,
                        error = inspection.ReasonHu,
                        skippedNewer = true
                    });
                    targetIndex.RemoveAll(x => x.FileName.Equals(newFileName, StringComparison.OrdinalIgnoreCase));
                    LauncherLogger.Info($"Modfrissítés kihagyva (cél Instance-ban újabb verzió): {normalized} — {inspection.NewerVersion}");
                    continue;
                }

                // A lecserélendő példányok: a forrás fájlnév (akár letiltva) és minden más,
                // ugyanahhoz a mod-azonosítóhoz tartozó fájl a cél Instance-ban.
                var toReplace = new List<string> { normalized, normalized + ".disabled" };
                toReplace.AddRange(inspection.SameModFiles);
                var replaced = new List<string>();
                foreach (var existing in toReplace.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (existing.Equals(newFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var existingPath = Path.Combine(modsDirectory, existing);
                    if (!File.Exists(existingPath)) continue;
                    Directory.CreateDirectory(backupDirectory);
                    File.Copy(existingPath, Path.Combine(backupDirectory, existing), true);
                    File.Delete(existingPath);
                    replaced.Add(existing);
                }

                if (replaced.Count > 0)
                {
                    targetIndex.RemoveAll(x => replaced.Any(r => r.Equals(x.FileName, StringComparison.OrdinalIgnoreCase)));
                    LauncherLogger.Info($"Modfrissítés: {replaced.Count} korábbi példány biztonsági mentésbe ({normalized})");
                }

                if (newDescriptor is not null && !string.IsNullOrWhiteSpace(newDescriptor.Id))
                {
                    targetIndex.RemoveAll(x => x.FileName.Equals(newFileName, StringComparison.OrdinalIgnoreCase));
                    targetIndex.Add(new ModTargetFile(newFileName, newDescriptor.Id, newVersionNumber));
                }

                var title = version.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? normalized : normalized;
                var oldMeta = ReadModMeta(metaSourceInstanceId, normalized);
                var finalTitle = oldMeta?.Title is { Length: > 0 } existingTitle &&
                                 !existingTitle.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    ? existingTitle
                    : title;
                await File.WriteAllTextAsync(Path.Combine(metaDirectory, newFileName + ".json"), JsonSerializer.Serialize(new
                {
                    title = finalTitle,
                    iconUrl = "",
                    projectId = resolvedProjectId,
                    versionId,
                    source = "update",
                    updatedAt = DateTime.UtcNow
                }));

                updated.Add(new { fileName = normalized, newFileName, versionId });
                LauncherLogger.Info($"Mod frissítve: {normalized} → {newFileName} ({versionId})");
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Modfrissítés sikertelen ({normalized}): {ex.Message}");
                failures.Add(new { fileName = normalized, error = ex.Message });
            }

            PostPackProgress(progressScope, $"Mod frissítése: {normalized}", (i + 1) * 100d / items.Count,
                normalized, 0, 0, 0, i + 1, items.Count);
        }

        return (updated.Count, failures.Count, updated, failures);
    }

    private static JsonElement? SelectJarFileElement(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        JsonElement? fallback = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            var fileName = file.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
            if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            // Üres letöltési URL-lel nem kínáljuk fel a fájlt (a tervező is kihagyja), különben
            // a terv telepíthetőnek látszana, de a letöltés URL-hibával elhasalna.
            var url = file.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url)) continue;
            var primary = file.TryGetProperty("primary", out var primaryEl) && primaryEl.ValueKind == JsonValueKind.True;
            if (primary) return file;
            fallback ??= file;
        }

        return fallback;
    }

    /// <summary>Instance JVM argumentumok szöveges mezőjének biztonságos feldolgozása.</summary>
    private static string[] ParseInstanceJvmArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(64)
            .ToArray();
        LauncherSettingsValidator.ValidateJvmArgs(parts);
        return parts;
    }
}
