using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Step one of fixing Plex movies with no metadata match: ask Plex for match candidates for each unmatched movie
/// and write them out for review. Read-only.
/// </summary>
public sealed partial class FixCandidatesOperation
{
    private static readonly string[] Columns =
    [
        "RatingKey", "PlexTitle", "File", "SearchTitle", "SearchYear", "Rank",
        "CandidateName", "CandidateYear", "CandidateGuid", "Score", "Approve",
    ];

    private readonly OperationContext _context;

    public FixCandidatesOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var plex = _context.Plex;

        log.Info("Reading Plex movies...");
        var movies = await plex.GetAllAsync(plex.MoviesPath, ct);
        var unmatched = movies.Where(m => !MediaIds.PlexIsMatched(m)).ToList();
        log.Detail($"{unmatched.Count} of {movies.Count} movies have no usable id");

        var known = _context.Values.Fix.KnownTitles
            .Where(k => k.PlexTitle.Length > 0)
            .GroupBy(k => k.PlexTitle, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<Dictionary<string, string>>();
        foreach (var movie in unmatched)
        {
            ct.ThrowIfCancellationRequested();
            var key = movie["ratingKey"]!.GetValue<string>();
            var title = movie["title"]?.GetValue<string>() ?? "";
            var file = movie["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault()?["file"]?.GetValue<string>() ?? "";
            var isKnown = known.TryGetValue(title, out var expected);
            var (searchTitle, searchYear) = isKnown
                ? (expected!.Name, expected.Year)
                : GuessTitle(title, movie["year"]?.ToString(), file);

            var url = $"/library/metadata/{key}/matches?manual=1&title={Uri.EscapeDataString(searchTitle)}"
                      + (searchYear.Length > 0 ? $"&year={searchYear}" : "");
            var container = await plex.GetAsync(url, ct);
            var results = container["SearchResult"]?.AsArray() ?? [];
            log.Detail($"{title}: searched \"{searchTitle}\" {searchYear}, {results.Count} candidates");

            var rank = 0;
            foreach (var result in results.Take(5))
            {
                rank++;
                var name = result!["name"]?.GetValue<string>() ?? "";
                var year = result["year"]?.ToString() ?? "";
                var score = result["score"]?.ToString() ?? "";

                // Pre-approve only an unambiguous hit. A known title is approved on an exact name and year match at
                // any rank; anything else only on a clear first hit (same year, score 90 or more).
                var approve = isKnown
                    ? year == expected!.Year
                      && Normalize(name) == Normalize(expected.Name)
                      && !rows.Any(r => r["RatingKey"] == key && r["Approve"] == "yes")
                        ? "yes" : ""
                    : rank == 1 && searchYear.Length > 0 && year == searchYear && int.TryParse(score, out var parsed) && parsed >= 90
                        ? "yes" : "";

                rows.Add(new Dictionary<string, string>
                {
                    ["RatingKey"] = key,
                    ["PlexTitle"] = title,
                    ["File"] = file,
                    ["SearchTitle"] = searchTitle,
                    ["SearchYear"] = searchYear,
                    ["Rank"] = rank.ToString(),
                    ["CandidateName"] = name,
                    ["CandidateYear"] = year,
                    ["CandidateGuid"] = result["guid"]?.GetValue<string>() ?? "",
                    ["Score"] = score,
                    ["Approve"] = approve,
                });
            }

            if (results.Count == 0)
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["RatingKey"] = key,
                    ["PlexTitle"] = title,
                    ["File"] = file,
                    ["SearchTitle"] = searchTitle,
                    ["SearchYear"] = searchYear,
                    ["Rank"] = "0",
                    ["CandidateName"] = "",
                    ["CandidateYear"] = "",
                    ["CandidateGuid"] = "",
                    ["Score"] = "",
                    ["Approve"] = "",
                });
            }
        }

        var runId = RunRecord.NewId("fix");
        var csvPath = Path.Combine(_context.Paths.Reports, runId, "plex-match-candidates.csv");
        Csv.WriteTable(csvPath, Columns, rows);
        _context.WritePointer("latest-fix.txt", csvPath);

        var run = _context.Runs.Save(new RunRecord
        {
            Id = runId,
            Kind = "fix",
            Title = "Unmatched Plex movies",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Columns = Columns.ToList(),
            Rows = rows,

            // A candidate row is identified by its movie and the metadata record it offers.
            KeyColumn = "CandidateGuid",
            EditableColumns = ["Approve"],
            CsvPath = csvPath,
            Summary =
            [
                $"Unmatched movies: {unmatched.Count} of {movies.Count}",
                $"Candidate rows: {rows.Count}",
                $"Pre-approved: {rows.Count(r => r["Approve"] == "yes")}",
                $"No candidates at all: {rows.Count(r => r["Rank"] == "0")}",
            ],
            NextStep = "fix apply latest --apply",
        });

        log.Success($"Wrote {csvPath}");
        log.Info("Review it: approve exactly one candidate per movie, then run Apply.");
        return run;
    }

    private static string Normalize(string value) => NonAlphanumeric().Replace(value.ToLowerInvariant(), "");

    /// <summary>
    /// Best guess at the real title and year: the movie's folder name ("Title (2024)") when it has one,
    /// otherwise the Plex title with scene junk trimmed.
    /// </summary>
    public static (string Title, string Year) GuessTitle(string plexTitle, string? plexYear, string file)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        var match = FolderWithYear().Match(folder);
        if (match.Success)
        {
            return (match.Groups["t"].Value, match.Groups["y"].Value);
        }

        var title = SceneSeparators().Replace(plexTitle, " ");
        var year = YearInTitle().Match(title);
        title = YearAndRest().Replace(title, "").Trim();
        return (title.Length > 0 ? title : plexTitle, year.Success ? year.Value : plexYear ?? "");
    }

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^(?<t>.+?)\s*\((?<y>\d{4})\)$")]
    private static partial Regex FolderWithYear();

    [GeneratedRegex(@"[.\-_~](?=\S)")]
    private static partial Regex SceneSeparators();

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearInTitle();

    [GeneratedRegex(@"\b(19|20)\d{2}\b.*$")]
    private static partial Regex YearAndRest();
}

