using System.Globalization;
using System.Text.Json.Nodes;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Lists the Plex movies rated below the threshold on Trakt, with their size, whether they were watched and a link
/// to IMDb, so the delete step has something reviewed to work from. Read-only.
/// </summary>
public sealed class PruneCandidatesOperation
{
    private static readonly string[] Columns =
    [
        "Title", "Year", "TraktRating", "RatedAt", "IMDb", "WatchedInPlex", "SizeMB", "File", "RatingKey", "Keep",
    ];

    private readonly OperationContext _context;

    public PruneCandidatesOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(int? threshold, string? backupFolder, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var limit = threshold ?? _context.Values.Migration.RatingThreshold;
        var backup = backupFolder ?? _context.LatestBackup();
        log.Info($"Trakt data: {backup}");

        // Trakt ratings below the threshold, indexed by every id Plex might know the movie by.
        var ratings = JsonNode.Parse(File.ReadAllText(Path.Combine(backup, "ratings-movies.json")))!.AsArray();
        var low = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var lowCount = 0;
        foreach (var entry in ratings)
        {
            if (entry!["rating"]!.GetValue<int>() >= limit)
            {
                continue;
            }

            lowCount++;
            foreach (var key in MediaIds.FromTrakt(entry["movie"]!["ids"]))
            {
                low[key] = entry;
            }
        }

        log.Detail($"{ratings.Count} rated movies on Trakt, {lowCount} rated below {limit}");

        log.Info("Reading Plex movies...");
        var movies = await _context.Plex.GetAllAsync(_context.Plex.MoviesPath, ct);

        var rows = new List<(Dictionary<string, string> Cells, long Bytes, int Rating, string Title)>();
        var matchedTrakt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var movie in movies)
        {
            var keys = MediaIds.FromPlex(movie);
            var entry = keys.Select(k => low.GetValueOrDefault(k)).FirstOrDefault(e => e != null);
            if (entry == null)
            {
                continue;
            }

            var ids = entry["movie"]!["ids"]!;
            matchedTrakt.Add(ids["trakt"]!.ToString());
            var imdb = ids["imdb"]?.ToString() ?? keys.FirstOrDefault(k => k.StartsWith("imdb:", StringComparison.Ordinal))?[5..];
            var part = movie["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault();
            var bytes = part?["size"]?.GetValue<long>() ?? 0;
            var title = movie["title"]?.GetValue<string>() ?? "?";
            var rating = entry["rating"]!.GetValue<int>();

            rows.Add((new Dictionary<string, string>
            {
                ["Title"] = title,
                ["Year"] = movie["year"]?.ToString() ?? "",
                ["TraktRating"] = rating.ToString(CultureInfo.InvariantCulture),
                ["RatedAt"] = entry["rated_at"]?.GetValue<string>()?[..10] ?? "",
                ["IMDb"] = imdb != null ? $"https://www.imdb.com/title/{imdb}/" : "",
                ["WatchedInPlex"] = (movie["viewCount"]?.GetValue<int>() ?? 0) > 0 ? "yes" : "no",
                ["SizeMB"] = (bytes / 1_048_576).ToString(CultureInfo.InvariantCulture),
                ["File"] = part?["file"]?.GetValue<string>() ?? "",
                ["RatingKey"] = movie["ratingKey"]!.GetValue<string>(),
                ["Keep"] = "",
            }, bytes, rating, title));
        }

        var sorted = rows.OrderBy(r => r.Rating).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var runId = RunRecord.NewId("prune");
        var csvPath = Path.Combine(_context.Paths.Reports, runId, "prune-candidates.csv");
        Csv.WriteTable(csvPath, Columns, sorted.Select(r => r.Cells));
        _context.WritePointer("latest-prune.txt", csvPath);

        var totalGb = sorted.Sum(r => r.Bytes) / 1_073_741_824.0;
        log.Info("");
        log.Detail($"In Plex and rated below {limit}: {sorted.Count} movies, {totalGb:F1} GB");
        log.Detail($"Rated below {limit} on Trakt but not in Plex: {lowCount - matchedTrakt.Count}");
        foreach (var group in sorted.GroupBy(r => r.Rating).OrderBy(g => g.Key))
        {
            log.Detail($"rated {group.Key}: {group.Count()}");
        }

        var run = _context.Runs.Save(new RunRecord
        {
            Id = runId,
            Kind = "prune",
            Title = $"Movies rated below {limit}",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Columns = Columns.ToList(),
            Rows = sorted.Select(r => r.Cells).ToList(),
            KeyColumn = "RatingKey",
            EditableColumns = ["Keep"],
            CsvPath = csvPath,
            Summary =
            [
                $"Trakt data: {backup}",
                $"Threshold: below {limit}",
                $"Candidates in Plex: {sorted.Count}",
                $"Space they use: {totalGb:F1} GB",
                $"Rated low on Trakt but not in Plex: {lowCount - matchedTrakt.Count}",
            ],
            NextStep = "prune apply latest --apply",
        });

        log.Success($"Wrote {csvPath}");
        log.Info("Review it: mark Keep on anything you want to hold on to. Nothing has been deleted.");
        return run;
    }
}

