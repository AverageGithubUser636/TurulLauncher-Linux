using System.Diagnostics;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Logging;

public static class LauncherLogger
{
    // A napló is az írható adatkönyvtárba kerül: ha a %APPDATA%\TurulMC írásvédett
    // (vírusirtó), akkor is legyen napló a diagnosztikához.
    private static readonly string LogDirectory = LauncherPaths.LogsRoot;

    private static readonly object LogLock = new();
    private static string ErrorSequenceDate = "";
    private static int ErrorSequence;

    static LauncherLogger()
    {
        try { Directory.CreateDirectory(LogDirectory); } catch { }
    }

    public static void Info(string message) => WriteLog("INFO", message);
    public static void Warning(string message) => WriteLog("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex}" : message;
        WriteLog("ERROR", msg);
    }
    public static void Debug(string message) => WriteLog("DEBUG", message);

    public static void LogProcessOutput(string data)
    {
        if (!string.IsNullOrEmpty(data))
            WriteLog("MC-OUT", data);
    }

    public static void LogProcessError(string data)
    {
        if (!string.IsNullOrEmpty(data))
            WriteLog("MC-ERR", data);
    }

    private static void WriteLog(string level, string message)
    {
        message = MaskSecrets(message);
        var now = DateTime.Now;
        var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [{level}] {message}";

        lock (LogLock)
        {
            var logFile = Path.Combine(LogDirectory, $"launcher-{now:yyyy-MM-dd}.log");
            try
            {
                File.AppendAllText(logFile, logLine + Environment.NewLine);

                if (level == "ERROR" || level == "MC-ERR")
                {
                    var date = now.ToString("yyyy-MM-dd");
                    if (!string.Equals(ErrorSequenceDate, date, StringComparison.Ordinal))
                    {
                        ErrorSequenceDate = date;
                        ErrorSequence = FindNextErrorSequence(date);
                    }

                    var errorFile = Path.Combine(
                        LogDirectory,
                        $"error.{date}-{ErrorSequence++}.log");

                    File.WriteAllText(
                        errorFile,
                        BuildErrorReport(now, level, message));
                }
            }
            catch
            {
                // A logger hibája nem döntheti el a launchert.
            }
        }

        System.Diagnostics.Debug.WriteLine(logLine);
    }

    private static string MaskSecrets(string message)
    {
        try
        {
            // accessToken / auth_access_token / token=... értékmazkolás
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(--accessToken\s+)(\S+)", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(auth_access_token[=:'\s]+)([A-Za-z0-9\-._~+/=]+)", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            message = System.Text.RegularExpressions.Regex.Replace(message, @"(accessToken[\"":=\s]+)([A-Za-z0-9\-._~+/=]+)", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { }
        return message;
    }

    private static int FindNextErrorSequence(string date)
    {
        try
        {
            var max = 0;
            foreach (var path in Directory.EnumerateFiles(LogDirectory, $"error.{date}-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var dash = name.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(name[(dash + 1)..], out var number))
                    max = Math.Max(max, number);
            }
            return max + 1;
        }
        catch
        {
            return 1;
        }
    }

    private static string BuildErrorReport(DateTime timestamp, string level, string message)
    {
        return
            $"TurulLauncher error report{Environment.NewLine}" +
            $"Date: {timestamp:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
            $"Type: {level}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}{Environment.NewLine}" +
            $".NET: {Environment.Version}{Environment.NewLine}" +
            $"64-bit process: {Environment.Is64BitProcess}{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            message +
            Environment.NewLine;
    }
}
