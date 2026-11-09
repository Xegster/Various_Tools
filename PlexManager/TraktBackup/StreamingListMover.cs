using System.Text;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>
/// Moves titles that the PlexMigrate plan marked "skip: on Netflix" or "skip: on Prime Video" off the watchlist and "Watch list 2"
/// and onto the Trakt lists "Watch on Netflix" and "Watch on Prime" (created if missing). A title on both services is already
/// marked Netflix by the plan. The new lists are filled and checked first; only then are the titles removed from the old lists.
/// </summary>
public sealed class StreamingListMover
{
    private const string OverflowListName = "Watch list 2";

    private static readonly (string Action, string ListName)[] Targets =
    [
        ("skip: on Netflix", "Watch on Netflix"),
        ("skip: on Prime Video", "Watch on Prime"),
    ];

    private readonly TraktClient _client;
    private readonly string _reportsDir;

    public StreamingListMover(TraktClient client, string reportsDir)
    {
        _client = client;
        _reportsDir = reportsDir;
    }

    private sealed record Row(string Type, string Title, string Year, long TraktId);

    public async Task RunAsync(string csvPath, bool apply, Func<CancellationToken, Task> backup, CancellationToken ct)
    {
        var plan = Targets.ToDictionary(t => t.ListName, t => ReadRows(csvPath, t.Action));
        foreach (var (list, rows) in plan)
        {
            Console.WriteLine($"{list}: {rows.Count(r => r.Type == "movie")} movies, {rows.Count(r => r.Type == "show")} shows");
            foreach (var row in rows)
            {
                Console.WriteLine($"  {row.Title} ({row.Year}) [{row.Type}]");
            }
        }

        if (plan.Values.All(r => r.Count == 0))
        {
            return;
        }

        if (!apply)
        {
            Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to create the lists, fill them, then remove these titles from the watchlist and Watch list 2.");
            return;
        }

        Console.WriteLine("Taking a backup first...");
        await backup(ct);

        var lists = await _client.GetAllAsync("/users/me/lists", ct, paged: false) as JsonArray ?? [];
        long ListId(string name) => lists.FirstOrDefault(l => string.Equals(l?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))?["ids"]?["trakt"]?.GetValue<long>() ?? 0;

        var log = new StringBuilder("List,Type,Title,Year,TraktId,Result\n");
        var all = new List<Row>();
        foreach (var (listName, rows) in plan.Where(p => p.Value.Count > 0))
        {
            var id = ListId(listName);
            if (id == 0)
            {
                var created = await _client.PostAsync("/users/me/lists", new JsonObject
                {
                    ["name"] = listName,
                    ["privacy"] = "private",
                    ["display_numbers"] = false,
                    ["allow_comments"] = false,
                }, ct);
                id = created?["ids"]?["trakt"]?.GetValue<long>() ?? throw new InvalidOperationException($"Could not create list '{listName}'.");
                Console.WriteLine($"Created list '{listName}'.");
            }

            var added = await _client.PostAsync($"/users/me/lists/{id}/items", Body(rows), ct);
            Console.WriteLine($"{listName}: added {added?["added"]?.ToJsonString()}, not found {added?["not_found"]?.ToJsonString()}");

            // Verify before anything is removed from the old lists.
            var present = (await _client.GetAllAsync($"/users/me/lists/{id}/items", ct) as JsonArray ?? [])
                .Select(e => (e!["type"]!.GetValue<string>(), e[e["type"]!.GetValue<string>()!]!["ids"]!["trakt"]!.GetValue<long>()))
                .ToHashSet();
            foreach (var row in rows)
            {
                var ok = present.Contains((row.Type, row.TraktId));
                log.Append($"{listName},{row.Type},\"{row.Title.Replace("\"", "\"\"")}\",{row.Year},{row.TraktId},{(ok ? "on new list" : "MISSING from new list")}\n");
                if (ok)
                {
                    all.Add(row);
                }
                else
                {
                    Console.WriteLine($"  NOT confirmed on '{listName}', left on the old lists: {row.Title} ({row.Year})");
                }
            }
        }

        if (all.Count > 0)
        {
            var overflowId = ListId(OverflowListName);
            var removeBody = Body(all);
            var wl = await _client.PostAsync("/sync/watchlist/remove", removeBody.DeepClone(), ct);
            Console.WriteLine($"Watchlist: deleted {wl?["deleted"]?.ToJsonString()}");
            if (overflowId != 0)
            {
                var l2 = await _client.PostAsync($"/users/me/lists/{overflowId}/items/remove", removeBody.DeepClone(), ct);
                Console.WriteLine($"{OverflowListName}: deleted {l2?["deleted"]?.ToJsonString()}");
            }
        }

        Directory.CreateDirectory(_reportsDir);
        var logPath = Path.Combine(_reportsDir, "trakt-streaming-move-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Done. Log: {logPath}");
    }

    private static JsonObject Body(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        JsonArray Ids(string type) => new(list.Where(r => r.Type == type)
            .Select(r => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = r.TraktId } }).ToArray());
        return new JsonObject { ["movies"] = Ids("movie"), ["shows"] = Ids("show") };
    }

    private static List<Row> ReadRows(string path, string wantedAction)
    {
        var lines = File.ReadAllLines(path);
        var header = MovieHistoryPusher.SplitCsv(lines[0].TrimStart('﻿'));
        int Col(string name) => header.IndexOf(name) is var i and >= 0 ? i : throw new InvalidDataException($"Column {name} missing from {path}.");
        int action = Col("Action"), type = Col("Type"), title = Col("Title"), year = Col("Year"), trakt = Col("TraktId");

        var rows = new List<Row>();
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var cells = MovieHistoryPusher.SplitCsv(line);
            if (cells[action].Trim() == wantedAction && long.TryParse(cells[trakt], out var id))
            {
                rows.Add(new Row(cells[type], cells[title], cells[year], id));
            }
        }

        return rows;
    }
}
