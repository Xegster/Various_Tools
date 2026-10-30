using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>Thin client for api.trakt.tv: auth headers, paging, and 429/401 handling.</summary>
public sealed class TraktClient
{
    private const int PageSize = 1000;

    // 500 GETs per 5 minutes is the documented limit; stay a little under it.
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(650);

    private readonly TraktConfig _config;
    private readonly TraktAuth _auth;
    private readonly HttpClient _http;
    private DateTime _lastCall = DateTime.MinValue;

    public TraktClient(TraktConfig config, TraktAuth auth, HttpClient http)
    {
        _config = config;
        _auth = auth;
        _http = http;
    }

    public int Calls { get; private set; }

    /// <summary>GETs a path (starting with /). Follows Trakt paging and returns every item in one array; non-array responses are returned as-is.</summary>
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

    /// <summary>POSTs a JSON body (writes are limited to 1 call per second) and returns the parsed response.</summary>
    public async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct) =>
        (await SendAsync(path, ct, HttpMethod.Post, body)).Body;

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
                request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            }

            request.Headers.Add("trakt-api-key", _config.ClientId);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Tokens!.AccessToken);

            using var response = await _http.SendAsync(request, ct);
            _lastCall = DateTime.UtcNow;
            Calls++;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                Console.WriteLine($"  rate limited; waiting {delay.TotalSeconds:0}s");
                await Task.Delay(delay + TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                await _auth.RefreshAsync(ct);
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

public sealed class TraktApiException(HttpStatusCode status, string url)
    : Exception($"Trakt returned {(int)status} {status} for {url}")
{
    public HttpStatusCode Status { get; } = status;
}
