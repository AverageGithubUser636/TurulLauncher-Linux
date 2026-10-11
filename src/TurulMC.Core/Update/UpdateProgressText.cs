namespace TurulMC.Core.Update;

/// <summary>
/// Updater-folyamat szövegformázás (sebesség, hátralévő idő, méret) —
/// a Windows-updater koncepciója szerint:
/// <c>812 MB / 1,20 GB • 24,6 MB/s • 16 mp</c>.
/// Tisztán számoló segéd, közvetlenül tesztelhető.
/// </summary>
public static class UpdateProgressText
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
              + $" {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "—";
        return FormatBytes((long)bytesPerSecond) + "/s";
    }

    public static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || remainingBytes <= 0) return "—";
        var seconds = (long)Math.Ceiling(remainingBytes / bytesPerSecond);
        if (seconds < 60) return $"{seconds} mp";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} p {seconds % 60} mp";
        return $"{minutes / 60} ó {minutes % 60} p";
    }

    /// <summary>Teljes állapot-sor: letöltött / összes • sebesség • ETA.</summary>
    public static string FormatDownload(long received, long total, double bytesPerSecond)
    {
        var size = total > 0
            ? $"{FormatBytes(received)} / {FormatBytes(total)}"
            : FormatBytes(received);
        if (bytesPerSecond <= 0 || total <= 0) return size;
        var eta = FormatEta(Math.Max(0, total - received), bytesPerSecond);
        return $"{size} • {FormatSpeed(bytesPerSecond)} • {eta}";
    }
}
