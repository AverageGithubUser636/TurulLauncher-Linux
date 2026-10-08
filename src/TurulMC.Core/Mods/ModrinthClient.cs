using System.Text.Json;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Mods;

/// <summary>
/// Modrinth API v2 kliens (https://api.modrinth.com/v2/).
/// A Windows-launcher beágyazott WebView-logikájának Core-ba emelt,
/// közvetlenül tesztelhető változata: a HTTP-réteg injektálható,
/// így a tesztek stub handlerrel, hálózat nélkül futnak.
/// </summary>
public sealed class ModrinthClient
{
    public const string ApiBase = "https://api.modrinth.com/v2/";
    public const string UserAgent = "TurulMC-Launcher/4.6.0 (Linux; +https://turulnetwork.hu)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly HttpClient _http;

    public ModrinthClient(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = TimeSpan.FromSeconds(45)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Keresés a Modrinth-en. A <c>loaders</c> (pl. fabric) és a
    /// <c>gameVersions</c> (pl. 1.21.1) a szerver oldalon szűr —
    /// csak kompatibilis projektek jönnek vissza.
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query,
        string[]? loaders = null,
        string[]? gameVersions = null,
        string projectType = "mod",
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);

        var facets = new List<string[]>
        {
            new[] { $"project_type:{projectType}" }
        };
        if (gameVersions is { Length: > 0 })
            facets.Add(gameVersions.Select(v => $"versions:{v}").ToArray());
        if (loaders is { Length: > 0 })
            facets.Add(loaders.Select(l => $"categories:{l.ToLowerInvariant()}").ToArray());

