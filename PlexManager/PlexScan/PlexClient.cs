using System.Text.Json.Nodes;

namespace PlexScan;

/// <summary>Plex Media Server client. Requests JSON and pages through library listings; PutAsync and DeleteAsync are the only writes and are used by PlexFix.</summary>
public sealed class PlexClient
{
    private const int PageSize = 500;

    private readonly HttpClient _http;
    private readonly PlexConfig _config;

    public PlexClient(PlexConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>Returns the Metadata items of a listing, following X-Plex-Container paging and asking Plex to include external guids.</summary>
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
        using var req = new HttpRequestMessage(HttpMethod.Get, _config.BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _config.Token);
        req.Headers.Add("Accept", "application/json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return node?["MediaContainer"] ?? throw new InvalidOperationException($"No MediaContainer in response for {pathAndQuery}.");
    }

    /// <summary>PUT a path (no body), for example a metadata match. Throws on a non-success status.</summary>
    public async Task PutAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, _config.BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _config.Token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>DELETE a path, for example a library item (removes the file too when the server allows media deletion). Throws on a non-success status.</summary>
    public async Task DeleteAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, _config.BaseUrl + pathAndQuery);
        req.Headers.Add("X-Plex-Token", _config.Token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
