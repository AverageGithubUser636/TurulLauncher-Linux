using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TurulMC.Core.Storage;

namespace TurulMC.Launcher.Avalonia;

/// <summary>
/// Indítási naplózás és krash-riadó. A Windows-os App.xaml.cs megfelelője:
/// ugyanazt a startup-*.log / startup-crash-*.log formátumot írja, hogy a
/// Linuxos fork ugyanazzal az eszközzel diagnosztizálható legyen.
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    private static string LogDirectory
    {
        get
        {
            if (_dir is not null) return _dir;
            try
            {
                _dir = LauncherPaths.LogsRoot;
                Directory.CreateDirectory(_dir);
            }
            catch
            {
                _dir = Path.Combine(Path.GetTempPath(), "turullauncher-logs");
                try { Directory.CreateDirectory(_dir); } catch { }
            }
            return _dir;
        }
    }

    public static void Trace(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"startup-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(LogDirectory, $"startup-crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var sb = new StringBuilder();
            sb.AppendLine("TurulLauncher startup crash (Linux/Avalonia fork)");
            sb.AppendLine($"Version: {AppInfo.Version}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Time: {DateTime.Now:O}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"Runtime: {RuntimeInformation.RuntimeIdentifier}");
            sb.AppendLine($".NET: {Environment.Version}");
            sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "Unknown exception");
            File.WriteAllText(path, sb.ToString());
        }
        catch { }
    }
}

internal static class AppInfo
{
    /// <summary>Egyetlen verzióforrás: az assembly verziója (3 tag).</summary>
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
        return "1.1.0";
    }
}
