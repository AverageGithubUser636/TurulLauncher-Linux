using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurulMC.Core.Java;
using TurulMC.Core.Models;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Minecraft;

public class MinecraftLauncherService : IMinecraftLauncherService
{
    private sealed class PersistedGameSession
    {
        public int ProcessId { get; set; }
        public DateTime ProcessStartUtc { get; set; }
        public DateTime SessionStartedUtc { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string GameDirectory { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool Verified { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public StringBuilder OutputTail { get; } = new();
    }

    private sealed class PlaytimeData
    {
        public long TotalPlaytimeSeconds { get; set; }
        public int LaunchCount { get; set; }
        public DateTime? LastPlayedAtUtc { get; set; }
    }

    private static readonly object ProcessLock = new();
    private static Process? RunningProcess;
    private static PersistedGameSession? RunningSession;
    private static bool RunningProcessReattached;
    private static event EventHandler<MinecraftProcessExitedEventArgs>? GlobalMinecraftExited;
    private static readonly string SessionFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurulMC", "game-session.json");

    private readonly IMinecraftInstallationService _installationService;
    private readonly IJavaRuntimeService _javaRuntimeService;
    private readonly string _instanceId;

    public event EventHandler<MinecraftProcessExitedEventArgs>? MinecraftExited
    {
        add => GlobalMinecraftExited += value;
        remove => GlobalMinecraftExited -= value;
    }

    public event EventHandler<string>? ProcessOutput;

    public bool IsGameRunning => GetStatus().Running;

    public MinecraftLauncherService(
        IMinecraftInstallationService installationService,
        IJavaRuntimeService javaRuntimeService,
        string instanceId = "")
    {
        _installationService = installationService;
        _javaRuntimeService = javaRuntimeService;
        _instanceId = instanceId ?? string.Empty;
        EnsureTrackedProcess();
    }

    public MinecraftProcessStatus GetStatus()
    {
        EnsureTrackedProcess();

        PersistedGameSession? session;
        Process? process;
        bool reattached;
        lock (ProcessLock)
        {
            session = RunningSession;
            process = RunningProcess;
            reattached = RunningProcessReattached;
        }

        var currentDirectory = _installationService.GetInstanceDirectory();
        var playtime = LoadPlaytime(currentDirectory);
        var running = IsAlive(process);
        var sessionSeconds = 0L;
        if (running && session != null)
            sessionSeconds = Math.Max(0L, (long)(DateTime.UtcNow - session.SessionStartedUtc).TotalSeconds);

        var total = playtime.TotalPlaytimeSeconds;
        if (running && session != null &&
            PathsEqual(session.GameDirectory, currentDirectory))
            total += sessionSeconds;

        return new MinecraftProcessStatus
        {
            Running = running,
            ProcessId = running ? process?.Id : null,
            RunningInstanceId = running ? session?.InstanceId ?? string.Empty : string.Empty,
            SessionStartedUtc = running ? session?.SessionStartedUtc : null,
            SessionSeconds = running ? sessionSeconds : 0,
            TotalPlaytimeSeconds = Math.Max(0, total),
            LaunchCount = Math.Max(0, playtime.LaunchCount),
            LastPlayedAtUtc = playtime.LastPlayedAtUtc,
            Reattached = running && reattached
        };
    }

    public async Task LaunchAsync(LaunchConfig config, CancellationToken cancellationToken = default)
    {
        TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJvmArgs(config.AdditionalJvmArgs);
        if (!string.IsNullOrWhiteSpace(config.JavaPath))
            TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(config.JavaPath);
        EnsureTrackedProcess();
        lock (ProcessLock)
        {
            if (IsAlive(RunningProcess))
                throw new InvalidOperationException("A Minecraft már fut.");
            RunningProcess = null;
            RunningSession = null;
            RunningProcessReattached = false;
        }

        if (string.IsNullOrWhiteSpace(config.JavaPath))
        {
            var runtime = await _javaRuntimeService.EnsureRuntimeAsync(
                config.RequiredJavaMajor,
                config.AutoInstallJava,
                progress: null,
                cancellationToken: cancellationToken);
            config.JavaPath = runtime.Path;
        }

        Directory.CreateDirectory(config.GameDirectory);
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = config.JavaPath,
            WorkingDirectory = config.GameDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in BuildJvmArguments(config)) psi.ArgumentList.Add(arg);
        if (!ManifestJvmContainsClasspath(config))
        {
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(BuildClasspath(config));
        }
        psi.ArgumentList.Add(config.MainClass ?? "net.minecraft.client.main.Main");
        foreach (var arg in BuildGameArguments(config)) psi.ArgumentList.Add(arg);

        LauncherLogger.Info($"Minecraft {config.Version} indítása ({config.Username})");
        LauncherLogger.Info($"Java: {config.JavaPath} (követelmény: {config.RequiredJavaMajor})");
        LauncherLogger.Debug("MainClass: " + config.MainClass);
        LauncherLogger.Debug("WorkingDir: " + config.GameDirectory);
        LauncherLogger.Debug("Classpath elemek: " + config.ClassPath.Count);
        LauncherLogger.Debug("Parancs: " + config.JavaPath + " " + string.Join(" ", psi.ArgumentList.Select(QuoteForLog)));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("A Java processz nem indítható el.");
        process.EnableRaisingEvents = true;
        process.Exited += OnGlobalMinecraftProcessExited;

        var session = new PersistedGameSession
        {
            ProcessId = process.Id,
            ProcessStartUtc = SafeProcessStartUtc(process) ?? DateTime.UtcNow,
            SessionStartedUtc = DateTime.UtcNow,
            InstanceId = config.InstanceId ?? string.Empty,
            GameDirectory = config.GameDirectory,
            Version = config.Version
        };

        lock (ProcessLock)
        {
            RunningProcess = process;
            RunningSession = session;
            RunningProcessReattached = false;
            SaveSessionNoThrow(session);
        }

        LauncherLogger.Info("Java/Minecraft processz létrejött, PID: " + process.Id);

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stdout) { if (stdout.Length < 30000) stdout.AppendLine(e.Data); }
            AppendDiagnosticTail(session, e.Data);
            LauncherLogger.LogProcessOutput(e.Data);
            try { ProcessOutput?.Invoke(this, e.Data); } catch { }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderr) { if (stderr.Length < 30000) stderr.AppendLine(e.Data); }
            AppendDiagnosticTail(session, e.Data);
            LauncherLogger.LogProcessError(e.Data);
            try { ProcessOutput?.Invoke(this, e.Data); } catch { }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(
            waitForExitTask,
            Task.Delay(TimeSpan.FromSeconds(6), cancellationToken));

