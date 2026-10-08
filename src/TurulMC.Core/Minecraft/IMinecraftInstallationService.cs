using TurulMC.Core.Models;

namespace TurulMC.Core.Minecraft;

public interface IMinecraftInstallationService
{
    Task<MinecraftVersionDetail> GetVersionDetailAsync(string versionId);
    Task<string> GetClientJarPathAsync(string versionId);
    Task EnsureVersionDownloadedAsync(string versionId, IProgress<OverallProgress>? progress = null);
    Task<bool> ValidateVersionFilesAsync(string versionId);
    Task<IEnumerable<string>> GetInstalledVersionsAsync();
    string GetInstanceDirectory();
    string GetVersionDirectory(string versionId);
    string GetLibrariesDirectory();
    string GetAssetsDirectory();
}
