using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Security;

namespace TurulMC.Core.Java;

/// <summary>
/// Eclipse Temurin (Adoptium) JRE automatikus letöltése és telepítése a launcher saját
/// Java könyvtárába. Minden írás a <see cref="RuntimeRoot"/> könyvtáron belül marad,
/// a kicsomagolás pedig ZIP-slip ellenőrzéssel történik.
/// </summary>
public sealed class JavaRuntimeProvisioner : IJavaRuntimeProvisioner
{
    /// <summary>A letöltött csomag felső méretkorlátja (512 MB).</summary>
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>Egy kicsomagolt fájl felső méretkorlátja (512 MB).</summary>
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>A kicsomagolt csomag teljes felső méretkorlátja (2 GB).</summary>
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>A kicsomagolható fájlok maximális száma.</summary>
    private const int MaxEntryCount = 20_000;

    private const string RegistryFileName = "runtimes.json";

    /// <summary>
    /// A Java csomagok letöltéséhez használt saját HTTP kliens (10 perces időkorlát).
    /// Szándékosan nem a <c>LauncherHttpClient.DownloadFileAsync</c>-et használjuk, mert
    /// a Java telepítés saját, 0–100 skálájú haladást jelent.
    /// </summary>
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    /// <summary>Érvényes runtime azonosító: fájlnév-biztos, legfeljebb 64 karakter.</summary>
    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AdoptiumReleaseClient _client;
    private readonly SemaphoreSlim _registryLock = new(1, 1);

    /// <summary>
    /// Létrehozza a provisionert.
    /// </summary>
    /// <param name="client">Adoptium kliens; ha nincs megadva, sajátot példányosít.</param>
    /// <param name="runtimeRoot">A Java runtime-ok gyökérkönyvtára; alapértelmezés: <c>%APPDATA%\TurulMC\java</c>.
    /// A könyvtár csak az első írásnál jön létre.</param>
    public JavaRuntimeProvisioner(AdoptiumReleaseClient? client = null, string? runtimeRoot = null)
    {
        _client = client ?? new AdoptiumReleaseClient();
        RuntimeRoot = string.IsNullOrWhiteSpace(runtimeRoot) ? DefaultRuntimeRoot() : Path.GetFullPath(runtimeRoot);
    }

    /// <inheritdoc />
    public string RuntimeRoot { get; }