        var url = "search" +
            $"?query={Uri.EscapeDataString(query ?? "")}" +
            $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets, JsonOptions))}" +
            $"&index=relevance&limit={limit}&offset={offset}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;

        var hits = new List<ModrinthHit>();
        if (root.TryGetProperty("hits", out var hitsEl) && hitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hitsEl.EnumerateArray())
            {
                var projectId = GetString(hit, "project_id");
                if (string.IsNullOrWhiteSpace(projectId)) continue;
                hits.Add(new ModrinthHit(
                    ProjectId: projectId,
                    Title: GetString(hit, "title") is { } t && t.Length > 0 ? t : projectId,
                    Description: GetString(hit, "description") ?? "",
                    Author: GetString(hit, "author") ?? "",
                    IconUrl: GetOptionalString(hit, "icon_url"),
                    ProjectType: GetString(hit, "project_type") ?? projectType,
                    Downloads: GetInt64(hit, "downloads"),
                    Loaders: GetStringArray(hit, "loaders"),
                    GameVersions: GetStringArray(hit, "game_versions")));
            }
        }

        return new ModrinthSearchResult(
            Hits: hits,
            TotalHits: root.TryGetProperty("total_hits", out var total) && total.TryGetInt32(out var n) ? n : hits.Count,
            Offset: offset,
            Limit: limit);
    }

    public async Task<ModrinthProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        using var response = await _http.GetAsync(
            $"project/{Uri.EscapeDataString(projectId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;
        var id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new ModrinthProject(
            ProjectId: id,
            Title: GetString(root, "title") ?? id,
            Description: GetString(root, "description") ?? "",
            IconUrl: GetOptionalString(root, "icon_url"),
            ProjectType: GetString(root, "project_type") ?? "",
            Downloads: GetInt64(root, "downloads"),
            Loaders: GetStringArray(root, "loaders"),
            GameVersions: GetStringArray(root, "game_versions"));
    }

    /// <summary>
    /// Egy projekt verziói, opcionálisan loader + MC-verzió szerint szűrve
    /// (szerver oldali szűrés — a visszaadott lista már kompatibilis).
    /// </summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string[]? loaders = null,
        string[]? gameVersions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Array.Empty<ModrinthVersion>();

        var url = $"project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false";
        if (loaders is { Length: > 0 })
            url += $"&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(loaders.Select(l => l.ToLowerInvariant()).ToArray(), JsonOptions))}";
        if (gameVersions is { Length: > 0 })
            url += $"&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(gameVersions, JsonOptions))}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ModrinthVersion>();

        var result = new List<ModrinthVersion>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var parsed = ParseVersion(v);
            if (parsed is not null) result.Add(parsed);
        }
        return result;
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;
        using var response = await _http.GetAsync(
            $"version/{Uri.EscapeDataString(versionId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseVersion(doc.RootElement);
    }

    /// <summary>
    /// Több projekt egy hívással (<c>projects?ids=[...]</c>).
    /// A telepített lista ikonjait így oldjuk fel: egy kérés az összes
    /// ismert projektre, nem pedig projektenként egy.
    /// </summary>
    public async Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(
        IEnumerable<string> projectIds, CancellationToken cancellationToken = default)
    {
        var ids = (projectIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        if (ids.Length == 0) return Array.Empty<ModrinthProject>();

        var url = "projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(ids, JsonOptions));
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ModrinthProject>();

        var result = new List<ModrinthProject>();
        foreach (var root in doc.RootElement.EnumerateArray())
        {
            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new ModrinthProject(
                ProjectId: id,
                Title: GetString(root, "title") ?? id,
                Description: GetString(root, "description") ?? "",
                IconUrl: GetOptionalString(root, "icon_url"),
                ProjectType: GetString(root, "project_type") ?? "",
                Downloads: GetInt64(root, "downloads"),
                Loaders: GetStringArray(root, "loaders"),
                GameVersions: GetStringArray(root, "game_versions")));
        }
        return result;
    }

    /// <summary>
    /// Telepített fájl → Modrinth verzió SHA-512 alapján. Így a kézzel
    /// bemásolt modokhoz is feloldható a projekt (ikon, frissítés).
    /// </summary>
    public async Task<ModrinthVersion?> GetVersionByHashAsync(string sha512Hex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha512Hex)) return null;
        using var response = await _http.GetAsync(
            $"version_file/{Uri.EscapeDataString(sha512Hex)}?algorithm=sha512",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseVersion(doc.RootElement);
    }

    // ------------------------------------------------------------------ segédek

    internal static ModrinthVersion? ParseVersion(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        var versionId = GetString(v, "id");
        if (string.IsNullOrWhiteSpace(versionId)) return null;

        var files = new List<ModrinthFile>();
        if (v.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                var fileName = GetString(f, "filename");
                var url = GetString(f, "url");
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(url)) continue;
                string? sha1 = null, sha512 = null;
                long size = 0;
                if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                {
                    sha1 = GetOptionalString(hashes, "sha1");
                    sha512 = GetOptionalString(hashes, "sha512");
                }
                if (f.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s)) size = s;
                files.Add(new ModrinthFile(
                    FileName: fileName,
                    Url: url,
                    Primary: f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
                    Size: size,
                    Sha1: sha1,
                    Sha512: sha512));
            }
        }

        var deps = new List<ModrinthDependency>();
        if (v.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depsEl.EnumerateArray())
            {
                deps.Add(new ModrinthDependency(
                    ProjectId: GetOptionalString(d, "project_id"),
                    VersionId: GetOptionalString(d, "version_id"),
                    DependencyType: GetString(d, "dependency_type") ?? ""));
            }
        }

        return new ModrinthVersion(
            VersionId: versionId,
            ProjectId: GetString(v, "project_id") ?? "",
            VersionNumber: GetString(v, "version_number") ?? "",
            VersionType: GetString(v, "version_type") ?? "",
            Loaders: GetStringArray(v, "loaders"),
            GameVersions: GetStringArray(v, "game_versions"),
            Files: files,
            Dependencies: deps,
            Changelog: GetOptionalString(v, "changelog"));
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static string? GetOptionalString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static long GetInt64(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : 0;

    private static string[] GetStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return p.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }
}

public sealed record ModrinthHit(
    string ProjectId,
    string Title,
    string Description,
    string Author,
    string? IconUrl,
    string ProjectType,
    long Downloads,
    string[] Loaders,
    string[] GameVersions);

public sealed record ModrinthSearchResult(
    IReadOnlyList<ModrinthHit> Hits,
    int TotalHits,
    int Offset,
    int Limit);

public sealed record ModrinthProject(
    string ProjectId,
    string Title,
    string Description,
    string? IconUrl,
    string ProjectType,
    long Downloads,
    string[] Loaders,
    string[] GameVersions);

public sealed record ModrinthFile(
    string FileName,
    string Url,
    bool Primary,
    long Size,
    string? Sha1,
    string? Sha512);

public sealed record ModrinthDependency(
    string? ProjectId,
    string? VersionId,
    string DependencyType);

public sealed record ModrinthVersion(
    string VersionId,
    string ProjectId,
    string VersionNumber,
    string VersionType,
    string[] Loaders,
    string[] GameVersions,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies,
    string? Changelog);
