using System.Diagnostics;
using TurulMC.Core.Logging;

namespace TurulMC.Launcher.Avalonia.Services;

/// <summary>
/// Játékablak-érzékelés Linuxon (a Windows-os GameWindowDetector megfelelője).
/// Egyszerű, best-effort megközelítés külső eszközökkel:
/// <list type="bullet">
/// <item><c>wmctrl -lp</c> — egy hívással listázza az ablakokat PID-del,</item>
/// <item><c>xdotool search --pid</c> — tartalék.</item>
/// </list>
/// Ha egyik eszköz sincs telepítve (vagy tiszta Wayland van X-eszközök
/// nélkül), mindig <c>false</c>-t ad — ilyenkor a betöltőablak a játék
/// kilépéséig vagy kézi elrejtésig marad.
/// </summary>
public static class GameWindowDetector
{
    private static int _toolState; // 0 = ismeretlen, 1 = wmctrl, 2 = xdotool, -1 = nincs

    public static bool HasVisibleWindow(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var tool = DetectTool();
            return tool switch
            {
                1 => CheckWmctrl(pid),
                2 => CheckXdotool(pid),
                _ => false
            };
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Ablak-érzékelés hiba (pid {pid}): {ex.Message}");
            return false;
        }
    }

    private static int DetectTool()
    {
        if (_toolState != 0) return _toolState;
        _toolState = ExistsOnPath("wmctrl") ? 1
            : ExistsOnPath("xdotool") ? 2
            : -1;
        if (_toolState < 0)
            LauncherLogger.Info("Nincs wmctrl/xdotool — a játékablak automatikus érzékelése kikapcsolva.");
        return _toolState;
    }

    private static bool ExistsOnPath(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(Path.PathSeparator)
                .Any(dir => File.Exists(Path.Combine(dir, name)));
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckWmctrl(int pid)
    {
        // Kimenet: "0x01200003  0 12345  host  ablakcím"
        var output = Run("wmctrl", "-lp", 4000);
        if (string.IsNullOrWhiteSpace(output)) return false;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[2], out var windowPid) && windowPid == pid)
                return true;
        }
        return false;
    }

    private static bool CheckXdotool(int pid)
    {
        var output = Run("xdotool", $"search --pid {pid}", 4000);
        return !string.IsNullOrWhiteSpace(output);
    }

    private static string Run(string file, string args, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            return "";
        }
        return process.ExitCode == 0 ? output : "";
    }
}
