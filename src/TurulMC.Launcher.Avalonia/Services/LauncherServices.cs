using TurulMC.Core.Authentication;
using TurulMC.Core.Diagnostics;
using TurulMC.Core.Java;
using TurulMC.Core.Minecraft;
using TurulMC.Core.Models;
using TurulMC.Core.Modpacks;
using TurulMC.Core.Networking;
using TurulMC.Core.Storage;
using TurulMC.Infrastructure.Authentication;
using TurulMC.Infrastructure.FileSystem;

namespace TurulMC.Launcher.Avalonia.Services;

/// <summary>Indítás folyamatjelzés a nézetek felé (szöveg + százalék).</summary>
public sealed record LaunchProgress(string Text, double Percent);

/// <summary>
/// A szolgáltatások összeállítása. A WinUI alkalmazásban ez a MainWindow
/// konstruktorában történt; itt egy helyen van, hogy a nézetek ugyanazokat
/// a példányokat kapják (nincs DI konténer — a launcher méretű projektnél
/// felesleges, ez a projekt eddigi döntése is).
/// </summary>
public sealed class LauncherServices
{
    private static LauncherServices? _current;
    public static LauncherServices Current => _current ??= new LauncherServices();

    public ISettingsStorage SettingsStorage { get; } = new SettingsStorage();
    public IAuthenticationService Auth { get; } = new LocalAuthenticationService();
    public IJavaRuntimeProvisioner JavaProvisioner { get; } = new JavaRuntimeProvisioner();
    public IJavaRuntimeService Java { get; }
    public IServerStatusService ServerStatus { get; } = new ServerStatusService();
    public IFabricVersionResolver FabricVersions { get; } = new FabricVersionResolver();
    public InstanceStore Instances { get; } = new();
    public Core.Mods.ModManager Mods { get; } = new();
    public Core.Servers.ServerListStorage Servers { get; } = new();
    public Core.Mods.ModrinthClient Modrinth { get; } = new();
    public Core.Mods.ModrinthInstaller ModrinthInstall { get; } = new();
    public Core.Mods.ModIconCache Icons { get; } = new();
    public Core.Mods.ModRepairService Repairs { get; } = new();
    public Core.Modpacks.ModpackInstaller PackInstaller { get; } = new();
    public Core.Update.UpdateService Updates { get; } = new();
    public Core.Minecraft.MinecraftVersionCatalog Versions { get; } = new(
        null,
        Path.Combine(Core.Storage.LauncherPaths.DataRoot, "cache", "version_manifest.json"));
    public LauncherSettings Settings { get; private set; } = new();

    private IMinecraftInstallationService? _install;
    private IFabricLoaderService? _fabric;
    private IModpackService? _modpack;
    private IMinecraftLauncherService? _launcher;
    private string? _preparedInstanceDir;

    private LauncherServices()
    {
        Java = new JavaRuntimeService(JavaProvisioner);
    }

    public async Task<LauncherSettings> LoadSettingsAsync()
    {
        try
        {
            Settings = await SettingsStorage.LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            StartupLog.WriteCrash("LoadSettingsAsync", ex);
            Settings = new LauncherSettings();
        }
        return Settings;
    }

    public Task SaveSettingsAsync() => SettingsStorage.SaveSettingsAsync(Settings);

    /// <summary>
    /// Az Instance-hoz tartozó szolgáltatások előkészítése. Ugyanaz a
    /// logika, mint a WinUI-ban: minden Instance-mappához külön
    /// install/fabric/modpack/launcher példány tartozik.
    /// </summary>
    public (IMinecraftInstallationService Install,
            IFabricLoaderService Fabric,
            IModpackService Modpack,
            IMinecraftLauncherService Launcher) PrepareInstance(string instanceDirectory)
    {
        if (_launcher is not null && string.Equals(_preparedInstanceDir, instanceDirectory, StringComparison.Ordinal))
            return (_install!, _fabric!, _modpack!, _launcher);

        _install = new MinecraftInstallationService(instanceDirectory);
        _fabric = new FabricLoaderService(instanceDirectory);
        _modpack = new ModpackService(instanceDirectory);
        _launcher = new MinecraftLauncherService(_install, Java, LastPreparedInstanceId);
        _preparedInstanceDir = instanceDirectory;
        return (_install, _fabric, _modpack, _launcher);
    }

    /// <summary>Indítás előtt beállítandó Instance-azonosító (a launcher ezt
    /// használja a kilépés/naplózás azonosítására).</summary>
    public string LastPreparedInstanceId { get; set; } = "";

    /// <summary>
    /// Az utoljára előkészített indító-szolgáltatás (a betöltőfigyelő
    /// eseményeihez: <c>ProcessOutput</c>, <c>MinecraftExited</c>).
    /// </summary>
    public IMinecraftLauncherService? CurrentLauncher => _launcher;

