using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Clients;

/// <summary>
/// Radarr and Sonarr (v3 API). One class for both: the paths differ only in "movie" against "series", so the
/// caller says which it is. These calls used to live inline in PlexMigrate and PlexPrune.
/// </summary>
public sealed partial class ArrClient : IDisposable
{
    private readonly ArrSettings _settings;
    private readonly HttpClient _http;

    public ArrClient(ArrSettings settings, bool isRadarr)
    {
        _settings = settings;
        IsRadarr = isRadarr;
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };

        if (settings.ApiKey.HasValue)
        {
            _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey.Value);
        }
    }

    public bool IsRadarr { get; }

    public string Name => IsRadarr ? "Radarr" : "Sonarr";

    /// <summary>"movie" for Radarr, "series" for Sonarr.</summary>
    public string Resource => IsRadarr ? "movie" : "series";

    public string BaseUrl => _settings.BaseUrl;

    /// <summary>Read-only check for the wizard: the version and instance name the server reports.</summary>
    public async Task<ArrStatus> StatusAsync(CancellationToken ct)
    {
        var body = await GetJsonAsync("api/v3/system/status", ct);
        return new ArrStatus(
            body?["instanceName"]?.GetValue<string>() ?? Name,
            body?["version"]?.GetValue<string>() ?? "",
            body?["appName"]?.GetValue<string>() ?? Name);
    }

    public async Task<List<ArrOption>> QualityProfilesAsync(CancellationToken ct) =>
        (await GetJsonAsync("api/v3/qualityprofile", ct))?.AsArray()
            .Select(p => new ArrOption(p?["id"]?.GetValue<int>() ?? 0, p?["name"]?.GetValue<string>() ?? ""))
            .ToList() ?? [];

    public async Task<List<string>> RootFoldersAsync(CancellationToken ct) =>
        (await GetJsonAsync("api/v3/rootfolder", ct))?.AsArray()
            .Select(f => f?["path"]?.GetValue<string>() ?? "")
            .Where(p => p.Length > 0)
            .ToList() ?? [];

    /// <summary>Sonarr's (older) language profiles; null when the server has none, which newer Sonarr does not.</summary>
    public async Task<int?> FirstLanguageProfileAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v3/languageprofile", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return body?.AsArray().FirstOrDefault()?["id"]?.GetValue<int>();
    }

    /// <summary>Everything the server already has, as a set of id keys (tmdb/tvdb/imdb).</summary>
    public async Task<HashSet<string>> LibraryKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in await LibraryAsync(ct))
        {
            foreach (var key in Util.MediaIds.FromArr(entry, IsRadarr))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    public async Task<List<JsonNode>> LibraryAsync(CancellationToken ct) =>
        (await GetJsonAsync($"api/v3/{Resource}", ct))?.AsArray().Where(n => n != null).Select(n => n!).ToList() ?? [];

    /// <summary>Looks a title up by external id ("tmdb:603" or "tvdb:81189") and returns the record to post back.</summary>
    public async Task<JsonNode?> LookupAsync(string term, CancellationToken ct) =>
        (await GetJsonAsync($"api/v3/{Resource}/lookup?term={Uri.EscapeDataString(term)}", ct))?.AsArray().FirstOrDefault();

    /// <summary>Adds the looked-up record. Returns what happened, distinguishing "already there" from a real failure.</summary>
    public async Task<ArrAddResult> AddAsync(JsonNode record, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            $"api/v3/{Resource}",
            new StringContent(record.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return new ArrAddResult(ArrAddState.Added, "added");
        }

        if (body.Contains("already been added", StringComparison.OrdinalIgnoreCase) || body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return new ArrAddResult(ArrAddState.AlreadyPresent, "already present");
        }

        var trimmed = (body.Length > 200 ? body[..200] : body).Replace('\n', ' ').Replace('\r', ' ');
        return new ArrAddResult(ArrAddState.Failed, $"failed: {(int)response.StatusCode} {trimmed}");
    }

    /// <summary>Removes a Radarr movie and adds an import exclusion, so it is not re-added by a list sync.</summary>
    public async Task<bool> DeleteMovieAsync(int id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/v3/movie/{id}?deleteFiles=false&addImportExclusion=true", ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Tries to read the server's own API key, so the wizard need not ask for it. Radarr and Sonarr serve it in
    /// initialize.js when the request is allowed without a login (the usual setting for a LAN server).
    /// </summary>
    public static async Task<string?> TryDiscoverApiKeyAsync(string baseUrl, HttpClient http, CancellationToken ct)
    {
        foreach (var path in new[] { "/initialize.js", "/initialize.json" })
        {
            try
            {
                using var response = await http.GetAsync(baseUrl.TrimEnd('/') + path, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var match = ApiKeyPattern().Match(body);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (HttpRequestException)
            {
                // Try the next path; the caller falls back to asking for the key.
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (!_settings.ApiKey.HasValue)
        {
            throw new SettingsIncompleteException($"No API key for {Name}. Open Settings and add it (the app can also read it from the server).");
        }

        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"{Name} rejected the API key.");
        }

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    [GeneratedRegex(@"[""']?apiKey[""']?\s*[:=]\s*[""']([A-Za-z0-9]{16,})[""']")]
    private static partial Regex ApiKeyPattern();
}

public sealed record ArrStatus(string InstanceName, string Version, string AppName);

public sealed record ArrOption(int Id, string Name);

public enum ArrAddState
{
    Added,
    AlreadyPresent,
    Failed,
}

public sealed record ArrAddResult(ArrAddState State, string Message);
