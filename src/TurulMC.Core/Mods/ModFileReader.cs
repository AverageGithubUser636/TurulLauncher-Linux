using System.IO.Compression;
using System.Text.Json;

namespace TurulMC.Core.Mods;

/// <summary>Egy mod JAR típusa a benne talált leíró alapján.</summary>
public enum ModFileKind
{
    /// <summary>Fabric mod (fabric.mod.json).</summary>
    Fabric,

    /// <summary>Quilt mod (quilt.mod.json).</summary>
    Quilt,

    /// <summary>Forge mod (META-INF/mods.toml).</summary>
    Forge,

    /// <summary>NeoForge mod.</summary>
    NeoForge,

    /// <summary>Nem felismerhető (nincs leíró, vagy sérült JAR).</summary>
    Unknown
}

/// <summary>Egy mod JAR-ból kiolvasott, ellenőrzéshez szükséges adatok.</summary>
public sealed record ModFileInfo
{
    public required string FileName { get; init; }
    public ModFileKind Kind { get; init; } = ModFileKind.Unknown;
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public string Environment { get; init; } = "";
    public string MinecraftRange { get; init; } = "";
    public string LoaderRange { get; init; } = "";
    public string JavaRange { get; init; } = "";

    /// <summary>Igaz, ha a leíró a Fabric Loadert igényli (depends.fabricloader).</summary>
    public bool RequiresFabricLoader => LoaderRange.Length > 0;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FileName : Title;
}

/// <summary>
/// A <c>fabric.mod.json</c> (és a Forge/NeoForge/quilt leírók) olvasása a mod JAR-okból.
/// Minden művelet védett: méretkorlát, kivételmentes hibaág, sérült ZIP nem állítja meg
/// a szkenner futását.
/// </summary>
public static class ModFileReader
{
    /// <summary>Ennél nagyobb JAR-t nem elemzünk (védett memória és idő).</summary>
    public const long MaxJarBytes = 256L * 1024L * 1024L;

    /// <summary>Egy leíró-bejegyzés maximális mérete.</summary>
    public const long MaxDescriptorBytes = 4L * 1024L * 1024L;

    /// <summary>
    /// Kiolvassa a mod leírót. Hiba esetén <c>null</c> és a <paramref name="error"/>
    /// tartalmazza a magyar nyelvű indoklást (a hívó "ismeretlen" állapotként kezeli).
    /// </summary>
    public static ModFileInfo? TryRead(string jarPath, out string error)
        => TryRead(jarPath, null, out error);

    /// <summary>
    /// Kiolvassa a mod leírót a megadott megjelenítési fájlnévvel. Hiba esetén <c>null</c>
    /// és a <paramref name="error"/> tartalmazza a magyar nyelvű indoklást.
    /// </summary>
    public static ModFileInfo? TryRead(string jarPath, string? fileName, out string error)
    {
        error = "";
        var name = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(jarPath) : fileName;

        try
        {
            if (!File.Exists(jarPath))
            {
                error = "A fájl nem található.";
                return null;
            }

            var length = new FileInfo(jarPath).Length;
            if (length <= 0)
            {
                error = "A fájl üres (valószínűleg megszakadt letöltés).";
                return null;
            }
            if (length > MaxJarBytes)
            {
                error = "A JAR mérete meghaladja a feldolgozhatót (256 MB).";
                return null;
            }

            using var archive = ZipFile.OpenRead(jarPath);
            var fabricEntry = FindEntry(archive, "fabric.mod.json");
            if (fabricEntry is not null && fabricEntry.Length is > 0 and <= MaxDescriptorBytes)
            {
                var info = ParseFabric(fabricEntry, name);
                if (info is not null) return info;
            }

            var quiltEntry = FindEntry(archive, "quilt.mod.json");
            if (quiltEntry is not null && quiltEntry.Length is > 0 and <= MaxDescriptorBytes)
            {
                using var stream = quiltEntry.Open();
                using var doc = JsonDocument.Parse(stream);
                var quiltLoader = doc.RootElement.TryGetProperty("quilt_loader", out var ql) ? ql : default;
                var quiltId = quiltLoader.ValueKind == JsonValueKind.Object ? GetString(quiltLoader, "id") : "";
                var quiltVersion = quiltLoader.ValueKind == JsonValueKind.Object ? GetString(quiltLoader, "version") : "";
                return new ModFileInfo
                {
                    FileName = name,
                    Kind = ModFileKind.Quilt,
                    Id = quiltId,
                    Title = quiltId,
                    Version = quiltVersion
                };
            }

            var forgeEntry = FindEntry(archive, "META-INF/mods.toml");
            if (forgeEntry is not null)
            {
                var kind = FindEntry(archive, "META-INF/neoforge.mods.toml") is not null
                    ? ModFileKind.NeoForge
                    : ModFileKind.Forge;
                return new ModFileInfo { FileName = name, Kind = kind, Id = "", Title = Path.GetFileNameWithoutExtension(name) };
            }

            error = "Nincs benne fabric.mod.json — nem Fabric mod (vagy sérült a JAR).";
            return null;
        }
        catch (InvalidDataException)
        {
            error = "A JAR sérült (nem érvényes ZIP).";
            return null;
        }
        catch (IOException ex)
        {
            error = "A JAR nem olvasható: " + ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "A JAR nem olvasható (jogosultság).";
            return null;
        }
        catch (JsonException)
        {
            error = "A fabric.mod.json sérült (érvénytelen JSON).";
            return null;
        }
        catch (Exception ex)
        {
            error = "Váratlan hiba a mod olvasásakor: " + ex.Message;
            return null;
        }
    }

    private static ModFileInfo? ParseFabric(ZipArchiveEntry entry, string fileName)
    {
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var depends = root.TryGetProperty("depends", out var dependsEl) && dependsEl.ValueKind == JsonValueKind.Object
            ? dependsEl
            : default;

        var id = GetString(root, "id");
        var title = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(title)) title = string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(fileName) : id;

        var minecraftRange = GetDependency(depends, "minecraft");
        var loaderRange = GetDependency(depends, "fabricloader", "fabric-loader", "fabric_loader");
        var javaRange = GetDependency(depends, "java");

        // Megjegyzés: a leírót csak a JAR gyökerében keressük (FindEntry), ezért egy beágyazott
        // (nested) mod leírója nem keveredik a fő mod adataival.

        return new ModFileInfo
        {
            FileName = fileName,
            Kind = ModFileKind.Fabric,
            Id = id,
            Title = title,
            Version = GetString(root, "version"),
            Environment = GetString(root, "environment"),
            MinecraftRange = minecraftRange,
            LoaderRange = loaderRange,
            JavaRange = javaRange
        };
    }

    private static string GetDependency(JsonElement depends, params string[] names)
    {
        if (depends.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in depends.EnumerateObject())
        {
            if (!names.Any(x => x.Equals(property.Name, StringComparison.OrdinalIgnoreCase))) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(" || ", property.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0)),
                _ => ""
            };
        }

        return "";
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string relativePath)
        => archive.Entries.FirstOrDefault(e =>
            NormalizeEntryPath(e.FullName).Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeEntryPath(string entryPath) => (entryPath ?? "").Replace('\\', '/').TrimStart('/');

    private static string GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }
}
