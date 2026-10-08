using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Storage;

namespace TurulMC.Core.Servers;

/// <summary>
/// servers.json storage with lock + traversal-safe paths.
///
/// Az alapértelmezett útvonal a platformfüggő adatkönyvtár
/// (<see cref="LauncherPaths.DataRoot"/>/servers.json): Windowson
/// %APPDATA%\TurulMC, Linuxon $XDG_DATA_HOME/TurulMC. Korábban a
/// %APPDATA% volt beégetve, ami Linuxon ~/.config/TurulMC-be mutatott —
/// így a szerverlista nem az adatkönyvtárba került volna.
/// </summary>
public sealed class ServerListStorage
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ServerListStorage(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(LauncherPaths.DataRoot, "servers.json");
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>A ténylegesen használt fájlútvonal (tesztekhez).</summary>
    public string FilePath => _filePath;

    public async Task<List<SavedServer>> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_filePath)) return new();
            var list = JsonSerializer.Deserialize<List<SavedServer>>(await File.ReadAllTextAsync(_filePath)) ?? new();
            return list.Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList();
        }
        catch { return new(); }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(List<SavedServer> servers)
    {
        await _lock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var result = Storage.AtomicFile.TryWriteAllText(
                _filePath, JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.Success)
                throw new IOException(result.Error);
        }
        finally { _lock.Release(); }
    }

    public async Task<SavedServer> AddAsync(string name, string host, int port)
    {
        name = (name ?? "").Trim();
        host = (host ?? "").Trim();
        if (name.Length is < 1 or > 48) throw new ArgumentException("Szerver név 1–48 karakter.");
        if (host.Length is < 1 or > 253 || host.Any(char.IsWhiteSpace)) throw new ArgumentException("Érvénytelen host.");
        if (port is < 1 or > 65535) throw new ArgumentException("Port 1–65535.");
        var servers = await LoadAsync();
        if (servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Már létezik ilyen nevű szerver.");
        var entry = new SavedServer { Id = Guid.NewGuid().ToString("N"), Name = name, Host = host, Port = port, LastUsed = DateTime.UtcNow };
        servers.Add(entry);
        await SaveAsync(servers);
        return entry;
    }

    public async Task<bool> RemoveAsync(string key)
    {
        var servers = await LoadAsync();
        var removed = servers.RemoveAll(s => s.Id.Equals(key, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) await SaveAsync(servers);
        return removed;
    }
}
