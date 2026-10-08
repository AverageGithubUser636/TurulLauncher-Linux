using TurulMC.Core.Models;

namespace TurulMC.Core.Modpacks;

public interface IModpackService
{
    Task<ModpackManifest> GetManifestAsync(string manifestUrl);
    Task SyncModpackAsync(ModpackManifest manifest, IProgress<OverallProgress>? progress = null);
    Task<bool> ValidateModpackAsync(ModpackManifest manifest);
    Task<ModpackDiff> GetDiffAsync(ModpackManifest manifest);
    Task RemoveObsoleteFilesAsync(ModpackManifest manifest);
}

public class ModpackDiff
{
    public List<ModpackFile> FilesToAdd { get; set; } = new();
    public List<ModpackFile> FilesToUpdate { get; set; } = new();
    public List<string> FilesToRemove { get; set; } = new();
    public bool HasChanges => FilesToAdd.Count > 0 || FilesToUpdate.Count > 0 || FilesToRemove.Count > 0;
}
