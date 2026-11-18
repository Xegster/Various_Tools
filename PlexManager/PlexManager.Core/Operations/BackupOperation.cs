using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlexManager.Core.Clients;

namespace PlexManager.Core.Operations;

/// <summary>Pulls everything from the Trakt account into a timestamped folder of JSON files plus a flat CSV of ids.</summary>
public sealed class BackupOperation
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly OperationContext _context;

    public BackupOperation(OperationContext context)
    {
        _context = context;
    }

    /// <summary>Runs a backup into a new folder under Backups and returns that folder.</summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        var client = await _context.TraktAsync(ct);
        var outputDir = Path.Combine(_context.Paths.Backups, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        return await RunAsync(client, outputDir, ct);
    }

    /// <summary>Runs a backup with a client the caller already has (so a write operation can back up first).</summary>
    public async Task<string> RunAsync(TraktClient client, string outputDir, CancellationToken ct)
    {
        var log = _context.Log;
        Directory.CreateDirectory(outputDir);
        log.Info($"Backing up Trakt to {outputDir}");

        await FetchAsync(client, outputDir, "settings.json", "/users/settings", ct, paged: false);
        await FetchAsync(client, outputDir, "last_activities.json", "/sync/last_activities", ct, paged: false);

        // Watched state. Shows include every watched season and episode.
        var watchedMovies = await FetchAsync(client, outputDir, "watched-movies.json", "/sync/watched/movies", ct);
        var watchedShows = await FetchAsync(client, outputDir, "watched-shows.json", "/sync/watched/shows", ct);
        await FetchAsync(client, outputDir, "history.json", "/sync/history", ct);

        foreach (var type in new[] { "movies", "shows", "seasons", "episodes" })
        {
            await FetchAsync(client, outputDir, $"ratings-{type}.json", $"/sync/ratings/{type}", ct);
        }

        var watchlist = await FetchAsync(client, outputDir, "watchlist.json", "/sync/watchlist", ct);
        await FetchAsync(client, outputDir, "favorites-movies.json", "/sync/favorites/movies", ct);
        await FetchAsync(client, outputDir, "favorites-shows.json", "/sync/favorites/shows", ct);
        await FetchAsync(client, outputDir, "collection-movies.json", "/sync/collection/movies", ct);
        await FetchAsync(client, outputDir, "collection-shows.json", "/sync/collection/shows", ct);

        var lists = await BackupListsAsync(client, outputDir, ct);
        await FetchAsync(client, outputDir, "lists-liked.json", "/users/likes/lists", ct);
        await FetchAsync(client, outputDir, "lists-collaborations.json", "/users/me/lists/collaborations", ct, optional: true);

        // smart-lists.json is written last on purpose: its presence marks the folder as a finished backup.
        await FetchAsync(client, outputDir, "smart-lists.json", "/users/me/smart-lists", ct, paged: false, optional: true);

        WriteIdsCsv(outputDir, watchedMovies, watchedShows, watchlist, lists);

        var run = new RunRecord
        {
            Id = RunRecord.NewId("backup"),
            Kind = "backup",
            Title = "Trakt backup",
            StartedAt = DateTimeOffset.Now,
            FinishedAt = DateTimeOffset.Now,
            Applied = false,
            Summary =
            [
                $"Folder: {outputDir}",
                $"Watched movies: {(watchedMovies as JsonArray)?.Count ?? 0}",
                $"Watched shows: {(watchedShows as JsonArray)?.Count ?? 0}",
                $"Watchlist: {(watchlist as JsonArray)?.Count ?? 0}",
                $"Personal lists: {lists.Count}",
                $"API calls: {client.Calls}",
            ],
            Columns = ["List", "Items"],
            Rows = lists.Select(l => new Dictionary<string, string>
            {
                ["List"] = l.Name,
                ["Items"] = ((l.Items as JsonArray)?.Count ?? 0).ToString(),
            }).ToList(),
        };
        _context.Runs.Save(run);

        log.Success($"Backup done. {client.Calls} API calls. {outputDir}");
        return outputDir;
    }

    /// <summary>Fetches every personal list and its items into lists/&lt;slug&gt;.json.</summary>
    private async Task<List<(string Name, JsonNode? Items)>> BackupListsAsync(TraktClient client, string outputDir, CancellationToken ct)
    {
        var index = await FetchAsync(client, outputDir, "lists.json", "/users/me/lists", ct, paged: false);
        var result = new List<(string, JsonNode?)>();
        if (index is not JsonArray lists)
        {
            return result;
        }

        Directory.CreateDirectory(Path.Combine(outputDir, "lists"));
        foreach (var list in lists)
        {
            var name = list?["name"]?.GetValue<string>() ?? "list";
            var traktId = list?["ids"]?["trakt"]?.GetValue<long>() ?? 0;
            var slug = list?["ids"]?["slug"]?.GetValue<string>() ?? traktId.ToString();

            var items = await client.GetAllAsync($"/users/me/lists/{traktId}/items", ct);
            _context.Log.Detail($"list '{name}': {(items as JsonArray)?.Count ?? 0} items");

            var document = new JsonObject { ["list"] = list!.DeepClone(), ["items"] = items };
            File.WriteAllText(Path.Combine(outputDir, "lists", $"{Safe(slug)}.json"), document.ToJsonString(Pretty));
            result.Add((name, items));
        }

        return result;
    }

    private async Task<JsonNode?> FetchAsync(
        TraktClient client,
        string outputDir,
        string file,
        string path,
        CancellationToken ct,
        bool paged = true,
        bool optional = false)
    {
        _context.Log.Detail($"Fetching {file}");
        try
        {
            var data = await client.GetAllAsync(path, ct, paged);
            File.WriteAllText(Path.Combine(outputDir, file), data?.ToJsonString(Pretty) ?? "null");
            return data;
        }
        catch (TraktApiException ex) when (optional && (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden || (int)ex.Status == 426))
        {
            _context.Log.Detail($"skipped ({(int)ex.Status})");
            return null;
        }
    }

    /// <summary>One row per title (watched, on the watchlist, or on a list) with the ids Radarr (tmdb) and Sonarr (tvdb) need.</summary>
    private static void WriteIdsCsv(
        string outputDir,
        JsonNode? watchedMovies,
        JsonNode? watchedShows,
        JsonNode? watchlist,
        List<(string Name, JsonNode? Items)> lists)
    {
        var csv = new StringBuilder("source,list,type,title,year,trakt,imdb,tmdb,tvdb\n");

        void Row(string source, string listName, string type, JsonNode? media)
        {
            if (media == null)
            {
                return;
            }

            var ids = media["ids"];
            csv.AppendLine(string.Join(
                ',',
                source,
                Quote(listName),
                type,
                Quote(media["title"]?.ToString()),
                media["year"],
                ids?["trakt"],
                ids?["imdb"],
                ids?["tmdb"],
                ids?["tvdb"]));
        }

        foreach (var item in watchedMovies as JsonArray ?? [])
        {
            Row("watched", "", "movie", item?["movie"]);
        }

        foreach (var item in watchedShows as JsonArray ?? [])
        {
            Row("watched", "", "show", item?["show"]);
        }

        foreach (var item in watchlist as JsonArray ?? [])
        {
            var type = item?["type"]?.ToString() ?? "";
            Row("watchlist", "", type, item?[type]);
        }

        foreach (var (name, items) in lists)
        {
            foreach (var item in items as JsonArray ?? [])
            {
                var type = item?["type"]?.ToString() ?? "";
                Row("list", name, type, item?[type]);
            }
        }

        File.WriteAllText(Path.Combine(outputDir, "titles.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
