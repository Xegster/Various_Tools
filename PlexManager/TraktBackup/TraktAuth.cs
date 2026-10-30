using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraktBackup;

public sealed class TraktTokens
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }

    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(CreatedAt + ExpiresIn);
}

/// <summary>Device-code OAuth for Trakt. Refresh tokens are single-use, so every new pair is written to disk before it is used.</summary>
public sealed class TraktAuth
{
    // Trakt wants every OAuth request on the auth host, not the API host.
    private static readonly Uri ApiBase = new("https://auth.trakt.tv");

    private readonly TraktConfig _config;
    private readonly HttpClient _http;
    private readonly string _tokenPath;

    public TraktAuth(TraktConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
        _tokenPath = Path.Combine(TraktConfig.HomeDirectory(), "trakt.tokens.json");
    }

    public TraktTokens? Tokens { get; private set; }

    /// <summary>Loads saved tokens, refreshing or running the device flow as needed.</summary>
    public async Task EnsureSignedInAsync(CancellationToken ct)
    {
        if (File.Exists(_tokenPath))
        {
            Tokens = JsonSerializer.Deserialize<TraktTokens>(File.ReadAllText(_tokenPath));
        }

        if (Tokens == null)
        {
            await DeviceFlowAsync(ct);
        }
        else if (Tokens.ExpiresAt < DateTimeOffset.UtcNow.AddHours(1))
        {
            await RefreshAsync(ct);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Tokens == null)
        {
            await DeviceFlowAsync(ct);
            return;
        }

        var response = await _http.PostAsJsonAsync(new Uri(ApiBase, "/oauth/token"), new
        {
            refresh_token = Tokens.RefreshToken,
            client_id = _config.ClientId,
            redirect_uri = _config.RedirectUri,
            grant_type = "refresh_token",
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Token refresh failed ({(int)response.StatusCode}); signing in again.");
            await DeviceFlowAsync(ct);
            return;
        }

        Save((await response.Content.ReadFromJsonAsync<TraktTokens>(ct))!);
    }

    private async Task DeviceFlowAsync(CancellationToken ct)
    {
        var codeResponse = await _http.PostAsJsonAsync(new Uri(ApiBase, "/oauth/device/code"), new { client_id = _config.ClientId }, ct);
        codeResponse.EnsureSuccessStatusCode();
        var code = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

        var userCode = code.GetProperty("user_code").GetString();
        var url = code.GetProperty("verification_url").GetString();
        var deviceCode = code.GetProperty("device_code").GetString();
        var interval = code.GetProperty("interval").GetInt32();
        var deadline = DateTime.UtcNow.AddSeconds(code.GetProperty("expires_in").GetInt32());

        Console.WriteLine();
        Console.WriteLine($"Open {url} and enter the code: {userCode}");
        Console.WriteLine("Waiting for approval...");

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var poll = await _http.PostAsJsonAsync(new Uri(ApiBase, "/oauth/device/token"), new
            {
                code = deviceCode,
                client_id = _config.ClientId,
            }, ct);

            switch ((int)poll.StatusCode)
            {
                case 200:
                    Save((await poll.Content.ReadFromJsonAsync<TraktTokens>(ct))!);
                    Console.WriteLine("Signed in.");
                    return;
                case 400: // pending
                    break;
                case 429: // slow down
                    interval += 1;
                    break;
                case 404:
                case 409:
                case 410:
                case 418:
                    throw new InvalidOperationException($"Sign-in failed ({(int)poll.StatusCode}: {poll.ReasonPhrase}).");
                default:
                    poll.EnsureSuccessStatusCode();
                    break;
            }
        }

        throw new TimeoutException("Sign-in code expired before it was approved.");
    }

    private void Save(TraktTokens tokens)
    {
        var temp = _tokenPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _tokenPath, overwrite: true);
        Tokens = tokens;
    }
}
