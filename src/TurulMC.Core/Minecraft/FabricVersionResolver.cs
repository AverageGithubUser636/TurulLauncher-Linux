using System.Text.Json;
using TurulMC.Core.Http;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;

namespace TurulMC.Core.Minecraft;

/// <summary>
/// Resolves Fabric Loader versions from Fabric Meta for one exact Minecraft version.
/// The Fabric Meta endpoint returns compatible versions newest-first; TurulLauncher
/// deliberately ignores stale cached/settings loader versions when "recommended"
/// resolution is requested.
/// </summary>
public sealed class FabricVersionResolver : IFabricVersionResolver
{
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private readonly LauncherHttpClient _httpClient = new();

    public async Task<IReadOnlyList<FabricLoaderVersion>> GetCompatibleAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("A Minecraft verzió nem lehet üres.", nameof(minecraftVersion));

        var encodedVersion = Uri.EscapeDataString(minecraftVersion.Trim());
        var url = $"{FabricMetaBase}/versions/loader/{encodedVersion}";

        LauncherLogger.Info($"Fabric resolver: kompatibilis loaderek lekérése: {minecraftVersion}");

        var json = await _httpClient.GetStringAsync(url);
        var entries = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(json)
                      ?? new List<FabricLoaderEntry>();

        var compatible = entries
            .Where(x => x.Loader is not null && !string.IsNullOrWhiteSpace(x.Loader.Version))
            .Select(x => new FabricLoaderVersion
            {
                Version = x.Loader!.Version,
                Stable = x.Loader.Stable
            })
            .ToList();

        if (compatible.Count == 0)
            throw new InvalidOperationException(
                $"A Fabric Meta nem adott kompatibilis Fabric Loadert a Minecraft {minecraftVersion} verzióhoz.");

        return compatible;
    }

    public async Task<FabricResolvedVersion> ResolveRecommendedAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("A Minecraft verzió nem lehet üres.", nameof(minecraftVersion));

        var encodedVersion = Uri.EscapeDataString(minecraftVersion.Trim());
        var url = $"{FabricMetaBase}/versions/loader/{encodedVersion}";
        var json = await _httpClient.GetStringAsync(url);

        var entries = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(json)
                      ?? new List<FabricLoaderEntry>();

        // Fabric Meta documents this endpoint as newest-first. Prefer the first
        // stable result. Only fall back to the newest non-stable result when the
        // API currently exposes no stable loader for that exact MC version.
        var selected = entries.FirstOrDefault(x =>
                           x.Loader is not null &&
                           x.Loader.Stable &&
                           !string.IsNullOrWhiteSpace(x.Loader.Version))
                       ?? entries.FirstOrDefault(x =>
                           x.Loader is not null &&
                           !string.IsNullOrWhiteSpace(x.Loader.Version));

        if (selected?.Loader is null)
            throw new InvalidOperationException(
                $"Nincs használható Fabric Loader a Minecraft {minecraftVersion} verzióhoz.");

        // Safety floor for the Java-25 / Minecraft 26.x generation. Older loaders
        // contain ASM versions that cannot read Java 25 (class major 69) game classes.
        if (IsMinecraft26OrNewer(minecraftVersion) &&
            CompareNumericVersion(selected.Loader.Version, "0.19.0") < 0)
        {
            var modern = entries.FirstOrDefault(x =>
                x.Loader is not null &&
                x.Loader.Stable &&
                CompareNumericVersion(x.Loader.Version, "0.19.0") >= 0);

            if (modern?.Loader is null)
                throw new InvalidOperationException(
                    $"A Fabric Meta nem kínál Java 25-kompatibilis stabil loadert a Minecraft {minecraftVersion} verzióhoz.");

            selected = modern;
        }

        LauncherLogger.Info(
            $"Fabric resolver: Minecraft {minecraftVersion} -> Fabric Loader {selected.Loader.Version} " +
            $"(stable={selected.Loader.Stable})");

        return new FabricResolvedVersion
        {
            MinecraftVersion = minecraftVersion,
            LoaderVersion = selected.Loader.Version,
            Stable = selected.Loader.Stable,
            Maven = selected.Loader.Maven
        };
    }

    private static bool IsMinecraft26OrNewer(string version)
    {
        var first = version.Split('.', '-', '+')[0];
        return int.TryParse(first, out var major) && major >= 26;
    }

    private static int CompareNumericVersion(string left, string right)
    {
        static int[] Parse(string value)
        {
            var core = value.Split('-', '+')[0];
            return core.Split('.')
                .Select(x => int.TryParse(x, out var n) ? n : 0)
                .ToArray();
        }

        var a = Parse(left);
        var b = Parse(right);
        var count = Math.Max(a.Length, b.Length);

        for (var i = 0; i < count; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            var cmp = av.CompareTo(bv);
            if (cmp != 0) return cmp;
        }

        return 0;
    }
}
