using System.Text.Json;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>
/// Tidies the Trakt watchlist and the overflow list "Watch list 2".
/// Movies with any play are removed from both. Shows are removed from both only when every aired episode is watched
/// and Trakt lists the show as canceled or ended. Anything else on both lists is removed from the watchlist and kept in "Watch list 2".
/// </summary>
public sealed class WatchlistCleaner
{
    private const string OverflowListName = "Watch list 2";
    private static readonly string[] DoneShowStatuses = ["canceled", "ended"];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly TraktClient _client;
    private readonly string _homeDir;

    public WatchlistCleaner(TraktClient client, string homeDir)
    {
        _client = client;
        _homeDir = homeDir;
    }

    private sealed record Entry(string Type, long TraktId, string Title, int? Year)
    {
        public string Key => $"{Type}:{TraktId}";

        public override string ToString() => $"{Title} ({Year?.ToString() ?? "?"}) [{Type}]";
    }

    private sealed record Removal(Entry Entry, bool FromWatchlist, bool FromList, string Reason);

    /// <summary>Builds the removal plan from live Trakt data. When apply is true the plan is carried out and the local copy refreshed.</summary>
    public async Task RunAsync(bool apply, Func<CancellationToken, Task> backup, CancellationToken ct)
    {
        Console.WriteLine("Reading watchlist, lists and watched state...");
        var listId = await FindOverflowListIdAsync(ct);
        var watchlist = ReadEntries(await _client.GetAllAsync("/sync/watchlist", ct));
        var list = ReadEntries(await _client.GetAllAsync($"/users/me/lists/{listId}/items", ct));
        var watchedMovies = ReadIds(await _client.GetAllAsync("/sync/watched/movies", ct), "movie");
        var watchedShows = ReadIds(await _client.GetAllAsync("/sync/watched/shows", ct), "show");
        Console.WriteLine($"  watchlist: {watchlist.Count}, {OverflowListName}: {list.Count}, watched movies: {watchedMovies.Count}, watched shows: {watchedShows.Count}");

        var onWatchlist = watchlist.ToDictionary(e => e.Key);
        var onList = list.ToDictionary(e => e.Key);
        var plan = new List<Removal>();
        var keptShows = new List<string>();

        foreach (var entry in onWatchlist.Values.Concat(onList.Values.Where(e => !onWatchlist.ContainsKey(e.Key))))
        {
            var inWatchlist = onWatchlist.ContainsKey(entry.Key);
            var inList = onList.ContainsKey(entry.Key);

            string? seenReason = null;
            if (entry.Type == "movie" && watchedMovies.Contains(entry.TraktId))
            {
                seenReason = "movie watched";
            }
            else if (entry.Type == "show" && watchedShows.Contains(entry.TraktId))
            {
                seenReason = await ShowFinishedReasonAsync(entry, ct);
                if (seenReason == null)
                {
                    keptShows.Add(entry.ToString());
                }
            }

            if (seenReason != null)
            {
                plan.Add(new Removal(entry, inWatchlist, inList, seenReason));
            }
            else if (inWatchlist && inList)
            {
                plan.Add(new Removal(entry, true, false, $"duplicate (kept in {OverflowListName})"));
            }
        }

        PrintPlan(plan, keptShows);
        if (plan.Count == 0)
        {
            return;
        }

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to remove these items.");
            return;
        }

        await backup(ct);

        await RemoveAsync("/sync/watchlist/remove", plan.Where(r => r.FromWatchlist).Select(r => r.Entry), ct);
        await RemoveAsync($"/users/me/lists/{listId}/items/remove", plan.Where(r => r.FromList).Select(r => r.Entry), ct);

