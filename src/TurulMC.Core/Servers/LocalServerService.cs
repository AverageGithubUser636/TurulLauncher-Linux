using System.Diagnostics;
using System.Text.Json;
using TurulMC.Core.Security;

namespace TurulMC.Core.Servers;

public enum LocalServerFlavor { Vanilla, Paper, Purpur }

/// <summary>
/// Self-hosting: separate folder per server, EULA gate, hash check, online-mode=false.
/// </summary>
public sealed class LocalServerService : IDisposable
{
    private readonly string _serversRoot;
    private Process? _process;
    private readonly object _lock = new();
    private readonly List<string> _logTail = new();
    public const long MaxServerJarBytes = 1024L * 1024L * 1024L; // 1 GB cap

    public LocalServerService(string? serversRoot = null)
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurulMC");
        _serversRoot = serversRoot ?? Path.Combine(appData, "local-servers");
        Directory.CreateDirectory(_serversRoot);
    }

    public string GetServerDir(string serverId)
    {
        var safe = new string((serverId ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Érvénytelen szerver ID.");
        return PathSecurity.ResolveInsideRoot(_serversRoot, safe);
    }

    public async Task InstallJarAsync(string serverId, string downloadUrl, string expectedSha256, LocalServerFlavor flavor, CancellationToken ct = default)
    {
        PathSecurity.EnsureSafeHttpsUrl(downloadUrl);
        if (string.IsNullOrWhiteSpace(expectedSha256)) throw new InvalidOperationException("Szerver jar hash kötelező (sha256).");
        var dir = GetServerDir(serverId);
        Directory.CreateDirectory(dir);
        var jarPath = Path.Combine(dir, flavor switch { LocalServerFlavor.Paper => "paper.jar", LocalServerFlavor.Purpur => "purpur.jar", _ => "server.jar" });
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TurulMC-Launcher/4.6.0");
        using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var len = resp.Content.Headers.ContentLength;
        if (len is <= 0 or > MaxServerJarBytes) throw new InvalidOperationException("Szerver jar méret érvénytelen / túl nagy (1 GB cap).");
        var tmp = jarPath + ".part";
        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            using var sha = System.Security.Cryptography.SHA256.Create();
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxServerJarBytes) { output.Close(); File.Delete(tmp); throw new InvalidOperationException("Szerver jar túl nagy."); }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!actual.Equals(expectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            { output.Close(); File.Delete(tmp); throw new InvalidOperationException("Szerver jar hash mismatch — fájl törölve."); }
        }
        if (File.Exists(jarPath)) File.Delete(jarPath);
        File.Move(tmp, jarPath);
        await File.WriteAllTextAsync(Path.Combine(dir, "flavor.json"), JsonSerializer.Serialize(new { flavor = flavor.ToString(), installedAt = DateTime.UtcNow }), ct);
        // Force offline-mode + EULA gate files
        var props = Path.Combine(dir, "server.properties");
        if (!File.Exists(props)) await File.WriteAllTextAsync(props, "online-mode=false\nserver-port=25565\n", ct);
        if (!File.Exists(Path.Combine(dir, "eula.txt"))) await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"), "# EULA kapu: állítsd eula=true-ra a modális elfogadása után\neula=false\n", ct);
    }

    public bool IsEulaAccepted(string serverId) =>
        File.Exists(Path.Combine(GetServerDir(serverId), "eula.txt")) &&
        File.ReadAllText(Path.Combine(GetServerDir(serverId), "eula.txt")).Contains("eula=true");

    public async Task AcceptEulaAsync(string serverId)
    {
        var dir = GetServerDir(serverId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"), "# Elfogadva a launcher EULA-modálisában (" + DateTime.UtcNow.ToString("u") + ")\neula=true\n");
    }

    public bool IsRunning { get { lock (_lock) return _process is not null && !_process.HasExited; } }

    public async Task StartAsync(string serverId, string javaPath, int port = 25565, int ramMb = 2048)
    {
        if (!IsEulaAccepted(serverId)) throw new InvalidOperationException("EULA nincs elfogadva (eula.txt).");
        if (port is < 1 or > 65535) throw new ArgumentException("Port 1–65535.");
        var dir = GetServerDir(serverId);
        var jar = Directory.EnumerateFiles(dir, "*.jar").FirstOrDefault() ?? throw new FileNotFoundException("Szerver jar nincs telepítve.");
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited) throw new InvalidOperationException("A szerver már fut.");
            var psi = new ProcessStartInfo
            {
                FileName = javaPath, WorkingDirectory = dir,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            psi.ArgumentList.Add($"-Xmx{Math.Clamp(ramMb, 512, 65536)}M");
            psi.ArgumentList.Add("-jar"); psi.ArgumentList.Add(jar); psi.ArgumentList.Add("nogui");
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Szerver processz nem indítható.");
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_logTail) { _logTail.Add(e.Data); if (_logTail.Count > 500) _logTail.RemoveAt(0); } };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_logTail) { _logTail.Add("[ERR] " + e.Data); if (_logTail.Count > 500) _logTail.RemoveAt(0); } };
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        }
        await Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_process is null || _process.HasExited) return;
            try { _process.Kill(true); } catch { }
            _process.Dispose(); _process = null;
        }
    }

    public IReadOnlyList<string> GetLogTail() { lock (_logTail) return _logTail.ToArray(); }
    public void OpenFolder(string serverId) =>
        Process.Start(new ProcessStartInfo(GetServerDir(serverId)) { UseShellExecute = true });

    public void Dispose() { try { Stop(); } catch { } }
}
