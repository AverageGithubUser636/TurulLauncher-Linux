using Microsoft.UI.Windowing;
using System;
using System.IO;
using System.Runtime.InteropServices;
using TurulMC.Core.Logging;

namespace TurulMC.Launcher;

public sealed partial class MainWindow
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_APP = 0x8000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_NULL = 0x0000;
    private const uint TrayCallbackMessage = WM_APP + 0x451;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TrayCommandShow = 0x5101;
    private const uint TrayCommandExit = 0x5102;

    private static readonly UIntPtr TraySubclassId = new(0x54555255u); // "TURU"

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr _trayHwnd;
    private IntPtr _trayIconHandle;
    private bool _trayIconVisible;
    private bool _traySubclassInstalled;
    private bool _hiddenToTray;
    private bool _trayTransition;
    private bool _trayDisabled;
    private int _trayFailureCount;
    private DateTime _suppressAutoHideUntilUtc;
    private uint _taskbarCreatedMessage;

    private void InitializeTrayIcon()
    {
        if (_traySubclassInstalled)
            return;

        try
        {
            _trayHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_trayHwnd == IntPtr.Zero)
                throw new InvalidOperationException("A launcher HWND-je még nem érhető el.");

            // Keep the callback delegate alive for the complete window lifetime.
            EnsureTraySubclassDelegate();
            if (!SetWindowSubclass(_trayHwnd, _traySubclassDelegateHolder!, TraySubclassId, UIntPtr.Zero))
                throw new InvalidOperationException($"SetWindowSubclass failed ({Marshal.GetLastWin32Error()}).");

            _traySubclassInstalled = true;
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "turullauncher.ico");
            if (File.Exists(iconPath))
            {
                _trayIconHandle = LoadImage(
                    IntPtr.Zero,
                    iconPath,
                    IMAGE_ICON,
                    0,
                    0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            LauncherLogger.Info("MainWindow: native Win32 tray initialized.");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Tray ikon inicializálása sikertelen: {ex.Message}");
            DisposeTrayIcon();
        }
    }

    // Retain the unmanaged callback delegate for the full window lifetime.
    private SubclassProc? _traySubclassDelegateHolder;

    private void EnsureTraySubclassDelegate()
    {
        _traySubclassDelegateHolder ??= TrayWindowProc;
    }

    private void ShowTrayIcon()
    {
        if (_trayIconVisible || !_traySubclassInstalled || _trayHwnd == IntPtr.Zero)
            return;

        var data = CreateTrayData();
        if (Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _trayIconVisible = true;
        }
        else
        {
            LauncherLogger.Warning($"Shell_NotifyIcon(NIM_ADD) failed ({Marshal.GetLastWin32Error()}).");
        }
    }

    private void HideTrayIcon()
    {
        if (!_trayIconVisible || _trayHwnd == IntPtr.Zero)
            return;

        var data = CreateTrayData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayIconVisible = false;
    }

    private NOTIFYICONDATA CreateTrayData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _trayHwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _trayIconHandle,
            szTip = "TurulLauncher",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr TrayWindowProc(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        try
        {
            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                // Explorer restart removes notification icons. Re-add ours when the
                // launcher is currently hidden to tray.
                _trayIconVisible = false;
                if (_hiddenToTray)
                    ShowTrayIcon();
                return IntPtr.Zero;
            }

            if (message == TrayCallbackMessage)
            {
                var trayMessage = unchecked((uint)lParam.ToInt64());
                if (trayMessage == WM_LBUTTONUP || trayMessage == WM_LBUTTONDBLCLK)
                {
                    DispatcherQueue.TryEnqueue(RestoreFromTray);
                    return IntPtr.Zero;
                }

                if (trayMessage == WM_RBUTTONUP || trayMessage == WM_CONTEXTMENU)
                {
                    ShowTrayContextMenu();
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Tray window message hiba: {ex.Message}");
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowTrayContextMenu()
    {
        if (_trayHwnd == IntPtr.Zero)
            return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MF_STRING, new UIntPtr(TrayCommandShow), "TurulLauncher megnyitása");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, new UIntPtr(TrayCommandExit), "Kilépés");

            if (!GetCursorPos(out var point))
                return;

            // Required by TrackPopupMenu so clicking elsewhere dismisses the menu.
            SetForegroundWindow(_trayHwnd);
            var command = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X,
                point.Y,
                0,
                _trayHwnd,
                IntPtr.Zero);

            if (command == TrayCommandShow)
                DispatcherQueue.TryEnqueue(RestoreFromTray);
            else if (command == TrayCommandExit)
                DispatcherQueue.TryEnqueue(ForceExitLauncher);

            PostMessage(_trayHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HideToTray()
    {
        LauncherLogger.Info($"HideToTray hívás: hidden={_hiddenToTray} transition={_trayTransition} disabled={_trayDisabled} suppress={(_suppressAutoHideUntilUtc > DateTime.UtcNow)}");
        if (_forceWindowClose || _hiddenToTray) return;

        // Ha a tálca ikon korábban nem volt létrehozható, nem próbálkozunk tovább:
        // ilyenkor normál minimalizálást végzünk (nem indítunk végtelen ciklust).
        if (_trayDisabled)
        {
            try
            {
                if (_appWindow?.Presenter is OverlappedPresenter p &&
                    p.State != OverlappedPresenterState.Minimized)
                    p.Minimize();
            }
            catch { }
            return;
        }

        // Újrabelépés-védelem: a ShowWindow/presenter hívások maguk is AppWindow.Changed
        // eseményt váltanak ki, ami korábban végtelen "rejtés → visszaállítás →
        // minimalizálás" ciklust okozott (az ablak ide-oda ugrált kicsinyített és
        // teljes méret között). Egy tranzakció alatt nem indulhat újabb.
        if (_trayTransition) return;

        // Rövid türelmi idő: közvetlenül egymás után érkező minimize eseményeket kizárunk.
        if (DateTime.UtcNow < _suppressAutoHideUntilUtc) return;

        _trayTransition = true;
        try
        {
            InitializeTrayIcon();

            if (!_traySubclassInstalled)
                throw new InvalidOperationException("A natív tray nem inicializálható.");

            // FONTOS: itt NEM állítjuk vissza az ablakot. A visszaállítás a
            // RestoreFromTray dolga; a régi kód itt Restore()-t hívott, ami minden
            // ciklusban újra megjelenítette az ablakot (ez volt a villogás oka).
            ShowTrayIcon();
            if (!_trayIconVisible)
                throw new InvalidOperationException("A tray ikon nem hozható létre.");

            // Legalább 1,2 másodpercig ne induljon újabb automatikus rejtés.
            _suppressAutoHideUntilUtc = DateTime.UtcNow.AddSeconds(1.2);
            _hiddenToTray = true;
            _trayFailureCount = 0;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SW_HIDE);

            LauncherLogger.Info("MainWindow: hidden to native system tray.");
        }
        catch (Exception ex)
        {
            _trayFailureCount++;
            LauncherLogger.Warning($"Tray-re rejtés sikertelen ({_trayFailureCount}. próba): {ex.Message}");
            _hiddenToTray = false;
            HideTrayIcon();

            // Két egymást követő hiba után feladjuk az automatikus tálcára rejtést:
            // ilyenkor egyetlen, normál minimalizálást végzünk, és nem próbálkozunk tovább
            // (a további próbálkozás volt a végtelen villogás másik forrása).
            _trayDisabled = _trayFailureCount >= 2;
            _suppressAutoHideUntilUtc = DateTime.UtcNow.AddSeconds(3);
            if (_trayDisabled)
                LauncherLogger.Warning("A tálcára rejtés letiltva erre a munkamenetre (a tálca ikon nem hozható létre).");

            if (_appWindow?.Presenter is OverlappedPresenter presenter &&
                presenter.State != OverlappedPresenterState.Minimized)
            {
                try { presenter.Minimize(); } catch { }
            }
        }
        finally
        {
            _trayTransition = false;
        }
    }

    private void RestoreFromTray()
    {
        if (_forceWindowClose) return;
        if (_trayTransition) return;

        _trayTransition = true;
        try
        {
            if (_appWindow?.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            }

            _hiddenToTray = false;
            _suppressAutoHideUntilUtc = DateTime.UtcNow.AddSeconds(1.2);
            HideTrayIcon();
            Activate();
            LauncherLogger.Info("MainWindow: restored from native system tray.");
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning($"Tray-ből visszaállítás sikertelen: {ex.Message}");
        }
        finally
        {
            _trayTransition = false;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_forceWindowClose || _hiddenToTray) return;
        if (_trayTransition || _trayDisabled) return;
        if (DateTime.UtcNow < _suppressAutoHideUntilUtc) return;

        if (sender.Presenter is not OverlappedPresenter presenter ||
            presenter.State != OverlappedPresenterState.Minimized)
            return;

        DispatcherQueue.TryEnqueue(HideToTray);
    }

    private void DisposeTrayIcon()
    {
        try
        {
            HideTrayIcon();

            if (_traySubclassInstalled && _trayHwnd != IntPtr.Zero && _traySubclassDelegateHolder is not null)
            {
                RemoveWindowSubclass(_trayHwnd, _traySubclassDelegateHolder, TraySubclassId);
            }

            _traySubclassInstalled = false;
            _trayHwnd = IntPtr.Zero;
            _traySubclassDelegateHolder = null;

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }
        }
        catch
        {
            // Best-effort cleanup during shutdown.
        }
    }
}
