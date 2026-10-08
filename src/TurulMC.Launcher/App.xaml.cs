using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using TurulMC.Infrastructure.FileSystem;

namespace TurulMC.Launcher;

public partial class App : Application
{
    // Keep the main window alive for the whole application lifetime.
    private Window? _mainWindow;
    private SplashWindow? _splashWindow;
    internal Window? MainWindowRef => _mainWindow;

    /// <summary>Egyetlen verzióforrás: az assembly verziója (3 tag). Minden
    /// felületi verziócímke (splash, title bar, WebView) innen jön, így nem
    /// maradhat beégetett, elavult szám sehol.</summary>
    internal static string AppVersion { get; } = ResolveAppVersion();

    private static string ResolveAppVersion()
    {
        try
        {
            var v = typeof(App).Assembly.GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
        return "4.6.0";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public App()
    {
        // These handlers are registered before InitializeComponent so very early
        // startup failures can still leave a useful diagnostic file behind.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteStartupCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteStartupCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        UnhandledException += (_, e) =>
        {
            WriteStartupCrash("WinUI.UnhandledException", e.Exception);
            // Let WinUI terminate normally after logging; hiding a fatal startup
            // exception can leave a headless process behind.
            e.Handled = false;
        };

        try
        {
            WriteStartupTrace("App constructor: InitializeComponent begin");
            InitializeComponent();
            WriteStartupTrace("App constructor: InitializeComponent OK");
        }
        catch (Exception ex)
        {
            FatalStartup("A TurulLauncher WinUI inicializálása sikertelen.", ex);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single-instance: 2. indítás előtérbe hoz és kilép. CLI-t nem érinti (külön exe).
        if (!SingleInstance.TryAcquire())
        {
            Exit();
            return;
        }
        var startupTimer = Stopwatch.StartNew();
        try
        {
            // Induláskori öngyógyítás: egy összeomlott/AV által blokkolt mentés után
            // maradhat írásvédett vagy "törlés alatt" állapotú *.tmp fájl az adatkönyvtárban,
            // ami a mentéseket Access denied hibával megbuktatná (2026-10-05-i indítási hiba).
            // A könyvtárat a LauncherPaths oldja fel (ha az APPDATA nem írható, váltunk).
            var dataRoot = TurulMC.Core.Storage.LauncherPaths.DataRoot;
            var cleanedTemps = TurulMC.Core.Storage.AtomicFile.CleanupStaleTemps(dataRoot);
            if (cleanedTemps > 0)
                WriteStartupTrace($"OnLaunched: {cleanedTemps} elárvult temp fájl eltakarítva");
            if (!string.IsNullOrWhiteSpace(TurulMC.Core.Storage.LauncherPaths.DataRootWarning))
                WriteStartupTrace("OnLaunched: " + TurulMC.Core.Storage.LauncherPaths.DataRootWarning);

            WriteStartupTrace("OnLaunched: startup settings load begin");
            var startupSettings = await new SettingsStorage().LoadSettingsAsync();
            WriteStartupTrace($"OnLaunched: startup settings loaded. theme={startupSettings.Theme}, language={startupSettings.Language}");

            WriteStartupTrace("OnLaunched: native splash create begin");
            var splash = new SplashWindow(startupSettings.Theme, startupSettings.Language);
            _splashWindow = splash;
            splash.Activate();
            splash.InitializeAfterActivation();
            splash.SetStatus("TurulLauncher indítása…", 8);
            WriteStartupTrace("OnLaunched: native splash visible");

            await Task.Delay(80);
            splash.SetStatus("Instance-ok és beállítások betöltése…", 20);

            WriteStartupTrace("OnLaunched: MainWindow create begin");
            var window = new MainWindow();
            _mainWindow = window;
            WriteStartupTrace("OnLaunched: MainWindow create OK");

            // WebView2 requires a real HWND. Activate the main window, immediately
            // hide it for preload, and return focus to the splash while startup work runs.
            window.Activate();
            WriteStartupTrace("OnLaunched: MainWindow Activate OK");
            var preloadTask = window.InitializeForSplashAsync((status, progress) =>
            {
                splash.SetStatus(status, progress);
            });
            splash.Activate();

            var ready = await preloadTask;

            // Avoid an ugly blink on very fast machines while never adding a long
            // artificial delay on slower systems.
            const int minimumSplashMilliseconds = 700;
            var remaining = minimumSplashMilliseconds - (int)startupTimer.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining);

            splash.SetStatus(ready ? "Kész — launcher megnyitása…" : "Launcher megnyitása…", 100);
            await Task.Delay(110);

            window.ShowAfterSplash();
            await splash.CloseWithFadeAsync();
            _splashWindow = null;
            WriteStartupTrace("OnLaunched: splash closed; launcher visible");
        }
        catch (Exception ex)
        {
            try { _splashWindow?.Close(); } catch { }
            _splashWindow = null;
            FatalStartup("A TurulLauncher indítása sikertelen.", ex);
            Exit();
        }
    }

    private static string LogDirectory => TurulMC.Core.Storage.LauncherPaths.LogsRoot;

    private static void WriteStartupTrace(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                Path.Combine(LogDirectory, $"startup-{DateTime.Now:yyyy-MM-dd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void WriteStartupCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"startup-crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var sb = new StringBuilder();
            sb.AppendLine("TurulLauncher startup crash");
            sb.AppendLine($"Version: 4.6.0");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Time: {DateTime.Now:O}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NET: {Environment.Version}");
            sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "Unknown exception");
            File.WriteAllText(path, sb.ToString());
        }
        catch { }
    }


    private static string FormatExceptionForDialog(Exception ex)
    {
        var lines = new List<string>();
        Exception? current = ex;
        for (var i = 0; current is not null && i < 4; i++, current = current.InnerException)
        {
            var prefix = i == 0 ? "" : $"Inner {i}: ";
            lines.Add($"{prefix}{current.GetType().Name}: {current.Message} (0x{current.HResult:X8})");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void FatalStartup(string message, Exception ex)
    {
        WriteStartupCrash(message, ex);
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                $"{message}\n\n{FormatExceptionForDialog(ex)}\n\n" +
                $"Napló: %APPDATA%\\TurulMC\\logs",
                "TurulLauncher – indítási hiba",
                0x00000010u);
        }
        catch { }
    }
}
