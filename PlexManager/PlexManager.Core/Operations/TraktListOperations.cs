using System.Text.Json.Nodes;
using PlexManager.Core.Clients;
using PlexManager.Core.Settings;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>Which rows of a reviewed CSV a removal run should act on.</summary>
public enum TraktRemovalSource
{
    /// <summary>A reviewed migrate plan: rows whose Action was blanked, or that the plan itself marked "remove:".</summary>
    ReviewedPlan,

    /// <summary>A verified list from the verify step: every row is confirmed present in Radarr or Sonarr.</summary>
    Verified,

    /// <summary>Rows never meant for Radarr or Sonarr (in Plex, watched, no id) plus whatever verify found missing.</summary>
    Leftovers,
}

/// <summary>
/// Removes titles from the chosen Trakt lists: the watchlist plus whichever personal lists Settings keeps in step
/// with it. Nothing is ever added. Dry run unless applied, and a fresh backup is taken before any write.
/// </summary>
public sealed class TraktRemoveOperation
{
    private readonly OperationContext _context;

    public TraktRemoveOperation(OperationContext context)
    {
        _context = context;
    }

    private sealed record Row(string Type, string Title, string Year, long TraktId);

    public async Task<RunRecord> RunAsync(string csvPath, TraktRemovalSource source, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;

        var path = csvPath;
        if (source == TraktRemovalSource.Leftovers)
        {
            path = BuildLeftoversCsv(csvPath);
            log.Info($"Leftovers list: {path}");
        }

        var rows = ReadRows(path, source);
        var client = await _context.TraktAsync(ct);
        var targets = await ResolveListsAsync(client, ct);

        log.Info($"{rows.Count} titles to remove from {string.Join(", ", targets.Select(t => t.Name))}, read from {path}:");
        foreach (var row in rows)
        {
            log.Detail($"{row.Title} ({row.Year}) [{row.Type}]");
        }

        var logRows = rows.Select(r => new Dictionary<string, string>
        {
            ["Type"] = r.Type,
            ["Title"] = r.Title,
            ["Year"] = r.Year,
            ["TraktId"] = r.TraktId.ToString(),
        }).ToList();

        var run = new RunRecord
        {
            Id = RunRecord.NewId("trakt-remove"),
            Kind = "trakt-remove",
            Title = (source switch
            {
                TraktRemovalSource.Verified => "Remove verified titles from Trakt",
                TraktRemovalSource.Leftovers => "Remove leftovers from Trakt",
                _ => "Remove reviewed titles from Trakt",
            }) + (apply ? " (applied)" : " (dry run)"),
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply && rows.Count > 0,
            Columns = ["Type", "Title", "Year", "TraktId"],
            Rows = logRows,
            KeyColumn = "TraktId",
            Summary =
            [
                $"Source: {path}",
                $"Lists: {string.Join(", ", targets.Select(t => t.Name))}",
                $"Titles: {rows.Count} ({rows.Count(r => r.Type == "movie")} movies, {rows.Count(r => r.Type == "show")} shows)",
                apply ? "Applied" : "Dry run: nothing was changed",
            ],
            NextStep = apply || rows.Count == 0 ? null : "run it again with Apply",
        };

        if (rows.Count == 0)
        {
            log.Info("Nothing to remove.");
            return _context.Runs.Save(run);
        }

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to remove these from "
                     + string.Join(" and ", targets.Select(t => t.Name)) + ".");
            return _context.Runs.Save(run);
        }

        log.Info("Taking a Trakt backup first...");
        await new BackupOperation(_context).RunAsync(client, Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), ct);

        var body = Body(rows);
        foreach (var target in targets)
        {
            var response = await client.PostAsync(TraktClient.RemovePath(target.Id), body.DeepClone(), ct);
            log.Info($"{target.Name}: deleted {response?["deleted"]?.ToJsonString()}, not found {response?["not_found"]?.ToJsonString()}");
        }

        run.CsvPath = Path.Combine(_context.Paths.Reports, "trakt-list-removal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        Csv.WriteTable(run.CsvPath, run.Columns, logRows);
        log.Success($"Done. Log: {run.CsvPath}");
        return _context.Runs.Save(run);
    }

    /// <summary>The watchlist plus the personal lists Settings keeps in step with it.</summary>
    internal static async Task<List<(long Id, string Name)>> ResolveListsAsync(TraktClient client, OperationContext context, CancellationToken ct)
    {
        var refs = new List<TraktListRef> { TraktListRef.Watchlist() };
        refs.AddRange(context.Values.Migration.RemoveFromLists);
        if (context.Values.Migration.OverflowList is { } overflow)
        {
            refs.Add(overflow);
        }

        var targets = new List<(long, string)>();
        foreach (var reference in refs)
        {
            long id;
            try
            {
                id = await client.ResolveListIdAsync(reference, ct);
            }
            catch (InvalidOperationException ex)
            {
                context.Log.Warn(ex.Message);
                continue;
            }

            if (targets.All(t => t.Item1 != id))
            {
                targets.Add((id, reference.ToString()));
            }
        }

        return targets;
    }

    private Task<List<(long Id, string Name)>> ResolveListsAsync(TraktClient client, CancellationToken ct) =>
        ResolveListsAsync(client, _context, ct);

    /// <summary>
    /// Builds the leftovers list: plan rows that were never meant for Radarr or Sonarr (already in Plex, already
    /// watched, or no usable id) plus the titles the verify step found missing. Its Action column is left blank, so
    /// the removal rules treat every row as "remove".
    /// </summary>
    private string BuildLeftoversCsv(string planPath)
    {
        var missingIds = new HashSet<string>(StringComparer.Ordinal);
        var newest = Directory.Exists(_context.Paths.Reports)
            ? Directory.GetFiles(_context.Paths.Reports, "migrate-missing-*.csv").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (newest != null)
        {
            foreach (var row in Csv.Read(newest).Rows)
            {
                missingIds.Add(row.GetValueOrDefault("TraktId", ""));
            }

            _context.Log.Detail($"Including the titles verify found missing, from {newest}.");
        }

        var leftovers = new List<Dictionary<string, string>>();
        foreach (var row in Csv.Read(planPath).Rows)
        {
            var action = row.GetValueOrDefault("Action", "");
            var isLeftover = action.StartsWith("skip: in Plex", StringComparison.Ordinal)
                             || action.StartsWith("skip: watched", StringComparison.Ordinal)
                             || action.StartsWith("exception", StringComparison.Ordinal)
                             || missingIds.Contains(row.GetValueOrDefault("TraktId", ""));
            if (isLeftover)
            {
                leftovers.Add(new Dictionary<string, string>
                {
                    ["Action"] = "",
                    ["Type"] = row.GetValueOrDefault("Type", ""),
                    ["Title"] = row.GetValueOrDefault("Title", ""),
                    ["Year"] = row.GetValueOrDefault("Year", ""),
                    ["TraktId"] = row.GetValueOrDefault("TraktId", ""),
                });
            }
        }

        var path = Path.Combine(_context.Paths.Reports, "migrate-leftovers-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        Csv.WriteTable(path, ["Action", "Type", "Title", "Year", "TraktId"], leftovers);
        return path;
    }

    private static JsonObject Body(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        JsonArray Ids(string type) => new(list.Where(r => r.Type == type)
            .Select(r => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = r.TraktId } }).ToArray());
        return new JsonObject { ["movies"] = Ids("movie"), ["shows"] = Ids("show") };
    }

    private static List<Row> ReadRows(string path, TraktRemovalSource source)
    {
        var table = Csv.Read(path);

        // A verified list has a PlanAction column and no Action column: every row in it is confirmed present in
        // Radarr or Sonarr, so every row is removed.
        var everyRow = source == TraktRemovalSource.Verified || (table.Has("PlanAction") && !table.Has("Action"));

        var rows = new List<Row>();
        foreach (var cells in table.Rows)
        {
            var action = everyRow ? "" : cells.GetValueOrDefault("Action", "").Trim();
            var type = cells.GetValueOrDefault("Type", "");
            if ((action.Length == 0 || action.StartsWith("remove:", StringComparison.OrdinalIgnoreCase))
                && type is "movie" or "show"
                && long.TryParse(cells.GetValueOrDefault("TraktId", ""), out var id))
            {
                rows.Add(new Row(type, cells.GetValueOrDefault("Title", ""), cells.GetValueOrDefault("Year", ""), id));
            }
        }

        return rows;
    }
}

