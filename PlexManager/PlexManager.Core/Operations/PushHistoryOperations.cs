using System.Globalization;
using System.Text.Json.Nodes;
using PlexManager.Core.Clients;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Adds movies Plex reports as watched to the Trakt history, from the scan's
/// movies-watched-in-plex-not-trakt.csv. Each movie gets one play dated with its last Plex view, and movies
/// already in Trakt's watched list are skipped, so a re-run does not create duplicate plays.
/// </summary>
public sealed class PushMoviesOperation
{
    private const int BatchSize = 25;

    private readonly OperationContext _context;

    public PushMoviesOperation(OperationContext context)
    {
        _context = context;
    }

    private sealed record Row(string Title, string Year, DateTime WatchedAt, string? Tmdb, string? Imdb);

    public async Task<RunRecord> RunAsync(string csvPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var rows = ReadRows(csvPath);
        log.Info($"{rows.Count} movies in {csvPath}");

        var client = await _context.TraktAsync(ct);
        log.Info("Reading the current Trakt watched movies...");
        var watched = await client.GetAllAsync("/sync/watched/movies", ct);
        var tmdbSeen = new HashSet<string>(StringComparer.Ordinal);
        var imdbSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in watched!.AsArray())
        {
            var ids = entry!["movie"]!["ids"]!;
            if (ids["tmdb"] != null)
            {
                tmdbSeen.Add(ids["tmdb"]!.ToString());
            }

            if (ids["imdb"] != null)
            {
                imdbSeen.Add(ids["imdb"]!.ToString());
            }
        }

        var toSend = new List<Row>();
        var alreadyThere = new List<Row>();
        var noId = new List<Row>();
        foreach (var row in rows)
        {
            if (row.Tmdb == null && row.Imdb == null)
            {
                noId.Add(row);
            }
            else if ((row.Tmdb != null && tmdbSeen.Contains(row.Tmdb)) || (row.Imdb != null && imdbSeen.Contains(row.Imdb)))
            {
                alreadyThere.Add(row);
            }
            else
            {
                toSend.Add(row);
            }
        }

        log.Detail($"to add: {toSend.Count}, already on Trakt: {alreadyThere.Count}, no usable id: {noId.Count}");
        foreach (var row in alreadyThere)
        {
            log.Detail($"skip (already on Trakt): {row.Title} ({row.Year})");
        }

        foreach (var row in noId)
        {
            log.Detail($"skip (no id): {row.Title} ({row.Year})");
        }

