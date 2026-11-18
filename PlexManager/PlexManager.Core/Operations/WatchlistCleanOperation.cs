using System.Text.Json;
using System.Text.Json.Nodes;
using PlexManager.Core.Clients;
using PlexManager.Core.Settings;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Tidies the chosen Trakt lists (the watchlist plus whichever personal lists are selected in Settings).
/// A movie with any play is removed from all of them. A show is removed only when every aired episode is watched
/// and Trakt lists it as canceled or ended. Anything on more than one of the lists is removed from all but the
/// last one in the selection, which acts as the overflow list.
/// </summary>
public sealed class WatchlistCleanOperation
{
    private static readonly string[] DoneShowStatuses = ["canceled", "ended"];
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly OperationContext _context;

    public WatchlistCleanOperation(OperationContext context)
    {
        _context = context;
    }

    private sealed record Entry(string Type, long TraktId, string Title, int? Year)
    {
        public string Key => $"{Type}:{TraktId}";

        public override string ToString() => $"{Title} ({Year?.ToString() ?? "?"}) [{Type}]";
    }

    private sealed record Removal(Entry Entry, List<ListTarget> From, string Reason);

    private sealed record ListTarget(long Id, string Name);

    public async Task<RunRecord> RunAsync(bool apply, CancellationToken ct)
    {
        var log = _context.Log;
        var client = await _context.TraktAsync(ct);
        var started = DateTimeOffset.Now;

        var targets = await ResolveListsAsync(client, ct);
        var keepOn = targets[^1];
        log.Info("Reading " + string.Join(", ", targets.Select(t => t.Name)) + " and the watched state...");

        var contents = new Dictionary<long, Dictionary<string, Entry>>();
        foreach (var target in targets)
        {
            var items = ReadEntries(await client.GetAllAsync(TraktClient.ItemsPath(target.Id), ct));
            contents[target.Id] = items.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            log.Detail($"{target.Name}: {items.Count}");
        }

        var watchedMovies = ReadIds(await client.GetAllAsync("/sync/watched/movies", ct), "movie");
        var watchedShows = ReadIds(await client.GetAllAsync("/sync/watched/shows", ct), "show");
        log.Detail($"watched movies: {watchedMovies.Count}, watched shows: {watchedShows.Count}");

        var all = new Dictionary<string, Entry>();
        foreach (var target in targets)
        {
            foreach (var (key, entry) in contents[target.Id])
            {
                all.TryAdd(key, entry);
            }
        }

        var plan = new List<Removal>();
        var keptShows = new List<string>();
        foreach (var entry in all.Values)
        {
            var on = targets.Where(t => contents[t.Id].ContainsKey(entry.Key)).ToList();

            string? seenReason = null;
            if (entry.Type == "movie" && watchedMovies.Contains(entry.TraktId))
            {
                seenReason = "movie watched";
            }
            else if (entry.Type == "show" && watchedShows.Contains(entry.TraktId))
            {
                seenReason = await ShowFinishedReasonAsync(client, entry, ct);
                if (seenReason == null)
                {
                    keptShows.Add(entry.ToString());
                }
            }

            if (seenReason != null)
            {
                plan.Add(new Removal(entry, on, seenReason));
            }
            else if (on.Count > 1)
            {
                plan.Add(new Removal(entry, on.Where(t => t.Id != keepOn.Id).ToList(), $"duplicate (kept in {keepOn.Name})"));
            }
        }

        foreach (var group in plan.GroupBy(r => r.Reason.StartsWith("duplicate", StringComparison.Ordinal)
                     ? $"Duplicates (removed from every list but {keepOn.Name})"
                     : "Seen (removed everywhere)"))
        {
            log.Info("");
            log.Info($"{group.Key}: {group.Count()}");
            foreach (var removal in group.OrderBy(r => r.Entry.Title, StringComparer.OrdinalIgnoreCase))
            {
                log.Detail($"- {removal.Entry}  [{string.Join(", ", removal.From.Select(f => f.Name))}]  {removal.Reason}");
            }
        }

        if (keptShows.Count > 0)
        {
            log.Info("");
            log.Info($"Started shows kept (still running or not fully watched): {keptShows.Count}");
        }

        log.Info("");
        log.Info($"Total items to remove: {plan.Count}");

        var run = new RunRecord
        {
            Id = RunRecord.NewId("clean"),
            Kind = "clean",
            Title = apply ? "Watchlist cleanup (applied)" : "Watchlist cleanup (dry run)",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Applied = apply && plan.Count > 0,
            Columns = ["Type", "Title", "Year", "RemovedFrom", "Reason", "TraktId"],
            KeyColumn = "TraktId",
            Rows = plan.OrderBy(r => r.Entry.Title, StringComparer.OrdinalIgnoreCase).Select(r => new Dictionary<string, string>
            {
                ["Type"] = r.Entry.Type,
                ["Title"] = r.Entry.Title,
                ["Year"] = r.Entry.Year?.ToString() ?? "",
                ["RemovedFrom"] = string.Join(" + ", r.From.Select(f => f.Name)),
                ["Reason"] = r.Reason,
                ["TraktId"] = r.Entry.TraktId.ToString(),
            }).ToList(),
            Summary =
            [
                $"Lists: {string.Join(", ", targets.Select(t => t.Name))}",
                $"To remove: {plan.Count}",
                $"Seen: {plan.Count(r => !r.Reason.StartsWith("duplicate", StringComparison.Ordinal))}",
                $"Duplicates: {plan.Count(r => r.Reason.StartsWith("duplicate", StringComparison.Ordinal))}",
                $"Started shows kept: {keptShows.Count}",
            ],
            NextStep = apply || plan.Count == 0 ? null : "clean --apply",
        };

        if (plan.Count == 0 || !apply)
        {
            if (plan.Count > 0)
            {
                log.Info("Dry run: nothing was changed. Run it again with Apply to remove these items.");
            }

            run.CsvPath = WriteCsv(run);
            return _context.Runs.Save(run);
        }

        log.Info("Taking a Trakt backup first...");
        await new BackupOperation(_context).RunAsync(client, Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), ct);