        if (ReferenceEquals(completed, waitForExitTask))
        {
            await waitForExitTask;
            await Task.Delay(150, CancellationToken.None);

            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }

            // Keep the end of the output: loader diagnostics commonly follow long startup logs.
            string errorText = ReadDiagnosticTail(session).Trim();
            if (errorText.Length > 3500) errorText = errorText[^3500..];

            ClearSessionForFailedStart(process, session);
            process.Exited -= OnGlobalMinecraftProcessExited;

            throw new InvalidOperationException(
                $"A Minecraft processz azonnal leállt (exit code: {exitCode}).\n\n" +
                (string.IsNullOrWhiteSpace(errorText)
                    ? "Nem érkezett Java hibaüzenet. Nézd meg a TurulLauncher logját."
                    : errorText));
        }

        MarkSuccessfulLaunch(session);
        LauncherLogger.Info("Minecraft processz ellenőrizve: fut.");
    }

    private static void AppendDiagnosticTail(PersistedGameSession session, string line)
    {
        lock (session.OutputTail)
        {
            session.OutputTail.AppendLine(line.Length > 65536 ? line[^65536..] : line);
            if (session.OutputTail.Length > 65536)
                session.OutputTail.Remove(0, session.OutputTail.Length - 65536);
        }
    }

    private static string ReadDiagnosticTail(PersistedGameSession session)
    {
        lock (session.OutputTail) return session.OutputTail.ToString();
    }

    private static void OnGlobalMinecraftProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;
        int exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }

        PersistedGameSession? session = null;
        lock (ProcessLock)
        {
            if (RunningProcess != null && RunningProcess.Id == process.Id)
            {
                session = RunningSession;
                RunningProcess = null;
                RunningSession = null;
                RunningProcessReattached = false;
            }
        }

        if (session != null)
        {
            var seconds = FinalizeSession(session);
            LauncherLogger.Info($"Minecraft processz leállt (PID: {process.Id}, exit code: {exitCode}, session: {seconds}s).");
            GlobalMinecraftExited?.Invoke(null, new MinecraftProcessExitedEventArgs
            {
                ExitCode = exitCode,
                WasVerified = session.Verified,
                OutputTail = ReadDiagnosticTail(session),
                InstanceId = session.InstanceId,
                SessionStartedUtc = session.SessionStartedUtc,
                SessionSeconds = seconds
            });
        }
        else
        {
            LauncherLogger.Info($"Minecraft processz leállt (PID: {process.Id}, exit code: {exitCode}).");
        }

        try { process.Exited -= OnGlobalMinecraftProcessExited; } catch { }
    }

    private static void EnsureTrackedProcess()
    {
        PersistedGameSession? staleSession = null;
        lock (ProcessLock)
        {
            if (IsAlive(RunningProcess)) return;
            RunningProcess = null;
            RunningSession = null;
            RunningProcessReattached = false;

            var persisted = LoadSessionNoThrow();
            if (persisted == null) return;

            try
            {
                var process = Process.GetProcessById(persisted.ProcessId);
                var start = SafeProcessStartUtc(process);
                var startMatches = start.HasValue && Math.Abs((start.Value - persisted.ProcessStartUtc).TotalSeconds) <= 5;
                var looksLikeJava = process.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) ||
                                    process.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase);
                if (!process.HasExited && startMatches && looksLikeJava)
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += OnGlobalMinecraftProcessExited;
                    RunningProcess = process;
                    RunningSession = persisted;
                    RunningProcessReattached = true;
                    if (!persisted.Verified && (DateTime.UtcNow - persisted.SessionStartedUtc).TotalSeconds >= 6)
                        MarkSuccessfulLaunch(persisted);
                    LauncherLogger.Info($"Futó Minecraft processz visszacsatolva, PID: {process.Id}.");
                    return;
                }
            }
            catch { }

            staleSession = persisted;
        }

        if (staleSession != null)
        {
            var seconds = FinalizeSession(staleSession);
            LauncherLogger.Info($"Korábbi Minecraft session lezárva helyreállításkor ({seconds}s).");
        }
    }

    private static bool IsAlive(Process? process)
    {
        if (process == null) return false;
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static DateTime? SafeProcessStartUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private static void ClearSessionForFailedStart(Process process, PersistedGameSession session)
    {
        lock (ProcessLock)
        {
            if (RunningProcess != null && RunningProcess.Id == process.Id)
            {
                RunningProcess = null;
                RunningSession = null;
                RunningProcessReattached = false;
            }
            DeleteSessionNoThrow();
        }
    }

    private static void MarkSuccessfulLaunch(PersistedGameSession session)
    {
        if (session.Verified) return;
        session.Verified = true;
        SaveSessionNoThrow(session);
        try
        {
            var data = LoadPlaytime(session.GameDirectory);
            data.LaunchCount = Math.Max(0, data.LaunchCount) + 1;
            data.LastPlayedAtUtc = session.SessionStartedUtc;
            SavePlaytime(session.GameDirectory, data);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Játékidő indítási statisztika nem menthető: " + ex.Message);
        }
    }

    private static long FinalizeSession(PersistedGameSession session)
    {
        var seconds = Math.Max(0L, (long)(DateTime.UtcNow - session.SessionStartedUtc).TotalSeconds);
        if (!session.Verified)
        {
            DeleteSessionNoThrow();
            return 0;
        }
        try
        {
            var data = LoadPlaytime(session.GameDirectory);
            data.TotalPlaytimeSeconds = Math.Max(0, data.TotalPlaytimeSeconds) + seconds;
            if (!data.LastPlayedAtUtc.HasValue)
                data.LastPlayedAtUtc = session.SessionStartedUtc;
            SavePlaytime(session.GameDirectory, data);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Játékidő mentése nem sikerült: " + ex.Message);
        }
        DeleteSessionNoThrow();
        return seconds;
    }

    private static string GetPlaytimePath(string gameDirectory) => Path.Combine(gameDirectory, ".turul-playtime.json");

    private static PlaytimeData LoadPlaytime(string gameDirectory)
    {
        try
        {
            var path = GetPlaytimePath(gameDirectory);
            if (!File.Exists(path)) return new PlaytimeData();
            return JsonSerializer.Deserialize<PlaytimeData>(File.ReadAllText(path)) ?? new PlaytimeData();
        }
        catch { return new PlaytimeData(); }
    }

    private static void SavePlaytime(string gameDirectory, PlaytimeData data)
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);
            var path = GetPlaytimePath(gameDirectory);
            // Játékidő: nem kritikus adat, ezért hiba esetén sem állítja meg az indítást.
            Storage.AtomicFile.TryWriteAllText(
                path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                createBackup: false);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("A játékidő nem menthető: " + ex.Message);
        }
    }

    private static PersistedGameSession? LoadSessionNoThrow()
    {
        try
        {
            if (!File.Exists(SessionFilePath)) return null;
            return JsonSerializer.Deserialize<PersistedGameSession>(File.ReadAllText(SessionFilePath));
        }
        catch { return null; }
    }

    private static void SaveSessionNoThrow(PersistedGameSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(SessionFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            Storage.AtomicFile.TryWriteAllText(
                SessionFilePath, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }),
                createBackup: false);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Minecraft session tracker nem menthető: " + ex.Message);
        }
    }

    private static void DeleteSessionNoThrow()
    {
        try { if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath); } catch { }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    public async Task<LaunchConfig> BuildLaunchConfigAsync(LocalProfile profile, LauncherSettings settings)
    {
        var instanceDir = _installationService.GetInstanceDirectory();
        var config = new LaunchConfig
        {
            Username = profile.Username,
            Uuid = profile.Uuid,
            AccessToken = string.IsNullOrWhiteSpace(profile.AccessToken) ? "0" : profile.AccessToken,
            UserType = string.IsNullOrWhiteSpace(profile.UserType) ? "legacy" : profile.UserType,
            Version = settings.MinecraftVersion,
            GameDirectory = instanceDir,
            AssetsDirectory = _installationService.GetAssetsDirectory(),
            NativesDirectory = Path.Combine(instanceDir, "natives"),
            MinMemoryMb = Math.Min(Math.Max(settings.DefaultRamMb / 2, 512), 2048),
            MaxMemoryMb = Math.Max(settings.DefaultRamMb, settings.MinecraftVersion.StartsWith("26.") ? 4096 : 2048),
            AutoInstallJava = settings.AutoInstallJava
        };

        if (settings.Loader == "fabric") await BuildFabricLaunchConfigAsync(config, settings);
        else await BuildVanillaLaunchConfigAsync(config, settings);

        var runtime = await _javaRuntimeService.GetRecommendedRuntimeAsync(config.RequiredJavaMajor.ToString());
        config.JavaPath = runtime?.Path ?? "";
        return config;
    }

    private async Task BuildVanillaLaunchConfigAsync(LaunchConfig config, LauncherSettings settings)
    {
        var detail = await _installationService.GetVersionDetailAsync(settings.MinecraftVersion);
        ApplyVanillaDetail(config, detail, settings.MinecraftVersion);
    }

    private void ApplyVanillaDetail(LaunchConfig config, MinecraftVersionDetail detail, string versionId)
    {
        config.MainClass = detail.MainClass ?? "net.minecraft.client.main.Main";
        config.VersionType = detail.Type ?? "release";
        config.RequiredJavaMajor = detail.JavaVersion?.MajorVersion > 0
            ? detail.JavaVersion.MajorVersion
            : (versionId.StartsWith("26.", StringComparison.OrdinalIgnoreCase) ? 25 : 21);
        config.VersionArguments = detail.Arguments;
        config.AssetIndexId = detail.AssetIndex?.Id ?? versionId;

        config.ClassPath.Clear();
        foreach (var lib in detail.Libraries)
        {
            if (!IsRuleSetAllowed(lib.Rules)) continue;
            var artifact = lib.Downloads?.Artifact;
            if (artifact == null) continue;
            var path = ResolveArtifactPath(lib.Name, artifact.Path, _installationService.GetLibrariesDirectory());
            if (File.Exists(path)) config.ClassPath.Add(path);
        }

        var clientJar = _installationService.GetClientJarPathAsync(versionId).GetAwaiter().GetResult();
        if (File.Exists(clientJar)) config.ClassPath.Add(clientJar);
    }

    private async Task BuildFabricLaunchConfigAsync(LaunchConfig config, LauncherSettings settings)
    {
        // First build a correct vanilla 26.x launch configuration from Mojang metadata.
        var vanilla = await _installationService.GetVersionDetailAsync(settings.MinecraftVersion);
        ApplyVanillaDetail(config, vanilla, settings.MinecraftVersion);

        var fabricVersionId = $"fabric-loader-{settings.LoaderVersion}-{settings.MinecraftVersion}";
        var fabricJsonPath = Path.Combine(_installationService.GetInstanceDirectory(), "versions", fabricVersionId, $"{fabricVersionId}.json");
        if (!File.Exists(fabricJsonPath))
            throw new InvalidOperationException("A kiválasztott Fabric Loader nincs telepítve. Telepítsd a loadert, vagy válaszd a 'Nincs' opciót.");

        using var fabricDoc = JsonDocument.Parse(await File.ReadAllTextAsync(fabricJsonPath));
        var root = fabricDoc.RootElement;
        if (root.TryGetProperty("mainClass", out var main)) config.MainClass = main.GetString() ?? config.MainClass;
        if (root.TryGetProperty("mainClassClient", out var mainClient)) config.MainClass = mainClient.GetString() ?? config.MainClass;

        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!lib.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var path = ResolveMavenPath(name, _installationService.GetLibrariesDirectory());
                if (File.Exists(path) && !config.ClassPath.Contains(path, StringComparer.OrdinalIgnoreCase)) config.ClassPath.Add(path);
            }
        }
    }

    public List<string> BuildJvmArguments(LaunchConfig config)
    {
        var args = new List<string> { $"-Xms{config.MinMemoryMb}M", $"-Xmx{config.MaxMemoryMb}M" };
        var manifestArgs = ExpandVersionArgumentGroup(config, "jvm");
        if (manifestArgs.Count > 0)
        {
            // Mojang's version metadata carries the version-specific JVM/native/classpath args.
            args.AddRange(manifestArgs.Where(a => !a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            args.Add($"-Djava.library.path={config.NativesDirectory}");
            args.Add("-Dminecraft.launcher.brand=TurulLauncher");
            args.Add("-Dminecraft.launcher.version=0.1");
        }

        // A launcher/Instance szintű extra JVM argumentumok minden esetben a lista végére
        // kerülnek (a Mojang metaadat jelenlététől függetlenül), így tényleg érvényesülnek.
        foreach (var extra in config.AdditionalJvmArgs)
        {
            if (!string.IsNullOrWhiteSpace(extra) && !args.Contains(extra, StringComparer.Ordinal))
                args.Add(extra);
        }

        return args;
    }

    public List<string> BuildGameArguments(LaunchConfig config)
    {
        var args = ExpandVersionArgumentGroup(config, "game");
        if (args.Count == 0)
        {
            args.AddRange(new[]
            {
                "--username", config.Username,
                "--version", config.Version,
                "--gameDir", config.GameDirectory,
                "--assetsDir", config.AssetsDirectory,
                "--assetIndex", config.AssetIndexId ?? config.Version,
                "--uuid", config.Uuid,
                "--accessToken", config.AccessToken,
                "--userType", config.UserType,
                "--versionType", config.VersionType
            });
        }
        if (!string.IsNullOrWhiteSpace(config.ServerIp))
        {
            args.Add("--server"); args.Add(config.ServerIp);
            args.Add("--port"); args.Add(config.ServerPort.ToString());
        }

        // A Mojang metaadatban a --width/--height a "has_custom_resolution" feature-höz
        // kötött, amit a launcher nem állít be — ezért az Instance-ből jövő felbontást
        // itt fűzzük a játék argumentumai közé.
        if (config.WindowWidth > 0 && config.WindowHeight > 0 && !args.Contains("--width"))
        {
            args.Add("--width"); args.Add(config.WindowWidth.ToString());
            args.Add("--height"); args.Add(config.WindowHeight.ToString());
        }
        return args;
    }

    public string BuildClasspath(LaunchConfig config) => string.Join(Path.PathSeparator, config.ClassPath.Where(File.Exists));

    private List<string> ExpandVersionArgumentGroup(LaunchConfig config, string group)
    {
        var result = new List<string>();
        if (config.VersionArguments.ValueKind != JsonValueKind.Object || !config.VersionArguments.TryGetProperty(group, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(ExpandPlaceholders(item.GetString() ?? "", config));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object || !ArgumentObjectAllowed(item)) continue;
            if (!item.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) result.Add(ExpandPlaceholders(value.GetString() ?? "", config));
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var v in value.EnumerateArray()) if (v.ValueKind == JsonValueKind.String) result.Add(ExpandPlaceholders(v.GetString() ?? "", config));
        }
        return result;
    }

    private static bool ArgumentObjectAllowed(JsonElement item)
    {
        if (!item.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = RuleMatches(rule);
            if (!matches) continue;
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            allowed = action == "allow";
        }
        return allowed;
    }

    private static bool RuleMatches(JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            // os.name: windows / linux / osx — a futó platformhoz illesztve.
            if (os.TryGetProperty("name", out var name) && !Platform.LauncherPlatform.OsMatches(name.GetString())) return false;
            if (os.TryGetProperty("arch", out var arch) &&
                !Platform.LauncherPlatform.ArchitectureMatches(arch.GetString())) return false;
            if (os.TryGetProperty("version", out var ver) && !string.IsNullOrWhiteSpace(ver.GetString()))
            {
                try { if (!Regex.IsMatch(Environment.OSVersion.VersionString, ver.GetString()!)) return false; } catch { }
            }
        }
        // Feature-gated args (demo, custom resolution, quick play) are disabled unless explicitly implemented.
        if (rule.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
        {
            foreach (var f in features.EnumerateObject())
            {
                if (f.Value.ValueKind == JsonValueKind.True) return false;
            }
        }
        return true;
    }

    private static bool IsRuleSetAllowed(List<LibraryRule>? rules)
    {
        if (rules == null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            var matches = Platform.LauncherPlatform.OsMatches(rule.Os?.Name);
            if (matches) allowed = rule.Action == "allow";
        }
        return allowed;
    }

    private string ExpandPlaceholders(string value, LaunchConfig c)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${natives_directory}"] = c.NativesDirectory,
            ["${launcher_name}"] = "TurulLauncher",
            ["${launcher_version}"] = "0.1",
            ["${classpath}"] = BuildClasspath(c),
            ["${auth_player_name}"] = c.Username,
            ["${version_name}"] = c.Version,
            ["${game_directory}"] = c.GameDirectory,
            ["${assets_root}"] = c.AssetsDirectory,
            ["${assets_index_name}"] = c.AssetIndexId ?? c.Version,
            ["${auth_uuid}"] = c.Uuid,
            ["${auth_access_token}"] = c.AccessToken,
            ["${auth_session}"] = c.AccessToken,
            ["${user_type}"] = c.UserType,
            ["${version_type}"] = c.VersionType,
            ["${user_properties}"] = "{}",
            ["${profile_properties}"] = "{}",
            ["${clientid}"] = "",
            ["${auth_xuid}"] = ""
        };
        foreach (var kv in map) value = value.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
        return value;
    }

    private bool ManifestJvmContainsClasspath(LaunchConfig config)
    {
        if (config.VersionArguments.ValueKind != JsonValueKind.Object || !config.VersionArguments.TryGetProperty("jvm", out var jvm)) return false;
        return jvm.ToString().Contains("${classpath}", StringComparison.Ordinal) ||
               ExpandVersionArgumentGroup(config, "jvm").Any(x => x == "-cp" || x == "-classpath");
    }

    private static string ResolveArtifactPath(string mavenName, string? manifestPath, string librariesDir)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath)) return Path.Combine(librariesDir, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        return ResolveMavenPath(mavenName, librariesDir);
    }

    private static string ResolveMavenPath(string mavenName, string librariesDir)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 3) return Path.Combine(librariesDir, mavenName.Replace(':', Path.DirectorySeparatorChar) + ".jar");
        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return Path.Combine(librariesDir, group, artifact, version, $"{artifact}-{version}{classifier}.jar");
    }

    private static string QuoteForLog(string arg) => arg.Contains(' ') ? '"' + arg + '"' : arg;
}
