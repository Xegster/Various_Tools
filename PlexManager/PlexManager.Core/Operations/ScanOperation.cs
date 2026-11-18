using System.Globalization;
using System.Text.Json.Nodes;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Read-only: compares what Plex says is watched with the newest Trakt backup and writes the gap reports
/// (movies, episodes, shows Trakt has never heard of, and Plex items with no usable id).
/// </summary>
public sealed class ScanOperation
{
    private readonly OperationContext _context;

    public ScanOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<ScanResult> RunAsync(string? backupFolder, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var backup = backupFolder ?? _context.LatestBackup();
        log.Info($"Trakt data: {backup}");

        var plex = _context.Plex;
        log.Info("Reading Plex...");
        var movies = await plex.GetAllAsync(plex.MoviesPath, ct);
        var shows = await plex.GetAllAsync(plex.ShowsPath, ct);
        var episodes = await plex.GetAllAsync(plex.EpisodesPath, ct);
        log.Detail($"Plex movies: {movies.Count}, shows: {shows.Count}, episodes: {episodes.Count}");

        var traktMovies = ReadTraktMovies(Path.Combine(backup, "watched-movies.json"));
        var traktShows = ReadTraktShows(Path.Combine(backup, "history.json"));
        log.Detail($"Trakt watched movie ids: {traktMovies.Count}, episodes: {traktShows.Values.Distinct().Sum(v => v.Count)}");

        var excludedShows = new HashSet<string>(_context.Values.Exclusions.Shows, StringComparer.OrdinalIgnoreCase);
        var excludedEpisodes = new HashSet<string>(_context.Values.Exclusions.Episodes, StringComparer.OrdinalIgnoreCase);
        if (excludedShows.Count > 0)
        {
            log.Detail("Excluded shows: " + string.Join("; ", excludedShows));
        }

        // Each run gets its own folder, and Reports\latest.txt holds the newest one (the push steps accept "latest").
        var runId = RunRecord.NewId("scan");
        var reports = Path.Combine(_context.Paths.Reports, runId);
        Directory.CreateDirectory(reports);

        // ---- Movies ----
        var unmatchedMovies = new List<Dictionary<string, string>>();
        var missingMovies = new List<Dictionary<string, string>>();
        var plexWatchedMovies = 0;
        var moviesAlsoOnTrakt = 0;
        foreach (var movie in movies)
        {
            var keys = MediaIds.FromPlex(movie);
            var watched = Views(movie) > 0;
            if (watched)
            {
                plexWatchedMovies++;
            }

            if (keys.Count == 0)
            {
                unmatchedMovies.Add(new Dictionary<string, string>
                {
                    ["Title"] = Title(movie),
                    ["Year"] = Year(movie),
                    ["File"] = FilePath(movie),
                });
                continue;
            }

            if (!watched)
            {
                continue;
            }

            if (keys.Any(traktMovies.Contains))
            {
                moviesAlsoOnTrakt++;
            }
            else
            {
                missingMovies.Add(new Dictionary<string, string>
                {
                    ["Title"] = Title(movie),
                    ["Year"] = Year(movie),
                    ["Plays"] = Views(movie).ToString(CultureInfo.InvariantCulture),
                    ["LastWatchedUtc"] = WatchedAt(movie),
                    ["Ids"] = string.Join(" ", keys),
                });
            }
        }

        // ---- Episodes ----
        var showKeys = shows.ToDictionary(s => s["ratingKey"]!.GetValue<string>(), MediaIds.FromPlex);
        var showTitles = shows.ToDictionary(s => s["ratingKey"]!.GetValue<string>(), Title);
        var showsWithoutIds = new HashSet<string>();
        var showsNotOnTrakt = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingByShow = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var missingNewShows = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var showIdsByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excludedCount = 0;
        var plexWatchedEpisodes = 0;
        var episodesOnTrakt = 0;
        foreach (var episode in episodes)
        {
            if (Views(episode) == 0)
            {
                continue;
            }

            plexWatchedEpisodes++;
            var showKey = episode["grandparentRatingKey"]?.GetValue<string>() ?? "";
            var showTitle = showTitles.GetValueOrDefault(showKey, episode["grandparentTitle"]?.GetValue<string>() ?? "?");
            var season = episode["parentIndex"]?.GetValue<int>() ?? -1;
            var number = episode["index"]?.GetValue<int>() ?? -1;

            if (excludedShows.Contains(showTitle) || excludedEpisodes.Contains($"{showTitle}|S{season:00}E{number:00}"))
            {
                excludedCount++;
                continue;
            }

            var keys = showKeys.GetValueOrDefault(showKey) ?? [];
            if (keys.Count == 0)
            {
                showsWithoutIds.Add(showTitle);
                continue;
            }

            var traktSet = keys.Select(k => traktShows.GetValueOrDefault(k)).FirstOrDefault(s => s != null);
            if (traktSet == null)
            {
                showsNotOnTrakt[showTitle] = showsNotOnTrakt.GetValueOrDefault(showTitle) + 1;
                showIdsByTitle[showTitle] = string.Join(" ", keys.OrderBy(k => k, StringComparer.Ordinal));
                if (!missingNewShows.TryGetValue(showTitle, out var fresh))
                {
                    missingNewShows[showTitle] = fresh = [];
                }

                fresh.Add($"S{season:00}E{number:00}|{WatchedAt(episode)}");
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

                showIdsByTitle[showTitle] = string.Join(" ", keys.OrderBy(k => k, StringComparer.Ordinal));
                list.Add($"S{season:00}E{number:00}|{WatchedAt(episode)}");
            }
        }

        var missingEpisodeRows = missingByShow.Select(kv => (kv, onTrakt: "yes"))
            .Concat(missingNewShows.Select(kv => (kv, onTrakt: "no")))
            .OrderBy(x => x.kv.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => x.kv.Value.Select(v => new Dictionary<string, string>
            {
                ["Show"] = x.kv.Key,
                ["Episode"] = v.Split('|')[0],
                ["LastWatchedUtc"] = v.Split('|')[1],
                ["ShowIds"] = showIdsByTitle[x.kv.Key],
                ["ShowOnTrakt"] = x.onTrakt,
            }))
            .ToList();

        // ---- Output: the same four CSVs, in the same folder layout, as before ----
        string[] movieCols = ["Title", "Year", "Plays", "LastWatchedUtc", "Ids"];
        string[] unmatchedCols = ["Title", "Year", "File"];
        string[] episodeCols = ["Show", "Episode", "LastWatchedUtc", "ShowIds", "ShowOnTrakt"];
        string[] showCols = ["Show", "WatchedEpisodes"];

        var moviesCsv = Path.Combine(reports, "movies-watched-in-plex-not-trakt.csv");
        var unmatchedCsv = Path.Combine(reports, "movies-without-ids.csv");
        var episodesCsv = Path.Combine(reports, "episodes-watched-in-plex-not-trakt.csv");
        var showsCsv = Path.Combine(reports, "shows-in-plex-not-on-trakt.csv");
        var showRows = showsNotOnTrakt.Select(kv => new Dictionary<string, string>
        {
            ["Show"] = kv.Key,
            ["WatchedEpisodes"] = kv.Value.ToString(CultureInfo.InvariantCulture),
        }).ToList();

        Csv.WriteTable(moviesCsv, movieCols, missingMovies);
        Csv.WriteTable(unmatchedCsv, unmatchedCols, unmatchedMovies);
        Csv.WriteTable(episodesCsv, episodeCols, missingEpisodeRows);
        Csv.WriteTable(showsCsv, showCols, showRows);
        _context.WritePointer("latest.txt", reports);

        log.Info("");
        log.Info("=== Movies ===");
        log.Detail($"Watched in Plex:                     {plexWatchedMovies}");
        log.Detail($"...also watched on Trakt:            {moviesAlsoOnTrakt}");
        log.Detail($"...MISSING from Trakt:               {missingMovies.Count}");
        log.Detail($"Plex movies with no usable id:       {unmatchedMovies.Count}");
        log.Info("=== Episodes ===");
        log.Detail($"Watched in Plex:                     {plexWatchedEpisodes}");
        log.Detail($"...also watched on Trakt:            {episodesOnTrakt}");
        log.Detail($"...MISSING from Trakt (show known):  {missingByShow.Sum(kv => kv.Value.Count)} across {missingByShow.Count} shows");
        log.Detail($"...show not in Trakt watched at all: {showsNotOnTrakt.Values.Sum()} episodes across {showsNotOnTrakt.Count} shows");
        log.Detail($"...show has no usable id:            {showsWithoutIds.Count} shows");
        log.Detail($"...excluded by the exclusion list:   {excludedCount} episodes");

        var run = new RunRecord
        {
            Id = runId,
            Kind = "scan",
            Title = "Plex vs Trakt",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Columns = movieCols.ToList(),
            Rows = missingMovies,
            KeyColumn = "Ids",
            CsvPath = moviesCsv,
            Summary =
            [
                $"Trakt data: {backup}",
                $"Movies missing from Trakt: {missingMovies.Count}",
                $"Episodes missing from Trakt: {missingEpisodeRows.Count}",
                $"Shows Trakt has never seen: {showsNotOnTrakt.Count}",
                $"Plex movies with no id: {unmatchedMovies.Count}",
                $"Episodes excluded: {excludedCount}",
            ],
            NextStep = missingMovies.Count > 0 || missingEpisodeRows.Count > 0 ? "push-movies latest / push-episodes latest" : null,
        };
        _context.Runs.Save(run);

        log.Success($"Reports written to {reports}");
        return new ScanResult(run, reports, missingMovies, missingEpisodeRows, showRows, unmatchedMovies);
    }

