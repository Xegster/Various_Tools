using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlexScan;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Read-only: compares what Plex says is watched with the latest Trakt backup and writes CSV reports to Reports\.
    // Usage: PlexScan [trakt-backup-folder]   (defaults to the newest folder in TraktBackup\Backups)
    var config = PlexConfig.Load();
    var home = PlexConfig.HomeDirectory();
    var backup = args.Length > 0 ? args[0] : LatestBackup(home);
    Console.WriteLine($"Trakt data: {backup}");

    using var http = new HttpClient();
    var plex = new PlexClient(config, http);
    var ct = cts.Token;

    Console.WriteLine("Reading Plex...");
    var movies = await plex.GetAllAsync("/library/sections/4/all", ct);
    var shows = await plex.GetAllAsync("/library/sections/1/all?type=2", ct);
    var episodes = await plex.GetAllAsync("/library/sections/1/all?type=4", ct);
    Console.WriteLine($"  Plex movies: {movies.Count}, shows: {shows.Count}, episodes: {episodes.Count}");

    var traktMovies = ReadTraktMovies(Path.Combine(backup, "watched-movies.json"));
    var traktShows = ReadTraktShows(Path.Combine(backup, "history.json"));
    Console.WriteLine($"  Trakt watched movie ids: {traktMovies.Count}, show episode sets: {traktShows.Values.Distinct().Count()}, episodes: {traktShows.Values.Distinct().Sum(v => v.Count)}");

    var reports = Path.Combine(home, "Reports");
    Directory.CreateDirectory(reports);

    // ---- Movies ----
    var unmatchedMovies = new List<string[]>();
    var missingMovies = new List<string[]>();
    var plexWatchedMovies = 0;
    var plexMoviesMatchedToTraktWatched = 0;
    foreach (var m in movies)
    {
        var keys = Keys(m);
        var watched = Views(m) > 0;
        if (watched)
        {
            plexWatchedMovies++;
        }

        if (keys.Count == 0)
        {
            unmatchedMovies.Add([Title(m), Year(m), Path0(m)]);
            continue;
        }

        if (!watched)
        {
            continue;
        }

        if (keys.Any(traktMovies.Contains))
        {
            plexMoviesMatchedToTraktWatched++;
        }
        else
        {
            missingMovies.Add([Title(m), Year(m), Views(m).ToString(), WatchedAt(m), string.Join(" ", keys)]);
        }
    }

    // ---- Episodes ----
    var showKeys = shows.ToDictionary(s => s["ratingKey"]!.GetValue<string>(), Keys);
    var showTitles = shows.ToDictionary(s => s["ratingKey"]!.GetValue<string>(), Title);
    var showsWithoutIds = new HashSet<string>();
    var showsNotOnTrakt = new SortedDictionary<string, int>();
    var missingByShow = new SortedDictionary<string, List<string>>();
    var plexWatchedEpisodes = 0;
    var episodesOnTrakt = 0;
    foreach (var e in episodes)
    {
        if (Views(e) == 0)
        {
            continue;
        }

        plexWatchedEpisodes++;
        var showKey = e["grandparentRatingKey"]?.GetValue<string>() ?? "";
        var showTitle = showTitles.GetValueOrDefault(showKey, e["grandparentTitle"]?.GetValue<string>() ?? "?");
        var keys = showKeys.GetValueOrDefault(showKey) ?? [];
        if (keys.Count == 0)
        {
            showsWithoutIds.Add(showTitle);
            continue;
        }

        var traktSet = keys.Select(k => traktShows.GetValueOrDefault(k)).FirstOrDefault(s => s != null);
        var season = e["parentIndex"]?.GetValue<int>() ?? -1;
        var number = e["index"]?.GetValue<int>() ?? -1;
        if (traktSet == null)
        {
            showsNotOnTrakt[showTitle] = showsNotOnTrakt.GetValueOrDefault(showTitle) + 1;
        }
        else if (traktSet.Contains((season, number)))
        {
            episodesOnTrakt++;
        }
        else
        {
            if (!missingByShow.TryGetValue(showTitle, out var list))
            {
                missingByShow[showTitle] = list = [];
            }

            list.Add($"S{season:00}E{number:00}|{WatchedAt(e)}");
        }
    }

    // ---- Output ----
    WriteCsv(Path.Combine(reports, "movies-watched-in-plex-not-trakt.csv"), ["Title", "Year", "Plays", "LastWatchedUtc", "Ids"], missingMovies);
    WriteCsv(Path.Combine(reports, "movies-without-ids.csv"), ["Title", "Year", "File"], unmatchedMovies);
    WriteCsv(Path.Combine(reports, "episodes-watched-in-plex-not-trakt.csv"), ["Show", "Episode", "LastWatchedUtc"],
        missingByShow.SelectMany(kv => kv.Value.Select(v => new[] { kv.Key, v.Split('|')[0], v.Split('|')[1] })).ToList());
    WriteCsv(Path.Combine(reports, "shows-in-plex-not-on-trakt.csv"), ["Show", "WatchedEpisodes"],
        showsNotOnTrakt.Select(kv => new[] { kv.Key, kv.Value.ToString() }).ToList());

    Console.WriteLine();
    Console.WriteLine("=== Movies ===");
    Console.WriteLine($"  Watched in Plex:                         {plexWatchedMovies}");
    Console.WriteLine($"  ...also watched on Trakt:                {plexMoviesMatchedToTraktWatched}");
    Console.WriteLine($"  ...MISSING from Trakt:                   {missingMovies.Count}");
    Console.WriteLine($"  Plex movies with no usable id:           {unmatchedMovies.Count}");
    Console.WriteLine("=== Episodes ===");
    Console.WriteLine($"  Watched in Plex:                         {plexWatchedEpisodes}");
    Console.WriteLine($"  ...also watched on Trakt:                {episodesOnTrakt}");
    Console.WriteLine($"  ...MISSING from Trakt (show known):      {missingByShow.Sum(kv => kv.Value.Count)} across {missingByShow.Count} shows");
    Console.WriteLine($"  ...show not in Trakt watched at all:     {showsNotOnTrakt.Values.Sum()} episodes across {showsNotOnTrakt.Count} shows");
    Console.WriteLine($"  ...show has no usable id:                {showsWithoutIds.Count} shows");
    Console.WriteLine();
    Console.WriteLine($"Reports written to {reports}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string LatestBackup(string home)
{
    var root = Path.Combine(home, "TraktBackup", "Backups");
    return Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
        ?? throw new DirectoryNotFoundException($"No Trakt backups in {root}. Run TraktBackup first.");
}

/// <summary>Normalised id keys such as "tmdb:123", "imdb:tt1", "tvdb:9", "plex:abc" from an item's Guid tags and its guid attribute (including legacy agent guids).</summary>
static HashSet<string> Keys(JsonNode item)
{
    var keys = new HashSet<string>();
    var guids = item["Guid"]?.AsArray();
    if (guids != null)
    {
        foreach (var g in guids)
        {
            AddKey(keys, g?["id"]?.GetValue<string>());
        }
    }

    AddKey(keys, item["guid"]?.GetValue<string>());
    return keys;
}

static void AddKey(HashSet<string> keys, string? guid)
{
    if (string.IsNullOrEmpty(guid))
    {
        return;
    }

    foreach (var scheme in new[] { "imdb", "tmdb", "tvdb" })
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

    // Legacy agents: com.plexapp.agents.themoviedb://611605?lang=en, ...imdb://tt123?lang=en, ...thetvdb://12345?lang=en
    var legacy = Regex.Match(guid, @"agents\.(themoviedb|imdb|thetvdb)://([^/?]+)");
    if (legacy.Success)
    {
        var scheme = legacy.Groups[1].Value switch { "themoviedb" => "tmdb", "thetvdb" => "tvdb", _ => "imdb" };
        keys.Add($"{scheme}:{legacy.Groups[2].Value}");
    }
}

static HashSet<string> TraktKeys(JsonNode ids)
{
    var keys = new HashSet<string>();
    foreach (var scheme in new[] { "imdb", "tmdb", "tvdb" })
    {
        var v = ids[scheme];
        if (v != null)
        {
            keys.Add($"{scheme}:{v}");
        }
    }

    var plexGuid = ids["plex"]?["guid"]?.GetValue<string>();
    if (plexGuid != null)
    {
        keys.Add("plex:" + plexGuid);
    }

    return keys;
}

static HashSet<string> ReadTraktMovies(string path)
{
    var set = new HashSet<string>();
    foreach (var entry in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
    {
        set.UnionWith(TraktKeys(entry!["movie"]!["ids"]!));
    }

    return set;
}

/// <summary>
/// Maps every id key of each Trakt show to the set of (season, episode) in the watch history.
/// watched-shows.json in the backup carries no episode detail, so history.json is the source. A show's keys share one set.
/// </summary>
static Dictionary<string, HashSet<(int Season, int Episode)>> ReadTraktShows(string path)
{
    var map = new Dictionary<string, HashSet<(int, int)>>();
    foreach (var entry in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
    {
        if (entry!["type"]?.GetValue<string>() != "episode")
        {
            continue;
        }

        var keys = TraktKeys(entry["show"]!["ids"]!);
        var watched = keys.Select(k => map.GetValueOrDefault(k)).FirstOrDefault(s => s != null) ?? [];
        watched.Add((entry["episode"]!["season"]!.GetValue<int>(), entry["episode"]!["number"]!.GetValue<int>()));
        foreach (var key in keys)
        {
            map[key] = watched;
        }
    }

    return map;
}

static int Views(JsonNode item) => item["viewCount"]?.GetValue<int>() ?? 0;

static string Title(JsonNode item) => item["title"]?.GetValue<string>() ?? "?";

static string Year(JsonNode item) => item["year"]?.ToString() ?? "";

static string Path0(JsonNode item) =>
    item["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault()?["file"]?.GetValue<string>() ?? "";

static string WatchedAt(JsonNode item) =>
    item["lastViewedAt"] is { } t
        ? DateTimeOffset.FromUnixTimeSeconds(t.GetValue<long>()).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : "";

static void WriteCsv(string path, string[] header, List<string[]> rows)
{
    static string Cell(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    var sb = new StringBuilder();
    sb.AppendLine(string.Join(",", header.Select(Cell)));
    foreach (var row in rows)
    {
        sb.AppendLine(string.Join(",", row.Select(Cell)));
    }

    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
}
