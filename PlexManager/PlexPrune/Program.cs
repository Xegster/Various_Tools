using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PlexScan;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Phase 2: lists Plex movies you rated below a threshold on Trakt, then deletes the ones you approved.
    // Usage: PlexPrune apply <csv|latest> [--apply]   delete every movie in the reviewed CSV whose Keep column is not "yes"
    //        from Plex (file included), then remove it from Radarr with an import exclusion; dry run unless --apply
    //        PlexPrune candidates [max-rating-exclusive] [trakt-backup-folder]   (read-only)
    //        defaults: below 7, newest finished folder in TraktBackup\Backups
    // Writes Reports\prune-<timestamp>\prune-candidates.csv and records its path in Reports\latest-prune.txt.
    // The Keep column is blank; put "yes" on any movie you want to keep and it will be skipped by the delete step.
    if (args.Length > 1 && args[0] == "apply")
    {
        return await Apply(args[1], args.Contains("--apply"), cts.Token);
    }

    if (args.Length == 0 || args[0] != "candidates")
    {
        Console.Error.WriteLine("Usage: PlexPrune candidates [max-rating-exclusive] [trakt-backup-folder] | PlexPrune apply <csv|latest> [--apply]");
        return 1;
    }

    var threshold = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 7;
    var config = PlexConfig.Load();
    var home = PlexConfig.HomeDirectory();
    var backup = args.Length > 2 ? args[2] : LatestBackup(home);
    Console.WriteLine($"Trakt data: {backup}");

    // Trakt ratings below the threshold, indexed by every id Plex might know the movie by.
    var ratings = JsonNode.Parse(File.ReadAllText(Path.Combine(backup, "ratings-movies.json")))!.AsArray();
    var low = new Dictionary<string, JsonNode>();
    var lowCount = 0;
    foreach (var entry in ratings)
    {
        if (entry!["rating"]!.GetValue<int>() >= threshold)
        {
            continue;
        }

        lowCount++;
        var ids = entry["movie"]!["ids"]!;
        foreach (var key in TraktKeys(ids))
        {
            low[key] = entry;
        }
    }

    Console.WriteLine($"  {ratings.Count} rated movies on Trakt, {lowCount} rated below {threshold}");

    using var http = new HttpClient();
    var plex = new PlexClient(config, http);
    Console.WriteLine("Reading Plex movies...");
    var movies = await plex.GetAllAsync("/library/sections/4/all", cts.Token);

    var rows = new List<(string[] Cells, long Bytes, int Rating, string Title)>();
    var matchedTrakt = new HashSet<string>();
    foreach (var m in movies)
    {
        var entry = PlexKeys(m).Select(k => low.GetValueOrDefault(k)).FirstOrDefault(e => e != null);
        if (entry == null)
        {
            continue;
        }

        var ids = entry["movie"]!["ids"]!;
        matchedTrakt.Add(ids["trakt"]!.ToString());
        var imdb = ids["imdb"]?.ToString() ?? PlexKeys(m).FirstOrDefault(k => k.StartsWith("imdb:"))?[5..];
        var part = m["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault();
        var bytes = part?["size"]?.GetValue<long>() ?? 0;
        var title = m["title"]?.GetValue<string>() ?? "?";
        var rating = entry["rating"]!.GetValue<int>();
        rows.Add(([
            title,
            m["year"]?.ToString() ?? "",
            rating.ToString(CultureInfo.InvariantCulture),
            entry["rated_at"]?.GetValue<string>()?[..10] ?? "",
            imdb != null ? $"https://www.imdb.com/title/{imdb}/" : "",
            (m["viewCount"]?.GetValue<int>() ?? 0) > 0 ? "yes" : "no",
            (bytes / 1_048_576).ToString(CultureInfo.InvariantCulture),
            part?["file"]?.GetValue<string>() ?? "",
            m["ratingKey"]!.GetValue<string>(),
            "",
        ], bytes, rating, title));
    }

    var sorted = rows.OrderBy(r => r.Rating).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    var dir = Path.Combine(home, "Reports", "prune-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "prune-candidates.csv");
    WriteCsv(path, ["Title", "Year", "TraktRating", "RatedAt", "IMDb", "WatchedInPlex", "SizeMB", "File", "RatingKey", "Keep"], sorted.Select(r => r.Cells).ToList());
    File.WriteAllText(Path.Combine(home, "Reports", "latest-prune.txt"), path);

    Console.WriteLine();
    Console.WriteLine($"  In Plex and rated below {threshold}: {sorted.Count} movies, {sorted.Sum(r => r.Bytes) / 1_073_741_824.0:F1} GB");
    Console.WriteLine($"  Rated below {threshold} on Trakt but not in Plex: {lowCount - matchedTrakt.Count}");
    foreach (var g in sorted.GroupBy(r => r.Rating).OrderBy(g => g.Key))
    {
        Console.WriteLine($"    rated {g.Key}: {g.Count()}");
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {path}");
    Console.WriteLine("Review it: put \"yes\" in the Keep column for anything you want to keep. Nothing has been deleted.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> Apply(string csvArg, bool apply, CancellationToken ct)
{
    var home = PlexConfig.HomeDirectory();
    var csvPath = csvArg == "latest" ? File.ReadAllText(Path.Combine(home, "Reports", "latest-prune.txt")).Trim() : csvArg;
    var lines = File.ReadAllLines(csvPath);
    var header = SplitCsv(lines[0].TrimStart('﻿'));
    int Col(string name) => header.IndexOf(name) is var i and >= 0 ? i : throw new InvalidDataException($"Column {name} missing from {csvPath}.");
    int cTitle = Col("Title"), cYear = Col("Year"), cRating = Col("TraktRating"), cSize = Col("SizeMB"), cFile = Col("File"), cKey = Col("RatingKey"), cKeep = Col("Keep");

    var all = lines.Skip(1).Where(l => l.Length > 0).Select(SplitCsv).ToList();
    var doomed = all.Where(c => !c[cKeep].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)).ToList();
    var kept = all.Count - doomed.Count;
    Console.WriteLine($"{all.Count} candidates in {csvPath}: {kept} marked Keep, {doomed.Count} to delete ({doomed.Sum(c => long.Parse(c[cSize], CultureInfo.InvariantCulture)) / 1024.0:F1} GB)");

    var config = PlexConfig.Load();
    using var http = new HttpClient();
    var plex = new PlexClient(config, http);
    var radarr = RadarrConfig.Load(home);
    using var radarrHttp = new HttpClient { BaseAddress = new Uri(radarr.BaseUrl + "/") };
    radarrHttp.DefaultRequestHeaders.Add("X-Api-Key", radarr.ApiKey);

    Console.WriteLine("Reading Radarr library...");
    var radarrMovies = JsonNode.Parse(await radarrHttp.GetStringAsync("api/v3/movie", ct))!.AsArray();
    var radarrByTmdb = new Dictionary<string, JsonNode>();
    var radarrByImdb = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
    foreach (var rm in radarrMovies)
    {
        if (rm!["tmdbId"] is { } t && t.GetValue<int>() > 0)
        {
            radarrByTmdb[t.ToString()] = rm;
        }

        if (rm["imdbId"]?.GetValue<string>() is { Length: > 0 } imdbId)
        {
            radarrByImdb[imdbId] = rm;
        }
    }

    var log = new StringBuilder("RatingKey,Title,Year,TraktRating,SizeMB,File,Imdb,Tmdb,Plex,Radarr\n");
    var deleted = 0;
    var problems = 0;
    foreach (var c in doomed)
    {
        var key = c[cKey];
        var label = $"{c[cTitle]} ({c[cYear]}), rated {c[cRating]}";

        // Re-read the item and check it is still the file the reviewed list described.
        JsonNode? item = null;
        try
        {
            item = (await plex.GetAsync($"/library/metadata/{key}?includeGuids=1", ct))["Metadata"]?[0];
        }
        catch (HttpRequestException)
        {
        }

        var file = item?["Media"]?[0]?["Part"]?[0]?["file"]?.GetValue<string>();
        if (item == null || file != c[cFile])
        {
            Console.WriteLine($"  SKIP {label}: no longer in Plex with the listed file");
            log.Append($"{key},{Q(c[cTitle])},{c[cYear]},{c[cRating]},{c[cSize]},{Q(c[cFile])},,,skipped (changed),\n");
            problems++;
            continue;
        }

        var keys = new List<string>();
        foreach (var g in item["Guid"]?.AsArray() ?? [])
        {
            keys.Add(g?["id"]?.GetValue<string>() ?? "");
        }

        var tmdb = keys.FirstOrDefault(k => k.StartsWith("tmdb://", StringComparison.Ordinal))?[7..];
        var imdb = keys.FirstOrDefault(k => k.StartsWith("imdb://", StringComparison.Ordinal))?[7..];
        JsonNode? inRadarr = (tmdb != null ? radarrByTmdb.GetValueOrDefault(tmdb) : null) ?? (imdb != null ? radarrByImdb.GetValueOrDefault(imdb) : null);
        if (!apply)
        {
            Console.WriteLine($"  would delete {label}, {c[cSize]} MB; Radarr: {(inRadarr != null ? "remove + exclude" : "not in Radarr")}");
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
            Console.WriteLine($"  FAILED {label}: {ex.Message}");
            log.Append($"{key},{Q(c[cTitle])},{c[cYear]},{c[cRating]},{c[cSize]},{Q(c[cFile])},{imdb},{tmdb},failed: {ex.Message},not attempted\n");
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
            using var resp = await radarrHttp.DeleteAsync($"api/v3/movie/{inRadarr["id"]}?deleteFiles=false&addImportExclusion=true", ct);
            radarrResult = resp.IsSuccessStatusCode ? "removed + excluded" : $"failed: {(int)resp.StatusCode}";
            if (!resp.IsSuccessStatusCode)
            {
                problems++;
            }
        }

        Console.WriteLine($"  {label}: Plex {plexResult}, Radarr {radarrResult}");
        log.Append($"{key},{Q(c[cTitle])},{c[cYear]},{c[cRating]},{c[cSize]},{Q(c[cFile])},{imdb},{tmdb},{plexResult},{radarrResult}\n");
    }

    if (!apply)
    {
        Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to delete these.");
        return 0;
    }

    var logPath = Path.Combine(home, "Reports", "prune-delete-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
    File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
    Console.WriteLine($"Done. {deleted} deleted, {problems} problems. Log: {logPath}");
    return problems == 0 ? 0 : 1;
}

static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

static List<string> SplitCsv(string line)
{
    var cells = new List<string>();
    var sb = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (quoted)
        {
            if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else if (ch == '"')
            {
                quoted = false;
            }
            else
            {
                sb.Append(ch);
            }
        }
        else if (ch == '"')
        {
            quoted = true;
        }
        else if (ch == ',')
        {
            cells.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(ch);
        }
    }

    cells.Add(sb.ToString());
    return cells;
}

static string LatestBackup(string home)
{
    var root = Path.Combine(home, "TraktBackup", "Backups");
    // smart-lists.json is the last file a backup writes, so a folder without it is unfinished or failed.
    return Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "smart-lists.json"))).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
        ?? throw new DirectoryNotFoundException($"No finished Trakt backups in {root}. Run TraktBackup first.");
}

