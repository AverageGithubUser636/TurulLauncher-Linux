using TurulMC.Core.Logging;
using TurulMC.Core.Security;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Mods;

/// <summary>
/// Egy Instance mod- és resource pack fájljainak kezelése.
/// A ki/bekapcsolás konvenciója megegyezik a Core többi részével:
/// a letiltott mod <c>.jar.disabled</c> végződést kap
/// (lásd <see cref="ModCompatibilityScanner"/>, <see cref="ModTargetReconciler"/>).
/// Minden fájlnév traversal-ellenőrzött, a méretek korlátozottak.
/// </summary>
public sealed class ModManager
{
    public const long MaxModBytes = 512L * 1024 * 1024;
    public const long MaxPackBytes = 1024L * 1024 * 1024;

    // ------------------------------------------------------------------ modok

    public IReadOnlyList<ManagedMod> ListMods(string modsDir)
    {
        var result = new List<ManagedMod>();
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) return result;

        string[] files;
        try
        {
            files = Directory.GetFiles(modsDir, "*.jar*");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Mod lista olvasási hiba ({modsDir}): {ex.Message}");
            return result;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (!IsModFileName(name)) continue;

            var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var info = ModFileReader.TryRead(file, out var error);
            long size;
            try { size = new FileInfo(file).Length; } catch { size = 0; }

            result.Add(new ManagedMod(
                FileName: name,
                Enabled: enabled,
                Title: info?.DisplayTitle ?? TrimDisabledSuffix(name),
                Version: info?.Version ?? "",
                Loader: DetectLoader(info, name),
                SizeBytes: size,
                Error: info is null ? error : null));
        }

