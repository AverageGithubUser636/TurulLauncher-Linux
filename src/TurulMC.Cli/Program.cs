using System.Diagnostics;
using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Networking;
using TurulMC.Core.Minecraft;
using TurulMC.Core.Java;
using TurulMC.Infrastructure.Authentication;
using TurulMC.Infrastructure.FileSystem;

const string Version = "4.6.0";

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TurulMC");

var instancesFile = Path.Combine(appData, "instances.json");
var profilesFile = Path.Combine(appData, "profiles.json");

Directory.CreateDirectory(appData);

(string ActiveId, List<LauncherInstance> Instances) LoadInstances()
{
    if (!File.Exists(instancesFile))
        return ("", new());

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(instancesFile));
        var root = doc.RootElement;
        var active = root.TryGetProperty("activeInstanceId", out var a)
            ? a.GetString() ?? ""
            : "";

        var list = root.TryGetProperty("instances", out var el)
            ? JsonSerializer.Deserialize<List<LauncherInstance>>(el.GetRawText()) ?? new()
            : new();

        return (active, list);
    }
    catch
    {
        return ("", new());
    }
}

void SaveInstances(List<LauncherInstance> list, string activeId = "")
{
    if (list.Count == 0)
    {
        if (File.Exists(instancesFile))
            File.Delete(instancesFile);
        return;
    }

    if (string.IsNullOrWhiteSpace(activeId) ||
        !list.Any(x => x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase)))
    {
        activeId = list.OrderByDescending(x => x.LastUsed).First().Id;
    }

    File.WriteAllText(
        instancesFile,
        JsonSerializer.Serialize(
            new { activeInstanceId = activeId, instances = list },
            new JsonSerializerOptions { WriteIndented = true }));
}

LauncherInstance? FindInstance(IEnumerable<LauncherInstance> list, string key)
{
    key = key.Trim();
    return list.FirstOrDefault(x =>
        x.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
        x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
}

void Header()
{
    Console.WriteLine($"TurulLauncher CLI {Version}");
    Console.WriteLine("-----------------------");
}

void Help()
{
    Header();
    Console.WriteLine("Instance:");
    Console.WriteLine("  instances | list                  Instance-ek listázása");
    Console.WriteLine("  show <név|id>                     Instance részletei");
    Console.WriteLine("  active                             Aktív Instance");
    Console.WriteLine("  play [név|id]                     Minecraft indítása (Tab választó)");
    Console.WriteLine("  select <név|id>                   Aktív Instance kiválasztása");
    Console.WriteLine("  create <név> [mc] [loader] [ram]  Instance létrehozása");
    Console.WriteLine("  rename <név|id> <új név>          Instance átnevezése");
    Console.WriteLine("  set-version <név|id> <verzió>     Minecraft-verzió módosítása");
    Console.WriteLine("  set-loader <név|id> <loader>      Loader: none | fabric");
    Console.WriteLine("  set-ram <név|id> <GB>             RAM módosítása");
    Console.WriteLine("  delete <név|id>                    Instance törlése");
    Console.WriteLine("  clean                              Configok javítása/takarítása");
    Console.WriteLine("  reset                              Minden Instance config törlése");
    Console.WriteLine();
    Console.WriteLine("Egyéb:");
    Console.WriteLine("  profiles                           Játékosprofilok listázása");
    Console.WriteLine("  server                             play.turulnetwork.hu státusz");
    Console.WriteLine("  servers.list                       Mentett szerverek");
    Console.WriteLine("  servers.add <név> <host> [port]    Szerver mentése");
    Console.WriteLine("  servers.remove <név|id>            Szerver törlése");
    Console.WriteLine("  server.connect [név|id|host] [port] Csatlakozás (--server/--port átmegy)");
    Console.WriteLine("  manifest-hash <manifest.json>      Manifest SHA256");
    Console.WriteLine("  modpack.export <dir> <out.mrpack>  Export (kötelező hash)");
    Console.WriteLine("  modpack.import <in.mrpack> <dir>   Import (zip-slip guard)");
    Console.WriteLine("  doctor                             Teljes diagnosztika (Java, instance, hálózat, logok)");
    Console.WriteLine("  support                            Support ZIP (logok + crash reportok + Doctor)");
    Console.WriteLine("  java list                          Java runtime-ok (rendszer + telepített)");
    Console.WriteLine("  java install [major]               Java telepítése (Eclipse Temurin JRE, SHA-256)");
    Console.WriteLine("  java remove <id>                   Launcher által telepített Java törlése");
    Console.WriteLine("  config                             Config útvonalak");
    Console.WriteLine("  gui                                Grafikus launcher indítása");
    Console.WriteLine("  version                            Verzió");
    Console.WriteLine("  help                               Súgó");
    Console.WriteLine();
    Console.WriteLine("Példa:");
    Console.WriteLine("  TurulLauncher.CLI.exe create Fabric 26.1.2 fabric 4");
}

(int Removed, int Fixed, List<LauncherInstance> Cleaned) CleanInstances(List<LauncherInstance> input)
{
    var removed = 0;
    var fixedCount = 0;
    var output = new List<LauncherInstance>();
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var item in input)
    {
        if (item is null ||
            string.IsNullOrWhiteSpace(item.Id) ||
            !ids.Add(item.Id) ||
            string.IsNullOrWhiteSpace(item.MinecraftVersion) ||
            (item.Loader != "none" && item.Loader != "fabric") ||
            item.RamMb < 1024 || item.RamMb > 65536)
        {
            removed++;
            continue;
        }

        var beforeName = item.Name;
        item.Name = (item.Name ?? "").Trim();

        if (item.Name.Equals("Alap példány", StringComparison.OrdinalIgnoreCase))
            item.Name = "Alap Instance";

        if (string.IsNullOrWhiteSpace(item.Name))
            item.Name = output.Count == 0 ? "Alap Instance" : $"Instance {output.Count + 1}";

        if (item.Name.Length > 48)
            item.Name = item.Name[..48].Trim();

        var baseName = item.Name;
        var suffix = 2;
        while (!names.Add(item.Name))
            item.Name = $"{baseName} {suffix++}";

        var normalizedLoader = item.Loader.ToLowerInvariant();
        if (item.Loader != normalizedLoader)
        {
            item.Loader = normalizedLoader;
            fixedCount++;
        }

        if (item.Loader == "none" && !string.IsNullOrWhiteSpace(item.LoaderVersion))
        {
            item.LoaderVersion = "";
            fixedCount++;
        }

        if (item.CreatedAt == default)
        {
            item.CreatedAt = DateTime.UtcNow;
            fixedCount++;
        }

        if (item.LastUsed == default)
        {
            item.LastUsed = item.CreatedAt;
            fixedCount++;
        }

        if (!string.Equals(beforeName, item.Name, StringComparison.Ordinal))
            fixedCount++;

        output.Add(item);
    }

    return (removed, fixedCount, output);
}

