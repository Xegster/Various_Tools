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

        return new PlexConfig(plex.GetProperty("baseUrl").GetString()!.TrimEnd('/'), token);
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
