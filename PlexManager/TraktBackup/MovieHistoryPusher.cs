using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>
/// Adds movies that Plex reports as watched to the Trakt history, using the CSV written by PlexScan
/// (movies-watched-in-plex-not-trakt.csv). Each movie gets one play dated with its last Plex view.
/// Movies already in Trakt's watched list are skipped, so a re-run does not create duplicate plays.
/// </summary>
public sealed class MovieHistoryPusher
{
    private const int BatchSize = 25;

    private readonly TraktClient _client;
    private readonly string _reportsDir;

    public MovieHistoryPusher(TraktClient client, string reportsDir)
    {
        _client = client;
        _reportsDir = reportsDir;
    }

    private sealed record Row(string Title, string Year, DateTime WatchedAt, string? Tmdb, string? Imdb);

    /// <summary>Builds the plan from live Trakt data. When apply is true the plan is sent after the backup runs.</summary>
    public async Task RunAsync(string csvPath, bool apply, Func<CancellationToken, Task> backup, CancellationToken ct)
    {
        var rows = ReadRows(csvPath);
        Console.WriteLine($"{rows.Count} movies in {csvPath}");

        Console.WriteLine("Reading current Trakt watched movies...");
        var watched = await _client.GetAllAsync("/sync/watched/movies", ct);
        var tmdbSeen = new HashSet<string>();
        var imdbSeen = new HashSet<string>();
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

        Console.WriteLine($"  to add: {toSend.Count}, already on Trakt: {alreadyThere.Count}, no usable id: {noId.Count}");
        foreach (var row in alreadyThere)
        {
            Console.WriteLine($"  skip (already on Trakt): {row.Title} ({row.Year})");
        }

        foreach (var row in noId)
        {
            Console.WriteLine($"  skip (no id): {row.Title} ({row.Year})");
        }

        if (!apply)
        {
            Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to add these plays.");
            return;
        }

        Console.WriteLine("Taking a backup first...");
        await backup(ct);

        var log = new StringBuilder("Title,Year,WatchedAtUtc,Result\n");
        var added = 0;
        var notFound = 0;
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

            var response = await _client.PostAsync("/sync/history", new JsonObject { ["movies"] = movies }, ct);
            var batchAdded = response?["added"]?["movies"]?.GetValue<int>() ?? 0;
            var missing = response?["not_found"]?["movies"]?.AsArray().Count ?? 0;
            added += batchAdded;
            notFound += missing;
            Console.WriteLine($"  batch {i / BatchSize + 1}: added {batchAdded}, not found {missing}");
            foreach (var row in batch)
            {
                log.Append($"\"{row.Title.Replace("\"", "\"\"")}\",{row.Year},{row.WatchedAt:yyyy-MM-ddTHH:mm:ssZ},sent\n");
            }

            if (missing > 0)
            {
                foreach (var nf in response!["not_found"]!["movies"]!.AsArray())
                {
                    Console.WriteLine($"    not found on Trakt: {nf!.ToJsonString()}");
                }
            }
        }

        Directory.CreateDirectory(_reportsDir);
        var logPath = Path.Combine(_reportsDir, "trakt-history-push-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Done. Added {added} plays, {notFound} not found. Log: {logPath}");
    }

    private static List<Row> ReadRows(string path)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0))
        {
            var cells = SplitCsv(line);
            string? tmdb = null;
            string? imdb = null;
            foreach (var id in cells[4].Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

            var when = DateTime.Parse(cells[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            rows.Add(new Row(cells[0], cells[1], when, tmdb, imdb));
        }

        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }
}