/// <summary>Step two: for each approved candidate, tell Plex to match that movie to it. Dry run unless applied.</summary>
public sealed class FixApplyOperation
{
    private readonly OperationContext _context;

    public FixApplyOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string csvPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var table = Csv.Read(csvPath);
        var approved = table.Rows
            .Where(r => r.GetValueOrDefault("Approve", "").Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var duplicates = approved.GroupBy(r => r.GetValueOrDefault("RatingKey", ""))
            .Where(g => g.Count() > 1)
            .Select(g => g.First().GetValueOrDefault("PlexTitle", ""))
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"More than one candidate is approved for: {string.Join("; ", duplicates)}. Approve one per movie.");
        }

        log.Info($"{approved.Count} matches approved in {csvPath}");
        foreach (var row in approved)
        {
            log.Detail($"{row.GetValueOrDefault("PlexTitle")}  ->  {row.GetValueOrDefault("CandidateName")} ({row.GetValueOrDefault("CandidateYear")})");
        }

        var logRows = new List<Dictionary<string, string>>();
        var matched = 0;
        var failed = 0;

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to match these in Plex.");
            foreach (var row in approved)
            {
                logRows.Add(LogRow(row, "would match"));
            }
        }
        else
        {
            foreach (var row in approved)
            {
                var guid = row.GetValueOrDefault("CandidateGuid", "");
                var url = $"/library/metadata/{row.GetValueOrDefault("RatingKey")}/match"
                          + $"?guid={Uri.EscapeDataString(guid)}"
                          + $"&name={Uri.EscapeDataString(row.GetValueOrDefault("CandidateName", ""))}"
                          + $"&year={row.GetValueOrDefault("CandidateYear")}";
                string result;
                try
                {
                    await _context.Plex.PutAsync(url, ct);
                    result = "matched";
                    matched++;
                }
                catch (HttpRequestException ex)
                {
                    result = "failed: " + ex.Message;
                    failed++;
                }

                log.Detail($"{row.GetValueOrDefault("PlexTitle")}: {result}");
                logRows.Add(LogRow(row, result));
            }
        }

        var run = new RunRecord
        {
            Id = RunRecord.NewId("fix-apply"),
            Kind = "fix-apply",
            Title = apply ? "Match unmatched movies (applied)" : "Match unmatched movies (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = ["RatingKey", "PlexTitle", "MatchedTo", "Guid", "Result"],
            Rows = logRows,
            Summary =
            [
                $"Source: {csvPath}",
                $"Approved: {approved.Count}",
                apply ? $"Matched: {matched}, failed: {failed}" : "Dry run: nothing was changed",
            ],
            NextStep = apply ? "scan" : "fix apply --apply",
        };

        if (apply)
        {
            run.CsvPath = Path.Combine(_context.Paths.Reports, "plex-match-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            Csv.WriteTable(run.CsvPath, run.Columns, logRows);
            log.Success($"Done. Log: {run.CsvPath}");
            log.Info("Plex refreshes metadata in the background; give it a minute, then run a scan again.");
        }

        return _context.Runs.Save(run);
    }

    private static Dictionary<string, string> LogRow(Dictionary<string, string> row, string result) => new()
    {
        ["RatingKey"] = row.GetValueOrDefault("RatingKey", ""),
        ["PlexTitle"] = row.GetValueOrDefault("PlexTitle", ""),
        ["MatchedTo"] = row.GetValueOrDefault("CandidateName", ""),
        ["Guid"] = row.GetValueOrDefault("CandidateGuid", ""),
        ["Result"] = result,
    };
}

