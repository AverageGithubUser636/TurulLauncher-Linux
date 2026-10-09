using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public class LauncherSettings
{
    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = "26.1.2";

    [JsonPropertyName("loader")]
    public string Loader { get; set; } = "none";

    [JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; set; } = "";

    [JsonPropertyName("modpackManifestUrl")]
    public string ModpackManifestUrl { get; set; } = "";

    [JsonPropertyName("testServerHost")]
    public string TestServerHost { get; set; } = "localhost";

    [JsonPropertyName("testServerPort")]
    public int TestServerPort { get; set; } = 25565;

    [JsonPropertyName("defaultRamMb")]
    public int DefaultRamMb { get; set; } = 2048;

    [JsonPropertyName("javaPathOverride")]
    public string? JavaPathOverride { get; set; }

    /// <summary>Ha igaz, a launcher a Minecraft-verzióhoz illő Java runtime-ot
    /// (Eclipse Temurin JRE) magától letölti és telepíti, ha nincs megfelelő.</summary>
    [JsonPropertyName("autoInstallJava")]
    public bool AutoInstallJava { get; set; } = true;

    /// <summary>Opcionális gyökérkönyvtár a launcher által telepített Java runtime-oknak.
    /// Üresen hagyva: %APPDATA%\TurulMC\java.</summary>
    [JsonPropertyName("javaRuntimeRoot")]
    public string? JavaRuntimeRoot { get; set; }

    [JsonPropertyName("currentProfileId")]
    public string? CurrentProfileId { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "yellow";

    /// <summary>Egyedi akcentusszín (#RRGGBB), vagy üres (témaszín).</summary>
    [JsonPropertyName("customAccent")]
    public string CustomAccent { get; set; } = "";

    /// <summary>Saját háttérkép fájlneve a DataRoot/backgrounds mappában, vagy üres.</summary>
    [JsonPropertyName("backgroundImage")]
    public string BackgroundImage { get; set; } = "";

    /// <summary>Ablak-átlátszóság: 0.4–1.0 (1.0 = átlátszatlan).</summary>
    [JsonPropertyName("windowOpacity")]
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>Felületi animációk (hover-átmenetek) ki/bekapcsolása.</summary>
    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    [JsonPropertyName("performanceMode")]
    public string PerformanceMode { get; set; } = "full";

    [JsonPropertyName("uiScalePercent")]
    public int UiScalePercent { get; set; } = 100;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "hu";

    [JsonPropertyName("firstRunCompleted")]
    public bool FirstRunCompleted { get; set; }

    [JsonPropertyName("tutorialCompleted")]
    public bool TutorialCompleted { get; set; }

    [JsonPropertyName("updateChecksEnabled")]
    public bool UpdateChecksEnabled { get; set; } = true;

    [JsonPropertyName("updateChannel")]
    public string UpdateChannel { get; set; } = "stable";

    [JsonPropertyName("closeBehavior")]
    public string CloseBehavior { get; set; } = "ask";

    [JsonPropertyName("gameStartBehavior")]
    public string GameStartBehavior { get; set; } = "tray";

}