int ParseRamGb(string value)
{
    if (!int.TryParse(value, out var gb) || gb < 1 || gb > 64)
        throw new ArgumentException("A RAM 1 és 64 GB között lehet.");
    return gb;
}

void PrintInstance(LauncherInstance x, bool active)
{
    var marker = active ? "*" : " ";
    var loader = x.Loader == "none"
        ? "Vanilla"
        : string.IsNullOrWhiteSpace(x.LoaderVersion)
            ? "Fabric"
            : $"Fabric {x.LoaderVersion}";

    Console.WriteLine(
        $"{marker} {x.Name,-24} | MC {x.MinecraftVersion,-10} | {loader,-18} | {x.RamMb / 1024,2} GB");
}


LauncherInstance? InteractiveInstancePicker(List<LauncherInstance> list, string activeId)
{
    if (list.Count == 0)
    {
        Console.WriteLine("Nincs Instance. Hozz létre egyet: create <név>");
        return null;
    }

    var index = Math.Max(0, list.FindIndex(x =>
        x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase)));

    Console.CursorVisible = false;
    try
    {
        while (true)
        {
            Console.Clear();
            Header();
            Console.WriteLine("PLAY — válassz Instance-t");
            Console.WriteLine("Tab / Shift+Tab: váltás   Enter: indítás   Esc: kilépés");
            Console.WriteLine();

            for (var i = 0; i < list.Count; i++)
            {
                var x = list[i];
                var selected = i == index;
                var marker = selected ? ">" : " ";
                var loader = x.Loader == "none" ? "Vanilla" : "Fabric";

                if (selected)
                {
                    var oldBg = Console.BackgroundColor;
                    var oldFg = Console.ForegroundColor;
                    Console.BackgroundColor = ConsoleColor.DarkYellow;
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.Write($"{marker} {x.Name,-24} ");
                    Console.BackgroundColor = oldBg;
                    Console.ForegroundColor = oldFg;
                    Console.WriteLine($" | MC {x.MinecraftVersion,-10} | {loader,-7} | {x.RamMb / 1024} GB");
                }
                else
                {
                    Console.WriteLine($"{marker} {x.Name,-24}  | MC {x.MinecraftVersion,-10} | {loader,-7} | {x.RamMb / 1024} GB");
                }
            }

            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
                return null;

            if (key.Key == ConsoleKey.Enter)
                return list[index];

            if (key.Key == ConsoleKey.Tab)
            {
                var backwards = (key.Modifiers & ConsoleModifiers.Shift) != 0;
                index = backwards
                    ? (index - 1 + list.Count) % list.Count
                    : (index + 1) % list.Count;
            }
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.RightArrow)
            {
                index = (index + 1) % list.Count;
            }
            else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.LeftArrow)
            {
                index = (index - 1 + list.Count) % list.Count;
            }
        }
    }
    finally
    {
        Console.CursorVisible = true;
        Console.ResetColor();
    }
}