    /// <inheritdoc />
    public async Task<ProvisionedJavaRuntime> ProvisionAsync(int featureVersion, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (featureVersion is < 8 or > 99)
            throw new ArgumentException("A Java főverziónak 8 és 99 között kell lennie.", nameof(featureVersion));

        var architecture = DetectArchitecture();
        var id = $"temurin-{featureVersion}-{architecture}";
        var targetDirectory = Path.Combine(RuntimeRoot, id);
        var stagingDirectory = Path.Combine(RuntimeRoot, id + ".staging-" + Guid.NewGuid().ToString("N"));
        // A kiterjesztés csak a helyi temp fájl nevét érinti: a valódi formátumot
        // a letöltött csomag határozza meg (.zip Windowson, .tar.gz Linux/macOS-on).
        var packagePath = Path.Combine(RuntimeRoot, id + "." + Guid.NewGuid().ToString("N") + ".part");

        try
        {
            EnsureRuntimeRoot();

            ReportStage(progress, id, 0, $"Java {featureVersion} keresése…");
            var release = await _client.GetLatestJreAsync(featureVersion, architecture, cancellationToken).ConfigureAwait(false);

            ReportStage(progress, id, 10, "Letöltés…");
            var packageBytes = await DownloadPackageAsync(release, packagePath, progress, id, cancellationToken).ConfigureAwait(false);

            ReportStage(progress, id, 60, "Ellenőrzés…");
            if (!await Sha256Service.VerifyFileAsync(packagePath, release.Sha256, cancellationToken).ConfigureAwait(false))
            {
                TryDeleteFile(packagePath);
                throw new InvalidDataException("A letöltött Java csomag ellenőrzése sikertelen (SHA-256).");
            }

            ReportStage(progress, id, 70, "Kicsomagolás…");
            await ExtractPackageAsync(packagePath, stagingDirectory, progress, id, cancellationToken).ConfigureAwait(false);

            var (launchPath, consolePath) = LocateJavaExecutables(stagingDirectory, id);
            var launchRelativePath = Path.GetRelativePath(stagingDirectory, launchPath);
            var consoleRelativePath = consolePath is null ? string.Empty : Path.GetRelativePath(stagingDirectory, consolePath);

            ReportStage(progress, id, 95, "Befejezés…");
            ReplaceTargetDirectory(targetDirectory, stagingDirectory);

            var runtime = new ProvisionedJavaRuntime
            {
                Id = id,
                Vendor = release.Vendor,
                FeatureVersion = release.FeatureVersion > 0 ? release.FeatureVersion : featureVersion,
                FullVersion = release.FullVersion,
                Architecture = architecture,
                JavaPath = Path.Combine(targetDirectory, launchRelativePath),
                JavaExePath = consoleRelativePath.Length == 0 ? string.Empty : Path.Combine(targetDirectory, consoleRelativePath),
                InstallDirectory = targetDirectory,
                SourceUrl = release.DownloadUrl,
                Sha256 = release.Sha256,
                PackageBytes = packageBytes,
                InstalledAtUtc = DateTimeOffset.UtcNow
            };

            await UpsertRegistryEntryAsync(runtime, cancellationToken).ConfigureAwait(false);

            ReportStage(progress, id, 100, "Kész");
            LauncherLogger.Info($"Java runtime telepítve: {runtime.Id} ({runtime.FullVersion}) → {runtime.InstallDirectory}");
            return runtime;
        }
        catch (OperationCanceledException)
        {
            LauncherLogger.Debug($"Java {featureVersion} telepítése megszakítva.");
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            LauncherLogger.Error($"Java {featureVersion} telepítése sikertelen.", ex);
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error($"Java {featureVersion} telepítése sikertelen.", ex);
            throw new InvalidOperationException($"A Java {featureVersion} telepítése sikertelen: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteFile(packagePath);
        }
    }

    /// <inheritdoc />
    public async Task<List<ProvisionedJavaRuntime>> ListProvisionedRuntimesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var registry = await ReadRegistryLockedAsync(cancellationToken).ConfigureAwait(false);
                if (!registry.LoadedFromDisk) return new List<ProvisionedJavaRuntime>();

                var alive = registry.Runtimes
                    .Where(runtime => !string.IsNullOrWhiteSpace(runtime.JavaPath) && File.Exists(runtime.JavaPath))
                    .ToList();

                if (alive.Count != registry.Runtimes.Count)
                {
                    LauncherLogger.Warning($"A Java nyilvántartásból {registry.Runtimes.Count - alive.Count} hiányzó telepítés eltávolítva.");
                    await WriteRegistryLockedAsync(alive, cancellationToken).ConfigureAwait(false);
                }

                return alive
                    .OrderBy(runtime => runtime.FeatureVersion)
                    .ThenBy(runtime => runtime.Architecture, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                _registryLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("A telepített Java runtime-ok listázása sikertelen.", ex);
            throw new InvalidOperationException("A telepített Java runtime-ok listázása sikertelen.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveProvisionedRuntimeAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            var runtimeId = id.Trim();
            if (runtimeId is "." or ".." || !SafeIdPattern.IsMatch(runtimeId)) return false;

            string directory;
            try
            {
                directory = PathSecurity.ResolveInsideRoot(RuntimeRoot, runtimeId);
            }
            catch (InvalidOperationException ex)
            {
                LauncherLogger.Warning($"Érvénytelen Java runtime azonosító ({id}): {ex.Message}");
                return false;
            }

            await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var registry = await ReadRegistryLockedAsync(cancellationToken).ConfigureAwait(false);
                var entry = registry.Runtimes.FirstOrDefault(runtime => runtime.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase));
                var directoryExists = Directory.Exists(directory);

                if (!directoryExists && entry is null) return false;

                if (directoryExists)
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        LauncherLogger.Error($"Java runtime törlése blokkolva (symlink): {directory}");
                        return false;
                    }

                    PathSecurity.SafeDeleteDirectory(RuntimeRoot, directory);
                    LauncherLogger.Info($"Java runtime törölve: {directory}");
                }

