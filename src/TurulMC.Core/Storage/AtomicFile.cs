using System.Text;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Storage;

/// <summary>Egy atomi írás eredménye (soha nem dob kivételt).</summary>
public sealed record AtomicWriteResult(bool Success, string Error = "", bool UsedFallback = false)
{
    public static readonly AtomicWriteResult Ok = new(true);
}

/// <summary>
/// Tartós, öngyógyító JSON/fájl írás a launcher adatkönyvtárába.
///
/// Miért kell: a launcher régebbi kódja fix nevű <c>*.tmp</c> fájlt írt felül
/// (pl. <c>instances.json.tmp</c>). Ha az a fájl írásvédett, más folyamat épp törli
/// ("delete pending" → ERROR_ACCESS_DENIED), vírusirtó blokkolja, vagy véletlenül
/// mappa áll a helyén, a <see cref="File.WriteAllText(string,string)"/> kivételt dob —
/// és ha ez indulás közben történik, a launcher el sem indul.
///
/// Ez az író ezért:
/// <list type="bullet">
/// <item>egyedi temp fájlnevet használ (nincs ütközés párhuzamos mentésekkel),</item>
/// <item>előbb eltakarítja az elárvult fix nevű temp fájlt (attribútum + mappa eset is),</item>
/// <item>leválasztja a csak olvasható attribútumot a célfájlról mentés előtt,</item>
/// <item>mentés előtt biztonsági másolatot készít (<c>.bak</c>),</item>
/// <item>hosszabb útvonalon is működik (\\?\ előtag),</item>
/// <item>és <see cref="TryWriteAllText"/> esetén hiba helyett eredményt ad vissza.</item>
/// </list>
/// </summary>
public static class AtomicFile
{
    private const int MaxAttempts = 6;

    /// <summary>
    /// Útvonalankénti zár: ugyanazt a fájlt egyszerre író hívók sorba állnak. Enélkül a
    /// párhuzamos csere (Move) időnként "Access denied"-be fut, mert a célfájl épp
    /// átnevezés/törlés alatt van.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static object LockFor(string fullPath)
        => PathLocks.GetOrAdd(fullPath, _ => new object());

    /// <summary>Atomi írás; hiba esetén kivételt dob (a hívó kezeli).</summary>
    public static void WriteAllText(string path, string content, bool createBackup = true)
    {
        var result = TryWriteAllText(path, content, createBackup);
        if (!result.Success)
            throw new IOException(result.Error);
    }

    /// <summary>
    /// Atomi írás, amely soha nem dob: az eredmény tartalmazza a sikert és a magyar
    /// nyelvű hibaokot. Így egy mentési hiba nem dönti be a launchert.
    /// </summary>
    public static AtomicWriteResult TryWriteAllText(string path, string content, bool createBackup = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new AtomicWriteResult(false, "Üres fájlútvonal.");

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) { return new AtomicWriteResult(false, "Érvénytelen útvonal: " + ex.Message); }

