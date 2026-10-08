using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public class FabricLoaderManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("libraries")]
    public List<MinecraftLibrary> Libraries { get; set; } = new();

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public class FabricLoaderVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}

public class FabricLoaderEntry
{
    [JsonPropertyName("loader")]
    public FabricLoaderInfo? Loader { get; set; }
}

public class FabricLoaderInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    [JsonPropertyName("maven")]
    public string? Maven { get; set; }
}

public class FabricGameVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    [JsonPropertyName("urls")]
    public FabricUrls? Urls { get; set; }
}

public class FabricUrls
{
    [JsonPropertyName("installer")]
    public FabricUrlEntry? Installer { get; set; }

    [JsonPropertyName("launcher")]
    public FabricUrlEntry? Launcher { get; set; }
}

public class FabricUrlEntry
{
    [JsonPropertyName("maven")]
    public string? Maven { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