        var logRows = new List<Dictionary<string, string>>();
        var added = 0;
        var notFound = 0;

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to add these plays.");
            foreach (var row in toSend)
            {
                logRows.Add(LogRow(row, "would add"));
            }
        }
        else
        {
            log.Info("Taking a Trakt backup first...");
            await new BackupOperation(_context).RunAsync(client, Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), ct);

            for (var i = 0; i < toSend.Count; i += BatchSize)
            {
                var batch = toSend.Skip(i).Take(BatchSize).ToList();
                var movies = new JsonArray();
                foreach (var row in batch)
                {
                    var ids = row.Tmdb != null
                        ? new JsonObject { ["tmdb"] = long.Parse(row.Tmdb, CultureInfo.InvariantCulture) }
                        : new JsonObject { ["imdb"] = row.Imdb };
                    movies.Add(new JsonObject
                    {
                        ["watched_at"] = row.WatchedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture),
                        ["ids"] = ids,
                    });
                }

                var response = await client.PostAsync("/sync/history", new JsonObject { ["movies"] = movies }, ct);
                var batchAdded = response?["added"]?["movies"]?.GetValue<int>() ?? 0;
                var missing = response?["not_found"]?["movies"]?.AsArray().Count ?? 0;
                added += batchAdded;
                notFound += missing;
                log.Detail($"batch {i / BatchSize + 1}: added {batchAdded}, not found {missing}");
                foreach (var row in batch)
                {
                    logRows.Add(LogRow(row, "sent"));
                }

                if (missing > 0)
                {
                    foreach (var entry in response!["not_found"]!["movies"]!.AsArray())
                    {
                        log.Warn($"not found on Trakt: {entry!.ToJsonString()}");
                    }
                }
            }
        }

        var run = new RunRecord
        {
            Id = RunRecord.NewId("push-movies"),
            Kind = "push-movies",
            Title = apply ? "Push movie history (applied)" : "Push movie history (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = ["Title", "Year", "WatchedAtUtc", "Result"],
            Rows = logRows,
            Summary =
            [
                $"Source: {csvPath}",
                $"To add: {toSend.Count}",
                $"Already on Trakt: {alreadyThere.Count}",
                $"No usable id: {noId.Count}",
                apply ? $"Added: {added}, not found: {notFound}" : "Dry run: nothing was sent",
            ],
            NextStep = apply ? null : "push-movies --apply",
        };

        if (apply)
        {
            run.CsvPath = Path.Combine(_context.Paths.Reports, "trakt-history-push-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            Csv.WriteTable(run.CsvPath, run.Columns, logRows);
            log.Success($"Done. Added {added} plays, {notFound} not found. Log: {run.CsvPath}");
        }

        return _context.Runs.Save(run);
    }

    private static Dictionary<string, string> LogRow(Row row, string result) => new()
    {
        ["Title"] = row.Title,
        ["Year"] = row.Year,
        ["WatchedAtUtc"] = row.WatchedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["Result"] = result,
    };

    private static List<Row> ReadRows(string path)
    {
        var table = Csv.Read(path);
        var rows = new List<Row>();
        foreach (var cells in table.Rows)
        {
            string? tmdb = null;
            string? imdb = null;
            foreach (var id in cells.GetValueOrDefault("Ids", "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.StartsWith("tmdb:", StringComparison.Ordinal))
                {
                    tmdb = id[5..];
                }
                else if (id.StartsWith("imdb:", StringComparison.Ordinal))
                {
                    imdb = id[5..];
                }
            }

            var when = DateTime.Parse(
                cells.GetValueOrDefault("LastWatchedUtc", ""),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            rows.Add(new Row(cells.GetValueOrDefault("Title", ""), cells.GetValueOrDefault("Year", ""), when, tmdb, imdb));
        }

        return rows;
    }
}

/// <summary>
/// Adds episodes Plex reports as watched to the Trakt history, from the scan's
/// episodes-watched-in-plex-not-trakt.csv. Shows are sent by tvdb id (falling back to tmdb, then imdb) and
/// episodes by season and number; episodes already in the history are skipped.
/// </summary>
public sealed class PushEpisodesOperation
{
    private const int MaxEpisodesPerRequest = 100;

    private readonly OperationContext _context;

    public PushEpisodesOperation(OperationContext context)
    {
        _context = context;
    }

    private sealed record Row(string Show, int Season, int Episode, DateTime WatchedAt, string? Tvdb, string? Tmdb, string? Imdb);

    public async Task<RunRecord> RunAsync(string csvPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var rows = ReadRows(csvPath);
        log.Info($"{rows.Count} episodes in {csvPath}");

        var client = await _context.TraktAsync(ct);
        log.Info("Reading the current Trakt episode history...");
        var history = await client.GetAllAsync("/sync/history/episodes", ct);
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
        var noId = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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

        log.Detail($"to add: {toSend.Count}, already on Trakt: {alreadyThere}, shows with no usable id: {noId.Count}");
        foreach (var show in noId)
        {
            log.Detail($"skip (no id): {show}");
        }

        foreach (var group in toSend.GroupBy(r => r.Show).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            log.Detail($"{group.Key}: {group.Count()}");
        }

        var logRows = new List<Dictionary<string, string>>();
        var added = 0;
        var notFoundShows = 0;
        var notFoundEpisodes = 0;

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to add these plays.");
            foreach (var row in toSend)
            {
                logRows.Add(LogRow(row, "would add"));
            }
        }
        else
        {
            log.Info("Taking a Trakt backup first...");
            await new BackupOperation(_context).RunAsync(client, Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), ct);

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
                        foreach (var episode in season.OrderBy(r => r.Episode))
                        {
                            episodes.Add(new JsonObject
                            {
                                ["number"] = episode.Episode,
                                ["watched_at"] = episode.WatchedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture),
                            });
                        }

                        seasons.Add(new JsonObject { ["number"] = season.Key, ["episodes"] = episodes });
                    }

                    shows.Add(new JsonObject { ["ids"] = ShowIds(show.First()), ["seasons"] = seasons });
                }

                var response = await client.PostAsync("/sync/history", new JsonObject { ["shows"] = shows }, ct);
                var batchAdded = response?["added"]?["episodes"]?.GetValue<int>() ?? 0;
                var missingShows = response?["not_found"]?["shows"]?.AsArray().Count ?? 0;
                var missingEpisodes = response?["not_found"]?["episodes"]?.AsArray().Count ?? 0;
                added += batchAdded;
                notFoundShows += missingShows;
                notFoundEpisodes += missingEpisodes;
                log.Detail($"batch {batchNumber}: sent {batch.Count}, added {batchAdded}, shows not found {missingShows}, episodes not found {missingEpisodes}");
                foreach (var row in batch)
                {
                    logRows.Add(LogRow(row, "sent"));
                }

                if (missingShows > 0)
                {
                    log.Warn($"shows not found on Trakt: {response!["not_found"]!["shows"]!.ToJsonString()}");
                }

                if (missingEpisodes > 0)
                {
                    log.Warn($"episodes not found on Trakt (numbering differs?): {response!["not_found"]!["episodes"]!.ToJsonString()}");
                }
            }
        }

        var run = new RunRecord
        {
            Id = RunRecord.NewId("push-episodes"),
            Kind = "push-episodes",
            Title = apply ? "Push episode history (applied)" : "Push episode history (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = ["Show", "Episode", "WatchedAtUtc", "Result"],
            Rows = logRows,
            Summary =
            [
                $"Source: {csvPath}",
                $"To add: {toSend.Count}",
                $"Already on Trakt: {alreadyThere}",
                $"Shows with no usable id: {noId.Count}",
                apply ? $"Added: {added}, shows not found: {notFoundShows}, episodes not found: {notFoundEpisodes}" : "Dry run: nothing was sent",
            ],
            NextStep = apply ? null : "push-episodes --apply",
        };

        if (apply)
        {
            run.CsvPath = Path.Combine(_context.Paths.Reports, "trakt-episode-push-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            Csv.WriteTable(run.CsvPath, run.Columns, logRows);
            log.Success($"Done. Added {added} episode plays, {notFoundShows} shows and {notFoundEpisodes} episodes not found. Log: {run.CsvPath}");
        }

        return _context.Runs.Save(run);
    }

    private static Dictionary<string, string> LogRow(Row row, string result) => new()
    {
        ["Show"] = row.Show,
        ["Episode"] = $"S{row.Season:00}E{row.Episode:00}",
        ["WatchedAtUtc"] = row.WatchedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["Result"] = result,
    };

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

    private static IEnumerable<string> Keys(Row row)
    {
        if (row.Tvdb != null)
        {
            yield return "tvdb:" + row.Tvdb;
        }

        if (row.Tmdb != null)
        {
            yield return "tmdb:" + row.Tmdb;
        }

        if (row.Imdb != null)
        {
            yield return "imdb:" + row.Imdb;
        }
    }

    private static JsonObject ShowIds(Row row) =>
        row.Tvdb != null ? new JsonObject { ["tvdb"] = long.Parse(row.Tvdb, CultureInfo.InvariantCulture) }
        : row.Tmdb != null ? new JsonObject { ["tmdb"] = long.Parse(row.Tmdb, CultureInfo.InvariantCulture) }
        : new JsonObject { ["imdb"] = row.Imdb };

    private static List<Row> ReadRows(string path)
    {
        var table = Csv.Read(path);
        var rows = new List<Row>();
        foreach (var cells in table.Rows)
        {
            string? tvdb = null;
            string? tmdb = null;
            string? imdb = null;
            foreach (var id in cells.GetValueOrDefault("ShowIds", "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

            // The Episode column is "S01E02".
            var parts = cells.GetValueOrDefault("Episode", "S00E00").TrimStart('S').Split('E');
            var when = DateTime.Parse(
                cells.GetValueOrDefault("LastWatchedUtc", ""),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            rows.Add(new Row(
                cells.GetValueOrDefault("Show", ""),
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                when,
                tvdb,
                tmdb,
                imdb));
        }

        return rows;
    }
}
