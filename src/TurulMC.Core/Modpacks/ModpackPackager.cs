using System.IO.Compression;
using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Security;

namespace TurulMC.Core.Modpacks;

/// <summary>
/// Modpack package / hash / serve helpers (CLI + GUI shared).
/// </summary>
public static class ModpackPackager
{
    public static string ComputeManifestHash(ModpackManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });
        return TurulMC.Core.Security.Sha256Service.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static async Task ExportAsync(ModpackManifest manifest, string instanceDir, string outputMrpack)
    {
        if (manifest.Files.Count == 0) throw new InvalidOperationException("Üres manifest nem exportálható.");
        foreach (var f in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Sha256)) throw new InvalidOperationException($"Hiányzó hash: {f.Path} — export blokkolva.");
            PathSecurity.ResolveInsideRoot(instanceDir, f.Path);
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputMrpack));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        using var zip = ZipFile.Open(outputMrpack, ZipArchiveMode.Create);
        foreach (var f in manifest.Files)
        {
            var full = PathSecurity.ResolveInsideRoot(instanceDir, f.Path);
            if (!File.Exists(full)) throw new FileNotFoundException("Hiányzó fájl exportnál.", full);
            zip.CreateEntryFromFile(full, "overrides/" + f.Path.Replace('\\', '/'));
        }
        var indexEntry = zip.CreateEntry("modrinth.index.json");
        await using var w = new StreamWriter(indexEntry.Open());
        await w.WriteAsync(JsonSerializer.Serialize(new { name = manifest.Id, versionId = manifest.Version, gameVersions = new[] { manifest.MinecraftVersion } }));
    }

    public static async Task<ModpackManifest> ImportMrpackAsync(string mrpackPath, string instanceDir)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var index = zip.Entries.FirstOrDefault(e => e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Nem érvényes .mrpack: hiányzik modrinth.index.json.");
        foreach (var entry in zip.Entries)
        {
            var full = entry.FullName.Replace('\\', '/');
            if (!(full.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) || full.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (full.EndsWith('/')) continue;
            var relative = full.Contains('/') ? full[(full.IndexOf('/') + 1)..] : full;
            var dest = PathSecurity.ResolveInsideRoot(instanceDir, relative); // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var src = entry.Open();
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await src.CopyToAsync(dst);
        }
        await using var idx = index.Open();
        using var doc = await JsonDocument.ParseAsync(idx);
        return new ModpackManifest { Id = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "imported" : "imported", Version = "imported" };
    }
}
