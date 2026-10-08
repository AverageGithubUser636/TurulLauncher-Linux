using TurulMC.Core.Models;

namespace TurulMC.Core.Minecraft;

public interface IFabricVersionResolver
{
    Task<FabricResolvedVersion> ResolveRecommendedAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FabricLoaderVersion>> GetCompatibleAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default);
}