        foreach (var target in targets)
        {
            var entries = plan.Where(r => r.From.Any(f => f.Id == target.Id)).Select(r => r.Entry).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var response = await client.PostAsync(TraktClient.RemovePath(target.Id), Body(entries), ct);
            log.Info($"{target.Name}: deleted {response?["deleted"]?.ToJsonString()} (not found: {response?["not_found"]?.ToJsonString()})");
        }

        await SaveLocalCopyAsync(client, targets, ct);
        run.CsvPath = WriteCsv(run);
        log.Success($"Cleanup done. {plan.Count} items removed.");
        return _context.Runs.Save(run);
    }

    /// <summary>The configured source lists, resolved to live ids. The watchlist is always first.</summary>
    private async Task<List<ListTarget>> ResolveListsAsync(TraktClient client, CancellationToken ct)
    {
        var refs = new List<TraktListRef>();
        if (!_context.Values.Migration.SourceLists.Any(l => l.IsWatchlist))
        {
            refs.Add(TraktListRef.Watchlist());
        }

        refs.AddRange(_context.Values.Migration.SourceLists);
        if (_context.Values.Migration.OverflowList is { } overflow && !refs.Any(r => r.Id == overflow.Id && !r.IsWatchlist))
        {
            refs.Add(overflow);
        }

        var targets = new List<ListTarget>();
        foreach (var reference in refs)
        {
            var id = await client.ResolveListIdAsync(reference, ct);
            if (targets.All(t => t.Id != id))
            {
                targets.Add(new ListTarget(id, reference.ToString()));
            }
        }

        return targets;
    }

    /// <summary>Why a started show counts as seen, or null when it should stay on the lists.</summary>
    private static async Task<string?> ShowFinishedReasonAsync(TraktClient client, Entry show, CancellationToken ct)
    {
        var details = await client.GetAllAsync($"/shows/{show.TraktId}?extended=full", ct, paged: false);
        var status = details?["status"]?.ToString();
        if (!DoneShowStatuses.Contains(status?.ToLowerInvariant()))
        {
            return null;
        }

        var progress = await client.GetAllAsync($"/shows/{show.TraktId}/progress/watched?hidden=false&specials=false", ct, paged: false);
        var aired = progress?["aired"]?.GetValue<int>() ?? 0;
        var completed = progress?["completed"]?.GetValue<int>() ?? 0;
        return aired > 0 && completed >= aired ? $"{status} show, all {aired} aired episodes watched" : null;
    }

    /// <summary>Re-reads the lists from Trakt into a timestamped folder under Current, as the old tool did.</summary>
    private async Task SaveLocalCopyAsync(TraktClient client, List<ListTarget> targets, CancellationToken ct)
    {
        var current = Path.Combine(_context.Paths.CurrentLists, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(current);
        foreach (var target in targets)
        {
            var items = await client.GetAllAsync(TraktClient.ItemsPath(target.Id), ct);
            var safe = string.Concat(target.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllText(Path.Combine(current, safe + ".json"), items?.ToJsonString(Pretty) ?? "[]");
            _context.Log.Detail($"{target.Name} now has {(items as JsonArray)?.Count ?? 0} items");
        }
    }

    private string WriteCsv(RunRecord run)
    {
        var path = Path.Combine(_context.Paths.Reports, $"clean-{run.Id[("clean-".Length)..]}.csv");
        Csv.WriteTable(path, run.Columns, run.Rows);
        _context.Log.Info($"Log: {path}");
        return path;
    }

    private static JsonObject Body(IEnumerable<Entry> entries)
    {
        var list = entries.ToList();
        JsonArray Ids(string type) => new(list.Where(e => e.Type == type)
            .Select(e => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = e.TraktId } }).ToArray());
        return new JsonObject { ["movies"] = Ids("movie"), ["shows"] = Ids("show") };
    }

    private static List<Entry> ReadEntries(JsonNode? items) =>
        (items as JsonArray ?? [])
        .Select(i => (Item: i, Type: i?["type"]?.ToString()))
        .Where(x => x.Type is "movie" or "show")
        .Select(x => (Media: x.Item![x.Type!], x.Type))
        .Where(x => x.Media?["ids"]?["trakt"] != null)
        .Select(x => new Entry(x.Type!, x.Media!["ids"]!["trakt"]!.GetValue<long>(), x.Media["title"]?.ToString() ?? "?", x.Media["year"]?.GetValue<int>()))
        .ToList();

    private static HashSet<long> ReadIds(JsonNode? items, string type) =>
        (items as JsonArray ?? [])
        .Select(i => i?[type]?["ids"]?["trakt"]?.GetValue<long>())
        .OfType<long>()
        .ToHashSet();
}
