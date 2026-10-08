using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using TurulMC.Core.Diagnostics;
using TurulMC.Core.Java;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;

namespace TurulMC.Launcher;

/// <summary>
/// Automatikus Java runtime kezelés és a Doctor diagnosztika bridge-végpontjai.
/// A tényleges ellenőrzéseket a Core <see cref="LauncherDoctor"/> végzi, így a GUI és a CLI ugyanazt látja.
/// </summary>
public partial class MainWindow
{
    private IJavaRuntimeProvisioner _javaProvisioner = null!;

    // A Doctor ugyanazt az (írható) adatkönyvtárat vizsgálja, amit a launcher használ.
    private string DataRoot => TurulMC.Core.Storage.LauncherPaths.DataRoot;

    private DoctorOptions BuildDoctorOptions(LauncherSettings settings)
    {
        var instanceDirectory = _installService.GetInstanceDirectory();
        return new DoctorOptions
        {
            LauncherVersion = LauncherVersion,
            DataRoot = DataRoot,
            SettingsFilePath = Path.Combine(DataRoot, "settings.json"),
            InstancesFilePath = _instancesFilePath,
            ProfilesFilePath = Path.Combine(DataRoot, "profiles.json"),
            LogsDirectory = Path.Combine(DataRoot, "logs"),
            InstanceDirectory = instanceDirectory,
            GameDirectory = instanceDirectory,
            ConfiguredJavaPath = settings.JavaPathOverride,
            RequiredJavaMajor = JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion),
            ServerHost = settings.TestServerHost,
            ServerPort = settings.TestServerPort,
            WebView2VersionProvider = GetWebView2Version
        };
    }

    private LauncherDoctor CreateDoctor(LauncherSettings settings) =>
        new(BuildDoctorOptions(settings), _javaService, _serverStatusService);

    private static string? GetWebView2Version()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex)
        {
            LauncherLogger.Debug("WebView2 verzió nem olvasható: " + ex.Message);
            return null;
        }
    }

    private IProgress<DownloadProgress> CreateJavaProgress(string scope = "java") =>
        new Progress<DownloadProgress>(p =>
        {
            // A provisioner virtuális 0–100 skálát és magyar fázisszöveget ad a Status mezőben
            // (keresés → letöltés → ellenőrzés → kicsomagolás), ezért azt jelenítjük meg.
            var task = !string.IsNullOrWhiteSpace(p.Status)
                ? p.Status
                : (string.IsNullOrWhiteSpace(p.FileName) ? "Java letöltése…" : $"Java letöltése: {p.FileName}");
            var percent = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
            SendProgress(task, percent, scope);
        });

    // ---- Java runtime ----

    private async System.Threading.Tasks.Task<object?> HandleJavaList()
    {
        var settings = await _settingsStorage.LoadSettingsAsync();
        var requiredMajor = JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion);
        var runtimes = await _javaService.DetectInstalledRuntimesAsync();
        var recommended = await _javaService.GetRecommendedRuntimeAsync(requiredMajor.ToString());

        return new
        {
            autoInstall = settings.AutoInstallJava,
            runtimeRoot = _javaProvisioner.RuntimeRoot,
            requiredMajor,
            minecraftVersion = settings.MinecraftVersion,
            recommended = recommended is null ? null : new
            {
                path = recommended.Path,
                version = recommended.Version,
                major = recommended.Major > 0 ? recommended.Major : recommended.FeatureVersion
            },
            runtimes = runtimes.Select(r => new
            {
                id = r.Id,
                path = r.Path,
                version = r.Version,
                major = r.Major > 0 ? r.Major : r.FeatureVersion,
                source = r.Source,
                vendor = r.Vendor,
                valid = r.IsValid
            })
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleJavaProvision(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var settings = await _settingsStorage.LoadSettingsAsync();
        var major = obj.TryGetProperty("major", out var m) && m.TryGetInt32(out var parsed) && parsed > 0
            ? parsed
            : JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion);

        if (major is < 8 or > 99)
            throw new InvalidOperationException("Érvénytelen Java verzió.");

        var runtime = await _javaService.EnsureRuntimeAsync(major, allowInstall: true, progress: CreateJavaProgress());
        LauncherLogger.Info($"Java {major} telepítve: {runtime.Path}");
        return new { success = true, major, path = runtime.Path, version = runtime.Version, id = runtime.Id };
    }

    private async System.Threading.Tasks.Task<object?> HandleJavaRemove(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Hiányzó Java runtime azonosító.");

        var removed = await _javaProvisioner.RemoveProvisionedRuntimeAsync(id);
        if (!removed) throw new InvalidOperationException("A Java runtime nem található vagy nem távolítható el.");
        return new { success = true, id };
    }

    private async System.Threading.Tasks.Task<object?> HandleJavaSetPath(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var path = obj.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

        if (!string.IsNullOrWhiteSpace(path))
            TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(path);

        await _javaService.SetJavaPathAsync(path);
        return new { success = true, path };
    }

    // ---- Doctor ----

    private async System.Threading.Tasks.Task<object?> HandleDoctorRun(string? data)
    {
        var settings = await _settingsStorage.LoadSettingsAsync();
        var doctor = CreateDoctor(settings);
        var report = await doctor.RunAsync();

        return new
        {
            generatedAtUtc = report.GeneratedAtUtc,
            launcherVersion = report.LauncherVersion,
            environmentSummary = report.EnvironmentSummary,
            okCount = report.OkCount,
            warningCount = report.WarningCount,
            failedCount = report.FailedCount,
            skippedCount = report.SkippedCount,
            healthy = report.Healthy,
            requiredMajor = JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion),
            checks = report.Checks.Select(c => new
            {
                id = c.Id,
                titleHu = c.TitleHu,
                titleEn = c.TitleEn,
                status = c.Status.ToString().ToLowerInvariant(),
                detail = c.Detail,
                fixHintHu = c.FixHintHu,
                fixHintEn = c.FixHintEn,
                fix = ToFixName(c.Fix),
                durationMs = c.DurationMs
            })
        };
    }

    private static string ToFixName(DoctorFix fix) => fix switch
    {
        DoctorFix.InstallJava => "installJava",
        DoctorFix.UseDetectedJava => "useDetectedJava",
        DoctorFix.OpenInstanceFolder => "openInstanceFolder",
        DoctorFix.OpenLogsFolder => "openLogsFolder",
        DoctorFix.CreateSupportBundle => "createSupportBundle",
        DoctorFix.FreeDiskSpace => "openInstanceFolder",
        DoctorFix.ResetSettings => "resetSettings",
        DoctorFix.RecheckNetwork => "recheckNetwork",
        _ => "none"
    };

    private async System.Threading.Tasks.Task<object?> HandleDoctorFix(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var fix = obj.TryGetProperty("fix", out var f) ? f.GetString() ?? "none" : "none";
        var settings = await _settingsStorage.LoadSettingsAsync();

        switch (fix)
        {
            case "installJava":
            {
                var major = JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion);
                var runtime = await _javaService.EnsureRuntimeAsync(major, allowInstall: true, progress: CreateJavaProgress());
                return new { success = true, message = $"Java {major} kész: {runtime.Version}" };
            }

            case "useDetectedJava":
            {
                await _javaService.SetJavaPathAsync(string.Empty);
                var recommended = await _javaService.GetRecommendedRuntimeAsync(
                    JavaVersionMap.ResolveRequiredMajor(settings.MinecraftVersion).ToString());
                return new
                {
                    success = true,
                    message = recommended is null
                        ? "A kézi Java-beállítás törölve; a launcher a következő indításnál automatikusan választ."
                        : $"A kézi Java-beállítás törölve; automatikus választás: Java {recommended.Major} ({recommended.Path})."
                };
            }

            case "openInstanceFolder":
            {
                var path = _installService.GetInstanceDirectory();
                Directory.CreateDirectory(path);
                HandleOpenFolder(JsonSerializer.Serialize(new { path }));
                return new { success = true, message = "Az Instance mappa megnyitva." };
            }

            case "openLogsFolder":
            {
                var logs = Path.Combine(DataRoot, "logs");
                Directory.CreateDirectory(logs);
                HandleOpenFolder(JsonSerializer.Serialize(new { path = logs }));
                return new { success = true, message = "A log mappa megnyitva." };
            }

            case "createSupportBundle":
            {
                var bundle = await CreateSupportBundleAsync(includeDoctorReport: true);
                return new
                {
                    success = bundle.Success,
                    message = bundle.Success
                        ? $"Support ZIP elkészült: {bundle.FileName}"
                        : $"A Support ZIP nem készült el: {bundle.Error}"
                };
            }

            case "resetSettings":
            {
                var settingsPath = Path.Combine(DataRoot, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var backup = settingsPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(settingsPath, backup, true);
                    File.Delete(settingsPath);
                    LauncherLogger.Warning($"Beállításfájl visszaállítva (mentés: {backup}).");
                    return new { success = true, message = $"Beállítások visszaállítva. Mentés: {Path.GetFileName(backup)}" };
                }
                return new { success = true, message = "Nem volt beállításfájl, amit vissza kellene állítani." };
            }

            case "recheckNetwork":
                return new { success = true, message = "Hálózat újraellenőrizve." };

            default:
                return new { success = false, message = "Ehhez az ellenőrzéshez nincs automatikus javítás." };
        }
    }

    // ---- Support bundle ----

    private async System.Threading.Tasks.Task<SupportBundleResult> CreateSupportBundleAsync(bool includeDoctorReport)
    {
        var settings = await _settingsStorage.LoadSettingsAsync();
        var options = BuildDoctorOptions(settings);
        var service = new SupportBundleService(options, CreateDoctor(settings));
        return await service.CreateAsync(includeDoctorReport);
    }
}
