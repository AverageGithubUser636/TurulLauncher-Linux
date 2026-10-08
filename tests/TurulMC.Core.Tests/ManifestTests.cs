using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Modpacks;

namespace TurulMC.Core.Tests;

public class ManifestTests
{
    [Fact]
    public void Deserialize_ModpackManifest_ParsesCorrectly()
    {
        var json = """
        {
            "id": "turulmc-dev",
            "version": "1.0.0-dev",
            "minecraftVersion": "1.20.1",
            "loader": "fabric",
            "loaderVersion": "0.15.7",
            "files": [
                {
                    "path": "mods/example.jar",
                    "url": "http://localhost:8080/mods/example.jar",
                    "sha256": "abc123",
                    "size": 1024
                },
                {
                    "path": "config/settings.toml",
                    "url": "http://localhost:8080/config/settings.toml",
                    "sha256": "def456",
                    "size": 256
                }
            ]
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModpackManifest>(json);

        Assert.NotNull(manifest);
        Assert.Equal("turulmc-dev", manifest.Id);
        Assert.Equal("1.0.0-dev", manifest.Version);
        Assert.Equal("1.20.1", manifest.MinecraftVersion);
        Assert.Equal("fabric", manifest.Loader);
        Assert.Equal("0.15.7", manifest.LoaderVersion);
        Assert.Equal(2, manifest.Files.Count);
    }

    [Fact]
    public void Deserialize_ModpackFile_ParsesCorrectly()
    {
        var json = """
        {
            "path": "mods/sodium.jar",
            "url": "http://localhost:8080/mods/sodium.jar",
            "sha256": "aabbccdd",
            "size": 2048
        }
        """;

        var file = JsonSerializer.Deserialize<ModpackFile>(json);

        Assert.NotNull(file);
        Assert.Equal("mods/sodium.jar", file.Path);
        Assert.Equal("http://localhost:8080/mods/sodium.jar", file.Url);
        Assert.Equal("aabbccdd", file.Sha256);
        Assert.Equal(2048, file.Size);
    }

    [Fact]
    public void Deserialize_EmptyFilesList_ParsesCorrectly()
    {
        var json = """
        {
            "id": "empty-pack",
            "version": "0.0.1",
            "minecraftVersion": "1.20.1",
            "loader": "fabric",
            "loaderVersion": "0.15.7",
            "files": []
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModpackManifest>(json);

        Assert.NotNull(manifest);
        Assert.Empty(manifest.Files);
    }

    [Fact]
    public void Deserialize_MissingOptionalFields_UsesDefaults()
    {
        var json = """
        {
            "id": "minimal",
            "version": "1.0.0",
            "minecraftVersion": "1.20.1",
            "files": []
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModpackManifest>(json);

        Assert.NotNull(manifest);
        Assert.Equal("fabric", manifest.Loader);
        Assert.Equal(string.Empty, manifest.LoaderVersion);
    }

    [Fact]
    public void Serialize_ModpackManifest_RoundTrips()
    {
        var original = new ModpackManifest
        {
            Id = "test",
            Version = "1.0.0",
            MinecraftVersion = "1.20.1",
            Loader = "fabric",
            LoaderVersion = "0.15.7",
            Files = new List<ModpackFile>
            {
                new() { Path = "mods/test.jar", Url = "http://example.com/test.jar", Sha256 = "hash", Size = 100 }
            }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ModpackManifest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Files.Count, deserialized.Files.Count);
        Assert.Equal(original.Files[0].Path, deserialized.Files[0].Path);
    }

    [Fact]
    public void ManifestDiff_NoChanges_EmptyDiff()
    {
        var diff = new ModpackDiff();
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.FilesToAdd);
        Assert.Empty(diff.FilesToUpdate);
        Assert.Empty(diff.FilesToRemove);
    }

    [Fact]
    public void ManifestDiff_WithChanges_HasChanges()
    {
        var diff = new ModpackDiff
        {
            FilesToAdd = { new ModpackFile { Path = "new.jar" } }
        };
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void Deserialize_MinecraftVersionManifest_ParsesVersions()
    {
        var json = """
        {
            "latest": { "release": "1.20.1", "snapshot": "23w31a" },
            "versions": [
                { "id": "1.20.1", "type": "release", "url": "https://example.com/v1", "releaseTime": "2023-06-12" },
                { "id": "23w31a", "type": "snapshot", "url": "https://example.com/v2", "releaseTime": "2023-08-02" }
            ]
        }
        """;

        var manifest = JsonSerializer.Deserialize<MinecraftVersionManifest>(json);

        Assert.NotNull(manifest);
        Assert.Equal("1.20.1", manifest.Latest?.Release);
        Assert.Equal("23w31a", manifest.Latest?.Snapshot);
        Assert.Equal(2, manifest.Versions.Count);
    }
}
