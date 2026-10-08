using System.Linq;
using System.Text;

namespace TurulMC.Core.Diagnostics;

/// <summary>Outcome of a single diagnostic check.</summary>
public enum DoctorStatus
{
    Ok,
    Warning,
    Failed,
    Skipped
}

/// <summary>Suggested one-click fix for a check that reported a problem.</summary>
public enum DoctorFix
{
    None,
    InstallJava,
    UseDetectedJava,
    OpenInstanceFolder,
    OpenLogsFolder,
    CreateSupportBundle,
    FreeDiskSpace,
    ResetSettings,
    RecheckNetwork
}

/// <summary>One diagnostic check with its result, timing and fix suggestion.</summary>
public sealed class DoctorCheck
{
    /// <summary>Stable technical identifier (for example <c>java-probe</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human readable Hungarian title.</summary>
    public string TitleHu { get; init; } = string.Empty;

    /// <summary>Human readable English title.</summary>
    public string TitleEn { get; init; } = string.Empty;

    /// <summary>Result state of the check.</summary>
    public DoctorStatus Status { get; set; } = DoctorStatus.Skipped;

    /// <summary>Short Hungarian explanation of the result.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Optional Hungarian hint describing the suggested fix.</summary>
    public string? FixHintHu { get; set; }

    /// <summary>Optional English hint describing the suggested fix.</summary>
    public string? FixHintEn { get; set; }

    /// <summary>Fix action the GUI or CLI can offer for this check.</summary>
    public DoctorFix Fix { get; set; } = DoctorFix.None;

    /// <summary>Wall clock duration of the check in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>True when the check reported a warning or a failure.</summary>
    public bool IsProblem => Status is DoctorStatus.Warning or DoctorStatus.Failed;
}

/// <summary>Aggregated result of a doctor run.</summary>
public sealed class DoctorReport
{
    /// <summary>Timestamp of the run.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Version string of the running launcher.</summary>
    public string LauncherVersion { get; init; } = string.Empty;

    /// <summary>One line OS / runtime / architecture summary.</summary>
    public string EnvironmentSummary { get; init; } = string.Empty;

    /// <summary>Completed checks in execution order.</summary>
    public List<DoctorCheck> Checks { get; init; } = new();

    /// <summary>Number of checks that passed.</summary>
    public int OkCount => Checks.Count(c => c.Status == DoctorStatus.Ok);

    /// <summary>Number of checks that reported a warning.</summary>
    public int WarningCount => Checks.Count(c => c.Status == DoctorStatus.Warning);

    /// <summary>Number of checks that failed.</summary>
    public int FailedCount => Checks.Count(c => c.Status == DoctorStatus.Failed);

    /// <summary>Number of checks that were skipped.</summary>
    public int SkippedCount => Checks.Count(c => c.Status == DoctorStatus.Skipped);

    /// <summary>True when nothing failed; warnings do not block.</summary>
    public bool Healthy => FailedCount == 0;

    /// <summary>One line Hungarian summary, for example "Doctor: 1 hiba, 2 figyelmeztetés".</summary>
    public string SummaryHu
    {
        get
        {
            var parts = new List<string>();
            if (FailedCount > 0) parts.Add($"{FailedCount} hiba");
            if (WarningCount > 0) parts.Add($"{WarningCount} figyelmeztetés");
            return parts.Count == 0 ? "Doctor: minden rendben" : $"Doctor: {string.Join(", ", parts)}";
        }
    }

    /// <summary>
    /// Multi-line plain text report. ASCII markers and layout, Hungarian wording;
    /// used by the CLI and written into the support bundle as <c>doctor.txt</c>.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("TurulLauncher Doctor");
        sb.AppendLine($"Generálva: {GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} (UTC)");
        sb.AppendLine($"Launcher: {(string.IsNullOrWhiteSpace(LauncherVersion) ? "ismeretlen" : LauncherVersion)}");
        sb.AppendLine($"Környezet: {EnvironmentSummary}");
        sb.AppendLine($"Összesítés: {SummaryHu}");
        sb.AppendLine(new string('-', 64));