async Task<int> LaunchInstanceAsync(
    LauncherInstance instance,
    List<LauncherInstance> allInstances,
    string currentActiveId)
{
    var auth = new LocalAuthenticationService();
    var profile = await auth.GetCurrentProfileAsync()
        ?? (await auth.GetProfilesAsync()).OrderByDescending(x => x.LastUsed).FirstOrDefault();

    if (profile is null)
    {
        Console.Error.WriteLine("Nincs játékosprofil. Hozz létre egyet a GUI-ban.");
        return 2;
    }

    var safeId = new string(instance.Id
        .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
        .ToArray());
    if (string.IsNullOrWhiteSpace(safeId))
    {
        Console.Error.WriteLine("Érvénytelen Instance ID.");
        return 2;
    }

    var instanceDirectory = Path.Combine(appData, "instances", safeId);
    Directory.CreateDirectory(instanceDirectory);

    var install = new MinecraftInstallationService(instanceDirectory);
    var java = new JavaRuntimeService(new JavaRuntimeProvisioner());
    var launcher = new MinecraftLauncherService(install, java);
    var fabric = new FabricLoaderService(instanceDirectory);
    var resolver = new FabricVersionResolver();
    var settingsStore = new SettingsStorage();

    var settings = await settingsStore.LoadSettingsAsync();
    settings.MinecraftVersion = instance.MinecraftVersion;
    settings.Loader = instance.Loader;
    settings.DefaultRamMb = instance.RamMb;

    var loaderVersion = instance.LoaderVersion ?? "";
    if (instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Fabric verzió feloldása: Minecraft {instance.MinecraftVersion}...");
        var resolved = await resolver.ResolveRecommendedAsync(instance.MinecraftVersion);
        loaderVersion = resolved.LoaderVersion;
        settings.LoaderVersion = loaderVersion;
        instance.LoaderVersion = loaderVersion;
    }
    else
    {
        settings.LoaderVersion = "";
        instance.LoaderVersion = "";
    }

    await settingsStore.SaveSettingsAsync(settings);

    instance.LastUsed = DateTime.UtcNow;
    SaveInstances(allInstances, instance.Id);

    var progress = new Progress<OverallProgress>(x =>
    {
        var pct = Math.Clamp((int)Math.Round(x.OverallPercentage), 0, 100);
        Console.Write($"\r[{pct,3}%] {x.CurrentTask,-55}");
    });

    Console.WriteLine($"Indítás: {instance.Name}");
    Console.WriteLine($"Profil: {profile.Username}");
    Console.WriteLine($"Minecraft: {instance.MinecraftVersion}");
    Console.WriteLine($"Loader: {(instance.Loader == "none" ? "Vanilla" : $"Fabric {loaderVersion}")}");
    Console.WriteLine();

    await install.EnsureVersionDownloadedAsync(instance.MinecraftVersion, progress);

    if (instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine();
        await fabric.InstallFabricLoaderAsync(
            instance.MinecraftVersion,
            loaderVersion,
            progress);
    }

    Console.WriteLine();
    Console.WriteLine("Launch config készítése...");
    var config = await launcher.BuildLaunchConfigAsync(profile, settings);

    if (!await EnsureJavaForLaunchAsync(java, config, settings)) return 1;

    Console.WriteLine("Minecraft indítása...");
    await launcher.LaunchAsync(config);

    Console.WriteLine($"Elindítva: {instance.Name}");
    return 0;
}

