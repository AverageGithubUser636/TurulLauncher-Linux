using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public sealed class LauncherInstance
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = "26.1.2";

    [JsonPropertyName("loader")]
    public string Loader { get; set; } = "none";

    [JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; set; } = string.Empty;

    [JsonPropertyName("ramMb")]
    public int RamMb { get; set; } = 4096;

    /// <summary>Instance-specifikus java.exe felülírás. Üres = automatikus (verzióhoz illő Java).</summary>
    [JsonPropertyName("javaPath")]
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>Instance-specifikus extra JVM argumentumok (szóközzel elválasztva, validáltan).</summary>
    [JsonPropertyName("jvmArgs")]
    public string JvmArgs { get; set; } = string.Empty;

    /// <summary>Játékablak szélessége pixelben. 0 = a Minecraft saját beállítása.</summary>
    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; }

    /// <summary>Játékablak magassága pixelben. 0 = a Minecraft saját beállítása.</summary>
    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; }

    /// <summary>Szabad szöveges megjegyzés az Instance-hez (pl. mi van benne).</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("modpackProjectId")]
    public string ModpackProjectId { get; set; } = string.Empty;

    [JsonPropertyName("modpackVersionId")]
    public string ModpackVersionId { get; set; } = string.Empty;

    [JsonPropertyName("modpackName")]
    public string ModpackName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastUsed")]
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}
