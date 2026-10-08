using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Diagnostics;
using TurulMC.Core.Authentication;
using TurulMC.Core.Java;
using TurulMC.Core.Minecraft;
using TurulMC.Core.Models;
using TurulMC.Core.Modpacks;
using TurulMC.Core.Networking;
using TurulMC.Core.Storage;
using TurulMC.Core.Logging;
using TurulMC.Infrastructure.Authentication;
using TurulMC.Infrastructure.FileSystem;

namespace TurulMC.Launcher;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private readonly IAuthenticationService _authService;
    private IMinecraftInstallationService _installService = null!;
    private IFabricLoaderService _fabricService = null!;
    private readonly IFabricVersionResolver _fabricVersionResolver;
    private IModpackService _modpackService = null!;
    private readonly IJavaRuntimeService _javaService;
    private IMinecraftLauncherService _launcherService = null!;
    private readonly IServerStatusService _serverStatusService;
    private readonly ISettingsStorage _settingsStorage;
    private static readonly HttpClient NewsHttpClient = CreateNewsHttpClient();
    private static readonly HttpClient ModrinthHttpClient = CreateModrinthHttpClient();
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();
    private static string LauncherVersion => App.AppVersion;
    private const string StableUpdateManifestUrl = "https://turulnetwork.hu/launcher/update/stable.json";
    private const string BetaUpdateManifestUrl = "https://turulnetwork.hu/launcher/update/beta.json";

    private AppWindow? _appWindow;
    private bool _forceWindowClose;
    private bool _closePromptOpen;
    private WebView2? _webView;
    private TaskCompletionSource<bool>? _webViewReadyTcs;
    private WebView2 WebView => _webView ?? throw new InvalidOperationException("A WebView2 még nincs inicializálva.");
    private GameLoadingWindow? _loadingWindow;

    // Az adatkönyvtárat a LauncherPaths oldja fel: ha a megszokott %APPDATA%\TurulMC
    // nem írható (vírusirtó / Controlled Folder Access), automatikusan más írható
    // helyre váltunk, hogy az Instance-ok és beállítások mentése ne hasaljon el.
    private readonly string _instancesFilePath = TurulMC.Core.Storage.LauncherPaths.InstancesFile;
    private readonly string _instancesRootPath = TurulMC.Core.Storage.LauncherPaths.InstancesRoot;
    private List<LauncherInstance> _instances = new();
    private string _activeInstanceId = "";

    /// <summary>Ki nem írt mentési hiba (a felületnek egyszer jelezzük).</summary>
    private string? _pendingPersistenceError;

    /// <summary>Már figyelmeztettük-e a felhasználót mentési problémára.</summary>
    private bool _persistenceWarningShown;

    private sealed record InstanceCopyCategory(string Key, string Label, string[] Directories, string[] Files);

    private static readonly InstanceCopyCategory[] InstanceCopyCategories =
    {
        new("mods", "Mods", new[] { "mods" }, Array.Empty<string>()),
        new("shaders", "Shaders", new[] { "shaderpacks" }, Array.Empty<string>()),
        new("worlds", "Worlds", new[] { "saves" }, Array.Empty<string>()),
        new("resourcepacks", "Resource Packs", new[] { "resourcepacks" }, Array.Empty<string>()),
        new("screenshots", "Screenshots", new[] { "screenshots" }, Array.Empty<string>()),
        new("config", "Configs", new[] { "config" }, Array.Empty<string>()),
        new("settings", "Minecraft Settings", Array.Empty<string>(), new[] { "options.txt" }),
        new("servers", "Server List", Array.Empty<string>(), new[] { "servers.dat", "servers.dat_old" }),
        new("maps", "Maps / Waypoints", new[] { "journeymap", "xaero", "XaeroWaypoints", "XaeroWorldMap", "voxelmap" }, Array.Empty<string>()),
        new("schematics", "Schematics", new[] { "schematics", "schematic", "litematica" }, Array.Empty<string>()),
        new("replays", "Replay Recordings", new[] { "replay_recordings", "replays" }, Array.Empty<string>())
    };

    public MainWindow()
    {
        this.InitializeComponent();

        _authService = new LocalAuthenticationService();
        _fabricVersionResolver = new FabricVersionResolver();
        _javaProvisioner = new TurulMC.Core.Java.JavaRuntimeProvisioner();
        _javaService = new JavaRuntimeService(_javaProvisioner);
        _serverStatusService = new ServerStatusService();
        _settingsStorage = new SettingsStorage();

        LauncherLogger.Info("MainWindow: services initialized; loading instances.");
        LoadInstances();
        MigrateLegacyInstanceDirectory();
        ConfigureInstanceServices(_activeInstanceId);

        // Ha nem a megszokott adatkönyvtárat használjuk (mert az nem írható), szólunk róla.
        if (!string.IsNullOrWhiteSpace(TurulMC.Core.Storage.LauncherPaths.DataRootWarning))
        {
            var warning = TurulMC.Core.Storage.LauncherPaths.DataRootWarning!;
            LauncherLogger.Warning(warning);
            _pendingPersistenceError ??= warning;
        }

        LauncherLogger.Info("MainWindow: constructor completed.");
    }

    /// <summary>
    /// Called by App only after Window.Activate(). This guarantees that the
    /// native HWND/AppWindow exists before sizing/titlebar operations run.
    /// </summary>
    public void InitializeAfterActivation()
    {
        LauncherLogger.Info("MainWindow: post-activation initialization begin.");
        SetupWindow();
        LauncherLogger.Info("MainWindow: window setup completed.");
        _ = InitWebView();
    }

    /// <summary>
    /// Real startup preload used by the native splash screen. The WinUI window is
    /// activated only so WebView2 gets a valid HWND, then immediately hidden while
    /// the browser runtime, HTML UI and initial launcher state are loaded.
    /// </summary>
    public async Task<bool> InitializeForSplashAsync(Action<string, double>? progress = null)
    {
        LauncherLogger.Info("MainWindow: splash preload begin.");
        progress?.Invoke("Ablak előkészítése…", 34);
        SetupWindow();
        LauncherLogger.Info("MainWindow: window setup completed for splash preload.");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_HIDE);

        progress?.Invoke("WebView2 előkészítése…", 52);
        _webViewReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = InitWebView();

        try
        {
            progress?.Invoke("Launcher felület előtöltése…", 68);
            var ready = await _webViewReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            progress?.Invoke(ready ? "Beállítások és profilok betöltve…" : "A felület hibajelzéssel töltött be…", 92);
            LauncherLogger.Info($"MainWindow: splash preload completed. ready={ready}");
            return ready;
        }
        catch (TimeoutException)
        {
            LauncherLogger.Warning("MainWindow: splash preload timed out; showing launcher with its normal loading state.");
            progress?.Invoke("A launcher tovább tölt a háttérben…", 92);
            return false;
        }
    }

    public void ShowAfterSplash()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }

        Activate();
    }

    private string GetInstanceDirectory(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return Path.Combine(_instancesRootPath, "_unassigned");

        // IDs are generated internally, but keep path construction defensive.
        var safeId = new string(instanceId
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .ToArray());

        if (string.IsNullOrWhiteSpace(safeId))
            throw new InvalidOperationException("Érvénytelen Instance ID.");

        // FONTOS: ha az Instance tartalma a megszokott helyen már létezik, OTT MARAD.
        // Soha nem váltunk át automatikusan egy másik (üres) mappára: az a felhasználó
        // számára úgy tűnne, mintha eltűntek volna a modok és a játék. Ha a mappa
        // írásvédett (vírusirtó), csak naplózzuk és figyelmeztetünk — az olvasás/indítás
        // a megszokott helyről történik.
        try
        {
            var existing = Path.Combine(TurulMC.Core.Storage.LauncherPaths.DefaultInstancesRoot, safeId);
            if (Directory.Exists(existing))
            {
                if (!TurulMC.Core.Storage.LauncherPaths.IsWritable(existing))
                    LauncherLogger.Warning($"A megszokott Instance mappa írásvédett ({existing}). " +
                                           "A launcher innen indít és innen olvas, de az írási műveletek " +
                                           "(modfrissítés, mentés) hibát adhatnak — adj vírusirtó-kizárást!");
                return existing;
            }
        }
        catch { }

        return Path.Combine(_instancesRootPath, safeId);
    }

    private void ConfigureInstanceServices(string? instanceId)
    {
        var directory = GetInstanceDirectory(instanceId ?? "");

        // Az instance mappa létrehozása nem írható adatkönyvtárnál elhasalhat. Ez NEM
        // állíthatja meg az Instance kiválasztását/váltását: csak jelezzük, és a
        // memóriabeli állapottal tovább dolgozunk.
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Az Instance mappa nem hozható létre ({directory}): {ex.Message}");
            ReportPersistenceError($"Az Instance mappa nem hozható létre: {ex.Message}");
        }

        if (_launcherService is not null)
            _launcherService.MinecraftExited -= OnMinecraftExited;

        try
        {
            _installService = new MinecraftInstallationService(directory);
            _fabricService = new FabricLoaderService(directory);
            _modpackService = new ModpackService(directory);
            _launcherService = new MinecraftLauncherService(_installService, _javaService, instanceId ?? "");
            _launcherService.MinecraftExited += OnMinecraftExited;
        }
        catch (Exception ex)
        {
            // Ha a szolgáltatások létrehozása sem sikerül (pl. írásvédett mappa), akkor is
            // maradjon működő launcher: a hibát naplózzuk és jelezzük.
            LauncherLogger.Error($"Az Instance szolgáltatások inicializálása sikertelen ({directory})", ex);
            ReportPersistenceError($"Az Instance mappa nem használható: {ex.Message}");
        }
    }

    private void MigrateLegacyInstanceDirectory()
    {
        try
        {
            var legacy = Path.Combine(_instancesRootPath, "dev-instance");
            if (!Directory.Exists(legacy) || _instances.Count == 0)
                return;

            var targetId = !string.IsNullOrWhiteSpace(_activeInstanceId)
                ? _activeInstanceId
                : _instances[0].Id;
            var target = GetInstanceDirectory(targetId);

            if (Directory.Exists(target))
                return;

            Directory.CreateDirectory(_instancesRootPath);
            Directory.Move(legacy, target);
            LauncherLogger.Info($"Legacy dev-instance migrálva: {targetId}");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Legacy Instance mappa migráció kihagyva: {ex.Message}");
        }
    }

    private void SetupWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("A launcher ablaka még nem rendelkezik érvényes HWND-val.");

        var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(wid)
            ?? throw new InvalidOperationException("Az AppWindow nem érhető el az aktivált launcher ablakhoz.");

        _appWindow.Title = "TurulLauncher";
        try { TitleBarVersion.Text = "v" + App.AppVersion; } catch { }
        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "turullauncher.ico");
        if (File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        // Fit the initial window to the current monitor instead of assuming 1280x760.
        // This matters especially on 1366x768 laptops and with 125/150% DPI scaling.
        var display = DisplayArea.GetFromWindowId(wid, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var maxWidth = Math.Max(1, (int)Math.Floor(work.Width * 0.94));
        var maxHeight = Math.Max(1, (int)Math.Floor(work.Height * 0.92));
        var initialWidth = Math.Min(1280, maxWidth);
        var initialHeight = Math.Min(760, maxHeight);
        var x = work.X + Math.Max(0, (work.Width - initialWidth) / 2);
        var y = work.Y + Math.Max(0, (work.Height - initialHeight) / 2);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, initialWidth, initialHeight));

        var presenter = OverlappedPresenter.Create();
        // Use the real Windows frame/title bar for now. This guarantees native
        // dragging, edge/corner resizing, snap layouts, minimize and maximize.
        // The previous fully borderless presenter removed the resize hit targets.
        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        _appWindow.SetPresenter(presenter);

        ExtendsContentIntoTitleBar = false;
        SetTitleBar(null);

        TitleBar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 15, 18));
    }

    /// <summary>
    /// WebView2 inicializálás önjavítással. A WebView2 leggyakrabban a felhasználói
    /// adatmappa sérülése/foglaltsága vagy ÍRHATATLANSÁGA miatt nem indul el (a vírusirtó /
    /// Controlled Folder Access blokkolja az AppData írását) — ilyenkor a hiba a
    /// msedgewebview2.exe „Alkalmazáshiba (0x80000003)” ablakában is megjelenhet.
    /// Ezért: 1) mappa az exe mellett → 2) ha az nem írható, a bizonyítottan írható
    /// launcher-adatkönyvtár → 3) sérült mappa félretétele és újra → 4) %LOCALAPPDATA%.
    /// </summary>
    private async System.Threading.Tasks.Task InitWebView()
    {
        var primaryFolder = Path.Combine(AppContext.BaseDirectory, "TurulMC.Launcher.exe.WebView2");
        // A launcher adatkönyvtára írhatósági próbán esett át (LauncherPaths), ezért a
        // WebView2 adatmappájának ez a legjobb hely, ha az exe melletti mappa nem írható.
        var dataRootFolder = Path.Combine(TurulMC.Core.Storage.LauncherPaths.DataRoot, "WebView2");
        var fallbackFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurulMC", "WebView2");

        // Ha az exe melletti mappa nem írható (AV-blokk), eleve nem is próbálkozunk vele:
        // a WebView2 ott azonnal elhasal, és a felhasználó hibapárbeszédet kap.
        var primaryWritable = TurulMC.Core.Storage.LauncherPaths.IsWritable(primaryFolder);
        if (!primaryWritable)
            LauncherLogger.Warning($"A WebView2 adatmappa az exe mellett nem írható ({primaryFolder}); " +
                                   $"helyette: {dataRootFolder}");

        var attempts = new List<(string Folder, bool Reset)>();
        if (primaryWritable) attempts.Add((primaryFolder, false));
        attempts.Add((dataRootFolder, false));
        if (primaryWritable) attempts.Add((primaryFolder, true));
        attempts.Add((fallbackFolder, false));

        Exception? lastError = null;
        foreach (var (folder, reset) in attempts)
        {
            try
            {
                if (reset)
                {
                    var movedTo = MoveWebViewFolderAside(folder);
                    if (movedTo is null) continue;
                    LauncherLogger.Warning($"Sérült WebView2 adatmappa félretéve: {movedTo}");
                }

                await InitWebViewCoreAsync(folder);
                if (!folder.Equals(primaryFolder, StringComparison.OrdinalIgnoreCase))
                    LauncherLogger.Warning($"WebView2 tartalék adatmappát használ: {folder}");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LauncherLogger.Error($"WebView2 inicializálás sikertelen (mappa: {folder}, reset: {reset})", ex);
            }
        }

        ShowWebViewFailure(lastError ?? new InvalidOperationException("Ismeretlen WebView2 hiba."));
    }

    private async System.Threading.Tasks.Task InitWebViewCoreAsync(string userDataFolder)
    {
        // Create WebView2 only after the WinUI Window is alive. Creating it in
        // MainWindow.xaml can surface native/WebView2 startup failures as a
        // generic XamlParseException before we have a usable window.
        if (WebViewHost is null)
            throw new InvalidOperationException("A WebViewHost vezérlő nem jött létre (XAML betöltési hiba).");

        if (_webView is null)
        {
            _webView = new WebView2
            {
                Visibility = Visibility.Collapsed,
                DefaultBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 8, 9, 11)
            };
            WebViewHost.Children.Insert(0, _webView);
        }

        // Force an early native-loader/runtime probe. This distinguishes a
        // missing WebView2 Runtime/loader from an HTML/navigation problem.
        var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        LauncherLogger.Info($"WebView2 runtime: {runtimeVersion}; adatmappa: {userDataFolder}");

        try { Directory.CreateDirectory(userDataFolder); } catch (Exception ex) { LauncherLogger.Warning("WebView2 adatmappa nem hozható létre: " + ex.Message); }

        // Saját környezet: így pontosan tudjuk, melyik adatmappát használjuk, és ha az
        // sérült, a következő próbálkozásnál félre tudjuk tenni. A WebView2 a
        // WEBVIEW2_USER_DATA_FOLDER környezeti változót veszi figyelembe az első
        // környezet létrehozásakor (a WinUI vetítés nem tesz elérhetővé minden
        // CreateAsync overloadot, ezért ez a legrobusztusabb út).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

        // A msedgewebview2.exe „Alkalmazáshiba (0x80000003)” ablakát a beépített
        // crash-riporter okozza; kikapcsoljuk, hogy egy háttérfolyamat hibája ne
        // dobáljon fel felhasználói hibapárbeszédet (a hibát mi naplózzuk).
        try
        {
            var existingArgs = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (string.IsNullOrWhiteSpace(existingArgs) ||
                !existingArgs.Contains("disable-crash-reporter", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(
                    "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    (string.IsNullOrWhiteSpace(existingArgs) ? "" : existingArgs + " ") +
                    "--disable-crash-reporter --disable-breakpad");
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug("WebView2 crash-riporter kikapcsolása nem sikerült: " + ex.Message);
        }

        await WebView.EnsureCoreWebView2Async();

        if (WebView.CoreWebView2 is null)
            throw new InvalidOperationException("A WebView2 motor nem jött létre (CoreWebView2 = null).");

        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
#if DEBUG
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
        // Production launcher: do not expose Chromium/WebView2 developer UI.
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;

        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

        // Never resolve UI assets relative to the shell working directory.
        // `dotnet run --project ...` typically starts from the solution directory.
            var uiFolder = Path.Combine(AppContext.BaseDirectory, "ui");
            var indexFile = Path.Combine(uiFolder, "index.html");

            if (!File.Exists(indexFile))
                throw new FileNotFoundException($"Launcher UI not found: {indexFile}");

            // Give the UI a real HTTPS origin so localStorage, relative assets and
            // browser state work reliably. NavigateToString has an opaque origin
            // and WebView2 can deny localStorage access for it.
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "turul.local",
                uiFolder,
                CoreWebView2HostResourceAccessKind.Allow);
            WebView.CoreWebView2.Navigate("https://turul.local/index.html");
    }

    /// <summary>A sikertelen WebView2 indítás részletes, használható hibaüzenete.</summary>
    private void ShowWebViewFailure(Exception ex)
    {
        LauncherLogger.Error("WebView initialization failed", ex);
        try
        {
            if (_webView is not null)
                _webView.Visibility = Visibility.Collapsed;
            WebViewSpinner.IsActive = false;

            string runtime = "?";
            try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }

            var primary = Path.Combine(AppContext.BaseDirectory, "TurulMC.Launcher.exe.WebView2");
            WebViewStatusText.Text =
                $"A launcher felülete nem tölthető be.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n" +
                $"HRESULT: 0x{ex.HResult:X8}\n" +
                $"WebView2 runtime: {runtime}\n" +
                $"Adatmappa: {primary}\n" +
                $"Architektúra: {RuntimeInformation.ProcessArchitecture}\n\n" +
                "A launcher megpróbálta félretenni a sérült WebView2 adatmappát és egy " +
                "tartalék mappával újraindulni. Ha ez sem sikerült, telepítsd újra a " +
                "WebView2 Runtime-ot, vagy indítsd a launchert rendszergazdaként.";
        }
        catch { }
        try { WebViewStatus.Visibility = Visibility.Visible; } catch { }
        _webViewReadyTcs?.TrySetResult(false);
    }

    /// <summary>
    /// A sérült WebView2 adatmappa félretétele (nem törlés!): így a felhasználó
    /// bármikor visszaállíthatja, ha kell. Visszatérés: az új hely, vagy null.
    /// </summary>
    private static string? MoveWebViewFolderAside(string folder)
    {
        try
        {
            // Ha véletlenül FÁJL áll a mappa helyén (pl. egy félrement mentés), azt is
            // félretesszük — különben a WebView2 soha nem tudná létrehozni az adatmappát.
            if (File.Exists(folder))
            {
                var fileTarget = $"{folder}.file-broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(folder, fileTarget, true);
                LauncherLogger.Warning($"A WebView2 adatmappa helyén fájl volt, félretéve: {fileTarget}");
                return fileTarget;
            }

            if (!Directory.Exists(folder)) return "(nem létezett)";

            var target = $"{folder}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.Move(folder, target);
            return target;
        }
        catch (IOException)
        {
            // Ha a mappa zárolt (futó WebView2 folyamat), nem erőltetjük.
            return null;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"A WebView2 adatmappa nem tehető félre ({folder}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Üzenet küldése a felületnek, hibatűrően. A WebView2 "not ready" állapotban
    /// (<c>ERROR_INVALID_STATE 0x8007139F</c>) vagy már lezárt motor esetén a
    /// <c>PostWebMessageAsJson</c> kivételt dob — ez normális átmeneti állapot a
    /// betöltés/újratöltés közben, ezért nem hiba, csak csendben kihagyjuk.
    /// </summary>
    private bool TryPostWebMessage(string json)
    {
        try
        {
            var core = _webView?.CoreWebView2;
            if (core is null) return false;
            core.PostWebMessageAsJson(json);
            return true;
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"WebView üzenet kihagyva ({ex.GetType().Name}: 0x{ex.HResult:X8}) — a felület épp tölt vagy bezárt.");
            return false;
        }
    }

    private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebViewMessage? msg = null;
        bool ownsInstanceLease = false;
        try
        {
            var json = e.WebMessageAsJson;
            msg = JsonSerializer.Deserialize<WebViewMessage>(json);
            if (msg == null || string.IsNullOrEmpty(msg.Action)) return;

            if (NeedsInstanceOperationLease(msg.Action))
            {
                if (_instanceOperationBusy)
                    throw new InvalidOperationException("Egy másik Instance-művelet még folyamatban van. Várd meg a végét.");
                _instanceOperationBusy = ownsInstanceLease = true;
                if (msg.Action is "game.launch" or "launchGame" or "server.connect")
                    _launchAttemptInstanceId = "";
            }
            await PrepareModRecoveryAsync(msg.Action, msg.Data);
            object? result = msg.Action switch
            {
                "getSettings" => await HandleGetSettings(),
                "environment.info" => new
                {
                    userName = Environment.UserName,
                    machineName = Environment.MachineName,
                    os = Environment.OSVersion.VersionString,
                    launcherVersion = LauncherVersion
                },
                "getNews" => await HandleGetNews(),
                "mods.search" => await HandleSearchMods(msg.Data),
                "mods.details" => await HandleGetModDetails(msg.Data),
                "mods.versions" => await HandleGetModVersions(msg.Data),
                "mods.install" => await HandleInstallMod(msg.Data),
                "mods.installVersion" => await HandleInstallModVersion(msg.Data),
                "mods.list" => await HandleListInstalledMods(),
                "mods.remove" => await HandleRemoveMod(msg.Data),
                "mods.toggle" => await HandleToggleMod(msg.Data),
                "resourcepack.search" => await HandleSearchResourcePacks(msg.Data),
                "resourcepack.install" => await HandleInstallResourcePack(msg.Data),
                "resourcepack.list" => await HandleListResourcePacks(),
                "resourcepack.remove" => await HandleRemoveResourcePack(msg.Data),
                "modpack.search" => await HandleSearchModpacks(msg.Data),
                "modpack.versions" => await HandleModpackVersions(msg.Data),
                "modpack.switchVersion" => await HandleModpackSwitchVersion(msg.Data),
                "content.list" => await HandleListContent(msg.Data),
                "content.remove" => await HandleRemoveContent(msg.Data),
                "instance.backup" => await HandleBackupInstance(),
                "instance.stats" => await HandleInstanceStats(),
                "instance.snapshot" => await HandleSnapshotInstance(),
                "instance.recovery.list" => await HandleRecoveryList(msg.Data),
                "instance.recovery.restore" => await HandleRecoveryRestore(msg.Data),
                "instance.crash.last" => await HandleLastCrash(msg.Data),
                "instance.repair" => await HandleRepairInstance(msg.Data),
                "mods.scan" => await HandleModScan(msg.Data),
                "mods.updatePlan" => await HandleModUpdatePlan(msg.Data),
                "mods.update" => await HandleModUpdateApply(msg.Data),
                "support.bundle" => await HandleSupportBundle(),
                "network.health" => await HandleNetworkHealth(msg.Data),
                "update.check" => await HandleCheckForUpdate(msg.Data),
                "update.install" => await HandleInstallUpdate(msg.Data),
                "openUrl" => HandleOpenUrl(msg.Data),
                "saveSettings" or "settings.save" => await HandleSaveSettings(msg.Data),
                "getProfiles" => await HandleGetProfiles(),
                "instances.list" => await HandleListInstances(),
                "instances.create" => await HandleCreateInstance(msg.Data),
                "instances.select" => await HandleSelectInstance(msg.Data),
                "instances.update" => await HandleUpdateInstance(msg.Data),
                "instances.compatibility" => await HandleInstanceCompatibility(msg.Data),
                "minecraft.versions" => await HandleMinecraftVersions(),
                "instances.copyPreview" => await HandleInstanceCopyPreview(msg.Data),
                "instances.copyContent" => await HandleInstanceCopyContent(msg.Data),
                "instances.delete" => await HandleDeleteInstance(msg.Data),
                "instances.clear" => await HandleClearInstances(),
                "data.clear" => await HandleDataClear(msg.Data),
                "createProfile" or "profile.create" => await HandleCreateProfile(msg.Data),
                "deleteProfile" => await HandleDeleteProfile(msg.Data),
                "selectProfile" => await HandleSelectProfile(msg.Data),
                "profile.rename" => await HandleRenameProfile(msg.Data),
                "checkServer" => await HandleCheckServer(msg.Data),
                "server.connect" => await HandleServerConnect(msg.Data),
                "servers.list" => await HandleServersList(),
                "servers.add" => await HandleServersAdd(msg.Data),
                "servers.remove" => await HandleServersRemove(msg.Data),
                "localserver.install" => await HandleLocalServerInstall(msg.Data),
                "localserver.eula" => await HandleLocalServerEula(msg.Data),
                "localserver.start" => await HandleLocalServerStart(msg.Data),
                "localserver.stop" => HandleLocalServerStop(),
                "localserver.log" => HandleLocalServerLog(),
                "localserver.open" => HandleLocalServerOpen(msg.Data),
                "modpack.import" => await HandleModpackImport(msg.Data),
                "modpack.install" => await HandleModpackInstall(msg.Data),
                "syncModpack" => await HandleSyncModpack(msg.Data),
                "launchGame" or "game.launch" => await HandleLaunchGame(msg.Data),
                "game.status" => _launcherService.GetStatus(),
                "getInstalledVersions" => await HandleGetInstalledVersions(),
                "getAvailableVersions" => await HandleGetAvailableVersions(),
                "getJavaInfo" => await HandleGetJavaInfo(),
                "detectJava" => await HandleDetectJava(),
                "java.list" => await HandleJavaList(),
                "java.provision" => await HandleJavaProvision(msg.Data),
                "java.remove" => await HandleJavaRemove(msg.Data),
                "java.setPath" => await HandleJavaSetPath(msg.Data),
                "doctor.run" => await HandleDoctorRun(msg.Data),
                "doctor.fix" => await HandleDoctorFix(msg.Data),
                "defender.exclusion" => await HandleDefenderExclusion(),
                "openFolder" => HandleOpenFolder(msg.Data),
                "window.minimize" => HandleWindowMinimize(),
                "window.maximize" => HandleWindowMaximize(),
                "window.close" => HandleWindowClose(),
                _ => throw new InvalidOperationException($"Ismeretlen launcher művelet: {msg.Action}")
            };

            var response = new WebViewResponse
            {
                Id = msg.Id,
                Success = true,
                Data = result
            };

            TryPostWebMessage(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("WebView message handling failed", ex);
            var userError = ex.Message;
            if (ownsInstanceLease && (msg?.Action is "game.launch" or "launchGame" or "server.connect") &&
                !string.IsNullOrEmpty(_launchAttemptInstanceId))
            {
                try
                {
                    var explanation = await ExplainFailureAsync(_launchAttemptInstanceId, _launchAttemptStartedUtc, ex.Message);
                    userError = explanation.Summary;
                }
                catch (Exception diagnosticError) { LauncherLogger.Warning("Hibaelemzés: " + diagnosticError.Message); }
            }
            var errorResponse = new WebViewResponse
            {
                Id = msg?.Id ?? "",
                Success = false,
                Error = userError
            };
            TryPostWebMessage(JsonSerializer.Serialize(errorResponse));
        }
        finally
        {
            if (ownsInstanceLease) _instanceOperationBusy = false;
        }
    }


    private static HttpClient CreateModrinthHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TurulNetwork-TurulLauncher/0.10 (https://turulnetwork.hu)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private LauncherInstance GetActiveModInstance()
    {
        var instance = _instances.FirstOrDefault(x =>
            x.Id.Equals(_activeInstanceId, StringComparison.OrdinalIgnoreCase));

        if (instance is null)
            throw new InvalidOperationException("A modok használatához előbb válassz egy aktív Instance-t.");

        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion))
            throw new InvalidOperationException("Az aktív Instance Minecraft-verziója nincs beállítva.");

        instance.Loader = (instance.Loader ?? "none").Trim().ToLowerInvariant();
        return instance;
    }

    private async System.Threading.Tasks.Task<Dictionary<string, string>> GetInstalledModProjectMapAsync()
    {
        var modsDirectory = Path.Combine(_installService.GetInstanceDirectory(), "mods");
        Directory.CreateDirectory(modsDirectory);
        var metadataDirectory = Path.Combine(modsDirectory, ".turul-meta");
        Directory.CreateDirectory(metadataDirectory);

        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Fast path: read the per-Instance metadata cache first. Mods installed by
        // the launcher and newer modpacks both write their Modrinth project id here.
        foreach (var metadataPath in Directory.EnumerateFiles(metadataDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var metaDoc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
                var meta = metaDoc.RootElement;
                var projectId = meta.TryGetProperty("projectId", out var projectEl) ? projectEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(projectId)) continue;
                var filename = Path.GetFileName(metadataPath);
                if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    filename = filename[..^".json".Length];
                installed[projectId] = filename;
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Mod metadata cache nem olvashato ({Path.GetFileName(metadataPath)}): {ex.Message}");
            }
        }

        // Compatibility path for older modpacks or manually copied JARs that do
        // not yet have Turul metadata. Resolve the Modrinth project by SHA-512 once
        // and cache it, so every later search is instant and remembers the state.
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            var filename = Path.GetFileName(path);
            var normalizedFilename = filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? filename[..^".disabled".Length]
                : filename;
            var metadataPath = Path.Combine(metadataDirectory, normalizedFilename + ".json");
            if (File.Exists(metadataPath)) continue;

            try
            {
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(stream)).ToLowerInvariant();
                using var versionResponse = await ModrinthHttpClient.GetAsync($"version_file/{hash}?algorithm=sha512");
                if (!versionResponse.IsSuccessStatusCode) continue;

                using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                var version = versionDoc.RootElement;
                var projectId = version.TryGetProperty("project_id", out var projectEl) ? projectEl.GetString() ?? "" : "";
                var versionId = version.TryGetProperty("id", out var versionEl) ? versionEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(projectId)) continue;

                installed[projectId] = filename;
                await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
                {
                    title = Path.GetFileNameWithoutExtension(normalizedFilename),
                    iconUrl = "",
                    projectId,
                    versionId,
                    source = "discovered",
                    cachedAt = DateTime.UtcNow
                }));
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Mod project felismeres sikertelen ({filename}): {ex.Message}");
            }
        }

        return installed;
    }

    private async System.Threading.Tasks.Task<object?> HandleSearchMods(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var query = obj.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var offset = obj.TryGetProperty("offset", out var offsetProp) && offsetProp.TryGetInt32(out var parsedOffset)
            ? Math.Max(0, parsedOffset) : 0;
        var limit = obj.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 50) : 12;

        // The active Instance is the single source of truth for mod compatibility.
        // Never trust stale Settings-page DOM values sent by the WebView.
        var instance = GetActiveModInstance();
        var mcVersion = instance.MinecraftVersion;
        var loader = instance.Loader;

        if (string.IsNullOrWhiteSpace(loader) || loader.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new { requiresLoader = true, hits = Array.Empty<object>() };

        var installedProjects = await GetInstalledModProjectMapAsync();

        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:mod" },
            new[] { $"versions:{mcVersion}" },
            new[] { $"categories:{loader.ToLowerInvariant()}" }
        });

        var url =
            $"search?query={Uri.EscapeDataString(query)}" +
            $"&facets={Uri.EscapeDataString(facets)}" +
            $"&index=relevance&limit={limit}&offset={offset}";

        using var response = await ModrinthHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hits = new List<object>();

        if (doc.RootElement.TryGetProperty("hits", out var hitsElement))
        {
            foreach (var hit in hitsElement.EnumerateArray())
            {
                var projectId = hit.TryGetProperty("project_id", out var id) ? id.GetString() ?? "" : "";
                var installedFilename = "";
                var isInstalled = !string.IsNullOrWhiteSpace(projectId) &&
                                  installedProjects.TryGetValue(projectId, out installedFilename);
                hits.Add(new
                {
                    projectId,
                    title = hit.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    description = hit.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    author = hit.TryGetProperty("author", out var author) ? author.GetString() ?? "" : "",
                    iconUrl = hit.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String
                        ? icon.GetString() ?? ""
                        : "",
                    downloads = hit.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count)
                        ? count
                        : 0,
                    installed = isInstalled,
                    installedFilename = isInstalled ? installedFilename ?? "" : ""
                });
            }
        }

        var totalHits = doc.RootElement.TryGetProperty("total_hits", out var totalProp) && totalProp.TryGetInt32(out var total)
            ? total : hits.Count;
        return new { requiresLoader = false, mcVersion, loader, offset, limit, totalHits, hits };
    }

    private async System.Threading.Tasks.Task<object?> HandleGetModDetails(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Hiányzik a Modrinth projektazonosító.");

        using var response = await ModrinthHttpClient.GetAsync(
            $"project/{Uri.EscapeDataString(projectId)}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var project = doc.RootElement;
        var installedProjects = await GetInstalledModProjectMapAsync();
        var installedFilename = "";
        var isInstalled = installedProjects.TryGetValue(projectId, out installedFilename);

        return new
        {
            projectId,
            installed = isInstalled,
            installedFilename = isInstalled ? installedFilename ?? "" : "",
            title = project.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            description = project.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            body = project.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            iconUrl = project.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String
                ? icon.GetString() ?? "" : "",
            downloads = project.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count)
                ? count : 0,
            sourceUrl = project.TryGetProperty("source_url", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString() ?? "" : "",
            issuesUrl = project.TryGetProperty("issues_url", out var issues) && issues.ValueKind == JsonValueKind.String
                ? issues.GetString() ?? "" : ""
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleGetModVersions(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Hiányzik a Modrinth projektazonosító.");

        // The active Instance is the single source of truth for mod compatibility.
        // Never trust stale Settings-page DOM values sent by the WebView.
        var instance = GetActiveModInstance();
        var mcVersion = instance.MinecraftVersion;
        var loader = instance.Loader;

        if (string.IsNullOrWhiteSpace(loader) || loader.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new { projectId, mcVersion, loader, versions = Array.Empty<object>() };

        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }));
        var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion }));

        using var response = await ModrinthHttpClient.GetAsync(
            $"project/{Uri.EscapeDataString(projectId)}/version?loaders={loaders}&game_versions={gameVersions}&include_changelog=false");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var versions = new List<object>();

        foreach (var version in doc.RootElement.EnumerateArray())
        {
            versions.Add(new
            {
                id = version.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                name = version.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                versionNumber = version.TryGetProperty("version_number", out var number) ? number.GetString() ?? "" : "",
                versionType = version.TryGetProperty("version_type", out var type) ? type.GetString() ?? "" : "",
                datePublished = version.TryGetProperty("date_published", out var date) ? date.GetString() ?? "" : ""
            });
        }

        return new { projectId, mcVersion, loader, versions };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstallModVersion(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var versionId = obj.TryGetProperty("versionId", out var v) ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("Hiányzik a Modrinth verzióazonosító.");

        // The active Instance is the single source of truth for mod compatibility.
        // Never trust stale Settings-page DOM values sent by the WebView.
        var instance = GetActiveModInstance();
        var mcVersion = instance.MinecraftVersion;
        var loader = instance.Loader;

        if (string.IsNullOrWhiteSpace(loader) || loader.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod telepítéséhez előbb válassz modloadert.");

        var installed = new List<object>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await InstallModrinthVersionAsync(versionId, mcVersion, loader, visited, installed);

        return new { success = true, installed };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstallMod(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var projectId = obj.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Hiányzik a Modrinth projektazonosító.");

        // The active Instance is the single source of truth for mod compatibility.
        // Never trust stale Settings-page DOM values sent by the WebView.
        var instance = GetActiveModInstance();
        var mcVersion = instance.MinecraftVersion;
        var loader = instance.Loader;

        if (string.IsNullOrWhiteSpace(loader) || loader.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod telepítéséhez előbb válassz Fabric, NeoForge vagy Forge loadert.");

        var installed = new List<object>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await InstallModrinthProjectAsync(projectId, mcVersion, loader, visited, installed);

        return new { success = true, installed };
    }

    private const int ModrinthMaxDepth = 25;
    public const long ModrinthMaxFileBytes = 512L * 1024L * 1024L; // 512 MB méretcap

    private async System.Threading.Tasks.Task InstallModrinthProjectAsync(
        string projectId,
        string mcVersion,
        string loader,
        HashSet<string> visited,
        List<object> installed)
    {
        if (visited.Count >= ModrinthMaxDepth)
            throw new InvalidOperationException("Modrinth függőség-mélység túllépve (25).");
        if (!visited.Add(projectId))
            return;

        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }));
        var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion }));

        using var response = await ModrinthHttpClient.GetAsync(
            $"project/{Uri.EscapeDataString(projectId)}/version?loaders={loaders}&game_versions={gameVersions}&include_changelog=false");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"Nincs kompatibilis Modrinth-verzió ehhez: {mcVersion} / {loader}.");

        JsonElement selected = default;
        foreach (var candidate in doc.RootElement.EnumerateArray())
        {
            if (selected.ValueKind == JsonValueKind.Undefined)
                selected = candidate.Clone();

            if (candidate.TryGetProperty("version_type", out var versionType) &&
                versionType.GetString() == "release")
            {
                selected = candidate.Clone();
                break;
            }
        }

        await InstallRequiredDependenciesAsync(selected, mcVersion, loader, visited, installed);
        await DownloadSelectedModFileAsync(selected, installed);
    }

    private async System.Threading.Tasks.Task InstallRequiredDependenciesAsync(
        JsonElement version,
        string mcVersion,
        string loader,
        HashSet<string> visited,
        List<object> installed)
    {
        if (!version.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
            return;

        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("dependency_type", out var dependencyType) ||
                dependencyType.GetString() != "required")
                continue;

            var depProjectId =
                dependency.TryGetProperty("project_id", out var projectProp) &&
                projectProp.ValueKind == JsonValueKind.String
                    ? projectProp.GetString()
                    : null;

            var depVersionId =
                dependency.TryGetProperty("version_id", out var versionProp) &&
                versionProp.ValueKind == JsonValueKind.String
                    ? versionProp.GetString()
                    : null;

            if (!string.IsNullOrWhiteSpace(depVersionId))
            {
                await InstallModrinthVersionAsync(
                    depVersionId!,
                    mcVersion,
                    loader,
                    visited,
                    installed);
            }
            else if (!string.IsNullOrWhiteSpace(depProjectId))
            {
                await InstallModrinthProjectAsync(
                    depProjectId!,
                    mcVersion,
                    loader,
                    visited,
                    installed);
            }
        }
    }

    private async System.Threading.Tasks.Task InstallModrinthVersionAsync(
        string versionId,
        string mcVersion,
        string loader,
        HashSet<string> visited,
        List<object> installed)
    {
        if (visited.Count >= ModrinthMaxDepth)
            throw new InvalidOperationException("Modrinth függőség-mélység túllépve (25).");
        using var response =
            await ModrinthHttpClient.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = doc.RootElement.Clone();

        var projectId =
            version.TryGetProperty("project_id", out var projectProp)
                ? projectProp.GetString() ?? versionId
                : versionId;

        if (!visited.Add(projectId))
            return;

        var gameOk =
            version.TryGetProperty("game_versions", out var gameVersions) &&
            gameVersions.ValueKind == JsonValueKind.Array &&
            gameVersions.EnumerateArray().Any(x => x.GetString() == mcVersion);

        var loaderOk =
            version.TryGetProperty("loaders", out var loaders) &&
            loaders.ValueKind == JsonValueKind.Array &&
            loaders.EnumerateArray().Any(
                x => string.Equals(x.GetString(), loader, StringComparison.OrdinalIgnoreCase));

        if (!gameOk || !loaderOk)
            throw new InvalidOperationException(
                "Egy szükséges függőség nem kompatibilis a kiválasztott Minecraft-verzióval vagy loaderrel.");

        await InstallRequiredDependenciesAsync(version, mcVersion, loader, visited, installed);
        await DownloadSelectedModFileAsync(version, installed);
    }

    private async System.Threading.Tasks.Task DownloadSelectedModFileAsync(
        JsonElement version,
        List<object> installed)
    {
        if (!version.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() == 0)
            throw new InvalidOperationException("A Modrinth-verzióhoz nem tartozik letölthető fájl.");

        JsonElement selectedFile = default;
        foreach (var file in files.EnumerateArray())
        {
            if (selectedFile.ValueKind == JsonValueKind.Undefined)
                selectedFile = file.Clone();

            if (file.TryGetProperty("primary", out var primary) &&
                primary.ValueKind == JsonValueKind.True)
            {
                selectedFile = file.Clone();
                break;
            }
        }

        var filename = Path.GetFileName(
            selectedFile.TryGetProperty("filename", out var filenameProp)
                ? filenameProp.GetString() ?? ""
                : "");

        var downloadUrl =
            selectedFile.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString() ?? ""
                : "";

        if (string.IsNullOrWhiteSpace(filename) ||
            !filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A kiválasztott Modrinth-fájl nem JAR mod.");

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Érvénytelen Modrinth letöltési URL.");

        var modsDirectory = Path.Combine(_installService.GetInstanceDirectory(), "mods");
        Directory.CreateDirectory(modsDirectory);

        var destination = Path.Combine(modsDirectory, filename);
        var temporary = destination + ".download";

        try
        {
            using (var response =
                   await ModrinthHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var declared = response.Content.Headers.ContentLength;
                if (declared is <= 0 or > ModrinthMaxFileBytes)
                    throw new InvalidOperationException($"Modrinth fájl méret érvénytelen / túl nagy (512 MB cap): {filename}");
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(temporary);
                var buf = new byte[128 * 1024];
                long total = 0;
                int r;
                while ((r = await input.ReadAsync(buf)) > 0)
                {
                    total += r;
                    if (total > ModrinthMaxFileBytes) throw new InvalidOperationException($"Modrinth fájl túl nagy (512 MB cap): {filename}");
                    await output.WriteAsync(buf.AsMemory(0, r));
                }
            }

            // Kötelező sha512: hiány = törlés + kivétel.
            if (!(selectedFile.TryGetProperty("hashes", out var hashes) &&
                hashes.TryGetProperty("sha512", out var sha512Element) &&
                sha512Element.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(sha512Element.GetString())))
                throw new InvalidOperationException($"Hiányzó sha512 hash: {filename} — letöltés elutasítva.");
            {
                var expected = sha512Element.GetString() ?? "";

                await using var stream = File.OpenRead(temporary);
                var actual =
                    Convert.ToHexString(await SHA512.HashDataAsync(stream))
                        .ToLowerInvariant();

                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"A letöltött mod ellenőrzőösszege hibás: {filename}");
            }

            File.Move(temporary, destination, true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }

        var installedProjectId =
            version.TryGetProperty("project_id", out var projectIdProp)
                ? projectIdProp.GetString() ?? ""
                : "";
        var installedVersionId =
            version.TryGetProperty("id", out var idProp)
                ? idProp.GetString() ?? ""
                : "";

        try
        {
            if (!string.IsNullOrWhiteSpace(installedProjectId))
            {
                using var projectResponse = await ModrinthHttpClient.GetAsync(
                    $"project/{Uri.EscapeDataString(installedProjectId)}");
                if (projectResponse.IsSuccessStatusCode)
                {
                    using var projectDoc = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
                    var project = projectDoc.RootElement;
                    var title = project.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? filename : filename;
                    var iconUrl = project.TryGetProperty("icon_url", out var iconEl) && iconEl.ValueKind == JsonValueKind.String
                        ? iconEl.GetString() ?? "" : "";

                    var metadataDirectory = Path.Combine(modsDirectory, ".turul-meta");
                    Directory.CreateDirectory(metadataDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(metadataDirectory, filename + ".json"),
                        JsonSerializer.Serialize(new
                        {
                            title,
                            iconUrl,
                            projectId = installedProjectId,
                            versionId = installedVersionId
                        }));
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Mod metaadat cache nem menthető ({filename}): {ex.Message}");
        }

        installed.Add(new
        {
            projectId = installedProjectId,
            versionId = installedVersionId,
            filename
        });
    }


    private System.Threading.Tasks.Task<object?> HandleToggleMod(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var filename = Path.GetFileName(obj.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "");
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidOperationException("Hiányzik a mod fájlneve.");

        var modsDirectory = Path.Combine(_installService.GetInstanceDirectory(), "mods");
        Directory.CreateDirectory(modsDirectory);

        var source = Path.Combine(modsDirectory, filename);
        string target;

        if (filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            target = Path.Combine(modsDirectory, filename[..^".disabled".Length]);
        else
            target = source + ".disabled";

        if (!File.Exists(source))
            throw new FileNotFoundException("A mod fájl nem található.", source);

        File.Move(source, target, true);
        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            success = true,
            filename = Path.GetFileName(target),
            enabled = !target.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
        });
    }

    private System.Threading.Tasks.Task<object?> HandleListContent(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var type = obj.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var folder = type switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            "world" => "saves",
            "screenshot" => "screenshots",
            _ => throw new InvalidOperationException("Ismeretlen tartalomtípus.")
        };

        var directory = Path.Combine(_installService.GetInstanceDirectory(), folder);
        Directory.CreateDirectory(directory);

        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Select(path =>
            {
                var isDirectory = Directory.Exists(path);
                long size = 0;
                if (!isDirectory)
                {
                    try { size = new FileInfo(path).Length; } catch { }
                }
                var name = Path.GetFileName(path);
                var iconDataUrl = type switch
                {
                    "world" when isDirectory => TryReadPngFileDataUrl(Path.Combine(path, "icon.png")),
                    "resourcepack" => TryReadResourcePackIconDataUrl(path),
                    _ => ""
                };
                return (object)new
                {
                    name,
                    isDirectory,
                    size,
                    iconDataUrl
                };
            })
            .OrderBy(x => x.ToString())
            .ToArray();

        return System.Threading.Tasks.Task.FromResult<object?>(new { type, folder, entries });
    }

    private System.Threading.Tasks.Task<object?> HandleRemoveContent(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var type = obj.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var name = Path.GetFileName(obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
        var folder = type switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            "world" => "saves",
            "screenshot" => "screenshots",
            _ => throw new InvalidOperationException("Ismeretlen tartalomtípus.")
        };
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Hiányzik a fájl vagy mappa neve.");

        var path = Path.Combine(_installService.GetInstanceDirectory(), folder, name);
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);

        return System.Threading.Tasks.Task.FromResult<object?>(new { success = true });
    }

    private System.Threading.Tasks.Task<object?> HandleInstanceStats()
    {
        var root = _installService.GetInstanceDirectory();
        long bytes = 0;
        int files = 0;
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch { }
            }
        }

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            path = root,
            files,
            bytes,
            mods = Directory.Exists(Path.Combine(root, "mods"))
                ? Directory.EnumerateFiles(Path.Combine(root, "mods"), "*.jar*", SearchOption.TopDirectoryOnly).Count()
                : 0,
            worlds = Directory.Exists(Path.Combine(root, "saves"))
                ? Directory.EnumerateDirectories(Path.Combine(root, "saves")).Count()
                : 0
        });
    }

    private async System.Threading.Tasks.Task<object?> HandleBackupInstance()
    {
        var instanceDirectory = _installService.GetInstanceDirectory();
        Directory.CreateDirectory(instanceDirectory);

        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TurulLauncher",
            "backups");
        Directory.CreateDirectory(backupDirectory);

        var filename = $"TurulLauncher-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var destination = Path.Combine(backupDirectory, filename);

        await System.Threading.Tasks.Task.Run(() =>
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(
                instanceDirectory,
                destination,
                System.IO.Compression.CompressionLevel.Fastest,
                false);
        });

        return new { success = true, filename, path = destination };
    }

    private async System.Threading.Tasks.Task<object?> HandleSnapshotInstance()
    {
        var root = _installService.GetInstanceDirectory();
        Directory.CreateDirectory(root);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurulMC", "snapshots");
        Directory.CreateDirectory(dir);
        var name = $"snapshot-{_activeInstanceId}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(dir, name);
        await System.Threading.Tasks.Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(root, path, System.IO.Compression.CompressionLevel.Fastest, false));
        return new { success = true, filename = name, path };
    }

    // A Smart Repair (HandleRepairInstance) a MainWindow.Repair.cs partial fájlban van,
    // mert a mod-verzió ellenőrzést is tartalmazza.

    private async System.Threading.Tasks.Task<object?> HandleSupportBundle()
    {
        // A csomag összeállítása a Core-ban történik (SupportBundleService), hogy a CLI
        // és a GUI ugyanazt a tartalmat állítsa elő — a Doctor jelentéssel együtt.
        var result = await CreateSupportBundleAsync(includeDoctorReport: true);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "A Support ZIP elkészítése nem sikerült.");

        LauncherLogger.Info($"Support bundle elkészült: {result.FileName} ({result.TotalBytes} byte)");
        return new
        {
            success = true,
            filename = result.FileName,
            path = result.FullPath,
            sizeBytes = result.TotalBytes,
            files = result.IncludedFiles.Count
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleNetworkHealth(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var host = obj.TryGetProperty("host", out var h) ? h.GetString() ?? "play.turulnetwork.hu" : "play.turulnetwork.hu";
        var port = obj.TryGetProperty("port", out var p) && p.TryGetInt32(out var parsed) ? parsed : 25565;
        bool dns = false, web = false;
        string dnsInfo = "";
        try { var ips = await Dns.GetHostAddressesAsync(host); dns = ips.Length > 0; dnsInfo = string.Join(", ", ips.Select(x => x.ToString()).Take(3)); } catch (Exception ex) { dnsInfo = ex.Message; }
        try { using var r = await NewsHttpClient.GetAsync("https://turulnetwork.hu", HttpCompletionOption.ResponseHeadersRead); web = (int)r.StatusCode < 500; } catch { }
        var mc = await _serverStatusService.CheckServerStatusAsync(host, port);
        return new { success = dns && mc.IsOnline, dns, dnsInfo, website = web, minecraft = mc.IsOnline, players = mc.OnlinePlayers, maxPlayers = mc.MaxPlayers, host, port };
    }

    private async System.Threading.Tasks.Task<object?> HandleListInstalledMods()
    {
        var modsDirectory = Path.Combine(_installService.GetInstanceDirectory(), "mods");
        Directory.CreateDirectory(modsDirectory);

        var metadataDirectory = Path.Combine(modsDirectory, ".turul-meta");
        Directory.CreateDirectory(metadataDirectory);

        var result = new List<object>();
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            var filename = Path.GetFileName(path);
            var normalizedFilename = filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? filename[..^".disabled".Length]
                : filename;
            var metadataPath = Path.Combine(metadataDirectory, normalizedFilename + ".json");

            string title = Path.GetFileNameWithoutExtension(normalizedFilename);
            string iconUrl = "";
            string projectId = "";
            string versionId = "";

            try
            {
                if (File.Exists(metadataPath))
                {
                    using var metaDoc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
                    var meta = metaDoc.RootElement;
                    title = meta.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? title : title;
                    iconUrl = meta.TryGetProperty("iconUrl", out var iconEl) ? iconEl.GetString() ?? "" : "";
                    projectId = meta.TryGetProperty("projectId", out var projectEl) ? projectEl.GetString() ?? "" : "";
                    versionId = meta.TryGetProperty("versionId", out var versionEl) ? versionEl.GetString() ?? "" : "";
                }
                else
                {
                    await using var fileStream = File.OpenRead(path);
                    var hash = Convert.ToHexString(await SHA512.HashDataAsync(fileStream)).ToLowerInvariant();

                    using var versionResponse = await ModrinthHttpClient.GetAsync(
                        $"version_file/{hash}?algorithm=sha512");
                    if (versionResponse.IsSuccessStatusCode)
                    {
                        using var versionDoc = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                        var version = versionDoc.RootElement;
                        projectId = version.TryGetProperty("project_id", out var projectEl) ? projectEl.GetString() ?? "" : "";
                        versionId = version.TryGetProperty("id", out var versionEl) ? versionEl.GetString() ?? "" : "";

                        if (!string.IsNullOrWhiteSpace(projectId))
                        {
                            using var projectResponse = await ModrinthHttpClient.GetAsync(
                                $"project/{Uri.EscapeDataString(projectId)}");
                            if (projectResponse.IsSuccessStatusCode)
                            {
                                using var projectDoc = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
                                var project = projectDoc.RootElement;
                                title = project.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? title : title;
                                iconUrl = project.TryGetProperty("icon_url", out var iconEl) && iconEl.ValueKind == JsonValueKind.String
                                    ? iconEl.GetString() ?? "" : "";

                                await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
                                {
                                    title,
                                    iconUrl,
                                    projectId,
                                    versionId
                                }));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Mod metaadat nem tölthető be ({filename}): {ex.Message}");
            }

            var iconDataUrl = string.IsNullOrWhiteSpace(iconUrl) ? TryReadModIconDataUrl(path) : "";
            result.Add(new
            {
                filename,
                title,
                iconUrl,
                iconDataUrl,
                projectId,
                versionId,
                size = new FileInfo(path).Length,
                enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    private System.Threading.Tasks.Task<object?> HandleRemoveMod(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");

        var filename =
            Path.GetFileName(
                obj.TryGetProperty("filename", out var fileProp)
                    ? fileProp.GetString() ?? ""
                    : "");

        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidOperationException("Hiányzik a mod fájlneve.");

        var modsDirectory = Path.Combine(_installService.GetInstanceDirectory(), "mods");
        var fullPath = Path.Combine(modsDirectory, filename);

        if (File.Exists(fullPath))
            File.Delete(fullPath);

        var normalizedFilename = filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? filename[..^".disabled".Length]
            : filename;
        var metadataPath = Path.Combine(modsDirectory, ".turul-meta", normalizedFilename + ".json");
        if (File.Exists(metadataPath))
            File.Delete(metadataPath);

        return System.Threading.Tasks.Task.FromResult<object?>(new { success = true });
    }

    private static HttpClient CreateNewsHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151.0 Safari/537.36 TurulLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private async System.Threading.Tasks.Task<object?> HandleGetNews()
    {
        // Prefer a small JSON endpoint if the website exposes one. This keeps the
        // launcher compatible with a future API without another launcher update.
        foreach (var url in new[]
        {
            "https://turulnetwork.hu/api/news",
            "https://turulnetwork.hu/news.json"
        })
        {
            try
            {
                using var response = await NewsHttpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync();
                var parsed = TryParseNewsJson(body);
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception ex)
            {
                LauncherLogger.Info($"News endpoint unavailable ({url}): {ex.Message}");
            }
        }

        // Fallback: extract the news cards/articles directly from the public site.
        try
        {
            using var response = await NewsHttpClient.GetAsync("https://turulnetwork.hu/");
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            return ParseNewsFromHtml(html);
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Could not load news from turulnetwork.hu", ex);
            return new List<LauncherNewsItem>();
        }
    }

    private static List<LauncherNewsItem> TryParseNewsJson(string json)
    {
        var result = new List<LauncherNewsItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("news", out var news))
                root = news;
            if (root.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in root.EnumerateArray())
            {
                var title = GetJsonString(item, "title", "name", "headline");
                if (string.IsNullOrWhiteSpace(title)) continue;
                result.Add(new LauncherNewsItem
                {
                    Title = title,
                    Excerpt = GetJsonString(item, "excerpt", "description", "summary"),
                    Date = GetJsonString(item, "date", "publishedAt", "published"),
                    Url = NormalizeNewsUrl(GetJsonString(item, "url", "link", "href")),
                    ImageUrl = NormalizeNewsUrl(GetJsonString(item, "imageUrl", "image", "cover"))
                });
                if (result.Count >= 12) break;
            }
        }
        catch { }
        return result;
    }

    private static string GetJsonString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }

    private static List<LauncherNewsItem> ParseNewsFromHtml(string html)
    {
        var items = new List<LauncherNewsItem>();
        var scope = html;
        var newsAnchor = Regex.Match(html, """<(?:section|div)[^>]+(?:id|class)\s*=\s*["'](?:[^"']*\bnews\b|news)[^"']*["'][^>]*>""", RegexOptions.IgnoreCase);
        if (newsAnchor.Success)
            scope = html.Substring(newsAnchor.Index, Math.Min(html.Length - newsAnchor.Index, 180000));

        var blocks = Regex.Matches(scope,
            """<(article|div)[^>]+class\s*=\s*["'][^"']*(?:news|post|article)[^"']*["'][^>]*>(?<body>.*?)</\1>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match block in blocks)
        {
            var body = block.Groups["body"].Value;
            var title = FirstText(body, @"<h[2-5][^>]*>(.*?)</h[2-5]>", """class\s*=\s*["'][^"']*(?:title|headline)[^"']*["'][^>]*>(.*?)</""");
            if (string.IsNullOrWhiteSpace(title) || title.Equals("Hírek", StringComparison.OrdinalIgnoreCase)) continue;

            var excerpt = FirstText(body, @"<p[^>]*>(.*?)</p>", """class\s*=\s*["'][^"']*(?:excerpt|description|summary)[^"']*["'][^>]*>(.*?)</""");
            var href = FirstGroup(body, """<a[^>]+href\s*=\s*["']([^"']+)["']""");
            var image = FirstGroup(body, """<img[^>]+src\s*=\s*["']([^"']+)["']""");
            var date = FirstText(body, @"<(?:time|span)[^>]*(?:date|time)[^>]*>(.*?)</(?:time|span)>");
            if (string.IsNullOrWhiteSpace(date))
                date = FirstGroup(body, """datetime\s*=\s*["']([^"']+)["']""");

            items.Add(new LauncherNewsItem
            {
                Title = title,
                Excerpt = excerpt,
                Date = date,
                Url = NormalizeNewsUrl(href),
                ImageUrl = NormalizeNewsUrl(image)
            });
            if (items.Count >= 12) break;
        }

        // Generic semantic fallback for simple static homepages.
        if (items.Count == 0)
        {
            foreach (Match heading in Regex.Matches(scope, @"<h[2-4][^>]*>(?<title>.*?)</h[2-4]>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var title = CleanHtmlText(heading.Groups["title"].Value);
                if (string.IsNullOrWhiteSpace(title) || title.Equals("Hírek", StringComparison.OrdinalIgnoreCase)) continue;
                var tail = scope.Substring(heading.Index + heading.Length, Math.Min(1800, scope.Length - (heading.Index + heading.Length)));
                var excerpt = FirstText(tail, @"<p[^>]*>(.*?)</p>");
                if (string.IsNullOrWhiteSpace(excerpt)) continue;
                items.Add(new LauncherNewsItem { Title = title, Excerpt = excerpt });
                if (items.Count >= 6) break;
            }
        }
        return items;
    }

    private static string FirstText(string source, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success) return CleanHtmlText(m.Groups[1].Value);
        }
        return "";
    }

    private static string FirstGroup(string source, string pattern)
    {
        var m = Regex.Match(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
    }

    private static string CleanHtmlText(string value)
    {
        value = Regex.Replace(value, @"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, "<[^>]+>", " ");
        value = WebUtility.HtmlDecode(value);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string NormalizeNewsUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (Uri.TryCreate(new Uri("https://turulnetwork.hu/"), value, out var relative)) return relative.ToString();
        return "";
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TurulNetwork-TurulLauncher/4.6.0");
        return client;
    }

    private static string NormalizeUpdateChannel(string? channel) =>
        string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    private static string GetManifestUrl(string channel) =>
        channel == "beta" ? BetaUpdateManifestUrl : StableUpdateManifestUrl;

    private static int CompareSemVer(string left, string right)
    {
        static (int[] Numbers, string Pre) Parse(string value)
        {
            var clean = (value ?? "0").Trim().TrimStart('v', 'V');
            var dash = clean.IndexOf('-');
            var core = dash >= 0 ? clean[..dash] : clean;
            var pre = dash >= 0 ? clean[(dash + 1)..] : "";
            var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var nums = new int[Math.Max(3, parts.Length)];
            for (var i = 0; i < parts.Length && i < nums.Length; i++)
                _ = int.TryParse(parts[i], out nums[i]);
            return (nums, pre);
        }

        var a = Parse(left);
        var b = Parse(right);
        for (var i = 0; i < Math.Max(a.Numbers.Length, b.Numbers.Length); i++)
        {
            var av = i < a.Numbers.Length ? a.Numbers[i] : 0;
            var bv = i < b.Numbers.Length ? b.Numbers[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        if (string.IsNullOrEmpty(a.Pre) && string.IsNullOrEmpty(b.Pre)) return 0;
        if (string.IsNullOrEmpty(a.Pre)) return 1;
        if (string.IsNullOrEmpty(b.Pre)) return -1;
        return string.Compare(a.Pre, b.Pre, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedUpdateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return uri.Host.Equals("turulnetwork.hu", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".turulnetwork.hu", StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task<LauncherUpdateManifest> GetUpdateManifestAsync(string channel)
    {
        var url = GetManifestUrl(channel);
        using var response = await UpdateHttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var manifest = JsonSerializer.Deserialize<LauncherUpdateManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Az update manifest üres vagy hibás.");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Az update manifest nem tartalmaz verziót.");
        if (!IsAllowedUpdateUrl(manifest.Url))
            throw new InvalidOperationException("Az update csomag URL-je nem engedélyezett.");
        if (!Regex.IsMatch(manifest.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("Az update manifest SHA-256 értéke hibás.");
        return manifest;
    }

    private async System.Threading.Tasks.Task<object?> HandleCheckForUpdate(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var settings = await _settingsStorage.LoadSettingsAsync();
        var requested = obj.TryGetProperty("channel", out var c) ? c.GetString() : null;
        var channel = NormalizeUpdateChannel(requested ?? settings.UpdateChannel);
        var manifest = await GetUpdateManifestAsync(channel);
        var available = CompareSemVer(manifest.Version, LauncherVersion) > 0;
        return new
        {
            currentVersion = LauncherVersion,
            channel,
            available,
            version = manifest.Version,
            required = manifest.Required,
            changelog = manifest.Changelog ?? Array.Empty<string>(),
            publishedAt = manifest.PublishedAt
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstallUpdate(string? data)
    {
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Frissítés előtt zárd be a Minecraftot.");

        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var settings = await _settingsStorage.LoadSettingsAsync();
        var requested = obj.TryGetProperty("channel", out var c) ? c.GetString() : null;
        var channel = NormalizeUpdateChannel(requested ?? settings.UpdateChannel);
        var manifest = await GetUpdateManifestAsync(channel);
        if (CompareSemVer(manifest.Version, LauncherVersion) <= 0)
            return new { started = false, reason = "up-to-date", currentVersion = LauncherVersion };

        // The updater owns the download now. That lets its progress bar, Pause/Resume,
        // Cancel and Log buttons represent the real update operation instead of a fake animation.
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurulMC", "updates", manifest.Version);
        Directory.CreateDirectory(updateRoot);

        var installedUpdater = Path.Combine(AppContext.BaseDirectory, "TurulMC.Updater.exe");
        if (!File.Exists(installedUpdater))
            throw new FileNotFoundException("A TurulMC.Updater.exe hiányzik a launcher mellől. Telepítsd újra a launchert.", installedUpdater);

        var tempUpdater = Path.Combine(updateRoot, $"TurulMC.Updater-{Guid.NewGuid():N}.exe");
        File.Copy(installedUpdater, tempUpdater, true);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("A launcher futtatható fájlja nem azonosítható.");

        var psi = new ProcessStartInfo
        {
            FileName = tempUpdater,
            UseShellExecute = true,
            WorkingDirectory = updateRoot
        };

        // A single-file .NET hoszt alapból a %TEMP%-be csomagol ki. Ha az a mappa
        // írásvédett/tele van (vagy vírusirtó blokkolja), az updater el sem indul:
        // "Access to the path '...\Temp\<random>' is denied". Ezért a kicsomagolást a
        // launcher saját, garantáltan írható mappájába irányítjuk.
        try
        {
            var extractBase = Path.Combine(updateRoot, "extract");
            Directory.CreateDirectory(extractBase);
            psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractBase;
            psi.Environment["TEMP"] = extractBase;
            psi.Environment["TMP"] = extractBase;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Az updater temp környezete nem állítható be: " + ex.Message);
        }

        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(manifest.Url);
        psi.ArgumentList.Add("--sha256");
        psi.ArgumentList.Add(manifest.Sha256);
        psi.ArgumentList.Add("--install-dir");
        psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        psi.ArgumentList.Add("--launcher");
        psi.ArgumentList.Add(processPath);
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(manifest.Version);
        var updaterProcess = Process.Start(psi);
        if (updaterProcess is null)
            throw new InvalidOperationException("Az updater nem indítható el.");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(850);
            DispatcherQueue.TryEnqueue(() => Close());
        });

        return new { started = true, version = manifest.Version };
    }

    private object HandleOpenUrl(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var url = obj.TryGetProperty("url", out var p) ? p.GetString() ?? "" : "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
        return new { success = true };
    }

    private async System.Threading.Tasks.Task<object?> HandleGetSettings()
    {
        var s = await _settingsStorage.LoadSettingsAsync();
        return s;
    }

    private async System.Threading.Tasks.Task<object?> HandleSaveSettings(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var settings = await _settingsStorage.LoadSettingsAsync();

        // The UI sends small { key, value } patches instead of replacing the
        // entire settings file. Only settings represented by LauncherSettings
        // are persisted here; UI-only preferences still acknowledge cleanly.
        if (obj.TryGetProperty("key", out var keyProp))
        {
            var key = keyProp.GetString() ?? "";
            obj.TryGetProperty("value", out var value);
            switch (key)
            {
                case "ram":
                    if (int.TryParse(value.ToString(), out var ramGb))
                        settings.DefaultRamMb = Math.Clamp(ramGb, 1, 64) * 1024;
                    break;
                case "javaPath":
                    settings.JavaPathOverride = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                    break;
                case "autoInstallJava":
                    settings.AutoInstallJava = value.ValueKind == JsonValueKind.True ||
                        (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var autoJava) && autoJava);
                    break;
                case "minecraftVersion":
                    settings.MinecraftVersion = value.GetString() ?? "26.1.2";
                    break;
                case "loader":
                    settings.Loader = value.GetString() ?? "none";
                    break;
                case "theme":
                    settings.Theme = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "yellow" : "yellow";
                    break;
                case "performanceMode":
                    settings.PerformanceMode = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "full" : "full";
                    break;
                case "uiScalePercent":
                    if (int.TryParse(value.ToString(), out var uiScale))
                        settings.UiScalePercent = Math.Clamp(uiScale, 75, 150);
                    break;
                case "language":
                case "lang":
                    var language = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "hu" : "hu";
                    settings.Language = language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "hu";
                    break;
                case "firstRunCompleted":
                    settings.FirstRunCompleted = value.ValueKind == JsonValueKind.True ||
                        (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var firstRun) && firstRun);
                    break;
                case "tutorialCompleted":
                    settings.TutorialCompleted = value.ValueKind == JsonValueKind.True ||
                        (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var tutorialDone) && tutorialDone);
                    break;
                case "closeBehavior":
                    {
                        var mode = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "ask" : "ask";
                        settings.CloseBehavior = mode is "exit" or "minimize" or "tray" or "ask" ? mode : "ask";
                        break;
                    }
                case "gameStartBehavior":
                    {
                        var mode = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "tray" : "tray";
                        settings.GameStartBehavior = mode is "exit" or "minimize" or "tray" or "keep" ? mode : "tray";
                        break;
                    }
                case "updates":
                case "updateChecksEnabled":
                    settings.UpdateChecksEnabled = value.ValueKind == JsonValueKind.True ||
                        (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var updateChecks) && updateChecks);
                    break;
                case "updateChannel":
                    var channel = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "stable" : "stable";
                    settings.UpdateChannel = channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
                    break;
                case "currentProfileId":
                    settings.CurrentProfileId = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    break;
            }
            await _settingsStorage.SaveSettingsAsync(settings);
            return new { success = true };
        }

        var replacement = JsonSerializer.Deserialize<LauncherSettings>(data ?? "{}");
        if (replacement != null)
            await _settingsStorage.SaveSettingsAsync(replacement);
        return new { success = true };
    }

    private void LoadInstances()
    {
        var directory = Path.GetDirectoryName(_instancesFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var backupPath = _instancesFilePath + ".bak";
        JsonDocument? doc = null;
        string? loadedFrom = null;

        foreach (var candidate in new[] { _instancesFilePath, backupPath })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(candidate));
                loadedFrom = candidate;
                break;
            }
            catch (Exception ex)
            {
                LauncherLogger.Warning($"Instance config nem olvasható ({Path.GetFileName(candidate)}): {ex.Message}");
            }
        }

        if (doc is null)
        {
            _instances = new List<LauncherInstance>();
            _activeInstanceId = "";
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LauncherLogger.Warning($"Instance config hibás gyökérelem ({loadedFrom ?? "unknown"}); üres listával indulunk.");
                _instances = new List<LauncherInstance>();
                _activeInstanceId = "";
                return;
            }

            var loaded = root.TryGetProperty("instances", out var instancesEl)
                ? JsonSerializer.Deserialize<List<LauncherInstance>>(instancesEl.GetRawText()) ?? new()
                : new List<LauncherInstance>();

            var cleaned = new List<LauncherInstance>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in loaded)
            {
                if (instance is null ||
                    string.IsNullOrWhiteSpace(instance.Id) ||
                    !usedIds.Add(instance.Id) ||
                    string.IsNullOrWhiteSpace(instance.MinecraftVersion) ||
                    (instance.Loader != "none" && instance.Loader != "fabric") ||
                    instance.RamMb < 1024 || instance.RamMb > 65536)
                {
                    LauncherLogger.Warning("Hibás Instance-bejegyzés kihagyva.");
                    continue;
                }

                instance.Name = instance.Name?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(instance.Name))
                    instance.Name = cleaned.Count == 0 ? "Alap Instance" : $"Instance {cleaned.Count + 1}";
                if (instance.Name.Length > 48)
                    instance.Name = instance.Name[..48].Trim();

                var baseName = instance.Name;
                var suffix = 2;
                while (!usedNames.Add(instance.Name))
                    instance.Name = $"{baseName} {suffix++}";

                instance.Loader = instance.Loader.ToLowerInvariant();
                if (instance.Loader == "none")
                    instance.LoaderVersion = "";
                if (instance.CreatedAt == default)
                    instance.CreatedAt = DateTime.UtcNow;
                if (instance.LastUsed == default)
                    instance.LastUsed = instance.CreatedAt;

                HydrateInstanceModpackMetadata(instance);
                cleaned.Add(instance);
            }

            _instances = cleaned;
            _activeInstanceId = root.TryGetProperty("activeInstanceId", out var active)
                ? active.GetString() ?? ""
                : "";

            if (!_instances.Any(x => x.Id.Equals(_activeInstanceId, StringComparison.OrdinalIgnoreCase)))
                _activeInstanceId = _instances.OrderByDescending(x => x.LastUsed).FirstOrDefault()?.Id ?? "";
        }

        // If backup recovery was needed, restore a canonical primary file.
        if (!string.Equals(loadedFrom, _instancesFilePath, StringComparison.OrdinalIgnoreCase))
            LauncherLogger.Warning("Instance config backupból visszaállítva.");
        SaveInstances();
    }

    private void HydrateInstanceModpackMetadata(LauncherInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.ModpackProjectId) &&
            !string.IsNullOrWhiteSpace(instance.ModpackVersionId))
            return;

        var path = Path.Combine(GetInstanceDirectory(instance.Id), ".turul-modpack.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            instance.ModpackProjectId = root.TryGetProperty("projectId", out var projectEl) ? projectEl.GetString() ?? "" : "";
            instance.ModpackVersionId = root.TryGetProperty("versionId", out var versionEl) ? versionEl.GetString() ?? "" : "";
            instance.ModpackName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? instance.Name : instance.Name;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Modpack metaadat nem olvasható ({instance.Name}): {ex.Message}");
        }
    }

    private void SaveInstances()
    {
        var directory = Path.GetDirectoryName(_instancesFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) { ReportPersistenceError("Az Instance mappa nem hozható létre: " + ex.Message); return; }
        }

        var tmpPath = _instancesFilePath + ".tmp";
        var backupPath = _instancesFilePath + ".bak";

        if (_instances.Count == 0)
        {
            _activeInstanceId = "";
            foreach (var path in new[] { _instancesFilePath, tmpPath, backupPath })
            {
                try
                {
                    TurulMC.Core.Storage.AtomicFile.ClearReadOnly(path);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    LauncherLogger.Warning($"Instance config törlés sikertelen ({Path.GetFileName(path)}): {ex.Message}");
                }
            }

            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            activeInstanceId = _activeInstanceId,
            instances = _instances
        }, new JsonSerializerOptions { WriteIndented = true });

        // Robusztus írás: egyedi temp fájl, elárvult temp takarítás, írásvédett attribútum
        // leválasztása, biztonsági másolat, és hiba esetén NEM dob — a launcher ettől nem
        // indulhat el (ez volt a 2026-10-05-i indítási hiba).
        var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(_instancesFilePath, json);
        if (!result.Success)
            ReportPersistenceError(result.Error);
        else if (result.UsedFallback)
            LauncherLogger.Warning("Az instances.json csak tartalék útvonalon menthető.");
    }

    /// <summary>
    /// Mentési hiba jelzése a felhasználónak: napló + egyszeri toast a felületen. A launcher
    /// ettől tovább működik (a memóriában lévő állapot megmarad), csak a fájl nem íródott ki.
    /// </summary>
    private void ReportPersistenceError(string error)
    {
        LauncherLogger.Error("Instance config mentése sikertelen: " + error);
        _pendingPersistenceError ??= error;
        try
        {
            if (_webView?.CoreWebView2 is null) return;
            var message = _pendingPersistenceError;
            _pendingPersistenceError = null;
            TryPostWebMessage(JsonSerializer.Serialize(new
            {
                action = "toast",
                data = new
                {
                    type = "error",
                    message = "A launcher beállításait nem sikerült kiírni a lemezre. " +
                              "Ellenőrizd, hogy nem fut-e két launcher, és hogy van-e írási jog a %APPDATA%\\TurulMC mappához. " +
                              error
                }
            }));
        }
        catch { }
    }

    private System.Threading.Tasks.Task<object?> HandleListInstances()
    {
        if (!string.IsNullOrWhiteSpace(_activeInstanceId) &&
            !_instances.Any(x => x.Id.Equals(_activeInstanceId, StringComparison.OrdinalIgnoreCase)))
        {
            _activeInstanceId = _instances.OrderByDescending(x => x.LastUsed).FirstOrDefault()?.Id ?? "";
            SaveInstances();
        }

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            activeInstanceId = _activeInstanceId,
            instances = _instances.Select(instance => new
            {
                id = instance.Id,
                name = instance.Name,
                minecraftVersion = instance.MinecraftVersion,
                loader = instance.Loader,
                loaderVersion = instance.LoaderVersion,
                ramMb = instance.RamMb,
                modpackProjectId = instance.ModpackProjectId,
                modpackVersionId = instance.ModpackVersionId,
                modpackName = instance.ModpackName,
                javaPath = instance.JavaPath,
                jvmArgs = instance.JvmArgs,
                windowWidth = instance.WindowWidth,
                windowHeight = instance.WindowHeight,
                notes = instance.Notes,
                createdAt = instance.CreatedAt,
                lastUsed = instance.LastUsed,
                path = GetInstanceDirectory(instance.Id)
            }).ToArray()
        });
    }

    private async System.Threading.Tasks.Task<object?> HandleCreateInstance(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var name = obj.TryGetProperty("name", out var nameEl) ? nameEl.GetString()?.Trim() ?? "" : "";
        var minecraftVersion = obj.TryGetProperty("minecraftVersion", out var mcEl) ? mcEl.GetString() ?? "26.1.2" : "26.1.2";
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new InvalidOperationException("Válassz Minecraft-verziót.");
        var loader = obj.TryGetProperty("loader", out var loaderEl) ? loaderEl.GetString()?.ToLowerInvariant() ?? "none" : "none";
        if (!loader.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A(z) {loader} loader még nincs támogatva ebben a kiadásban.");
        var ramGb = obj.TryGetProperty("ram", out var ramEl) && ramEl.TryGetInt32(out var parsedRam) ? parsedRam : 4;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Adj nevet az Instance-nek.");
        if (name.Length > 48)
            throw new InvalidOperationException("Az Instance neve legfeljebb 48 karakter lehet.");
        if (_instances.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Már létezik ilyen nevű Instance.");

        var instance = new LauncherInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = "",
            RamMb = Math.Clamp(ramGb, 1, 64) * 1024,
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        _instances.Add(instance);
        _activeInstanceId = instance.Id;
        SaveInstances();
        ConfigureInstanceServices(instance.Id);

        return new
        {
            success = true,
            instance = new
            {
                id = instance.Id,
                name = instance.Name,
                minecraftVersion = instance.MinecraftVersion,
                loader = instance.Loader,
                loaderVersion = instance.LoaderVersion,
                ramMb = instance.RamMb,
                modpackProjectId = instance.ModpackProjectId,
                modpackVersionId = instance.ModpackVersionId,
                modpackName = instance.ModpackName,
                createdAt = instance.CreatedAt,
                lastUsed = instance.LastUsed,
                path = GetInstanceDirectory(instance.Id)
            }
        };
    }

    private System.Threading.Tasks.Task<object?> HandleSelectInstance(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var instance = _instances.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az Instance nem található.");

        instance.LastUsed = DateTime.UtcNow;
        _activeInstanceId = instance.Id;
        SaveInstances();
        ConfigureInstanceServices(instance.Id);

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            success = true,
            instance = new
            {
                id = instance.Id,
                name = instance.Name,
                minecraftVersion = instance.MinecraftVersion,
                loader = instance.Loader,
                loaderVersion = instance.LoaderVersion,
                ramMb = instance.RamMb,
                modpackProjectId = instance.ModpackProjectId,
                modpackVersionId = instance.ModpackVersionId,
                modpackName = instance.ModpackName,
                createdAt = instance.CreatedAt,
                lastUsed = instance.LastUsed,
                path = GetInstanceDirectory(instance.Id)
            }
        });
    }

    private System.Threading.Tasks.Task<object?> HandleUpdateInstance(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : _activeInstanceId;
        var instance = _instances.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az aktív Instance nem található.");

        if (obj.TryGetProperty("minecraftVersion", out var mc) && mc.ValueKind == JsonValueKind.String)
        {
            var nextVersion = mc.GetString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(nextVersion))
                throw new InvalidOperationException("Válassz Minecraft-verziót.");
            instance.MinecraftVersion = nextVersion;
        }

        if (obj.TryGetProperty("loader", out var loader) && loader.ValueKind == JsonValueKind.String)
        {
            var nextLoader = (loader.GetString() ?? instance.Loader).ToLowerInvariant();
            if (!nextLoader.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                !nextLoader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A(z) {nextLoader} loader még nincs támogatva ebben a kiadásban.");
            if (!string.Equals(nextLoader, instance.Loader, StringComparison.OrdinalIgnoreCase))
                instance.LoaderVersion = "";
            instance.Loader = nextLoader;
        }

        if (obj.TryGetProperty("ramMb", out var ram) && ram.TryGetInt32(out var ramMb))
            instance.RamMb = Math.Clamp(ramMb, 1024, 65536);

        // Fabric Loader verzió: üres = automatikus (ajánlott) verzió indításkor.
        if (obj.TryGetProperty("loaderVersion", out var loaderVersionEl) && loaderVersionEl.ValueKind == JsonValueKind.String)
        {
            var nextLoaderVersion = loaderVersionEl.GetString()?.Trim() ?? "";
            if (nextLoaderVersion.Length > 64)
                throw new InvalidOperationException("A Fabric Loader verzió legfeljebb 64 karakter lehet.");
            if (nextLoaderVersion.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '+' or '_')))
                throw new InvalidOperationException("A Fabric Loader verzió csak betűt, számot és . - + _ karaktereket tartalmazhat.");
            instance.LoaderVersion = nextLoaderVersion;
        }

        // Instance-specifikus Java felülírás (üres = automatikus, verzióhoz illő Java).
        if (obj.TryGetProperty("javaPath", out var javaPathEl) && javaPathEl.ValueKind == JsonValueKind.String)
        {
            var nextJavaPath = (javaPathEl.GetString() ?? "").Trim().Trim('"');
            if (nextJavaPath.Length == 0)
            {
                instance.JavaPath = "";
            }
            else
            {
                TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(nextJavaPath);
                instance.JavaPath = nextJavaPath;
            }
        }

        // Extra JVM argumentumok (tiltott fragmentek validálva).
        if (obj.TryGetProperty("jvmArgs", out var jvmArgsEl) && jvmArgsEl.ValueKind == JsonValueKind.String)
        {
            var nextJvmArgs = (jvmArgsEl.GetString() ?? "").Trim();
            if (nextJvmArgs.Length > 512)
                throw new InvalidOperationException("A JVM argumentumok legfeljebb 512 karakter hosszúak lehetnek.");
            ParseInstanceJvmArgs(nextJvmArgs);
            instance.JvmArgs = nextJvmArgs;
        }

        // Játékablak méret (0 = a Minecraft saját beállítása).
        var hasWidth = obj.TryGetProperty("windowWidth", out var widthEl) && widthEl.TryGetInt32(out _);
        var hasHeight = obj.TryGetProperty("windowHeight", out var heightEl) && heightEl.TryGetInt32(out _);
        if (hasWidth || hasHeight)
        {
            var width = hasWidth && widthEl.TryGetInt32(out var parsedWidth) ? parsedWidth : instance.WindowWidth;
            var height = hasHeight && heightEl.TryGetInt32(out var parsedHeight) ? parsedHeight : instance.WindowHeight;
            if (width != 0 && (width < 320 || width > 7680))
                throw new InvalidOperationException("Az ablak szélessége 0 (automatikus) vagy 320–7680 pixel lehet.");
            if (height != 0 && (height < 240 || height > 4320))
                throw new InvalidOperationException("Az ablak magassága 0 (automatikus) vagy 240–4320 pixel lehet.");
            if ((width == 0) != (height == 0))
                throw new InvalidOperationException("Az ablak szélességét és magasságát együtt kell megadni (vagy mindkettő 0).");
            instance.WindowWidth = width;
            instance.WindowHeight = height;
        }

        if (obj.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
        {
            var nextNotes = (notesEl.GetString() ?? "").Trim();
            if (nextNotes.Length > 500)
                throw new InvalidOperationException("A megjegyzés legfeljebb 500 karakter lehet.");
            instance.Notes = nextNotes;
        }

        if (obj.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            var nextName = name.GetString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(nextName))
                throw new InvalidOperationException("Az Instance neve nem lehet üres.");
            if (nextName.Length > 48)
                throw new InvalidOperationException("Az Instance neve legfeljebb 48 karakter lehet.");
            if (_instances.Any(x => x.Id != instance.Id &&
                                    x.Name.Equals(nextName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Már létezik ilyen nevű Instance.");
            instance.Name = nextName;
        }

        // Loader nélküli Instance-hoz nem tartozhat Fabric Loader verzió.
        if (!instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            instance.LoaderVersion = "";

        instance.LastUsed = DateTime.UtcNow;
        _activeInstanceId = instance.Id;
        SaveInstances();
        ConfigureInstanceServices(instance.Id);

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            success = true,
            instance = new
            {
                id = instance.Id,
                name = instance.Name,
                minecraftVersion = instance.MinecraftVersion,
                loader = instance.Loader,
                loaderVersion = instance.LoaderVersion,
                ramMb = instance.RamMb,
                modpackProjectId = instance.ModpackProjectId,
                modpackVersionId = instance.ModpackVersionId,
                modpackName = instance.ModpackName,
                javaPath = instance.JavaPath,
                jvmArgs = instance.JvmArgs,
                windowWidth = instance.WindowWidth,
                windowHeight = instance.WindowHeight,
                notes = instance.Notes,
                createdAt = instance.CreatedAt,
                lastUsed = instance.LastUsed,
                path = GetInstanceDirectory(instance.Id)
            }
        });
    }

    private (LauncherInstance Source, LauncherInstance Target, InstanceCopyCategory[] Categories) ParseInstanceCopyRequest(JsonElement obj)
    {
        var sourceId = obj.TryGetProperty("sourceId", out var sourceEl)
            ? sourceEl.GetString()?.Trim() ?? ""
            : _activeInstanceId;
        var targetId = obj.TryGetProperty("targetId", out var targetEl)
            ? targetEl.GetString()?.Trim() ?? ""
            : "";

        var source = _instances.FirstOrDefault(x => x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("A forrás Instance nem található.");
        var target = _instances.FirstOrDefault(x => x.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("A cél Instance nem található.");

        if (source.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A forrás és a cél Instance nem lehet ugyanaz.");

        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (obj.TryGetProperty("categories", out var categoriesEl) && categoriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in categoriesEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    requested.Add(item.GetString()!);
            }
        }

        var categories = InstanceCopyCategories.Where(x => requested.Contains(x.Key)).ToArray();
        if (categories.Length == 0)
            throw new InvalidOperationException("Válassz legalább egy másolandó kategóriát.");

        return (source, target, categories);
    }

    private static IEnumerable<(string SourcePath, string RelativePath, long Length)> EnumerateCopyFiles(
        string sourceRoot, IEnumerable<InstanceCopyCategory> categories)
    {
        var sourceFull = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
        {
            foreach (var relativeDirectory in category.Directories)
            {
                var directory = Path.Combine(sourceRoot, relativeDirectory);
                if (!Directory.Exists(directory))
                    continue;

                var stack = new Stack<string>();
                stack.Push(directory);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        var full = Path.GetFullPath(file);
                        if (!full.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase) || !seen.Add(full))
                            continue;
                        var info = new FileInfo(full);
                        yield return (full, Path.GetRelativePath(sourceRoot, full), info.Exists ? info.Length : 0L);
                    }

                    foreach (var subDirectory in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            if ((File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0)
                                continue;
                        }
                        catch
                        {
                            continue;
                        }
                        stack.Push(subDirectory);
                    }
                }
            }

            foreach (var relativeFile in category.Files)
            {
                var file = Path.Combine(sourceRoot, relativeFile);
                if (!File.Exists(file))
                    continue;
                var full = Path.GetFullPath(file);
                if (!full.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase) || !seen.Add(full))
                    continue;
                var info = new FileInfo(full);
                yield return (full, Path.GetRelativePath(sourceRoot, full), info.Exists ? info.Length : 0L);
            }
        }
    }

    private async System.Threading.Tasks.Task<object?> HandleInstanceCopyPreview(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var request = ParseInstanceCopyRequest(obj);
        var sourceRoot = GetInstanceDirectory(request.Source.Id);
        var targetRoot = GetInstanceDirectory(request.Target.Id);

        var sourceVersion = (request.Source.MinecraftVersion ?? "").Trim();
        var targetVersion = (request.Target.MinecraftVersion ?? "").Trim();
        var versionConflict = sourceVersion.Length > 0 && targetVersion.Length > 0 &&
                              !TurulMC.Core.Mods.MinecraftVersionOrder.IsSame(sourceVersion, targetVersion);
        var targetLoader = (request.Target.Loader ?? "none").Trim().ToLowerInvariant();
        var modsIncluded = request.Categories.Any(category => category.Key.Equals("mods", StringComparison.OrdinalIgnoreCase));

        // A mod-kompatibilitást a CÉL Instance verziójára nézzük: ez mondja meg, melyik
        // átmásolt mod fog elindulni a cél Instance-ban (1.21.11 → 26.2 esetén ez a lényeg).
        TurulMC.Core.Mods.ModScanReport? modScan = null;
        if (modsIncluded)
        {
            modScan = await System.Threading.Tasks.Task.Run(() => TurulMC.Core.Mods.ModCompatibilityScanner.Scan(
                Path.Combine(sourceRoot, "mods"), targetVersion, targetLoader, request.Target.LoaderVersion));
        }

        // World folders can contain tens of thousands of files. Enumerate them
        // away from the UI thread so the launcher stays responsive while the
        // preview counts size/conflicts.
        var preview = await System.Threading.Tasks.Task.Run(() =>
        {
            var files = EnumerateCopyFiles(sourceRoot, request.Categories).ToArray();
            return new
            {
                totalFiles = files.Length,
                totalBytes = files.Sum(file => file.Length),
                conflicts = files.Count(file => File.Exists(Path.Combine(targetRoot, file.RelativePath)))
            };
        });

        var problems = modScan?.Problems ?? Array.Empty<TurulMC.Core.Mods.ModScanEntry>();
        return new
        {
            sourceId = request.Source.Id,
            sourceName = request.Source.Name,
            sourceVersion,
            sourceLoader = request.Source.Loader,
            targetId = request.Target.Id,
            targetName = request.Target.Name,
            targetVersion,
            targetLoader,
            preview.totalFiles,
            preview.totalBytes,
            preview.conflicts,
            versionConflict,
            modsIncluded,
            modTotals = modScan is null
                ? null
                : new
                {
                    total = modScan.Total,
                    compatible = modScan.Compatible,
                    incompatible = modScan.Incompatible,
                    unknown = modScan.Unknown,
                    duplicates = modScan.Duplicates,
                    summary = modScan.SummaryHu
                },
            incompatibleMods = problems.Select(entry => new
            {
                fileName = entry.FileName,
                title = entry.Title,
                status = entry.Status.ToString(),
                statusLabel = StatusLabelHu(entry.Status),
                reason = entry.ReasonHu
            }).ToArray(),
            incompatibleCount = problems.Count
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleInstanceCopyContent(string? data)
    {
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Instance tartalom másolása előtt zárd be a Minecraftot.");

        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var request = ParseInstanceCopyRequest(obj);
        var conflictMode = obj.TryGetProperty("conflictMode", out var conflictEl)
            ? conflictEl.GetString()?.Trim().ToLowerInvariant() ?? "skip"
            : "skip";
        var overwrite = conflictMode == "overwrite";

        // Mod-stratégia verzióütközésnél:
        //  copyAll          → minden modot változatlanul másolunk
        //  skipIncompatible → a célverzióval inkompatibilis modok kimaradnak
        //  updateMods       → a kimaradók helyére a Modrinthről a célverzióhoz illő verzió kerül
        var modStrategy = obj.TryGetProperty("modStrategy", out var strategyEl)
            ? strategyEl.GetString()?.Trim() ?? "copyAll"
            : "copyAll";
        if (modStrategy is not ("copyAll" or "skipIncompatible" or "updateMods")) modStrategy = "copyAll";

        var sourceRoot = GetInstanceDirectory(request.Source.Id);
        var targetRoot = GetInstanceDirectory(request.Target.Id);
        Directory.CreateDirectory(targetRoot);

        var targetVersion = (request.Target.MinecraftVersion ?? "").Trim();
        var targetLoader = (request.Target.Loader ?? "none").Trim().ToLowerInvariant();
        var modsIncluded = request.Categories.Any(category => category.Key.Equals("mods", StringComparison.OrdinalIgnoreCase));

        var excludedModFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedMetaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatibleMods = new List<TurulMC.Core.Mods.ModScanEntry>();
        if (modsIncluded && modStrategy != "copyAll")
        {
            var scan = TurulMC.Core.Mods.ModCompatibilityScanner.Scan(
                Path.Combine(sourceRoot, "mods"), targetVersion, targetLoader, request.Target.LoaderVersion);
            incompatibleMods = scan.Problems.ToList();
            foreach (var entry in incompatibleMods)
            {
                excludedModFiles.Add(entry.FileName);
                excludedModFiles.Add(NormalizeModFileName(entry.FileName));
                // A kihagyott mod metaadatát sem visszük át, különben a cél Instance olyan
                // modot hirdetne, ami nincs is ott (és a takarítás törölné).
                excludedMetaPaths.Add("mods/.turul-meta/" + NormalizeModFileName(entry.FileName) + ".json");
            }
        }

        // Csak a mods mappa kizárt fájljait szűrjük, minden más kategória érintetlen marad.
        var files = EnumerateCopyFiles(sourceRoot, request.Categories)
            .Where(file =>
            {
                if (excludedModFiles.Count == 0) return true;
                var relative = file.RelativePath.Replace('\\', '/');
                if (excludedMetaPaths.Contains(relative)) return false;
                if (!relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) return true;
                return !excludedModFiles.Contains(relative["mods/".Length..]);
            })
            .ToArray();

        var result = await System.Threading.Tasks.Task.Run(() =>
        {
            var copied = 0;
            var skipped = 0;
            long copiedBytes = 0;
            foreach (var file in files)
            {
                var destination = Path.GetFullPath(Path.Combine(targetRoot, file.RelativePath));
                var targetFull = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Érvénytelen célútvonal másolás közben.");

                if (File.Exists(destination) && !overwrite)
                {
                    skipped++;
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);
                File.Copy(file.SourcePath, destination, overwrite);
                copied++;
                copiedBytes += file.Length;
            }
            return (copied, skipped, copiedBytes);
        });

        var modUpdates = new List<object>();
        var modUpdateFailures = new List<object>();
        var modsWithoutUpdate = new List<object>();
        if (modsIncluded && modStrategy == "updateMods" && incompatibleMods.Count > 0)
        {
            var candidates = await ResolveModUpdateTargetsAsync(
                request.Source.Id,
                targetVersion,
                targetLoader,
                incompatibleMods.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            var resolvable = candidates
                .Select(x => (x.FileName, x.ProjectId, x.VersionId))
                .ToArray();

            var install = await InstallModUpdatesAsync(
                metaSourceInstanceId: request.Source.Id,
                targetInstanceId: request.Target.Id,
                targetVersion,
                targetLoader,
                resolvable,
                Path.Combine(targetRoot, "mods-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss")),
                progressScope: "mods");

            modUpdates = install.UpdatedFiles;
            modUpdateFailures = install.Failures;

            var resolvedFiles = candidates.Select(x => NormalizeModFileName(x.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            modsWithoutUpdate = incompatibleMods
                .Where(x => !resolvedFiles.Contains(NormalizeModFileName(x.FileName)))
                .Select(x => (object)new
                {
                    fileName = x.FileName,
                    title = x.Title,
                    statusLabel = StatusLabelHu(x.Status),
                    reason = x.ReasonHu
                })
                .ToList();
        }

        LauncherLogger.Info(
            $"Instance content copied: {request.Source.Name} -> {request.Target.Name}; copied={result.copied}, " +
            $"skipped={result.skipped}, bytes={result.copiedBytes}, modStrategy={modStrategy}, " +
            $"modUpdate={modUpdates.Count}, modSkipped={incompatibleMods.Count}, modFailed={modUpdateFailures.Count}");

        return new
        {
            success = true,
            sourceId = request.Source.Id,
            targetId = request.Target.Id,
            copied = result.copied,
            skipped = result.skipped,
            copiedBytes = result.copiedBytes,
            modStrategy,
            versionConflict = !TurulMC.Core.Mods.MinecraftVersionOrder.IsSame(
                request.Source.MinecraftVersion, request.Target.MinecraftVersion),
            sourceVersion = request.Source.MinecraftVersion,
            targetVersion = request.Target.MinecraftVersion,
            incompatibleMods = incompatibleMods.Select(x => new
            {
                fileName = x.FileName,
                title = x.Title,
                statusLabel = StatusLabelHu(x.Status),
                reason = x.ReasonHu
            }).ToArray(),
            modsUpdated = modUpdates.Count,
            modsUpdateFailures = modUpdateFailures.Count,
            modsWithoutUpdate = modsWithoutUpdate.Count,
            modsWithoutUpdateList = modsWithoutUpdate.Take(20).ToArray(),
            modsExcluded = incompatibleMods.Count,
            modsExcludedList = incompatibleMods.Take(20).Select(x => new
            {
                fileName = x.FileName,
                title = x.Title,
                statusLabel = StatusLabelHu(x.Status),
                reason = x.ReasonHu
            }).ToArray()
        };
    }

    private System.Threading.Tasks.Task<object?> HandleDeleteInstance(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Hiányzó Instance ID.");

        var target = _instances.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return System.Threading.Tasks.Task.FromResult<object?>(new
            {
                success = false,
                error = "Az Instance nem található."
            });

        _instances.Remove(target);

        if (_activeInstanceId.Equals(id, StringComparison.OrdinalIgnoreCase))
            _activeInstanceId = _instances
                .OrderByDescending(x => x.LastUsed)
                .FirstOrDefault()?.Id ?? "";

        SaveInstances();
        ConfigureInstanceServices(_activeInstanceId);

        // Hard verification: after SaveInstances the deleted ID must not exist
        // in memory nor in instances.json.
        var stillInMemory = _instances.Any(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        var stillOnDisk = false;
        if (File.Exists(_instancesFilePath))
        {
            try
            {
                using var verifyDoc = JsonDocument.Parse(File.ReadAllText(_instancesFilePath));
                if (verifyDoc.RootElement.TryGetProperty("instances", out var verifyInstances))
                {
                    stillOnDisk = verifyInstances.EnumerateArray().Any(x =>
                        x.TryGetProperty("id", out var verifyId) &&
                        string.Equals(verifyId.GetString(), id, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Az Instance törlése után az instances.json nem ellenőrizhető.", ex);
            }
        }

        if (stillInMemory || stillOnDisk)
            throw new InvalidOperationException("Az Instance törlése nem sikerült teljesen.");

        LauncherLogger.Info($"Instance végleg törölve: {target.Name} ({target.Id})");

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            success = true,
            deletedId = id,
            activeInstanceId = _activeInstanceId,
            remaining = _instances.Count,
            configExists = File.Exists(_instancesFilePath)
        });
    }

    private System.Threading.Tasks.Task<object?> HandleClearInstances()
    {
        var removed = _instances.Count;
        _instances.Clear();
        _activeInstanceId = "";

        foreach (var path in new[] { _instancesFilePath, _instancesFilePath + ".bak", _instancesFilePath + ".tmp" })
            if (File.Exists(path)) File.Delete(path);

        if (File.Exists(_instancesFilePath))
            throw new IOException("Az instances.json nem törölhető.");

        ConfigureInstanceServices("");
        LauncherLogger.Info($"Minden Instance config törölve. Darabszám: {removed}");

        return System.Threading.Tasks.Task.FromResult<object?>(new
        {
            success = true,
            removed,
            configExists = false
        });
    }

    private async System.Threading.Tasks.Task<object?> HandleDataClear(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var target = obj.TryGetProperty("target", out var targetEl)
            ? targetEl.GetString()?.Trim().ToLowerInvariant() ?? ""
            : "";

        var confirmation = obj.TryGetProperty("confirmation", out var confirmationEl)
            ? confirmationEl.GetString()?.Trim() ?? ""
            : "";

        if (target == "all" &&
            !confirmation.Equals("I CONFIRM", StringComparison.OrdinalIgnoreCase) &&
            !confirmation.Equals("CONFIRM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Gyári visszaállításhoz írd be pontosan: I CONFIRM vagy CONFIRM.");
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TurulMC");

        var settingsPath = Path.Combine(appData, "settings.json");
        var profilesPath = Path.Combine(appData, "profiles.json");
        var logsPath = Path.Combine(appData, "logs");

        var cleared = new List<string>();

        async System.Threading.Tasks.Task ClearProfilesAsync()
        {
            var profiles = (await _authService.GetProfilesAsync()).ToList();
            foreach (var profile in profiles)
                await _authService.DeleteProfileAsync(profile.Id);
            foreach (var path in new[] { profilesPath, profilesPath + ".bak", profilesPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            cleared.Add($"profiles:{profiles.Count}");
        }

        void ClearInstances()
        {
            var count = _instances.Count;
            _instances.Clear();
            _activeInstanceId = "";
            foreach (var path in new[] { _instancesFilePath, _instancesFilePath + ".bak", _instancesFilePath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            ConfigureInstanceServices("");
            cleared.Add($"instances:{count}");
        }

        void ClearSettings()
        {
            foreach (var path in new[] { settingsPath, settingsPath + ".bak", settingsPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);

            // 4.4.4 uses unique transactional temp names for concurrent-save safety.
            // A factory reset must also clean any abandoned settings.json.<guid>.tmp files.
            if (Directory.Exists(appData))
            {
                foreach (var tempPath in Directory.EnumerateFiles(appData, "settings.json.*.tmp"))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            cleared.Add("settings");
        }

        void ClearLogs()
        {
            if (Directory.Exists(logsPath))
            {
                foreach (var file in Directory.EnumerateFiles(logsPath, "*", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            cleared.Add("logs");
        }

        switch (target)
        {
            case "instances":
                ClearInstances();
                break;

            case "profiles":
                await ClearProfilesAsync();
                break;

            case "settings":
                ClearSettings();
                break;

            case "logs":
                ClearLogs();
                break;

            case "all":
                ClearInstances();
                await ClearProfilesAsync();
                ClearSettings();
                ClearLogs();
                break;

            default:
                throw new InvalidOperationException("Ismeretlen Data Clear cél.");
        }

        if ((target is "instances" or "all") && File.Exists(_instancesFilePath))
            throw new IOException("Az Instance config fájl a törlés után is létezik.");

        if ((target is "settings" or "all") && File.Exists(settingsPath))
            throw new IOException("A settings.json a törlés után is létezik.");

        if (target is not ("logs" or "all"))
            LauncherLogger.Info($"Data Clear kész: {target} [{string.Join(", ", cleared)}]");

        return new
        {
            success = true,
            target,
            cleared
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleGetProfiles()
    {
        var profiles = (await _authService.GetProfilesAsync()).ToList();
        // Sérült (név nélküli) profilokat nem küldünk ki: az UI üres kártyát
        // rajzolna név nélkül. Törlés nincs, csak szűrés + log.
        var corrupt = profiles.RemoveAll(p => string.IsNullOrWhiteSpace(p.Username));
        if (corrupt > 0)
            LauncherLogger.Warning($"Névtelen profil kihagyva a listából: {corrupt} db.");
        var settings = await _settingsStorage.LoadSettingsAsync();
        var selectedId = settings.CurrentProfileId;

        // During factory-reset/first-run there are intentionally no profiles.
        // Do not recreate settings.json just because the UI polls getProfiles.
        if (profiles.Count == 0)
        {
            return new
            {
                profiles,
                currentProfileId = ""
            };
        }

        if (string.IsNullOrWhiteSpace(selectedId) ||
            !profiles.Any(p => p.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            selectedId = profiles.OrderByDescending(p => p.LastUsed).First().Id;
            settings.CurrentProfileId = selectedId;
            await _settingsStorage.SaveSettingsAsync(settings);
        }

        return new
        {
            profiles,
            currentProfileId = selectedId
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleCreateProfile(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var username = obj.TryGetProperty("username", out var usernameProp)
            ? usernameProp.GetString() ?? ""
            : obj.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        var result = await _authService.CreateLocalProfileAsync(username);
        if (result.Success && result.Profile != null)
        {
            var settings = await _settingsStorage.LoadSettingsAsync();
            settings.CurrentProfileId = result.Profile.Id;
            await _settingsStorage.SaveSettingsAsync(settings);
        }
        return new
        {
            success = result.Success,
            error = result.ErrorMessage,
            profile = result.Profile
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleRenameProfile(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        var username = obj.TryGetProperty("username", out var usernameProp) ? usernameProp.GetString() ?? "" : "";

        var result = await _authService.RenameProfileAsync(id, username);
        return new
        {
            success = result.Success,
            error = result.ErrorMessage,
            profile = result.Profile
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleDeleteProfile(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            return new { success = false, error = "Hiányzó profil ID." };

        var deleted = await _authService.DeleteProfileAsync(id);
        if (deleted)
        {
            var settings = await _settingsStorage.LoadSettingsAsync();
            if (string.Equals(settings.CurrentProfileId, id, StringComparison.OrdinalIgnoreCase))
            {
                var remaining = (await _authService.GetProfilesAsync()).OrderByDescending(p => p.LastUsed).ToList();
                settings.CurrentProfileId = remaining.FirstOrDefault()?.Id;
                await _settingsStorage.SaveSettingsAsync(settings);
            }
        }
        return new { success = deleted, error = deleted ? "" : "A profil nem található." };
    }

    private async System.Threading.Tasks.Task<object?> HandleSelectProfile(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString()?.Trim() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            return new { success = false, error = "Hiányzó profil ID." };

        var result = await _authService.SelectProfileAsync(id);
        if (!result.Success)
            return new { success = false, error = result.ErrorMessage };

        var settings = await _settingsStorage.LoadSettingsAsync();
        settings.CurrentProfileId = id;
        await _settingsStorage.SaveSettingsAsync(settings);
        return new { success = true, profile = result.Profile };
    }

    private async System.Threading.Tasks.Task<object?> HandleCheckServer(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var host = obj.TryGetProperty("host", out var h) ? h.GetString() ?? "localhost" : "localhost";
        var port = obj.TryGetProperty("port", out var p) ? p.GetInt32() : 25565;

        var status = await _serverStatusService.CheckServerStatusAsync(host, port);
        return new
        {
            online = status.IsOnline,
            players = status.OnlinePlayers,
            maxPlayers = status.MaxPlayers,
            motd = status.Motd,
            version = status.Version
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleServerConnect(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var host = obj.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
        var port = obj.TryGetProperty("port", out var p) && p.TryGetInt32(out var parsedPort) ? parsedPort : 25565;
        if (string.IsNullOrWhiteSpace(host)) return new { success = false, error = "Hiányzik a szerver címe." };

        var settings = await _settingsStorage.LoadSettingsAsync();
        settings.TestServerHost = host;
        settings.TestServerPort = port;
        await _settingsStorage.SaveSettingsAsync(settings);

        return await HandleLaunchGame(JsonSerializer.Serialize(new { serverHost = host, serverPort = port }));
    }

    private async System.Threading.Tasks.Task<object?> HandleServersList()
    {
        var store = new TurulMC.Core.Servers.ServerListStorage();
        return await store.LoadAsync();
    }

    private async System.Threading.Tasks.Task<object?> HandleServersAdd(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var name = obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var host = obj.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
        var port = obj.TryGetProperty("port", out var p) && p.TryGetInt32(out var parsed) ? parsed : 25565;
        var store = new TurulMC.Core.Servers.ServerListStorage();
        return await store.AddAsync(name, host, port);
    }

    private async System.Threading.Tasks.Task<object?> HandleServersRemove(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var key = obj.TryGetProperty("id", out var i) ? i.GetString() ?? "" : obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var store = new TurulMC.Core.Servers.ServerListStorage();
        return new { success = await store.RemoveAsync(key) };
    }

    private static readonly TurulMC.Core.Servers.LocalServerService _localServer = new();

    private async System.Threading.Tasks.Task<object?> HandleLocalServerInstall(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var i) ? i.GetString() ?? "default" : "default";
        var url = obj.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var sha = obj.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
        var flavorStr = obj.TryGetProperty("flavor", out var f) ? f.GetString() ?? "Paper" : "Paper";
        Enum.TryParse<TurulMC.Core.Servers.LocalServerFlavor>(flavorStr, true, out var flavor);
        await _localServer.InstallJarAsync(id, url, sha, flavor);
        return new { success = true, eulaAccepted = _localServer.IsEulaAccepted(id), note = "EULA elfogadása kötelező a modálisban indulás előtt." };
    }

    private async System.Threading.Tasks.Task<object?> HandleLocalServerEula(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var i) ? i.GetString() ?? "default" : "default";
        var accept = obj.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True;
        if (accept) await _localServer.AcceptEulaAsync(id);
        return new { eulaAccepted = _localServer.IsEulaAccepted(id) };
    }

    private async System.Threading.Tasks.Task<object?> HandleLocalServerStart(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var id = obj.TryGetProperty("id", out var i) ? i.GetString() ?? "default" : "default";
        var port = obj.TryGetProperty("port", out var p) && p.TryGetInt32(out var parsed) ? parsed : 25565;
        var settings = await _settingsStorage.LoadSettingsAsync();
        var java = new TurulMC.Core.Java.JavaRuntimeService();
        var rt = await java.GetRecommendedRuntimeAsync("21");
        var javaPath = !string.IsNullOrWhiteSpace(settings.JavaPathOverride) ? settings.JavaPathOverride : rt?.Path ?? "javaw.exe";
        await _localServer.StartAsync(id, javaPath, port);
        return new { success = true };
    }

    private object? HandleLocalServerStop() { _localServer.Stop(); return new { success = true }; }
    private object? HandleLocalServerLog() => new { lines = _localServer.GetLogTail() };
    private object? HandleLocalServerOpen(string? data)
    {
        try
        {
            var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
            var id = obj.TryGetProperty("id", out var i) ? i.GetString() ?? "default" : "default";
            _localServer.OpenFolder(id);
            return new { success = true };
        }
        catch (Exception ex) { return new { success = false, error = ex.Message }; }
    }

    private async System.Threading.Tasks.Task<object?> HandleSyncModpack(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var url = obj.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        var manifest = await _modpackService.GetManifestAsync(url);
        var progress = new Progress<OverallProgress>(p =>
        {
            var current = p.CurrentFile;
            var phasePercent = p.OverallPercentage;
            var currentPercent = current?.TotalBytes > 0 ? current.Percentage : phasePercent;
            var taskLower = (p.CurrentTask ?? string.Empty).ToLowerInvariant();

            // Keep the visible launch bar monotonic-ish across the main Minecraft phases.
            // File-level progress is still shown separately as MB / MB/s.
            double launchPercent;
            if (taskLower.Contains("kliens"))
                launchPercent = 10 + currentPercent * 0.15;
            else if (taskLower.Contains("könyvtár"))
                launchPercent = 25 + phasePercent * 0.30;
            else if (taskLower.Contains("asset"))
                launchPercent = 60 + phasePercent * 0.28;
            else
                launchPercent = Math.Clamp(10 + phasePercent * 0.78, 10, 88);

            TryPostWebMessage(JsonSerializer.Serialize(new
            {
                type = "progress",
                scope = "modpack",
                task = p.CurrentTask,
                percent = Math.Round(Math.Clamp(launchPercent, 0, 100), 1),
                fileName = current?.FileName ?? string.Empty,
                bytesReceived = current?.BytesReceived ?? 0L,
                totalBytes = current?.TotalBytes ?? 0L,
                bytesPerSecond = current?.BytesPerSecond ?? 0d,
                filePercent = current?.Percentage ?? 0d,
                filesCompleted = p.FilesCompleted,
                totalFiles = p.TotalFiles
            }));
        });

        await _modpackService.SyncModpackAsync(manifest, progress);
        return new { success = true, modpackId = manifest.Id, version = manifest.Version };
    }

    private async System.Threading.Tasks.Task<object?> HandleLaunchGame(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");

        var requestedInstanceId = obj.TryGetProperty("instanceId", out var instanceEl)
            ? instanceEl.GetString() ?? ""
            : "";

        var selectedInstance = !string.IsNullOrWhiteSpace(requestedInstanceId)
            ? _instances.FirstOrDefault(x => x.Id.Equals(requestedInstanceId, StringComparison.OrdinalIgnoreCase))
            : _instances.FirstOrDefault(x => x.Id.Equals(_activeInstanceId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(requestedInstanceId) && selectedInstance is null)
            throw new InvalidOperationException("A kiválasztott Instance már nem létezik.");

        if (selectedInstance is null)
            throw new InvalidOperationException("Nincs kiválasztott Instance.");

        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("A Minecraft már fut.");
        _launchAttemptStartedUtc = DateTime.UtcNow;
        _launchAttemptInstanceId = selectedInstance.Id;
        await System.Threading.Tasks.Task.Run(() => _modRecovery.RecoverInterruptedRestore(GetInstanceDirectory(selectedInstance.Id)));
        _activeInstanceId = selectedInstance.Id;
        ConfigureInstanceServices(selectedInstance.Id);
        SaveInstances();

        var version = selectedInstance.MinecraftVersion
            ?? (obj.TryGetProperty("version", out var v)
                ? v.GetString() ?? "26.1.2"
                : obj.TryGetProperty("mcVersion", out var mv) ? mv.GetString() ?? "26.1.2" : "26.1.2");

        var loader = selectedInstance.Loader;

        var requestedLoaderVersion = selectedInstance.LoaderVersion ?? "";
        var loaderVersion = requestedLoaderVersion;

        if (loader == "fabric" && string.IsNullOrWhiteSpace(loaderVersion))
        {
            var available = await _fabricService.GetAvailableLoaderVersionsAsync(version);
            loaderVersion = available
                .FirstOrDefault(v => v.Stable)?.Version
                ?? available.FirstOrDefault()?.Version
                ?? throw new InvalidOperationException("Nem található Fabric Loader ehhez a Minecraft verzióhoz.");
        }
        var profileId = obj.TryGetProperty("profileId", out var pi) ? pi.GetString() ?? "" : "";

        var profiles = (await _authService.GetProfilesAsync()).ToList();
        var profile = string.IsNullOrEmpty(profileId)
            ? await _authService.GetCurrentProfileAsync()
            : profiles.FirstOrDefault(p => p.Id == profileId);

        profile ??= profiles.OrderByDescending(p => p.LastUsed).FirstOrDefault();

        if (profile == null)
            return new { success = false, error = "Nincs kiválasztott Minecraft-profil. Hozz létre vagy válassz ki egy profilt." };

        var settings = await _settingsStorage.LoadSettingsAsync();
        settings.MinecraftVersion = version;
        settings.Loader = loader;
        if (selectedInstance.RamMb > 0)
            settings.DefaultRamMb = selectedInstance.RamMb;

        if (loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
        {
            SendProgress("Fabric Loader verzió feloldása...", 5, "game");

            // Modrinth modpacks can require an exact Fabric Loader version.
            // Keep that version when it is actually available for this Minecraft
            // version; ordinary Instances without a pin still use the launcher
            // recommended loader.
            if (!string.IsNullOrWhiteSpace(requestedLoaderVersion))
            {
                var availableLoaders = await _fabricService.GetAvailableLoaderVersionsAsync(version);
                var exact = availableLoaders.FirstOrDefault(v =>
                    string.Equals(v.Version, requestedLoaderVersion, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    loaderVersion = exact.Version;
                }
                else
                {
                    var resolvedFabric = await _fabricVersionResolver.ResolveRecommendedAsync(version);
                    loaderVersion = resolvedFabric.LoaderVersion;
                    LauncherLogger.Warning(
                        $"A modpack által kért Fabric Loader nem érhető el: {requestedLoaderVersion}; " +
                        $"fallback: {loaderVersion} (Minecraft {version}).");
                }
            }
            else
            {
                var resolvedFabric = await _fabricVersionResolver.ResolveRecommendedAsync(version);
                loaderVersion = resolvedFabric.LoaderVersion;
            }

            settings.LoaderVersion = loaderVersion;
            if (selectedInstance != null)
            {
                selectedInstance.LoaderVersion = loaderVersion;
                selectedInstance.LastUsed = DateTime.UtcNow;
                _activeInstanceId = selectedInstance.Id;
                SaveInstances();
            }
            await _settingsStorage.SaveSettingsAsync(settings);
        }
        else
        {
            settings.LoaderVersion = "";
            selectedInstance.LoaderVersion = "";
            selectedInstance.LastUsed = DateTime.UtcNow;
            SaveInstances();
            await _settingsStorage.SaveSettingsAsync(settings);
        }

        SendProgress("Minecraft fájlok előkészítése...", 10, "game");
        var progress = new Progress<OverallProgress>(p =>
        {
            var current = p.CurrentFile;
            var phasePercent = p.OverallPercentage;
            var currentPercent = current?.TotalBytes > 0 ? current.Percentage : phasePercent;
            var taskLower = (p.CurrentTask ?? string.Empty).ToLowerInvariant();

            double launchPercent;
            if (taskLower.Contains("kliens"))
                launchPercent = 10 + currentPercent * 0.15;
            else if (taskLower.Contains("könyvtár"))
                launchPercent = 25 + phasePercent * 0.30;
            else if (taskLower.Contains("asset"))
                launchPercent = 60 + phasePercent * 0.28;
            else
                launchPercent = Math.Clamp(10 + phasePercent * 0.78, 10, 88);

            TryPostWebMessage(JsonSerializer.Serialize(new
            {
                type = "progress",
                scope = "game",
                task = p.CurrentTask,
                percent = Math.Round(Math.Clamp(launchPercent, 0, 100), 1),
                fileName = current?.FileName ?? string.Empty,
                bytesReceived = current?.BytesReceived ?? 0L,
                totalBytes = current?.TotalBytes ?? 0L,
                bytesPerSecond = current?.BytesPerSecond ?? 0d,
                filePercent = current?.Percentage ?? 0d,
                filesCompleted = p.FilesCompleted,
                totalFiles = p.TotalFiles
            }));
        });

        await _installService.EnsureVersionDownloadedAsync(version, progress);

        if (loader == "fabric")
        {
            SendProgress("Fabric Loader telepítése...", 90, "game");
            await _fabricService.InstallFabricLoaderAsync(version, loaderVersion, progress);
        }
        else if (loader == "neoforge")
        {
            SendProgress("NeoForge telepítése...", 90, "game");
        }

        SendProgress("Indítási konfiguráció készítése...", 97, "game");
        var config = await _launcherService.BuildLaunchConfigAsync(profile, settings);
        config.InstanceId = selectedInstance?.Id ?? _activeInstanceId;

        // Instance-specifikus felülírások (4.6.0): Java útvonal, extra JVM argumentumok,
        // játékablak méret. Mindegyik validált, hogy ne lehessen parancsot injektálni.
        if (selectedInstance is not null && !string.IsNullOrWhiteSpace(selectedInstance.JavaPath))
        {
            TurulMC.Core.Validation.LauncherSettingsValidator.ValidateJavaPath(selectedInstance.JavaPath);
            config.JavaPath = selectedInstance.JavaPath;
            LauncherLogger.Info($"Instance Java felülírás használatban: {selectedInstance.JavaPath}");
        }

        if (selectedInstance is not null)
        {
            var instanceJvmArgs = ParseInstanceJvmArgs(selectedInstance.JvmArgs);
            if (instanceJvmArgs.Length > 0)
                config.AdditionalJvmArgs = instanceJvmArgs.ToList();

            if (selectedInstance.WindowWidth > 0 && selectedInstance.WindowHeight > 0)
            {
                config.WindowWidth = selectedInstance.WindowWidth;
                config.WindowHeight = selectedInstance.WindowHeight;
            }
        }

        var serverHost = obj.TryGetProperty("serverHost", out var sh) ? sh.GetString() ?? "" : "";
        var serverPort = obj.TryGetProperty("serverPort", out var sp) && sp.TryGetInt32(out var parsedServerPort) ? parsedServerPort : 25565;
        if (!string.IsNullOrWhiteSpace(serverHost))
        {
            config.ServerIp = serverHost;
            config.ServerPort = serverPort;
            LauncherLogger.Info($"Quick Play autojoin: {serverHost}:{serverPort}");
        }

        // Ha nincs a verzióhoz illő Java, és az automatikus telepítés be van kapcsolva,
        // a launcher még a játékindítás előtt letölti (Temurin JRE, SHA-256 ellenőrzéssel).
        if (string.IsNullOrWhiteSpace(config.JavaPath) || !File.Exists(config.JavaPath))
        {
            SendProgress($"Java {config.RequiredJavaMajor} előkészítése...", 98, "game");
            var javaProgress = new Progress<TurulMC.Core.Models.DownloadProgress>(p =>
            {
                var percent = p.TotalBytes > 0 ? Math.Clamp(p.Percentage, 0, 100) : 0;
                var task = string.IsNullOrWhiteSpace(p.FileName)
                    ? $"Java {config.RequiredJavaMajor} letöltése..."
                    : $"Java letöltése: {p.FileName}";
                SendProgress(task, percent, "game");
            });
            var runtime = await _javaService.EnsureRuntimeAsync(config.RequiredJavaMajor, settings.AutoInstallJava, javaProgress);
            config.JavaPath = runtime.Path;
            LauncherLogger.Info($"Java runtime kiválasztva indításhoz: {runtime.Path} (Java {runtime.Major}, forrás: {runtime.Source})");
        }

        SendProgress("Minecraft indítása...", 99, "game");
        await _launcherService.LaunchAsync(config);

        var loaderLabel = loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            ? $"Fabric {loaderVersion}".Trim()
            : "Vanilla";
        ShowGameLoadingOverlay(selectedInstance?.Name ?? "Minecraft", settings.GameStartBehavior, settings.Theme, $"MC {version} · {loaderLabel}");

        // Ha a beállítások mentése nem sikerült (pl. vírusirtó blokkolja az AppData írását),
        // az indítás akkor is lefutott — a felhasználót egyszer figyelmeztetjük.
        if (_settingsStorage is TurulMC.Infrastructure.FileSystem.SettingsStorage storage && !storage.LastSaveSucceeded)
            WarnPersistenceProblemOnce(storage.LastError);

        return new
        {
            success = true,
            instanceId = selectedInstance?.Id ?? "",
            profileId = profile.Id,
            username = profile.Username,
            version,
            loader,
            loaderVersion
        };
    }

    /// <summary>
    /// Rendszergazdai kizárás kérése a vírusirtóban (Defender): kizárt folyamat, kizárt
    /// adatmappa és Controlled Folder Access engedélyezett alkalmazás. A UAC ablak
    /// megszakítása NEM hiba: a launcher a tartalék adatkönyvtárban tovább működik.
    /// </summary>
    private async System.Threading.Tasks.Task<object?> HandleDefenderExclusion()
    {
            var exePath = Environment.ProcessPath ?? "";
        var dataRoot = TurulMC.Core.Storage.LauncherPaths.DefaultDataRoot;
        // A rendszergazdai szkript kimenetét fájlba írjuk, hogy hibánál a valódi okot
        // (pl. „Add-MpPreference nem elérhető”, harmadik féltől származó vírusirtó)
        // meg tudjuk jeleníteni a felhasználónak.
        var logPath = Path.Combine(TurulMC.Core.Storage.LauncherPaths.DataRoot, "defender-exclusion.log");
        try
        {
            var script =
                "Add-MpPreference -ExclusionProcess 'TurulMC.Launcher.exe'; " +
                $"Add-MpPreference -ExclusionPath '{dataRoot}'; " +
                (string.IsNullOrWhiteSpace(exePath)
                    ? ""
                    : $"Add-MpPreference -ControlledFolderAccessAllowedApplications '{exePath}'; ");

            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" +
                            script.Replace("\"", "\\\"") + " *> '" + logPath + "'\""
            };

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
                return new { success = false, error = "A rendszergazdai folyamat nem indult el." };

            await process.WaitForExitAsync();
            var writable = TurulMC.Core.Storage.LauncherPaths.IsWritable(dataRoot);
            var detail = ReadExclusionLogTail(logPath);
            LauncherLogger.Info($"Vírusirtó kizárás kérése: exit={process.ExitCode}, megszokott mappa írható={writable}, részletek: {detail}");

            return writable
                ? new { success = true, restartNeeded = true, error = "" }
                : new
                {
                    success = false,
                    error = "A kizárás nem érvényesült. " +
                            (string.IsNullOrWhiteSpace(detail) ? "" : "A Defender válasza: " + detail + " ") +
                            "Ha harmadik féltől származó vírusirtót használsz (Avast, ESET, Kaspersky…), " +
                            "abban kell kivételt adni. A launcher a tartalék adatmappában zavartalanul működik tovább."
                };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 = a felhasználó megszakította a UAC ablakot.
            LauncherLogger.Info("Vírusirtó kizárás kérése megszakítva (UAC).");
            return new
            {
                success = false,
                cancelled = true,
                error = "A rendszergazdai engedélyezést megszakítottad. A launcher így is működik."
            };
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Vírusirtó kizárás kérése sikertelen: " + ex.Message);
            return new
            {
                success = false,
                error = "Nem sikerült a kizárás kérése: " + ex.Message + " A launcher így is működik."
            };
        }
    }

    /// <summary>A rendszergazdai kizárás-szkript naplójának utolsó sorai (rövidítve).</summary>
    private static string ReadExclusionLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return "";
            var lines = File.ReadAllLines(logPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .TakeLast(3)
                .ToArray();
            var text = string.Join(" | ", lines);
            return text.Length > 400 ? text.Substring(0, 400) : text;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Egyszeri, érthető figyelmeztetés arról, hogy a launcher nem tud fájlba menteni
    /// (a leggyakoribb ok: vírusirtó / Controlled Folder Access blokkolja az AppData írását).
    /// </summary>
    private void WarnPersistenceProblemOnce(string? error)
    {
        if (_persistenceWarningShown) return;
        _persistenceWarningShown = true;

        var detail = string.IsNullOrWhiteSpace(error) ? "" : " (" + error + ")";
        LauncherLogger.Warning("Mentési probléma jelzése a felhasználónak" + detail);
        TryPostWebMessage(JsonSerializer.Serialize(new
        {
            action = "toast",
            data = new
            {
                type = "warning",
                message = "A launcher nem tudott fájlba menteni — valószínűleg egy vírusirtó vagy a " +
                          "Windows „Controlled Folder Access” blokkolja a %APPDATA%\\TurulMC mappát. " +
                          "A játék ettől elindul, de a beállításaid nem maradnak meg. " +
                          "Engedélyezd a TurulLaunchert a vírusirtóban, vagy indítsd rendszergazdaként." + detail
            }
        }));
    }

    private void OnMinecraftExited(object? sender, MinecraftProcessExitedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;
                TryPostWebMessage(JsonSerializer.Serialize(new
                {
                    action = "play.state",
                    data = new
                    {
                        state = "idle", exitCode = e.ExitCode, instanceId = e.InstanceId,
                        sessionSeconds = e.SessionSeconds, status = _launcherService.GetStatus()
                    }
                }));
                // Failed starts are explained by the launch request's catch path, once only.
                if (e.ExitCode != 0 && e.WasVerified && !string.IsNullOrEmpty(e.InstanceId))
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    await ExplainFailureAsync(e.InstanceId, e.SessionStartedUtc ?? DateTime.UtcNow,
                        e.OutputTail, e.ExitCode);
                }
            }
            catch (Exception ex) { LauncherLogger.Warning("Kilépés utáni hibaelemzés: " + ex.Message); }
        });
    }

    private void SendProgress(string task, double percent, string scope = "generic")
    {
        TryPostWebMessage(JsonSerializer.Serialize(new
        {
            type = "progress",
            scope,
            task,
            percent
        }));
    }

    private async System.Threading.Tasks.Task<object?> HandleGetInstalledVersions()
    {
        var versions = await _installService.GetInstalledVersionsAsync();
        return versions.ToList();
    }

    private async System.Threading.Tasks.Task<object?> HandleGetAvailableVersions()
    {
        var settings = await _settingsStorage.LoadSettingsAsync();
        var minecraftVersion = string.IsNullOrWhiteSpace(settings.MinecraftVersion)
            ? "26.1.2"
            : settings.MinecraftVersion;

        var versions = await _fabricVersionResolver.GetCompatibleAsync(minecraftVersion);
        var recommended = await _fabricVersionResolver.ResolveRecommendedAsync(minecraftVersion);

        return new
        {
            minecraftVersion,
            recommended = recommended.LoaderVersion,
            loaderVersions = versions
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleGetJavaInfo()
    {
        var runtimes = await _javaService.DetectInstalledRuntimesAsync();
        var recommended = await _javaService.GetRecommendedRuntimeAsync();
        return new
        {
            runtimes = runtimes.Select(r => new { path = r.Path, version = r.Version, valid = r.IsValid }).ToList(),
            recommended = recommended?.Path,
            recommendedVersion = recommended?.Version
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleDetectJava()
    {
        var runtimes = await _javaService.DetectInstalledRuntimesAsync();
        return runtimes.Select(r => new { path = r.Path, version = r.Version, valid = r.IsValid }).ToList();
    }

    private object? HandleOpenFolder(string? data)
    {
        var obj = JsonSerializer.Deserialize<JsonElement>(data ?? "{}");
        var path = obj.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        return new { success = true };
    }

    private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            WebViewSpinner.IsActive = false;
            WebViewStatus.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            await SendInitialState();
            _webViewReadyTcs?.TrySetResult(true);
        }
        else
        {
            WebView.Visibility = Visibility.Collapsed;
            WebViewSpinner.IsActive = false;
            WebViewStatusText.Text = $"A launcher felülete nem tölthető be.\nWebView2: {e.WebErrorStatus}";
            WebViewStatus.Visibility = Visibility.Visible;
            _webViewReadyTcs?.TrySetResult(false);
        }
    }

    private async System.Threading.Tasks.Task SendInitialState()
    {
        try
        {
            var settings = await _settingsStorage.LoadSettingsAsync();
            var profiles = await _authService.GetProfilesAsync();
            var javaInfo = await HandleGetJavaInfo();

            TryPostWebMessage(JsonSerializer.Serialize(new
            {
                type = "init",
                settings,
                profiles,
                javaInfo
            }));
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Failed to send initial state", ex);
        }
    }

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Dragging is handled natively by SetTitleBar(TitleBar).
        // Keep this handler only for XAML compatibility with the existing markup.
    }

    private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private object HandleWindowMinimize()
    {
        HideToTray();
        return new { success = true };
    }

    private object HandleWindowMaximize()
    {
        ToggleMaximize();
        return new { success = true };
    }

    private object HandleWindowClose()
    {
        _ = RequestWindowCloseAsync();
        return new { success = true };
    }

    private async System.Threading.Tasks.Task RequestWindowCloseAsync()
    {
        if (_forceWindowClose)
        {
            Close();
            return;
        }

        var settings = await _settingsStorage.LoadSettingsAsync();
        var behavior = (settings.CloseBehavior ?? "ask").ToLowerInvariant();

        if (behavior is "minimize" or "tray")
        {
            HideToTray();
            return;
        }

        if (behavior == "exit")
        {
            ForceExitLauncher();
            return;
        }

        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "TurulLauncher bezárása",
                Content = "Mit tegyen a launcher?",
                PrimaryButtonText = "Kilépés",
                SecondaryButtonText = "Tálcára rejtés",
                CloseButtonText = "Mégse",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = WebViewHost.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                ForceExitLauncher();
            else if (result == ContentDialogResult.Secondary)
                HideToTray();
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceWindowClose)
            return;

        args.Cancel = true;
        _ = RequestWindowCloseAsync();
    }

    private void ForceExitLauncher()
    {
        _forceWindowClose = true;
        LauncherLogger.Info("Kilépés kérve: a launcher leáll.");
        DisposeTrayIcon();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Ablak bezárása sikertelen a kilépésnél: " + ex.Message);
        }

        // A WinUI nem garantálja, hogy az utolsó ablak bezárásával a folyamat is kilép
        // (a WebView2 háttérfolyamatai életben tarthatják) — ezért rövid várakozás után
        // kényszerítjük a kilépést. Enélkül a launcher "csak eltűnt", de futott tovább.
        try
        {
            var exitTimer = DispatcherQueue.CreateTimer();
            exitTimer.Interval = TimeSpan.FromMilliseconds(350);
            exitTimer.Tick += (_, _) =>
            {
                try { exitTimer.Stop(); } catch { }
                Environment.Exit(0);
            };
            exitTimer.Start();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Betöltőképernyő a játék ablaka helyett: sok moddal a Java processz
    /// percekig tölt — ezalatt élő státusz + log látszik. Ha megjelenik a
    /// játékablak, az overlay magától eltűnik és a beállított viselkedés fut.
    /// </summary>
    private void ShowGameLoadingOverlay(string instanceName, string? startBehavior, string? themeName = null, string? detail = null)
    {
        try { _loadingWindow?.Close(); } catch { }
        _loadingWindow = null;

        var pid = _launcherService.GetStatus().ProcessId;
        var window = new GameLoadingWindow(instanceName, themeName, detail);
        _loadingWindow = window;
        var startedUtc = DateTime.UtcNow;

        try
        {
            window.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
            {
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWin = AppWindow.GetFromWindowId(id);
                if (appWin is not null)
                {
                    const int w = 560, h = 620;
                    var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
                    var work = display.WorkArea;
                    appWin.MoveAndResize(new Windows.Graphics.RectInt32(
                        work.X + Math.Max(0, (work.Width - w) / 2),
                        work.Y + Math.Max(0, (work.Height - h) / 2), w, h));
                    appWin.Title = "Minecraft betöltése…";
                }
            }
        }
        catch (Exception ex) { LauncherLogger.Warning("Betöltőablak méretezés sikertelen: " + ex.Message); }

        EventHandler<string>? outputHandler = null;
        EventHandler<MinecraftProcessExitedEventArgs>? exitHandler = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;

        void Cleanup()
        {
            try { timer?.Stop(); } catch { }
            if (outputHandler is not null) { try { _launcherService.ProcessOutput -= outputHandler; } catch { } }
            if (exitHandler is not null) { try { _launcherService.MinecraftExited -= exitHandler; } catch { } }
        }

        outputHandler = (_, line) =>
        {
            if (_loadingWindow != window) return;
            window.AppendLog(line);
            var friendly = TurulMC.Core.Minecraft.GameLoadingStatus.MapLogLine(line);
            if (friendly is not null)
            {
                window.UpdateStatus(friendly);
                // A naplósorok alapján a fázislista is előrelép (a szöveges állapotból).
                var lower = line.ToLowerInvariant();
                if (lower.Contains("fabric") || lower.Contains("library") || lower.Contains("asset") || lower.Contains("letölt"))
                    window.SetStage(1);
                else if (lower.Contains("launching") || lower.Contains("main") || lower.Contains("java") || lower.Contains("indít"))
                    window.SetStage(2);
            }
        };
        exitHandler = (_, e) =>
        {
            if (_loadingWindow != window) return;
            Cleanup();
            var tail = (e.OutputTail ?? "").Trim();
            if (tail.Length > 600) tail = "…" + tail[^600..];
            window.MarkFailed(string.IsNullOrWhiteSpace(tail) ? $"Kilépési kód: {e.ExitCode}" : tail);
        };
        _launcherService.ProcessOutput += outputHandler;
        _launcherService.MinecraftExited += exitHandler;

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            if (_loadingWindow != window) { Cleanup(); return; }
            window.SetElapsed(DateTime.UtcNow - startedUtc);
            if (!_launcherService.IsGameRunning) return;
            if (pid.HasValue && GameWindowDetector.HasVisibleWindow(pid.Value))
            {
                Cleanup();
                window.MarkLoaded();
                var t = DispatcherQueue.CreateTimer();
                t.Interval = TimeSpan.FromSeconds(3);
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (_loadingWindow == window)
                    {
                        try { window.Close(); } catch { }
                        _loadingWindow = null;
                    }
                    _ = ApplyGameStartBehaviorAsync(startBehavior);
                };
                t.Start();
            }
        };
        timer.Start();
        window.Closed += (_, _) => { if (_loadingWindow == window) { Cleanup(); _loadingWindow = null; } };
    }

    private async System.Threading.Tasks.Task ApplyGameStartBehaviorAsync(string? mode)
    {
        // Give the WebView enough time to receive the successful launch result
        // before the launcher changes its own window state.
        await System.Threading.Tasks.Task.Delay(250);

        DispatcherQueue.TryEnqueue(() =>
        {
            switch ((mode ?? "tray").ToLowerInvariant())
            {
                case "exit":
                    ForceExitLauncher();
                    break;
                case "minimize":
                case "tray":
                    HideToTray();
                    break;
                case "keep":
                default:
                    break;
            }
        });
    }

    private void ToggleMaximize()
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        HandleWindowMinimize();
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        HandleWindowMaximize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        HandleWindowClose();
    }

    private void OnCloseHover(object sender, PointerRoutedEventArgs e)
    {
        BtnClose.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 240, 93, 104));
        BtnClose.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255));
    }

    private void OnCloseLeave(object sender, PointerRoutedEventArgs e)
    {
        BtnClose.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0, 0, 0, 0));
        BtnClose.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 156, 163, 175));
    }
}

public sealed class LauncherUpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("changelog")] public string[]? Changelog { get; set; }
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
}

public class LauncherNewsItem
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("excerpt")] public string Excerpt { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = "";
}

public class WebViewMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public class WebViewResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
