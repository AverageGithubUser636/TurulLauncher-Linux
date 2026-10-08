namespace TurulMC.Core.Storage;

/// <summary>
/// A launcher adatkönyvtárának feloldása. Az alapértelmezett hely a
/// <c>%APPDATA%\TurulMC</c>, de ha az nem írható (vírusirtó, Windows
/// Controlled Folder Access, jogosultság vagy tele lévő lemez), akkor a launcher
/// automatikusan más, írható helyre vált — így az Instance-ok, a beállítások és a
/// profilok mentése nem hasal el, és a játék indítása sem törik meg.
///
/// Sorrend: <c>%APPDATA%\TurulMC</c> → <c>%LOCALAPPDATA%\TurulMC</c> →
/// <c>%USERPROFILE%\TurulMC</c> → az exe melletti <c>data</c> mappa.
/// </summary>
public static class LauncherPaths
{
    private static readonly object Sync = new();
    private static string? _dataRoot;
    private static string? _defaultRoot;
    private static string? _warning;

    /// <summary>Az alapértelmezett (megszokott) adatkönyvtár.</summary>
    /// <remarks>
    /// Linuxon az XDG Base Directory szabályt követjük: az adat (Instance-ok,
    /// világok, modok — akár több GB) a <c>$XDG_DATA_HOME/TurulMC</c> alá kerül,
    /// mert a <c>$XDG_CONFIG_HOME</c> (alapból <c>~/.config</c>) kizárólag
    /// konfigurációnak való. Windowson minden a megszokott
    /// <c>%APPDATA%\TurulMC</c> helyen marad.
    /// </remarks>
    public static string DefaultDataRoot => _defaultRoot ??= ResolveDefaultRoot();

    private static string ResolveDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurulMC");

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgData) || !Path.IsPathRooted(xdgData))
            xdgData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(xdgData, "TurulMC");
    }

    /// <summary>A ténylegesen használt, írhatónak bizonyult adatkönyvtár.</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is not null) return _dataRoot;
            lock (Sync)
            {
                if (_dataRoot is not null) return _dataRoot;

                var candidates = new List<string> { DefaultDataRoot };
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                    candidates.Add(Path.Combine(localAppData, "TurulMC"));
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                    candidates.Add(Path.Combine(userProfile, "TurulMC"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));

                foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsWritable(candidate)) continue;
                    _dataRoot = candidate;
                    if (!candidate.Equals(DefaultDataRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // A meglévő beállítások/Instance-lista átmásolása, hogy a váltás ne
                        // tűnjön adatvesztésnek (a nagy Instance-tartalom a helyén marad).
                        var copied = MirrorConfigFromDefault(DefaultDataRoot, candidate);
                        _warning = "A megszokott launcher-mappa (%APPDATA%\\TurulMC) nem írható, " +
                                   "ezért a launcher innen dolgozik: " + candidate +
                                   (copied > 0 ? $" ({copied} beállításfájl átmásolva)" : "");
                    }

                    return candidate;
                }

                // Ha semmi nem írható, marad az alapértelmezett: a hibát a mentés jelzi.
                _dataRoot = DefaultDataRoot;
                _warning = "A launcher egyetlen adatkönyvtára sem írható. Ellenőrizd a vírusirtót " +
                           "(Controlled Folder Access) és a lemez szabad helyét!";
                return _dataRoot;
            }
        }
    }

    /// <summary>Figyelmeztetés, ha nem a megszokott mappát használjuk (null = minden rendben).</summary>
    public static string? DataRootWarning => _warning;

    public static string InstancesFile => Path.Combine(DataRoot, "instances.json");

    public static string InstancesRoot => Path.Combine(DataRoot, "instances");

    public static string LogsRoot => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// Írhatósági próba: létrehozunk (majd törlünk) egy apró fájlt. Nem csak az ACL-t
    /// nézzük, mert a blokkolást leggyakrabban egy vírusirtó szűrő végzi.
    /// </summary>
    public static bool IsWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var probe = Path.Combine(directory, ".turul-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x54);
                stream.Flush(true);
            }

            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            return false;
        }
    }

    /// <summary>Az Instance-tartalom megszokott gyökere (a nagy Minecraft-telepítéseké).</summary>
    public static string DefaultInstancesRoot => Path.Combine(DefaultDataRoot, "instances");

    /// <summary>
    /// A kis méretű konfigurációs fájlok átmásolása a megszokott mappából az új, írható
    /// mappába (instances.json, settings.json, profiles.json, servers.json + .bak).
    /// A nagy Instance-tartalmat szándékosan nem mozgatjuk.
    /// </summary>
    private static int MirrorConfigFromDefault(string from, string to)
    {
        var copied = 0;
        foreach (var name in new[] { "instances.json", "instances.json.bak", "settings.json", "settings.json.bak",
                                     "profiles.json", "profiles.json.bak", "servers.json", "servers.json.bak" })
        {
            try
            {
                var source = Path.Combine(from, name);
                var destination = Path.Combine(to, name);
                if (!File.Exists(source) || File.Exists(destination)) continue;
                Directory.CreateDirectory(to);
                File.Copy(source, destination, false);
                copied++;
            }
            catch
            {
                // A másolás hibája nem akadályozhatja a launcher indulását.
            }
        }

        return copied;
    }

    /// <summary>Teszteléshez: az újrapróbálkozás kényszerítése.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _dataRoot = null;
            _warning = null;
        }
    }
}
