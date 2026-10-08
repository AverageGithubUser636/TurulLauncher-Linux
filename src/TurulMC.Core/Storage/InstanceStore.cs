using System.Text.Json;
using System.Text.Json.Serialization;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;

namespace TurulMC.Core.Storage;

/// <summary>
/// Az <c>instances.json</c> egyetlen, megosztott olvasója/írója.
///
/// Korábban ez a logika kétszer volt duplikálva (a CLI és a WinUI felület
/// külön-külön), ami miatt eltérően viselkedtek — az audit <b>M-22</b> pontja
/// épp ezt rögzíti: a CLI hibás parse után felülírta a közös fájlt, a
/// <c>clean</c> pedig törölte a listát. Itt egyetlen, a WinUI verzió
/// robusztusságával felvértezett implementáció van, AtomicFile-os mentéssel.
/// </remarks>
public sealed class InstanceStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _instancesRoot;

    public InstanceStore(string? path = null, string? instancesRoot = null)
    {
        _path = path ?? LauncherPaths.InstancesFile;
        _instancesRoot = instancesRoot ?? LauncherPaths.InstancesRoot;
    }

    /// <summary>A ténylegesen használt fájl útvonala.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Instance-lista betöltése. Hibás bejegyzések kihagyva, duplák szűrve,
    /// név-ütközések feloldva. A <c>.bak</c> tartalék fájlról is visszatölt.
    /// </summary>
    public (string ActiveId, List<LauncherInstance> Instances) Load()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) { LauncherLogger.Warning($"Instance mappa nem hozható létre: {ex.Message}"); }
        }

        JsonDocument? doc = null;
        string? loadedFrom = null;
        var backupPath = _path + ".bak";

        foreach (var candidate in new[] { _path, backupPath })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(candidate));
                loadedFrom = candidate;
                break;
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Instance config nem olvasható ({Path.GetFileName(candidate)}): {ex.Message}");
            }
        }

        if (doc is null) return ("", new List<LauncherInstance>());

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LauncherLogger.Warning($"Instance config hibás gyökérelem ({loadedFrom ?? "unknown"}); üres listával indulunk.");
                return ("", new List<LauncherInstance>());
            }

            var loaded = root.TryGetProperty("instances", out var instancesEl)
                ? JsonSerializer.Deserialize<List<LauncherInstance>>(instancesEl.GetRawText())
                  ?? new List<LauncherInstance>()
                : new List<LauncherInstance>();

            var cleaned = new List<LauncherInstance>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in loaded)
            {
                if (instance is null ||
                    string.IsNullOrWhiteSpace(instance.Id) ||
                    !usedIds.Add(instance.Id) ||
                    string.IsNullOrWhiteSpace(instance.MinecraftVersion) ||
                    (instance.Loader != "none" && instance.Loader != "fabric") ||
                    instance.RamMb < 1024 || instance.RamMb > 65536)
                {
                    LauncherLogger.Warning("Hibás Instance-bejegyzés kihagyva.");
                    continue;
                }

                instance.Name = instance.Name?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(instance.Name))
                    instance.Name = cleaned.Count == 0 ? "Alap Instance" : $"Instance {cleaned.Count + 1}";
                if (instance.Name.Length > 48)
                    instance.Name = instance.Name[..48].Trim();

                var baseName = instance.Name;
                var suffix = 2;
                while (!usedNames.Add(instance.Name))
                    instance.Name = $"{baseName} {suffix++}";

                instance.Loader = instance.Loader.ToLowerInvariant();
                if (instance.Loader == "none")
                    instance.LoaderVersion = "";
                if (instance.CreatedAt == default)
                    instance.CreatedAt = DateTime.UtcNow;
                if (instance.LastUsed == default)
                    instance.LastUsed = instance.CreatedAt;

                HydrateModpackMetadata(instance);
                cleaned.Add(instance);
            }

            var active = root.TryGetProperty("activeInstanceId", out var activeEl)
                ? activeEl.GetString() ?? ""
                : "";

            if (!cleaned.Any(x => x.Id.Equals(active, StringComparison.OrdinalIgnoreCase)))
                active = cleaned.OrderByDescending(x => x.LastUsed).FirstOrDefault()?.Id ?? "";

            if (!string.Equals(loadedFrom, _path, StringComparison.OrdinalIgnoreCase))
                LauncherLogger.Warning("Instance config backupból visszaállítva.");

            return (active, cleaned);
        }
    }

    /// <summary>
    /// Mentés. Üres lista = fájl törlése. A hiba NEM dobódik (a launcher nem
    /// indulhat el egy mentési hibától), hanem naplózódik; a visszajelzés a
    /// <see cref="SaveResult"/>-on keresztül történik.
    /// </summary>
    public SaveResult Save(IReadOnlyList<LauncherInstance> instances, string activeId)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex)
            {
                var msg = "Az Instance mappa nem hozható létre: " + ex.Message;
                LauncherLogger.Error(msg);
                return new SaveResult(false, msg);
            }
        }

        if (instances.Count == 0)
        {
            foreach (var path in new[] { _path, _path + ".tmp", _path + ".bak" })
            {
                try
                {
                    AtomicFile.ClearReadOnly(path);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Instance config törlés sikertelen ({Path.GetFileName(path)}): {ex.Message}");
                }
            }
            return SaveResult.Ok;
        }

        if (string.IsNullOrWhiteSpace(activeId) ||
            !instances.Any(x => x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase)))
        {
            activeId = instances.OrderByDescending(x => x.LastUsed).First().Id;
        }

        var json = JsonSerializer.Serialize(new
        {
            activeInstanceId = activeId,
            instances
        }, WriteOptions);

        var result = AtomicFile.TryWriteAllText(_path, json);
        if (!result.Success)
        {
            LauncherLogger.Error("Instance config mentése sikertelen: " + result.Error);
            return new SaveResult(false, result.Error ?? "Ismeretlen mentési hiba");
        }

        if (result.UsedFallback)
            LauncherLogger.Warning("Az instances.json csak tartalék útvonalon menthető.");

        return SaveResult.Ok;
    }

    /// <summary>Az Instance tartalmának könyvtára (világ, modok, logok).</summary>
    public string GetInstanceDirectory(string instanceId)
        => Path.Combine(_instancesRoot, SanitizeId(instanceId));

    /// <summary>Instance-azonosító szűrése: csak biztonságos karakterek, korlátozott hossz.</summary>
    public static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var trimmed = id.Trim();
        if (trimmed.Length > 64) trimmed = trimmed[..64];
        var ok = new string(trimmed.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return ok.Trim('.', '_');
    }

    private static void HydrateModpackMetadata(LauncherInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.ModpackProjectId) &&
            !string.IsNullOrWhiteSpace(instance.ModpackVersionId))
            return;

        var dir = Path.Combine(LauncherPaths.InstancesRoot, SanitizeId(instance.Id));
        var path = Path.Combine(dir, ".turul-modpack.json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            instance.ModpackProjectId = root.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
            instance.ModpackVersionId = root.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
            instance.ModpackName = root.TryGetProperty("name", out var n) ? n.GetString() ?? instance.Name : instance.Name;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Modpack metaadat nem olvasható ({instance.Name}): {ex.Message}");
        }
    }

    public readonly record struct SaveResult(bool Success, string? Error)
    {
        public static SaveResult Ok => new(true, null);
    }
}
