using TurulMC.Core.Models;

namespace TurulMC.Core.Java;

/// <summary>
/// Java runtime automatikus telepítése és nyilvántartása a launcher saját
/// Java könyvtárában (Eclipse Temurin / Adoptium letöltésekkel).
/// </summary>
public interface IJavaRuntimeProvisioner
{
    /// <summary>A telepített Java runtime-ok gyökérkönyvtára.</summary>
    string RuntimeRoot { get; }

    /// <summary>
    /// Letölti és telepíti a kért főverziójú JRE-t, majd visszaadja a nyilvántartott bejegyzést.
    /// </summary>
    /// <param name="featureVersion">Java főverzió (8–99).</param>
    /// <param name="progress">Opcionális haladásjelentés (magyar állapotszövegekkel).</param>
    /// <param name="cancellationToken">Megszakítási token.</param>
    Task<ProvisionedJavaRuntime> ProvisionAsync(int featureVersion, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A nyilvántartott, még létező Java runtime-ok listája.
    /// </summary>
    Task<List<ProvisionedJavaRuntime>> ListProvisionedRuntimesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Eltávolítja a megadott azonosítójú runtime-ot a lemezről és a nyilvántartásból.
    /// </summary>
    /// <returns>Igaz, ha történt törlés; hamis, ha nem volt ilyen bejegyzés vagy érvénytelen az azonosító.</returns>
    Task<bool> RemoveProvisionedRuntimeAsync(string id, CancellationToken cancellationToken = default);
}
