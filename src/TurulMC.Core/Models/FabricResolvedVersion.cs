namespace TurulMC.Core.Models;

public sealed class FabricResolvedVersion
{
    public string MinecraftVersion { get; init; } = string.Empty;
    public string LoaderVersion { get; init; } = string.Empty;
    public bool Stable { get; init; }
    public string? Maven { get; init; }
}
