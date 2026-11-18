using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlexManager.Core.Util;

/// <summary>
/// Turns the many ways Plex and Trakt name a title into one set of comparable keys such as
/// <c>tmdb:603</c>, <c>imdb:tt0133093</c>, <c>tvdb:81189</c> or <c>plex:5d776b...</c>.
/// Extracted from the four copies that lived in PlexScan, PlexPrune, PlexFix and PlexMigrate.
/// </summary>
public static partial class MediaIds
{
    private static readonly string[] Schemes = ["imdb", "tmdb", "tvdb"];

    /// <summary>Every key a Plex item carries, from its Guid tags, its guid attribute and legacy agent guids.</summary>
    public static HashSet<string> FromPlex(JsonNode item)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guid in item["Guid"]?.AsArray() ?? [])
        {
            AddPlexGuid(keys, guid?["id"]?.GetValue<string>());
        }

        AddPlexGuid(keys, item["guid"]?.GetValue<string>());
        return keys;
    }

    /// <summary>True when Plex has matched the item to a real metadata record (any external or plex:// id).</summary>
    public static bool PlexIsMatched(JsonNode item)
    {
        var guids = (item["Guid"]?.AsArray() ?? []).Select(g => g?["id"]?.GetValue<string>()).ToList();
        guids.Add(item["guid"]?.GetValue<string>());
        return guids.Any(g => g != null
            && (g.StartsWith("imdb://", StringComparison.Ordinal) || g.StartsWith("tmdb://", StringComparison.Ordinal)
                || g.StartsWith("tvdb://", StringComparison.Ordinal) || g.StartsWith("plex://", StringComparison.Ordinal)
                || LegacyAgent().IsMatch(g)));
    }

    /// <summary>Every key in a Trakt "ids" object, including its plex guid when Trakt knows one.</summary>
    public static HashSet<string> FromTrakt(JsonNode? ids)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (ids == null)
        {
            return keys;
        }

        foreach (var scheme in Schemes)
        {
            if (ids[scheme] != null)
            {
                keys.Add($"{scheme}:{ids[scheme]}");
            }
        }

        if (ids["plex"]?["guid"]?.GetValue<string>() is { Length: > 0 } plexGuid)
        {
            keys.Add("plex:" + plexGuid);
        }

        return keys;
    }

    /// <summary>The external-id keys of a Radarr movie or Sonarr series record.</summary>
    public static IEnumerable<string> FromArr(JsonNode entry, bool isMovie)
    {
        if (isMovie)
        {
            if (entry["tmdbId"] is { } tmdb && tmdb.GetValue<int>() > 0)
            {
                yield return "tmdb:" + tmdb;
            }
        }
        else if (entry["tvdbId"] is { } tvdb && tvdb.GetValue<int>() > 0)
        {
            yield return "tvdb:" + tvdb;
        }

        if (entry["imdbId"]?.GetValue<string>() is { Length: > 0 } imdb)
        {
            yield return "imdb:" + imdb;
        }
    }

    private static void AddPlexGuid(HashSet<string> keys, string? guid)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return;
        }

        foreach (var scheme in Schemes)
        {
            if (guid.StartsWith(scheme + "://", StringComparison.Ordinal))
            {
                keys.Add($"{scheme}:{guid[(scheme.Length + 3)..]}");
                return;
            }
        }

        if (guid.StartsWith("plex://", StringComparison.Ordinal))
        {
            keys.Add("plex:" + guid[(guid.LastIndexOf('/') + 1)..]);
            return;
        }

        // Legacy agents: com.plexapp.agents.themoviedb://611605?lang=en, ...imdb://tt123, ...thetvdb://12345
        var legacy = LegacyAgent().Match(guid);
        if (legacy.Success)
        {
            var scheme = legacy.Groups[1].Value switch
            {
                "themoviedb" => "tmdb",
                "thetvdb" => "tvdb",
                _ => "imdb",
            };
            keys.Add($"{scheme}:{legacy.Groups[2].Value}");
        }
    }

    [GeneratedRegex(@"agents\.(themoviedb|imdb|thetvdb)://([^/?]+)")]
    private static partial Regex LegacyAgent();
}