    /// <summary>
    /// Egy Instance elindítása a Home-nézetből és a Szerverek-nézetből is.
    /// A teljes folyamat (profil, letöltés, Fabric, Java, indítás) egy helyen,
    /// a folyamatjelzés <see cref="LaunchProgress"/>-en keresztül megy.
    /// Visszaadja a megjelenítendő záró státuszszöveget.
    /// </summary>
    /// <param name="serverHost">Ha meg van adva, <c>--server/--port</c>-tal
    /// a megadott szerverre csatlakozik (lásd a CLI <c>server.connect</c>-et).</param>
    public async Task<string> LaunchInstanceAsync(
        LauncherInstance instance,
        IProgress<LaunchProgress>? progress = null,
        string? serverHost = null,
        int serverPort = 25565)
    {
        ArgumentNullException.ThrowIfNull(instance);

        void Report(string text, double percent)
            => progress?.Report(new LaunchProgress(text, percent));

        // Profil: ha nincs, létrehozunk egy offline alapértelmezettet.
        var profile = await Auth.GetCurrentProfileAsync();
        profile ??= (await Auth.CreateLocalProfileAsync(
            string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName)).Profile;
        if (profile is null)
            throw new InvalidOperationException("Nincs létrehozható profil — hozz létre egyet a Beállításokban.");

        var version = instance.MinecraftVersion;
        var loader = instance.Loader?.ToLowerInvariant() ?? "none";
        var loaderVersion = instance.LoaderVersion;

        instance.LastUsed = DateTime.UtcNow;
        LastPreparedInstanceId = instance.Id;

        ApplyInstanceToSettings(Settings, instance, instance.LoaderVersion);

        var instanceDir = Instances.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceDir);

        // A tuple sorrendje: (Install, Fabric, Modpack, Launcher).
        var (install, fabric, _, launcher) = PrepareInstance(instanceDir);

        Report("Minecraft fájlok előkészítése…", 5);
        await install.EnsureVersionDownloadedAsync(version, MakeOverall(progress));

