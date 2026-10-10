using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using TurulMC.Core.Java;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Networking;
using TurulMC.Core.Security;

namespace TurulMC.Core.Diagnostics;

/// <summary>
/// Shared diagnostic runner used by the GUI bridge and the CLI. Checks run
/// sequentially in a fixed order and a failing check never escapes as an
/// exception; it becomes a <see cref="DoctorStatus.Failed"/> result instead.
/// </summary>
public sealed class LauncherDoctor
{
    private const int ProbeTimeoutSeconds = 8;
    private const int NetworkTimeoutSeconds = 8;
    private const int MaxNetworkProbeBytes = 64 * 1024;
    private const int MaxListedRuntimes = 5;
    private const long DiskWarningBytes = 2L * 1024 * 1024 * 1024;
    private const long DiskFailedBytes = 512L * 1024 * 1024;
    private const long LogsMaxDirectoryBytes = 200L * 1024 * 1024;
    private const string WriteProbeFileName = "doctor-write-probe.tmp";

    private const string MojangManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ModrinthLoadersUrl = "https://api.modrinth.com/v2/tag/loader";
    private const string UpdateManifestUrl = "https://turulnetwork.hu/launcher/update/stable.json";

    /// <summary>Hard-coded allow list; a request is only built for these hosts.</summary>
    private static readonly string[] AllowedHosts =
    {
        "piston-meta.mojang.com",
        "api.modrinth.com",
        "turulnetwork.hu"
    };

    private static readonly TimeSpan LogsMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Fallback client: véges timeout biztonsági hálóként, a per-request
    /// CancellationTokenSource az elsődleges korlát.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly DoctorOptions _options;
    private readonly IJavaRuntimeService _javaService;
    private readonly IServerStatusService? _serverStatusService;
    private readonly HttpClient _httpClient;

