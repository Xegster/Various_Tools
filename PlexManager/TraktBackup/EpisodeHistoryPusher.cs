using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>
/// Adds episodes that Plex reports as watched to the Trakt history, using the CSV written by PlexScan
/// (episodes-watched-in-plex-not-trakt.csv). Each episode gets one play dated with its last Plex view.
/// Episodes already in Trakt's history are skipped, so a re-run does not create duplicate plays.
/// Shows are sent by tvdb id (falling back to tmdb, then imdb) and episodes by season and number.
/// </summary>
public sealed class EpisodeHistoryPusher
{
    private const int MaxEpisodesPerRequest = 100;

    private readonly TraktClient _client;
    private readonly string _reportsDir;

    public EpisodeHistoryPusher(TraktClient client, string reportsDir)
    {
        _client = client;
        _reportsDir = reportsDir;
    }

    private sealed record Row(string Show, int Season, int Episode, DateTime WatchedAt, string? Tvdb, string? Tmdb, string? Imdb);

    /// <summary>Builds the plan from live Trakt data. When apply is true the plan is sent after the backup runs.</summary>
    public async Task RunAsync(string csvPath, bool apply, Func<CancellationToken, Task> backup, CancellationToken ct)
    {
        var rows = ReadRows(csvPath);
        Console.WriteLine($"{rows.Count} episodes in {csvPath}");

        Console.WriteLine("Reading current Trakt episode history...");
        var history = await _client.GetAllAsync("/sync/history/episodes", ct);
        var seen = new HashSet<(string Key, int Season, int Episode)>();
        foreach (var entry in history!.AsArray())
        {
            var ids = entry!["show"]!["ids"]!;
            var season = entry["episode"]!["season"]!.GetValue<int>();
            var number = entry["episode"]!["number"]!.GetValue<int>();
            foreach (var scheme in new[] { "tvdb", "tmdb", "imdb" })
            {
                if (ids[scheme] != null)
                {
                    seen.Add(($"{scheme}:{ids[scheme]}", season, number));
                }
            }
        }

        var toSend = new List<Row>();
        var alreadyThere = 0;
        var noId = new SortedSet<string>();
        foreach (var row in rows)
        {
            if (row.Tvdb == null && row.Tmdb == null && row.Imdb == null)
            {
                noId.Add(row.Show);
            }
            else if (Keys(row).Any(k => seen.Contains((k, row.Season, row.Episode))))
            {
                alreadyThere++;
            }
            else
            {
                toSend.Add(row);
            }
        }

        Console.WriteLine($"  to add: {toSend.Count}, already on Trakt: {alreadyThere}, shows with no usable id: {noId.Count}");
        foreach (var show in noId)
        {
            Console.WriteLine($"  skip (no id): {show}");
        }

        foreach (var group in toSend.GroupBy(r => r.Show).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        if (!apply)
        {
            Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to add these plays.");
            return;
        }

        Console.WriteLine("Taking a backup first...");
        await backup(ct);

        var log = new StringBuilder("Show,Episode,WatchedAtUtc,Result\n");
        var added = 0;
        var notFoundShows = 0;
        var notFoundEpisodes = 0;
        var batchNumber = 0;
        foreach (var batch in Batches(toSend))
        {
            batchNumber++;
            var shows = new JsonArray();
            foreach (var show in batch.GroupBy(r => r.Show))
            {
                var seasons = new JsonArray();
                foreach (var season in show.GroupBy(r => r.Season).OrderBy(g => g.Key))
                {
                    var episodes = new JsonArray();
                    foreach (var ep in season.OrderBy(r => r.Episode))
                    {
                        episodes.Add(new JsonObject
                        {
                            ["number"] = ep.Episode,
                            ["watched_at"] = ep.WatchedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture),
                        });
                    }

                    seasons.Add(new JsonObject { ["number"] = season.Key, ["episodes"] = episodes });
                }

                shows.Add(new JsonObject { ["ids"] = ShowIds(show.First()), ["seasons"] = seasons });
            }

            var response = await _client.PostAsync("/sync/history", new JsonObject { ["shows"] = shows }, ct);
            var batchAdded = response?["added"]?["episodes"]?.GetValue<int>() ?? 0;
            var missingShows = response?["not_found"]?["shows"]?.AsArray().Count ?? 0;
            var missingEpisodes = response?["not_found"]?["episodes"]?.AsArray().Count ?? 0;
            added += batchAdded;
            notFoundShows += missingShows;
            notFoundEpisodes += missingEpisodes;
            Console.WriteLine($"  batch {batchNumber}: sent {batch.Count}, added {batchAdded}, shows not found {missingShows}, episodes not found {missingEpisodes}");
            foreach (var row in batch)
            {
                log.Append($"\"{row.Show.Replace("\"", "\"\"")}\",S{row.Season:00}E{row.Episode:00},{row.WatchedAt:yyyy-MM-ddTHH:mm:ssZ},sent\n");
            }

