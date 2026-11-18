using System.Text.Json.Nodes;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Adds every "add" row of a reviewed plan to Radarr or Sonarr. Dry run unless applied, and it never touches Trakt:
/// removing the added titles from the lists is a separate, later step.
/// </summary>
public sealed class MigrateApplyOperation
{
    private readonly OperationContext _context;

    public MigrateApplyOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string planPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        log.Info($"Plan: {planPath}");

        var table = Csv.Read(planPath);
        var adds = table.Rows.Where(r => r.GetValueOrDefault("Action", "") == "add").ToList();
        log.Detail($"{adds.Count(r => r.GetValueOrDefault("Type") == "movie")} movies and "
                   + $"{adds.Count(r => r.GetValueOrDefault("Type") == "show")} series to add.");

        var radarr = _context.Radarr;
        var sonarr = _context.Sonarr;
        var movieProfile = await MigratePlanOperation.ProfileIdAsync(radarr, _context.Values.Radarr, ct);
        var showProfile = await MigratePlanOperation.ProfileIdAsync(sonarr, _context.Values.Sonarr, ct);
        var movieRoot = await MigratePlanOperation.RootFolderAsync(radarr, _context.Values.Radarr, ct);
        var showRoot = await MigratePlanOperation.RootFolderAsync(sonarr, _context.Values.Sonarr, ct);
        var languageProfile = await sonarr.FirstLanguageProfileAsync(ct);

        log.Detail($"Radarr: profile {movieProfile}, root {movieRoot}");
        log.Detail($"Sonarr: profile {showProfile}, root {showRoot}, language profile {languageProfile?.ToString() ?? "none"}");
        if (!apply)
        {
            log.Info("Dry run: nothing will be added (lookups only). Run it again with Apply to add.");
        }

