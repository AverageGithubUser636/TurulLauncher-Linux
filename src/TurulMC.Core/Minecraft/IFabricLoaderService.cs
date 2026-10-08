using TurulMC.Core.Models;

namespace TurulMC.Core.Minecraft;

public interface IFabricLoaderService
{
    Task InstallFabricLoaderAsync(string minecraftVersion, string loaderVersion, IProgress<OverallProgress>? progress = null);
    Task<bool> IsFabricInstalledAsync(string minecraftVersion, string loaderVersion);
    Task<string> GetFabricJarPathAsync(string minecraftVersion, string loaderVersion);
    Task<List<FabricLoaderVersion>> GetAvailableLoaderVersionsAsync(string minecraftVersion);
}