        foreach (var check in Checks)
        {
            sb.AppendLine($"[{StatusTag(check.Status)}] {check.Id} - {check.TitleHu} / {check.TitleEn} ({check.DurationMs} ms)");
            if (!string.IsNullOrWhiteSpace(check.Detail))
                sb.AppendLine($"    {check.Detail}");
            if (check.Fix != DoctorFix.None)
                sb.AppendLine($"    Fix: {check.Fix}");
            if (!string.IsNullOrWhiteSpace(check.FixHintHu))
                sb.AppendLine($"    Tipp: {check.FixHintHu}");
            if (!string.IsNullOrWhiteSpace(check.FixHintEn))
                sb.AppendLine($"    Hint: {check.FixHintEn}");
        }

        sb.AppendLine(new string('-', 64));
        sb.AppendLine($"Doctor: {OkCount} OK, {WarningCount} figyelmeztetés, {FailedCount} hiba, {SkippedCount} kihagyva");
        return sb.ToString();
    }

    private static string StatusTag(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "OK  ",
        DoctorStatus.Warning => "WARN",
        DoctorStatus.Failed => "FAIL",
        _ => "SKIP"
    };
}

/// <summary>
/// Inputs of a doctor run. Everything Windows-specific (registry, WebView2) is
/// injected through delegates so the service stays platform neutral.
/// </summary>
public sealed class DoctorOptions
{
    /// <summary>Launcher version shown in the report.</summary>
    public string LauncherVersion { get; set; } = string.Empty;

    /// <summary>Launcher data directory, normally <c>%APPDATA%\TurulMC</c>.</summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>Path of <c>settings.json</c>; null skips the check.</summary>
    public string? SettingsFilePath { get; set; }

    /// <summary>Path of <c>instances.json</c>; null skips the check.</summary>
    public string? InstancesFilePath { get; set; }

    /// <summary>Path of <c>profiles.json</c>; used by the support bundle.</summary>
    public string? ProfilesFilePath { get; set; }

    /// <summary>Directory that holds the launcher log files.</summary>
    public string? LogsDirectory { get; set; }

    /// <summary>Root of the active instance, may be null when none is selected.</summary>
    public string? InstanceDirectory { get; set; }

    /// <summary>Game directory of the active instance, may be null.</summary>
    public string? GameDirectory { get; set; }

    /// <summary>Java path configured by the user, may be null.</summary>
    public string? ConfiguredJavaPath { get; set; }

    /// <summary>Java major version required by the active instance; 0 means unknown.</summary>
    public int RequiredJavaMajor { get; set; }

    /// <summary>Optional Minecraft server host to ping.</summary>
    public string? ServerHost { get; set; }

    /// <summary>Minecraft server port, defaults to 25565.</summary>
    public int ServerPort { get; set; } = 25565;

    /// <summary>Injected by the GUI; when null the WebView2 check is skipped.</summary>
    public Func<string?>? WebView2VersionProvider { get; set; }

    /// <summary>Optional clock override, mainly for tests.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>Reserved: verify modpack file hashes when true.</summary>
    public bool DeepInstanceCheck { get; set; }
}

/// <summary>Result of a support bundle creation.</summary>
public sealed class SupportBundleResult
{
    /// <summary>True when the ZIP was written successfully.</summary>
    public bool Success { get; set; }

    /// <summary>File name of the bundle (<c>TurulSupport-yyyyMMdd-HHmmss.zip</c>).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Absolute path of the produced ZIP.</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>Size of the produced ZIP in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Sorted relative entry names inside the ZIP.</summary>
    public List<string> IncludedFiles { get; set; } = new();

    /// <summary>Hungarian error description when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}