        lock (LockFor(fullPath))
        {
            return WriteCore(fullPath, content, createBackup);
        }
    }

    private static AtomicWriteResult WriteCore(string fullPath, string content, bool createBackup)
    {
        string? temp = null;
        var usedFallback = false;
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            ClearReadOnly(fullPath);
            RemoveStaleTemp(fullPath);

            // Egyedi temp név: két párhuzamos mentés nem írja felül egymást.
            temp = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            WriteTextFile(temp, content);

            if (createBackup && File.Exists(fullPath))
            {
                try
                {
                    File.Copy(fullPath, fullPath + ".bak", true);
                }
                catch (Exception ex)
                {
                    // A biztonsági másolat hiánya nem teheti használhatatlanná a mentést.
                    LauncherLogger.Warning($"Biztonsági másolat nem készült ({Path.GetFileName(fullPath)}): {ex.Message}");
                }
            }

            MoveIntoPlace(temp, fullPath);
            temp = null;
            return usedFallback ? new AtomicWriteResult(true, "", true) : AtomicWriteResult.Ok;
        }
        catch (Exception firstError)
        {
            // 1. tartalék: a célfájl törlése után áthelyezés (a .bak megvan, adat nem vész el).
            try
            {
                if (temp is not null && File.Exists(temp))
                {
                    ClearReadOnly(fullPath);
                    try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
                    MoveIntoPlace(temp, fullPath);
                    temp = null;
                    usedFallback = true;
                    LauncherLogger.Warning($"A mentés csak tartalék útvonalon sikerült ({Path.GetFileName(fullPath)}): {firstError.Message}");
                    return new AtomicWriteResult(true, "", true);
                }
            }
            catch (Exception secondError)
            {
                firstError = secondError;
            }

            // 2. tartalék: KÖZVETLEN írás a célfájlba. Egyes vírusirtók/Controlled Folder
            //    Access beállítások az új temp fájlok létrehozását blokkolják, a már létező,
            //    ismert fájl felülírását viszont engedik. A biztonsági másolat ilyenkor is
            //    elkészül, ezért adat nem vész el.
            try
            {
                if (createBackup && File.Exists(fullPath))
                {
                    try { File.Copy(fullPath, fullPath + ".bak", true); } catch { }
                }

                ClearReadOnly(fullPath);
                using (var stream = new FileStream(ToExtendedPath(fullPath), FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                LauncherLogger.Warning(
                    $"A mentés közvetlenül a célfájlba történt ({Path.GetFileName(fullPath)}) — " +
                    $"a temp fájl írását valószínűleg egy vírusirtó blokkolja: {firstError.Message}");
                return new AtomicWriteResult(true, "", true);
            }
            catch (Exception directError)
            {
                LauncherLogger.Error($"Mentés sikertelen: {Path.GetFileName(fullPath)}", directError);
                return new AtomicWriteResult(false,
                    $"A(z) {Path.GetFileName(fullPath)} nem menthető: {directError.Message}");
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp))
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>Csak olvasható attribútum leválasztása (ha van).</summary>
    public static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                LauncherLogger.Info($"Írásvédett attribútum leválasztva: {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Attribútum módosítás sikertelen ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

    /// <summary>
    /// Elárvult, fix nevű temp fájl (<c>fájl.tmp</c>) eltakarítása. Ha véletlenül mappa áll
    /// a helyén, azt is eltávolítjuk — az soha nem lehet valódi launcher-adat.
    /// </summary>
    public static void RemoveStaleTemp(string path)
    {
        var legacyTemp = path + ".tmp";
        try
        {
            if (Directory.Exists(legacyTemp))
            {
                Directory.Delete(legacyTemp, true);
                LauncherLogger.Warning($"Elárvult temp mappa törölve: {Path.GetFileName(legacyTemp)}");
                return;
            }

            if (!File.Exists(legacyTemp)) return;

            // Friss (kevesebb mint 2 perces) temp fájlhoz nem nyúlunk: lehet, hogy egy
            // másik folyamat éppen írja.
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(legacyTemp);
            if (age < TimeSpan.FromMinutes(2)) return;

            ClearReadOnly(legacyTemp);
            File.Delete(legacyTemp);
            LauncherLogger.Info($"Elárvult temp fájl törölve: {Path.GetFileName(legacyTemp)}");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Elárvult temp nem törölhető ({Path.GetFileName(legacyTemp)}): {ex.Message}");
        }
    }

    /// <summary>
    /// Induláskori öngyógyítás: a launcher adatkönyvtárában maradt elárvult temp fájlok
    /// eltakarítása. Nem dob, és nem nyúl a friss fájlokhoz.
    /// </summary>
    public static int CleanupStaleTemps(string directory)
    {
        var removed = 0;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

        try
        {
            foreach (var candidate in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(candidate);
                var isTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
                if (!isTemp) continue;

                try
                {
                    if (File.Exists(candidate))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) < TimeSpan.FromMinutes(2)) continue;
                        ClearReadOnly(candidate);
                        File.Delete(candidate);
                        removed++;
                    }
                    else if (Directory.Exists(candidate))
                    {
                        Directory.Delete(candidate, true);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Temp takarítás kihagyva ({name}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Temp takarítás sikertelen ({directory}): {ex.Message}");
        }

        return removed;
    }

    private static void WriteTextFile(string path, string content)
    {
        // Hosszú útvonal (\\?\): a %APPDATA% és a felhasználói nevek együtt átléphetik a
        // 260 karaktert, ami némely gépen "Access denied"-ként jelenik meg.
        using var stream = new FileStream(ToExtendedPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true); // a tartalom a lemezre kerüljön a csere előtt
    }

    private static void MoveIntoPlace(string temp, string destination)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                File.Move(temp, destination, true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Vírusirtó/indexelő rövid ideig foghatja a célfájlt: rövid várakozás után újra.
                last = ex;
                ClearReadOnly(destination);
                Thread.Sleep(30 * attempt + Random.Shared.Next(0, 25));
            }
        }

        throw last ?? new IOException($"A fájl nem helyezhető a helyére: {destination}");
    }

    private static string ToExtendedPath(string path)
        => OperatingSystem.IsWindows() && path.Length > 200 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\" + path
            : path;
}
