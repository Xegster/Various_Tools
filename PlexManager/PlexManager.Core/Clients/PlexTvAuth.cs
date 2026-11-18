using System.Text.Json.Nodes;

namespace PlexManager.Core.Clients;

/// <summary>
/// "Sign in with Plex": the plex.tv PIN flow, so the wizard can fetch the server token instead of asking the user
/// to dig it out of Plex Web. A PIN is created, the user approves it on plex.tv, and the PIN is then polled for the token.
/// </summary>
public sealed class PlexTvAuth
{
    private const string Product = "PlexManager";

    private readonly HttpClient _http;

    public PlexTvAuth(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Creates a PIN. The caller shows <see cref="PlexPin.Url"/> (and the code) and then polls.</summary>
    public async Task<PlexPin> StartAsync(string clientIdentifier, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true");
        AddHeaders(request, clientIdentifier);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var id = body["id"]!.GetValue<long>();
        var code = body["code"]!.GetValue<string>();
        var url = $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(clientIdentifier)}&code={Uri.EscapeDataString(code)}"
                  + $"&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(Product)}";
        return new PlexPin(id, code, url, clientIdentifier);
    }

    /// <summary>One poll. Returns the token once the user has approved the PIN, otherwise null.</summary>
    public async Task<string?> PollAsync(PlexPin pin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://plex.tv/api/v2/pins/{pin.Id}");
        AddHeaders(request, pin.ClientIdentifier);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("The Plex sign-in code expired. Start again.");
        }

        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var token = body["authToken"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>The servers the account owns, so the wizard can offer an address instead of asking for one.</summary>
    public async Task<List<PlexResource>> ServersAsync(string accountToken, string clientIdentifier, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=0");
        AddHeaders(request, clientIdentifier);
        request.Headers.Add("X-Plex-Token", accountToken);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var servers = new List<PlexResource>();
        foreach (var resource in JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray() ?? [])
        {
            if (resource?["provides"]?.GetValue<string>()?.Contains("server", StringComparison.Ordinal) != true)
            {
                continue;
            }

            var name = resource["name"]?.GetValue<string>() ?? "Plex";
            var token = resource["accessToken"]?.GetValue<string>() ?? accountToken;
            foreach (var connection in resource["connections"]?.AsArray() ?? [])
            {
                if (connection?["local"]?.GetValue<bool>() == true)
                {
                    servers.Add(new PlexResource(
                        name,
                        connection["address"]?.GetValue<string>() ?? "",
                        connection["port"]?.GetValue<int>() ?? 32400,
                        connection["protocol"]?.GetValue<string>() == "https",
                        token));
                }
            }
        }

        return servers;
    }

    /// <summary>A stable per-install identifier; plex.tv ties the PIN and the token to it.</summary>
    public static string ClientIdentifier(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, "client-id.txt");
        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(file, id);
        return id;
    }

    private static void AddHeaders(HttpRequestMessage request, string clientIdentifier)
    {
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Product", Product);
        request.Headers.Add("X-Plex-Version", "1.0");
        request.Headers.Add("X-Plex-Client-Identifier", clientIdentifier);
        request.Headers.Add("X-Plex-Platform", "Windows");
        request.Headers.Add("X-Plex-Device", "PlexManager");
    }
}

public sealed record PlexPin(long Id, string Code, string Url, string ClientIdentifier);

public sealed record PlexResource(string Name, string Address, int Port, bool Https, string Token);
