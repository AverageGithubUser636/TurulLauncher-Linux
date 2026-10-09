using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace TurulMC.Launcher.Avalonia;

public partial class App : Application
{
    private Views.ShellWindow? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A Windows-os App.xaml.cs-hez hasonlóan: a korai indulási hibák is
        // hátra hagyjanak egy diagnosztikai fájlt.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StartupLog.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        StartupLog.Trace($"OnFrameworkInitializationCompleted: {RuntimeInformationOs}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                // Splash-csel indulunk (mint a Windows-verzió): a nehéz
                // inicializálás folyamatjelzővel fut, és csak utána jön a shell.
                var splash = new Views.SplashWindow();
                desktop.MainWindow = splash;
                splash.Show();
                desktop.ShutdownRequested += (_, _) => StartupLog.Trace("ShutdownRequested");

                _ = StartupAsync(desktop, splash);
            }
            catch (Exception ex)
            {
                StartupLog.WriteCrash("Indítás", ex);
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop, Views.SplashWindow splash)
    {
        try
        {
            // 1) Takarítás.
            splash.ReportProgress(5, "Indítás…");
            var dataRoot = Core.Storage.LauncherPaths.DataRoot;
            var cleaned = await Task.Run(() => Core.Storage.AtomicFile.CleanupStaleTemps(dataRoot));
            if (cleaned > 0) StartupLog.Trace($"OnLaunched: {cleaned} elárvult temp fájl eltakarítva");
            if (!string.IsNullOrWhiteSpace(Core.Storage.LauncherPaths.DataRootWarning))
                StartupLog.Trace("OnLaunched: " + Core.Storage.LauncherPaths.DataRootWarning);

            // 2) Beállítások + téma.
            splash.ReportProgress(22, "Beállítások betöltése…");
            var settings = await Services.LauncherServices.Current.LoadSettingsAsync();
            await Task.Run(() => Dispatcher.UIThread.Invoke(() =>
            {
                Services.ThemeService.Current.Apply(settings.Theme);
                if (!string.IsNullOrWhiteSpace(settings.CustomAccent))
                    Services.ThemeService.Current.ApplyCustomAccent(settings.CustomAccent);
                Services.ThemeService.Current.SetAnimationsEnabled(settings.AnimationsEnabled);
            }));
            StartupLog.Trace($"OnLaunched: téma={settings.Theme}");

            // 3) Instance-ok előtöltése (a nézetek saját listát építenek,
            // de a hibás fájlt itt jelezzük).
            splash.ReportProgress(55, "Instance-ok betöltése…");
            var (activeId, instances) = await Task.Run(
                () => Services.LauncherServices.Current.Instances.Load());
            StartupLog.Trace($"OnLaunched: {instances.Count} instance, aktív={activeId}");
            await Task.Delay(150);

            // 4) Felület.
            splash.ReportProgress(80, "Felület felépítése…");
            _shell = await Task.Run(() => Dispatcher.UIThread.Invoke(() =>
            {
                var shell = new Views.ShellWindow();
                desktop.MainWindow = shell;
                shell.Show();
                return shell;
            }));
            StartupLog.Trace("MainWindow létrehozva");

            splash.ReportProgress(100, "Kész.");
            await Task.Delay(250);
            splash.Close();
        }
        catch (Exception ex)
        {
            StartupLog.WriteCrash("StartupAsync", ex);
            try
            {
                splash.ReportProgress(100, "Indítási hiba: " + ex.Message);
                await Task.Delay(2500);
            }
            catch { }
            desktop.Shutdown(1);
        }
    }

    private static string RuntimeInformationOs =>
        System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}
