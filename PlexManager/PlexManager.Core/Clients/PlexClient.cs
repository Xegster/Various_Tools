using System.Text.Json.Nodes;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Clients;

/// <summary>
/// Plex Media Server client. Asks for JSON and pages through library listings; PUT (a metadata match) and DELETE
/// (an item and its file) are the only writes.
/// </summary>
public sealed class PlexClient
{
    private const int PageSize = 500;

    private readonly HttpClient _http;
    private readonly PlexSettings _settings;

    public PlexClient(PlexSettings settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    public string BaseUrl => _settings.BaseUrl;

    /// <summary>The Metadata items of a listing, following X-Plex-Container paging and asking Plex to include external guids.</summary>
    public async Task<List<JsonNode>> GetAllAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        var start = 0;
        while (true)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var container = await GetAsync($"{path}{sep}includeGuids=1&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}", ct);
            var items = container["Metadata"]?.AsArray();
            if (items == null || items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                all.Add(item!);
            }

            start += items.Count;
            var total = container["totalSize"]?.GetValue<int>() ?? container["size"]?.GetValue<int>() ?? start;
            if (start >= total)
            {
                break;
            }
        }

        return all;
    }

    /// <summary>GET a path and return its MediaContainer.</summary>
    public async Task<JsonNode> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _settings.Token);
        req.Headers.Add("Accept", "application/json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return node?["MediaContainer"] ?? throw new InvalidOperationException($"No MediaContainer in the response for {pathAndQuery}.");
    }

    /// <summary>PUT a path (no body), for example a metadata match. Throws on a non-success status.</summary>
    public async Task PutAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _settings.Token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>DELETE a path, for example a library item (the file too, when the server allows media deletion).</summary>
    public async Task DeleteAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _settings.Token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Read-only check used by the wizard's Test connection button: the server's own name and version.</summary>
    public async Task<PlexIdentity> IdentityAsync(CancellationToken ct)
    {
        var container = await GetAsync("/", ct);
        return new PlexIdentity(
            container["friendlyName"]?.GetValue<string>() ?? container["machineIdentifier"]?.GetValue<string>() ?? "Plex",
            container["version"]?.GetValue<string>() ?? "",
            container["machineIdentifier"]?.GetValue<string>() ?? "");
    }

    /// <summary>The library sections, so the wizard can offer the movie and TV libraries as drop-downs.</summary>
    public async Task<List<PlexSection>> SectionsAsync(CancellationToken ct)
    {
        var container = await GetAsync("/library/sections", ct);
        var sections = new List<PlexSection>();
        foreach (var dir in container["Directory"]?.AsArray() ?? [])
        {
            if (dir == null)
            {
                continue;
            }

            var key = dir["key"]?.GetValue<string>() ?? "";
            if (int.TryParse(key, out var id))
            {
                sections.Add(new PlexSection(id, dir["title"]?.GetValue<string>() ?? key, dir["type"]?.GetValue<string>() ?? ""));
            }
        }

        return sections;
    }

    /// <summary>Whether the server will delete files, not just library entries. PlexPrune and PlexFix refuse to delete without it.</summary>
    public async Task<bool?> AllowsMediaDeletionAsync(CancellationToken ct)
    {
        var prefs = await GetAsync("/:/prefs", ct);
        var value = prefs["Setting"]?.AsArray().FirstOrDefault(s => s?["id"]?.GetValue<string>() == "allowMediaDeletion")?["value"]?.ToString();
        return value == null ? null : string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }

    public string MoviesPath => $"/library/sections/{_settings.MovieSectionId}/all";

    public string ShowsPath => $"/library/sections/{_settings.ShowSectionId}/all?type=2";

    public string EpisodesPath => $"/library/sections/{_settings.ShowSectionId}/all?type=4";
}

public sealed record PlexIdentity(string Name, string Version, string MachineIdentifier);

public sealed record PlexSection(int Id, string Title, string Type);