        return result;
    }

    public void SetEnabled(string modsDir, string fileName, bool enabled)
    {
        var current = ResolveModPath(modsDir, fileName);
        var name = Path.GetFileName(current);
        var isDisabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

        if (enabled == !isDisabled) return; // már a kívánt állapotban van

        string targetName = enabled
            ? TrimDisabledSuffix(name)
            : name + ".disabled";

        var target = PathSecurity.ResolveInsideRoot(modsDir, targetName);
        if (File.Exists(target))
            throw new IOException($"Már létezik ilyen fájl: {targetName}");

        File.Move(current, target);
        LauncherLogger.Info($"Mod {(enabled ? "bekapcsolva" : "kikapcsolva")}: {name} → {targetName}");
    }

    /// <returns>A telepített fájl neve a mods mappában.</returns>
    public string AddMod(string modsDir, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("A forrásfájl nem létezik.", sourcePath);

        var sourceName = Path.GetFileName(sourcePath);
        if (!sourceName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Csak .jar fájl adható hozzá modként.");

        var size = new FileInfo(sourcePath).Length;
        if (size <= 0 || size > MaxModBytes)
            throw new IOException($"A mod fájlméret érvénytelen (max. {MaxModBytes / 1024 / 1024} MB).");

        Directory.CreateDirectory(modsDir);
        var targetName = UniqueFileName(modsDir, sourceName);
        var target = PathSecurity.ResolveInsideRoot(modsDir, targetName);
        File.Copy(sourcePath, target);
        LauncherLogger.Info($"Mod hozzáadva: {targetName}");
        return targetName;
    }

    public void RemoveMod(string modsDir, string fileName)
    {
        var path = ResolveModPath(modsDir, fileName);
        PathSecurity.SafeDeleteFile(modsDir, path);
        LauncherLogger.Info($"Mod törölve: {Path.GetFileName(path)}");
    }

    // -------------------------------------------------------- resource packek

    public IReadOnlyList<ManagedPack> ListResourcePacks(string gameDir)
    {
        var result = new List<ManagedPack>();
        var packsDir = PackRoot(gameDir);
        if (!Directory.Exists(packsDir)) return result;

        var active = GetActivePackNames(gameDir);

        string[] files;
        try
        {
            files = Directory.GetFiles(packsDir, "*.zip");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Resource pack lista olvasási hiba ({packsDir}): {ex.Message}");
            return result;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

            long size;
            try { size = new FileInfo(file).Length; } catch { size = 0; }

            var (description, packFormat) = TryReadPackMeta(file);
            result.Add(new ManagedPack(
                FileName: name,
                Active: active.Contains("file/" + name),
                Description: description,
                PackFormat: packFormat,
                SizeBytes: size));
        }

        return result;
    }

    /// <returns>A telepített fájl neve a resourcepacks mappában.</returns>
    public string AddResourcePack(string gameDir, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("A forrásfájl nem létezik.", sourcePath);

        var sourceName = Path.GetFileName(sourcePath);
        if (!sourceName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Csak .zip fájl adható hozzá resource packként.");

        var size = new FileInfo(sourcePath).Length;
        if (size <= 0 || size > MaxPackBytes)
            throw new IOException($"A pack fájlméret érvénytelen (max. {MaxPackBytes / 1024 / 1024} MB).");

        var packsDir = PackRoot(gameDir);
        Directory.CreateDirectory(packsDir);
        var targetName = UniqueFileName(packsDir, sourceName);
        var target = PathSecurity.ResolveInsideRoot(packsDir, targetName);
        File.Copy(sourcePath, target);
        LauncherLogger.Info($"Resource pack hozzáadva: {targetName}");
        return targetName;
    }

    public void RemoveResourcePack(string gameDir, string fileName)
    {
        var packsDir = PackRoot(gameDir);
        var path = ResolvePackPath(packsDir, fileName);
        PathSecurity.SafeDeleteFile(packsDir, path);
        // Ha aktív volt, vegyük ki az options.txt-ből is, hogy ne hivatkozzon törölt packre.
        SetResourcePackActive(gameDir, Path.GetFileName(path), false);
        LauncherLogger.Info($"Resource pack törölve: {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Resource pack ki/bekapcsolása az <c>options.txt</c> <c>resourcePacks:[...]</c>
    /// sorának biztonságos átírásával. Minden más sort változatlanul hagy,
    /// a mentés <see cref="AtomicFile"/>-lal történik. Ha az options.txt még nem
    /// létezik (a játék sosem indult), csak bekapcsoláskor hozzuk létre.
    /// </summary>
    public void SetResourcePackActive(string gameDir, string fileName, bool active)
    {
        var entry = "file/" + fileName;
        var optionsPath = Path.Combine(gameDir, "options.txt");
        var current = GetActivePackNames(gameDir);

        var want = new List<string>(current);
        if (active && !want.Contains(entry)) want.Add(entry);
        if (!active) want.RemoveAll(x => x == entry);
        if (want.SequenceEqual(current)) return;

        var lines = new List<string>();
        if (File.Exists(optionsPath))
        {
            try { lines.AddRange(File.ReadAllLines(optionsPath)); }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"options.txt olvasási hiba: {ex.Message}");
                return;
            }
        }
        else if (!active)
        {
            return; // nincs fájl és kikapcsolnánk — nincs teendő
        }

        var replacement = "resourcePacks:[\"" + string.Join("\",\"", want) + "\"]";
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("resourcePacks:", StringComparison.Ordinal))
            {
                lines[i] = replacement;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(replacement);

        var write = AtomicFile.TryWriteAllText(optionsPath, string.Join("\n", lines) + "\n");
        if (!write.Success)
            throw new IOException("options.txt mentése nem sikerült: " + write.Error);
        LauncherLogger.Info($"Resource pack {(active ? "bekapcsolva" : "kikapcsolva")}: {fileName}");
    }

    /// <summary>Az options.txt-ben aktívnak jelölt packek (<c>file/...</c> alakban).</summary>
    public HashSet<string> GetActivePackNames(string gameDir)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var optionsPath = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(optionsPath)) return result;

        try
        {
            foreach (var line in File.ReadLines(optionsPath))
            {
                if (!line.StartsWith("resourcePacks:", StringComparison.Ordinal)) continue;
                var start = line.IndexOf('[');
                var end = line.LastIndexOf(']');
                if (start < 0 || end <= start) continue;
                foreach (var part in line.Substring(start + 1, end - start - 1).Split(','))
                {
                    var name = part.Trim().Trim('"');
                    if (name.Length > 0) result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"options.txt feldolgozási hiba: {ex.Message}");
        }

        return result;
    }

    // ------------------------------------------------------------------ segédek

    private static string PackRoot(string gameDir) => Path.Combine(gameDir, "resourcepacks");

    private static bool IsModFileName(string name)
        => name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
           name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static string TrimDisabledSuffix(string name)
        => name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;

    private static string ResolveModPath(string modsDir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Üres mod fájlnév.");
        var path = PathSecurity.ResolveInsideRoot(modsDir, Path.GetFileName(fileName));
        if (!IsModFileName(Path.GetFileName(path)))
            throw new ArgumentException("Csak .jar / .jar.disabled kezelhető.");
        if (!File.Exists(path))
            throw new FileNotFoundException("A mod nem található.", path);
        return path;
    }

    private static string ResolvePackPath(string packsDir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Üres pack fájlnév.");
        var path = PathSecurity.ResolveInsideRoot(packsDir, Path.GetFileName(fileName));
        if (!Path.GetFileName(path).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Csak .zip kezelhető.");
        if (!File.Exists(path))
            throw new FileNotFoundException("A pack nem található.", path);
        return path;
    }

    private static string UniqueFileName(string dir, string wanted)
    {
        var baseName = Path.GetFileNameWithoutExtension(wanted);
        var ext = Path.GetExtension(wanted);
        var candidate = wanted;
        for (var i = 2; i < 1000 && File.Exists(Path.Combine(dir, candidate)); i++)
            candidate = $"{baseName} ({i}){ext}";
        if (File.Exists(Path.Combine(dir, candidate)))
            candidate = $"{baseName} {Guid.NewGuid():N}{ext}";
        return candidate;
    }

    private static string DetectLoader(ModFileInfo? info, string fileName)
    {
        if (info is null) return "ismeretlen";
        if (!string.IsNullOrWhiteSpace(info.Environment)) return "fabric";
        if (info.RequiresFabricLoader) return "fabric";
        if (!string.IsNullOrWhiteSpace(info.Id)) return "forge";
        return "ismeretlen";
    }

    private static (string Description, int? PackFormat) TryReadPackMeta(string zipPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length is <= 0 or > 64 * 1024) return ("", null);

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var description = "";
            if (root.TryGetProperty("pack", out var pack))
            {
                if (pack.TryGetProperty("description", out var desc))
                    // A leírás lehet string, de akár chat-komponens objektum is
                    // ({"text":"..."} / {"translate":"..."} + extra). Az utóbbi
                    // esetben a ToString() nyers JSON-t adna a felületre —
                    // ezért lapítjuk (lásd DisplayText).
                    description = Text.DisplayText.FlattenChatComponent(desc);
            }

            int? format = null;
            if (pack.ValueKind == System.Text.Json.JsonValueKind.Object &&
                pack.TryGetProperty("pack_format", out var fmt) &&
                fmt.TryGetInt32(out var f))
                format = f;

            return (description.Trim(), format);
        }
        catch
        {
            return ("", null);
        }
    }
}

/// <summary>Egy felismert modfájl a mods mappában.</summary>
public sealed record ManagedMod(
    string FileName,
    bool Enabled,
    string Title,
    string Version,
    string Loader,
    long SizeBytes,
    string? Error);

/// <summary>Egy felismert resource pack a resourcepacks mappában.</summary>
public sealed record ManagedPack(
    string FileName,
    bool Active,
    string Description,
    int? PackFormat,
    long SizeBytes);