                if (entry is not null)
                {
                    registry.Runtimes.RemoveAll(runtime => runtime.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase));
                    await WriteRegistryLockedAsync(registry.Runtimes, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                _registryLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error($"Java runtime törlése sikertelen ({id}).", ex);
            throw new InvalidOperationException($"A Java runtime törlése sikertelen ({id}).", ex);
        }
    }

    private string RegistryPath => Path.Combine(RuntimeRoot, RegistryFileName);

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TurulMC-Launcher/2.0");
        return client;
    }

    private static string DefaultRuntimeRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.GetFullPath(Path.Combine(appData, "TurulMC", "java"));
    }

    /// <summary>A futó operációs rendszerhez illeszkedő Temurin architektúra név.</summary>
    private static string DetectArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "aarch64",
        _ => "x64"
    };

    private void EnsureRuntimeRoot()
    {
        if (Directory.Exists(RuntimeRoot)) return;
        Directory.CreateDirectory(RuntimeRoot);
        LauncherLogger.Info($"Java runtime könyvtár létrehozva: {RuntimeRoot}");
    }

    /// <summary>
    /// Haladásjelentés a telepítés 0–100 skáláján (a szövegek magyarul, a felhasználói felülethez).
    /// </summary>
    private static void ReportStage(IProgress<DownloadProgress>? progress, string id, double percent, string status)
    {
        progress?.Report(new DownloadProgress
        {
            FileName = id,
            BytesReceived = (long)Math.Round(percent, MidpointRounding.AwayFromZero),
            TotalBytes = 100,
            BytesPerSecond = 0,
            Status = status
        });
    }

    /// <summary>
    /// A letöltés bájtjait a 10% ("Letöltés…") és 60% ("Ellenőrzés…") közötti sávra képezi le,
    /// így a haladás folyamatos és monoton marad.
    /// </summary>
    private static void ReportDownload(IProgress<DownloadProgress>? progress, string id, string fileName, long received, long total, double bytesPerSecond)
    {
        var fraction = total > 0 ? Math.Min(1d, (double)received / total) : 0d;
        var percent = 10d + fraction * 50d;

        progress?.Report(new DownloadProgress
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? id : fileName,
            BytesReceived = (long)Math.Round(percent, MidpointRounding.AwayFromZero),
            TotalBytes = 100,
            BytesPerSecond = bytesPerSecond,
            Status = "Letöltés…"
        });
    }

    /// <summary>
    /// Letölti a csomagot streamelve a megadott temp fájlba, méretkorláttal és saját haladásjelentéssel.
    /// </summary>
    /// <returns>A ténylegesen letöltött bájtok száma.</returns>
    private static async Task<long> DownloadPackageAsync(AdoptiumRelease release, string packagePath, IProgress<DownloadProgress>? progress, string id, CancellationToken cancellationToken)
    {
        if (!AdoptiumHosts.IsAllowedDownloadUrl(release.DownloadUrl))
            throw new InvalidOperationException($"Nem engedélyezett Java letöltési cím: {release.DownloadUrl}");
        PathSecurity.EnsureSafeHttpsUrl(release.DownloadUrl);

        using var response = await DownloadClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxPackageBytes)
            throw new InvalidDataException("A Java csomag mérete meghaladja az 512 MB-os korlátot.");

        var expectedBytes = contentLength is > 0 ? contentLength.Value : release.SizeBytes;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        var buffer = new byte[65536];
        long received = 0;
        var lastPercent = -1;
        var stopwatch = Stopwatch.StartNew();

        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > MaxPackageBytes)
                throw new InvalidDataException("A Java csomag mérete meghaladja az 512 MB-os korlátot.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            var percent = expectedBytes > 0 ? (int)(received * 100 / expectedBytes) : 0;
            if (percent == lastPercent) continue;

            lastPercent = percent;
            var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            ReportDownload(progress, id, release.FileName, received, expectedBytes, received / seconds);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        ReportDownload(progress, id, release.FileName, received, Math.Max(expectedBytes, received), 0);
        return received;
    }

    /// <summary>
    /// Kicsomagolja a csomagot a staging könyvtárba. A formátum a csomag fejlécéből
    /// derül ki: <c>.zip</c> (Windows) vagy <c>.tar.gz</c> (Linux/macOS).
    /// Minden bejegyzés útvonala <see cref="PathSecurity.ResolveInsideRoot"/>-nal
    /// ellenőrzött, a kibontott méret és a fájlszám korlátozott.
    ///
    /// Nyilvános, hogy közvetlenül tesztelhető legyen — a korábbi audit (H-02/H-03)
    /// éppen a „a teszt a saját másolatát ellenőrzi, nem a termékkódot" mintára
    /// figyelmeztetett, ezért itt a valódi kód fut a teszt alatt.
    /// </summary>
    public static async Task ExtractPackageAsync(string packagePath, string stagingDirectory, IProgress<DownloadProgress>? progress, string id, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stagingDirectory);

        if (IsTarArchive(packagePath))
        {
            await ExtractTarPackageAsync(packagePath, stagingDirectory, progress, id, cancellationToken).ConfigureAwait(false);
            EnsureUnixExecutableBits(stagingDirectory);
            return;
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var totalEntries = archive.Entries.Count;
        var fileCount = 0;
        long declaredBytes = 0;
        long writtenBytes = 0;

        for (var index = 0; index < totalEntries; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries[index];

            // A könyvtár bejegyzéseknél a ZipArchive üres Name-t ad.
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            fileCount++;
            if (fileCount > MaxEntryCount)
                throw new InvalidDataException($"A Java csomag túl sok fájlt tartalmaz (max. {MaxEntryCount}).");

            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"A Java csomag egy fájlja túl nagy: {entry.FullName}");

            declaredBytes += entry.Length;
            if (declaredBytes > MaxExtractedBytes)
                throw new InvalidDataException("A Java csomag kibontott mérete meghaladja a 2 GB-os korlátot.");

            string destinationPath;
            try
            {
                destinationPath = PathSecurity.ResolveInsideRoot(stagingDirectory, entry.FullName); // zip-slip védelem
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A bejegyzés a staging könyvtáron kívülre mutatna (vagy a neve érvénytelen):
                // kihagyjuk, sosem írunk a gyökéren kívülre.
                LauncherLogger.Warning($"Kihagyott ZIP bejegyzés ({entry.FullName}): {ex.Message}");
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using var entryStream = entry.Open();
            await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long entryBytes = 0;

            int read;
            while ((read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                entryBytes += read;
                writtenBytes += read;

                if (entryBytes > MaxEntryBytes)
                    throw new InvalidDataException($"A kicsomagolt fájl túl nagy: {entry.FullName}");
                if (writtenBytes > MaxExtractedBytes)
                    throw new InvalidDataException("A Java csomag kibontott mérete meghaladja a 2 GB-os korlátot.");

                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if ((fileCount & 31) == 0 && totalEntries > 0)
            {
                var fraction = Math.Min(1d, (double)index / totalEntries);
                ReportStage(progress, id, 70d + fraction * 20d, "Kicsomagolás…");
            }
        }
    }

    /// <summary>
    /// Megállapítja, hogy a letöltött csomag tar (esetleg gzip tömörített) formátumú-e.
    /// A Temurin Linux/macOS csomagjai <c>.tar.gz</c>-k, amit a <see cref="ZipFile"/>
    /// nem tud olvasni.
    /// </summary>
    private static bool IsTarArchive(string packagePath)
    {
        var name = packagePath.ToLowerInvariant();
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz") || name.EndsWith(".tar")) return true;

        // A temp fájl neve szándékosan kiterjesztés nélküli (".part"), ezért a
        // fejléc alapján döntünk: a gzip magic 1F 8B.
        try
        {
            using var stream = File.OpenRead(packagePath);
            var header = new byte[2];
            return stream.Read(header, 0, 2) == 2 && header[0] == 0x1F && header[1] == 0x8B;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// tar/tar.gz csomag kicsomagolása ugyanazokkal a korlátokkal és a
    /// zip-slip védelemmel, mint a ZIP esetén. A <c>System.Formats.Tar</c>
    /// a .NET 7 óta a beépített része.
    /// </summary>
    private static async Task ExtractTarPackageAsync(string packagePath, string stagingDirectory, IProgress<DownloadProgress>? progress, string id, CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var gzip = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);

        using var reader = new System.Formats.Tar.TarReader(gzip, leaveOpen: true);

        var fileCount = 0;
        long writtenBytes = 0;
        long processedBytes = 0;

        while (reader.GetNextEntry(false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Könyvtár bejegyzés: létrehozzuk és tovább.
            if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory)
            {
                try
                {
                    Directory.CreateDirectory(PathSecurity.ResolveInsideRoot(stagingDirectory, entry.Name));
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Kihagyott tar könyvtár ({entry.Name}): {ex.Message}");
                }
                continue;
            }

            if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile))
                continue;

            fileCount++;
            if (fileCount > MaxEntryCount)
                throw new InvalidDataException($"A Java csomag túl sok fájlt tartalmaz (max. {MaxEntryCount}).");

            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"A Java csomag egy fájlja túl nagy: {entry.Name}");

            writtenBytes += entry.Length;
            if (writtenBytes > MaxExtractedBytes)
                throw new InvalidDataException("A Java csomag kibontott mérete meghaladja a 2 GB-os korlátot.");

            string destinationPath;
            try
            {
                destinationPath = PathSecurity.ResolveInsideRoot(stagingDirectory, entry.Name); // zip-slip védelem
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                LauncherLogger.Warning($"Kihagyott tar bejegyzés ({entry.Name}): {ex.Message}");
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using var entryStream = entry.DataStream;
            if (entryStream is null) continue;

            await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long entryBytes = 0;
            int read;
            while ((read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                entryBytes += read;
                if (entryBytes > MaxEntryBytes)
                    throw new InvalidDataException($"A kicsomagolt fájl túl nagy: {entry.Name}");
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            processedBytes += entryBytes;
            if ((fileCount & 31) == 0)
                ReportStage(progress, id, 70d + Math.Min(1d, processedBytes / (double)Math.Max(1, writtenBytes)) * 20d, "Kicsomagolás…");
        }
    }

    /// <summary>
    /// A tar kicsomagolás után beállítja a végrehajthatósági bitet a <c>bin</c>
    /// alatti fájlokon. A <see cref="System.Formats.Tar.TarFile"/> ugyanis nem
    /// alkalmazza a Unix jogosultságokat, így a <c>java</c> nem futtatható lenne.
    /// Csak Linux/macOS-on van értelme.
    /// </summary>
    private static void EnsureUnixExecutableBits(string stagingDirectory)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var dir = Path.GetFileName(Path.GetDirectoryName(file));
                if (!string.Equals(dir, "bin", StringComparison.OrdinalIgnoreCase)) continue;

                // 0755 = rwxr-xr-x. A meglévő jogok megtartása mellett a +x bitet állítjuk.
                File.SetUnixFileMode(file,
                    File.GetUnixFileMode(file) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"A végrehajthatósági beállítás nem sikerült: {ex.Message}");
        }
    }

    /// <summary>
    /// Megkeresi a futtatható Java állományokat a kicsomagolt fában (a <c>bin</c> könyvtárat előnyben részesítve).
    /// Nyilvános: a platform-specifikus keresés (java.exe/javaw.exe vs. java) tesztelhetőségéért.
    /// </summary>
    /// <exception cref="InvalidDataException">Ha nincs futtatható Java a csomagban.</exception>
    public static (string Launch, string? Console) LocateJavaExecutables(string stagingDirectory, string id)
    {
        // Windowson a javaw.exe / java.exe, Linux/macOS-on a sima "java" a megfelelő.
        var consolePath = OperatingSystem.IsWindows()
            ? FindExecutable(stagingDirectory, "java.exe")
            : FindExecutable(stagingDirectory, "java");
        var windowPath = OperatingSystem.IsWindows()
            ? FindExecutable(stagingDirectory, "javaw.exe")
            : null;
        var launchPath = windowPath ?? consolePath;

        if (launchPath is null)
        {
            LauncherLogger.Error($"A kicsomagolt Java csomagban nem található futtatható Java ({id}).");
            throw new InvalidDataException(OperatingSystem.IsWindows()
                ? "A kicsomagolt Java csomagban nem található javaw.exe."
                : "A kicsomagolt Java csomagban nem található 'java' futtatható.");
        }

        return (launchPath, consolePath);
    }

    private static string? FindExecutable(string root, string fileName)
    {
        if (!Directory.Exists(root)) return null;

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Java futtatható állomány keresése sikertelen ({fileName}): {ex.Message}");
            return null;
        }

        return candidates.FirstOrDefault(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// A meglévő célkönyvtárat (symlink ellenőrzés után) törli, majd a staging könyvtárat a helyére mozgatja.
    /// </summary>
    private void ReplaceTargetDirectory(string targetDirectory, string stagingDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            if ((File.GetAttributes(targetDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"A célkönyvtár symlink, a telepítés blokkolva: {targetDirectory}");

            LauncherLogger.Warning($"Korábbi Java telepítés felülírása: {targetDirectory}");
            try
            {
                PathSecurity.SafeDeleteDirectory(RuntimeRoot, targetDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"A korábbi Java telepítés nem törölhető: {targetDirectory}", ex);
            }
        }
        else if (File.Exists(targetDirectory))
        {
            File.Delete(targetDirectory);
        }

        EnsureRuntimeRoot();
        Directory.Move(stagingDirectory, targetDirectory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Ideiglenes fájl törlése sikertelen ({path}): {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort takarítás: a <see cref="RuntimeRoot"/>-on belüli ideiglenes könyvtár törlése.
    /// </summary>
    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            PathSecurity.SafeDeleteDirectory(RuntimeRoot, path);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Ideiglenes könyvtár törlése sikertelen ({path}): {ex.Message}");
        }
    }

    /// <summary>
    /// Felveszi (vagy frissíti) a runtime bejegyzését a <c>runtimes.json</c>-ba, atomikus írással.
    /// </summary>
    private async Task UpsertRegistryEntryAsync(ProvisionedJavaRuntime runtime, CancellationToken cancellationToken)
    {
        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registry = await ReadRegistryLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!registry.LoadedFromDisk)
                QuarantineCorruptRegistry();

            registry.Runtimes.RemoveAll(entry => entry.Id.Equals(runtime.Id, StringComparison.OrdinalIgnoreCase));
            registry.Runtimes.Add(runtime);

            await WriteRegistryLockedAsync(registry.Runtimes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// A sérült nyilvántartást félreteszi (nem írjuk felül nyomtalanul).
    /// </summary>
    private void QuarantineCorruptRegistry()
    {
        try
        {
            if (!File.Exists(RegistryPath)) return;

            var quarantinePath = $"{RegistryPath}.corrupt-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            File.Move(RegistryPath, quarantinePath, true);
            LauncherLogger.Warning($"A sérült Java nyilvántartás félretéve: {quarantinePath}");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"A sérült Java nyilvántartás félretétele sikertelen: {ex.Message}");
        }
    }

    /// <summary>
    /// Beolvassa a nyilvántartást. Sérült fájl esetén figyelmeztet, és üres listát ad
    /// vissza <c>LoadedFromDisk = false</c> jelöléssel (ilyenkor nem írunk vissza üres listát).
    /// </summary>
    private async Task<RegistryReadResult> ReadRegistryLockedAsync(CancellationToken cancellationToken)
    {
        var path = RegistryPath;
        if (!File.Exists(path)) return new RegistryReadResult(new List<ProvisionedJavaRuntime>(), true);

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new RegistryReadResult(new List<ProvisionedJavaRuntime>(), true);

            var parsed = JsonSerializer.Deserialize<List<ProvisionedJavaRuntime?>>(json);
            if (parsed is null)
            {
                LauncherLogger.Warning($"A Java nyilvántartás ({path}) nem lista, üresnek tekintve.");
                return new RegistryReadResult(new List<ProvisionedJavaRuntime>(), false);
            }

            var runtimes = parsed
                .Where(runtime => runtime is not null && !string.IsNullOrWhiteSpace(runtime.Id))
                .Select(runtime => runtime!)
                .ToList();

            return new RegistryReadResult(runtimes, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"A Java nyilvántartás ({path}) nem olvasható, üresnek tekintve: {ex.Message}");
            return new RegistryReadResult(new List<ProvisionedJavaRuntime>(), false);
        }
    }

    /// <summary>
    /// A nyilvántartás atomikus írása: egyedi <c>*.tmp</c> fájl, majd csere.
    /// </summary>
    private Task WriteRegistryLockedAsync(List<ProvisionedJavaRuntime> runtimes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RuntimeRoot);

        var json = JsonSerializer.Serialize(runtimes, new JsonSerializerOptions { WriteIndented = true });
        var result = Storage.AtomicFile.TryWriteAllText(RegistryPath, json);
        if (!result.Success)
            throw new IOException(result.Error);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>A nyilvántartás beolvasásának eredménye.</summary>
    /// <param name="Runtimes">A bejegyzések (sérült fájl esetén üres).</param>
    /// <param name="LoadedFromDisk">Igaz, ha a fájl hiányzott vagy sikeresen beolvasható volt.</param>
    private sealed record RegistryReadResult(List<ProvisionedJavaRuntime> Runtimes, bool LoadedFromDisk);
}