        await SaveLocalCopyAsync(listId, plan, ct);
    }

    private async Task<long> FindOverflowListIdAsync(CancellationToken ct)
    {
        var lists = await _client.GetAllAsync("/users/me/lists", ct, paged: false) as JsonArray ?? [];
        var match = lists.FirstOrDefault(l => string.Equals(l?["name"]?.ToString(), OverflowListName, StringComparison.OrdinalIgnoreCase));
        return match?["ids"]?["trakt"]?.GetValue<long>() ?? throw new InvalidOperationException($"No list named '{OverflowListName}' found.");
    }

    /// <summary>Returns why a started show counts as seen, or null when it should stay on the lists.</summary>
    private async Task<string?> ShowFinishedReasonAsync(Entry show, CancellationToken ct)
    {
        var details = await _client.GetAllAsync($"/shows/{show.TraktId}?extended=full", ct, paged: false);
        var status = details?["status"]?.ToString();
        if (!DoneShowStatuses.Contains(status?.ToLowerInvariant()))
        {
            return null;
        }

        var progress = await _client.GetAllAsync($"/shows/{show.TraktId}/progress/watched?hidden=false&specials=false", ct, paged: false);
        var aired = progress?["aired"]?.GetValue<int>() ?? 0;
        var completed = progress?["completed"]?.GetValue<int>() ?? 0;
        return aired > 0 && completed >= aired ? $"{status} show, all {aired} aired episodes watched" : null;
    }

    private async Task RemoveAsync(string path, IEnumerable<Entry> entries, CancellationToken ct)
    {
        var items = entries.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var body = new JsonObject
        {
            ["movies"] = Ids(items.Where(e => e.Type == "movie")),
            ["shows"] = Ids(items.Where(e => e.Type == "show")),
        };

        var response = await _client.PostAsync(path, body, ct);
        Console.WriteLine($"Removed via {path}: {response?["deleted"]?.ToJsonString()} (not found: {response?["not_found"]?.ToJsonString()})");
    }

    private static JsonArray Ids(IEnumerable<Entry> entries) =>
        new(entries.Select(e => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = e.TraktId } }).ToArray());

    /// <summary>Re-reads both lists from Trakt into Current\ and writes a log of what was removed.</summary>
    private async Task SaveLocalCopyAsync(long listId, List<Removal> plan, CancellationToken ct)
    {
        var current = Path.Combine(_homeDir, "Current");
        Directory.CreateDirectory(current);

        var watchlist = await _client.GetAllAsync("/sync/watchlist", ct);
        var list = await _client.GetAllAsync($"/users/me/lists/{listId}/items", ct);
        File.WriteAllText(Path.Combine(current, "watchlist.json"), watchlist?.ToJsonString(Pretty) ?? "[]");
        File.WriteAllText(Path.Combine(current, "watch-list-2.json"), list?.ToJsonString(Pretty) ?? "[]");

        var log = plan.Select(r => new
        {
            item = r.Entry.ToString(),
            trakt = r.Entry.TraktId,
            r.FromWatchlist,
            FromWatchList2 = r.FromList,
            r.Reason,
        });
        var logPath = Path.Combine(_homeDir, "Backups", $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, JsonSerializer.Serialize(log, Pretty));

        Console.WriteLine($"Local copy refreshed: {(watchlist as JsonArray)?.Count} on watchlist, {(list as JsonArray)?.Count} on {OverflowListName}.");
        Console.WriteLine($"Removal log: {logPath}");
    }

    private static void PrintPlan(List<Removal> plan, List<string> keptShows)
    {
        foreach (var group in plan.GroupBy(r => r.Reason.StartsWith("duplicate") ? "Duplicates (removed from watchlist only)" : "Seen (removed everywhere)"))
        {
            Console.WriteLine();
            Console.WriteLine($"{group.Key}: {group.Count()}");
            foreach (var removal in group.OrderBy(r => r.Entry.Title))
            {
                var from = removal.FromWatchlist && removal.FromList ? "both" : removal.FromWatchlist ? "watchlist" : OverflowListName;
                Console.WriteLine($"  - {removal.Entry}  [{from}]  {removal.Reason}");
            }
        }

        if (keptShows.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Started shows kept (still running or not fully watched): {keptShows.Count}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total items to remove: {plan.Count}");
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