/// <summary>
/// Deletes every reviewed candidate not marked Keep: from Plex with its file, then from Radarr with an import
/// exclusion so a list sync does not fetch it again. Dry run unless applied. Each row is re-read from Plex first
/// and skipped when it is no longer the file the reviewed list described.
/// </summary>
public sealed class PruneApplyOperation
{
    private readonly OperationContext _context;

    public PruneApplyOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string csvPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var table = Csv.Read(csvPath);
        var all = table.Rows;
        var doomed = all.Where(r => !r.GetValueOrDefault("Keep", "").Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)).ToList();
        var keptCount = all.Count - doomed.Count;
        var doomedGb = doomed.Sum(r => long.TryParse(r.GetValueOrDefault("SizeMB", "0"), CultureInfo.InvariantCulture, out var mb) ? mb : 0) / 1024.0;
        log.Info($"{all.Count} candidates in {csvPath}: {keptCount} marked Keep, {doomed.Count} to delete ({doomedGb:F1} GB)");

        var plex = _context.Plex;
        var radarr = _context.Radarr;
        log.Info("Reading the Radarr library...");
        var radarrByTmdb = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var radarrByImdb = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await radarr.LibraryAsync(ct))
        {
            if (entry["tmdbId"] is { } tmdbId && tmdbId.GetValue<int>() > 0)
            {
                radarrByTmdb[tmdbId.ToString()] = entry;
            }

            if (entry["imdbId"]?.GetValue<string>() is { Length: > 0 } imdbId)
            {
                radarrByImdb[imdbId] = entry;
            }
        }

        var logRows = new List<Dictionary<string, string>>();
        var deleted = 0;
        var problems = 0;
        foreach (var row in doomed)
        {
            ct.ThrowIfCancellationRequested();
            var key = row.GetValueOrDefault("RatingKey", "");
            var label = $"{row.GetValueOrDefault("Title")} ({row.GetValueOrDefault("Year")}), rated {row.GetValueOrDefault("TraktRating")}";

            // Re-read the item and check it is still the file the reviewed list described.
            JsonNode? item = null;
            try
            {
                item = (await plex.GetAsync($"/library/metadata/{key}?includeGuids=1", ct))["Metadata"]?[0];
            }
            catch (HttpRequestException)
            {
                // Treated below as "no longer in Plex".
            }

            var file = item?["Media"]?[0]?["Part"]?[0]?["file"]?.GetValue<string>();
            if (item == null || file != row.GetValueOrDefault("File"))
            {
                log.Warn($"SKIP {label}: no longer in Plex with the listed file");
                logRows.Add(LogRow(row, null, null, "skipped (changed)", ""));
                problems++;
                continue;
            }

            var keys = MediaIds.FromPlex(item);
            var tmdb = keys.FirstOrDefault(k => k.StartsWith("tmdb:", StringComparison.Ordinal))?[5..];
            var imdb = keys.FirstOrDefault(k => k.StartsWith("imdb:", StringComparison.Ordinal))?[5..];
            var inRadarr = (tmdb != null ? radarrByTmdb.GetValueOrDefault(tmdb) : null)
                           ?? (imdb != null ? radarrByImdb.GetValueOrDefault(imdb) : null);

            if (!apply)
            {
                log.Detail($"would delete {label}, {row.GetValueOrDefault("SizeMB")} MB; "
                           + $"Radarr: {(inRadarr != null ? "remove + exclude" : "not in Radarr")}");
                logRows.Add(LogRow(row, imdb, tmdb, "would delete", inRadarr != null ? "would remove + exclude" : "not in Radarr"));
                continue;
            }

            string plexResult;
            try
            {
                await plex.DeleteAsync($"/library/metadata/{key}", ct);
                plexResult = "deleted";
                deleted++;
            }
            catch (HttpRequestException ex)
            {
                log.Error($"FAILED {label}: {ex.Message}");
                logRows.Add(LogRow(row, imdb, tmdb, "failed: " + ex.Message, "not attempted"));
                problems++;
                continue;
            }

            string radarrResult;
            if (inRadarr == null)
            {
                radarrResult = "not in Radarr";
            }
            else
            {
                var ok = await radarr.DeleteMovieAsync(inRadarr["id"]!.GetValue<int>(), ct);
                radarrResult = ok ? "removed + excluded" : "failed";
                if (!ok)
                {
                    problems++;
                }
            }

            log.Detail($"{label}: Plex {plexResult}, Radarr {radarrResult}");
            logRows.Add(LogRow(row, imdb, tmdb, plexResult, radarrResult));
        }

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to delete these.");
        }

        var run = new RunRecord
        {
            Id = RunRecord.NewId("prune-apply"),
            Kind = "prune-apply",
            Title = apply ? "Delete low-rated movies (applied)" : "Delete low-rated movies (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = ["RatingKey", "Title", "Year", "TraktRating", "SizeMB", "File", "Imdb", "Tmdb", "Plex", "Radarr"],
            Rows = logRows,
            Summary =
            [
                $"Source: {csvPath}",
                $"Marked Keep: {keptCount}",
                $"To delete: {doomed.Count} ({doomedGb:F1} GB)",
                apply ? $"Deleted: {deleted}, problems: {problems}" : "Dry run: nothing was deleted",
            ],
            NextStep = apply ? null : "prune apply --apply",
        };

        if (apply)
        {
            run.CsvPath = Path.Combine(_context.Paths.Reports, "prune-delete-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            Csv.WriteTable(run.CsvPath, run.Columns, logRows);
            log.Success($"Done. {deleted} deleted, {problems} problems. Log: {run.CsvPath}");
        }

        return _context.Runs.Save(run);
    }

    private static Dictionary<string, string> LogRow(
        Dictionary<string, string> row,
        string? imdb,
        string? tmdb,
        string plexResult,
        string radarrResult) => new()
    {
        ["RatingKey"] = row.GetValueOrDefault("RatingKey", ""),
        ["Title"] = row.GetValueOrDefault("Title", ""),
        ["Year"] = row.GetValueOrDefault("Year", ""),
        ["TraktRating"] = row.GetValueOrDefault("TraktRating", ""),
        ["SizeMB"] = row.GetValueOrDefault("SizeMB", ""),
        ["File"] = row.GetValueOrDefault("File", ""),
        ["Imdb"] = imdb ?? "",
        ["Tmdb"] = tmdb ?? "",
        ["Plex"] = plexResult,
        ["Radarr"] = radarrResult,
    };
}
