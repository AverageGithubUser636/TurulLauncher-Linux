namespace TurulMC.Core.Models;

/// <summary>
/// A launcher által automatikusan telepített (Eclipse Temurin) Java runtime leírója.
/// </summary>
public class ProvisionedJavaRuntime
{
    /// <summary>Runtime azonosító, pl. "temurin-21-x64".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Szállító neve (alapértelmezés: Eclipse Temurin).</summary>
    public string Vendor { get; set; } = "Eclipse Temurin";

    /// <summary>Java főverzió, pl. 21.</summary>
    public int FeatureVersion { get; set; }

    /// <summary>Teljes verziószám, pl. "21.0.5+11".</summary>
    public string FullVersion { get; set; } = string.Empty;

    /// <summary>Architektúra: x64, x86 vagy aarch64.</summary>
    public string Architecture { get; set; } = "x64";

    /// <summary>Indításhoz használt futtatható állomány, pl. ...\bin\javaw.exe.</summary>
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>Konzolos futtatható állomány, pl. ...\bin\java.exe (lehet üres).</summary>
    public string JavaExePath { get; set; } = string.Empty;

    /// <summary>A runtime telepítési könyvtára.</summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>A telepítés forrása (letöltési URL).</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>A telepített csomag SHA-256 lenyomata.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>A letöltött csomag mérete bájtban.</summary>
    public long PackageBytes { get; set; }

    /// <summary>A telepítés időpontja (UTC).</summary>
    public DateTimeOffset InstalledAtUtc { get; set; }
}