    private static int Views(JsonNode item) => item["viewCount"]?.GetValue<int>() ?? 0;

    private static string Title(JsonNode item) => item["title"]?.GetValue<string>() ?? "?";

    private static string Year(JsonNode item) => item["year"]?.ToString() ?? "";

    private static string FilePath(JsonNode item) =>
        item["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault()?["file"]?.GetValue<string>() ?? "";

    private static string WatchedAt(JsonNode item) =>
        item["lastViewedAt"] is { } at
            ? DateTimeOffset.FromUnixTimeSeconds(at.GetValue<long>()).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "";

    private static HashSet<string> ReadTraktMovies(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            set.UnionWith(MediaIds.FromTrakt(entry!["movie"]!["ids"]));
        }

        return set;
    }

    /// <summary>
    /// Maps every id key of each Trakt show to the set of (season, episode) in the watch history.
    /// watched-shows.json carries no episode detail, so history.json is the source. A show's keys share one set.
    /// </summary>
    private static Dictionary<string, HashSet<(int Season, int Episode)>> ReadTraktShows(string path)
    {
        var map = new Dictionary<string, HashSet<(int, int)>>(StringComparer.Ordinal);
        foreach (var entry in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            if (entry!["type"]?.GetValue<string>() != "episode")
            {
                continue;
            }

            var keys = MediaIds.FromTrakt(entry["show"]!["ids"]);
            var watched = keys.Select(k => map.GetValueOrDefault(k)).FirstOrDefault(s => s != null) ?? [];
            watched.Add((entry["episode"]!["season"]!.GetValue<int>(), entry["episode"]!["number"]!.GetValue<int>()));
            foreach (var key in keys)
            {
                map[key] = watched;
            }
        }

        return map;
    }
}

/// <summary>What a scan found: the run it recorded, its folder, and each table so the UI need not re-read the CSVs.</summary>
public sealed record ScanResult(
    RunRecord Run,
    string Folder,
    List<Dictionary<string, string>> MissingMovies,
    List<Dictionary<string, string>> MissingEpisodes,
    List<Dictionary<string, string>> ShowsNotOnTrakt,
    List<Dictionary<string, string>> MoviesWithoutIds);