IProgress<DownloadProgress> JavaProgress(string label) => new Progress<DownloadProgress>(p =>
{
    if (p.TotalBytes > 0)
        Console.Write($"\r{label}: {p.Percentage,5:0.0}%  {p.BytesReceived / 1048576} / {p.TotalBytes / 1048576} MB   ");
    else
        Console.Write($"\r{label}: {p.FileName}   ");
});

async Task<bool> EnsureJavaForLaunchAsync(JavaRuntimeService javaService, LaunchConfig launchConfig, LauncherSettings launchSettings)
{
    if (!string.IsNullOrWhiteSpace(launchConfig.JavaPath) && File.Exists(launchConfig.JavaPath)) return true;

    Console.WriteLine($"Java {launchConfig.RequiredJavaMajor} előkészítése...");
    try
    {
        var runtime = await javaService.EnsureRuntimeAsync(
            launchConfig.RequiredJavaMajor,
            launchSettings.AutoInstallJava,
            JavaProgress($"Java {launchConfig.RequiredJavaMajor}"));
        Console.WriteLine();
        launchConfig.JavaPath = runtime.Path;
        Console.WriteLine($"Java {runtime.Major} kiválasztva ({runtime.Source}): {runtime.Path}");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.Error.WriteLine("A Java előkészítése nem sikerült: " + ex.Message);
        return false;
    }
}

