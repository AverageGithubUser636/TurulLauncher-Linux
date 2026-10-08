using System.Text.Json;

namespace TurulMC.Core.Mods;

/// <summary>Egy Modrinth verzió, amely a cél-Minecraft-verzióhoz telepíthető.</summary>
public sealed record ModUpdateCandidate
{
    public required string VersionId { get; init; }
    public string VersionNumber { get; init; } = "";
    public string VersionType { get; init; } = "release";
    public string FileName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public long FileSize { get; init; }
    public string DatePublished { get; init; } = "";

    /// <summary>A Modrinth kiadás által támogatott Minecraft-verziók (tájékoztatáshoz).</summary>
    public string[] GameVersions { get; init; } = Array.Empty<string>();

    /// <summary>"exact" (pontos verzióegyezés) vagy "family" (ugyanaz a verziócsalád).</summary>
    public string MatchKind { get; init; } = "exact";
}

/// <summary>
/// Modrinth <c>project/{id}/version</c> válasz feldolgozása: kiválasztja a cél
/// Minecraft-verzióhoz és loaderhez illő, legfrissebb stabil verziót. Tiszta függvény,
/// hálózat nélkül tesztelhető; a letöltést és a fájlcserét a hívó végzi.
/// </summary>
public static class ModUpdatePlanner
{
    /// <summary>Kiválasztja a legjobban illeszkedő verziót a JSON tömbből.</summary>
    public static ModUpdateCandidate? PickBest(
        string? versionsJson,
        string minecraftVersion,
        string loader,
        string? currentVersionId = null,
        string? installedVersionNumber = null)
    {
        if (string.IsNullOrWhiteSpace(versionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(versionsJson);
            return PickBest(doc.RootElement, minecraftVersion, loader, currentVersionId, installedVersionNumber);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Igaz, ha a jelölt automatikusan telepíthető. A "family" (verziócsalád) egyezés csak
    /// tájékoztató: a Minecraft pontverziója és a mod által deklarált verzió nem egyezik,
    /// ezért azt a felhasználónak kell eldöntenie — különben a terv olyan frissítést
    /// kínálna, amit a telepítő (és a Fabric is) elutasítana.
    /// </summary>
    public static bool IsInstallable(ModUpdateCandidate? candidate)
        => candidate is not null && candidate.MatchKind == "exact";

    /// <summary>
    /// Igaz, ha a <paramref name="currentVersionId"/>val azonosított, már telepített verzió
    /// támogatja a megadott Minecraft-verziót és loadert (akkor nincs mit frissíteni).
    /// </summary>
    public static bool IsInstalledVersionCompatible(
        string? versionsJson,
        string minecraftVersion,
        string loader,
        string? currentVersionId)
    {
        if (string.IsNullOrWhiteSpace(versionsJson) || string.IsNullOrWhiteSpace(currentVersionId)) return false;
        try
        {
            using var doc = JsonDocument.Parse(versionsJson);
            return IsInstalledVersionCompatible(doc.RootElement, minecraftVersion, loader, currentVersionId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Igaz, ha a telepített verzió a célverziót és loadert támogatja.</summary>
    public static bool IsInstalledVersionCompatible(
        JsonElement versions,
        string minecraftVersion,
        string loader,
        string? currentVersionId)
    {
        if (versions.ValueKind != JsonValueKind.Array || string.IsNullOrWhiteSpace(currentVersionId)) return false;
        var target = (minecraftVersion ?? "").Trim();
        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object) continue;
            var versionId = GetString(version, "id");
            if (!versionId.Equals(currentVersionId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var games = GetStringArray(version, "game_versions");
            var loaders = GetStringArray(version, "loaders");
            return LoaderMatches(loaders, (loader ?? "none").Trim().ToLowerInvariant()) &&
                   games.Any(x => x.Equals(target, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>Kiválasztja a legjobban illeszkedő verziót egy már beolvasott JSON tömbből.</summary>
    public static ModUpdateCandidate? PickBest(
        JsonElement versions,
        string minecraftVersion,
        string loader,
        string? currentVersionId = null,
        string? installedVersionNumber = null)
    {
        if (versions.ValueKind != JsonValueKind.Array) return null;

        var normalizedLoader = (loader ?? "none").Trim().ToLowerInvariant();
        var candidates = new List<(int Rank, ModUpdateCandidate Candidate)>();
        var target = (minecraftVersion ?? "").Trim();
        var family = MinecraftVersionOrder.Family(target);

        // A telepített verzió kiadási dátuma és verziószáma a "ne legyen visszalépés" védelemhez.
        DateTime? installedDate = null;

        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object) continue;

            var versionId = GetString(version, "id");
            if (string.IsNullOrWhiteSpace(versionId)) continue;
            if (!string.IsNullOrWhiteSpace(currentVersionId) &&
                versionId.Equals(currentVersionId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var installed = ParseDate(GetString(version, "date_published"));
                if (installed > DateTime.MinValue) installedDate = installed;
                if (string.IsNullOrWhiteSpace(installedVersionNumber))
                    installedVersionNumber = GetString(version, "version_number");
                continue;
            }

            var gameVersions = GetStringArray(version, "game_versions");
            var loaders = GetStringArray(version, "loaders");

            if (!LoaderMatches(loaders, normalizedLoader)) continue;

            var exact = gameVersions.Any(x => x.Equals(target, StringComparison.OrdinalIgnoreCase));
            var sameFamily = !exact && target.Length > 0 && gameVersions.Any(x =>
                MinecraftVersionOrder.Family(x).Equals(family, StringComparison.OrdinalIgnoreCase));
            if (!exact && !sameFamily) continue;

            var file = SelectJarFile(version);
            if (file is null) continue;

            var candidate = new ModUpdateCandidate
            {
                VersionId = versionId,
                VersionNumber = GetString(version, "version_number"),
                VersionType = GetString(version, "version_type") is { Length: > 0 } type ? type : "release",
                FileName = file.Value.FileName,
                DownloadUrl = file.Value.Url,
                Sha1 = file.Value.Sha1,
                Sha512 = file.Value.Sha512,
                FileSize = file.Value.Size,
                DatePublished = GetString(version, "date_published"),
                GameVersions = gameVersions,
                MatchKind = exact ? "exact" : "family"
            };

            // Rangsor: pontos verzióegyezés > család; release > beta > alpha; újabb dátum előrébb.
            var rank = (exact ? 0 : 100) + VersionTypeRank(candidate.VersionType);
            candidates.Add((rank, candidate));
        }

        if (candidates.Count == 0) return null;

        // Visszalépés-védelem: nem ajánlunk a telepítettnél régebbi kiadást. A döntés a
        // kiadási dátumon alapul, ha az ismert; ha nem, akkor a verziószámon.
        var newer = candidates.Where(x => IsNewerThanInstalled(x.Candidate, installedDate, installedVersionNumber)).ToList();
        if (newer.Count == 0) return null;
        candidates = newer;

        return candidates
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => ParseDate(x.Candidate.DatePublished))
            .Select(x => x.Candidate)
            .First();
    }

    /// <summary>
    /// Igaz, ha a jelölt újabb a telepített verziónál. A döntés elsődlegesen a
    /// <b>verziószámon</b> alapul (a mod-verziók összehasonlításával, ami a Modrinth
    /// <c>mc26.3-0.9.3-…</c> alakú értékeit is kezeli), hogy ugyanazt a szabályt használja,
    /// mint a telepítő oldali cél-Instance ellenőrzés. Ha a verziószámok nem
    /// összehasonlíthatók, akkor a kiadási dátum dönt; ha az sem ismert, a jelöltet
    /// elfogadjuk (a letöltés előtt úgyis hash-ellenőrzés és biztonsági mentés történik).
    /// </summary>
    private static bool IsNewerThanInstalled(ModUpdateCandidate candidate, DateTime? installedDate, string? installedVersionNumber)
    {
        var numeric = ModVersionOrder.TryCompare(candidate.VersionNumber, installedVersionNumber);
        if (numeric is not null) return numeric > 0;

        var candidateDate = ParseDate(candidate.DatePublished);
        if (installedDate.HasValue && candidateDate > DateTime.MinValue)
            return candidateDate > installedDate.Value;

        return true;
    }

    /// <summary>Igaz, ha a Modrinth verzió támogatja a megadott Minecraft-verziót és loadert.</summary>
    public static bool Supports(JsonElement version, string minecraftVersion, string loader)
    {
        if (version.ValueKind != JsonValueKind.Object) return false;
        var target = (minecraftVersion ?? "").Trim();
        var games = GetStringArray(version, "game_versions");
        if (!games.Any(x => x.Equals(target, StringComparison.OrdinalIgnoreCase))) return false;
        return LoaderMatches(GetStringArray(version, "loaders"), (loader ?? "none").Trim().ToLowerInvariant());
    }

    private static bool LoaderMatches(string[] loaders, string loader)
    {
        if (loaders.Length == 0) return true;
        if (loader is "none" or "")
            return loaders.Any(x => x.Equals("minecraft", StringComparison.OrdinalIgnoreCase));
        return loaders.Any(x => x.Equals(loader, StringComparison.OrdinalIgnoreCase));
    }

    private static int VersionTypeRank(string versionType) => versionType.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

    private static DateTime ParseDate(string? value)
        => DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed) ? parsed : DateTime.MinValue;

    private static (string FileName, string Url, string Sha1, string Sha512, long Size)? SelectJarFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        (string FileName, string Url, string Sha1, string Sha512, long Size)? fallback = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            var fileName = GetString(file, "filename");
            if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

            var url = GetString(file, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            string sha1 = "", sha512 = "";
            if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
            {
                sha1 = GetString(hashes, "sha1");
                sha512 = GetString(hashes, "sha512");
            }

            long size = 0;
            if (file.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number)
                sizeEl.TryGetInt64(out size);

            var candidate = (fileName, url, sha1, sha512, size);
            var primary = file.TryGetProperty("primary", out var primaryEl) && primaryEl.ValueKind == JsonValueKind.True;
            if (primary) return candidate;
            fallback ??= candidate;
        }

        return fallback;
    }

    private static string[] GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
