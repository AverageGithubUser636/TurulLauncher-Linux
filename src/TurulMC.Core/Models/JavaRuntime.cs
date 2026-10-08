namespace TurulMC.Core.Models;

public class JavaRuntime
{
    public string Path { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Java főverzió (8, 17, 21, 25…), a <c>java -version</c> kimenetéből.</summary>
    public int Major { get; set; }

    /// <summary>Honnan származik a runtime: "override" (kézi beállítás), "provisioned"
    /// (launcher telepítette), "system" (gépen talált).</summary>
    public string Source { get; set; } = "system";

    /// <summary>Telepített runtime esetén a launcher-oldali azonosító (pl. temurin-21-x64).</summary>
    public string? Id { get; set; }

    /// <summary>Telepített runtime esetén a kért feature-verzió (pl. 21).</summary>
    public int FeatureVersion { get; set; }

    /// <summary>Névleges szállító (pl. Eclipse Temurin) vagy "System".</summary>
    public string Vendor { get; set; } = "System";
}