    /// <summary>Creates a doctor for one launcher configuration.</summary>
    /// <param name="options">Paths, versions and injected delegates.</param>
    /// <param name="javaService">Java detection service.</param>
    /// <param name="serverStatusService">Optional server status service for the ping check.</param>
    /// <param name="httpClient">Optional HTTP client; a shared one is used when null.</param>
    public LauncherDoctor(
        DoctorOptions options,
        IJavaRuntimeService javaService,
        IServerStatusService? serverStatusService = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _javaService = javaService ?? throw new ArgumentNullException(nameof(javaService));
        _serverStatusService = serverStatusService;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <summary>
    /// Runs every check in a deterministic order and reports each finished check
    /// through <paramref name="progress"/>.
    /// </summary>
    /// <param name="progress">Optional progress sink invoked once per check.</param>
    /// <param name="cancellationToken">Cancels outstanding work; the run still returns a report.</param>
    public async Task<DoctorReport> RunAsync(
        IProgress<DoctorCheck>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        var detectedRuntimes = new List<JavaRuntime>();

        checks.Add(await RunCheckAsync("system", "Rendszer", "System",
            (check, _) =>
            {
                CheckSystem(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("gpu-driver", "GPU driver", "GPU driver",
            (check, ct) => CheckGpuDriverAsync(check, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("settings-json", "Beállításfájl", "Settings file",
            (check, ct) => CheckJsonFileAsync(
                check, _options.SettingsFilePath, "A beállításfájl", "beállításfájl", false, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("instances-json", "Instance lista", "Instance list",
            (check, ct) => CheckJsonFileAsync(
                check, _options.InstancesFilePath, "Az instance lista", "instance lista", true, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("java-configured", "Beállított Java", "Configured Java",
            (check, _) =>
            {
                CheckConfiguredJava(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("java-detected", "Telepített Java-k", "Detected Java runtimes",
            (check, _) => CheckDetectedJavaAsync(check, detectedRuntimes),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("java-required", "Szükséges Java verzió", "Required Java version",
            (check, _) =>
            {
                CheckRequiredJava(check, detectedRuntimes);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("java-probe", "Java próba", "Java probe",
            (check, ct) => CheckJavaProbeAsync(check, detectedRuntimes, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("instance-dir", "Instance mappa", "Instance folder",
            (check, _) =>
            {
                CheckInstanceDirectory(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("write-access", "Írási jog", "Write access",
            (check, ct) => CheckWriteAccessAsync(check, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("disk-space", "Szabad lemezterület", "Free disk space",
            (check, _) =>
            {
                CheckDiskSpace(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("deps-vcpp", "VC++ függőségek", "VC++ dependencies",
            (check, _) =>
            {
                CheckVcppDependencies(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("deps-webview2", "WebView2", "WebView2",
            (check, _) =>
            {
                CheckWebView2(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        checks.Add(await RunCheckAsync("net-mojang", "Mojang elérés", "Mojang reachability",
            (check, ct) => CheckEndpointAsync(check, "Mojang", MojangManifestUrl, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("net-modrinth", "Modrinth elérés", "Modrinth reachability",
            (check, ct) => CheckEndpointAsync(check, "Modrinth", ModrinthLoadersUrl, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("net-update", "Frissítés-szerver", "Update server",
            (check, ct) => CheckEndpointAsync(check, "A frissítés-szerver", UpdateManifestUrl, ct),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("net-server", "Minecraft szerver", "Minecraft server",
            (check, _) => CheckServerStatusAsync(check),
            progress, cancellationToken));

        checks.Add(await RunCheckAsync("logs", "Naplók", "Logs",
            (check, _) =>
            {
                CheckLogs(check);
                return Task.CompletedTask;
            }, progress, cancellationToken));

        var report = new DoctorReport
        {
            GeneratedAtUtc = Now(),
            LauncherVersion = _options.LauncherVersion ?? string.Empty,
            EnvironmentSummary = BuildEnvironmentSummary(_options.LauncherVersion),
            Checks = checks
        };

        LauncherLogger.Info($"Doctor kész: {report.SummaryHu}");
        return report;
    }

    /// <summary>
    /// Executes one check, times it and converts any escaping exception into a
    /// failed result.
    /// </summary>
    private async Task<DoctorCheck> RunCheckAsync(
        string id,
        string titleHu,
        string titleEn,
        Func<DoctorCheck, CancellationToken, Task> body,
        IProgress<DoctorCheck>? progress,
        CancellationToken cancellationToken)
    {
        var check = new DoctorCheck
        {
            Id = id,
            TitleHu = titleHu,
            TitleEn = titleEn,
            Status = DoctorStatus.Skipped
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await body(check, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            check.Status = DoctorStatus.Failed;
            check.Detail = "A vizsgálat megszakítva.";
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Detail = $"Váratlan hiba a vizsgálat közben – {ex.GetType().Name}: {ex.Message}";
            if (check.Fix == DoctorFix.None)
            {
                check.Fix = DoctorFix.CreateSupportBundle;
                check.FixHintHu = "Készíts támogatási csomagot, és küldd el a Discord szerveren.";
                check.FixHintEn = "Create a support bundle and send it on Discord.";
            }

            LauncherLogger.Error($"Doctor check hiba ({id})", ex);
        }
        finally
        {
            stopwatch.Stop();
            check.DurationMs = stopwatch.ElapsedMilliseconds;
            check.Detail = Redact(check.Detail) ?? string.Empty;
            check.FixHintHu = Redact(check.FixHintHu);
            check.FixHintEn = Redact(check.FixHintEn);
        }

        try
        {
            progress?.Report(check);
        }
        catch (Exception ex)
        {
            // A hibás progress handler nem boríthatja a teljes doctor futást.
            LauncherLogger.Warning($"Doctor progress hiba ({id}): {ex.Message}");
        }

        return check;
    }

    // ---------------------------------------------------------------------
    // system
    // ---------------------------------------------------------------------

    private void CheckSystem(DoctorCheck check)
    {
        check.Status = DoctorStatus.Ok;
        check.Detail =
            $"{BuildEnvironmentSummary(_options.LauncherVersion)}; " +
            $"64 bites rendszer: {YesNo(Environment.Is64BitOperatingSystem)}; " +
            $"OS architektúra: {RuntimeInformation.OSArchitecture}";
    }

    private static string BuildEnvironmentSummary(string? launcherVersion)
    {
        var version = string.IsNullOrWhiteSpace(launcherVersion) ? "ismeretlen" : launcherVersion;
        return $"{DescribeOperatingSystem()}; .NET {Environment.Version}; " +
               $"{RuntimeInformation.ProcessArchitecture}; launcher {version}";
    }

    private static string DescribeOperatingSystem()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
                return $"{name} ({version.Major}.{version.Minor}.{version.Build})";
            }

            return $"{RuntimeInformation.OSDescription.Trim()} ({version})";
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"OS leírás hiba: {ex.Message}");
            return "ismeretlen operációs rendszer";
        }
    }

    // ---------------------------------------------------------------------
    // GPU driver (Nouveau-figyelmeztetés Linuxon)
    // ---------------------------------------------------------------------

    private static async Task CheckGpuDriverAsync(
        DoctorCheck check, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "A GPU-driver ellenőrzés csak Linuxon fut.";
            return;
        }

        GpuDriverReport report;
        try
        {
            report = await GpuDriverInfo.ProbeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = $"A GPU-driver nem kérdezhető le – {ex.GetType().Name}: {ex.Message}";
            return;
        }

        var renderer = string.IsNullOrWhiteSpace(report.Renderer)
            ? ""
            : $" (renderer: {report.Renderer})";

        if (report.NouveauDetected)
        {
            check.Status = DoctorStatus.Warning;
            check.Detail =
                $"Nouveau nyílt NVIDIA driver aktív{renderer}. " +
                "A játék ettől még elindul, de fagyás, villogás vagy " +
                "alacsony FPS előfordulhat.";
            check.FixHintHu = "Stabilabb működéshez telepítsd az NVIDIA zárt driverét " +
                "(pl. a disztribúció „további driverek” eszközével).";
            check.FixHintEn = "For stable gameplay install the proprietary NVIDIA driver.";
            return;
        }

        check.Status = DoctorStatus.Ok;
        check.Detail = string.IsNullOrWhiteSpace(report.Renderer)
            ? "Nouveau driver nem észlelhető."
            : $"Nouveau driver nem észlelhető ({report.Renderer}).";
    }

    // ---------------------------------------------------------------------
    // settings.json / instances.json
    // ---------------------------------------------------------------------

    private async Task CheckJsonFileAsync(
        DoctorCheck check,
        string? path,
        string labelHu,
        string nounHu,
        bool countInstances,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = $"Nincs megadva {nounHu} útvonal.";
            return;
        }

        var fileName = SafeFileName(path);
        if (!File.Exists(path))
        {
            check.Status = DoctorStatus.Warning;
            check.Detail = $"Még nincs {nounHu} ({fileName}).";
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Detail = $"{labelHu} nem olvasható – {ex.GetType().Name}: {ex.Message}";
            check.FixHintHu = "Zárd be a launchert, majd próbáld újra.";
            check.FixHintEn = "Close the launcher and try again.";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var detail = countInstances
                ? BuildInstancesDetail(document.RootElement)
                : $"{labelHu} rendben";

            var backup = FindSiblingBackup(path);
            if (backup is not null)
                detail += $"; biztonsági másolat is van: {backup}";

            check.Status = DoctorStatus.Ok;
            check.Detail = $"{detail}.";
        }
        catch (JsonException ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.ResetSettings;
            check.FixHintHu = "A fájl sérült; állítsd vissza a .bak másolatot, vagy indítsd újra a launchert alapértelmezett beállításokkal.";
            check.FixHintEn = "The file is corrupted; restore the .bak copy or restart with default settings.";
            check.Detail = $"{labelHu} nem értelmezhető JSON ({fileName}) – {ex.Message}";
        }
    }

    private static string BuildInstancesDetail(JsonElement root)
    {
        var count = CountInstanceEntries(root);
        return count.HasValue
            ? $"Az instance lista rendben ({count.Value.ToString(CultureInfo.InvariantCulture)} bejegyzés)"
            : "Az instance lista rendben (a bejegyzések száma nem állapítható meg)";
    }

    private static int? CountInstanceEntries(JsonElement root)
    {
        try
        {
            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength();

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("instances", out var instances))
                return null;

            return instances.ValueKind switch
            {
                JsonValueKind.Array => instances.GetArrayLength(),
                JsonValueKind.Object => instances.EnumerateObject().Count(),
                JsonValueKind.Null => 0,
                _ => null
            };
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Instance lista feldolgozási hiba: {ex.Message}");
            return null;
        }
    }

    private static string? FindSiblingBackup(string path)
    {
        try
        {
            foreach (var suffix in new[] { ".bak", ".tmp" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    return Path.GetFileName(candidate);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Biztonsági másolat ellenőrzési hiba: {ex.Message}");
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Java
    // ---------------------------------------------------------------------

    private void CheckConfiguredJava(DoctorCheck check)
    {
        var configured = _options.ConfiguredJavaPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs beállított Java útvonal.";
            return;
        }

        if (!File.Exists(configured))
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = "A launcher a felismert Java runtime-ok egyikét tudja használni.";
            check.FixHintEn = "The launcher can use one of the detected Java runtimes instead.";
            check.Detail = $"A beállított Java nem található: {Redact(configured)}.";
            return;
        }

        if (!IsJavaExecutable(configured))
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = "Válassz java.exe vagy javaw.exe fájlt a bin mappából.";
            check.FixHintEn = "Pick java.exe or javaw.exe from the bin folder.";
            check.Detail = $"A beállított útvonal nem java.exe vagy javaw.exe: {Redact(configured)}.";
            return;
        }

        check.Status = DoctorStatus.Ok;
        check.Detail = $"A beállított Java rendben: {Redact(configured)}.";
    }

    private async Task CheckDetectedJavaAsync(DoctorCheck check, List<JavaRuntime> detected)
    {
        List<JavaRuntime> runtimes;
        try
        {
            runtimes = await _javaService.DetectInstalledRuntimesAsync();
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.CreateSupportBundle;
            check.FixHintHu = "Készíts támogatási csomagot, hogy lássuk a Java keresés részleteit.";
            check.FixHintEn = "Create a support bundle so the Java scan details can be reviewed.";
            check.Detail = $"Java keresési hiba – {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (runtimes is not null && runtimes.Count > 0)
            detected.AddRange(runtimes.Where(runtime => runtime is not null));

        if (detected.Count == 0)
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.InstallJava;
            check.FixHintHu = "Telepíts Java 21-et (például Eclipse Temurin x64).";
            check.FixHintEn = "Install Java 21 (for example Eclipse Temurin x64).";
            check.Detail = "Nem található telepített Java runtime.";
            return;
        }

        var listed = detected
            .Take(MaxListedRuntimes)
            .Select(DescribeRuntime);

        check.Status = DoctorStatus.Ok;
        check.Detail =
            $"{detected.Count.ToString(CultureInfo.InvariantCulture)} Java runtime: " +
            string.Join("; ", listed) + ".";
    }

    private void CheckRequiredJava(DoctorCheck check, IReadOnlyList<JavaRuntime> detected)
    {
        var required = _options.RequiredJavaMajor;
        if (required <= 0)
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs megadva szükséges Java verzió.";
            return;
        }

        var compatible = detected
            .Select(runtime => new
            {
                Runtime = runtime,
                Major = JavaRuntimeService.ParseJavaMajor(runtime.Version)
            })
            .Where(item => IsCompatibleJava(item.Major, required))
            .OrderByDescending(item => item.Major)
            .FirstOrDefault();

        if (compatible is not null)
        {
            check.Status = DoctorStatus.Ok;
            check.Detail = $"A szükséges Java {required} verzió elérhető: {DescribeRuntime(compatible.Runtime)}.";
            return;
        }

        var configured = _options.ConfiguredJavaPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = $"Ellenőrizd, hogy a beállított Java {required} verziójú-e.";
            check.FixHintEn = $"Check that the configured Java is version {required}.";
            check.Detail =
                $"A felismert Java-k egyike sem felel meg a {required} verziónak, " +
                $"de saját Java útvonal van beállítva ({Redact(configured)}).";
            return;
        }

        check.Status = DoctorStatus.Failed;
        check.Fix = DoctorFix.InstallJava;
        check.FixHintHu = $"Telepíts Java {required} verziót (például Eclipse Temurin x64).";
        check.FixHintEn = $"Install Java {required} (for example Eclipse Temurin x64).";
        check.Detail =
            $"Nincs a Java {required} verziót kielégítő runtime. " +
            $"Telepített verziók: {DescribeDetectedMajors(detected)}.";
    }

    /// <summary>
    /// Minecraft kompatibilitási szabály — egyetlen forrás: <see cref="JavaVersionMap.IsCompatible"/>
    /// (pontos egyezés, vagy a követelménynél újabb Java, ha a követelmény legalább 17).
    /// </summary>
    private static bool IsCompatibleJava(int actualMajor, int requiredMajor)
    {
        if (actualMajor <= 0 || requiredMajor <= 0)
            return false;

        return JavaVersionMap.IsCompatible(actualMajor, requiredMajor);
    }

    private async Task CheckJavaProbeAsync(
        DoctorCheck check,
        IReadOnlyList<JavaRuntime> detected,
        CancellationToken cancellationToken)
    {
        var runtimePath = SelectProbeRuntime(detected);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs telepített vagy beállított Java, nincs mit próbálni.";
            return;
        }

        var displayPath = Redact(runtimePath);
        var result = await RunJavaVersionAsync(runtimePath, cancellationToken);

        if (result.TimedOut)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = "Próbálj másik Java runtime-ot választani.";
            check.FixHintEn = "Try selecting another Java runtime.";
            check.Detail = $"A Java nem válaszolt {ProbeTimeoutSeconds} másodpercen belül ({displayPath}).";
            return;
        }

        if (result.Cancelled)
        {
            check.Status = DoctorStatus.Failed;
            check.Detail = $"A Java próba megszakítva ({displayPath}).";
            return;
        }

        if (!result.Started)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = "Próbálj másik Java runtime-ot választani.";
            check.FixHintEn = "Try selecting another Java runtime.";
            check.Detail = $"A Java nem indítható ({displayPath}) – {result.Error}.";
            return;
        }

        var versionLine = ExtractVersionLine(result.StandardError, result.StandardOutput);
        if (versionLine is not null)
        {
            check.Status = DoctorStatus.Ok;
            check.Detail = $"A Java válaszol: {versionLine} ({displayPath}).";
            return;
        }

        if (result.ExitCode != 0)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.UseDetectedJava;
            check.FixHintHu = "A Java telepítése sérült lehet; telepítsd újra vagy válassz másikat.";
            check.FixHintEn = "The Java installation may be broken; reinstall it or pick another one.";
            check.Detail = $"A Java hibás kilépési kóddal állt le: {result.ExitCode} ({displayPath}).";
            return;
        }

        check.Status = DoctorStatus.Warning;
        check.Detail = $"A Java elindult, de a verzió nem olvasható ki ({displayPath}).";
    }

    private string? SelectProbeRuntime(IReadOnlyList<JavaRuntime> detected)
    {
        var configured = _options.ConfiguredJavaPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured) && IsJavaExecutable(configured))
            return configured;

        var candidates = detected
            .Where(runtime => runtime is not null && !string.IsNullOrWhiteSpace(runtime.Path))
            .ToList();

        var required = _options.RequiredJavaMajor;
        if (required > 0)
        {
            var compatible = candidates
                .Where(runtime => IsCompatibleJava(JavaRuntimeService.ParseJavaMajor(runtime.Version), required))
                .OrderByDescending(runtime => JavaRuntimeService.ParseJavaMajor(runtime.Version))
                .FirstOrDefault();

            if (compatible is not null)
                return compatible.Path;
        }

        // Nincs követelmény: az első felismert runtime-ot próbáljuk (a szolgáltatás
        // csökkenő verziósorrendben adja vissza a listát).
        return candidates.FirstOrDefault()?.Path;
    }

    /// <summary>
    /// Runs <c>&lt;runtime&gt; -version</c> without blocking the calling thread and
    /// with a hard eight second limit.
    /// </summary>
    private static async Task<JavaProbeResult> RunJavaVersionAsync(
        string runtimePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            Arguments = "-version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return JavaProbeResult.StartFailure("a folyamat nem indult el");
        }
        catch (Exception ex)
        {
            return JavaProbeResult.StartFailure($"{ex.GetType().Name}: {ex.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            await ReadQuietlyAsync(standardOutput);
            await ReadQuietlyAsync(standardError);

            return cancellationToken.IsCancellationRequested
                ? JavaProbeResult.Cancel()
                : JavaProbeResult.Timeout();
        }

        var stdout = await ReadQuietlyAsync(standardOutput);
        var stderr = await ReadQuietlyAsync(standardError);
        return JavaProbeResult.Completed(process.ExitCode, stdout, stderr);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Java próba leállítási hiba: {ex.Message}");
        }
    }

    private static async Task<string> ReadQuietlyAsync(Task<string> reader)
    {
        try
        {
            return await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Java próba kimenet olvasási hiba: {ex.Message}");
            return string.Empty;
        }
    }

    private static string? ExtractVersionLine(string standardError, string standardOutput)
    {
        foreach (var text in new[] { standardError, standardOutput })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var quoted = Regex.Match(text, "\"([^\"]+)\"");
            if (quoted.Success && !string.IsNullOrWhiteSpace(quoted.Groups[1].Value))
                return quoted.Groups[1].Value.Trim();

            var line = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0);

            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Instance, disk, dependencies
    // ---------------------------------------------------------------------

    private void CheckInstanceDirectory(DoctorCheck check)
    {
        var directory = _options.InstanceDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs aktív instance mappa.";
            return;
        }

        if (!Directory.Exists(directory))
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.OpenInstanceFolder;
            check.FixHintHu = "Nyisd meg az instance mappát a launcherből, vagy telepítsd újra az instance-t.";
            check.FixHintEn = "Open the instance folder from the launcher or reinstall the instance.";
            check.Detail = $"Az instance mappa nem található: {Redact(directory)}.";
            return;
        }

        var modsDirectory = Path.Combine(directory, "mods");
        var modsExists = Directory.Exists(modsDirectory);
        var savesExists = Directory.Exists(Path.Combine(directory, "saves"));
        var configExists = Directory.Exists(Path.Combine(directory, "config"));
        var jarCount = CountJars(modsDirectory);

        check.Status = DoctorStatus.Ok;
        check.Detail =
            $"Instance mappa rendben ({Redact(directory)}); " +
            $"mods: {YesNo(modsExists)} ({(jarCount?.ToString(CultureInfo.InvariantCulture) ?? "0")} db .jar); " +
            $"saves: {YesNo(savesExists)}; config: {YesNo(configExists)}.";
    }

    private static int? CountJars(string modsDirectory)
    {
        try
        {
            if (!Directory.Exists(modsDirectory))
                return null;

            return Directory
                .EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                .Count();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Mods számlálási hiba: {ex.Message}");
            return null;
        }
    }

    private async Task CheckWriteAccessAsync(DoctorCheck check, CancellationToken cancellationToken)
    {
        var target = FirstNonEmpty(_options.InstanceDirectory, _options.DataRoot);
        if (string.IsNullOrWhiteSpace(target))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs írási célmappa (instance vagy adatkönyvtár).";
            return;
        }

        var display = Redact(target);
        if (!Directory.Exists(target))
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.OpenInstanceFolder;
            check.FixHintHu = "A mappa nem létezik, ezért az írás nem tesztelhető.";
            check.FixHintEn = "The folder does not exist, so writing cannot be tested.";
            check.Detail = $"Az írási teszt nem futott le, a mappa nem létezik: {display}.";
            return;
        }

        string probePath;
        try
        {
            probePath = PathSecurity.ResolveInsideRoot(target, WriteProbeFileName);
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.OpenInstanceFolder;
            check.Detail = $"Érvénytelen írási cél ({display}) – {ex.Message}";
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                probePath,
                $"TurulLauncher doctor write probe {Now():O}",
                cancellationToken);
            File.Delete(probePath);

            check.Status = DoctorStatus.Ok;
            check.Detail = $"Az írási teszt rendben ({display}, {WriteProbeFileName}).";
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.OpenInstanceFolder;
            check.FixHintHu = "Ellenőrizd a mappa jogosultságait, vagy futtasd a launchert rendszergazdaként.";
            check.FixHintEn = "Check the folder permissions or run the launcher as administrator.";
            check.Detail = $"Írási hiba ({display}) – {ex.GetType().Name}: {ex.Message}";
            TryDeleteProbe(probePath);
        }
    }

    private static void TryDeleteProbe(string probePath)
    {
        try
        {
            if (File.Exists(probePath))
                File.Delete(probePath);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Doctor írási próba törlési hiba: {ex.Message}");
        }
    }

    private void CheckDiskSpace(DoctorCheck check)
    {
        var target = FirstNonEmpty(_options.InstanceDirectory, _options.DataRoot);
        if (string.IsNullOrWhiteSpace(target))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs mappa, amelynek a lemezterülete ellenőrizhető lenne.";
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrWhiteSpace(root))
            {
                check.Status = DoctorStatus.Warning;
                check.Detail = $"Nem sikerült meghatározni a meghajtót: {Redact(target)}.";
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                check.Status = DoctorStatus.Warning;
                check.Detail = $"A meghajtó nem elérhető: {Redact(root)}.";
                return;
            }

            var freeBytes = drive.AvailableFreeSpace;
            var freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
            var sizeText = $"{freeGb.ToString("0.0", CultureInfo.InvariantCulture)} GB";
            var driveName = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (freeBytes < DiskFailedBytes)
            {
                check.Status = DoctorStatus.Failed;
                check.Fix = DoctorFix.FreeDiskSpace;
                check.FixHintHu = "Szabadíts fel legalább néhány GB-ot, különben a játék nem indul el.";
                check.FixHintEn = "Free up at least a few GB or the game will not start.";
                check.Detail = $"Kevés szabad hely: {sizeText} ({driveName}) – 512 MB alatt a játék nem indul el.";
            }
            else if (freeBytes < DiskWarningBytes)
            {
                check.Status = DoctorStatus.Warning;
                check.Fix = DoctorFix.FreeDiskSpace;
                check.FixHintHu = "A modpack frissítéséhez érdemes több szabad helyet hagyni.";
                check.FixHintEn = "Leave more free space for modpack updates.";
                check.Detail = $"Kevés szabad hely: {sizeText} ({driveName}).";
            }
            else
            {
                check.Status = DoctorStatus.Ok;
                check.Detail = $"Szabad hely: {sizeText} ({driveName}).";
            }
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.FreeDiskSpace;
            check.Detail = $"Lemezterület ellenőrzési hiba – {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void CheckVcppDependencies(DoctorCheck check)
    {
        if (!OperatingSystem.IsWindows())
        {
            check.Status = DoctorStatus.Warning;
            check.Detail = "A VC++ függőségek csak Windows alatt ellenőrizhetők.";
            return;
        }

        string systemDirectory;
        try
        {
            systemDirectory = Environment.SystemDirectory;
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Warning;
            check.Detail = $"A rendszerkönyvtár nem olvasható – {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            check.Status = DoctorStatus.Warning;
            check.Detail = "A rendszerkönyvtár nem érhető el ezen a platformon.";
            return;
        }

        var missing = new List<string>();
        foreach (var dll in new[] { "vcruntime140.dll", "msvcp140.dll" })
        {
            try
            {
                if (!File.Exists(Path.Combine(systemDirectory, dll)))
                    missing.Add(dll);
            }
            catch (Exception ex)
            {
                LauncherLogger.Debug($"VC++ ellenőrzési hiba ({dll}): {ex.Message}");
                missing.Add(dll);
            }
        }

        if (missing.Count == 0)
        {
            check.Status = DoctorStatus.Ok;
            check.Detail = "A VC++ 2015-2022 x64 futtatókörnyezet rendben (vcruntime140.dll, msvcp140.dll).";
            return;
        }

        check.Status = DoctorStatus.Warning;
        check.FixHintHu = "Telepítsd a Microsoft VC++ 2015-2022 x64 névjegyzéket (a telepítő tartalmazza).";
        check.FixHintEn = "Install the Microsoft VC++ 2015-2022 x64 redistributable (the installer ships it).";
        check.Detail = $"Hiányzó függőség: {string.Join(", ", missing)}.";
    }

    private void CheckWebView2(DoctorCheck check)
    {
        var provider = _options.WebView2VersionProvider;
        if (provider is null)
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "A WebView2 ellenőrzés csak a grafikus felületen fut.";
            return;
        }

        string? version;
        try
        {
            version = provider();
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Warning;
            check.FixHintHu = "Telepítsd a Microsoft Edge WebView2 Runtime-ot.";
            check.FixHintEn = "Install the Microsoft Edge WebView2 Runtime.";
            check.Detail = $"A WebView2 verzió nem kérdezhető le – {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "A WebView2 verzió nem érhető el ebben a folyamatban.";
            return;
        }

        check.Status = DoctorStatus.Ok;
        check.Detail = $"A WebView2 rendben: {version}.";
    }

    // ---------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------

    private async Task CheckEndpointAsync(
        DoctorCheck check,
        string label,
        string url,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRequest(url, out var request, out var error))
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.RecheckNetwork;
            check.Detail = $"{label}: {error}.";
            return;
        }

        using (request)
        {
            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(NetworkTimeoutSeconds));

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                var bytesRead = await ReadUpToAsync(response, MaxNetworkProbeBytes, timeout.Token);
                stopwatch.Stop();
                var elapsed = stopwatch.ElapsedMilliseconds;

                if (response.IsSuccessStatusCode)
                {
                    check.Status = DoctorStatus.Ok;
                    check.Detail =
                        $"{label} elérhető (HTTP {(int)response.StatusCode}, {elapsed} ms, " +
                        $"{bytesRead.ToString(CultureInfo.InvariantCulture)} byte).";
                    return;
                }

                check.Status = DoctorStatus.Failed;
                check.Fix = DoctorFix.RecheckNetwork;
                check.FixHintHu = "Ellenőrizd az internetkapcsolatot, majd futtasd újra a diagnosztikát.";
                check.FixHintEn = "Check the internet connection and run the diagnostics again.";
                check.Detail = $"{label} hibás állapotot adott: HTTP {(int)response.StatusCode} ({elapsed} ms).";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                check.Status = DoctorStatus.Failed;
                check.Fix = DoctorFix.RecheckNetwork;
                check.FixHintHu = "Ellenőrizd az internetkapcsolatot, majd futtasd újra a diagnosztikát.";
                check.FixHintEn = "Check the internet connection and run the diagnostics again.";
                check.Detail = $"{label} nem válaszolt {NetworkTimeoutSeconds} másodpercen belül.";
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                check.Status = DoctorStatus.Failed;
                check.Fix = DoctorFix.RecheckNetwork;
                check.Detail = $"{label}: a vizsgálat megszakítva.";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                check.Status = DoctorStatus.Failed;
                check.Fix = DoctorFix.RecheckNetwork;
                check.FixHintHu = "Ellenőrizd az internetkapcsolatot, majd futtasd újra a diagnosztikát.";
                check.FixHintEn = "Check the internet connection and run the diagnostics again.";
                check.Detail = $"{label}: {ex.GetType().Name} – {ex.Message}";
            }
        }
    }

    private async Task CheckServerStatusAsync(DoctorCheck check)
    {
        var host = _options.ServerHost;
        if (string.IsNullOrWhiteSpace(host) || _serverStatusService is null)
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs megadott Minecraft szerver vagy szerver-státusz szolgáltatás.";
            return;
        }

        var port = _options.ServerPort > 0 ? _options.ServerPort : 25565;
        try
        {
            var status = await _serverStatusService.CheckServerStatusAsync(host, port);

            if (status is null)
            {
                check.Status = DoctorStatus.Warning;
                check.Fix = DoctorFix.RecheckNetwork;
                check.Detail = $"{host}:{port} állapota nem kérdezhető le.";
                return;
            }

            if (status.IsOnline)
            {
                check.Status = DoctorStatus.Ok;
                check.Detail = $"{host}:{port} online – {DescribePlayers(status)} ({status.ResponseTimeMs} ms).";
                return;
            }

            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.RecheckNetwork;
            check.FixHintHu = "Ellenőrizd az internetkapcsolatot és a szerver címét, majd próbáld újra.";
            check.FixHintEn = "Check the internet connection and the server address, then try again.";
            check.Detail = string.IsNullOrWhiteSpace(status.ErrorMessage)
                ? $"{host}:{port} nem elérhető."
                : $"{host}:{port} nem elérhető – {status.ErrorMessage}";
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Failed;
            check.Fix = DoctorFix.RecheckNetwork;
            check.FixHintHu = "Ellenőrizd az internetkapcsolatot és a szerver címét, majd próbáld újra.";
            check.FixHintEn = "Check the internet connection and the server address, then try again.";
            check.Detail = $"Szerver ellenőrzési hiba – {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string DescribePlayers(ServerStatus status)
    {
        if (status.OnlinePlayers.HasValue && status.MaxPlayers.HasValue)
            return $"{status.OnlinePlayers.Value}/{status.MaxPlayers.Value} játékos";

        if (status.OnlinePlayers.HasValue)
            return $"{status.OnlinePlayers.Value} játékos";

        return "játékosszám ismeretlen";
    }

    private bool TryCreateRequest(string url, out HttpRequestMessage request, out string error)
    {
        request = null!;
        error = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "érvénytelen URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "csak HTTPS kérés engedélyezett";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            error = $"a hoszt nincs az engedélyezett listán ({uri.Host})";
            return false;
        }

        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            message.Headers.TryAddWithoutValidation("User-Agent", BuildUserAgent());

        request = message;
        return true;
    }

    private string BuildUserAgent()
    {
        var version = string.IsNullOrWhiteSpace(_options.LauncherVersion) ? "0.0.0" : _options.LauncherVersion;
        return $"TurulNetwork-TurulLauncher/{version} (doctor)";
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<long> ReadUpToAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            long total = 0;

            while (total < maxBytes)
            {
                var remaining = (int)Math.Min(buffer.Length, maxBytes - total);
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
                if (read <= 0)
                    break;

                total += read;
            }

            return total;
        }
        catch (Exception ex)
        {
            // A státuszkód már megvan a fejlécekből, a törzs csonkolása nem hiba.
            LauncherLogger.Debug($"Doctor válasz olvasási hiba: {ex.Message}");
            return 0;
        }
    }

    // ---------------------------------------------------------------------
    // Logs
    // ---------------------------------------------------------------------

    private void CheckLogs(DoctorCheck check)
    {
        var directory = _options.LogsDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            check.Status = DoctorStatus.Skipped;
            check.Detail = "Nincs megadva napló mappa.";
            return;
        }

        if (!Directory.Exists(directory))
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.OpenLogsFolder;
            check.Detail = $"Még nincs napló mappa ({Redact(directory)}).";
            return;
        }

        List<FileInfo> files;
        try
        {
            files = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .ToList();
        }
        catch (Exception ex)
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.OpenLogsFolder;
            check.Detail = $"A napló mappa nem olvasható ({Redact(directory)}) – {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (files.Count == 0)
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.OpenLogsFolder;
            check.Detail = $"A napló mappa üres ({Redact(directory)}).";
            return;
        }

        long totalBytes = 0;
        foreach (var file in files)
        {
            try
            {
                totalBytes += file.Length;
            }
            catch (Exception ex)
            {
                LauncherLogger.Debug($"Napló méret hiba ({file.Name}): {ex.Message}");
            }
        }

        var newest = files.OrderByDescending(info => info.LastWriteTimeUtc).First();
        var sizeText = $"{(totalBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        var newestText = $"{newest.Name} ({newest.LastWriteTime:yyyy-MM-dd HH:mm})";
        var age = Now().UtcDateTime - newest.LastWriteTimeUtc;

        if (age > LogsMaxAge)
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.OpenLogsFolder;
            check.FixHintHu = "A legfrissebb napló 30 napnál régebbi; indíts egy új launcher futást a friss naplóhoz.";
            check.FixHintEn = "The newest log is older than 30 days; start a new launcher run for a fresh log.";
            check.Detail =
                $"{files.Count.ToString(CultureInfo.InvariantCulture)} naplófájl, {sizeText}; " +
                $"a legfrissebb {newestText} – 30 napnál régebbi.";
            return;
        }

        if (totalBytes > LogsMaxDirectoryBytes)
        {
            check.Status = DoctorStatus.Warning;
            check.Fix = DoctorFix.OpenLogsFolder;
            check.FixHintHu = "A napló mappa 200 MB-nál nagyobb; érdemes régi naplókat törölni.";
            check.FixHintEn = "The log folder is larger than 200 MB; delete old logs.";
            check.Detail =
                $"{files.Count.ToString(CultureInfo.InvariantCulture)} naplófájl, {sizeText} – " +
                $"a mappa 200 MB-nál nagyobb. Legfrissebb: {newestText}.";
            return;
        }

        check.Status = DoctorStatus.Ok;
        check.Detail =
            $"{files.Count.ToString(CultureInfo.InvariantCulture)} naplófájl, {sizeText}; " +
            $"legfrissebb: {newestText}.";
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private DateTimeOffset Now()
    {
        try
        {
            return _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Doctor óra hiba: {ex.Message}");
            return DateTimeOffset.UtcNow;
        }
    }

    private static string DescribeRuntime(JavaRuntime runtime)
    {
        var version = string.IsNullOrWhiteSpace(runtime.Version) ? "ismeretlen verzió" : runtime.Version;
        return $"{version} — {Redact(runtime.Path)}";
    }

    private static string DescribeDetectedMajors(IReadOnlyList<JavaRuntime> detected)
    {
        var majors = detected
            .Select(runtime => JavaRuntimeService.ParseJavaMajor(runtime.Version))
            .Where(major => major > 0)
            .Distinct()
            .OrderByDescending(major => major)
            .ToList();

        return majors.Count == 0
            ? "nincs felismert Java"
            : string.Join(", ", majors.Select(major => major.ToString(CultureInfo.InvariantCulture)));
    }

    private static bool IsJavaExecutable(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Fájlnév kiolvasási hiba: {ex.Message}");
            return "ismeretlen fájl";
        }
    }

    private static string YesNo(bool value) => value ? "igen" : "nem";

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Replaces the user profile, AppData and LocalAppData prefixes with
    /// placeholders so a shared report does not leak absolute user paths.
    /// </summary>
    private static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var result = value;
        result = ReplacePrefix(result, SafeFolder(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        result = ReplacePrefix(result, SafeFolder(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        result = ReplacePrefix(result, SafeFolder(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        return result;
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Könyvtár feloldási hiba ({folder}): {ex.Message}");
            return string.Empty;
        }
    }

    private static string ReplacePrefix(string value, string prefix, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return value;

        return value.Replace(prefix, placeholder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Result of one asynchronous <c>java -version</c> probe.</summary>
    private sealed record JavaProbeResult(
        bool Started,
        bool TimedOut,
        bool Cancelled,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string Error)
    {
        public static JavaProbeResult StartFailure(string error) =>
            new(false, false, false, -1, string.Empty, string.Empty, error);

        public static JavaProbeResult Timeout() =>
            new(true, true, false, -1, string.Empty, string.Empty, string.Empty);

        public static JavaProbeResult Cancel() =>
            new(true, false, true, -1, string.Empty, string.Empty, string.Empty);

        public static JavaProbeResult Completed(int exitCode, string standardOutput, string standardError) =>
            new(true, false, false, exitCode, standardOutput, standardError, string.Empty);
    }
}
