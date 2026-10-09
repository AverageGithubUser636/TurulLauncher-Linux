using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TurulMC.Core.Diagnostics;
using TurulMC.Core.Java;
using TurulMC.Core.Minecraft;
using TurulMC.Core.Models;
using TurulMC.Core.Mods;

namespace TurulMC.Features.SmokeTests;

/// <summary>
/// Füsttesztek a 4.5.0-s funkciókhoz: automatikus Java telepítés (Adoptium/Temurin)
/// és a Doctor diagnosztika + Support Bundle. Minden ellenőrzés a produkciós
/// belépési pontokat hívja (nem teszt-local másolatokat), és hálózat nélkül fut.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();
    private static string _tempRoot = string.Empty;
    private static int _counter;

    private static async Task<int> Main()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "TurulFeatures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        Console.WriteLine("TurulMC feature smoke tests — Java provisioning + Doctor");
        Console.WriteLine();

        try
        {
            JavaVersionMapTests();
            AdoptiumParsingTests();
            ProvisionerTests();
            JavaRuntimeServiceTests();
            ModCompatibilityTests();
            AtomicFileTests();
            LauncherPathsTests();
            ModManagerTests();
            ModrinthTests();
            ModpackTests();
            UpdateTests();
            DisplayTextTests();
            LaunchSyncTests();
            ContentOpsTests();
            await LinuxPortTestsAsync();
            await DoctorTestsAsync();
            await SupportBundleTestsAsync();
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add("váratlan hiba a futtatásban: " + ex);
            Console.WriteLine("FAIL váratlan hiba: " + ex);
        }
        finally
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"OSSZESEN: {_passed + _failed} ellenőrzés | PASS={_passed} FAIL={_failed}");
        if (_failed > 0)
        {
            Console.WriteLine("--- HIBAK ---");
            foreach (var failure in Failures) Console.WriteLine(failure);
        }

        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- Java verziótérkép

    private static void JavaVersionMapTests()
    {
        Check("JavaVersionMap: Minecraft verzió → Java főverzió", () =>
        {
            AssertEqual(8, JavaVersionMap.ResolveRequiredMajor("1.16.5"), "1.16.5");
            AssertEqual(16, JavaVersionMap.ResolveRequiredMajor("1.17.1"), "1.17.1");
            AssertEqual(17, JavaVersionMap.ResolveRequiredMajor("1.18"), "1.18");
            AssertEqual(17, JavaVersionMap.ResolveRequiredMajor("1.20.4"), "1.20.4");
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor("1.20.5"), "1.20.5");
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor("1.21.4"), "1.21.4");
            AssertEqual(25, JavaVersionMap.ResolveRequiredMajor("26.1.2"), "26.1.2");
        });

        Check("JavaVersionMap: manifest javaVersion felülírja a heurisztikát", () =>
        {
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor("1.20.4", 21), "manifest 21");
            AssertEqual(25, JavaVersionMap.ResolveRequiredMajor("26.1.2", 25), "manifest 25");
            AssertEqual(17, JavaVersionMap.ResolveRequiredMajor("1.20.4", 0), "manifest nélkül");
        });

        Check("JavaVersionMap: ismeretlen bemenet nem dob kivételt", () =>
        {
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor(null), "null");
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor(""), "üres");
            AssertEqual(21, JavaVersionMap.ResolveRequiredMajor("snapshot-abc"), "snapshot");
        });

        Check("JavaVersionMap: IsCompatible és PickBest", () =>
        {
            Assert(JavaVersionMap.IsCompatible(21, 21), "21/21");
            Assert(JavaVersionMap.IsCompatible(25, 21), "25 kiszolgálja a 21-et");
            Assert(JavaVersionMap.IsCompatible(21, 17), "21 kiszolgálja a 17-et");
            Assert(!JavaVersionMap.IsCompatible(17, 21), "17 nem elég a 21-hez");
            Assert(!JavaVersionMap.IsCompatible(17, 8), "17 nem helyettesíti a 8-at");
            AssertEqual(21, JavaVersionMap.PickBest(new[] { 8, 17, 21 }, 21), "pontos egyezés");
            AssertEqual(25, JavaVersionMap.PickBest(new[] { 17, 25 }, 21), "legkisebb kompatibilis");
            AssertEqual(0, JavaVersionMap.PickBest(new[] { 8 }, 21), "nincs kompatibilis");
        });
    }

    // ---------------------------------------------------------------- Adoptium feldolgozás

    private const string AdoptiumFixture = """
    [
      {
        "binary": {
          "architecture": "aarch64",
          "image_type": "jre",
          "os": "windows",
          "package": {
            "checksum": "1111111111111111111111111111111111111111111111111111111111111111",
            "link": "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.5%2B11/OpenJDK21U-jre_aarch64_windows_hotspot_21.0.5_11.zip",
            "name": "OpenJDK21U-jre_aarch64_windows_hotspot_21.0.5_11.zip",
            "size": 46000000
          }
        },
        "release_name": "jdk-21.0.5+11",
        "version": { "major": 21, "semver": "21.0.5+11.0.LTS", "openjdk_version": "21.0.5+11-LTS" }
      },
      {
        "binary": {
          "architecture": "x64",
          "image_type": "jre",
          "os": "windows",
          "package": {
            "checksum": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "link": "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.5%2B11/OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip",
            "name": "OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip",
            "size": 47123456
          }
        },
        "release_name": "jdk-21.0.5+11",
        "version": { "major": 21, "semver": "21.0.5+11.0.LTS", "openjdk_version": "21.0.5+11-LTS" }
      }
    ]
    """;

    /// <summary>Egyetlen (x64) jelölt: a negatív eseteknél nem eshet vissza tartalékra.</summary>
    private const string AdoptiumSingleFixture = """
    [
      {
        "binary": {
          "architecture": "x64",
          "image_type": "jre",
          "os": "windows",
          "package": {
            "checksum": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "link": "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.5%2B11/OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip",
            "name": "OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip",
            "size": 47123456
          }
        },
        "release_name": "jdk-21.0.5+11",
        "version": { "major": 21, "semver": "21.0.5+11.0.LTS", "openjdk_version": "21.0.5+11-LTS" }
      }
    ]
    """;

    private static void AdoptiumParsingTests()
    {
        Check("Adoptium: a kért architektúrát választja", () =>
        {
            var release = AdoptiumReleaseClient.ParseLatestRelease(AdoptiumFixture, 21, "x64");
            Assert(release is not null, "nem parse-olható a minta");
            AssertEqual(21, release!.FeatureVersion, "feature verzió");
            AssertEqual("x64", release.Architecture, "architektúra");
            AssertEqual(47123456L, release.SizeBytes, "méret");
            AssertEqual("OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip", release.FileName, "fájlnév");
            Assert(release.DownloadUrl.StartsWith("https://github.com/", StringComparison.Ordinal), "letöltési URL");
            Assert(release.FullVersion.StartsWith("21.0.5", StringComparison.Ordinal), "teljes verzió: " + release.FullVersion);
            AssertEqual(64, release.Sha256.Length, "sha256 hossz");
        });

        Check("Adoptium: tartalék, ha csak más architektúra van", () =>
        {
            var onlyArm = AdoptiumFixture.Replace("\"architecture\": \"x64\"", "\"architecture\": \"aarch64\"");
            var release = AdoptiumReleaseClient.ParseLatestRelease(onlyArm, 21, "x64");
            Assert(release is not null, "a tartalék ág nem működik");
            AssertEqual("aarch64", release!.Architecture, "tartalék architektúra");
        });

        Check("Adoptium: hibás/veszélyes jelölteket elutasít", () =>
        {
            Assert(AdoptiumReleaseClient.ParseLatestRelease("[]", 21) is null, "üres tömb");
            Assert(AdoptiumReleaseClient.ParseLatestRelease("nem json", 21) is null, "érvénytelen JSON");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(AdoptiumSingleFixture, 17) is null, "nem egyező feature verzió");

            var shortHash = AdoptiumSingleFixture.Replace(
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "abc123");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(shortHash, 21) is null, "rövid hash");

            var httpLink = AdoptiumSingleFixture.Replace("https://github.com/adoptium", "http://github.com/adoptium");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(httpLink, 21) is null, "http letöltés");

            var evilHost = AdoptiumSingleFixture.Replace("https://github.com/adoptium", "https://github.com.evil.example/adoptium");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(evilHost, 21) is null, "idegen hoszt");

            var notZip = AdoptiumSingleFixture.Replace(".zip\"", ".msi\"");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(notZip, 21) is null, "nem zip");

            var zeroSize = AdoptiumSingleFixture.Replace("\"size\": 47123456", "\"size\": 0");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(zeroSize, 21) is null, "nulla méret");

            var hugeSize = AdoptiumSingleFixture.Replace("\"size\": 47123456", "\"size\": 999999999");
            Assert(AdoptiumReleaseClient.ParseLatestRelease(hugeSize, 21) is null, "túl nagy csomag");
        });

        Check("Adoptium: URL és hoszt-allowlist", () =>
        {
            var url = AdoptiumReleaseClient.BuildLatestJreUrl(21, "x64");
            Assert(url.Contains("/assets/latest/21/hotspot", StringComparison.Ordinal), "API útvonal: " + url);

            // A platformszűrő a futó rendszerhez igazodik: Windowson windows,
            // Linuxon linux (különben a launcher ellentétes rendszerre töltené).
            var expectedOs = OperatingSystem.IsWindows() ? "os=windows" : "os=linux";
            if (OperatingSystem.IsMacOS()) expectedOs = "os=mac";
            Assert(url.Contains(expectedOs, StringComparison.Ordinal),
                $"platformszűrő ({expectedOs}) az URL-ben: " + url);
            Assert(AdoptiumReleaseClient.CurrentOsToken() is "windows" or "linux" or "mac", "érvényes os token");

            Assert(url.Contains("image_type=jre", StringComparison.Ordinal), "jre szűrő");
            Assert(url.StartsWith("https://api.adoptium.net/", StringComparison.Ordinal), "https");

            // Az explicit os tokenes túlterhelés rögzített értékkel tesztelhető.
            Assert(AdoptiumReleaseClient.BuildLatestJreUrl(21, "x64", "windows").Contains("os=windows", StringComparison.Ordinal),
                "explicit windows szűrő");

            Assert(AdoptiumHosts.IsAllowedDownloadUrl("https://github.com/a/b.zip"), "github");
            Assert(AdoptiumHosts.IsAllowedDownloadUrl("https://objects.githubusercontent.com/a/b.zip"), "githubusercontent");
            Assert(!AdoptiumHosts.IsAllowedDownloadUrl("http://github.com/a/b.zip"), "http tiltva");
            Assert(!AdoptiumHosts.IsAllowedDownloadUrl("https://evil.example/a.zip"), "idegen hoszt tiltva");
            Assert(!AdoptiumHosts.IsAllowedDownloadUrl("https://github.com.evil.example/a.zip"), "hamis hoszt tiltva");
        });
    }

    // ---------------------------------------------------------------- Provisioner

    private static void ProvisionerTests()
    {
        Check("Provisioner: érvénytelen feature verzió elutasítva", () =>
        {
            var root = NewDirectory("java-invalid");
            var provisioner = new JavaRuntimeProvisioner(runtimeRoot: root);
            AssertThrows<ArgumentException>(() => provisioner.ProvisionAsync(7).GetAwaiter().GetResult(), "7");
            AssertThrows<ArgumentException>(() => provisioner.ProvisionAsync(120).GetAwaiter().GetResult(), "120");
        });

        Check("Provisioner: üres gyökér listázása nem dob", () =>
        {
            var root = NewDirectory("java-empty");
            var provisioner = new JavaRuntimeProvisioner(runtimeRoot: root);
            AssertEqual(root, provisioner.RuntimeRoot, "runtime root");
            var runtimes = provisioner.ListProvisionedRuntimesAsync().GetAwaiter().GetResult();
            AssertEqual(0, runtimes.Count, "üres lista");
        });

        Check("Provisioner: korrupt runtimes.json nem dob és nem listáz", () =>
        {
            var root = NewDirectory("java-corrupt");
            File.WriteAllText(Path.Combine(root, "runtimes.json"), "{ ez nem json", Encoding.UTF8);
            var provisioner = new JavaRuntimeProvisioner(runtimeRoot: root);
            var runtimes = provisioner.ListProvisionedRuntimesAsync().GetAwaiter().GetResult();
            AssertEqual(0, runtimes.Count, "korrupt registry");
        });

        Check("Provisioner: path traversal és ismeretlen id elutasítva", () =>
        {
            var root = NewDirectory("java-remove");
            Directory.CreateDirectory(Path.Combine(root, "temurin-21-x64", "bin"));
            var provisioner = new JavaRuntimeProvisioner(runtimeRoot: root);
            Assert(!provisioner.RemoveProvisionedRuntimeAsync("../../evil").GetAwaiter().GetResult(), "traversal id");
            Assert(!provisioner.RemoveProvisionedRuntimeAsync("..\\..\\evil").GetAwaiter().GetResult(), "backslash traversal");
            Assert(!provisioner.RemoveProvisionedRuntimeAsync("nem-letezik").GetAwaiter().GetResult(), "ismeretlen id");
            Assert(Directory.Exists(Path.Combine(root, "temurin-21-x64")), "az idegen könyvtár megmaradt");
        });

        Check("Provisioner: nem létező javaw-ra mutató bejegyzést kidob", () =>
        {
            var root = NewDirectory("java-prune");
            var missingExe = Path.Combine(root, "temurin-17-x64", "bin", "javaw.exe");
            File.WriteAllText(Path.Combine(root, "runtimes.json"), JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = "temurin-17-x64",
                    Vendor = "Eclipse Temurin",
                    FeatureVersion = 17,
                    FullVersion = "17.0.9+9",
                    Architecture = "x64",
                    JavaPath = missingExe,
                    JavaExePath = string.Empty,
                    InstallDirectory = Path.Combine(root, "temurin-17-x64"),
                    SourceUrl = "https://github.com/adoptium/x.zip",
                    Sha256 = new string('a', 64),
                    PackageBytes = 1L,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                }
            }), Encoding.UTF8);

            var provisioner = new JavaRuntimeProvisioner(runtimeRoot: root);
            var runtimes = provisioner.ListProvisionedRuntimesAsync().GetAwaiter().GetResult();
            AssertEqual(0, runtimes.Count, "hiányzó javaw.exe kiszűrve");
        });
    }

    // ---------------------------------------------------------------- JavaRuntimeService

    private static void JavaRuntimeServiceTests()
    {
        Check("JavaRuntimeService: ParseJavaMajor", () =>
        {
            AssertEqual(21, JavaRuntimeService.ParseJavaMajor("21.0.5+11-LTS"), "21");
            AssertEqual(8, JavaRuntimeService.ParseJavaMajor("1.8.0_402"), "8");
            AssertEqual(25, JavaRuntimeService.ParseJavaMajor("25.0.4"), "25");
            // A `java -version` nyers sora idézőjellel kezdődik: a verziót a
            // GetJavaVersionAsync szűri ki előtte, a ParseJavaMajor erre 0-t ad.
            AssertEqual(0, JavaRuntimeService.ParseJavaMajor("\"25.0.4\" 2026-07-21"), "idézőjeles nyers sor");
            AssertEqual(0, JavaRuntimeService.ParseJavaMajor(""), "üres");
            AssertEqual(0, JavaRuntimeService.ParseJavaMajor("ismeretlen"), "szöveg");
        });

        Check("JavaRuntimeService: nem létező java nem blokkol", () =>
        {
            var missing = Path.Combine(_tempRoot, "nincs-ilyen", "javaw.exe");
            var started = Environment.TickCount64;
            var version = JavaRuntimeService.GetJavaVersionAsync(missing).GetAwaiter().GetResult();
            var elapsed = Environment.TickCount64 - started;
            AssertEqual("Unknown", version, "verzió");
            Assert(elapsed < 8000, $"túl lassú volt: {elapsed} ms");
        });

        Check("JavaRuntimeService: felismerés nem dob és kitölti a mezőket", () =>
        {
            var dataRoot = NewDirectory("java-detect");
            var service = new JavaRuntimeService(dataRoot: dataRoot);
            var runtimes = service.DetectInstalledRuntimesAsync().GetAwaiter().GetResult();
            Assert(runtimes.All(r => !string.IsNullOrWhiteSpace(r.Source)), "minden runtime-nak van forrása");
            Assert(runtimes.All(r => r.Major >= 0), "főverzió kitöltve");
        });

        Check("JavaRuntimeService: gépi Java verziópróba (ha van Java)", () =>
        {
            var dataRoot = NewDirectory("java-probe");
            var service = new JavaRuntimeService(dataRoot: dataRoot);
            var runtimes = service.DetectInstalledRuntimesAsync().GetAwaiter().GetResult();
            var valid = runtimes.Where(r => r.IsValid && r.Major > 0).ToList();
            if (valid.Count == 0)
            {
                Console.WriteLine("     (nincs a gépen Java — a verziópróba kihagyva)");
                return;
            }

            var probe = JavaRuntimeService.GetJavaVersionAsync(valid[0].Path).GetAwaiter().GetResult();
            Assert(JavaRuntimeService.ParseJavaMajor(probe) > 0, $"a próba nem adott verziót: '{probe}'");
        });

        Check("JavaRuntimeService: EnsureRuntimeAsync hiba esetén nem telepít magától", () =>
        {
            var dataRoot = NewDirectory("java-ensure");
            var service = new JavaRuntimeService(dataRoot: dataRoot);
            var detected = service.DetectInstalledRuntimesAsync().GetAwaiter().GetResult();
            var present = new HashSet<int>(detected.Where(r => r.IsValid).Select(r => r.Major));

            var absent = Enumerable.Range(8, 40).First(m => !present.Contains(m) && !JavaVersionMap.IsCompatible(m, 8));
            AssertThrows<InvalidOperationException>(
                () => service.EnsureRuntimeAsync(absent, allowInstall: false).GetAwaiter().GetResult(),
                $"Java {absent} telepítés nélkül");
        });

        Check("JavaRuntimeService: EnsureRuntimeAsync megtalálja a meglévő Javát", () =>
        {
            var dataRoot = NewDirectory("java-ensure2");
            var service = new JavaRuntimeService(dataRoot: dataRoot);
            var detected = service.DetectInstalledRuntimesAsync().GetAwaiter().GetResult()
                .Where(r => r.IsValid && r.Major > 0).ToList();
            if (detected.Count == 0)
            {
                Console.WriteLine("     (nincs a gépen Java — a meglévő runtime ellenőrzés kihagyva)");
                return;
            }

            var runtime = service.EnsureRuntimeAsync(detected[0].Major, allowInstall: false).GetAwaiter().GetResult();
            AssertEqual(detected[0].Major, runtime.Major, "főverzió");
            Assert(File.Exists(runtime.Path), "a kiválasztott java létezik");
        });

        Check("JavaRuntimeService: beállított Java path mentése atomikus", () =>
        {
            var dataRoot = NewDirectory("java-setpath");
            var service = new JavaRuntimeService(dataRoot: dataRoot);
            service.SetJavaPathAsync(@"C:\Program Files\Java\jdk-21\bin\javaw.exe").GetAwaiter().GetResult();
            AssertEqual(@"C:\Program Files\Java\jdk-21\bin\javaw.exe", service.GetConfiguredJavaPathAsync().GetAwaiter().GetResult(), "mentett érték");
            Assert(File.Exists(Path.Combine(dataRoot, "settings.json")), "settings.json létrejött");
            Assert(!Directory.EnumerateFiles(dataRoot, "*.tmp").Any(), "nem maradt .tmp fájl");
        });
    }

    // ---------------------------------------------------------------- Doctor

    private static async Task DoctorTestsAsync()
    {
        await CheckAsync("Doctor: teljes futás hálózat nélkül (stub HTTP)", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-happy", healthyJson: true);
            var doctor = CreateDoctor(options, HttpStatusCode.OK);
            var report = await doctor.RunAsync();

            var expected = new[]
            {
                "system", "settings-json", "instances-json", "java-configured", "java-detected",
                "java-required", "java-probe", "instance-dir", "write-access", "disk-space",
                "deps-vcpp", "deps-webview2", "net-mojang", "net-modrinth", "net-update",
                "net-server", "logs"
            };
            var ids = report.Checks.Select(c => c.Id).ToList();
            foreach (var id in expected) Assert(ids.Contains(id), $"hiányzó ellenőrzés: {id}");

            AssertEqual("ok", report.Checks.First(c => c.Id == "settings-json").Status.ToString().ToLowerInvariant(), "settings-json");
            AssertEqual("ok", report.Checks.First(c => c.Id == "net-mojang").Status.ToString().ToLowerInvariant(), "net-mojang");
            AssertEqual("ok", report.Checks.First(c => c.Id == "net-update").Status.ToString().ToLowerInvariant(), "net-update");
            AssertEqual("ok", report.Checks.First(c => c.Id == "write-access").Status.ToString().ToLowerInvariant(), "write-access");
            AssertEqual("ok", report.Checks.First(c => c.Id == "deps-webview2").Status.ToString().ToLowerInvariant(), "deps-webview2");
            AssertEqual("skipped", report.Checks.First(c => c.Id == "net-server").Status.ToString().ToLowerInvariant(), "net-server kihagyva");
            Assert(report.Healthy, "a jelentés nem egészséges: " + report.SummaryHu);
        });

        await CheckAsync("Doctor: progress minden ellenőrzésre lefut", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-progress", healthyJson: true);
            var seen = new List<string>();
            var progress = new Progress<DoctorCheck>(c => { lock (seen) seen.Add(c.Id); });
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync(progress);
            await Task.Delay(50);
            AssertEqual(report.Checks.Count, seen.Count, "progress hívások száma");
        });

        await CheckAsync("Doctor: hibás beállításfájl javítást ajánl", async () =>
        {
            var (options, root) = CreateDoctorFixture("doctor-badjson", healthyJson: true);
            File.WriteAllText(options.SettingsFilePath!, "{ ez nem json", Encoding.UTF8);
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();

            var check = report.Checks.First(c => c.Id == "settings-json");
            AssertEqual("failed", check.Status.ToString().ToLowerInvariant(), "státusz");
            AssertEqual(DoctorFix.ResetSettings, check.Fix, "javítás típusa");
            Assert(!report.Healthy, "hiba esetén nem lehet egészséges");
            Assert(report.ToPlainText().Contains("settings-json", StringComparison.Ordinal), "a szöveges jelentés tartalmazza a checket");
            Assert(Directory.Exists(root), "a fixture megvan");
        });

        await CheckAsync("Doctor: hiányzó beállításfájl figyelmeztetés", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-missingjson", healthyJson: true);
            File.Delete(options.SettingsFilePath!);
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();
            AssertEqual("warning", report.Checks.First(c => c.Id == "settings-json").Status.ToString().ToLowerInvariant(), "státusz");
        });

        await CheckAsync("Doctor: nem elérhető hálózat hibát ad", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-neterr", healthyJson: true);
            var report = await CreateDoctor(options, HttpStatusCode.ServiceUnavailable).RunAsync();
            AssertEqual("failed", report.Checks.First(c => c.Id == "net-mojang").Status.ToString().ToLowerInvariant(), "net-mojang");
            AssertEqual("failed", report.Checks.First(c => c.Id == "net-modrinth").Status.ToString().ToLowerInvariant(), "net-modrinth");
            Assert(!report.Healthy, "hálózati hiba esetén nem egészséges");
        });

        await CheckAsync("Doctor: hiányzó Java telepítést ajánl", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-java", healthyJson: true);
            options.RequiredJavaMajor = 99;
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();
            var check = report.Checks.First(c => c.Id == "java-required");
            AssertEqual("failed", check.Status.ToString().ToLowerInvariant(), "státusz");
            AssertEqual(DoctorFix.InstallJava, check.Fix, "javítás típusa");
        });

        await CheckAsync("Doctor: hiányzó instance mappa hibát ad", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-instance", healthyJson: true);
            options.InstanceDirectory = Path.Combine(_tempRoot, "doctor-instance", "nincs-ilyen-mappa");
            options.GameDirectory = options.InstanceDirectory;
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();
            AssertEqual("failed", report.Checks.First(c => c.Id == "instance-dir").Status.ToString().ToLowerInvariant(), "státusz");
        });

        await CheckAsync("Doctor: WebView2 hiánya figyelmeztetés", async () =>
        {
            var (options, _) = CreateDoctorFixture("doctor-webview", healthyJson: true);
            options.WebView2VersionProvider = () => null;
            var report = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();
            AssertEqual("skipped", report.Checks.First(c => c.Id == "deps-webview2").Status.ToString().ToLowerInvariant(), "nincs provider");

            options.WebView2VersionProvider = () => throw new InvalidOperationException("nincs WebView2");
            var report2 = await CreateDoctor(options, HttpStatusCode.OK).RunAsync();
            AssertEqual("warning", report2.Checks.First(c => c.Id == "deps-webview2").Status.ToString().ToLowerInvariant(), "dobó provider");
        });
    }

    // ---------------------------------------------------------------- Support bundle

    private static async Task SupportBundleTestsAsync()
    {
        await CheckAsync("Support bundle: teljes csomag tartalma", async () =>
        {
            var (options, root) = CreateDoctorFixture("bundle-full", healthyJson: true);
            var crashDir = Path.Combine(root, "instance", "crash-reports");
            Directory.CreateDirectory(crashDir);
            File.WriteAllText(Path.Combine(crashDir, "crash-2026-10-03.txt"), "crash", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "profiles.json"), "[]", Encoding.UTF8);

            var service = new SupportBundleService(options, CreateDoctor(options, HttpStatusCode.OK));
            var result = await service.CreateAsync(includeDoctorReport: true);

            Assert(result.Success, "a csomag nem készült el: " + result.Error);
            Assert(result.FileName.StartsWith("TurulSupport-", StringComparison.Ordinal), "fájlnév: " + result.FileName);
            Assert(File.Exists(result.FullPath), "a ZIP nem létezik");
            Assert(result.TotalBytes > 0, "üres ZIP");

            using var archive = ZipFile.OpenRead(result.FullPath);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert(names.Contains("system.txt"), "system.txt hiányzik");
            Assert(names.Contains("doctor.txt"), "doctor.txt hiányzik");
            Assert(names.Contains("settings.json"), "settings.json hiányzik");
            Assert(names.Contains("instances.json"), "instances.json hiányzik");
            Assert(names.Contains("crash-reports/crash-2026-10-03.txt"), "crash report hiányzik");
            Assert(names.Any(n => n.StartsWith("logs/", StringComparison.Ordinal)), "logok hiányoznak");
            Assert(result.IncludedFiles.SequenceEqual(result.IncludedFiles.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), "a lista nem rendezett");
            Assert(!names.Any(n => n.Contains("..", StringComparison.Ordinal)), "gyanús zip bejegyzés");
        });

        await CheckAsync("Support bundle: üres adatkönyvtárból is készül", async () =>
        {
            var root = NewDirectory("bundle-empty");
            var options = new DoctorOptions { DataRoot = root, LauncherVersion = "4.5.0-test" };
            var service = new SupportBundleService(options);
            var result = await service.CreateAsync(includeDoctorReport: false);
            Assert(result.Success, "nem készült el: " + result.Error);
            using var archive = ZipFile.OpenRead(result.FullPath);
            Assert(archive.Entries.Any(e => e.FullName == "system.txt"), "system.txt hiányzik");
            Assert(!archive.Entries.Any(e => e.FullName == "doctor.txt"), "doctor.txt nem kértük");
        });

        await CheckAsync("Support bundle: zárolt logfájl nem töri el a csomagot", async () =>
        {
            var (options, root) = CreateDoctorFixture("bundle-locked", healthyJson: true);
            var locked = Path.Combine(root, "logs", "launcher-locked.log");
            File.WriteAllText(locked, "lock", Encoding.UTF8);
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var service = new SupportBundleService(options);
                var result = await service.CreateAsync(includeDoctorReport: false);
                Assert(result.Success, "zárolt fájl mellett sem szabad elhasalnia: " + result.Error);
            }
        });

        await CheckAsync("Support bundle: hiányzó DataRoot hibát jelez, nem dob", async () =>
        {
            var service = new SupportBundleService(new DoctorOptions());
            var result = await service.CreateAsync();
            Assert(!result.Success, "üres DataRoot-dal nem lehet sikeres");
            Assert(!string.IsNullOrWhiteSpace(result.Error), "nincs hibaüzenet");
        });

        await CheckAsync("Support bundle: nem marad .tmp a support mappában", async () =>
        {
            var (options, root) = CreateDoctorFixture("bundle-tmp", healthyJson: true);
            var service = new SupportBundleService(options, CreateDoctor(options, HttpStatusCode.OK));
            var result = await service.CreateAsync();
            Assert(result.Success, "nem készült el");
            var leftovers = Directory.EnumerateFiles(Path.Combine(root, "support"), "*.tmp").ToList();
            AssertEqual(0, leftovers.Count, "maradék temp fájlok");
        });
    }

    // ---------------------------------------------------------------- mod kompatibilitás (4.6.0)

    private static void ModCompatibilityTests()
    {
        Check("MinecraftVersionOrder: séma, összehasonlítás, család", () =>
        {
            AssertEqual(-1, MinecraftVersionOrder.TryCompare("1.21.11", "26.2"), "1.21.11 < 26.2");
            AssertEqual(1, MinecraftVersionOrder.TryCompare("26.2", "26.1.2"), "26.2 > 26.1.2");
            AssertEqual(0, MinecraftVersionOrder.TryCompare("26.2", "26.2.0"), "26.2 == 26.2.0");
            AssertEqual(true, MinecraftVersionOrder.IsSnapshot("25w14a"), "pillanatkép felismerés");
            AssertEqual(false, MinecraftVersionOrder.IsSnapshot("26.2"), "26.2 nem pillanatkép");
            Assert(MinecraftVersionOrder.TryCompare("25w14a", "26.2") is null, "pillanatkép nem összehasonlítható");
            AssertEqual("1.21", MinecraftVersionOrder.Family("1.21.11"), "család 1.21.11");
            AssertEqual("26.2", MinecraftVersionOrder.Family("26.2"), "család 26.2");
            Assert(MinecraftVersionOrder.IsNewScheme("26.2"), "26.2 az új séma");
            Assert(!MinecraftVersionOrder.IsNewScheme("1.21.11"), "1.21.11 a régi séma");
        });

        Check("FabricVersionRange: 1.21.11 → 26.2 verzióütközés felismerése", () =>
        {
            // A tipikus 1.21.11-es mod tartománya: a 26.2-vel NEM kompatibilis.
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies(">=1.21.9 <1.22", "1.21.11"), "1.21.11 illeszkedik");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies(">=1.21.9 <1.22", "26.2"), "26.2 nem illeszkedik");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies(">=26.1", "26.2"), ">=26.1");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies(">=26.1", "1.21.11"), ">=26.1 fordítva");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("*", "26.2"), "csillag");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("", "26.2"), "üres tartomány");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("1.21.x", "1.21.11"), "1.21.x");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("1.21.x", "26.2"), "1.21.x nem 26.2");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("~1.21.4", "1.21.11"), "tilde");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("~1.21.4", "1.22"), "tilde felső korlát");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("^1.21", "26.2"), "caret 1.x");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("1.21", "1.21.4"), "rövid alak = család");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies(">=1.21 <1.22 || >=26.1", "26.2"), "VAGY kapcsolat");
            AssertEqual(RangeMatch.Unknown, FabricVersionRange.Satisfies(">=1.21", "25w14a"), "pillanatkép → ismeretlen");
            AssertEqual(RangeMatch.Unknown, FabricVersionRange.Satisfies("ez-nem-tartomany", "26.2"), "értelmezhetetlen → ismeretlen");
            // Él esetek (verifier M5): ezek korábban hamis "nem kompatibilis" eredményt adtak.
            AssertEqual(RangeMatch.Unknown, FabricVersionRange.Satisfies("||", "26.2"), "csak operátor → ismeretlen, nem 'nem'");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("1.20.1 - 1.21.4", "1.21.4"), "hyphen range alsó/felső határ");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("1.20.1 - 1.21.4", "1.21.11"), "hyphen range felett");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies(">=1.21<1.22", "1.22.5"), "szóköz nélküli felső korlát nem veszik el");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies(">=1.21\u00A0<1.22", "1.22.5"), "nem törhető szóköz kezelése");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies(">=1.21<1.22", "1.21.11"), "szóköz nélküli tartomány alsó határa");
            // További él esetek (verifier D7): ezek korábban hamis "nem kompatibilis" eredményt adtak.
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies(">= 1.20.1", "1.21.11"), "operátor utáni szóköz");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies(">= 1.20.1", "1.19.4"), "operátor utáni szóköz, nem illeszkedő");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("1.20.1-1.21.4", "1.21.4"), "szóköz nélküli hyphen range");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("1.20.1-1.21.4", "1.21.11"), "szóköz nélküli hyphen range felett");
            AssertEqual(RangeMatch.Unknown, FabricVersionRange.Satisfies(">=1.0 - 2.0", "1.21.11"), "hibás hyphen alak → ismeretlen");
            AssertEqual(RangeMatch.Yes, FabricVersionRange.Satisfies("1.0.0-beta", "1.0.0-beta"),
                "az előzetes verzió egyetlen verziónak számít (nem lesz belőle hyphen range)");
            AssertEqual(RangeMatch.No, FabricVersionRange.Satisfies("1.0.0-beta", "1.0.0"),
                "az előzetes verzió nem egyezik a kiadással");
        });

        Check("ModVersionOrder: valós Modrinth verziószámformátumok", () =>
        {
            AssertEqual(-1, ModVersionOrder.TryCompare("mc26.3-0.9.2-fabric", "0.9.3"), "platform előtag levágása");
            AssertEqual(1, ModVersionOrder.TryCompare("mc26.3-0.9.3-fabric", "0.9.2"), "fordított irány");
            AssertEqual(1, ModVersionOrder.TryCompare("1.0.0", "1.0.0-beta.1"), "kiadás > előzetes");
            AssertEqual(-1, ModVersionOrder.TryCompare("1.0.0-beta.1", "1.0.0-beta.2"), "előzetes sorrend");
            Assert(ModVersionOrder.TryCompare("fabric", "0.9.3") is null, "nincs szám → nem összehasonlítható");
            Assert(ModVersionOrder.IsOlder("0.9.2-fabric", "0.9.3"), "IsOlder");
            Assert(ModVersionOrder.IsNewer("1.2.3+1.21.4", "1.2.2"), "IsNewer build metaadattal");
            AssertEqual("0.9.3-alpha.1-neoforge", ModVersionOrder.ExtractComparableCore("mc26.3-0.9.3-alpha.1-neoforge"), "mag kiemelése");
            AssertEqual("", ModVersionOrder.ExtractComparableCore("nincs-szam"), "szám nélküli szöveg");
        });

        Check("ModTargetReconciler: valós verziószámok és célverzió-kompatibilitás", () =>
        {
            // A célban egy MÁSIK Minecraft-verzióhoz készült, szám szerint újabb példány van:
            // a csere kívánatos (ez a másolás „frissítem a modokat" stratégiájának lényege).
            var mods = Path.Combine(NewDirectory("reconcile-crossmc"), "mods");
            Directory.CreateDirectory(mods);
            CreateJar(Path.Combine(mods, "sodium-fabric-0.9.3+mc1.21.11.jar"),
                "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"0.9.3\",\"depends\":{\"minecraft\":\">=1.21 <1.22\"}}");
            var crossIndex = ModTargetReconciler.BuildIndex(mods, "26.2", "fabric");
            var crossInspection = ModTargetReconciler.Inspect(crossIndex, "sodium", "mc26.3-0.9.2-fabric");
            Assert(!crossInspection.HasNewerVersion, "a másik MC-verzióhoz készült példány nem blokkolhatja a cserét");
            AssertEqual(1, crossInspection.SameModFiles.Count, "a régi példány cserére kerül");

            // Ha a célban a célverzióval MŰKÖDŐ, szám szerint újabb példány van: nincs visszalépés.
            var mods2 = Path.Combine(NewDirectory("reconcile-block"), "mods");
            Directory.CreateDirectory(mods2);
            CreateJar(Path.Combine(mods2, "sodium-0.9.3.jar"),
                "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"0.9.3\",\"depends\":{\"minecraft\":\">=26.1\"}}");
            var blockIndex = ModTargetReconciler.BuildIndex(mods2, "26.2", "fabric");
            var blocked = ModTargetReconciler.Inspect(blockIndex, "sodium", "mc26.3-0.9.2-fabric");
            Assert(blocked.HasNewerVersion, "a működő, újabb példány blokkol (valós Modrinth verziószámmal is)");
            AssertEqual("0.9.3", blocked.NewerVersion, "újabb verzió");
            AssertEqual("0.9.2-fabric", ModVersionOrder.ExtractComparableCore("mc26.3-0.9.2-fabric"),
                "bejövő mag (a platform-előtag levágva, az utótag megmarad)");
        });

        Check("MinecraftVersionOrder: azonos pillanatképek egyenlők", () =>
        {
            Assert(MinecraftVersionOrder.IsSame("25w14a", "25w14a"), "azonos pillanatkép");
            Assert(MinecraftVersionOrder.IsSame("26.2", "26.2.0"), "26.2 == 26.2.0");
            Assert(!MinecraftVersionOrder.IsSame("25w14a", "25w15a"), "különböző pillanatkép");
            Assert(!MinecraftVersionOrder.IsSame("26.2", "26.1"), "különböző verzió");
        });

        Check("ModFileReader: fabric.mod.json és Forge/sérült JAR kezelése", () =>
        {
            var root = NewDirectory("modreader");
            var fabricJar = Path.Combine(root, "sodium.jar");
            CreateJar(fabricJar, "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"1.0.0\"," +
                                 "\"environment\":\"client\",\"depends\":{\"minecraft\":\">=1.21.9 <1.22\",\"fabricloader\":\">=0.16.0\"}}");
            var info = ModFileReader.TryRead(fabricJar, out var error);
            Assert(info is not null, "fabric.mod.json nem olvasható: " + error);
            AssertEqual("sodium", info!.Id, "id");
            AssertEqual("Sodium", info.Title, "név");
            AssertEqual("1.0.0", info.Version, "verzió");
            AssertEqual(">=1.21.9 <1.22", info.MinecraftRange, "minecraft igény");
            AssertEqual(">=0.16.0", info.LoaderRange, "loader igény");
            AssertEqual(ModFileKind.Fabric, info.Kind, "típus");

            var forgeJar = Path.Combine(root, "forge-mod.jar");
            using (var archive = ZipFile.Open(forgeJar, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("META-INF/mods.toml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("modLoader=\"javafml\"");
            }
            var forge = ModFileReader.TryRead(forgeJar, out _);
            AssertEqual(ModFileKind.Forge, forge?.Kind ?? ModFileKind.Unknown, "Forge felismerés");

            var brokenJar = Path.Combine(root, "broken.jar");
            File.WriteAllText(brokenJar, "ez nem zip", Encoding.UTF8);
            var broken = ModFileReader.TryRead(brokenJar, out var brokenError);
            Assert(broken is null, "sérült JAR-t nem szabad modként felismerni");
            Assert(brokenError.Length > 0, "a sérült JAR-hoz magyar indoklás kell");

            var missing = ModFileReader.TryRead(Path.Combine(root, "nincs.jar"), out var missingError);
            Assert(missing is null && missingError.Length > 0, "hiányzó fájl kezelése");
        });

        Check("ModCompatibilityScanner: célverzió, loader, duplikáció, sérült JAR", () =>
        {
            var root = NewDirectory("modscan");
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);

            CreateJar(Path.Combine(mods, "sodium-1.0.0.jar"),
                "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"1.0.0\",\"depends\":{\"minecraft\":\">=1.21 <1.22\"}}");
            CreateJar(Path.Combine(mods, "sodium-0.9.0.jar"),
                "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"0.9.0\",\"depends\":{\"minecraft\":\">=1.20 <1.21\"}}");
            CreateJar(Path.Combine(mods, "lithium.jar"),
                "{\"id\":\"lithium\",\"name\":\"Lithium\",\"version\":\"2.0.0\",\"depends\":{\"minecraft\":\">=26.1\",\"fabricloader\":\">=0.16.0\"}}");
            CreateJar(Path.Combine(mods, "old-loader.jar"),
                "{\"id\":\"oldloader\",\"name\":\"Old Loader Mod\",\"version\":\"1.0.0\",\"depends\":{\"minecraft\":\">=26.1\",\"fabricloader\":\">=0.19.0\"}}");
            CreateJar(Path.Combine(mods, "fabric-api.jar.disabled"),
                "{\"id\":\"fabric-api\",\"name\":\"Fabric API\",\"version\":\"0.100.0\",\"depends\":{\"minecraft\":\"*\"}}");
            var forgeJar = Path.Combine(mods, "forge-only.jar");
            using (var archive = ZipFile.Open(forgeJar, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("META-INF/mods.toml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("modLoader=\"javafml\"");
            }
            File.WriteAllText(Path.Combine(mods, "broken.jar"), "nem zip", Encoding.UTF8);

            var report = ModCompatibilityScanner.Scan(mods, "26.2", "fabric", "0.16.9");
            AssertEqual(7, report.Total, "összes mod");
            AssertEqual(6, report.Enabled, "aktív modok");
            AssertEqual(1, report.Disabled, "letiltott modok");
            AssertEqual(2, report.Compatible, "kompatibilis (lithium + fabric-api)");
            AssertEqual(3, report.Incompatible, "inkompatibilis (sodium + old-loader + Forge)");
            AssertEqual(1, report.Unknown, "nem ellenőrizhető (sérült)");
            AssertEqual(1, report.Duplicates, "duplikált sodium");

            var sodium = report.Entries.First(x => x.FileName == "sodium-1.0.0.jar");
            AssertEqual(ModCompatibilityStatus.IncompatibleGame, sodium.Status, "sodium 1.21 → 26.2");
            var oldLoader = report.Entries.First(x => x.FileName == "old-loader.jar");
            AssertEqual(ModCompatibilityStatus.IncompatibleLoader, oldLoader.Status, "loader igény nem teljesül");
            var forge = report.Entries.First(x => x.FileName == "forge-only.jar");
            AssertEqual(ModCompatibilityStatus.NotFabric, forge.Status, "Forge mod Fabric Instance-ban");
            var duplicate = report.Entries.First(x => x.FileName == "sodium-0.9.0.jar");
            AssertEqual(ModCompatibilityStatus.Duplicate, duplicate.Status, "duplikáció a régebbi példányon");
            Assert(duplicate.ReasonHu.Contains("sodium-1.0.0.jar", StringComparison.Ordinal), "a megtartott példány a magyar indoklásban");
            AssertEqual(5, report.Problems.Count, "blokkoló problémák (sodium, régi loader, Forge, duplikált, sérült)");
            Assert(report.SummaryHu.Contains("7 mod", StringComparison.Ordinal), "összegző szöveg");

            // Ugyanez 1.21.11 célverzióval: a sodium rendben, a lithium nem.
            var report121 = ModCompatibilityScanner.Scan(mods, "1.21.11", "fabric", "0.16.9");
            AssertEqual(ModCompatibilityStatus.IncompatibleGame,
                report121.Entries.First(x => x.FileName == "lithium.jar").Status, "lithium 26.1 → 1.21.11");
            AssertEqual(ModCompatibilityStatus.Compatible,
                report121.Entries.First(x => x.FileName == "sodium-1.0.0.jar").Status, "sodium 1.21.11");

            // Loader nélküli Instance figyelmeztet.
            var vanilla = ModCompatibilityScanner.Scan(mods, "26.2", "none", "");
            Assert(vanilla.Notices.Any(x => x.Contains("loader nélküli", StringComparison.Ordinal)), "vanilla figyelmeztetés");
        });

        Check("ModTargetReconciler: cél Instance újabb verziója és duplikátuma", () =>
        {
            var root = NewDirectory("reconcile");
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);
            CreateJar(Path.Combine(mods, "sodium-3.0.0.jar"), "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"3.0.0\"}");
            CreateJar(Path.Combine(mods, "sodium-regi.jar"), "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"1.0.0\"}");
            CreateJar(Path.Combine(mods, "lithium.jar"), "{\"id\":\"lithium\",\"name\":\"Lithium\",\"version\":\"1.0.0\"}");

            var newer = ModTargetReconciler.Inspect(mods, "sodium", "2.0.0", excludeFileName: "sodium-2.0.0.jar");
            Assert(newer.HasNewerVersion, "a cél Instance-ban újabb verzió van — nem szabad visszalépni");
            AssertEqual("3.0.0", newer.NewerVersion, "legújabb verzió");
            AssertEqual(2, newer.SameModFiles.Count, "két azonos mod-id fájl a célban");

            var older = ModTargetReconciler.Inspect(mods, "sodium", "4.0.0", excludeFileName: "sodium-4.0.0.jar");
            Assert(!older.HasNewerVersion, "nincs újabb verzió a célban");
            AssertEqual(2, older.SameModFiles.Count, "a duplikátumok lecserélendők");
            Assert(older.ReasonHu.Length > 0, "a duplikátumhoz indoklás tartozik");

            var excluded = ModTargetReconciler.Inspect(mods, "lithium", "1.0.0", excludeFileName: "lithium.jar");
            AssertEqual(0, excluded.SameModFiles.Count, "a frissen letöltött fájlt nem jelöli cserére");

            var unknown = ModTargetReconciler.Inspect(mods, "nincs-ilyen-mod", "1.0.0", null);
            Assert(!unknown.HasNewerVersion && unknown.SameModFiles.Count == 0, "ismeretlen mod azonosító");
            AssertEqual(0, ModTargetReconciler.Inspect(Path.Combine(root, "nincs-mappa"), "sodium", "1.0.0", null).SameModFiles.Count,
                "hiányzó mappa nem dob");
        });

        Check("ModUpdatePlanner: célverzióhoz illő Modrinth verzió kiválasztása", () =>
        {
            var json = """
            [
              {"id":"V1","version_number":"1.0.0","version_type":"release","date_published":"2026-01-01T00:00:00Z",
               "game_versions":["26.2"],"loaders":["fabric"],
               "files":[{"filename":"mod-1.jar","url":"https://cdn.modrinth.com/data/x/versions/V1/mod-1.jar","primary":true,"size":10,"hashes":{"sha512":"AA"}}]},
              {"id":"V2","version_number":"2.0.0-beta","version_type":"beta","date_published":"2026-07-01T00:00:00Z",
               "game_versions":["26.2"],"loaders":["fabric"],
               "files":[{"filename":"mod-2.jar","url":"https://cdn.modrinth.com/data/x/versions/V2/mod-2.jar","primary":true,"size":10,"hashes":{"sha512":"BB"}}]},
              {"id":"V3","version_number":"3.0.0","version_type":"release","date_published":"2026-06-01T00:00:00Z",
               "game_versions":["26.2"],"loaders":["fabric"],
               "files":[{"filename":"mod-3.jar","url":"https://cdn.modrinth.com/data/x/versions/V3/mod-3.jar","primary":true,"size":10,"hashes":{"sha512":"CC"}}]},
              {"id":"V4","version_number":"1.5.0","version_type":"release","date_published":"2026-05-01T00:00:00Z",
               "game_versions":["1.21.11"],"loaders":["fabric"],
               "files":[{"filename":"mod-4.jar","url":"https://cdn.modrinth.com/data/x/versions/V4/mod-4.jar","primary":true,"size":10,"hashes":{"sha512":"DD"}}]},
              {"id":"V5","version_number":"3.1.0","version_type":"release","date_published":"2026-08-01T00:00:00Z",
               "game_versions":["26.2"],"loaders":["forge"],
               "files":[{"filename":"mod-5.jar","url":"https://cdn.modrinth.com/data/x/versions/V5/mod-5.jar","primary":true,"size":10,"hashes":{"sha512":"EE"}}]},
              {"id":"V6","version_number":"1.2.0","version_type":"release","date_published":"2026-02-01T00:00:00Z",
               "game_versions":["26.1.2"],"loaders":["fabric"],
               "files":[{"filename":"mod-6.jar","url":"https://cdn.modrinth.com/data/x/versions/V6/mod-6.jar","primary":true,"size":10,"hashes":{"sha512":"FF"}}]}
            ]
            """;

            var best = ModUpdatePlanner.PickBest(json, "26.2", "fabric", "V1");
            AssertEqual("V3", best?.VersionId ?? "", "release előnyben a béta előtt");
            AssertEqual("mod-3.jar", best?.FileName ?? "", "fájlnév");
            AssertEqual("exact", best?.MatchKind ?? "", "pontos egyezés");

            // A verziószám az elsődleges: a 3.0.0 telepítése után a régebbi 1.0.0/2.0.0-beta
            // kiadások nem ajánlhatók fel (korábban a dátum-alapú szabály miatt felajánlotta).
            Assert(ModUpdatePlanner.PickBest(json, "26.2", "fabric", "V3") is null,
                "a telepített 3.0.0 után nincs újabb kiadás → nincs mit frissíteni");

            var family = ModUpdatePlanner.PickBest(json, "26.1", "fabric", "");
            AssertEqual("V6", family?.VersionId ?? "", "verziócsalád-egyezés");
            AssertEqual("family", family?.MatchKind ?? "", "család jelölés");
            Assert(!ModUpdatePlanner.IsInstallable(family),
                "a verziócsalád-egyezés tájékoztató: nem telepíthető automatikusan (nem lehet ellentmondás a terv és a telepítő között)");
            Assert(ModUpdatePlanner.IsInstallable(ModUpdatePlanner.PickBest(json, "26.2", "fabric", "V1")),
                "a pontos verzióegyezés telepíthető");

            // Visszalépés-védelem verziószám alapján (akkor is, ha a dátum nem ismert).
            Assert(ModUpdatePlanner.PickBest(json, "26.2", "fabric", "V9", "99.0.0") is null,
                "a telepítettnél régebbi kiadást akkor sem ajánljuk, ha nincs dátum");
            Assert(ModUpdatePlanner.PickBest(json, "26.2", "fabric", "V9", "0.1.0")?.VersionId == "V3",
                "régi telepített verzió esetén a legjobb jelölt jön");

            Assert(ModUpdatePlanner.IsInstalledVersionCompatible(json, "26.2", "fabric", "V3"),
                "a telepített verzió támogatja a célverziót");
            Assert(!ModUpdatePlanner.IsInstalledVersionCompatible(json, "1.21.11", "fabric", "V3"),
                "a telepített verzió nem támogatja a másik verziót");
            Assert(!ModUpdatePlanner.IsInstalledVersionCompatible(json, "26.2", "fabric", "NINCS"),
                "ismeretlen verzióazonosító");

            Assert(ModUpdatePlanner.PickBest(json, "1.20.1", "fabric", "") is null, "nincs találat");
            Assert(ModUpdatePlanner.PickBest(json, "1.21.11", "fabric", "V4") is null,
                "visszalépés-védelem: a telepítettnél régebbi kiadást nem ajánlunk");
            Assert(ModUpdatePlanner.PickBest(json, "26.2", "forge", "")?.VersionId == "V5", "forge szűrés");
            Assert(ModUpdatePlanner.PickBest("nem json", "26.2", "fabric", "") is null, "sérült JSON nem dob");
        });

        Check("Indítási argumentumok: ablakméret és extra JVM argumentumok", () =>
        {
            var launcher = new MinecraftLauncherService(new StubInstallationService(), new JavaRuntimeService(dataRoot: NewDirectory("args-java")));
            using var doc = JsonDocument.Parse("""
            {
              "game": [
                {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}","--height","${resolution_height}"]},
                "--username","${auth_player_name}"
              ],
              "jvm": ["-Djava.library.path=${natives_directory}","-cp","${classpath}"]
            }
            """);
            var config = new LaunchConfig
            {
                Username = "Teszt",
                Version = "26.2",
                GameDirectory = @"C:\game",
                AssetsDirectory = @"C:\assets",
                NativesDirectory = @"C:\natives",
                Uuid = "00000000-0000-0000-0000-000000000000",
                VersionArguments = doc.RootElement.Clone(),
                WindowWidth = 1280,
                WindowHeight = 720,
                AdditionalJvmArgs = new List<string> { "-Dturul.test=1" }
            };

            var gameArgs = launcher.BuildGameArguments(config);
            Assert(gameArgs.Contains("--username"), "manifest játék argumentum megmaradt");
            AssertEqual(1, gameArgs.Count(x => x == "--width"), "a feature-höz kötött --width nem duplikálódik");
            var widthIndex = gameArgs.IndexOf("--width");
            AssertEqual("1280", gameArgs[widthIndex + 1], "ablak szélesség");
            AssertEqual("720", gameArgs[widthIndex + 3], "ablak magasság");

            var jvmArgs = launcher.BuildJvmArguments(config);
            Assert(jvmArgs.Contains("-Dturul.test=1"), "extra JVM argumentum manifest jelenlétében is érvényesül");
            AssertEqual(1, jvmArgs.Count(x => x == "-Dturul.test=1"), "nincs duplikáció");
        });
    }

    // ---------------------------------------------------------------- tartós írás (AtomicFile)

    private static void AtomicFileTests()
    {
        Check("AtomicFile: írásvédett célfájl fölé is ment (indítási hiba javítása)", () =>
        {
            var root = NewDirectory("atomic-readonly");
            var path = Path.Combine(root, "instances.json");
            File.WriteAllText(path, "{\"regi\":true}", Encoding.UTF8);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(path, "{\"uj\":true}");
            Assert(result.Success, "írásvédett fájl mentése sikertelen: " + result.Error);
            AssertEqual("{\"uj\":true}", File.ReadAllText(path), "tartalom");
            Assert(!File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly), "readonly attribútum leválasztva");
            AssertEqual("{\"regi\":true}", File.ReadAllText(path + ".bak"), "biztonsági másolat a régi tartalommal");
        });

        Check("AtomicFile: elárvult fix nevű .tmp (írásvédett) nem töri el a mentést", () =>
        {
            var root = NewDirectory("atomic-staletmp");
            var path = Path.Combine(root, "instances.json");
            var stale = path + ".tmp";
            File.WriteAllText(stale, "szemet", Encoding.UTF8);
            File.SetAttributes(stale, File.GetAttributes(stale) | FileAttributes.ReadOnly);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));

            var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(path, "{\"ok\":1}");
            Assert(result.Success, "mentés sikertelen: " + result.Error);
            Assert(!File.Exists(stale), "az elárvult temp fájl nem tűnt el");
            AssertEqual("{\"ok\":1}", File.ReadAllText(path), "tartalom");
        });

        Check("AtomicFile: mappa a temp helyén + friss temp nem blokkol", () =>
        {
            var root = NewDirectory("atomic-tmpdir");
            var path = Path.Combine(root, "instances.json");
            Directory.CreateDirectory(path + ".tmp");
            File.WriteAllText(Path.Combine(path + ".tmp", "x.txt"), "x", Encoding.UTF8);

            var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(path, "{\"mappa\":1}");
            Assert(result.Success, "mentés sikertelen: " + result.Error);
            Assert(!Directory.Exists(path + ".tmp"), "a temp mappa nem tűnt el");
            AssertEqual("{\"mappa\":1}", File.ReadAllText(path), "tartalom");

            var fresh = path + ".tmp";
            File.WriteAllText(fresh, "fut", Encoding.UTF8);
            var second = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(path, "{\"friss\":1}");
            Assert(second.Success, "második mentés sikertelen: " + second.Error);
            Assert(File.Exists(fresh), "a friss temp fájlt nem szabad törölni");
        });

        Check("AtomicFile: hiba esetén nem dob, hanem eredményt ad", () =>
        {
            var root = NewDirectory("atomic-error");
            var blocker = Path.Combine(root, "fajl-nev");
            File.WriteAllText(blocker, "nem mappa", Encoding.UTF8);
            var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(
                Path.Combine(blocker, "x", "instances.json"), "{}");
            Assert(!result.Success, "lehetetlen útvonalra nem lehet sikeres a mentés");
            Assert(result.Error.Length > 0, "a hibaok üres");
        });

        Check("AtomicFile: párhuzamos mentések nem rontják el a fájlt", () =>
        {
            var root = NewDirectory("atomic-parallel");
            var path = Path.Combine(root, "instances.json");
            var errors = new List<string>();
            Parallel.For(0, 24, i =>
            {
                var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(
                    path, $"{{\"i\":{i}}}", createBackup: false);
                if (!result.Success) lock (errors) errors.Add(result.Error);
            });
            AssertEqual(0, errors.Count, "párhuzamos hibák: " + string.Join(" | ", errors));
            var content = File.ReadAllText(path);
            Assert(content.StartsWith('{') && content.EndsWith('}'), "a fájl sérült: " + content);
            AssertEqual(0, Directory.GetFiles(root, "*.tmp").Length, "maradék temp fájlok");
        });

        Check("AtomicFile: induláskori temp takarítás csak a régieket törli", () =>
        {
            var root = NewDirectory("atomic-cleanup");
            var old = Path.Combine(root, "instances.json.tmp");
            var oldGuid = Path.Combine(root, "settings.json.abc123.tmp");
            var fresh = Path.Combine(root, "profiles.json.tmp");
            File.WriteAllText(old, "x", Encoding.UTF8);
            File.WriteAllText(oldGuid, "x", Encoding.UTF8);
            File.WriteAllText(fresh, "x", Encoding.UTF8);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(oldGuid, DateTime.UtcNow.AddMinutes(-30));

            var removed = TurulMC.Core.Storage.AtomicFile.CleanupStaleTemps(root);
            AssertEqual(2, removed, "törölt temp fájlok");
            Assert(!File.Exists(old) && !File.Exists(oldGuid), "a régi temp fájlok megmaradtak");
            Assert(File.Exists(fresh), "a friss temp fájlt nem szabad törölni");
        });
    }

    // ---------------------------------------------------------------- adatkönyvtár feloldása

    // ---------------------------------------------------------------- Linux port

    /// <summary>
    /// A Linux-port kritikus útvonalainak lefedése. Mind a produkciós kódot hívja
    /// (nem teszt-saját másolatot), és hálózat nélkül fut.
    /// </summary>
    private static async Task LinuxPortTestsAsync()
    {
        await CheckAsync("Linux: tar.gz csomag kicsomagolása a produkciós úton", async () =>
        {
            var root = NewDirectory("tar-extract");
            var staging = Path.Combine(root, "staging");
            var tarPath = Path.Combine(root, "jdk.tar.gz");

            // Egy valódi tar.gz csomag: bin/java + license + egy könyvtár.
            await CreateTarGzAsync(tarPath, new Dictionary<string, string>
            {
                ["jdk-21/bin/java"] = "#!/bin/sh\necho fake-java\n",
                ["jdk-21/legal/java.base/LICENSE"] = "MIT",
                ["jdk-21/release"] = "JAVA_VERSION=\"21\""
            });

            await JavaRuntimeProvisioner.ExtractPackageAsync(tarPath, staging, null, "test");

            Assert(File.Exists(Path.Combine(staging, "jdk-21", "bin", "java")), "a bin/java kicsomagolva");
            Assert(File.Exists(Path.Combine(staging, "jdk-21", "release")), "a release fájl kicsomagolva");
            AssertEqual(3, Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Length, "fájlszám");
        });

        await CheckAsync("Linux: a kicsomagolt bin/java végrehajtható lesz", async () =>
        {
            if (OperatingSystem.IsWindows()) return; // ez a viselkedés csak Unixon érvényes

            var root = NewDirectory("tar-exec");
            var staging = Path.Combine(root, "staging");
            var tarPath = Path.Combine(root, "jdk.tar.gz");
            await CreateTarGzAsync(tarPath, new Dictionary<string, string>
            {
                ["jdk-21/bin/java"] = "#!/bin/sh\necho fake-java\n"
            });

            await JavaRuntimeProvisioner.ExtractPackageAsync(tarPath, staging, null, "test");

            var java = Path.Combine(staging, "jdk-21", "bin", "java");
            var mode = File.GetUnixFileMode(java);
            Assert(mode.HasFlag(UnixFileMode.UserExecute),
                "a java futtatható bitje beállítva (különben a launcher nem tudná indítani)");
        });

        await CheckAsync("Linux: a tar zip-slip bejegyzést elutasítja", async () =>
        {
            var root = NewDirectory("tar-slip");
            var staging = Path.Combine(root, "staging");
            var tarPath = Path.Combine(root, "evil.tar.gz");
            await CreateTarGzAsync(tarPath, new Dictionary<string, string>
            {
                ["../../../../etc/turul-pwned"] = "pwned",
                ["jdk-21/bin/java"] = "ok"
            });

            await JavaRuntimeProvisioner.ExtractPackageAsync(tarPath, staging, null, "test");

            Assert(!File.Exists("/etc/turul-pwned"), "nem írt a staging könyvtáron kívülre");
            Assert(File.Exists(Path.Combine(staging, "jdk-21", "bin", "java")), "a jó bejegyzés megmaradt");
        });

        await CheckAsync("Linux: ZIP csomag továbbra is működik", async () =>
        {
            var root = NewDirectory("zip-extract");
            var staging = Path.Combine(root, "staging");
            var zipPath = Path.Combine(root, "jdk.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("jdk-21/bin/java.exe");
                await using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes("fake");
                await s.WriteAsync(bytes);
            }

            await JavaRuntimeProvisioner.ExtractPackageAsync(zipPath, staging, null, "test");
            Assert(File.Exists(Path.Combine(staging, "jdk-21", "bin", "java.exe")), "a ZIP bejegyzés kicsomagolva");
        });

        Check("Linux: a Java futtatható neve platformfüggő", () =>
        {
            var root = NewDirectory("locate-java");
            if (OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(root, "bin");
                Directory.CreateDirectory(exe);
                File.WriteAllText(Path.Combine(exe, "javaw.exe"), "");
                var (launch, _) = JavaRuntimeProvisioner.LocateJavaExecutables(root, "test");
                Assert(launch.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase), "Windowson a javaw.exe");
            }
            else
            {
                var exe = Path.Combine(root, "bin");
                Directory.CreateDirectory(exe);
                File.WriteAllText(Path.Combine(exe, "java"), "");
                var (launch, console) = JavaRuntimeProvisioner.LocateJavaExecutables(root, "test");
                Assert(Path.GetFileName(launch) == "java", "Linuxon a sima 'java'");
                Assert(console is not null, "a konzolos java megtalálva");
            }
        });

        Check("Linux: a javaPath validátor elfogadja a rendszerszintű javát", () =>
        {
            // Létrehozzuk a saját "rendszerszintű" javánkat a tesztkönyvtárban,
            // hogy a validátor fájl-létezés ellenőrzése is lefusson.
            var root = NewDirectory("java-path");
            var javaPath = Path.Combine(root, OperatingSystem.IsWindows() ? "java.exe" : "java");
            File.WriteAllText(javaPath, "");

            TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(javaPath); // nem dob

            // Idegen név továbbra sem mehet át (a notjava "java"-ra végződne, de nem egyezik).
            var notJava = Path.Combine(root, OperatingSystem.IsWindows() ? "notjava.exe" : "notjava");
            File.WriteAllText(notJava, "");
            AssertThrows<ArgumentException>(
                () => TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(notJava),
                "nem java nevű fájlt elutasít");

            // Az RCE-zár (argumentumok) továbbra is zárva.
            AssertThrows<ArgumentException>(
                () => TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(javaPath + " -version"),
                "argumentumos javaPath elutasít");
        });

        Check("Linux: a platformszabályok a futó rendszerhez igazodnak", () =>
        {
            var os = TurulMC.Core.Platform.LauncherPlatform.MinecraftOsName;
            if (OperatingSystem.IsWindows()) AssertEqual("windows", os, "Mojang os.name");
            else if (OperatingSystem.IsMacOS()) AssertEqual("osx", os, "Mojang os.name");
            else AssertEqual("linux", os, "Mojang os.name");

            Assert(TurulMC.Core.Platform.LauncherPlatform.OsMatches(null), "üres szabály mindenhol érvényes");
            Assert(TurulMC.Core.Platform.LauncherPlatform.OsMatches(os), "saját platform illeszkedik");
            Assert(!TurulMC.Core.Platform.LauncherPlatform.OsMatches("windows" == os ? "linux" : "windows"),
                "más platform nem illeszkedik");

            Assert(TurulMC.Core.Platform.LauncherPlatform.ArchitectureMatches(null), "üres arch illeszkedik");
            Assert(TurulMC.Core.Platform.LauncherPlatform.ArchitectureMatches(
                TurulMC.Core.Platform.LauncherPlatform.RuleArchitecture), "saját arch illeszkedik");
        });

        Check("Linux: az Adoptium csomag-kiterjesztése platformfüggő", () =>
        {
            Assert(AdoptiumReleaseClient.HasSupportedArchiveExtension("OpenJDK21U-jre_x64_windows_hotspot.zip"), "zip elfogadva");
            Assert(AdoptiumReleaseClient.HasSupportedArchiveExtension("OpenJDK21U-jre_x64_linux_hotspot.tar.gz"), "tar.gz elfogadva");
            Assert(AdoptiumReleaseClient.HasSupportedArchiveExtension("OpenJDK21U-jre_x64_linux_hotspot.tgz"), "tgz elfogadva");
            Assert(!AdoptiumReleaseClient.HasSupportedArchiveExtension("payload.sh"), "más kiterjesztés elutasítva");
            Assert(!AdoptiumReleaseClient.HasSupportedArchiveExtension("evil.exe"), "exe elutasítva");

            var ext = AdoptiumReleaseClient.CurrentArchiveExtension();
            Assert(ext == ".zip" || ext == ".tar.gz", "érvényes kiterjesztés: " + ext);
        });
    }

    /// <summary>
    /// Valódi tar.gz csomag készítése a teszthez (a bejegyzésnevekben
    /// explicit megadott útvonalakkal, hogy a zip-slip eset is reprodukálható legyen).
    /// </summary>
    private static async Task CreateTarGzAsync(string path, IReadOnlyDictionary<string, string> entries)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new System.Formats.Tar.TarWriter(gzip, leaveOpen: false);

        foreach (var (name, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes)
            };
            writer.WriteEntry(entry);
        }
    }

    // ---------------------------------------------------------------- ModManager

    /// <summary>
    /// A ModManager (mod + resource pack fájlműveletek) lefedése.
    /// Mind a produkciós kódot hívja, hálózat nélkül.
    /// </summary>
    private static void ModManagerTests()
    {
        var manager = new TurulMC.Core.Mods.ModManager();

        Check("Mods: üres és hiányzó mappa üres listát ad", () =>
        {
            var root = NewDirectory("mods-empty");
            AssertEqual(0, manager.ListMods(Path.Combine(root, "mods")).Count, "hiányzó mappa");
            Directory.CreateDirectory(Path.Combine(root, "mods2"));
            AssertEqual(0, manager.ListMods(Path.Combine(root, "mods2")).Count, "üres mappa");
        });

        Check("Mods: fabric jar felismerése és metaadatok", () =>
        {
            var root = NewDirectory("mods-list");
            var mods = Path.Combine(root, "mods");
            CreateJar(Path.Combine(mods, "sodium-1.0.0.jar"),
                "{\"id\":\"sodium\",\"name\":\"Sodium\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(mods, "readme.txt"), "nem mod");
            File.WriteAllText(Path.Combine(mods, "old.jar.disabled"), "PK\0\0");

            var list = manager.ListMods(mods);
            AssertEqual(2, list.Count, "csak a jar-ok (a txt kimarad)");
            var sodium = list.First(m => m.FileName == "sodium-1.0.0.jar");
            Assert(sodium.Enabled, "a sima jar engedélyezett");
            AssertEqual("Sodium", sodium.Title, "cím a fabric.mod.json-ból");
            AssertEqual("1.0.0", sodium.Version, "verzió");
            var old = list.First(m => m.FileName == "old.jar.disabled");
            Assert(!old.Enabled, "a .disabled tiltott");
            Assert(old.Title == "old.jar", "cím tiltottnál: " + old.Title);
        });

        Check("Mods: ki/bekapcsolás átnevezéssel", () =>
        {
            var root = NewDirectory("mods-toggle");
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(mods, "a.jar"), "PK");

            manager.SetEnabled(mods, "a.jar", false);
            Assert(!File.Exists(Path.Combine(mods, "a.jar")), "az eredeti eltűnt");
            Assert(File.Exists(Path.Combine(mods, "a.jar.disabled")), "a tiltott megvan");
            AssertEqual(0, manager.ListMods(mods).Count(m => m.Enabled), "nincs engedélyezett");

            manager.SetEnabled(mods, "a.jar.disabled", true);
            Assert(File.Exists(Path.Combine(mods, "a.jar")), "visszakapcsolva");
            AssertEqual(1, manager.ListMods(mods).Count(m => m.Enabled), "egy engedélyezett");

            // Már a kívánt állapotban: nem dob, nem nyúl hozzá.
            manager.SetEnabled(mods, "a.jar", true);

            AssertThrows<FileNotFoundException>(
                () => manager.SetEnabled(mods, "nincs.jar", false), "hiányzó mod");
            AssertThrows<ArgumentException>(
                () => manager.SetEnabled(mods, "../kint.txt", false), "traversal");
        });

        Check("Mods: hozzáadás és törlés", () =>
        {
            var root = NewDirectory("mods-add");
            var mods = Path.Combine(root, "mods");
            var src = Path.Combine(root, "src.jar");
            File.WriteAllText(src, "PK");
            var txt = Path.Combine(root, "src.txt");
            File.WriteAllText(txt, "x");

            var installed = manager.AddMod(mods, src);
            AssertEqual("src.jar", installed, "fájlnév");
            Assert(File.Exists(Path.Combine(mods, "src.jar")), "bemásolva");

            // Ütközés: új névvel teszi be.
            var installed2 = manager.AddMod(mods, src);
            Assert(installed2 != installed, "ütközéskor új név: " + installed2);

            AssertThrows<ArgumentException>(() => manager.AddMod(mods, txt), "nem jar elutasítva");
            AssertThrows<FileNotFoundException>(
                () => manager.AddMod(mods, Path.Combine(root, "nincs.jar")), "hiányzó forrás");

            manager.RemoveMod(mods, installed);
            Assert(!File.Exists(Path.Combine(mods, installed)), "törölve");
            AssertThrows<FileNotFoundException>(() => manager.RemoveMod(mods, installed), "újra törlés");
        });

        Check("Resource pack: lista pack.mcmeta olvasással", () =>
        {
            var root = NewDirectory("packs-list");
            var game = Path.Combine(root, "game");
            var packs = Path.Combine(game, "resourcepacks");
            Directory.CreateDirectory(packs);

            using (var zip = ZipFile.Open(Path.Combine(packs, "szep.zip"), ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("pack.mcmeta");
                using var w = new StreamWriter(e.Open());
                w.Write("{\"pack\":{\"pack_format\":15,\"description\":\"Szép pack\"}}");
            }
            File.WriteAllText(Path.Combine(packs, "ures.zip"), "PK");

            var list = manager.ListResourcePacks(game);
            AssertEqual(2, list.Count, "két pack");
            var szep = list.First(p => p.FileName == "szep.zip");
            AssertEqual("Szép pack", szep.Description, "leírás");
            AssertEqual(15, szep.PackFormat, "pack_format");
            Assert(!szep.Active, "options.txt nélkül inaktív");
        });

        Check("Resource pack: ki/bekapcsolás options.txt-ben, más sorok megmaradnak", () =>
        {
            var root = NewDirectory("packs-toggle");
            var game = Path.Combine(root, "game");
            var packs = Path.Combine(game, "resourcepacks");
            Directory.CreateDirectory(packs);
            File.WriteAllText(Path.Combine(packs, "a.zip"), "PK");
            File.WriteAllText(Path.Combine(packs, "b.zip"), "PK");
            File.WriteAllText(Path.Combine(game, "options.txt"),
                "version:1234\nresourcePacks:[\"file/a.zip\"]\ngraphics:1\n");

            manager.SetResourcePackActive(game, "b.zip", true);
            var active = manager.GetActivePackNames(game);
            Assert(active.Contains("file/a.zip") && active.Contains("file/b.zip"), "mindkettő aktív");

            var text = File.ReadAllText(Path.Combine(game, "options.txt"));
            Assert(text.Contains("version:1234"), "másik sor megmaradt");
            Assert(text.Contains("graphics:1"), "másik sor megmaradt");

            var list = manager.ListResourcePacks(game);
            Assert(list.First(p => p.FileName == "b.zip").Active, "b aktív jelzés");

            manager.SetResourcePackActive(game, "a.zip", false);
            Assert(!manager.GetActivePackNames(game).Contains("file/a.zip"), "a kikapcsolva");
            Assert(manager.GetActivePackNames(game).Contains("file/b.zip"), "b maradt");

            // Törlés az aktív listából is kiveszi.
            manager.RemoveResourcePack(game, "b.zip");
            Assert(!File.Exists(Path.Combine(packs, "b.zip")), "fájl törölve");
            Assert(!manager.GetActivePackNames(game).Contains("file/b.zip"), "aktívból is kikerült");
        });

        Check("Szerverlista: alapértelmezett útvonal az adatkönyvtárban", () =>
        {
            var store = new TurulMC.Core.Servers.ServerListStorage();
            var expected = Path.Combine(TurulMC.Core.Storage.LauncherPaths.DataRoot, "servers.json");
            AssertEqual(expected, store.FilePath, "XDG/adatkönyvtár útvonal");
        });

        CheckAsync("Szerverlista: hozzáadás, duplikáció, törlés", async () =>
        {
            var root = NewDirectory("servers");
            var store = new TurulMC.Core.Servers.ServerListStorage(Path.Combine(root, "servers.json"));

            var added = await store.AddAsync("Teszt Szerver", "mc.example.hu", 25565);
            AssertEqual("Teszt Szerver", added.Name, "név");
            AssertEqual(25565, added.Port, "port");

            var list = await store.LoadAsync();
            AssertEqual(1, list.Count, "egy szerver");

            var dup = false;
            try { await store.AddAsync("Teszt Szerver", "mas.hu", 25566); }
            catch (ArgumentException) { dup = true; }
            Assert(dup, "dupla név elutasítva");

            var bad = false;
            try { await store.AddAsync("Rossz", "nem host!", 25565); }
            catch (ArgumentException) { bad = true; }
            Assert(bad, "érvénytelen host elutasítva");

            Assert(await store.RemoveAsync(added.Id), "törlés id alapján");
            AssertEqual(0, (await store.LoadAsync()).Count, "üres utána");
            Assert(!await store.RemoveAsync("nincs"), "hiányzó törlése false");
        });
    }

    // ---------------------------------------------------------------- Modrinth

    /// <summary>
    /// A Modrinth-réteg (kliens + telepítő + ikon-gyorsítótár) lefedése.
    /// Minden hálózati hívás stub handleren át megy — hálózat nélkül fut.
    /// </summary>
    private static void ModrinthTests()
    {
        Check("Modrinth: keresés-parszolás (találat, ikon, letöltésszám)", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("/search", """
                {"hits":[
                  {"project_id":"sodium","title":"Sodium","description":"Fast renderer","author":"jellysquid3",
                   "icon_url":"https://cdn.modrinth.com/data/sodium/icon.png","project_type":"mod","downloads":1000,
                   "loaders":["fabric"],"game_versions":["1.21.1"]},
                  {"project_id":"iris","title":"Iris","description":"Shaders","author":"coderbot",
                   "icon_url":null,"project_type":"mod","downloads":500,
                   "loaders":["fabric"],"game_versions":["1.21.1"]}
                ],"total_hits":2}
                """);
            var client = new TurulMC.Core.Mods.ModrinthClient(
                new HttpClient(routes) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) });

            var result = client.SearchAsync("sodium",
                new[] { "fabric" }, new[] { "1.21.1" }).GetAwaiter().GetResult();

            AssertEqual(2, result.Hits.Count, "találatok");
            AssertEqual(2, result.TotalHits, "összes");
            var sodium = result.Hits[0];
            AssertEqual("Sodium", sodium.Title, "cím");
            AssertEqual("jellysquid3", sodium.Author, "szerző");
            AssertEqual("https://cdn.modrinth.com/data/sodium/icon.png", sodium.IconUrl, "ikon URL");
            AssertEqual(1000, sodium.Downloads, "letöltésszám");
            Assert(result.Hits[1].IconUrl is null, "hiányzó ikon null");
        });

        Check("Modrinth: verzióválasztás release-preferenciával", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("project/sodium/version", """
                [{"id":"ver-beta","project_id":"sodium","version_number":"0.9.3-beta","version_type":"beta",
                  "loaders":["fabric"],"game_versions":["1.21.1"],"files":[],"dependencies":[]},
                 {"id":"ver-rel","project_id":"sodium","version_number":"0.9.3","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],"files":[],"dependencies":[]}]
                """);
            var client = new TurulMC.Core.Mods.ModrinthClient(
                new HttpClient(routes) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) });

            var versions = client.GetVersionsAsync("sodium",
                new[] { "fabric" }, new[] { "1.21.1" }).GetAwaiter().GetResult();
            AssertEqual(2, versions.Count, "verziók");
            var best = TurulMC.Core.Mods.ModrinthInstaller.PickBest(versions);
            Assert(best is not null && best.VersionId == "ver-rel", "release nyer a beta ellen");
        });

        Check("Modrinth: projekt telepítése függőséggel + meta", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("project/sodium/version", """
                [{"id":"ver-rel","project_id":"sodium","version_number":"0.9.3","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"sodium-1.0.jar","url":"https://cdn.modrinth.com/data/sodium/sodium-1.0.jar",
                            "primary":true,"size":20,"hashes":{}}],
                  "dependencies":[{"project_id":"fabric-api","version_id":null,"dependency_type":"required"},
                                  {"project_id":"optifine","version_id":null,"dependency_type":"optional"}]}]
                """);
            routes.Map("project/fabric-api/version", """
                [{"id":"fapi-v1","project_id":"fabric-api","version_number":"0.100.0","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"fabric-api.jar","url":"https://cdn.modrinth.com/data/fapi/fapi.jar",
                            "primary":true,"size":20,"hashes":{}}],
                  "dependencies":[]}]
                """);
            routes.MapBytes("cdn.modrinth.com/data/sodium/sodium-1.0.jar",
                "PK-sodium-fake-jar!!!"u8.ToArray(), "application/java-archive");
            routes.MapBytes("cdn.modrinth.com/data/fapi/fapi.jar",
                "PK-fapi-fake-jar!!!!!"u8.ToArray(), "application/java-archive");

            var root = NewDirectory("mr-install");
            var mods = Path.Combine(root, "mods");
            var installer = CreateInstaller(routes);

            var installed = installer.InstallProjectAsync(
                "sodium", "1.21.1", "fabric", mods).GetAwaiter().GetResult();

            AssertEqual(2, installed.Count, "fő mod + 1 required függőség (az optional kimarad)");
            // A függőségek települnek előbb (mélységi bejárás), a fő mod utoljára.
            Assert(installed[^1].FileName == "sodium-1.0.jar" && !installed[^1].WasDependency,
                "utolsó a fő mod");
            Assert(installed.Any(i => i.FileName == "fabric-api.jar" && i.WasDependency), "függőség jelölve");
            Assert(File.Exists(Path.Combine(mods, "sodium-1.0.jar")), "fájl a helyén");
            Assert(File.Exists(Path.Combine(mods, "fabric-api.jar")), "függőség fájl a helyén");

            // Meta: a lista ebből tudja az ikont/frissítést.
            var meta = TurulMC.Core.Mods.ModrinthInstaller.ReadMeta(mods, "sodium-1.0.jar");
            Assert(meta is not null && meta.ProjectId == "sodium", "meta projectId");
            Assert(meta!.VersionNumber == "0.9.3", "meta verziószám");
            var map = TurulMC.Core.Mods.ModrinthInstaller.BuildProjectMap(mods);
            Assert(map.TryGetValue("sodium", out var fn) && fn == "sodium-1.0.jar", "projekt-térkép");
        });

        Check("Modrinth: körkörös függőség nem akad el", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("project/mod-a/version", """
                [{"id":"a-v1","project_id":"mod-a","version_number":"1.0","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"a.jar","url":"https://cdn.modrinth.com/data/a/a.jar",
                            "primary":true,"size":10,"hashes":{}}],
                  "dependencies":[{"project_id":"mod-b","version_id":null,"dependency_type":"required"}]}]
                """);
            routes.Map("project/mod-b/version", """
                [{"id":"b-v1","project_id":"mod-b","version_number":"1.0","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"b.jar","url":"https://cdn.modrinth.com/data/b/b.jar",
                            "primary":true,"size":10,"hashes":{}}],
                  "dependencies":[{"project_id":"mod-a","version_id":null,"dependency_type":"required"}]}]
                """);
            routes.MapBytes("cdn.modrinth.com/data/a/a.jar", "PK-a"u8.ToArray(), "application/java-archive");
            routes.MapBytes("cdn.modrinth.com/data/b/b.jar", "PK-b"u8.ToArray(), "application/java-archive");

            var root = NewDirectory("mr-cycle");
            var installer = CreateInstaller(routes);
            var installed = installer.InstallProjectAsync(
                "mod-a", "1.21.1", "fabric", Path.Combine(root, "mods")).GetAwaiter().GetResult();

            AssertEqual(2, installed.Count, "mindkettő egyszer (nincs végtelen rekurzió)");
        });

        Check("Modrinth: inkompatibilis verzió és rossz URL elutasítva", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("project/oldmod/version", """
                [{"id":"old-v1","project_id":"oldmod","version_number":"1.0","version_type":"release",
                  "loaders":["forge"],"game_versions":["1.20.1"],
                  "files":[{"filename":"old.jar","url":"https://cdn.modrinth.com/data/old/old.jar",
                            "primary":true,"size":10,"hashes":{}}],
                  "dependencies":[]}]
                """);
            routes.Map("project/evil/version", """
                [{"id":"evil-v1","project_id":"evil","version_number":"1.0","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"evil.jar","url":"https://evil.example.com/evil.jar",
                            "primary":true,"size":10,"hashes":{}}],
                  "dependencies":[]}]
                """);
            routes.Map("project/broken/version", """
                [{"id":"broken-v1","project_id":"broken","version_number":"1.0","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"broken.jar","url":"https://cdn.modrinth.com/data/broken/broken.jar",
                            "primary":true,"size":10,
                            "hashes":{"sha512":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}}],
                  "dependencies":[]}]
                """);
            routes.MapBytes("cdn.modrinth.com/data/broken/broken.jar",
                "PK-broken-content"u8.ToArray(), "application/java-archive");

            var root = NewDirectory("mr-reject");
            var installer = CreateInstaller(routes);

            // Nincs kompatibilis verzió fabric/1.21.1-re (csak forge/1.20.1) → hiba.
            AssertThrows<InvalidOperationException>(() => installer.InstallProjectAsync(
                "oldmod", "1.21.1", "fabric", Path.Combine(root, "m1")).GetAwaiter().GetResult(),
                "inkompatibilis verzió");

            // Nem-modrinth hoszt → hiba.
            AssertThrows<InvalidOperationException>(() => installer.InstallProjectAsync(
                "evil", "1.21.1", "fabric", Path.Combine(root, "m2")).GetAwaiter().GetResult(),
                "idegen letöltési hoszt");

            // SHA-512 nem egyezik → hiba, és nem marad fájl.
            AssertThrows<InvalidOperationException>(() => installer.InstallProjectAsync(
                "broken", "1.21.1", "fabric", Path.Combine(root, "m3")).GetAwaiter().GetResult(),
                "hash-eltérés");
            Assert(!File.Exists(Path.Combine(root, "m3", "broken.jar")), "sérült fájl nem marad");

            // Loader nélkül telepítés eleve tiltott.
            AssertThrows<InvalidOperationException>(() => installer.InstallProjectAsync(
                "oldmod", "1.20.1", "none", Path.Combine(root, "m4")).GetAwaiter().GetResult(),
                "loader nélküli telepítés");
        });

        Check("Modrinth: ikon-gyorsítótár (kép jó, szöveg és idegen hoszt nem)", async () =>
        {
            var routes = new RoutingHandler();
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            routes.MapBytes("cdn.modrinth.com/data/sodium/icon.png", png, "image/png");
            routes.MapBytes("cdn.modrinth.com/data/fake/icon.txt",
                "ez nem kep"u8.ToArray(), "text/plain");
            routes.MapBytes("cdn.modrinth.com/i.png", png, "image/png");

            var root = NewDirectory("mr-icons");
            var cache = new TurulMC.Core.Mods.ModIconCache(
                Path.Combine(root, "icons"), new HttpClient(routes));

            var path = await cache.GetIconPathAsync(
                "sodium", "https://cdn.modrinth.com/data/sodium/icon.png");
            Assert(path is not null && File.Exists(path), "ikon letöltve és cache-elve");

            // Másodjára már a cache-ből jön (rossz URL-lel is megtalálja).
            var path2 = await cache.GetIconPathAsync("sodium", "https://cdn.modrinth.com/rossz.png");
            AssertEqual(path, path2, "cache-találat");

            Assert(await cache.GetIconPathAsync(
                "fake", "https://cdn.modrinth.com/data/fake/icon.txt") is null, "nem-kép elutasítva");
            Assert(await cache.GetIconPathAsync(
                "evil", "https://evil.example.com/icon.png") is null, "idegen hoszt elutasítva");
            Assert(await cache.GetIconPathAsync("x", null) is null, "hiányzó URL null");
            // A "../kint" projectId-ből "kint" lesz — a fájl a cache-en belülre kerül.
            var traversal = await cache.GetIconPathAsync("../kint", "https://cdn.modrinth.com/i.png");
            Assert(traversal is not null && traversal.Contains("..") == false, "projectId tisztítva");
            Assert(Path.GetFileName(traversal!) == "kint.png", "tisztított fájlnév: " + traversal);
            Assert(Path.GetDirectoryName(traversal!) == Path.Combine(root, "icons"), "cache-en belül");
        });
    }

    /// <summary>URL-részlet alapján elágazó stub HTTP-kezelő a Modrinth-tesztekhez.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(string Key, Func<HttpRequestMessage, HttpResponseMessage> Fn)> _routes = new();

        public void Map(string urlContains, string content,
            string mediaType = "application/json",
            System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
            => _routes.Add((urlContains, _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            }));

        public void MapBytes(string urlContains, byte[] content, string mediaType)
            => _routes.Add((urlContains, _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
                }
            }));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            foreach (var (key, fn) in _routes)
            {
                if (url.Contains(key, StringComparison.Ordinal))
                {
                    var response = fn(request);
                    response.RequestMessage = request;
                    return Task.FromResult(response);
                }
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":\"no stub route\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static TurulMC.Core.Mods.ModrinthInstaller CreateInstaller(RoutingHandler routes)
        => new(
            new TurulMC.Core.Mods.ModrinthClient(
                new HttpClient(routes) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) }),
            new HttpClient(routes));

    // ---------------------------------------------------------------- Java + modpack

    /// <summary>
    /// A Java-szolgáltatás és a modpack-telepítő lefedése.
    /// A modpack-tesztek szintetikus .mrpack fájllal és stub letöltéssel
    /// futnak — hálózat nélkül.
    /// </summary>
    private static void ModpackTests()
    {
        Check("Java: alapértelmezett adatkönyvtár az XDG-helyen", () =>
        {
            var svc = new TurulMC.Core.Java.JavaRuntimeService(new TurulMC.Core.Java.JavaRuntimeProvisioner());
            Assert(svc.RuntimeRoot is not null, "provisioner gyökér létezik");
            // A szolgáltatás a LauncherPaths adatkönyvtárát használja —
            // közvetlen mező híján a settings/cache útvonalon át ellenőrizzük:
            // egy ideiglenes gyökérrel induló példány oda ír.
            var root = NewDirectory("java-root");
            var svc2 = new TurulMC.Core.Java.JavaRuntimeService(null, root);
            Assert(Directory.Exists(root), "a megadott gyökér létrejön");
            _ = svc2;
        });

        Check("Modpack: .mrpack telepítés új Instance-ba (fájl + override + meta)", () =>
        {
            var routes = new RoutingHandler();
            var modBytes = "PK-mod-tartalom-12345"u8.ToArray();
            using var sha = System.Security.Cryptography.SHA512.Create();
            var modHash = Convert.ToHexString(sha.ComputeHash(modBytes)).ToLowerInvariant();
            routes.MapBytes("cdn.modrinth.com/data/pack/mod-a.jar", modBytes, "application/java-archive");

            var root = NewDirectory("mrpack-install");
            var mrpack = Path.Combine(root, "test.mrpack");
            CreateMrpack(mrpack,
                name: "Teszt Pack",
                minecraft: "1.21.1",
                loader: "fabric",
                loaderVersion: "0.16.14",
                """
                    [{"path":"mods/mod-a.jar",
                      "hashes":{"sha512":"HASH"},
                      "env":{"client":"required","server":"required"},
                      "downloads":["https://cdn.modrinth.com/data/pack/mod-a.jar"]},
                     {"path":"mods/server-only.jar",
                      "hashes":{},
                      "env":{"client":"unsupported","server":"required"},
                      "downloads":["https://cdn.modrinth.com/data/pack/server-only.jar"]}]
                    """.Replace("HASH", modHash),
                overrides: new Dictionary<string, string>
                {
                    ["config/teszt.txt"] = "hello"
                });

            var store = new TurulMC.Core.Storage.InstanceStore(
                Path.Combine(root, "instances.json"),
                Path.Combine(root, "instances"));
            var list = new List<TurulMC.Core.Models.LauncherInstance>();
            var installer = new TurulMC.Core.Modpacks.ModpackInstaller(
                new TurulMC.Core.Mods.ModrinthClient(
                    new HttpClient(routes) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) }),
                new HttpClient(routes));

            var result = installer.InstallMrpackAsync(
                mrpack, "pack-projekt", "pack-verzio", store, list)
                .GetAwaiter().GetResult();

            AssertEqual(1, list.Count, "egy új instance a listában");
            AssertEqual("fabric", result.Instance.Loader, "loader");
            AssertEqual("0.16.14", result.Instance.LoaderVersion, "loader verzió");
            AssertEqual("1.21.1", result.Instance.MinecraftVersion, "MC verzió");
            AssertEqual("pack-projekt", result.Instance.ModpackProjectId, "pack projekt");
            AssertEqual(1, result.FileCount, "csak a kliens-fájl (server-only kimarad)");

            Assert(File.Exists(Path.Combine(result.InstanceDir, "mods", "mod-a.jar")), "mod letöltve");
            Assert(!File.Exists(Path.Combine(result.InstanceDir, "mods", "server-only.jar")), "server-only kihagyva");
            AssertEqual("hello",
                File.ReadAllText(Path.Combine(result.InstanceDir, "config", "teszt.txt")), "override kicsomagolva");
            Assert(!File.Exists(Path.Combine(root, "kint.txt")), "zip-slip blokkolva");
            Assert(!File.Exists(Path.Combine(result.InstanceDir, "kint.txt")), "zip-slip blokkolva (2)");
            Assert(File.Exists(Path.Combine(result.InstanceDir, ".turul-modpack.json")), "követőfájl");
            Assert(File.Exists(Path.Combine(root, "instances.json")), "lista mentve");
        });

        Check("Modpack: traversal bejegyzés az egész telepítést meghiúsítja", () =>
        {
            var root = NewDirectory("mrpack-slip");
            var evil = Path.Combine(root, "evil.mrpack");
            CreateMrpack(evil, "Evil", "1.21.1", "fabric", "0.16.14", "[]",
                new Dictionary<string, string>
                {
                    ["../kint.txt"] = "gonosz",
                    ["config/ok.txt"] = "ok"
                });

            var store = new TurulMC.Core.Storage.InstanceStore(
                Path.Combine(root, "instances.json"),
                Path.Combine(root, "instances"));
            var list = new List<TurulMC.Core.Models.LauncherInstance>();
            var installer = new TurulMC.Core.Modpacks.ModpackInstaller();

            AssertThrows<InvalidOperationException>(() => installer.InstallMrpackAsync(
                evil, "p", "v", store, list).GetAwaiter().GetResult(),
                "traversal az egész telepítést dobja");
            AssertEqual(0, list.Count, "lista változatlan");
            Assert(!File.Exists(Path.Combine(root, "kint.txt")), "nincs kiírás a mappán kívülre");
            var instancesRoot = Path.Combine(root, "instances");
            Assert(!Directory.Exists(instancesRoot)
                || Directory.GetDirectories(instancesRoot).Length == 0, "félig kész mappa törölve");
        });

        Check("Modpack: elutasítások (forge, rossz formátum, hiányzó index, hash-hiba)", () =>
        {
            var root = NewDirectory("mrpack-reject");
            var store = new TurulMC.Core.Storage.InstanceStore(
                Path.Combine(root, "instances.json"),
                Path.Combine(root, "instances"));
            var installer = new TurulMC.Core.Modpacks.ModpackInstaller(
                new TurulMC.Core.Mods.ModrinthClient(
                    new HttpClient(new RoutingHandler()) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) }),
                new HttpClient(new RoutingHandler()));

            // Forge loader.
            var forge = Path.Combine(root, "forge.mrpack");
            CreateMrpack(forge, "Forge Pack", "1.20.1", "forge", "47.1.0", "[]",
                new Dictionary<string, string>());
            var list1 = new List<TurulMC.Core.Models.LauncherInstance>();
            AssertThrows<InvalidOperationException>(() => installer.InstallMrpackAsync(
                forge, "p", "v", store, list1).GetAwaiter().GetResult(), "forge elutasítva");
            AssertEqual(0, list1.Count, "lista változatlan");
            AssertEqual(0, Directory.Exists(Path.Combine(root, "instances"))
                ? Directory.GetDirectories(Path.Combine(root, "instances")).Length : 0,
                "félig kész mappa törölve");

            // Hiányzó index.
            var noindex = Path.Combine(root, "noindex.zip");
            using (var zip = ZipFile.Open(noindex, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("overrides/x.txt");
                using var w = new StreamWriter(e.Open());
                w.Write("x");
            }
            AssertThrows<InvalidOperationException>(() => installer.InstallMrpackAsync(
                noindex, "p", "v", store, new List<TurulMC.Core.Models.LauncherInstance>())
                .GetAwaiter().GetResult(), "index nélkül elutasítva");

            // Helyi/privát letöltési URL.
            var routes = new RoutingHandler();
            routes.MapBytes("cdn.modrinth.com/data/pack/ok.jar", "PK"u8.ToArray(), "application/java-archive");
            var installer2 = new TurulMC.Core.Modpacks.ModpackInstaller(
                new TurulMC.Core.Mods.ModrinthClient(
                    new HttpClient(routes) { BaseAddress = new Uri(TurulMC.Core.Mods.ModrinthClient.ApiBase) }),
                new HttpClient(routes));
            var evil = Path.Combine(root, "evil.mrpack");
            CreateMrpack(evil, "Evil", "1.21.1", "fabric", "0.16.14",
                """[{"path":"mods/evil.jar","hashes":{},"downloads":["http://192.168.1.1/evil.jar"]}]""",
                new Dictionary<string, string>());
            AssertThrows<InvalidOperationException>(() => installer2.InstallMrpackAsync(
                evil, "p", "v", store, new List<TurulMC.Core.Models.LauncherInstance>())
                .GetAwaiter().GetResult(), "privát IP elutasítva");
        });

        Check("Modpack: export-manifest építés (technikai fájlok kihagyva)", () =>
        {
            var root = NewDirectory("mrpack-export");
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "mods"));
            Directory.CreateDirectory(Path.Combine(game, "logs"));
            Directory.CreateDirectory(Path.Combine(game, "versions", "1.21.1"));
            File.WriteAllText(Path.Combine(game, "mods", "a.jar"), "PK-a");
            File.WriteAllText(Path.Combine(game, "logs", "latest.log"), "log");
            File.WriteAllText(Path.Combine(game, "versions", "1.21.1", "client.jar"), "MC");
            File.WriteAllText(Path.Combine(game, "options.txt"), "beállítás");

            var manifest = TurulMC.Core.Modpacks.ModpackInstaller.BuildExportManifestAsync(
                game, "teszt-pack", "1.0.0", "1.21.1", "fabric", "0.16.14")
                .GetAwaiter().GetResult();

            AssertEqual(1, manifest.Files.Count, "csak a mod (log/verzió/options kimarad)");
            AssertEqual("mods/a.jar", manifest.Files[0].Path, "relatív útvonal");
            Assert(manifest.Files[0].Sha256.Length == 64, "SHA-256 hash");
            AssertEqual("1.21.1", manifest.MinecraftVersion, "MC verzió");
        });
    }

    /// <summary>Szintetikus .mrpack készítése a teszthez.</summary>
    private static void CreateMrpack(
        string path,
        string name,
        string minecraft,
        string loader,
        string loaderVersion,
        string filesJson,
        Dictionary<string, string> overrides)
    {
        var loaderKey = loader == "fabric" ? "fabric-loader" : loader;
        var index = $$"""
            {"formatVersion":1,"game":"minecraft","name":"{{name}}",
             "dependencies":{"minecraft":"{{minecraft}}","{{loaderKey}}":"{{loaderVersion}}"},
             "files":{{filesJson}}}
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var indexEntry = zip.CreateEntry("modrinth.index.json");
        using (var w = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
            w.Write(index);
        foreach (var (rel, content) in overrides)
        {
            var e = zip.CreateEntry("overrides/" + rel);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    // ---------------------------------------------------------------- Frissítés

    /// <summary>
    /// Az UpdateService (manifest, verzió-összehasonlítás, biztonság) és a
    /// tématábla lefedése. Minden hálózat stub handleren át megy.
    /// </summary>
    private static void UpdateTests()
    {
        const string manifestJson = """
            {"version":"4.7.0","required":false,
             "url":"https://turulnetwork.hu/launcher/releases/TurulLauncher-4.7.0.zip",
             "sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
             "publishedAt":"2026-10-07T10:00:00+02:00",
             "changelog":["Új téma-választék","Javított indítás"]}
            """;

        Check("Frissítés: manifest-parszolás és verzió-összehasonlítás", () =>
        {
            AssertEqual("beta", TurulMC.Core.Update.UpdateService.NormalizeChannel("BETA"), "csatorna");
            AssertEqual("stable", TurulMC.Core.Update.UpdateService.NormalizeChannel(""), "üres csatorna");
            AssertEqual("stable", TurulMC.Core.Update.UpdateService.NormalizeChannel("xyz"), "ismeretlen csatorna");
            Assert(TurulMC.Core.Update.UpdateService.ManifestUrlFor("beta").EndsWith("beta.json"), "beta URL");
            Assert(TurulMC.Core.Update.UpdateService.ManifestUrlFor("stable").EndsWith("stable.json"), "stable URL");

            var cmp = TurulMC.Core.Update.UpdateService.CompareVersions;
            Assert(cmp("4.7.0", "4.6.0") > 0, "újabb nagyobb");
            Assert(cmp("4.6.0", "4.6.0") == 0, "egyenlő");
            Assert(cmp("4.5.0", "4.6.0") < 0, "régebbi kisebb");
            Assert(cmp("v4.7.0", "4.6.0") > 0, "v-előtag tűrése");
            Assert(cmp("4.7.0-linux", "4.6.0") > 0, "utótag tűrése");
            Assert(cmp("4.10.0", "4.9.0") > 0, "numerikus, nem lexikális");
            Assert(cmp("", "4.6.0") < 0, "üres kisebb");
        });

        Check("Frissítés: ellenőrzés (elérhető / naprakész / szerverhiba)", async () =>
        {
            var routes = new RoutingHandler();
            routes.Map("linux/update/stable.json", manifestJson);
            var svc = new TurulMC.Core.Update.UpdateService(new HttpClient(routes));

            // A Linux-fork a saját manifestjét olvassa (nem a Windowsét).
            var newer = await svc.CheckAsync("1.0.0", "stable");
            Assert(newer.Available, "1.0.0-hoz elérhető a stub 4.7.0-s Linux-manifest");
            AssertEqual("4.7.0", newer.Manifest!.Version, "verzió");
            AssertEqual(2, newer.Manifest.Changelog.Count, "changelog");

            var same = await svc.CheckAsync("4.7.0", "stable");
            Assert(!same.Available, "azonos verzióra nincs frissítés");

            var svc404 = new TurulMC.Core.Update.UpdateService(new HttpClient(new RoutingHandler()));
            var failed = await svc404.CheckAsync("1.0.0", "stable");
            Assert(!failed.Available && failed.Error is not null, "szerverhiba jelzése");
        });

        Check("Frissítés: URL-engedélyezés és hash-ellenőrzés", () =>
        {
            TurulMC.Core.Update.UpdateService.EnsureSafeUpdateUrl(
                "https://turulnetwork.hu/launcher/releases/x.zip"); // nem dob
            AssertThrows<InvalidOperationException>(() => TurulMC.Core.Update.UpdateService.EnsureSafeUpdateUrl(
                "https://evil.example.com/x.zip"), "idegen hoszt");
            AssertThrows<InvalidOperationException>(() => TurulMC.Core.Update.UpdateService.EnsureSafeUpdateUrl(
                "http://turulnetwork.hu/x.zip"), "nem HTTPS");

            var root = NewDirectory("update-hash");
            var file = Path.Combine(root, "a.bin");
            File.WriteAllBytes(file, "tartalom"u8.ToArray());
            using var sha = System.Security.Cryptography.SHA256.Create();
            var good = Convert.ToHexString(sha.ComputeHash("tartalom"u8.ToArray()));
            TurulMC.Core.Update.UpdateService.VerifySha256(file, good); // nem dob
            TurulMC.Core.Update.UpdateService.VerifySha256(file, good.ToLowerInvariant()); // kisbetű is jó
            AssertThrows<InvalidOperationException>(() => TurulMC.Core.Update.UpdateService.VerifySha256(
                file, new string('0', 64)), "rossz hash");
        });

        Check("Frissítés: AppImage önfrissítés (letöltés + SHA + csere)", () =>
        {
            var routes = new RoutingHandler();
            var newBytes = "uj-appimage-tartalom-1234567890"u8.ToArray();
            routes.MapBytes("turulnetwork.hu/launcher/linux/releases/fake.AppImage",
                newBytes, "application/octet-stream");

            var root = NewDirectory("appimage-update");
            var current = Path.Combine(root, "TurulLauncher.AppImage");
            File.WriteAllBytes(current, "regi-appimage"u8.ToArray());

            using var sha = System.Security.Cryptography.SHA256.Create();
            var good = Convert.ToHexString(sha.ComputeHash(newBytes));
            var manifest = new TurulMC.Core.Update.LauncherUpdateManifest(
                "9.9.9", false,
                "https://turulnetwork.hu/launcher/linux/releases/fake.AppImage",
                good, "2026-10-09", new List<string>());

            var svc = new TurulMC.Core.Update.UpdateService(new HttpClient(routes));
            svc.InstallAppImageAsync(manifest, current).GetAwaiter().GetResult();

            AssertEqual("uj-appimage-tartalom-1234567890",
                File.ReadAllText(current), "fájl kicserélődött");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(current);
                Assert(mode.HasFlag(UnixFileMode.UserExecute), "futtatható bit");
            }

            // Elutasítások.
            var badExt = manifest with
            {
                Url = "https://turulnetwork.hu/launcher/linux/releases/fake.zip"
            };
            AssertThrows<InvalidOperationException>(() => svc.InstallAppImageAsync(
                badExt, current).GetAwaiter().GetResult(), "nem AppImage URL");
            var noSha = manifest with { Sha256 = "" };
            AssertThrows<InvalidOperationException>(() => svc.InstallAppImageAsync(
                noSha, current).GetAwaiter().GetResult(), "SHA nélkül nem telepít");
            AssertThrows<InvalidOperationException>(() => svc.InstallAppImageAsync(
                manifest, Path.Combine(root, "nincs.AppImage")).GetAwaiter().GetResult(),
                "hiányzó cél");
        });

        Check("Beállítások: új megjelenés-mezők alapértelmezései", () =>
        {
            var s = new TurulMC.Core.Models.LauncherSettings();
            AssertEqual("", s.BackgroundImage, "háttér üres");
            AssertEqual(1.0, s.WindowOpacity, "átlátszatlanság 1");
            AssertEqual("", s.CustomAccent, "egyedi akcentus üres");
            AssertEqual(100, s.UiScalePercent, "UI-skála 100% (betűméret-alap)");
            Assert(s.AnimationsEnabled, "animációk bekapcsolva");
        });

        Check("Frissítés: kicsomagolás zip-slip védelemmel + portable-jelölő", () =>
        {
            var root = NewDirectory("update-extract");
            var zip = Path.Combine(root, "u.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var ok = archive.CreateEntry("TurulLauncher.dll");
                using (var w = new StreamWriter(ok.Open())) w.Write("bin");
                var evil = archive.CreateEntry("../../kint.txt");
                using (var w2 = new StreamWriter(evil.Open())) w2.Write("gonosz");
            }
            var target = Path.Combine(root, "install");
            Directory.CreateDirectory(target);
            // A traversal-bejegyzés kivételt dob (nem csendben átnevezi) —
            // a már kicsomagolt fájl megmarad, de semmi sem kerül ki.
            AssertThrows<InvalidOperationException>(() =>
                TurulMC.Core.Update.UpdateService.ExtractPackage(zip, target), "traversal dob");
            Assert(File.Exists(Path.Combine(target, "TurulLauncher.dll")), "rendes fájl kicsomagolva");
            Assert(!File.Exists(Path.Combine(root, "kint.txt")), "nincs kiírás kívülre");

            Assert(!TurulMC.Core.Update.UpdateService.IsPortable(target), "jelölő nélkül nem portable");
            File.WriteAllText(Path.Combine(target, "TurulMC.portable"), "");
            Assert(TurulMC.Core.Update.UpdateService.IsPortable(target), "jelölővel portable");
        });

        Check("Témák: 5 séma érvényes színekkel és létező logóval", () =>
        {
            var logosDir = Path.Combine(
                FindRepoRoot(), "src", "TurulMC.Launcher.Avalonia", "Assets", "Logos");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(
                Path.Combine(FindRepoRoot(), "src", "TurulMC.Launcher.Avalonia", "Services", "ThemeService.cs")))
            {
                var t = line.Trim();
                if (t.StartsWith("new(", StringComparison.Ordinal))
                    names.Add(t.Split('"')[1]);
            }
            AssertEqual(5, names.Count, "öt téma");
            foreach (var n in names)
            {
                Assert(File.Exists(Path.Combine(logosDir, $"turul-logo-{n}.png")),
                    $"logó létezik: {n}");
            }
        });
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TurulMC.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ---------------------------------------------------------------- Szövegtisztítás

    /// <summary>
    /// A DisplayText (HTML/markdown/chat-komponens/§-kód mentesítés) lefedése.
    /// Ezek a hibák okozták a „furán írja ki a szöveget + JSON-t is kiír" jelenséget.
    /// </summary>
    private static void DisplayTextTests()
    {
        var clean = TurulMC.Core.Text.DisplayText.CleanModrinth;

        Check("Szöveg: Modrinth HTML-entitás és markdown tisztítás", () =>
        {
            AssertEqual("Sodium's renderer", clean("Sodium&#39;s renderer"), "entitás");
            AssertEqual("Fast renderer", clean("**Fast** renderer"), "félkövér");
            AssertEqual("see wiki", clean("see [wiki](https://example.com/x)"), "link");
            AssertEqual("logo", clean("![logo](https://example.com/i.png)"), "kép");
            AssertEqual("code here", clean("`code` here"), "inline kód");
            AssertEqual("• item", clean("- item"), "lista");
            AssertEqual("Nincs leírás.", clean(""), "üres");
            AssertEqual("Nincs leírás.", clean(null), "null");
            Assert(!clean("a  b\n\n\nc").Contains("  "), "térköz-összevonás");
        });

        Check("Szöveg: chat-komponens lapítás (sose nyers JSON)", () =>
        {
            var flat = TurulMC.Core.Text.DisplayText.FlattenChatComponent;
            AssertEqual("Szép pack", flat(Json("{\"text\":\"Szép pack\"}")), "sima szöveg");
            AssertEqual("Hello világ",
                flat(Json("{\"text\":\"Hello \",\"extra\":[{\"text\":\"világ\"},\"!\"]}"))[..11],
                "extra-tömb");
            AssertEqual("", flat(Json("{\"translate\":\"pack.name\"}")), "translate nem szöveg");
            AssertEqual("", flat(Json("{\"unknown\":123}")), "ismeretlen alak üres");
            AssertEqual("5", flat(Json("5")), "szám");
            Assert(!flat(Json("{\"text\":\"x\",\"extra\":[{\"a\":1}]}")).Contains("{"), "nincs JSON-maradék");
        });

        Check("Szöveg: §-kód vágás és log-rövidítés", () =>
        {
            AssertEqual("Hello világ",
                TurulMC.Core.Text.DisplayText.StripSectionCodes("§aHello §lvilág§r"), "§-kódok");
            AssertEqual("simaszöveg",
                TurulMC.Core.Text.DisplayText.StripSectionCodes("simaszöveg"), "kód nélkül változatlan");
            AssertEqual("Indul a játék",
                TurulMC.Core.Minecraft.GameLoadingStatus.TrimForLog("§eIndul §ba §cjá§dték"), "log-nézet tiszta");
        });

        Check("Szöveg: pack.mcmeta objektum-leírás nem JSON-ként jelenik meg", () =>
        {
            var root = NewDirectory("pack-desc");
            var game = Path.Combine(root, "game");
            var packs = Path.Combine(game, "resourcepacks");
            Directory.CreateDirectory(packs);
            using (var zip = ZipFile.Open(Path.Combine(packs, "obj.zip"), ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("pack.mcmeta");
                using var w = new StreamWriter(e.Open());
                w.Write("{\"pack\":{\"pack_format\":15,\"description\":{\"text\":\"Szép \",\"extra\":[\"pack\"]}}}");
            }
            var manager = new TurulMC.Core.Mods.ModManager();
            var list = manager.ListResourcePacks(game);
            AssertEqual(1, list.Count, "egy pack");
            AssertEqual("Szép pack", list[0].Description, "lapított leírás");
            Assert(!list[0].Description.Contains("{"), "nincs JSON");
        });
    }

    private static System.Text.Json.JsonElement Json(string text)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------- Indítás-szinkron

    /// <summary>
    /// A Fabric-indítási bug regressziós tesztje: az instance loader-verziója
    /// (az eredeti vagy a frissen feloldott) MINDIG átkerül a settings-be,
    /// mert a build-konfiguráció onnan keresi a fabric JSON-t. Ha lemarad,
    /// minden Fabric-indítás „nincs telepítve" hibával hal el.
    /// </summary>
    private static void LaunchSyncTests()
    {
        Check("Indítás: Fabric loader-verzió átkerül a settings-be", () =>
        {
            var settings = new TurulMC.Core.Models.LauncherSettings();
            var instance = new TurulMC.Core.Models.LauncherInstance
            {
                MinecraftVersion = "1.21.1",
                Loader = "fabric",
                LoaderVersion = "",
                RamMb = 4096
            };

            // 1) Ismeretlen pin-nel indulunk (ez volt a hibás eset: "" maradt).
            TurulMC.Launcher.Avalonia.Services.LauncherServices.ApplyInstanceToSettings(
                settings, instance, instance.LoaderVersion);
            AssertEqual("fabric", settings.Loader, "loader");
            AssertEqual("", settings.LoaderVersion, "üres pin továbbadva");

            // 2) Feloldás után a MEGOLDOTT verzió íródik vissza —
            // ez a sor hiányzott, ettől halt el minden Fabric-indítás.
            TurulMC.Launcher.Avalonia.Services.LauncherServices.ApplyInstanceToSettings(
                settings, instance, "0.16.14");
            AssertEqual("0.16.14", settings.LoaderVersion, "feloldott verzió szinkronizálva");
            AssertEqual("1.21.1", settings.MinecraftVersion, "MC-verzió");
            AssertEqual(4096, settings.DefaultRamMb, "RAM");
        });

        Check("Indítás: vanilla úton a loader-verzió törlődik", () =>
        {
            var settings = new TurulMC.Core.Models.LauncherSettings { LoaderVersion = "0.16.14" };
            var instance = new TurulMC.Core.Models.LauncherInstance
            {
                MinecraftVersion = "1.21.1",
                Loader = "none",
                LoaderVersion = "0.16.14", // elavult instance-érték
                RamMb = 2048
            };

            TurulMC.Launcher.Avalonia.Services.LauncherServices.ApplyInstanceToSettings(
                settings, instance, instance.LoaderVersion);
            AssertEqual("", settings.LoaderVersion, "elavult verzió nem szivárog át");
            AssertEqual("none", settings.Loader, "loader");
        });

        Check("Indítás: nagybetűs loader normalizálva", () =>
        {
            var settings = new TurulMC.Core.Models.LauncherSettings();
            var instance = new TurulMC.Core.Models.LauncherInstance
            {
                MinecraftVersion = "1.21.1",
                Loader = "Fabric",
                RamMb = 0 // 0 RAM nem írja felül az alapot
            };
            settings.DefaultRamMb = 8192;

            TurulMC.Launcher.Avalonia.Services.LauncherServices.ApplyInstanceToSettings(
                settings, instance, "0.16.14");
            AssertEqual("fabric", settings.Loader, "kisbetűsítve");
            AssertEqual("0.16.14", settings.LoaderVersion, "verzió");
            AssertEqual(8192, settings.DefaultRamMb, "0 RAM nem ír felül");
        });
    }

    // ---------------------------------------------------------------- Shader + frissítés + másolás

    /// <summary>
    /// Shaderek, mod-frissítés és instance-másolás/backup lefedése.
    /// A hálózat stub handleren át megy.
    /// </summary>
    private static void ContentOpsTests()
    {
        var manager = new TurulMC.Core.Mods.ModManager();

        Check("Shaderek: lista, ki/bekapcsolás, hozzáadás, törlés", () =>
        {
            var root = NewDirectory("shaders");
            var game = Path.Combine(root, "game");
            var shaders = Path.Combine(game, "shaderpacks");
            Directory.CreateDirectory(shaders);
            File.WriteAllText(Path.Combine(shaders, "szep.zip"), "PK");
            File.WriteAllText(Path.Combine(shaders, "regi.zip.disabled"), "PK");
            File.WriteAllText(Path.Combine(shaders, "leiras.txt"), "x");

            var list = manager.ListShaders(game);
            AssertEqual(2, list.Count, "csak a zip-ek");
            Assert(list.First(s => s.FileName == "szep.zip").Enabled, "simma zip be");
            Assert(!list.First(s => s.FileName == "regi.zip.disabled").Enabled, "disabled ki");

            manager.SetShaderEnabled(game, "szep.zip", false);
            Assert(File.Exists(Path.Combine(shaders, "szep.zip.disabled")), "kikapcsolva");
            manager.SetShaderEnabled(game, "szep.zip.disabled", true);
            Assert(File.Exists(Path.Combine(shaders, "szep.zip")), "visszakapcsolva");

            var src = Path.Combine(root, "uj.zip");
            File.WriteAllText(src, "PK");
            var txtSrc = Path.Combine(root, "x.txt");
            File.WriteAllText(txtSrc, "x");
            AssertEqual("uj.zip", manager.AddShaderPack(game, src), "hozzáadva");
            manager.RemoveShaderPack(game, "uj.zip");
            Assert(!File.Exists(Path.Combine(shaders, "uj.zip")), "törölve");
            AssertThrows<ArgumentException>(() => manager.AddShaderPack(game, txtSrc),
                "nem zip elutasítva");
        });

        Check("Mod-frissítés: újabb verzió települ, tiltás megmarad", () =>
        {
            var routes = new RoutingHandler();
            var v2json = "{\"id\":\"v2\",\"project_id\":\"testmod\",\"version_number\":\"1.1\",\"version_type\":\"release\",\"loaders\":[\"fabric\"],\"game_versions\":[\"1.21.1\"],\"files\":[{\"filename\":\"mod-1.1.jar\",\"url\":\"https://cdn.modrinth.com/data/t/mod-1.1.jar\",\"primary\":true,\"size\":10,\"hashes\":{}}],\"dependencies\":[]}";
            routes.Map("version/v2", v2json);
            // Modrinth-sorrend: legfrissebb elöl → PickBest a v2 release-t adja.
            var v1json = "{\"id\":\"v1\",\"project_id\":\"testmod\",\"version_number\":\"1.0\",\"version_type\":\"release\",\"loaders\":[\"fabric\"],\"game_versions\":[\"1.21.1\"],\"files\":[{\"filename\":\"mod-1.0.jar\",\"url\":\"https://cdn.modrinth.com/data/t/mod-1.0.jar\",\"primary\":true,\"size\":10,\"hashes\":{}}],\"dependencies\":[]}";
            routes.Map("project/testmod/version", "[" + v2json + "," + v1json + "]");
            routes.MapBytes("cdn.modrinth.com/data/t/mod-1.1.jar",
                "PK-mod-1.1!!!"u8.ToArray(), "application/java-archive");

            var root = NewDirectory("mod-update");
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(Path.Combine(mods, ".turul-meta"));
            File.WriteAllText(Path.Combine(mods, "mod-1.0.jar"), "PK-old");
            File.WriteAllText(Path.Combine(mods, ".turul-meta", "mod-1.0.jar.json"),
                """{"ProjectId":"testmod","VersionId":"v1","VersionNumber":"1.0","FileName":"mod-1.0.jar","InstalledAt":"2026-01-01T00:00:00Z"}""");

            var installer = CreateInstaller(routes);
            var info = installer.CheckForUpdateAsync(mods, "mod-1.0.jar", "1.21.1", "fabric")
                .GetAwaiter().GetResult();
            Assert(info is not null && info.NewVersion == "1.1", "frissítés felajánlva");

            var updated = installer.UpdateModAsync(mods, "mod-1.0.jar", "1.21.1", "fabric")
                .GetAwaiter().GetResult();
            Assert(updated is not null && updated.FileName == "mod-1.1.jar", "új fájl");
            Assert(File.Exists(Path.Combine(mods, "mod-1.1.jar")), "új fájl a helyén");
            Assert(!File.Exists(Path.Combine(mods, "mod-1.0.jar")), "régi törölve");
            var meta = TurulMC.Core.Mods.ModrinthInstaller.ReadMeta(mods, "mod-1.1.jar");
            Assert(meta is not null && meta.VersionId == "v2", "meta frissítve");
        });

        Check("Mod-frissítés: tiltott mod tiltva marad, naprakészre nincs ajánlat", () =>
        {
            var routes = new RoutingHandler();
            routes.Map("version/v2", """{"id":"v2","project_id":"testmod","version_number":"1.1","version_type":"release","loaders":["fabric"],"game_versions":["1.21.1"],"files":[{"filename":"mod-1.1.jar","url":"https://cdn.modrinth.com/data/t/mod-1.1.jar","primary":true,"size":10,"hashes":{}}],"dependencies":[]}""");
            routes.Map("project/testmod/version", """
                [{"id":"v2","project_id":"testmod","version_number":"1.1","version_type":"release",
                  "loaders":["fabric"],"game_versions":["1.21.1"],
                  "files":[{"filename":"mod-1.1.jar","url":"https://cdn.modrinth.com/data/t/mod-1.1.jar",
                            "primary":true,"size":10,"hashes":{}}],
                  "dependencies":[]}]
                """);
            routes.MapBytes("cdn.modrinth.com/data/t/mod-1.1.jar",
                "PK-mod-1.1!!!"u8.ToArray(), "application/java-archive");

            var root = NewDirectory("mod-update-dis");
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(Path.Combine(mods, ".turul-meta"));
            File.WriteAllText(Path.Combine(mods, "mod-1.0.jar.disabled"), "PK-old");
            File.WriteAllText(Path.Combine(mods, ".turul-meta", "mod-1.0.jar.json"),
                """{"ProjectId":"testmod","VersionId":"v1","VersionNumber":"1.0","FileName":"mod-1.0.jar","InstalledAt":"2026-01-01T00:00:00Z"}""");

            var installer = CreateInstaller(routes);
            var updated = installer.UpdateModAsync(mods, "mod-1.0.jar.disabled", "1.21.1", "fabric")
                .GetAwaiter().GetResult();
            Assert(updated is not null, "frissült");
            Assert(File.Exists(Path.Combine(mods, "mod-1.1.jar.disabled")), "új fájl tiltva maradt");
            Assert(!File.Exists(Path.Combine(mods, "mod-1.1.jar")), "nincs engedélyezett duplikátum");
            Assert(!File.Exists(Path.Combine(mods, "mod-1.0.jar.disabled")), "régi törölve");

            // Naprakész: a meta már v2-re mutat.
            File.WriteAllText(Path.Combine(mods, ".turul-meta", "mod-1.1.jar.json"),
                """{"ProjectId":"testmod","VersionId":"v2","VersionNumber":"1.1","FileName":"mod-1.1.jar","InstalledAt":"2026-01-01T00:00:00Z"}""");
            var none = installer.CheckForUpdateAsync(mods, "mod-1.1.jar.disabled", "1.21.1", "fabric")
                .GetAwaiter().GetResult();
            Assert(none is null, "naprakészre nincs ajánlat");

            // Meta nélkül nincs ellenőrzés.
            var nometa = installer.CheckForUpdateAsync(mods, "kezileg.jar", "1.21.1", "fabric")
                .GetAwaiter().GetResult();
            Assert(nometa is null, "meta nélkül nincs ajánlat");
        });

        Check("Instance: duplikálás (logs nélkül) és backup export", () =>
        {
            var root = NewDirectory("inst-copy");
            var store = new TurulMC.Core.Storage.InstanceStore(
                Path.Combine(root, "instances.json"),
                Path.Combine(root, "instances"));
            var list = new List<TurulMC.Core.Models.LauncherInstance>
            {
                new() { Id = "abc123", Name = "Eredeti", MinecraftVersion = "1.21.1",
                        Loader = "fabric", RamMb = 4096 }
            };
            store.Save(list, "abc123");

            var srcDir = store.GetInstanceDirectory("abc123");
            Directory.CreateDirectory(Path.Combine(srcDir, "mods"));
            Directory.CreateDirectory(Path.Combine(srcDir, "logs"));
            File.WriteAllText(Path.Combine(srcDir, "mods", "a.jar"), "PK");
            File.WriteAllText(Path.Combine(srcDir, "logs", "latest.log"), "log");
            File.WriteAllText(Path.Combine(srcDir, "options.txt"), "opt");

            var (created, targetDir) = store.CopyInstanceAsync("abc123", "Eredeti")
                .GetAwaiter().GetResult();
            Assert(created.Id != "abc123", "új azonosító");
            Assert(created.Name != "Eredeti", "egyedi név: " + created.Name);
            AssertEqual("fabric", created.Loader, "loader öröklődik");
            Assert(File.Exists(Path.Combine(targetDir, "mods", "a.jar")), "mod másolva");
            Assert(File.Exists(Path.Combine(targetDir, "options.txt")), "config másolva");
            Assert(!Directory.Exists(Path.Combine(targetDir, "logs")), "logs kimarad");

            var (reloadedActive, reloaded) = store.Load();
            AssertEqual(2, reloaded.Count, "két instance mentve");
            AssertEqual(created.Id, reloadedActive, "az új az aktív");

            var zip = Path.Combine(root, "backup.zip");
            store.ExportBackupAsync(created.Id, zip).GetAwaiter().GetResult();
            Assert(File.Exists(zip), "zip elkészült");
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert(names.Contains("mods/a.jar"), "mod a zipben");
            Assert(!names.Any(n => n.StartsWith("logs/")), "logs nincs a zipben");

            AssertThrows<InvalidOperationException>(() =>
                store.CopyInstanceAsync("nincs", "X").GetAwaiter().GetResult(), "hiányzó forrás");
        });
    }

    private static void LauncherPathsTests()
    {
        Check("LauncherPaths: mindig írható adatkönyvtárat választ", () =>
        {
            Assert(TurulMC.Core.Storage.LauncherPaths.IsWritable(_tempRoot), "a teszt mappa írható");

            // Nem-írható rendszermappa: Windowson a System32, Linuxon a /proc
            // (oda semmilyen folyamat nem tud fájlt írni).
            var readOnlyPath = OperatingSystem.IsWindows()
                ? @"C:\Windows\System32\turul-proba-" + Guid.NewGuid().ToString("N")
                : "/proc/turul-proba-" + Guid.NewGuid().ToString("N");
            Assert(!TurulMC.Core.Storage.LauncherPaths.IsWritable(readOnlyPath),
                "rendszermappa nem írható: " + readOnlyPath);

            var root = TurulMC.Core.Storage.LauncherPaths.DataRoot;
            Assert(root.Length > 0, "van feloldott adatkönyvtár");
            Assert(TurulMC.Core.Storage.LauncherPaths.IsWritable(root),
                "a feloldott adatkönyvtárnak írhatónak kell lennie: " + root);

            var probe = Path.Combine(root, "smoke-test-" + Guid.NewGuid().ToString("N") + ".json");
            var write = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(probe, "{\"ok\":true}");
            Assert(write.Success, "írás a feloldott adatkönyvtárba: " + write.Error);
            AssertEqual("{\"ok\":true}", File.ReadAllText(probe), "tartalom");
            File.Delete(probe);
        });
    }

    // ---------------------------------------------------------------- segédek

    private static void CreateJar(string path, string fabricModJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(fabricModJson);
    }

    private sealed class StubInstallationService : IMinecraftInstallationService
    {
        public Task<MinecraftVersionDetail> GetVersionDetailAsync(string versionId)
            => Task.FromResult(new MinecraftVersionDetail { Id = versionId });

        public Task<string> GetClientJarPathAsync(string versionId) => Task.FromResult(Path.Combine(Path.GetTempPath(), versionId + ".jar"));
        public Task EnsureVersionDownloadedAsync(string versionId, IProgress<OverallProgress>? progress = null) => Task.CompletedTask;
        public Task<bool> ValidateVersionFilesAsync(string versionId) => Task.FromResult(true);
        public Task<IEnumerable<string>> GetInstalledVersionsAsync() => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        public string GetInstanceDirectory() => Path.GetTempPath();
        public string GetVersionDirectory(string versionId) => Path.Combine(Path.GetTempPath(), versionId);
        public string GetLibrariesDirectory() => Path.Combine(Path.GetTempPath(), "libraries");
        public string GetAssetsDirectory() => Path.Combine(Path.GetTempPath(), "assets");
    }

    private static LauncherDoctor CreateDoctor(DoctorOptions options, HttpStatusCode status) =>
        new(options, new JavaRuntimeService(dataRoot: options.DataRoot), serverStatusService: null,
            httpClient: new HttpClient(new StubHandler(status)) { Timeout = Timeout.InfiniteTimeSpan });

    private static (DoctorOptions Options, string Root) CreateDoctorFixture(string name, bool healthyJson)
    {
        var root = NewDirectory(name);
        var logs = Path.Combine(root, "logs");
        var instance = Path.Combine(root, "instance");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(logs, "launcher-2026-10-03.log"), "log line", Encoding.UTF8);
        File.WriteAllText(Path.Combine(logs, "startup-2026-10-03.log"), "startup", Encoding.UTF8);
        File.WriteAllText(Path.Combine(mods, "sodium.jar"), "jar", Encoding.UTF8);

        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            minecraftVersion = "26.1.2",
            loader = "fabric",
            defaultRamMb = 4096,
            autoInstallJava = true
        }), Encoding.UTF8);
        File.WriteAllText(Path.Combine(root, "instances.json"),
            healthyJson ? "{\"instances\":[{\"id\":\"dev\",\"name\":\"Dev\"}]}" : "{ }", Encoding.UTF8);

        var options = new DoctorOptions
        {
            LauncherVersion = "4.5.0-test",
            DataRoot = root,
            SettingsFilePath = settingsPath,
            InstancesFilePath = Path.Combine(root, "instances.json"),
            ProfilesFilePath = Path.Combine(root, "profiles.json"),
            LogsDirectory = logs,
            InstanceDirectory = instance,
            GameDirectory = instance,
            RequiredJavaMajor = 21,
            ServerHost = null,
            WebView2VersionProvider = () => "131.0.2903.86"
        };
        return (options, root);
    }

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(_tempRoot, name + "-" + (++_counter));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Check(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            Failures.Add($"{name}: {ex}");
        }
    }

    private static async Task CheckAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            Failures.Add($"{name}: {ex}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: várt='{expected}', kapott='{actual}'");
    }

    private static void AssertThrows<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{what}: {typeof(TException).Name} helyett {ex.GetType().Name} ({ex.Message})");
        }

        throw new InvalidOperationException($"{what}: nem dobott {typeof(TException).Name} kivételt");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StubHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                RequestMessage = request,
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
