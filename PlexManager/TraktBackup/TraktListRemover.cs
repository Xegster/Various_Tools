using System.Text;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>
/// Removes titles from the Trakt watchlist and the list "Watch list 2" when their Action cell is blank (you blanked it in a reviewed
/// PlexMigrate plan, migrate-plan.csv) or starts with "remove:" (set by the plan itself, for example a non-English series). Nothing is added anywhere. Rows with any other Action are left alone.
/// </summary>
public sealed class TraktListRemover
{
    private const string OverflowListName = "Watch list 2";

    private readonly TraktClient _client;
    private readonly string _reportsDir;

    public TraktListRemover(TraktClient client, string reportsDir)
    {
        _client = client;
        _reportsDir = reportsDir;
    }

    private sealed record Row(string Type, string Title, string Year, long TraktId);

    public async Task RunAsync(string csvPath, bool apply, Func<CancellationToken, Task> backup, CancellationToken ct)
    {
        var rows = ReadRows(csvPath);
        Console.WriteLine($"{rows.Count} titles to remove (blank or remove: Action) in {csvPath}:");
        foreach (var row in rows)
        {
            Console.WriteLine($"  {row.Title} ({row.Year}) [{row.Type}]");
        }

        if (rows.Count == 0)
        {
            return;
        }

        if (!apply)
        {
            Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to remove these from the watchlist and Watch list 2.");
            return;
        }

        Console.WriteLine("Taking a backup first...");
        await backup(ct);

        var lists = await _client.GetAllAsync("/users/me/lists", ct, paged: false) as JsonArray ?? [];
        var listId = lists.FirstOrDefault(l => string.Equals(l?["name"]?.ToString(), OverflowListName, StringComparison.OrdinalIgnoreCase))?["ids"]?["trakt"]?.GetValue<long>()
            ?? throw new InvalidOperationException($"No list named '{OverflowListName}' found.");

        var body = new JsonObject
        {
            ["movies"] = Ids(rows.Where(r => r.Type == "movie")),
            ["shows"] = Ids(rows.Where(r => r.Type == "show")),
        };

        var fromWatchlist = await _client.PostAsync("/sync/watchlist/remove", body.DeepClone(), ct);
        Console.WriteLine($"Watchlist: deleted {fromWatchlist?["deleted"]?.ToJsonString()}, not found {fromWatchlist?["not_found"]?.ToJsonString()}");
        var fromList = await _client.PostAsync($"/users/me/lists/{listId}/items/remove", body.DeepClone(), ct);
        Console.WriteLine($"{OverflowListName}: deleted {fromList?["deleted"]?.ToJsonString()}, not found {fromList?["not_found"]?.ToJsonString()}");

        var log = new StringBuilder("Type,Title,Year,TraktId\n");
        foreach (var row in rows)
        {
            log.Append($"{row.Type},\"{row.Title.Replace("\"", "\"\"")}\",{row.Year},{row.TraktId}\n");
        }

        Directory.CreateDirectory(_reportsDir);
        var logPath = Path.Combine(_reportsDir, "trakt-list-removal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Done. Log: {logPath}");
    }

    private static JsonArray Ids(IEnumerable<Row> rows) =>
        new(rows.Select(r => (JsonNode)new JsonObject { ["ids"] = new JsonObject { ["trakt"] = r.TraktId } }).ToArray());

    private static List<Row> ReadRows(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = MovieHistoryPusher.SplitCsv(lines[0].TrimStart('﻿'));
        int Col(string name) => header.IndexOf(name) is var i and >= 0 ? i : throw new InvalidDataException($"Column {name} missing from {path}.");
        // A verified list from "PlexMigrate verify" (migrate-verified-*.csv) has a PlanAction column and no Action column:
        // every row in it is confirmed present in Radarr/Sonarr, so every row is removed.
        var verified = header.IndexOf("PlanAction") >= 0 && header.IndexOf("Action") < 0;
        int action = verified ? -1 : Col("Action"), type = Col("Type"), title = Col("Title"), year = Col("Year"), trakt = Col("TraktId");

        var rows = new List<Row>();
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var cells = MovieHistoryPusher.SplitCsv(line);
            var act = verified ? "" : cells[action].Trim();
            if ((act.Length == 0 || act.StartsWith("remove:", StringComparison.OrdinalIgnoreCase)) && cells[type] is "movie" or "show" && long.TryParse(cells[trakt], out var id))
            {
                rows.Add(new Row(cells[type], cells[title], cells[year], id));
            }
        }

        return rows;
    }
}