/// <summary>
/// Moves titles the plan marked "skip: on &lt;service&gt;" off the source lists and onto the list chosen for that
/// service (created if it does not exist). The new list is filled and checked first; only then is anything removed
/// from the old lists, so a title is never dropped without landing somewhere.
/// </summary>
public sealed class StreamingMoveOperation
{
    private readonly OperationContext _context;

    public StreamingMoveOperation(OperationContext context)
    {
        _context = context;
    }

    private sealed record Row(string Type, string Title, string Year, long TraktId);

    public async Task<RunRecord> RunAsync(string planPath, bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var table = Csv.Read(planPath);
        var targets = _context.Values.Migration.StreamingTargets.Where(t => t.Enabled && t.Provider.Length > 0).ToList();
        if (targets.Count == 0)
        {
            throw new SettingsIncompleteException("No streaming services are set up. Add one in Settings with the Trakt list to move its titles to.");
        }

        var plan = targets.ToDictionary(
            t => t,
            t => ReadRows(table, $"skip: on {t.Provider}"));

        foreach (var (target, rows) in plan)
        {
            log.Info($"{target.Provider} -> {target.List}: {rows.Count(r => r.Type == "movie")} movies, {rows.Count(r => r.Type == "show")} shows");
            foreach (var row in rows)
            {
                log.Detail($"{row.Title} ({row.Year}) [{row.Type}]");
            }
        }

        var logRows = new List<Dictionary<string, string>>();
        var run = new RunRecord
        {
            Id = RunRecord.NewId("move-streaming"),
            Kind = "move-streaming",
            Title = apply ? "Move streaming titles (applied)" : "Move streaming titles (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply && plan.Values.Any(r => r.Count > 0),
            Columns = ["List", "Type", "Title", "Year", "TraktId", "Result"],
            Rows = logRows,
            KeyColumn = "TraktId",
            Summary = [$"Plan: {planPath}", .. plan.Select(p => $"{p.Key.Provider}: {p.Value.Count} titles -> {p.Key.List}")],
            NextStep = apply ? null : "move-streaming --apply",
        };

        if (plan.Values.All(rows => rows.Count == 0))
        {
            log.Info("Nothing to move.");
            return _context.Runs.Save(run);
        }

        if (!apply)
        {
            log.Info("Dry run: nothing was changed. Run it again with Apply to create the lists, fill them, then remove these titles from the source lists.");
            foreach (var (target, rows) in plan)
            {
                foreach (var row in rows)
                {
                    logRows.Add(LogRow(target.List.ToString(), row, "would move"));
                }
            }

            return _context.Runs.Save(run);
        }

        var client = await _context.TraktAsync(ct);
        log.Info("Taking a Trakt backup first...");
        await new BackupOperation(_context).RunAsync(client, Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), ct);