        var logRows = new List<Dictionary<string, string>>();
        var added = 0;
        var existing = 0;
        var failed = 0;
        foreach (var row in adds)
        {
            ct.ThrowIfCancellationRequested();
            var isMovie = row.GetValueOrDefault("Type") == "movie";
            var label = $"{row.GetValueOrDefault("Title")} ({row.GetValueOrDefault("Year")})";
            var monitor = row.GetValueOrDefault("Monitor") == "yes";
            var search = row.GetValueOrDefault("SearchOnAdd") == "yes";
            string result;
            try
            {
                var client = isMovie ? radarr : sonarr;
                var term = isMovie ? "tmdb:" + row.GetValueOrDefault("TmdbId") : "tvdb:" + row.GetValueOrDefault("TvdbId");
                var found = await client.LookupAsync(term, ct);
                if (found == null)
                {
                    result = "failed: lookup found nothing";
                    failed++;
                }
                else if (!apply)
                {
                    result = $"would add ({(monitor ? "monitored" : "unmonitored")}{(search ? ", search" : "")})";
                }
                else
                {
                    if (isMovie)
                    {
                        found["rootFolderPath"] = movieRoot;
                        found["qualityProfileId"] = movieProfile;
                        found["monitored"] = monitor;
                        found["minimumAvailability"] = "released";
                        found["addOptions"] = new JsonObject { ["monitor"] = "movieOnly", ["searchForMovie"] = search };
                    }
                    else
                    {
                        found["rootFolderPath"] = showRoot;
                        found["qualityProfileId"] = showProfile;
                        if (languageProfile != null)
                        {
                            found["languageProfileId"] = languageProfile;
                        }

                        found["monitored"] = monitor;
                        found["seasonFolder"] = true;
                        found["addOptions"] = new JsonObject
                        {
                            ["monitor"] = monitor ? "all" : "none",
                            ["searchForMissingEpisodes"] = search,
                            ["searchForCutoffUnmetEpisodes"] = false,
                        };
                    }

                    var outcome = await client.AddAsync(found, ct);
                    result = outcome.Message;
                    switch (outcome.State)
                    {
                        case Clients.ArrAddState.Added:
                            added++;
                            break;
                        case Clients.ArrAddState.AlreadyPresent:
                            existing++;
                            break;
                        default:
                            failed++;
                            break;
                    }

                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = "failed: " + ex.Message;
                failed++;
            }

            log.Detail($"[{row.GetValueOrDefault("Type")}] {label}: {result}");
            logRows.Add(new Dictionary<string, string>
            {
                ["Type"] = row.GetValueOrDefault("Type", ""),
                ["Title"] = row.GetValueOrDefault("Title", ""),
                ["Year"] = row.GetValueOrDefault("Year", ""),
                ["TmdbId"] = row.GetValueOrDefault("TmdbId", ""),
                ["TvdbId"] = row.GetValueOrDefault("TvdbId", ""),
                ["TraktId"] = row.GetValueOrDefault("TraktId", ""),
                ["Monitored"] = monitor ? "yes" : "no",
                ["Search"] = search ? "yes" : "no",
                ["Result"] = result,
            });
        }

        var logPath = Path.Combine(
            _context.Paths.Reports,
            (apply ? "migrate-apply-" : "migrate-apply-dryrun-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        string[] columns = ["Type", "Title", "Year", "TmdbId", "TvdbId", "TraktId", "Monitored", "Search", "Result"];
        Csv.WriteTable(logPath, columns, logRows);

        log.Info("");
        log.Info(apply
            ? $"Added {added}, already present {existing}, failed {failed}."
            : $"Dry run: {adds.Count - failed} found, {failed} failed lookup.");
        log.Info($"Log: {logPath}");
        log.Info("Trakt was not changed. Removing the added titles from Trakt is a separate step.");

        return _context.Runs.Save(new RunRecord
        {
            Id = RunRecord.NewId("migrate-apply"),
            Kind = "migrate-apply",
            Title = apply ? "Add to Radarr and Sonarr (applied)" : "Add to Radarr and Sonarr (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply,
            Columns = columns.ToList(),
            Rows = logRows,
            CsvPath = logPath,
            Summary =
            [
                $"Plan: {planPath}",
                $"Rows marked add: {adds.Count}",
                apply ? $"Added: {added}, already present: {existing}, failed: {failed}" : $"Dry run: {adds.Count - failed} found, {failed} failed lookup",
            ],
            NextStep = apply ? "migrate verify latest" : "migrate apply --apply",
        });
    }
}

/// <summary>
/// Re-reads Radarr and Sonarr and confirms every row that should be there is. Writes a verified list (safe to
/// remove from Trakt) and a missing list (the exceptions). Read-only.
/// </summary>
public sealed class MigrateVerifyOperation
{
    private static readonly string[] Columns = ["Type", "Title", "Year", "TmdbId", "TvdbId", "TraktId", "PlanAction"];

    private readonly OperationContext _context;

    public MigrateVerifyOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string planPath, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        log.Info($"Plan: {planPath}");
        var table = Csv.Read(planPath);

        var radarrHave = new HashSet<string>(StringComparer.Ordinal);
        foreach (var movie in await _context.Radarr.LibraryAsync(ct))
        {
            radarrHave.Add("tmdb:" + movie["tmdbId"]);
        }

        var sonarrHave = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in await _context.Sonarr.LibraryAsync(ct))
        {
            sonarrHave.Add("tvdb:" + series["tvdbId"]);
        }

        log.Detail($"Radarr has {radarrHave.Count} movies, Sonarr has {sonarrHave.Count} series.");

        var verified = new List<Dictionary<string, string>>();
        var missing = new List<Dictionary<string, string>>();
        foreach (var row in table.Rows.Where(r =>
                     r.GetValueOrDefault("Action") is "add" or "already in Radarr" or "already in Sonarr"))
        {
            var isMovie = row.GetValueOrDefault("Type") == "movie";
            var present = isMovie
                ? radarrHave.Contains("tmdb:" + row.GetValueOrDefault("TmdbId"))
                : sonarrHave.Contains("tvdb:" + row.GetValueOrDefault("TvdbId"));
            var line = new Dictionary<string, string>
            {
                ["Type"] = row.GetValueOrDefault("Type", ""),
                ["Title"] = row.GetValueOrDefault("Title", ""),
                ["Year"] = row.GetValueOrDefault("Year", ""),
                ["TmdbId"] = row.GetValueOrDefault("TmdbId", ""),
                ["TvdbId"] = row.GetValueOrDefault("TvdbId", ""),
                ["TraktId"] = row.GetValueOrDefault("TraktId", ""),
                ["PlanAction"] = row.GetValueOrDefault("Action", ""),
            };
            (present ? verified : missing).Add(line);
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var verifiedPath = Path.Combine(_context.Paths.Reports, $"migrate-verified-{stamp}.csv");
        var missingPath = Path.Combine(_context.Paths.Reports, $"migrate-missing-{stamp}.csv");
        Csv.WriteTable(verifiedPath, Columns, verified);
        Csv.WriteTable(missingPath, Columns, missing);
        _context.WritePointer("latest-verified.txt", verifiedPath);

        log.Info("");
        log.Info($"Verified present: {verified.Count(v => v["Type"] == "movie")} movies, {verified.Count(v => v["Type"] == "show")} series.");
        log.Info($"Missing:          {missing.Count(v => v["Type"] == "movie")} movies, {missing.Count(v => v["Type"] == "show")} series.");
        foreach (var row in missing)
        {
            log.Warn($"MISSING [{row["Type"]}] {row["Title"]} ({row["Year"]}) - the plan said \"{row["PlanAction"]}\"");
        }

        var other = table.Rows
            .Where(r => r.GetValueOrDefault("Action") is not ("add" or "already in Radarr" or "already in Sonarr"))
            .GroupBy(r => r.GetValueOrDefault("Action", "").Split(':')[0])
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        log.Info("Not checked here (not meant for Radarr or Sonarr): "
                 + string.Join(", ", other.Select(g => $"{g.Key} {g.Count()}")));
        log.Success($"Wrote {verifiedPath} and {missingPath}. Nothing was changed anywhere.");

        return _context.Runs.Save(new RunRecord
        {
            Id = RunRecord.NewId("migrate-verify"),
            Kind = "migrate-verify",
            Title = "Verify Radarr and Sonarr",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Columns = [.. Columns, "Present"],
            Rows = verified.Select(r => Mark(r, "yes")).Concat(missing.Select(r => Mark(r, "no"))).ToList(),
            CsvPath = verifiedPath,
            Summary =
            [
                $"Plan: {planPath}",
                $"Verified present: {verified.Count}",
                $"Missing: {missing.Count}",
                $"Verified list: {verifiedPath}",
                $"Missing list: {missingPath}",
            ],
            NextStep = missing.Count == 0 ? "trakt remove-verified latest --apply" : "look at the missing list first",
        });
    }

    private static Dictionary<string, string> Mark(Dictionary<string, string> row, string present)
    {
        var copy = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase) { ["Present"] = present };
        return copy;
    }
}
