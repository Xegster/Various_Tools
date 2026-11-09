using System.Text.Json;

namespace PlexScan;

/// <summary>Connection settings from plexmanager.config.json, found by walking up from the working or build folder.</summary>
public sealed record PlexConfig(string BaseUrl, string Token)
{
    public const string FileName = "plexmanager.config.json";

    /// <summary>Folder that holds plexmanager.config.json (the PlexManager folder).</summary>
    public static string HomeDirectory() =>
        Path.GetDirectoryName(FindFile()) ?? Directory.GetCurrentDirectory();

    public static PlexConfig Load()
    {
        var path = FindFile() ?? throw new FileNotFoundException($"{FileName} not found. Copy plexmanager.config.example.json and fill it in.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var plex = doc.RootElement.GetProperty("plex");
        var token = plex.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"plex.token is empty in {path}.");
        }

        return new PlexConfig(ServiceUrl(doc.RootElement, "plex"), token);
    }

    /// <summary>
    /// Base URL of a service: its own "baseUrl" if it has one, otherwise http://{host}:{port} from the single top-level "host"
    /// and the service's "port", so the LAN address lives in one place.
    /// </summary>
    public static string ServiceUrl(JsonElement root, string service)
    {
        var section = root.GetProperty(service);
        if (section.TryGetProperty("baseUrl", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            return url.GetString()!.TrimEnd('/');
        }

        var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
        return string.IsNullOrWhiteSpace(host)
            ? throw new InvalidOperationException($"Set \"host\" (the LAN address) at the top of {FileName}, or a baseUrl for {service}.")
            : $"http://{host}:{section.GetProperty("port").GetInt32()}";
    }

    private static string? FindFile()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
