namespace TurulMC.Core.Platform;

/// <summary>
/// A futó platform leírása a Minecraft verzió-JSON szabályaihoz.
///
/// A Mojang verzió-JSON <c>os.name</c> értékei: <c>windows</c>, <c>linux</c>, <c>osx</c>.
/// Korábban mindenhol a <c>windows</c> volt beégetve (a launcher Windows-ra készült),
/// ami miatt Linuxon rossz könyvtárak és rossz natives-ek kerültek volna be.
/// Ezt az egy osztályt használja a telepítő és az indító is, hogy ne szóródjon szét.
/// </summary>
public static class LauncherPlatform
{
    /// <summary>A Mojang verzió-JSON <c>os.name</c> értéke a futó platformhoz.</summary>
    public static string MinecraftOsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" :
        "linux";

    /// <summary>
    /// A natives klasszifikátorban lévő <c>${arch}</c> feloldása: 64 vagy 32.
    /// </summary>
    public static string NativesArch => Environment.Is64BitOperatingSystem ? "64" : "32";

    /// <summary>
    /// A verzió-JSON szabályaiban használt architektúra név.
    /// A Mojang itt csak az <c>x86</c> értéket használja (a 64 bites nem jelenik meg).
    /// </summary>
    public static string RuleArchitecture => Environment.Is64BitProcess ? "x86_64" : "x86";

    /// <summary>
    /// Igaz, ha a megadott <c>os.name</c> a futó platformra illik.
    /// Üres/nincs megadva = minden platformra érvényes.
    /// </summary>
    public static bool OsMatches(string? ruleOsName)
        => string.IsNullOrWhiteSpace(ruleOsName) ||
           ruleOsName.Equals(MinecraftOsName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Igaz, ha a verzió-JSON szabályaiban megadott architektúra illik a futó platformhoz.
    /// </summary>
    public static bool ArchitectureMatches(string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return true;

        if (wanted.Equals(RuleArchitecture, StringComparison.OrdinalIgnoreCase)) return true;

        // A "x86" szabály a 32 bites folyamatra vonatkozik; a Mojang JSON-ban
        // másképp nem jelenik meg, ezért a visszafelé egyezést is elfogadjuk.
        if (wanted.Equals("x86", StringComparison.OrdinalIgnoreCase))
            return !Environment.Is64BitProcess;

        if (wanted.Equals("x86_64", StringComparison.OrdinalIgnoreCase))
            return Environment.Is64BitProcess;

        return false;
    }
}
