namespace TurulMC.Core.Models;

public class DownloadProgress
{
    public string FileName { get; set; } = string.Empty;
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100 : 0;
    public string Status { get; set; } = string.Empty;
}

public class OverallProgress
{
    public string CurrentTask { get; set; } = string.Empty;
    public int FilesCompleted { get; set; }
    public int TotalFiles { get; set; }
    public double OverallPercentage => TotalFiles > 0 ? (double)FilesCompleted / TotalFiles * 100 : 0;
    public DownloadProgress? CurrentFile { get; set; }
}