            if (missingShows > 0)
            {
                Console.WriteLine($"    shows not found on Trakt: {response!["not_found"]!["shows"]!.ToJsonString()}");
            }

            if (missingEpisodes > 0)
            {
                Console.WriteLine($"    episodes not found on Trakt (numbering differs?): {response!["not_found"]!["episodes"]!.ToJsonString()}");
            }
        }

        Directory.CreateDirectory(_reportsDir);
        var logPath = Path.Combine(_reportsDir, "trakt-episode-push-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Done. Added {added} episode plays, {notFoundShows} shows and {notFoundEpisodes} episodes not found. Log: {logPath}");
    }

    /// <summary>Groups whole shows into requests of at most MaxEpisodesPerRequest episodes (a bigger show goes alone).</summary>
    private static IEnumerable<List<Row>> Batches(List<Row> rows)
    {
        var batch = new List<Row>();
        foreach (var show in rows.GroupBy(r => r.Show))
        {
            if (batch.Count > 0 && batch.Count + show.Count() > MaxEpisodesPerRequest)
            {
                yield return batch;
                batch = [];
            }

            batch.AddRange(show);
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static IEnumerable<string> Keys(Row r)
    {
        if (r.Tvdb != null)
        {
            yield return "tvdb:" + r.Tvdb;
        }

        if (r.Tmdb != null)
        {
            yield return "tmdb:" + r.Tmdb;
        }

        if (r.Imdb != null)
        {
            yield return "imdb:" + r.Imdb;
        }
    }

    private static JsonObject ShowIds(Row r) =>
        r.Tvdb != null ? new JsonObject { ["tvdb"] = long.Parse(r.Tvdb, CultureInfo.InvariantCulture) }
        : r.Tmdb != null ? new JsonObject { ["tmdb"] = long.Parse(r.Tmdb, CultureInfo.InvariantCulture) }
        : new JsonObject { ["imdb"] = r.Imdb };

    private static List<Row> ReadRows(string path)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0))
        {
            var cells = MovieHistoryPusher.SplitCsv(line);
            string? tvdb = null;
            string? tmdb = null;
            string? imdb = null;
            foreach (var id in cells[3].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.StartsWith("tvdb:", StringComparison.Ordinal))
                {
                    tvdb = id[5..];
                }
                else if (id.StartsWith("tmdb:", StringComparison.Ordinal))
                {
                    tmdb = id[5..];
                }
                else if (id.StartsWith("imdb:", StringComparison.Ordinal))
                {
                    imdb = id[5..];
                }
            }

            // Episode column is "S01E02".
            var se = cells[1].TrimStart('S').Split('E');
            var when = DateTime.Parse(cells[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            rows.Add(new Row(cells[0], int.Parse(se[0], CultureInfo.InvariantCulture), int.Parse(se[1], CultureInfo.InvariantCulture), when, tvdb, tmdb, imdb));
        }

        return rows;
    }
}
