using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TurulMC.Core.Logging;

namespace TurulMC.Launcher.Avalonia.Services;

/// <summary>
/// Rendszertálca-ikon (Ayatana/AppIndicator, GNOME-bővítmény, KDE, XFCE).
/// Ha a tálca nem elérhető, minden művelet csendben minimalizálásra esik
/// vissza — a launcher ilyenkor is teljesen használható marad.
/// Nem szálbiztos: csak UI-szálról hívható (a hívók ablak-események).
/// </summary>
public static class TrayManager
{
    private static TrayIcon? _tray;
    private static Window? _trayedWindow;
    private static bool _transition;

    /// <summary>Igaz, ha egy ablak épp tálcára van téve.</summary>
    public static bool IsTrayed => _tray is not null && _trayedWindow is not null;

    /// <summary>
    /// Ablak elrejtése a tálcára. Sikertelenség (nincs tálca) esetén
    /// minimalizál, és hamissal tér vissza.
    /// </summary>
    public static bool HideToTray(Window window)
    {
        if (_transition) return IsTrayed;
        _transition = true;
        try
        {
            if (IsTrayed && ReferenceEquals(_trayedWindow, window))
                return true;

            EnsureTray(window);
            if (_tray is null)
            {
                window.WindowState = WindowState.Minimized;
                return false;
            }

            _trayedWindow = window;
            window.Hide();
            return true;
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Tálcára tevés hiba: " + ex.Message);
            try { window.WindowState = WindowState.Minimized; } catch { }
            return false;
        }
        finally
        {
            _transition = false;
        }
    }

    /// <summary>
    /// Tálcáról vagy minimalizálásból visszaállítás (ablak mutatása).
    /// </summary>
    public static void Restore(Window? window)
    {
        if (window is null) return;
        if (_transition) return;
        _transition = true;
        try
        {
            if (IsTrayed && ReferenceEquals(_trayedWindow, window))
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                try { window.Activate(); } catch { }
                DisposeTray();
            }
            else if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
                try { window.Activate(); } catch { }
            }
            else if (!window.IsVisible)
            {
                window.Show();
                try { window.Activate(); } catch { }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Tálcáról visszaállítás hiba: " + ex.Message);
        }
        finally
        {
            _transition = false;
        }
    }

    /// <summary>Tálca-ikon megszüntetése (pl. ablak zárásakor).</summary>
    public static void DisposeTray()
    {
        try
        {
            var app = Application.Current;
            var icons = app is null ? null : TrayIcon.GetIcons(app);
            if (_tray is not null && icons is not null)
                icons.Remove(_tray);
        }
        catch { }
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        _trayedWindow = null;
    }

    private static void EnsureTray(Window window)
    {
        var app = Application.Current;
        if (_tray is not null || app is null) return;
        var icons = TrayIcon.GetIcons(app);
        if (icons is null) return;

        var icon = LoadIcon();
        if (icon is null) return;

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "TurulLauncher"
        };

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Megnyitás");
        open.Click += (_, _) => Dispatcher.UIThread.Post(() => Restore(window));
        var exit = new NativeMenuItem("Kilépés");
        exit.Click += (_, _) => Dispatcher.UIThread.Post(() => RequestExit(window));
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);
        tray.Menu = menu;
        tray.Clicked += (_, _) => Dispatcher.UIThread.Post(() => Restore(window));

        icons.Add(tray);
        _tray = tray;
    }

    private static void RequestExit(Window window)
    {
        try
        {
            DisposeTray();
            if (window is Views.ShellWindow shell)
                shell.ForceClose();
            else
                window.Close();
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Tálcás kilépés hiba: " + ex.Message);
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            // Aktuális téma-logó, tartalék az alapszínű.
            foreach (var uri in new[]
            {
                ThemeService.Current.LogoUri,
                "avares://TurulLauncher/Assets/Logos/turul-logo.png"
            })
            {
                try
                {
                    using var stream = AssetLoader.Open(new Uri(uri));
                    return new WindowIcon(stream);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Tálca-ikon betöltési hiba: " + ex.Message);
        }
        return null;
    }
}
