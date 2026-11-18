using System.Text.Json;

namespace PlexManager.Core.Settings;

/// <summary>Reads and writes <c>settings.json</c> in the data folder. Writes go through a temp file so a crash cannot leave half a file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly Lock _gate = new();

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
        Current = Read();
    }

    /// <summary>The settings in memory. Mutate and then call <see cref="Save"/>.</summary>
    public PlexManagerSettings Current { get; private set; }

    public string Path => _paths.SettingsFile;

    public bool Exists => File.Exists(_paths.SettingsFile);

    public PlexManagerSettings Reload()
    {
        lock (_gate)
        {
            return Current = Read();
        }
    }

    public void Save() => Save(Current);

    public void Save(PlexManagerSettings settings)
    {
        lock (_gate)
        {
            Normalize(settings);
            Directory.CreateDirectory(_paths.Root);
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _paths.SettingsFile, overwrite: true);
            Current = settings;
        }
    }

    /// <summary>Serializes for the UI with every secret replaced by a hint, so tokens never reach the browser.</summary>
    public static JsonDocument Redact(PlexManagerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Options);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var (section, key) in new[]
                 {
                     ("plex", "token"), ("radarr", "apiKey"), ("sonarr", "apiKey"),
                     ("trakt", "accessToken"), ("trakt", "refreshToken"), ("tmdb", "apiKey"), ("omdb", "apiKey"),
                 })
        {
            var holder = node[section];
            if (holder?[key] != null)
            {
                holder[key] = Hint(settings, section, key);
            }
        }

        return JsonSerializer.SerializeToDocument(node);
    }

    private static string Hint(PlexManagerSettings s, string section, string key) => (section, key) switch
    {
        ("plex", _) => s.Plex.Token.Hint,
        ("radarr", _) => s.Radarr.ApiKey.Hint,
        ("sonarr", _) => s.Sonarr.ApiKey.Hint,
        ("trakt", "accessToken") => s.Trakt.AccessToken.Hint,
        ("trakt", "refreshToken") => s.Trakt.RefreshToken.Hint,
        ("tmdb", _) => s.Tmdb.ApiKey.Hint,
        ("omdb", _) => s.Omdb.ApiKey.Hint,
        _ => "",
    };

    private PlexManagerSettings Read()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            var fresh = new PlexManagerSettings();
            Normalize(fresh);
            return fresh;
        }

        var settings = JsonSerializer.Deserialize<PlexManagerSettings>(File.ReadAllText(_paths.SettingsFile), Options) ?? new PlexManagerSettings();
        Normalize(settings);
        return settings;
    }

    private static void Normalize(PlexManagerSettings settings)
    {
        settings.Radarr.Kind = "Radarr";
        settings.Sonarr.Kind = "Sonarr";
        if (settings.Radarr.Port == 0)
        {
            settings.Radarr.Port = 7878;
        }

        if (settings.Sonarr.Port == 0)
        {
            settings.Sonarr.Port = 8989;
        }

        if (settings.Plex.Port == 0)
        {
            settings.Plex.Port = 32400;
        }

        if (string.IsNullOrWhiteSpace(settings.Trakt.RedirectUri))
        {
            settings.Trakt.RedirectUri = "http://localhost";
        }

        if (settings.Migration.SourceLists.Count == 0)
        {
            settings.Migration.SourceLists.Add(TraktListRef.Watchlist());
        }
    }
}
