using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Security;

namespace TurulMC.Core.Diagnostics;

/// <summary>
/// Builds a single, shareable ZIP with the newest logs, crash reports,
/// configuration files and an optional doctor report.
/// The bundle is always written to <c>&lt;DataRoot&gt;\support</c>; nothing is
/// written anywhere else and a failure never throws to the caller.
/// </summary>
public sealed class SupportBundleService
{
    private const int MaxLogFiles = 12;
    private const int MaxCrashReportsPerDirectory = 5;
    private const long MaxSingleFileBytes = 4L * 1024 * 1024;
    private const long MaxCategoryBytes = 16L * 1024 * 1024;
    private const string LogsEntryPrefix = "logs/";
    private const string CrashReportsEntryPrefix = "crash-reports/";

    private readonly DoctorOptions _options;
    private readonly LauncherDoctor? _doctor;

    /// <summary>Creates the bundle builder for one launcher configuration.</summary>
    /// <param name="options">Paths of the launcher data; <c>DataRoot</c> is required.</param>
    /// <param name="doctor">Optional doctor used to add <c>doctor.txt</c>.</param>
    public SupportBundleService(DoctorOptions options, LauncherDoctor? doctor = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _doctor = doctor;
    }

    /// <summary>
    /// Creates <c>&lt;DataRoot&gt;\support\TurulSupport-yyyyMMdd-HHmmss.zip</c>.
    /// Locked or unreadable files are skipped and logged; any other failure is
    /// reported through <see cref="SupportBundleResult.Error"/>.
    /// </summary>
    /// <param name="includeDoctorReport">When true and a doctor was supplied, adds <c>doctor.txt</c>.</param>
    /// <param name="cancellationToken">Cancels the operation; no half ZIP is left behind.</param>
    public async Task<SupportBundleResult> CreateAsync(
        bool includeDoctorReport = true,
        CancellationToken cancellationToken = default)
    {
        var result = new SupportBundleResult();
        string? tempPath = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_options.DataRoot))
            {
                result.Error = "Nincs megadva adatkönyvtár (DataRoot), a támogatási csomag nem készíthető el.";
                return result;
            }

            var supportDirectory = Path.Combine(_options.DataRoot, "support");
            Directory.CreateDirectory(supportDirectory);

            var generatedAt = Now();
            result.FileName = BuildFileName(supportDirectory, generatedAt);
            result.FullPath = Path.Combine(supportDirectory, result.FileName);
            tempPath = PathSecurity.ResolveInsideRoot(supportDirectory, result.FileName + ".tmp");
            DeleteQuietly(tempPath);

            var entries = new List<string>();
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await AddFilesAsync(
                    archive, usedNames, entries, LogsEntryPrefix,
                    CollectNewestFiles(_options.LogsDirectory, MaxLogFiles),
                    cancellationToken);

                foreach (var crashDirectory in CollectCrashReportDirectories())
                {
                    await AddFilesAsync(
                        archive, usedNames, entries, CrashReportsEntryPrefix,
                        CollectNewestFiles(crashDirectory, MaxCrashReportsPerDirectory),
                        cancellationToken);
                }

                await AddFileEntryAsync(archive, usedNames, entries, "settings.json", _options.SettingsFilePath, cancellationToken);
                await AddFileEntryAsync(archive, usedNames, entries, "instances.json", _options.InstancesFilePath, cancellationToken);
                await AddFileEntryAsync(archive, usedNames, entries, "profiles.json", _options.ProfilesFilePath, cancellationToken);

                await AddTextEntryAsync(archive, usedNames, entries, "system.txt", BuildSystemText(generatedAt), cancellationToken);

                if (includeDoctorReport && _doctor is not null)
                {
                    var doctorText = await BuildDoctorTextAsync(cancellationToken);
                    await AddTextEntryAsync(archive, usedNames, entries, "doctor.txt", doctorText, cancellationToken);
                }
            }

            File.Move(tempPath, result.FullPath, true);
            tempPath = null;

            result.Success = true;
            result.TotalBytes = new FileInfo(result.FullPath).Length;
            result.IncludedFiles.AddRange(entries);
            LauncherLogger.Info($"Támogatási csomag elkészült: {result.FileName} ({result.TotalBytes} byte)");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            LauncherLogger.Error("Támogatási csomag készítési hiba", ex);
        }
        finally
        {
            if (tempPath is not null)
                DeleteQuietly(tempPath);
        }

        result.IncludedFiles.Sort(StringComparer.Ordinal);
        return result;
    }

    // ---------------------------------------------------------------------
    // Archive helpers
    // ---------------------------------------------------------------------

    private static async Task AddFilesAsync(
        ZipArchive archive,
        HashSet<string> usedNames,
        List<string> entries,
        string entryPrefix,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        long written = 0;
        foreach (var file in files)
        {
            if (written >= MaxCategoryBytes)
                break;

            written += await AddFileEntryAsync(
                archive, usedNames, entries, entryPrefix, file, MaxCategoryBytes - written, cancellationToken);
        }
    }

    private static async Task AddFileEntryAsync(
        ZipArchive archive,
        HashSet<string> usedNames,
        List<string> entries,
        string entryName,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        await AddFileEntryAsync(archive, usedNames, entries, entryName, sourcePath, MaxSingleFileBytes, cancellationToken);
    }

    private static async Task<long> AddFileEntryAsync(
        ZipArchive archive,
        HashSet<string> usedNames,
        List<string> entries,
        string entryName,
        string? sourcePath,
        long remainingBudget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return 0;

        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                return 0;

            if (info.Length <= 0)
            {
                LauncherLogger.Debug($"Támogatási csomag: üres fájl kihagyva ({info.Name})");
                return 0;
            }

            if (info.Length > MaxSingleFileBytes || info.Length > remainingBudget)
            {
                LauncherLogger.Warning($"Támogatási csomag: méret miatt kihagyva ({info.Name}, {info.Length} byte)");
                return 0;
            }

            var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            var name = MakeUniqueEntryName(usedNames, BuildEntryName(entryName, sourcePath));
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);

            await using (var stream = entry.Open())
            {
                await stream.WriteAsync(bytes, cancellationToken);
            }

            entries.Add(name);
            return bytes.LongLength;
        }
        catch (Exception ex)
        {
            // Zárolt vagy olvashatatlan fájl: kihagyjuk, a csomag attól még elkészül.
            LauncherLogger.Warning($"Támogatási csomag: kihagyott fájl ({entryName}): {ex.Message}");
            return 0;
        }
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        HashSet<string> usedNames,
        List<string> entries,
        string entryName,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            var name = MakeUniqueEntryName(usedNames, entryName);
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);

            await using (var stream = entry.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken);
            }

            entries.Add(name);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Támogatási csomag: szöveges bejegyzés hiba ({entryName}): {ex.Message}");
        }
    }

    /// <summary>
    /// Entry name is a fixed prefix plus the file name of the source, so an odd
    /// source name can never escape the archive structure.
    /// </summary>
    private static string BuildEntryName(string entryName, string sourcePath)
    {
        var prefix = string.Empty;
        var slash = entryName.LastIndexOf('/');
        if (slash >= 0)
            prefix = entryName[..(slash + 1)];

        return prefix + SafeFileName(sourcePath);
    }

    private static string MakeUniqueEntryName(HashSet<string> usedNames, string entryName)
    {
        var name = SafeFileName(entryName);
        if (string.IsNullOrWhiteSpace(name))
            name = "file";

        var prefix = string.Empty;
        var slash = entryName.LastIndexOf('/');
        if (slash >= 0)
            prefix = entryName[..(slash + 1)];

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = prefix + name;
        var index = 2;

        while (!usedNames.Add(candidate))
        {
            candidate = $"{prefix}{stem}-{index}{extension}";
            index++;
        }

        return candidate;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Támogatási csomag: fájlnév hiba: {ex.Message}");
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------------
    // Sources
    // ---------------------------------------------------------------------

    private static List<string> CollectNewestFiles(string? directory, int maxFiles)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(directory))
            return result;

        try
        {
            if (!Directory.Exists(directory))
                return result;

            result = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists && info.Length > 0 && info.Length <= MaxSingleFileBytes)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(maxFiles)
                .Select(info => info.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Támogatási csomag: lista hiba ({directory}): {ex.Message}");
        }

        return result;
    }

    private List<string> CollectCrashReportDirectories()
    {
        var roots = new List<string>();
        AddDistinctRoot(roots, _options.InstanceDirectory);
        AddDistinctRoot(roots, _options.GameDirectory);

        return roots
            .Select(root => Path.Combine(root, "crash-reports"))
            .ToList();
    }

    private static void AddDistinctRoot(List<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        try
        {
            var full = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (roots.Any(root => string.Equals(root, full, StringComparison.OrdinalIgnoreCase)))
                return;

            roots.Add(full);
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Támogatási csomag: útvonal kihagyva ({candidate}): {ex.Message}");
        }
    }

    private string BuildSystemText(DateTimeOffset generatedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("TurulLauncher support bundle");
        builder.AppendLine($"Generated: {generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Launcher version: {(string.IsNullOrWhiteSpace(_options.LauncherVersion) ? "unknown" : _options.LauncherVersion)}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
        builder.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"Data root: {_options.DataRoot}");
        builder.AppendLine($"Instance directory: {DescribePath(_options.InstanceDirectory)}");
        builder.AppendLine($"Game directory: {DescribePath(_options.GameDirectory)}");
        return builder.ToString();
    }

    private async Task<string> BuildDoctorTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _doctor!.RunAsync(null, cancellationToken);
            return report.ToPlainText();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Támogatási csomag: doctor futtatási hiba: {ex.Message}");
            return $"A doctor futtatása nem sikerült: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
        }
    }

    private DateTimeOffset Now() => _options.Clock?.Invoke() ?? DateTimeOffset.Now;

    private static string BuildFileName(string supportDirectory, DateTimeOffset generatedAt)
    {
        var stamp = generatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"TurulSupport-{stamp}.zip";
        var index = 2;

        while (File.Exists(Path.Combine(supportDirectory, name)))
        {
            name = $"TurulSupport-{stamp}-{index}.zip";
            index++;
        }

        return name;
    }

    private static string DescribePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "(none)" : path;

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Támogatási csomag: temp törlési hiba ({Path.GetFileName(path)}): {ex.Message}");
        }
    }
}