static HashSet<string> TraktKeys(JsonNode ids)
{
    var keys = new HashSet<string>();
    foreach (var scheme in new[] { "imdb", "tmdb" })
    {
        if (ids[scheme] != null)
        {
            keys.Add($"{scheme}:{ids[scheme]}");
        }
    }

    if (ids["plex"]?["guid"]?.GetValue<string>() is { } plexGuid)
    {
        keys.Add("plex:" + plexGuid);
    }

    return keys;
}

static HashSet<string> PlexKeys(JsonNode item)
{
    var keys = new HashSet<string>();
    var guids = item["Guid"]?.AsArray().Select(g => g?["id"]?.GetValue<string>()).ToList() ?? [];
    guids.Add(item["guid"]?.GetValue<string>());
    foreach (var g in guids)
    {
        if (g == null)
        {
            continue;
        }

        if (g.StartsWith("imdb://", StringComparison.Ordinal))
        {
            keys.Add("imdb:" + g[7..]);
        }
        else if (g.StartsWith("tmdb://", StringComparison.Ordinal))
        {
            keys.Add("tmdb:" + g[7..]);
        }
        else if (g.StartsWith("plex://", StringComparison.Ordinal))
        {
            keys.Add("plex:" + g[(g.LastIndexOf('/') + 1)..]);
        }
    }

    return keys;
}

static void WriteCsv(string path, string[] header, List<string[]> rows)
{
    static string Cell(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    var sb = new StringBuilder();
    sb.AppendLine(string.Join(",", header.Select(Cell)));
    foreach (var row in rows)
    {
        sb.AppendLine(string.Join(",", row.Select(Cell)));
    }

    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
}

sealed record RadarrConfig(string BaseUrl, string ApiKey)
{
    public static RadarrConfig Load(string home)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(home, PlexConfig.FileName)));
        var r = doc.RootElement.GetProperty("radarr");
        var key = r.GetProperty("apiKey").GetString();
        return string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException("radarr.apiKey is empty in " + PlexConfig.FileName)
            : new RadarrConfig(PlexConfig.ServiceUrl(doc.RootElement, "radarr"), key);
    }
}
