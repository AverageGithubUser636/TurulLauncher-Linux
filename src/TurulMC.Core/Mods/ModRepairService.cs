using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Security;

namespace TurulMC.Core.Mods;

/// <summary>
/// Smart Repair fájlműveletek: mentés + javítás (tiltás/karantén).
/// A vizsgálatot a <see cref="ModCompatibilityScanner"/> végzi; ez az
/// osztály a kiválasztott problémás fájlok sorsát intézi:
/// <list type="bullet">
/// <item>mentés: a teljes <c>mods</c> mappa másolata
/// <c>mods-backup/&lt;dátum&gt;</c> alá,</item>
/// <item><c>disable</c>: <c>.jar.disabled</c> átnevezés,</item>
/// <item><c>quarantine</c>: átmozgatás <c>mods-quarantine/&lt;dátum&gt;</c> alá
/// <c>quarantine.json</c> leltárral (visszaállítható).</item>
/// </list>
/// </summary>
public sealed class ModRepairService
{
    /// <summary>A mods mappa mentése időbélyeges könyvtárba.</summary>
    /// <returns>A backup könyvtár útvonala.</returns>
    public async Task<string> BackupModsAsync(
        string modsDir,
        IProgress<(string Text, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
            throw new DirectoryNotFoundException("A mods mappa nem található: " + modsDir);

        var backupDir = Path.Combine(
            Path.GetDirectoryName(modsDir) ?? modsDir,
            "mods-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);

        var files = Directory.GetFiles(modsDir, "*", SearchOption.TopDirectoryOnly);
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(files[i]);
            progress?.Report(($"Mentés ({i + 1}/{files.Length}): {name}",
                files.Length == 0 ? 100 : (i * 100.0 / files.Length)));
            var dest = PathSecurity.ResolveInsideRoot(backupDir, name);
            using var src = File.OpenRead(files[i]);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(("Kész.", 100));
        LauncherLogger.Info($"Mod backup készítve: {backupDir} ({files.Length} fájl)");
        return backupDir;
    }

    /// <summary>
    /// Kiválasztott fájlok javítása. Ismeretlen fixMode esetén kivételt dob
    /// (nem „csendben semmit").
    /// </summary>
    /// <returns>A javított fájlok száma.</returns>
    public async Task<int> ApplyFixAsync(
        string modsDir,
        IEnumerable<string> fileNames,
        string fixMode,
        IProgress<(string Text, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (fixMode is not ("disable" or "quarantine"))
            throw new ArgumentException("Ismeretlen javítási mód (disable/quarantine).", nameof(fixMode));
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
            throw new DirectoryNotFoundException("A mods mappa nem található: " + modsDir);

        var files = (fileNames ?? Enumerable.Empty<string>())
            .Select(f => Path.GetFileName(f ?? ""))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return 0;

        string? quarantineDir = null;
        if (fixMode == "quarantine")
        {
            quarantineDir = Path.Combine(
                Path.GetDirectoryName(modsDir) ?? modsDir,
                "mods-quarantine", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(quarantineDir);
        }

        var done = 0;
        var moved = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = files[i];
            progress?.Report(($"Javítás ({i + 1}/{files.Count}): {name}",
                files.Count == 0 ? 100 : (i * 100.0 / files.Count)));

            string source;
            try
            {
                source = PathSecurity.ResolveInsideRoot(modsDir, name);
            }
            catch
            {
                continue; // traversal-gyanús név: kihagyjuk, nem dobunk
            }
            if (!File.Exists(source)) continue;

            if (fixMode == "disable")
            {
                if (source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    done++;
                    continue;
                }
                var target = source + ".disabled";
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
                done++;
            }
            else
            {
                var dest = PathSecurity.ResolveInsideRoot(quarantineDir!, name);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(source, dest);
                moved.Add(name);
                done++;
            }
        }

        if (fixMode == "quarantine" && quarantineDir is not null)
        {
            var inventory = new
            {
                date = DateTime.UtcNow,
                mode = "quarantine",
                files = moved
            };
            await File.WriteAllTextAsync(
                Path.Combine(quarantineDir, "quarantine.json"),
                JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(("Kész.", 100));
        LauncherLogger.Info($"Smart Repair: {done} fájl javítva ({fixMode})");
        return done;
    }
}