        var moved = new List<Row>();
        foreach (var (target, rows) in plan.Where(p => p.Value.Count > 0))
        {
            var listId = await client.ResolveOrCreateListAsync(target.List, ct);
            if (target.List.Id != listId)
            {
                // Remember the id of a list that has just been created, so a later rename cannot break the setting.
                target.List.Id = listId;
                _context.Settings.Save();
            }

            var added = await client.PostAsync(TraktClient.AddPath(listId), Body(rows), ct);
            log.Info($"{target.List}: added {added?["added"]?.ToJsonString()}, not found {added?["not_found"]?.ToJsonString()}");

            // Check the titles really landed before anything is removed from the old lists.
            var present = (await client.GetAllAsync(TraktClient.ItemsPath(listId), ct) as JsonArray ?? [])
                .Select(e => (e!["type"]!.GetValue<string>(), e[e["type"]!.GetValue<string>()!]!["ids"]!["trakt"]!.GetValue<long>()))
                .ToHashSet();
            foreach (var row in rows)
            {
                var ok = present.Contains((row.Type, row.TraktId));
                logRows.Add(LogRow(target.List.ToString(), row, ok ? "on the new list" : "MISSING from the new list"));
                if (ok)
                {
                    moved.Add(row);
                }
                else
                {
                    log.Warn($"not confirmed on '{target.List}', left on the old lists: {row.Title} ({row.Year})");
                }
            }
        }

        if (moved.Count > 0)
        {
            var sources = await TraktRemoveOperation.ResolveListsAsync(client, _context, ct);
            var body = Body(moved);
            foreach (var source in sources)
            {
                var response = await client.PostAsync(TraktClient.RemovePath(source.Id), body.DeepClone(), ct);
                log.Info($"{source.Name}: deleted {response?["deleted"]?.ToJsonString()}");
            }
        }

        run.CsvPath = Path.Combine(_context.Paths.Reports, "trakt-streaming-move-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        Csv.WriteTable(run.CsvPath, run.Columns, logRows);
        log.Success($"Done. {moved.Count} titles moved. Log: {run.CsvPath}");
        return _context.Runs.Save(run);
    }

    private static Dictionary<string, string> LogRow(string list, Row row, string result) => new()
    {
        ["List"] = list,
        ["Type"] = row.Type,
        ["Title"] = row.Title,
        ["Year"] = row.Year,
        ["TraktId"] = row.TraktId.ToString(),
        ["Result"] = result,
    };

    private static JsonObject Body(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        JsonArray Ids(string type) => new(list.Where(r => r.Type == type)
            .Select(r => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = r.TraktId } }).ToArray());
        return new JsonObject { ["movies"] = Ids("movie"), ["shows"] = Ids("show") };
    }

    private static List<Row> ReadRows(CsvTable table, string wantedAction)
    {
        var rows = new List<Row>();
        foreach (var cells in table.Rows)
        {
            if (cells.GetValueOrDefault("Action", "").Trim() == wantedAction
                && long.TryParse(cells.GetValueOrDefault("TraktId", ""), out var id))
            {
                rows.Add(new Row(
                    cells.GetValueOrDefault("Type", ""),
                    cells.GetValueOrDefault("Title", ""),
                    cells.GetValueOrDefault("Year", ""),
                    id));
            }
        }

        return rows;
    }
}
