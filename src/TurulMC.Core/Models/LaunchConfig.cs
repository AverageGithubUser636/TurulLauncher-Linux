using System.Text.Json;

namespace TurulMC.Core.Models;

public class LaunchConfig
{
    public string Username { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;
    public string AccessToken { get; set; } = "0";
    public string UserType { get; set; } = "legacy";
    public string Version { get; set; } = "26.1.2";
    public string InstanceId { get; set; } = string.Empty;
    public string VersionType { get; set; } = "release";
    public string GameDirectory { get; set; } = string.Empty;
    public string AssetsDirectory { get; set; } = string.Empty;
    public string NativesDirectory { get; set; } = string.Empty;
    public string JavaPath { get; set; } = string.Empty;
    public int RequiredJavaMajor { get; set; } = 25;

    /// <summary>Ha igaz és nincs megfelelő Java a gépen, a launcher a
    /// Minecraft-verzióhoz illő Temurin JRE-t magától letölti indítás előtt.</summary>
    public bool AutoInstallJava { get; set; } = true;
    public int MinMemoryMb { get; set; } = 512;
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>Játékablak szélessége pixelben (0 = a Minecraft saját beállítása).</summary>
    public int WindowWidth { get; set; }

    /// <summary>Játékablak magassága pixelben (0 = a Minecraft saját beállítása).</summary>
    public int WindowHeight { get; set; }
    public string? ServerIp { get; set; }
    public int ServerPort { get; set; } = 25565;
    public string? AssetIndexId { get; set; }
    public string? MainClass { get; set; }
    public List<string> ClassPath { get; set; } = new();
    public List<string> AdditionalJvmArgs { get; set; } = new();
    public Dictionary<string, string> SystemProperties { get; set; } = new();
    public JsonElement VersionArguments { get; set; }
}
