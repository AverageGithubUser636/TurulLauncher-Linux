using System.Diagnostics;
using System.Runtime.InteropServices;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Diagnostics;

/// <summary>GPU-driver felderítés eredménye (Nouveau-figyelmeztetéshez).</summary>
/// <param name="IsLinux">Linuxon fut-e a launcher (máshol nincs értelme).</param>
/// <param name="NouveauDetected">Nouveau nyílt NVIDIA driver aktív.</param>
/// <param name="Renderer">OpenGL renderer szöveg, ha kideríthető (pl. glxinfo).</param>
/// <param name="DetectionMethod">Melyik próba találta: env, sys-module,
/// proc-modules, lspci, glxinfo, renderer, none, unsupported-platform.</param>
public sealed record GpuDriverReport(
    bool IsLinux,
    bool NouveauDetected,
    string Renderer,
    string DetectionMethod);

/// <summary>
/// Nouveau (nyílt forrású NVIDIA) driver észlelése Linuxon.
/// A Minecraft + Nouveau páros gyakran fagy, villog vagy lassú —
/// ezért a launcher figyelmeztet, de <b>az indítást engedi</b>.
/// Minden próba best-effort: hiányzó eszköz, timeout vagy hiba esetén
/// a következő próba jön, kivétel soha nem szökik ki.
/// </summary>
public static class GpuDriverInfo
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static GpuDriverReport? _cached;
    private static DateTime _cachedAtUtc = DateTime.MinValue;
    private static readonly object CacheLock = new();

    /// <summary>Gyorsítótárazott felderítés (5 perc).</summary>
    public static async Task<GpuDriverReport> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        lock (CacheLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheLifetime)
                return _cached;
        }

        var report = await ProbeUncachedAsync(cancellationToken).ConfigureAwait(false);

        lock (CacheLock)
        {
            _cached = report;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return report;
    }

    internal static async Task<GpuDriverReport> ProbeUncachedAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            return new GpuDriverReport(false, false, "", "unsupported-platform");

        try
        {
            // 1. Explicit Mesa-override (pl. DRI_PRIME helyett kézi kényszerítés).
            if (string.Equals(
                    Environment.GetEnvironmentVariable("MESA_LOADER_DRIVER_OVERRIDE"),
                    "nouveau", StringComparison.OrdinalIgnoreCase))
                return new GpuDriverReport(true, true, "", "env");

            // 2. Betöltött kernelmodul (eszköz nélkül, fájlolvasással).
            if (Directory.Exists("/sys/module/nouveau"))
                return new GpuDriverReport(true, true, await ReadRendererAsync(cancellationToken)
                    .ConfigureAwait(false), "sys-module");

            try
            {
                if (File.Exists("/proc/modules") &&
                    ProcModulesHasNouveau(await File.ReadAllTextAsync(
                        "/proc/modules", cancellationToken).ConfigureAwait(false)))
                    return new GpuDriverReport(true, true, await ReadRendererAsync(cancellationToken)
                        .ConfigureAwait(false), "proc-modules");
            }
            catch { }

            // 3. PCI-eszközhöz kötött driver (pciutils, ha van).
            var lspci = await RunToolAsync("lspci", "-k", cancellationToken)
                .ConfigureAwait(false);
            if (LspciUsesNouveau(lspci))
                return new GpuDriverReport(true, true, await ReadRendererAsync(cancellationToken)
                    .ConfigureAwait(false), "lspci");

            // 4. OpenGL renderer (mesa-utils, ha van).
            var renderer = await ReadRendererAsync(cancellationToken).ConfigureAwait(false);
            if (RendererLooksLikeNouveau(renderer))
                return new GpuDriverReport(true, true, renderer, "glxinfo");

            return new GpuDriverReport(true, false, renderer,
                string.IsNullOrWhiteSpace(renderer) ? "none" : "renderer");
        }
        catch
        {
            return new GpuDriverReport(true, false, "", "none");
        }
    }

    /// <summary>Csak Nouveau, ha a renderer-szövegben a "nouveau" szerepel.</summary>
    public static bool RendererLooksLikeNouveau(string? renderer)
        => !string.IsNullOrWhiteSpace(renderer) &&
           renderer.Contains("nouveau", StringComparison.OrdinalIgnoreCase);

    /// <summary>Az <c>lspci -k</c> kimenetében van
    /// <c>Kernel driver in use: nouveau</c> sor.</summary>
    public static bool LspciUsesNouveau(string? lspciOutput)
    {
        if (string.IsNullOrWhiteSpace(lspciOutput)) return false;
        foreach (var line in lspciOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Kernel driver in use:",
                    StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains("nouveau", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A <c>/proc/modules</c> tartalmaz <c>nouveau</c> sort.</summary>
    public static bool ProcModulesHasNouveau(string? procModules)
    {
        if (string.IsNullOrWhiteSpace(procModules)) return false;
        foreach (var line in procModules.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("nouveau ",
                    StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("nouveau", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<string> ReadRendererAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunToolAsync("glxinfo", "-B", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output)) return "";
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("OpenGL renderer string:",
                    StringComparison.OrdinalIgnoreCase))
                    return trimmed["OpenGL renderer string:".Length..].Trim();
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"GPU renderer olvasási hiba: {ex.Message}");
        }
        return "";
    }

    private static async Task<string?> RunToolAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Hiányzó eszköz (pl. nincs lspci/glxinfo) — nem hiba, csak nincs adat.
            return null;
        }
    }
}
