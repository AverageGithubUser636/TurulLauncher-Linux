using TurulMC.Core.Models;

namespace TurulMC.Core.Java;

public interface IJavaRuntimeService
{
    /// <summary>Telepített (rendszerben talált) és a launcher által telepített Java runtime-ok,
    /// főverzió szerint csökkenő sorrendben.</summary>
    Task<List<JavaRuntime>> DetectInstalledRuntimesAsync(CancellationToken cancellationToken = default);

    /// <summary>A megadott Java követelményhez (pl. "21") legjobban illő runtime, vagy null.</summary>
    Task<JavaRuntime?> GetRecommendedRuntimeAsync(string? requiredVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Biztosít egy működő Java runtime-ot a kért főverzióhoz. Ha nincs és
    /// <paramref name="allowInstall"/> igaz, a launcher letölti (Eclipse Temurin JRE).
    /// Ellenkező esetben <see cref="InvalidOperationException"/>-t dob magyar, használható üzenettel.</summary>
    Task<JavaRuntime> EnsureRuntimeAsync(
        int requiredMajor,
        bool allowInstall,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string?> GetConfiguredJavaPathAsync();
    Task SetJavaPathAsync(string path);
    bool ValidateJavaPath(string path);
}
