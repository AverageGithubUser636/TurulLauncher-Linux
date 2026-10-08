using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Modpacks;

namespace TurulMC.Core.Tests;

public sealed class ModpackSafetyTests
{
    [Fact]
    public async Task Diff_DoesNotRemoveUserFiles_WhenNoManagedIndexExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "turul-modpack-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "my-own-mod.jar"), "user");
            var service = new ModpackService(root);
            var diff = await service.GetDiffAsync(new ModpackManifest { Id = "test", Version = "1" });
            Assert.Empty(diff.FilesToRemove);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Diff_RemovesOnlyPreviouslyManagedObsoleteFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "turul-modpack-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "old-managed.jar"), "managed");
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "my-own-mod.jar"), "user");
            await File.WriteAllTextAsync(
                Path.Combine(root, ".turul-managed-files.json"),
                JsonSerializer.Serialize(new[] { "mods/old-managed.jar" }));

            var service = new ModpackService(root);
            var diff = await service.GetDiffAsync(new ModpackManifest { Id = "test", Version = "2" });

            Assert.Contains(diff.FilesToRemove, x => x.Replace('\\', '/') == "mods/old-managed.jar");
            Assert.DoesNotContain(diff.FilesToRemove, x => x.Contains("my-own-mod", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
