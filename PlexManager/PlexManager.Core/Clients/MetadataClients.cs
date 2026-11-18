using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Clients;

/// <summary>A JSON file in the Caches folder, loaded once and written back whenever it changes.</summary>
public abstract class JsonFileCache
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    protected JsonFileCache(string path)
    {
        Path = path;
    }

    public string Path { get; }

    protected JsonObject Load()
    {
        if (!File.Exists(Path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(Path))?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    protected void Store(JsonObject contents)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, contents.ToJsonString(Pretty));
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>
/// Rotten Tomatoes percentage by IMDb id, from omdbapi.com, cached on disk. A "no score" answer is cached too,
/// so a title with no score is not asked about twice.
/// </summary>
public sealed class OmdbClient : JsonFileCache
{
    private readonly string _key;
    private readonly HttpClient _http;
    private readonly Dictionary<string, double?> _cache = new(StringComparer.Ordinal);

    public OmdbClient(OmdbSettings settings, string cachePath, HttpClient http)
        : base(cachePath)
    {
        _key = settings.ApiKey;
        _http = http;
        foreach (var entry in Load())
        {
            _cache[entry.Key] = entry.Value?.GetValue<double>();
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_key);

    public int Calls { get; private set; }

    public int CacheHits { get; private set; }

    public bool LimitReached { get; private set; }

    public async Task<double?> RottenTomatoesAsync(string imdb, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (_cache.TryGetValue(imdb, out var cached))
        {
            CacheHits++;
            return cached;
        }

        if (LimitReached)
        {
            return null;
        }

        Calls++;
        var body = JsonNode.Parse(await _http.GetStringAsync(
            $"https://www.omdbapi.com/?apikey={_key}&i={Uri.EscapeDataString(imdb)}", ct))!;
        if (body["Response"]?.GetValue<string>() != "True")
        {
            if ((body["Error"]?.GetValue<string>() ?? "").Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                LimitReached = true;
                Calls--;
                return null;
            }

            _cache[imdb] = null;
            Save();
            return null;
        }

        var value = body["Ratings"]?.AsArray()
            .FirstOrDefault(r => r?["Source"]?.GetValue<string>() == "Rotten Tomatoes")?["Value"]?.GetValue<string>();
        double? score = value != null && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        _cache[imdb] = score;
        Save();
        return score;
    }

    /// <summary>Read-only check for the wizard: one lookup of a well-known id.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var body = JsonNode.Parse(await _http.GetStringAsync($"https://www.omdbapi.com/?apikey={_key}&i=tt0133093", ct))!;
        return body["Response"]?.GetValue<string>() == "True"
            ? $"OMDb answered for {body["Title"]} ({body["Year"]})."
            : throw new InvalidOperationException("OMDb said: " + (body["Error"]?.GetValue<string>() ?? "unknown error"));
    }

    public void Save()
    {
        var contents = new JsonObject();
        foreach (var entry in _cache.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            contents[entry.Key] = entry.Value;
        }

        Store(contents);
    }
}

/// <summary>
/// Subscription ("flatrate") streaming services for a title in one region, from TMDB's watch-provider data
/// (JustWatch). Cached on disk, keyed by type and id.
/// </summary>
public sealed class TmdbClient : JsonFileCache
{
    private readonly string _key;
    private readonly HttpClient _http;
    private readonly IOperationLog _log;
    private readonly Dictionary<string, List<string>> _cache = new(StringComparer.Ordinal);

    public TmdbClient(TmdbSettings settings, string cachePath, HttpClient http, IOperationLog log)
        : base(cachePath)
    {
        _key = settings.ApiKey;
        Region = string.IsNullOrWhiteSpace(settings.Region) ? "US" : settings.Region;
        _http = http;
        _log = log;
        foreach (var entry in Load())
        {
            _cache[entry.Key] = entry.Value?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        }
    }

    public string Region { get; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_key);

    /// <summary>Services that carry the title on a subscription in the configured region.</summary>
    public async Task<List<string>> FlatrateAsync(bool isMovie, string? tmdb, string? tvdb, string? imdb, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var key = (isMovie ? "movie:" : "show:") + (isMovie ? tmdb : imdb ?? tvdb);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var names = new List<string>();
        var id = isMovie ? tmdb : await FindTvIdAsync(tvdb, imdb, ct);
        if (id != null)
        {
            var kind = isMovie ? "movie" : "tv";
            var body = await GetJsonOrNullAsync($"https://api.themoviedb.org/3/{kind}/{id}/watch/providers?api_key={_key}", ct);
            foreach (var provider in body?["results"]?[Region]?["flatrate"]?.AsArray() ?? [])
            {
                names.Add(provider!["provider_name"]!.GetValue<string>());
            }
        }

        _cache[key] = names;
        Save();
        return names;
    }

    /// <summary>Read-only check for the wizard, plus the region list for its drop-down.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var body = JsonNode.Parse(await _http.GetStringAsync($"https://api.themoviedb.org/3/movie/603?api_key={_key}", ct))!;
        return $"TMDB answered for {body["title"]} ({body["release_date"]?.GetValue<string>()?[..4]}); region {Region}.";
    }

    public async Task<List<ArrOption>> RegionsAsync(CancellationToken ct)
    {
        var body = JsonNode.Parse(await _http.GetStringAsync($"https://api.themoviedb.org/3/watch/providers/regions?api_key={_key}", ct))!;
        var regions = new List<ArrOption>();
        var index = 0;
        foreach (var region in body["results"]?.AsArray() ?? [])
        {
            regions.Add(new ArrOption(index++, $"{region!["iso_3166_1"]} - {region["english_name"]}"));
        }

        return regions;
    }

    private async Task<JsonNode?> GetJsonOrNullAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _log.Detail($"TMDB has no record at {url[..url.IndexOf('?', StringComparison.Ordinal)]} (treated as not streaming)");
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string?> FindTvIdAsync(string? tvdb, string? imdb, CancellationToken ct)
    {
        var (source, id) = tvdb != null ? ("tvdb_id", tvdb) : ("imdb_id", imdb);
        if (id == null)
        {
            return null;
        }

        var body = await GetJsonOrNullAsync($"https://api.themoviedb.org/3/find/{id}?external_source={source}&api_key={_key}", ct);
        return body?["tv_results"]?.AsArray().FirstOrDefault()?["id"]?.ToString();
    }

    public void Save()
    {
        var contents = new JsonObject();
        foreach (var entry in _cache.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            contents[entry.Key] = new JsonArray(entry.Value.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        }

        Store(contents);
    }
}

/// <summary>Original language of a show from Trakt's public show endpoint, cached on disk with the title for readability.</summary>
public sealed class ShowLanguageLookup : JsonFileCache
{
    private readonly string _clientId;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly JsonObject _contents;

    public ShowLanguageLookup(string clientId, string cachePath, HttpClient http)
        : base(cachePath)
    {
        _clientId = clientId;
        _http = http;
        _contents = Load();
        foreach (var entry in _contents)
        {
            _cache[entry.Key] = entry.Value?["language"]?.GetValue<string>();
        }
    }

    public async Task<string?> OriginalAsync(string traktId, string title, int year, CancellationToken ct)
    {
        if (_cache.TryGetValue(traktId, out var cached))
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/shows/{traktId}?extended=full");
        request.Headers.Add("trakt-api-key", _clientId);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.UserAgent.ParseAdd("PlexManager/1.0");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var language = body["language"]?.GetValue<string>();
        _contents[traktId] = new JsonObject
        {
            ["title"] = title,
            ["year"] = year,
            ["language"] = body["language"]?.DeepClone(),
            ["country"] = body["country"]?.DeepClone(),
        };
        _cache[traktId] = language;
        Save();
        await Task.Delay(300, ct);
        return language;
    }

    public void Save() => Store(_contents);
}
