using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Clients;

/// <summary>
/// Device-code OAuth for Trakt. The tokens live in settings.json (DPAPI-encrypted), not in a separate file.
/// Refresh tokens are single-use, so every new pair is saved before it is used.
/// The flow is split into Start and Poll so the wizard can show the code while it waits; the CLI uses <see cref="SignInAsync"/>.
/// </summary>
public sealed class TraktAuth
{
    // Trakt wants every OAuth request on the auth host, not the API host.
    private static readonly Uri AuthBase = new("https://auth.trakt.tv");

    private readonly SettingsStore _store;
    private readonly HttpClient _http;

    public TraktAuth(SettingsStore store, HttpClient http)
    {
        _store = store;
        _http = http;
    }

    private TraktSettings Trakt => _store.Current.Trakt;

    public string AccessToken => Trakt.AccessToken;

    public bool IsSignedIn => Trakt.HasTokens;

    /// <summary>Makes sure there is a usable access token, refreshing it when it is close to expiry.</summary>
    public async Task EnsureSignedInAsync(IOperationLog log, CancellationToken ct)
    {
        RequireClientId();

        if (!Trakt.HasTokens)
        {
            await SignInAsync(log, ct);
            return;
        }

        if (Trakt.ExpiresAt < DateTimeOffset.UtcNow.AddHours(1))
        {
            await RefreshAsync(log, ct);
        }
    }

    public async Task RefreshAsync(IOperationLog log, CancellationToken ct)
    {
        RequireClientId();
        if (!Trakt.HasTokens)
        {
            await SignInAsync(log, ct);
            return;
        }

        var response = await _http.PostAsJsonAsync(new Uri(AuthBase, "/oauth/token"), new
        {
            refresh_token = Trakt.RefreshToken.Value,
            client_id = Trakt.ClientId,
            redirect_uri = Trakt.RedirectUri,
            grant_type = "refresh_token",
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            log.Warn($"Trakt token refresh failed ({(int)response.StatusCode}); signing in again.");
            await SignInAsync(log, ct);
            return;
        }

        Save(await response.Content.ReadFromJsonAsync<JsonNode>(ct));
    }

    /// <summary>Console sign-in: prints the code and URL, then polls until Trakt says yes.</summary>
    public async Task SignInAsync(IOperationLog log, CancellationToken ct)
    {
        var device = await StartDeviceAsync(ct);
        log.Info("");
        log.Info($"Open {device.VerificationUrl} and enter the code: {device.UserCode}");
        log.Info("Waiting for approval...");

        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = device.Interval;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var result = await PollDeviceAsync(device.DeviceCode, ct);
            if (result.State == TraktDeviceState.Signedin)
            {
                log.Success("Signed in to Trakt.");
                return;
            }

            if (result.State == TraktDeviceState.SlowDown)
            {
                interval++;
            }
            else if (result.State == TraktDeviceState.Failed)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        throw new TimeoutException("The Trakt sign-in code expired before it was approved.");
    }

    /// <summary>Asks Trakt for a device code. The caller shows the code and URL and then polls.</summary>
    public async Task<TraktDeviceCode> StartDeviceAsync(CancellationToken ct)
    {
        RequireClientId();
        var response = await _http.PostAsJsonAsync(new Uri(AuthBase, "/oauth/device/code"), new { client_id = Trakt.ClientId }, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Trakt rejected the Client ID ({(int)response.StatusCode}). Check it on https://trakt.tv/oauth/applications.");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new TraktDeviceCode(
            body.GetProperty("device_code").GetString()!,
            body.GetProperty("user_code").GetString()!,
            body.GetProperty("verification_url").GetString()!,
            body.GetProperty("interval").GetInt32(),
            body.GetProperty("expires_in").GetInt32());
    }

    /// <summary>One poll of a device code. Saves the tokens when the user has approved it.</summary>
    public async Task<TraktDeviceResult> PollDeviceAsync(string deviceCode, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(new Uri(AuthBase, "/oauth/device/token"), new
        {
            code = deviceCode,
            client_id = Trakt.ClientId,
        }, ct);

        switch ((int)response.StatusCode)
        {
            case 200:
                Save(await response.Content.ReadFromJsonAsync<JsonNode>(ct));
                return new TraktDeviceResult(TraktDeviceState.Signedin, "Signed in.");
            case 400:
                return new TraktDeviceResult(TraktDeviceState.Pending, "Waiting for approval on trakt.tv.");
            case 429:
                return new TraktDeviceResult(TraktDeviceState.SlowDown, "Polling too fast; slowing down.");
            case 404:
                return new TraktDeviceResult(TraktDeviceState.Failed, "Trakt does not know that code any more. Start again.");
            case 409:
                return new TraktDeviceResult(TraktDeviceState.Signedin, "Already approved.");
            case 410:
                return new TraktDeviceResult(TraktDeviceState.Failed, "The code expired. Start again.");
            case 418:
                return new TraktDeviceResult(TraktDeviceState.Failed, "The sign-in was denied on trakt.tv.");
            default:
                return new TraktDeviceResult(TraktDeviceState.Failed, $"Trakt returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    /// <summary>Forgets the tokens (the Settings screen's Disconnect button).</summary>
    public void SignOut()
    {
        Trakt.AccessToken = new Secret();
        Trakt.RefreshToken = new Secret();
        Trakt.CreatedAt = 0;
        Trakt.ExpiresIn = 0;
        Trakt.Username = "";
        _store.Save();
    }

    private void RequireClientId()
    {
        if (string.IsNullOrWhiteSpace(Trakt.ClientId))
        {
            throw new SettingsIncompleteException(
                "No Trakt Client ID. Create an API app at https://trakt.tv/oauth/applications (redirect URI "
                + Trakt.RedirectUri + ") and paste its Client ID into Settings.");
        }
    }

    private void Save(JsonNode? tokens)
    {
        var access = tokens?["access_token"]?.GetValue<string>();
        var refresh = tokens?["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
        {
            throw new InvalidOperationException("Trakt returned no tokens.");
        }

        Trakt.AccessToken = new Secret(access);
        Trakt.RefreshToken = new Secret(refresh);
        Trakt.ExpiresIn = tokens!["expires_in"]?.GetValue<int>() ?? 0;
        Trakt.CreatedAt = tokens["created_at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _store.Save();
    }
}

public sealed record TraktDeviceCode(string DeviceCode, string UserCode, string VerificationUrl, int Interval, int ExpiresIn);

public enum TraktDeviceState
{
    Pending,
    SlowDown,
    Signedin,
    Failed,
}

public sealed record TraktDeviceResult(TraktDeviceState State, string Message);
