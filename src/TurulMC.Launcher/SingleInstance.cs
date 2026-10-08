using System.Runtime.InteropServices;

namespace TurulMC.Launcher;

/// <summary>
/// Single-instance: named mutex + wake event. CLI nem blokkolja (csak GUI hívja).
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\TurulMC.Launcher.4x.SingleInstance";
    private const string WakeEventName = @"Global\TurulMC.Launcher.4x.Wake";
    private static Mutex? _mutex;
    private static EventWaitHandle? _wake;
    private static bool _abandonedMutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew) { SignalExisting(); return false; }
        }
        catch (AbandonedMutexException)
        {
            // Az előző launcher összeomlott, ezért a mutex "gazdátlan" maradt. Ilyenkor a
            // mutex a MIÉNK: folytatjuk az indítást (korábban ez kivételt dobott, és a
            // launcher indítási hibával elhalt).
            _abandonedMutex = true;
        }
        catch (UnauthorizedAccessException)
        {
            // Egy másik felhasználó/Credential szintű példány birtokolja a globális mutexet:
            // nem indulunk el másodikként, de nem is halunk el hibaüzenettel.
            return false;
        }

        try
        {
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out _);
            _ = Task.Run(WakeLoop);
        }
        catch { }
        return true;
    }

    private static void SignalExisting()
    {
        try { using var ev = EventWaitHandle.OpenExisting(WakeEventName); ev.Set(); } catch { }
    }

    private static async Task WakeLoop()
    {
        while (true)
        {
            try { _wake?.WaitOne(); } catch { break; }
            await MainThreadRestoreAsync();
        }
    }

    private static async Task MainThreadRestoreAsync()
    {
        try
        {
            var win = (Microsoft.UI.Xaml.Application.Current as App)?.MainWindowRef;
            if (win is null) return;
            win.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
                    ShowWindow(hwnd, 9); // SW_RESTORE
                    SetForegroundWindow(hwnd);
                    win.Activate();
                }
                catch { }
            });
        }
        catch { }
        await Task.CompletedTask;
    }

    public static void Release()
    {
        try
        {
            if (!_abandonedMutex) _mutex?.ReleaseMutex();
        }
        catch { }
        _mutex?.Dispose(); _wake?.Dispose();
        _abandonedMutex = false;
    }
}
