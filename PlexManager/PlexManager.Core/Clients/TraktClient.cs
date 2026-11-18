using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Clients;

/// <summary>Thin client for api.trakt.tv: auth headers, paging, and 429/401 handling.</summary>
public sealed class TraktClient
{
    private const int PageSize = 1000;

    // 500 GETs per 5 minutes is the documented limit; stay a little under it.
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(650);

    private readonly SettingsStore _store;
    private readonly TraktAuth _auth;
    private readonly HttpClient _http;
    private readonly IOperationLog _log;
    private DateTime _lastCall = DateTime.MinValue;

    public TraktClient(SettingsStore store, TraktAuth auth, HttpClient http, IOperationLog log)
    {
        _store = store;
        _auth = auth;
        _http = http;
        _log = log;
    }

    public int Calls { get; private set; }

    /// <summary>GETs a path (starting with /). Follows Trakt paging and returns every item in one array; a non-array response is returned as-is.</summary>
    public async Task<JsonNode?> GetAllAsync(string path, CancellationToken ct, bool paged = true)
    {
        var results = new JsonArray();
        var page = 1;
        while (true)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var url = paged ? $"{path}{separator}page={page}&limit={PageSize}" : path;
            var (body, pageCount) = await SendAsync(url, ct);

            if (body is not JsonArray array)
            {
                return body;
            }

            foreach (var item in array)
            {
                results.Add(item?.DeepClone());
            }

            if (!paged || page >= pageCount)
            {
                return results;
            }

            page++;
        }
    }

    /// <summary>POSTs a JSON body (writes are limited to one call per second) and returns the parsed response.</summary>
    public async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct) =>
        (await SendAsync(path, ct, HttpMethod.Post, body)).Body;

    /// <summary>The account's own settings; the wizard's Test connection step and the status bar use the user name.</summary>
    public async Task<string> AccountNameAsync(CancellationToken ct)
    {
        var settings = await GetAllAsync("/users/settings", ct, paged: false);
        var user = settings?["user"];
        var name = user?["username"]?.GetValue<string>() ?? user?["name"]?.GetValue<string>() ?? "";
        if (name.Length > 0 && _store.Current.Trakt.Username != name)
        {
            _store.Current.Trakt.Username = name;
            _store.Save();
        }

        return name;
    }

    /// <summary>
    /// The account's personal lists, newest first, plus the default watchlist as a fixed first entry.
    /// This is what the list picker shows; liked lists are excluded by the endpoint itself.
    /// </summary>
    public async Task<List<TraktListInfo>> PersonalListsAsync(CancellationToken ct)
    {
        var watchlist = await GetAllAsync("/sync/watchlist", ct);
        var lists = new List<TraktListInfo>
        {
            new(0, "watchlist", "Watchlist (default)", (watchlist as JsonArray)?.Count ?? 0, true),
        };

        foreach (var list in await GetAllAsync("/users/me/lists", ct, paged: false) as JsonArray ?? [])
        {
            if (list == null)
            {
                continue;
            }

            lists.Add(new TraktListInfo(
                list["ids"]?["trakt"]?.GetValue<long>() ?? 0,
                list["ids"]?["slug"]?.GetValue<string>() ?? "",
                list["name"]?.GetValue<string>() ?? "list",
                list["item_count"]?.GetValue<int>() ?? 0,
                false));
        }

        return lists;
    }

    /// <summary>Resolves a stored list reference to a live Trakt list id, by id first and then by name.</summary>
    public async Task<long> ResolveListIdAsync(TraktListRef reference, CancellationToken ct)
    {
        if (reference.IsWatchlist)
        {
            return 0;
        }

        var lists = await GetAllAsync("/users/me/lists", ct, paged: false) as JsonArray ?? [];
        if (reference.Id != 0 && lists.Any(l => l?["ids"]?["trakt"]?.GetValue<long>() == reference.Id))
        {
            return reference.Id;
        }

        var byName = lists.FirstOrDefault(l => string.Equals(l?["name"]?.GetValue<string>(), reference.Name, StringComparison.OrdinalIgnoreCase));
        return byName?["ids"]?["trakt"]?.GetValue<long>()
               ?? throw new InvalidOperationException($"Trakt has no list \"{reference}\" any more. Pick it again in Settings.");
    }

    /// <summary>Resolves a list reference, creating a private list with that name when it does not exist yet.</summary>
    public async Task<long> ResolveOrCreateListAsync(TraktListRef reference, CancellationToken ct)
    {
        try
        {
            return await ResolveListIdAsync(reference, ct);
        }
        catch (InvalidOperationException)
        {
            if (string.IsNullOrWhiteSpace(reference.Name))
            {
                throw;
            }

            var created = await PostAsync("/users/me/lists", new JsonObject
            {
                ["name"] = reference.Name,
                ["privacy"] = "private",
                ["display_numbers"] = false,
                ["allow_comments"] = false,
            }, ct);
            var id = created?["ids"]?["trakt"]?.GetValue<long>()
                     ?? throw new InvalidOperationException($"Could not create the Trakt list \"{reference.Name}\".");
            _log.Info($"Created the Trakt list \"{reference.Name}\".");
            return id;
        }
    }

    /// <summary>The path that reads a list's items: the watchlist or a personal list.</summary>
    public static string ItemsPath(long listId) => listId == 0 ? "/sync/watchlist" : $"/users/me/lists/{listId}/items";

    /// <summary>The path that removes items from a list.</summary>
    public static string RemovePath(long listId) => listId == 0 ? "/sync/watchlist/remove" : $"/users/me/lists/{listId}/items/remove";

    /// <summary>The path that adds items to a list.</summary>
    public static string AddPath(long listId) => listId == 0 ? "/sync/watchlist" : $"/users/me/lists/{listId}/items";

    private async Task<(JsonNode? Body, int PageCount)> SendAsync(string url, CancellationToken ct, HttpMethod? method = null, JsonNode? body = null)
    {
        method ??= HttpMethod.Get;
        var gap = method == HttpMethod.Get ? MinGap : TimeSpan.FromMilliseconds(1100);
        var refreshed = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var wait = gap - (DateTime.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            using var request = new HttpRequestMessage(method, "https://api.trakt.tv" + url);
            if (body != null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            request.Headers.Add("trakt-api-key", _store.Current.Trakt.ClientId);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.AccessToken);

            using var response = await _http.SendAsync(request, ct);
            _lastCall = DateTime.UtcNow;
            Calls++;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                _log.Detail($"rate limited; waiting {delay.TotalSeconds:0}s");
                await Task.Delay(delay + TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                await _auth.RefreshAsync(_log, ct);
                continue;
            }

            if ((int)response.StatusCode is 502 or 503 or 504 or >= 520)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TraktApiException(response.StatusCode, url);
            }

            var pageCount = 1;
            if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var parsed))
            {
                pageCount = parsed;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            return (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text), pageCount);
        }

        throw new TraktApiException(HttpStatusCode.ServiceUnavailable, url);
    }
}

public sealed record TraktListInfo(long Id, string Slug, string Name, int ItemCount, bool IsWatchlist);

public sealed class TraktApiException(HttpStatusCode status, string url)
    : Exception($"Trakt returned {(int)status} {status} for {url}")
{
    public HttpStatusCode Status { get; } = status;
}
