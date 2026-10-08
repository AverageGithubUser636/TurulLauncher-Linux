using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public class ModpackManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = "26.1.2";

    [JsonPropertyName("loader")]
    public string Loader { get; set; } = "fabric";

    [JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ModpackFile> Files { get; set; } = new();
}

public class ModpackFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
