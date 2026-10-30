using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TraktBackup;

/// <summary>Pulls everything from the user's Trakt account into a timestamped folder of JSON files plus a flat CSV of ids.</summary>
public sealed class BackupRunner
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly TraktClient _client;
    private readonly string _outputDir;

    public BackupRunner(TraktClient client, string outputDir)
    {
        _client = client;
        _outputDir = outputDir;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_outputDir);
        Console.WriteLine($"Backing up to {_outputDir}");

        await FetchAsync("settings.json", "/users/settings", ct, paged: false);
        await FetchAsync("last_activities.json", "/sync/last_activities", ct, paged: false);

        // Watched state. Shows include every watched season/episode.
        var watchedMovies = await FetchAsync("watched-movies.json", "/sync/watched/movies", ct);
        var watchedShows = await FetchAsync("watched-shows.json", "/sync/watched/shows", ct);
        await FetchAsync("history.json", "/sync/history", ct);

        foreach (var type in new[] { "movies", "shows", "seasons", "episodes" })
        {
            await FetchAsync($"ratings-{type}.json", $"/sync/ratings/{type}", ct);
        }

        var watchlist = await FetchAsync("watchlist.json", "/sync/watchlist", ct);
        await FetchAsync("favorites-movies.json", "/sync/favorites/movies", ct);
        await FetchAsync("favorites-shows.json", "/sync/favorites/shows", ct);
        await FetchAsync("collection-movies.json", "/sync/collection/movies", ct);
        await FetchAsync("collection-shows.json", "/sync/collection/shows", ct);

        var lists = await BackupListsAsync(ct);
        await FetchAsync("lists-liked.json", "/users/likes/lists", ct);
        await FetchAsync("lists-collaborations.json", "/users/me/lists/collaborations", ct, optional: true);
        await FetchAsync("smart-lists.json", "/users/me/smart-lists", ct, paged: false, optional: true);

        WriteIdsCsv(watchedMovies, watchedShows, watchlist, lists);

        Console.WriteLine($"Done. {_client.Calls} API calls.");
    }

    /// <summary>Fetches every personal list and its items into lists/&lt;slug&gt;.json.</summary>
    private async Task<List<(string Name, JsonNode? Items)>> BackupListsAsync(CancellationToken ct)
    {
        var index = await FetchAsync("lists.json", "/users/me/lists", ct, paged: false);
        var result = new List<(string, JsonNode?)>();
        if (index is not JsonArray lists)
        {
            return result;
        }

        Directory.CreateDirectory(Path.Combine(_outputDir, "lists"));
        foreach (var list in lists)
        {
            var name = list?["name"]?.GetValue<string>() ?? "list";
            var traktId = list?["ids"]?["trakt"]?.GetValue<long>() ?? 0;
            var slug = list?["ids"]?["slug"]?.GetValue<string>() ?? traktId.ToString();

            var items = await _client.GetAllAsync($"/users/me/lists/{traktId}/items", ct);
            Console.WriteLine($"  list '{name}': {(items as JsonArray)?.Count ?? 0} items");

            var document = new JsonObject { ["list"] = list!.DeepClone(), ["items"] = items };
            File.WriteAllText(Path.Combine(_outputDir, "lists", $"{Safe(slug)}.json"), document.ToJsonString(Pretty));
            result.Add((name, items));
        }

        return result;
    }

    private async Task<JsonNode?> FetchAsync(string file, string path, CancellationToken ct, bool paged = true, bool optional = false)
    {
        Console.WriteLine($"Fetching {file}");
        try
        {
            var data = await _client.GetAllAsync(path, ct, paged);
            File.WriteAllText(Path.Combine(_outputDir, file), data?.ToJsonString(Pretty) ?? "null");
            if (data is JsonArray array)
            {
                Console.WriteLine($"  {array.Count} items");
            }

            return data;
        }
        catch (TraktApiException ex) when (optional && (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden || (int)ex.Status == 426))
        {
            Console.WriteLine($"  skipped ({(int)ex.Status})");
            return null;
        }
    }

    /// <summary>One row per title (watched, on the watchlist, or on a list) with the ids Radarr (tmdb) and Sonarr (tvdb) need.</summary>
    private void WriteIdsCsv(JsonNode? watchedMovies, JsonNode? watchedShows, JsonNode? watchlist, List<(string Name, JsonNode? Items)> lists)
    {
        var csv = new StringBuilder("source,list,type,title,year,trakt,imdb,tmdb,tvdb\n");

        void Row(string source, string listName, string type, JsonNode? media)
        {
            if (media == null)
            {
                return;
            }

            var ids = media["ids"];
            csv.AppendLine(string.Join(',',
                source, Quote(listName), type, Quote(media["title"]?.ToString()), media["year"], ids?["trakt"], ids?["imdb"], ids?["tmdb"], ids?["tvdb"]));
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

        File.WriteAllText(Path.Combine(_outputDir, "titles.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    private static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