async Task<TurulMC.Core.Diagnostics.DoctorOptions> BuildDoctorOptionsAsync(string currentActiveId, List<LauncherInstance> allInstances)
{
    var cliSettings = await new SettingsStorage().LoadSettingsAsync();
    var instance = allInstances.FirstOrDefault(x => x.Id == currentActiveId) ?? allInstances.FirstOrDefault();

    string? instanceDirectory = null;
    if (instance is not null)
    {
        var safeId = new string(instance.Id.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (!string.IsNullOrWhiteSpace(safeId))
        {
            instanceDirectory = Path.Combine(appData, "instances", safeId);
            Directory.CreateDirectory(instanceDirectory);
        }
    }

    return new TurulMC.Core.Diagnostics.DoctorOptions
    {
        LauncherVersion = Version,
        DataRoot = appData,
        SettingsFilePath = Path.Combine(appData, "settings.json"),
        InstancesFilePath = instancesFile,
        ProfilesFilePath = profilesFile,
        LogsDirectory = Path.Combine(appData, "logs"),
        InstanceDirectory = instanceDirectory,
        GameDirectory = instanceDirectory,
        ConfiguredJavaPath = cliSettings.JavaPathOverride,
        RequiredJavaMajor = JavaVersionMap.ResolveRequiredMajor(instance?.MinecraftVersion ?? cliSettings.MinecraftVersion),
        ServerHost = string.IsNullOrWhiteSpace(cliSettings.TestServerHost) ? null : cliSettings.TestServerHost,
        ServerPort = cliSettings.TestServerPort
    };
}

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var (activeId, instances) = LoadInstances();

try
{
    switch (command)
    {
        case "instances":
        case "list":
            Header();
            if (instances.Count == 0)
            {
                Console.WriteLine("Nincs Instance.");
                break;
            }

            foreach (var instance in instances)
                PrintInstance(instance, instance.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine();
            Console.WriteLine("* = aktív");
            break;

        case "show":
        {
            var key = string.Join(" ", args.Skip(1)).Trim();
            var x = FindInstance(instances, key);
            if (x is null)
            {
                Console.Error.WriteLine("Az Instance nem található.");
                return 2;
            }

            Header();
            Console.WriteLine($"Név:            {x.Name}");
            Console.WriteLine($"ID:             {x.Id}");
            Console.WriteLine($"Aktív:          {(x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase) ? "igen" : "nem")}");
            Console.WriteLine($"Minecraft:      {x.MinecraftVersion}");
            Console.WriteLine($"Loader:         {(x.Loader == "none" ? "Vanilla" : x.Loader)}");
            Console.WriteLine($"Loader verzió:  {(string.IsNullOrWhiteSpace(x.LoaderVersion) ? "-" : x.LoaderVersion)}");
            Console.WriteLine($"RAM:            {x.RamMb / 1024} GB");
            Console.WriteLine($"Létrehozva:     {x.CreatedAt:u}");
            Console.WriteLine($"Utoljára:       {x.LastUsed:u}");
            break;
        }

        case "active":
        {
            var x = instances.FirstOrDefault(i =>
                i.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
            if (x is null)
            {
                Console.WriteLine("Nincs aktív Instance.");
                return 0;
            }
            PrintInstance(x, true);
            break;
        }

        case "play":
        {
            LauncherInstance? selected;

            if (args.Length >= 2)
            {
                var key = string.Join(" ", args.Skip(1)).Trim();
                selected = FindInstance(instances, key);
                if (selected is null)
                {
                    Console.Error.WriteLine("Az Instance nem található.");
                    return 2;
                }
            }
            else
            {
                selected = InteractiveInstancePicker(instances, activeId);
                if (selected is null)
                    return 0;
            }

            return await LaunchInstanceAsync(selected, instances, activeId);
        }

        case "select":
        {
            var key = string.Join(" ", args.Skip(1)).Trim();
            var x = FindInstance(instances, key);
            if (x is null)
            {
                Console.Error.WriteLine("Az Instance nem található.");
                return 2;
            }

            x.LastUsed = DateTime.UtcNow;
            SaveInstances(instances, x.Id);
            Console.WriteLine($"Aktív Instance: {x.Name}");
            break;
        }

        case "create":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Használat: create <név> [mc] [none|fabric] [ramGB]");
                return 2;
            }

            var name = args[1].Trim();
            var mc = args.Length >= 3 ? args[2].Trim() : "26.1.2";
            var loader = args.Length >= 4 ? args[3].Trim().ToLowerInvariant() : "none";
            var ramGb = args.Length >= 5 ? ParseRamGb(args[4]) : 4;

            if (name.Length is < 1 or > 48)
                throw new ArgumentException("Az Instance neve 1–48 karakter lehet.");

            if (instances.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Már létezik ilyen nevű Instance.");

            if (loader is not ("none" or "fabric"))
                throw new ArgumentException("Loader csak: none vagy fabric.");

            var created = new LauncherInstance
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                MinecraftVersion = mc,
                Loader = loader,
                LoaderVersion = "",
                RamMb = ramGb * 1024,
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow
            };

            instances.Add(created);
            SaveInstances(instances, created.Id);
            Console.WriteLine($"Létrehozva és aktiválva: {created.Name}");
            break;
        }

        case "rename":
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Használat: rename <név|id> <új-név>");
                return 2;
            }

            var x = FindInstance(instances, args[1]);
            if (x is null)
                throw new ArgumentException("Az Instance nem található.");

            var newName = string.Join(" ", args.Skip(2)).Trim();
            if (newName.Length is < 1 or > 48)
                throw new ArgumentException("Az Instance neve 1–48 karakter lehet.");

            if (instances.Any(i => i.Id != x.Id &&
                                   i.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Már létezik ilyen nevű Instance.");

            var old = x.Name;
            x.Name = newName;
            SaveInstances(instances, activeId);
            Console.WriteLine($"{old} -> {newName}");
            break;
        }

        case "set-version":
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Használat: set-version <név|id> <verzió>");
                return 2;
            }

            var x = FindInstance(instances, args[1]);
            if (x is null)
                throw new ArgumentException("Az Instance nem található.");

            x.MinecraftVersion = args[2].Trim();
            x.LoaderVersion = "";
            SaveInstances(instances, activeId);
            Console.WriteLine($"{x.Name}: Minecraft {x.MinecraftVersion}");
            break;
        }

        case "set-loader":
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Használat: set-loader <név|id> <none|fabric>");
                return 2;
            }

            var x = FindInstance(instances, args[1]);
            if (x is null)
                throw new ArgumentException("Az Instance nem található.");

            var loader = args[2].Trim().ToLowerInvariant();
            if (loader is not ("none" or "fabric"))
                throw new ArgumentException("Loader csak: none vagy fabric.");

            x.Loader = loader;
            x.LoaderVersion = "";
            SaveInstances(instances, activeId);
            Console.WriteLine($"{x.Name}: {(loader == "none" ? "Vanilla" : "Fabric")}");
            break;
        }

        case "set-ram":
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Használat: set-ram <név|id> <GB>");
                return 2;
            }

            var x = FindInstance(instances, args[1]);
            if (x is null)
                throw new ArgumentException("Az Instance nem található.");

            var gb = ParseRamGb(args[2]);
            x.RamMb = gb * 1024;
            SaveInstances(instances, activeId);
            Console.WriteLine($"{x.Name}: {gb} GB RAM");
            break;
        }

        case "delete":
        {
            var key = string.Join(" ", args.Skip(1)).Trim();
            var x = FindInstance(instances, key);
            if (x is null)
            {
                Console.Error.WriteLine("Az Instance nem található.");
                return 2;
            }

            instances.Remove(x);
            if (x.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase))
                activeId = instances.OrderByDescending(i => i.LastUsed).FirstOrDefault()?.Id ?? "";

            SaveInstances(instances, activeId);
            Console.WriteLine($"Törölve: {x.Name}");
            break;
        }

        case "clean":
        {
            var (removed, fixedCount, cleaned) = CleanInstances(instances);
            var nextActive = cleaned.Any(x => x.Id == activeId)
                ? activeId
                : cleaned.OrderByDescending(x => x.LastUsed).FirstOrDefault()?.Id ?? "";

            SaveInstances(cleaned, nextActive);
            Console.WriteLine($"Kész. Törölt hibás configok: {removed}");
            Console.WriteLine($"Javított configok: {fixedCount}");
            break;
        }

        case "reset":
        {
            if (File.Exists(instancesFile))
                File.Delete(instancesFile);
            Console.WriteLine("Minden Instance config törölve.");
            break;
        }

        case "profiles":
        {
            Header();
            if (!File.Exists(profilesFile))
            {
                Console.WriteLine("Nincs profil.");
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profilesFile));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine("A profiles.json formátuma hibás.");
                    break;
                }

                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    var username = p.TryGetProperty("username", out var u) ? u.GetString() ?? "?" : "?";
                    var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    Console.WriteLine($"{username,-18} | {id}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Profil config hiba: {ex.Message}");
            }
            break;
        }

        case "server":
        {
            Console.WriteLine("play.turulnetwork.hu ellenőrzése...");
            var service = new ServerStatusService();
            var status = await service.CheckServerStatusAsync("play.turulnetwork.hu", 25565);

            if (!status.IsOnline)
            {
                Console.WriteLine("Offline / nem elérhető.");
                if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
                    Console.WriteLine(status.ErrorMessage);
                return 1;
            }

            Console.WriteLine("Online");
            Console.WriteLine($"Játékosok: {status.OnlinePlayers ?? 0} / {status.MaxPlayers ?? 0}");
            if (!string.IsNullOrWhiteSpace(status.Version))
                Console.WriteLine($"Verzió: {status.Version}");
            Console.WriteLine($"Ping: {status.ResponseTimeMs} ms");
            break;
        }

        case "servers.list":
        {
            var store = new TurulMC.Core.Servers.ServerListStorage();
            var servers = await store.LoadAsync();
            Header();
            if (servers.Count == 0) { Console.WriteLine("Nincs mentett szerver."); break; }
            foreach (var s in servers.OrderByDescending(x => x.LastUsed))
                Console.WriteLine($"{s.Name,-24} | {s.Host}:{s.Port} | {s.Id}");
            break;
        }

        case "servers.add":
        {
            if (args.Length < 3) { Console.Error.WriteLine("Használat: servers.add <név> <host> [port]"); return 2; }
            var port = args.Length >= 4 && int.TryParse(args[3], out var p) ? p : 25565;
            var store = new TurulMC.Core.Servers.ServerListStorage();
            var added = await store.AddAsync(args[1], args[2], port);
            Console.WriteLine($"Mentve: {added.Name} -> {added.Host}:{added.Port}");
            break;
        }

        case "servers.remove":
        {
            if (args.Length < 2) { Console.Error.WriteLine("Használat: servers.remove <név|id>"); return 2; }
            var store = new TurulMC.Core.Servers.ServerListStorage();
            Console.WriteLine(await store.RemoveAsync(args[1]) ? "Törölve." : "Nem található.");
            break;
        }

        case "server.connect":
        {
            string host; int port;
            if (args.Length >= 2)
            {
                var store = new TurulMC.Core.Servers.ServerListStorage();
                var servers = await store.LoadAsync();
                var key = args[1];
                var saved = servers.FirstOrDefault(s => s.Id.Equals(key, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (saved is not null) { host = saved.Host; port = args.Length >= 3 && int.TryParse(args[2], out var p2) ? p2 : saved.Port; }
                else { host = key; port = args.Length >= 3 && int.TryParse(args[2], out var p3) ? p3 : 25565; }
            }
            else { Console.Error.WriteLine("Használat: server.connect <név|id|host> [port]"); return 2; }
            var target = instances.OrderByDescending(x => x.LastUsed).FirstOrDefault() ?? throw new ArgumentException("Nincs Instance.");
            // Handoff: --server/--port tényleg átmegy via LaunchConfig.ServerIp/ServerPort.
            var auth2 = new LocalAuthenticationService();
            var profile2 = await auth2.GetCurrentProfileAsync() ?? (await auth2.GetProfilesAsync()).OrderByDescending(x => x.LastUsed).FirstOrDefault()
                ?? throw new ArgumentException("Nincs profil.");
            var sdir = Path.Combine(appData, "instances", new string(target.Id.Where(char.IsLetterOrDigit).ToArray()));
            Directory.CreateDirectory(sdir);
            var install2 = new MinecraftInstallationService(sdir);
            var java2 = new JavaRuntimeService(new JavaRuntimeProvisioner());
            var launcher2 = new MinecraftLauncherService(install2, java2);
            var store3 = new SettingsStorage();
            var settings2 = await store3.LoadSettingsAsync();
            settings2.MinecraftVersion = target.MinecraftVersion; settings2.Loader = target.Loader; settings2.DefaultRamMb = target.RamMb;
            var cfg = await launcher2.BuildLaunchConfigAsync(profile2, settings2);
            cfg.ServerIp = host; cfg.ServerPort = port; // explicit handoff
            Console.WriteLine($"Csatlakozás: {host}:{port} (--server/--port átadva)");
            if (!await EnsureJavaForLaunchAsync(java2, cfg, settings2)) return 1;
            await launcher2.LaunchAsync(cfg);
            return 0;
        }

        case "manifest-hash":
        {
            if (args.Length < 2) { Console.Error.WriteLine("Használat: manifest-hash <manifest.json>"); return 2; }
            var svc = new TurulMC.Core.Modpacks.ModpackService();
            var manifest = await svc.GetManifestAsync(args[1]);
            Console.WriteLine(TurulMC.Core.Modpacks.ModpackPackager.ComputeManifestHash(manifest));
            break;
        }

        case "modpack.export":
        {
            if (args.Length < 3) { Console.Error.WriteLine("Használat: modpack.export <instanceDir> <out.mrpack>"); return 2; }
            var svc = new TurulMC.Core.Modpacks.ModpackService(args[1]);
            var diff = await svc.GetDiffAsync(new TurulMC.Core.Models.ModpackManifest());
            _ = diff;
            Console.Error.WriteLine("Add meg a manifestet settings-ben, vagy használd a GUI exportot. Itt: manifest.json az instanceDir-ben.");
            var manifestPath = Path.Combine(args[1], "manifest.json");
            var manifest = await svc.GetManifestAsync(manifestPath);
            await TurulMC.Core.Modpacks.ModpackPackager.ExportAsync(manifest, args[1], args[2]);
            Console.WriteLine($"Exportálva: {args[2]}");
            break;
        }

        case "modpack.import":
        {
            if (args.Length < 3) { Console.Error.WriteLine("Használat: modpack.import <in.mrpack> <instanceDir>"); return 2; }
            var imported = await TurulMC.Core.Modpacks.ModpackPackager.ImportMrpackAsync(args[1], args[2]);
            Console.WriteLine($"Importálva: {imported.Id}");
            break;
        }

        case "serve":
        {
            Console.Error.WriteLine("Használat: serve helyett: python -m http.server <port> a modpack mappában. (Egyszerű statikus kiszolgálás.)");
            return 2;
        }

        case "doctor":
        {
            Header();
            var doctorOptions = await BuildDoctorOptionsAsync(activeId, instances);
            var doctor = new TurulMC.Core.Diagnostics.LauncherDoctor(
                doctorOptions,
                new JavaRuntimeService(new JavaRuntimeProvisioner()),
                new ServerStatusService());

            var report = await doctor.RunAsync();
            Console.WriteLine(report.ToPlainText());
            return report.Healthy ? 0 : 1;
        }

        case "support":
        {
            Header();
            var supportOptions = await BuildDoctorOptionsAsync(activeId, instances);
            var supportDoctor = new TurulMC.Core.Diagnostics.LauncherDoctor(
                supportOptions,
                new JavaRuntimeService(new JavaRuntimeProvisioner()),
                new ServerStatusService());

            Console.WriteLine("Support csomag készítése (logok + crash reportok + Doctor jelentés)...");
            var bundle = await new TurulMC.Core.Diagnostics.SupportBundleService(supportOptions, supportDoctor)
                .CreateAsync(includeDoctorReport: true);

            if (!bundle.Success)
            {
                Console.Error.WriteLine("A csomag nem készült el: " + bundle.Error);
                return 1;
            }

            Console.WriteLine($"Support ZIP: {bundle.FullPath}");
            Console.WriteLine($"  {bundle.IncludedFiles.Count} fájl, {bundle.TotalBytes / 1024} KB");
            return 0;
        }

        case "java":
        {
            Header();
            var javaSettings = await new SettingsStorage().LoadSettingsAsync();
            var javaProvisioner = new JavaRuntimeProvisioner();
            var javaService = new JavaRuntimeService(javaProvisioner);
            var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            var activeInstance = instances.FirstOrDefault(x => x.Id == activeId) ?? instances.FirstOrDefault();
            var requiredMajor = JavaVersionMap.ResolveRequiredMajor(
                activeInstance?.MinecraftVersion ?? javaSettings.MinecraftVersion);

            switch (sub)
            {
                case "list":
                {
                    var runtimes = await javaService.DetectInstalledRuntimesAsync();
                    if (runtimes.Count == 0)
                        Console.WriteLine("Nincs ismert Java runtime a gépen.");
                    foreach (var runtime in runtimes)
                        Console.WriteLine($"  Java {runtime.Major,-3} {runtime.Source,-11} {runtime.Version,-24} {runtime.Path}");

                    Console.WriteLine();
                    Console.WriteLine($"Telepítési gyökér: {javaProvisioner.RuntimeRoot}");
                    var recommended = await javaService.GetRecommendedRuntimeAsync(requiredMajor.ToString());
                    Console.WriteLine(recommended is null
                        ? $"A Minecraft {activeInstance?.MinecraftVersion ?? javaSettings.MinecraftVersion} Java {requiredMajor}-et igényel, de nincs megfelelő runtime. Telepítés: java install {requiredMajor}"
                        : $"Ajánlott (Java {requiredMajor}): Java {recommended.Major} — {recommended.Path}");
                    return 0;
                }

                case "install":
                {
                    var major = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : requiredMajor;
                    Console.WriteLine($"Java {major} telepítése (Eclipse Temurin JRE, SHA-256 ellenőrzéssel)...");
                    var runtime = await javaService.EnsureRuntimeAsync(major, allowInstall: true, progress: JavaProgress($"Java {major}"));
                    Console.WriteLine();
                    Console.WriteLine($"Kész: Java {runtime.Major} — {runtime.Path}");
                    return 0;
                }

                case "remove":
                {
                    if (args.Length < 3)
                    {
                        Console.Error.WriteLine("Használat: java remove <id>  (az id-t a java list mutatja)");
                        return 2;
                    }

                    var removed = await javaProvisioner.RemoveProvisionedRuntimeAsync(args[2]);
                    Console.WriteLine(removed ? $"Eltávolítva: {args[2]}" : $"Nem található vagy nem távolítható el: {args[2]}");
                    return removed ? 0 : 1;
                }

                default:
                    Console.Error.WriteLine("Használat: java list | java install [major] | java remove <id>");
                    return 2;
            }
        }

        case "config":
            Header();
            Console.WriteLine($"AppData:    {appData}");
            Console.WriteLine($"Instances:  {instancesFile}");
            Console.WriteLine($"Profiles:   {profilesFile}");
            break;

        case "gui":
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "TurulMC.Launcher.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "GUI", "TurulMC.Launcher.exe"),
                Path.Combine(AppContext.BaseDirectory, "TurulLauncher.exe")
            };

            var exe = candidates
                .Select(Path.GetFullPath)
                .FirstOrDefault(File.Exists);

            if (exe is null)
            {
                Console.Error.WriteLine("A GUI .exe nem található.");
                return 3;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Console.WriteLine("GUI elindítva.");
            break;
        }

        case "--version":
        case "-v":
        case "version":
            Console.WriteLine($"TurulLauncher CLI {Version}");
            break;

        case "--help":
        case "-h":
        case "help":
            Help();
            break;

        default:
            Console.Error.WriteLine($"Ismeretlen parancs: {command}");
            Console.Error.WriteLine("Használd: TurulLauncher.CLI.exe help");
            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"HIBA: {ex.Message}");
    return 2;
}
