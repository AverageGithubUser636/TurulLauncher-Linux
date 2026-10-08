using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public class MinecraftVersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersion? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<MinecraftVersionInfo> Versions { get; set; } = new();
}

public class LatestVersion
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

public class MinecraftVersionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("releaseTime")]
    public string ReleaseTime { get; set; } = string.Empty;
}

public class MinecraftVersionDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public Downloads? Downloads { get; set; }

    [JsonPropertyName("libraries")]
    public List<MinecraftLibrary> Libraries { get; set; } = new();

    [JsonPropertyName("assetIndex")]
    public AssetIndex? AssetIndex { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("arguments")]
    public System.Text.Json.JsonElement Arguments { get; set; }

    [JsonPropertyName("javaVersion")]
    public MinecraftJavaVersion? JavaVersion { get; set; }
}

public class Downloads
{
    [JsonPropertyName("client")]
    public DownloadEntry? Client { get; set; }
}

public class DownloadEntry
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public class MinecraftLibrary
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("rules")]
    public List<LibraryRule>? Rules { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }
}

public class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadEntry? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadEntry>? Classifiers { get; set; }
}

public class LibraryRule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }
}

public class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class AssetIndex
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class MinecraftJavaVersion
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}