        if (loader == "fabric")
        {
            Report("Fabric Loader telepítése…", 90);
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                var resolved = await FabricVersions.ResolveRecommendedAsync(version);
                loaderVersion = resolved.LoaderVersion;
            }
            await fabric.InstallFabricLoaderAsync(version, loaderVersion, MakeOverall(progress));
            instance.LoaderVersion = loaderVersion;
            // A feloldott verziót is vissza kell írni (nem elég az instance
            // eredetije, ami üres is lehetett) — lásd ApplyInstanceToSettings.
            ApplyInstanceToSettings(Settings, instance, loaderVersion);
        }
        else
        {
            // Vanilla úton nem kell loader-verzió; a régit töröljük, hogy egy
            // későbbi Fabric-indítás se botoljon elavult értékbe.
            Settings.LoaderVersion = "";
        }

        Report("Indítási konfiguráció készítése…", 95);
        var config = await launcher.BuildLaunchConfigAsync(profile, Settings);
        config.InstanceId = instance.Id;

        // Instance-specifikus felülírások (4.6.0) — validáltan, ahogy a WinUI-ban.
        if (!string.IsNullOrWhiteSpace(instance.JavaPath))
        {
            Core.Validation.LauncherSettingsValidator.ValidateJavaPath(instance.JavaPath);
            config.JavaPath = instance.JavaPath;
        }

        var extraArgs = ParseJvmArgs(instance.JvmArgs);
        if (extraArgs.Length > 0) config.AdditionalJvmArgs = extraArgs.ToList();

        if (instance.WindowWidth > 0 && instance.WindowHeight > 0)
        {
            config.WindowWidth = instance.WindowWidth;
            config.WindowHeight = instance.WindowHeight;
        }

        if (!string.IsNullOrWhiteSpace(serverHost))
        {
            config.ServerIp = serverHost.Trim();
            config.ServerPort = Math.Clamp(serverPort, 1, 65535);
        }

        if (string.IsNullOrWhiteSpace(config.JavaPath) || !File.Exists(config.JavaPath))
        {
            Report($"Java {config.RequiredJavaMajor} előkészítése…", 97);
            var runtime = await Java.EnsureRuntimeAsync(
                config.RequiredJavaMajor, Settings.AutoInstallJava, MakeDownload(progress));
            config.JavaPath = runtime.Path;
            Core.Logging.LauncherLogger.Info(
                $"Java runtime kiválasztva indításhoz: {runtime.Path} (Java {runtime.Major}, forrás: {runtime.Source})");
        }

        Report(string.IsNullOrWhiteSpace(serverHost) ? "Minecraft indítása…" : $"Csatlakozás: {serverHost}…", 99);
        await launcher.LaunchAsync(config);

        await SaveSettingsAsync();

        var where = string.IsNullOrWhiteSpace(serverHost) ? "" : $" → {serverHost}";
        return $"{instance.Name} elindítva · MC {version}" +
               (loader == "fabric" ? $" · Fabric {loaderVersion}" : "") + where;
    }

    private static IProgress<Core.Models.OverallProgress> MakeOverall(IProgress<LaunchProgress>? progress)
        => new Progress<Core.Models.OverallProgress>(p =>
        {
            var task = p.CurrentTask ?? "";
            var pct = p.CurrentFile?.TotalBytes > 0
                ? Math.Clamp(p.OverallPercentage * 0.85 + p.CurrentFile.Percentage * 0.15, 5, 92)
                : Math.Clamp(p.OverallPercentage, 5, 92);
            progress?.Report(new LaunchProgress(string.IsNullOrWhiteSpace(task) ? "Letöltés…" : task, pct));
        });

    private static IProgress<Core.Models.DownloadProgress> MakeDownload(IProgress<LaunchProgress>? progress)
        => new Progress<Core.Models.DownloadProgress>(p =>
        {
            var pct = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
            progress?.Report(new LaunchProgress(string.IsNullOrWhiteSpace(p.FileName)
                ? "Java letöltése…" : $"Java letöltése: {p.FileName}", pct));
        });

    private static string[] ParseJvmArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Instance → indítási beállítások szinkronizálás. Egyetlen helyen, mert
    /// a <c>BuildLaunchConfigAsync</c> a settings-ből dolgozik: ha bármelyik
    /// mező (különösen a <c>LoaderVersion</c>) lemarad, a Fabric-indítás
    /// „nincs telepítve" hibával hal el a frissen telepített loader ellenére.
    /// Nyilvános, hogy közvetlenül tesztelhető legyen.
    /// </summary>
    /// <param name="resolvedLoaderVersion">A feloldott Fabric-verzió (lehet az
    /// instance eredetije vagy frissen ajánlott); vanilla úton figyelmen kívül.</param>
    public static void ApplyInstanceToSettings(
        LauncherSettings settings,
        LauncherInstance instance,
        string? resolvedLoaderVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instance);

        settings.MinecraftVersion = instance.MinecraftVersion;
        settings.Loader = instance.Loader?.ToLowerInvariant() ?? "none";
        if (instance.RamMb > 0) settings.DefaultRamMb = instance.RamMb;

        if (settings.Loader == "fabric")
            settings.LoaderVersion = resolvedLoaderVersion ?? "";
        else
            settings.LoaderVersion = "";
    }

    /// <summary>
    /// Másik nézet hozott létre/módosított/törölt Instance-t (pl. modpack
    /// telepítés). A feliratkozott nézetek (Játék, Modok, Textúrák) újratöltik
    /// a listájukat — a kiválasztás az aktív azonosító alapján helyreáll.
    /// </summary>
    public event Action? InstancesChanged;

    public void NotifyInstancesChanged() => InstancesChanged?.Invoke();

    /// <summary>Az utolsó frissítés-ellenőrzés eredménye (induláskori
    /// auto-check tölti; a jelvény, a banner és a Frissítés-lap ebből dolgozik).</summary>
    public Core.Update.UpdateCheckResult? LastUpdateCheck { get; set; }

    /// <summary>Lapváltási kérés nézetek között (pl. Home-banner → Frissítés-lap).</summary>
    public event Action<int>? NavigateRequested;

    public void RequestNavigate(int page) => NavigateRequested?.Invoke(page);

    /// <summary>Az induláskori frissítés-ellenőrzés lefutott (eredmény a
    /// <see cref="LastUpdateCheck"/>-ben) — a banner és a jelvény frissíthet.</summary>
    public event Action? UpdateCheckCompleted;

    internal void NotifyUpdateCheckCompleted() => UpdateCheckCompleted?.Invoke();

    public DoctorOptions BuildDoctorOptions(string? configuredJavaPath, string? instanceDirectory, string? serverHost, int serverPort)
        => new()
        {
            LauncherVersion = AppInfo.Version,
            DataRoot = LauncherPaths.DataRoot,
            SettingsFilePath = Path.Combine(LauncherPaths.DataRoot, "settings.json"),
            InstancesFilePath = LauncherPaths.InstancesFile,
            ProfilesFilePath = Path.Combine(LauncherPaths.DataRoot, "profiles.json"),
            LogsDirectory = LauncherPaths.LogsRoot,
            InstanceDirectory = instanceDirectory,
            GameDirectory = instanceDirectory,
            ConfiguredJavaPath = configuredJavaPath,
            RequiredJavaMajor = Core.Java.JavaVersionMap.ResolveRequiredMajor(Settings.MinecraftVersion),
            ServerHost = string.IsNullOrWhiteSpace(serverHost) ? null : serverHost,
            ServerPort = serverPort
        };
}