/// <summary>
/// Deletes the one Plex movie whose file path contains a fragment, file included. Refuses unless exactly one movie
/// matches, and refuses to apply when Plex is not set to allow media deletion (it would drop the entry and leave the file).
/// </summary>
public sealed class FixDeleteOperation
{
    private readonly OperationContext _context;

    public FixDeleteOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string pathFragment, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var plex = _context.Plex;
        var movies = await plex.GetAllAsync(plex.MoviesPath, ct);
        var hits = movies.Where(m => m["Media"]?.AsArray().Any(media =>
            media?["Part"]?.AsArray().Any(part =>
                (part?["file"]?.GetValue<string>() ?? "").Contains(pathFragment, StringComparison.OrdinalIgnoreCase)) == true) == true).ToList();

        if (hits.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one movie whose file contains \"{pathFragment}\", found {hits.Count}. Nothing was deleted.");
        }

        var movie = hits[0];
        var part = movie["Media"]![0]!["Part"]![0]!;
        var title = movie["title"]?.GetValue<string>() ?? "?";
        var file = part["file"]?.GetValue<string>() ?? "";
        var sizeMb = (part["size"]?.GetValue<long>() ?? 0) / 1_048_576;
        log.Info($"Movie: {title} (ratingKey {movie["ratingKey"]})");
        log.Info($"File:  {file}");
        log.Info($"Size:  {sizeMb} MB");

        var allowed = await plex.AllowsMediaDeletionAsync(ct);
        log.Info($"Plex 'Allow media deletion': {(allowed == null ? "unknown" : allowed.Value ? "on" : "off")}");

        var result = "would delete";
        if (apply)
        {
            if (allowed != true)
            {
                throw new InvalidOperationException(
                    "Plex's 'Allow media deletion' is off, so Plex would only drop the library entry and leave the file. "
                    + "Turn it on in Plex (Settings, Library) and try again.");
            }

            await plex.DeleteAsync($"/library/metadata/{movie["ratingKey"]}", ct);
            result = "deleted";
            log.Success("Deleted.");
        }
        else
        {
            log.Info("Dry run: nothing was deleted. Run it again with Apply to delete this movie and its file.");
        }

        return _context.Runs.Save(new RunRecord
        {
            Id = RunRecord.NewId("fix-delete"),
            Kind = "fix-delete",
            Title = apply ? "Delete one Plex movie (applied)" : "Delete one Plex movie (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = ["Title", "File", "SizeMB", "Result"],
            Rows =
            [
                new Dictionary<string, string>
                {
                    ["Title"] = title,
                    ["File"] = file,
                    ["SizeMB"] = sizeMb.ToString(),
                    ["Result"] = result,
                },
            ],
            Summary = [$"Fragment: {pathFragment}", $"Movie: {title}", $"Result: {result}"],
        });
    }
}
