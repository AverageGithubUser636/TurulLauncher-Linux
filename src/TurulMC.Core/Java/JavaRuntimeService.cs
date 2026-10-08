using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Java;

/// <summary>
/// Java runtime felismerés, kiválasztás és (a <see cref="IJavaRuntimeProvisioner"/>-en keresztül)
/// automatikus telepítés. A verziópróba aszinkron és időkorlátos: egy nem válaszoló
/// <c>java.exe</c> nem blokkolhatja a launchert.
/// </summary>
public class JavaRuntimeService : IJavaRuntimeService
{
    private const int ProbeTimeoutMs = 8000;
    private const int MaxProbesPerScan = 12;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim ProbeGate = new(2, 2);
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true };

    private readonly string _dataRoot;
    private readonly string _settingsFilePath;
    private readonly string _cacheFilePath;
    private readonly IJavaRuntimeProvisioner? _provisioner;

    public JavaRuntimeService(IJavaRuntimeProvisioner? provisioner = null, string? dataRoot = null)
    {
        // Az alapértelmezett adatkönyvtár platformfüggő (LauncherPaths):
        // Windowson %APPDATA%\TurulMC, Linuxon $XDG_DATA_HOME/TurulMC.
        // Korábban a %APPDATA% volt beégetve, ami Linuxon ~/.config/TurulMC-be
        // mutatott — így a java-cache.json és a settings.json nem az
        // adatkönyvtárba került (ugyanaz a hibakategória, mint a ServerListStorage-nál).
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Storage.LauncherPaths.DataRoot
            : dataRoot!;
        try { Directory.CreateDirectory(_dataRoot); }
        catch (Exception ex) { LauncherLogger.Warning($"Az adatkönyvtár nem hozható létre ({_dataRoot}): {ex.Message}"); }

        _settingsFilePath = Path.Combine(_dataRoot, "settings.json");
        _cacheFilePath = Path.Combine(_dataRoot, "java-cache.json");
        _provisioner = provisioner;
    }

    /// <summary>A launcher által telepített runtime-ok gyökere (ha van provisioner).</summary>
    public string? RuntimeRoot => _provisioner?.RuntimeRoot;

    public async Task<List<JavaRuntime>> DetectInstalledRuntimesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) A launcher által telepített (Temurin) runtime-ok: ismert, gyors lista.
        if (_provisioner is not null)
        {
            try
            {
                foreach (var provisioned in await _provisioner.ListProvisionedRuntimesAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(provisioned.JavaPath) || !File.Exists(provisioned.JavaPath)) continue;
                    if (!seen.Add(provisioned.JavaPath)) continue;
                    result.Add(ToRuntime(provisioned));
                }
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning("A telepített Java runtime-ok listázása sikertelen: " + ex.Message);
            }
        }

        // 2) A gépen található runtime-ok, gyorsítótárazott verziópróbával.
        var cache = LoadProbeCache();
        var probes = 0;
        var pending = new List<Task<JavaProbeCacheEntry?>>();

        foreach (var path in CollectCandidatePaths())
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (seen.Contains(path)) continue;

            if (cache.TryGetValue(path, out var hit) && IsFresh(hit) && hit.Major > 0)
            {
                if (seen.Add(path))
                {
                    result.Add(new JavaRuntime
                    {
                        Path = path,
                        Version = hit.Version,
                        Major = hit.Major,
                        IsValid = true,
                        Source = "system",
                        Vendor = GuessVendor(path)
                    });
                }
                continue;
            }

            if (probes >= MaxProbesPerScan) continue;
            probes++;
            pending.Add(ProbeWithGateAsync(path, cancellationToken));
        }

        foreach (var task in pending)
        {
            var entry = await task.ConfigureAwait(false);
            if (entry is null) continue;
            if (entry.Major > 0) cache[entry.Path] = entry;
            else cache.Remove(entry.Path);
            if (!seen.Add(entry.Path)) continue;
            result.Add(new JavaRuntime
            {
                Path = entry.Path,
                Version = entry.Version,
                Major = entry.Major,
                IsValid = entry.Major > 0,
                Source = "system",
                Vendor = GuessVendor(entry.Path),
                ErrorMessage = entry.Major > 0 ? null : "A Java verziója nem olvasható."
            });
        }

        SaveProbeCache(cache);

        // Ugyanaz a JDK több útvonalon is felbukkanhat (symlink, 8.3-as rövid név,
        // bin\java.exe + bin\javaw.exe, Program Files + Program Files (x86) tükrözés):
        // telepítési mappánként (a bin fölötti könyvtár) csak EGY bejegyzést adunk vissza,
        // különben a felület ugyanazt a Java-t többször listázza.
        var unique = new List<JavaRuntime>();
        var seenHomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in result
                     .OrderByDescending(r => r.FeatureVersion > 0 ? r.FeatureVersion : r.Major)
                     .ThenBy(r => string.Equals(r.Source, "provisioned", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var home = GetJavaHome(runtime.Path);
            if (seenHomes.Add(home)) unique.Add(runtime);
        }

        return unique;
    }

    /// <summary>A java.exe/javaw.exe útvonalából a Java telepítési gyökere (…\jdk-25\bin\java.exe → …\jdk-25).</summary>
    private static string GetJavaHome(string javaPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(javaPath);
            if (directory is not null && Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase))
                directory = Path.GetDirectoryName(directory);
            return Path.GetFullPath(directory ?? javaPath).TrimEnd('\\', '/');
        }
        catch
        {
            return javaPath;
        }
    }

    public async Task<JavaRuntime?> GetRecommendedRuntimeAsync(string? requiredVersion = null, CancellationToken cancellationToken = default)
    {
        var requiredMajor = ParseRequestedMajor(requiredVersion);

        // A kézi beállítás elsőbbséget élvez, ha megfelel a követelménynek.
        var configured = await GetConfiguredJavaPathAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configured) && ValidateJavaPath(configured))
        {
            var version = await GetJavaVersionAsync(configured, cancellationToken).ConfigureAwait(false);
            var major = ParseJavaMajor(version);
            if (requiredMajor <= 0 || IsCompatible(major, requiredMajor))
            {
                return new JavaRuntime
                {
                    Path = configured,
                    Version = version,
                    Major = major,
                    IsValid = true,
                    Source = "override",
                    Vendor = GuessVendor(configured)
                };
            }
        }

        var runtimes = await DetectInstalledRuntimesAsync(cancellationToken).ConfigureAwait(false);
        if (requiredMajor <= 0) return runtimes.FirstOrDefault(r => r.IsValid);

        var usable = runtimes.Where(r => r.IsValid).ToList();
        return usable.FirstOrDefault(r => r.Major == requiredMajor)
            ?? usable.FirstOrDefault(r => IsCompatible(r.Major, requiredMajor));
    }

    public async Task<JavaRuntime> EnsureRuntimeAsync(
        int requiredMajor,
        bool allowInstall,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (requiredMajor is < 8 or > 99)
            throw new ArgumentOutOfRangeException(nameof(requiredMajor), "A Java főverzió 8 és 99 között lehet.");

        var existing = await GetRecommendedRuntimeAsync(requiredMajor.ToString(), cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        if (!allowInstall || _provisioner is null)
        {
            throw new InvalidOperationException(
                $"A Minecraft Java {requiredMajor}-et igényel, de ilyen Java runtime nincs a gépen. " +
                "Engedélyezd a Beállításokban az automatikus Java telepítést, vagy állíts be kézzel egy megfelelő java.exe-t.");
        }

        LauncherLogger.Info($"Java {requiredMajor} nem található a gépen — automatikus telepítés (Eclipse Temurin JRE).");
        var provisioned = await _provisioner.ProvisionAsync(requiredMajor, progress, cancellationToken).ConfigureAwait(false);
        return ToRuntime(provisioned);
    }

    public async Task<string?> GetConfiguredJavaPathAsync()
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return null;
            var json = await File.ReadAllTextAsync(_settingsFilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LauncherSettings>(json)?.JavaPathOverride;
        }
        catch { return null; }
    }

    public async Task SetJavaPathAsync(string path)
    {
        LauncherSettings settings;
        try
        {
            settings = File.Exists(_settingsFilePath)
                ? JsonSerializer.Deserialize<LauncherSettings>(await File.ReadAllTextAsync(_settingsFilePath).ConfigureAwait(false)) ?? new LauncherSettings()
                : new LauncherSettings();
        }
        catch { settings = new LauncherSettings(); }

        settings.JavaPathOverride = path;
        var json = JsonSerializer.Serialize(settings, CacheJson);
        var result = Storage.AtomicFile.TryWriteAllText(_settingsFilePath, json);
        if (!result.Success)
        {
            LauncherLogger.Error("A Java útvonal beállítása nem sikerült: " + result.Error);
            throw new IOException(result.Error);
        }
    }

    public bool ValidateJavaPath(string path)
    {
        try { Validation.LauncherSettingsValidator.ValidateJavaPath(path); return true; }
        catch { return false; }
    }

    /// <summary>„21”, „1.8”, „17.0.9+9-LTS” → főverzió. Nem parse-olható bemenetre 0.</summary>
    public static int ParseJavaMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var m = Regex.Match(version, @"^(?:1\.)?(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
    }

    /// <summary>Egy Java runtime verziósora, időkorláttal. A folyamatot időtúllépéskor kilövi,
    /// így egy nem válaszoló java.exe nem blokkolhatja a hívót.</summary>
    public static async Task<string> GetJavaVersionAsync(string javaPath, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = Process.Start(psi);
            if (process is null) return "Unknown";

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                LauncherLogger.Warning($"A Java verziópróba nem válaszolt {ProbeTimeoutMs / 1000} másodpercen belül: {javaPath}");
                return "Unknown";
            }

            var stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);
            var stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            var quoted = Regex.Match(output, "\"([^\"]+)\"");
            if (quoted.Success) return quoted.Groups[1].Value;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Unknown";
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Java verziópróba hiba ({javaPath}): {ex.Message}");
            return "Unknown";
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static int ParseRequestedMajor(string? requiredVersion)
    {
        if (string.IsNullOrWhiteSpace(requiredVersion)) return 0;
        var m = Regex.Match(requiredVersion, @"\d+");
        return m.Success && int.TryParse(m.Value, out var major) ? major : 0;
    }

    /// <summary>Pontos egyezés, vagy (17-es követelmény felett) újabb Java is megfelel.
    /// Egyetlen forrás: <see cref="JavaVersionMap.IsCompatible"/>.</summary>
    public static bool IsCompatible(int actualMajor, int requiredMajor) =>
        JavaVersionMap.IsCompatible(actualMajor, requiredMajor);

    private static JavaRuntime ToRuntime(ProvisionedJavaRuntime provisioned) => new()
    {
        Path = provisioned.JavaPath,
        Version = string.IsNullOrWhiteSpace(provisioned.FullVersion) ? provisioned.FeatureVersion.ToString() : provisioned.FullVersion,
        Major = provisioned.FeatureVersion,
        FeatureVersion = provisioned.FeatureVersion,
        IsValid = File.Exists(provisioned.JavaPath),
        Source = "provisioned",
        Id = provisioned.Id,
        Vendor = string.IsNullOrWhiteSpace(provisioned.Vendor) ? "Eclipse Temurin" : provisioned.Vendor
    };

    private static string GuessVendor(string path)
    {
        var p = path ?? string.Empty;
        if (p.Contains("Temurin", StringComparison.OrdinalIgnoreCase) || p.Contains("Adoptium", StringComparison.OrdinalIgnoreCase)) return "Eclipse Temurin";
        if (p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft OpenJDK";
        if (p.Contains("Zulu", StringComparison.OrdinalIgnoreCase)) return "Azul Zulu";
        if (p.Contains("Corretto", StringComparison.OrdinalIgnoreCase)) return "Amazon Corretto";
        if (p.Contains("BellSoft", StringComparison.OrdinalIgnoreCase) || p.Contains("Liberica", StringComparison.OrdinalIgnoreCase)) return "BellSoft Liberica";
        if (p.Contains("TurulMC", StringComparison.OrdinalIgnoreCase)) return "Eclipse Temurin";
        return "System";
    }

    private static IEnumerable<string> CollectCandidatePaths()
    {
        var list = new List<string>();

        void AddHome(string? home)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            if (OperatingSystem.IsWindows())
            {
                list.Add(Path.Combine(home!, "bin", "javaw.exe"));
                list.Add(Path.Combine(home!, "bin", "java.exe"));
            }
            else
            {
                list.Add(Path.Combine(home!, "bin", "java"));
            }
        }

        AddHome(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // Windowson a Program Files a megszokott hely; Linuxon a disztribúciók
        // szabványos telepítési könyvtárai + a jól ismert alternatív disztribúciók.
        foreach (var basePath in SearchRoots())
        {
            AddHome(basePath);
            try
            {
                foreach (var dir in Directory.GetDirectories(basePath)) AddHome(dir);
            }
            catch (Exception ex) { LauncherLogger.Debug($"Java keresési hiba ({basePath}): {ex.Message}"); }
        }

        // A Minecraft runtime könyvtára: Windowson %APPDATA%\.minecraft,
        // Linuxon ~/.minecraft (az XDG-átirányítást is figyelembe véve).
        foreach (var minecraftRoot in MinecraftRuntimeRoots())
            AddTree(list, Path.Combine(minecraftRoot, "runtime"), 8);

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddTree(list, Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"), 9);
            AddTree(list, Path.Combine(local, "Minecraft Launcher", "runtime"), 8);
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddTree(list, Path.Combine(local, "Minecraft", "runtime"), 8);
            AddTree(list, Path.Combine(local, "minecraft-launcher", "runtime"), 8);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim('"');
            if (trimmed.Length == 0) continue;
            foreach (var exe in ExecutableFileNames())
                list.Add(Path.Combine(trimmed, exe));
        }

        return list.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A platformon érvényes Java futtatható nevei, elsőként a preferáltal.
    /// </summary>
    private static string[] ExecutableFileNames()
        => OperatingSystem.IsWindows() ? new[] { "javaw.exe", "java.exe" } : new[] { "java" };

    /// <summary>
    /// A Java-keresés gyökérkönyvtárai: Windowson a Program Files, Linuxon a
    /// disztribúciók szabványos helyei és az ismert alternatív disztribúciók.
    /// </summary>
    private static IEnumerable<string> SearchRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Java";
            yield return @"C:\Program Files\Eclipse Adoptium";
            yield return @"C:\Program Files\Microsoft";
            yield return @"C:\Program Files\Zulu";
            yield return @"C:\Program Files\Amazon Corretto";
            yield return @"C:\Program Files\BellSoft";
            yield break;
        }

        // Linux: rendszerszintű csomagkezelők + SDKMAN + a launcher saját runtime könyvtára.
        yield return "/usr/lib/jvm";
        yield return "/usr/lib64/jvm";
        yield return "/opt/java";
        yield return "/opt/jdk";
        yield return "/opt/adoptium";
        yield return "/usr/java";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".sdkman", "candies", "java-current");
            yield return Path.Combine(home, ".local", "share", "TurulMC", "java");
            yield return Path.Combine(home, ".jdks");
        }
    }

    /// <summary>
    /// A <c>.minecraft</c> könyvtár lehelyezkedése: Windowson a profil,
    /// Linuxon az otthoni mappa (és az XDG átirányítás).
    /// </summary>
    private static IEnumerable<string> MinecraftRuntimeRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".minecraft");

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData) && Path.IsPathRooted(xdgData))
            yield return Path.Combine(xdgData, "minecraft");
    }

    private static void AddTree(List<string> list, string root, int maxDepth)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var rootDepth = root.Count(c => c == Path.DirectorySeparatorChar);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!ExecutableFileNames().Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                var depth = file.Count(c => c == Path.DirectorySeparatorChar) - rootDepth;
                if (depth <= maxDepth) list.Add(file);
            }
        }
        catch (Exception ex) { LauncherLogger.Debug($"Java runtime keresési hiba ({root}): {ex.Message}"); }
    }

    private static async Task<JavaProbeCacheEntry?> ProbeWithGateAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await ProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }

        try
        {
            var version = await GetJavaVersionAsync(path, cancellationToken).ConfigureAwait(false);
            return new JavaProbeCacheEntry
            {
                Path = path,
                Version = version,
                Major = ParseJavaMajor(version),
                ProbedAtUtc = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    private static bool IsFresh(JavaProbeCacheEntry entry) => DateTimeOffset.UtcNow - entry.ProbedAtUtc < CacheTtl;

    private Dictionary<string, JavaProbeCacheEntry> LoadProbeCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return new Dictionary<string, JavaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(_cacheFilePath);
            var entries = JsonSerializer.Deserialize<List<JavaProbeCacheEntry>>(json) ?? new List<JavaProbeCacheEntry>();
            var map = new Dictionary<string, JavaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!IsFresh(entry)) continue;
                if (!File.Exists(entry.Path)) continue;
                map[entry.Path] = entry;
            }
            return map;
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("A Java gyorsítótár nem olvasható: " + ex.Message);
            return new Dictionary<string, JavaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveProbeCache(Dictionary<string, JavaProbeCacheEntry> cache)
    {
        try
        {
            var entries = cache.Values
                .Where(e => e.Major > 0 && File.Exists(e.Path))
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var json = JsonSerializer.Serialize(entries, CacheJson);
            Storage.AtomicFile.TryWriteAllText(_cacheFilePath, json, createBackup: false);
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("A Java gyorsítótár nem menthető: " + ex.Message);
        }
    }

    private sealed class JavaProbeCacheEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int Major { get; set; }
        public DateTimeOffset ProbedAtUtc { get; set; }
    }
}
