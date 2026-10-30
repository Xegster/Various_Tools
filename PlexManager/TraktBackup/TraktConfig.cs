using System.Text.Json;

namespace TraktBackup;

/// <summary>Client id of the user's Trakt API app (no secret: the app uses PKCE/device sign-in), read from trakt.config.json or the TRAKT_CLIENT_ID environment variable.</summary>
public sealed class TraktConfig
{
    public string ClientId { get; set; } = "";

    /// <summary>Must match the redirect URI registered on the Trakt app; only sent on token refresh.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    public static TraktConfig Load()
    {
        var config = new TraktConfig();
        var path = FindFile("trakt.config.json");
        if (path != null)
        {
            config = JsonSerializer.Deserialize<TraktConfig>(File.ReadAllText(path)) ?? config;
        }

        config.ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? config.ClientId;

        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            throw new InvalidOperationException(
                "Missing Trakt Client ID. Copy trakt.config.example.json to trakt.config.json and fill in ClientId (or set TRAKT_CLIENT_ID).");
        }

        return config;
    }

    /// <summary>Folder that holds trakt.config.json, tokens and Backups: found from the working directory or the exe, else the working directory.</summary>
    public static string HomeDirectory() =>
        Path.GetDirectoryName(FindFile("trakt.config.json")) ?? Directory.GetCurrentDirectory();

    /// <summary>Looks in the working directory and up the tree, then next to the executable and up the tree (so `dotnet run` and bin\ both work).</summary>
    public static string? FindFile(string name)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
