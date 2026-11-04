using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlexScan;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

const string ListName = "Watch list 2";
const string RadarrProfileName = "SD + 720";
const string SonarrProfileName = "SD";
const int CutoffYear = 2025;

try
{
    // Phase 3: works out what to move from Trakt (watchlist + "Watch list 2") into Radarr and Sonarr.
    // Usage: PlexMigrate plan [trakt-backup-folder]   read-only; writes Reports\migrate-<timestamp>\migrate-plan.csv
    if (args.Length == 0 || args[0] != "plan")
    {
        Console.Error.WriteLine("Usage: PlexMigrate plan [trakt-backup-folder]");
        return 1;
    }

    var ct = cts.Token;
    var home = PlexConfig.HomeDirectory();
    var backup = args.Length > 1 ? args[1] : LatestBackup(home);
    Console.WriteLine($"Trakt data: {backup}");

    // ---- Trakt inventory: watchlist + Watch list 2, deduped by Trakt id ----
    var items = new Dictionary<string, Item>();
    AddItems(items, JsonNode.Parse(File.ReadAllText(Path.Combine(backup, "watchlist.json")))!.AsArray(), "watchlist");
    var list2 = Directory.GetFiles(Path.Combine(backup, "lists"), "*.json")
        .Select(f => JsonNode.Parse(File.ReadAllText(f))!)
        .FirstOrDefault(d => d["list"]?["name"]?.GetValue<string>() == ListName)
        ?? throw new FileNotFoundException($"List \"{ListName}\" not found in {backup}\\lists.");
    AddItems(items, list2["items"]!.AsArray(), "Watch list 2");
    Console.WriteLine($"  Trakt inventory: {items.Values.Count(i => i.IsMovie)} movies, {items.Values.Count(i => !i.IsMovie)} shows (after dedupe)");

    var watchedMovies = TraktIds(Path.Combine(backup, "watched-movies.json"), "movie");
    var watchedShows = TraktIds(Path.Combine(backup, "watched-shows.json"), "show");
    var ratedLow = new Dictionary<string, int>();
    foreach (var (file, kind) in new[] { ("ratings-movies.json", "movie"), ("ratings-shows.json", "show") })
    {
        foreach (var r in JsonNode.Parse(File.ReadAllText(Path.Combine(backup, file)))!.AsArray())
        {
            if (r!["rating"]!.GetValue<int>() < 7)
            {
                ratedLow[kind + ":" + r[kind]!["ids"]!["trakt"]] = r["rating"]!.GetValue<int>();
            }
        }
    }

    // ---- Plex: what you already own ----
    var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, PlexConfig.FileName))).RootElement;
    using var http = new HttpClient();
    var plex = new PlexClient(PlexConfig.Load(), http);
    Console.WriteLine("Reading Plex...");
    var plexMovies = PlexKeySet(await plex.GetAllAsync("/library/sections/4/all", ct));
    var plexShows = PlexKeySet(await plex.GetAllAsync("/library/sections/1/all?type=2", ct));

    var omdb = new Omdb(cfg, Path.Combine(home, "omdb-cache.json"));

    // ---- Radarr and Sonarr ----
    using var radarr = Arr(cfg, "radarr");
    using var sonarr = Arr(cfg, "sonarr");
    var movieProfile = await ProfileAsync(radarr, RadarrProfileName, ct);
    var showProfile = await ProfileAsync(sonarr, SonarrProfileName, ct);
    var movieRoot = await RootAsync(radarr, ct);
    var showRoot = await RootAsync(sonarr, ct);
    Console.WriteLine($"  Radarr: profile \"{RadarrProfileName}\" = {movieProfile}, root {movieRoot}");
    Console.WriteLine($"  Sonarr: profile \"{SonarrProfileName}\" = {showProfile}, root {showRoot}");

    var radarrHave = new HashSet<string>();
    foreach (var m in JsonNode.Parse(await radarr.GetStringAsync("api/v3/movie", ct))!.AsArray())
    {
        radarrHave.Add("tmdb:" + m!["tmdbId"]);
        if (m["imdbId"] != null)
        {
            radarrHave.Add("imdb:" + m["imdbId"]);
        }
    }

    var sonarrHave = new HashSet<string>();
    foreach (var s in JsonNode.Parse(await sonarr.GetStringAsync("api/v3/series", ct))!.AsArray())
    {
        sonarrHave.Add("tvdb:" + s!["tvdbId"]);
        if (s["imdbId"] != null)
        {
            sonarrHave.Add("imdb:" + s["imdbId"]);
        }
    }

    // ---- Decide ----
    var plan = new List<Row>();
    foreach (var item in items.Values.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
    {
        var kind = item.IsMovie ? "movie" : "show";
        var row = new Row(item);
        plan.Add(row);

        if (ratedLow.TryGetValue(kind + ":" + item.Trakt, out var rating))
        {
            row.Action = $"skip: rated {rating}";
            continue;
        }

        if (item.IsMovie && watchedMovies.Contains(item.Trakt))
        {
            row.Action = "skip: watched";
            continue;
        }

        if (!item.IsMovie && watchedShows.Contains(item.Trakt))
        {
            row.PartlyWatched = true;
        }

        if (item.Keys.Any((item.IsMovie ? plexMovies : plexShows).Contains))
        {
            row.Action = "skip: in Plex";
            continue;
        }

        if (item.IsMovie ? item.Tmdb == null : item.Tvdb == null)
        {
            row.Action = item.IsMovie ? "exception: no tmdb id" : "exception: no tvdb id";
            continue;
        }

        if (item.Keys.Any((item.IsMovie ? radarrHave : sonarrHave).Contains))
        {
            row.Action = item.IsMovie ? "already in Radarr" : "already in Sonarr";
            continue;
        }

        row.Action = "add";
        if (item.Year >= CutoffYear)
        {
            row.Monitor = true;
            row.Why = $"released {item.Year}";
            continue;
        }

        // Older than the cutoff: monitor only if Rotten Tomatoes (from OMDb, by IMDb id) says 80% or more.
        row.RottenTomatoes = item.Imdb != null ? await omdb.RottenTomatoesAsync(item.Imdb, ct) : null;

        if (row.RottenTomatoes >= 80)
        {
            row.Monitor = true;
            row.Why = $"Rotten Tomatoes {row.RottenTomatoes:0}%";
        }
        else
        {
            row.Why = row.RottenTomatoes != null ? $"Rotten Tomatoes {row.RottenTomatoes:0}% (under 80)" : $"released {item.Year}, no Rotten Tomatoes score";
        }
    }

    omdb.Save();
    Console.WriteLine($"  OMDb: {omdb.Calls} lookups this run, {omdb.CacheHits} answered from omdb-cache.json{(omdb.LimitReached ? " (DAILY LIMIT REACHED: re-run tomorrow to fill the rest)" : "")}");

    var dir = Path.Combine(home, "Reports", "migrate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "migrate-plan.csv");
    WriteCsv(path, ["Action", "Type", "Title", "Year", "Monitor", "SearchOnAdd", "Why", "RottenTomatoes", "PartlyWatchedOnTrakt", "Source", "TmdbId", "TvdbId", "ImdbId", "TraktId"],
        plan.OrderBy(r => r.Action == "add" ? 0 : 1).ThenBy(r => r.Action, StringComparer.Ordinal).ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase).Select(r => new[]
        {
            r.Action, r.Item.IsMovie ? "movie" : "show", r.Item.Title, r.Item.Year.ToString(CultureInfo.InvariantCulture),
            r.Action == "add" ? (r.Monitor ? "yes" : "no") : "", r.Action == "add" ? (r.Monitor ? "yes" : "no") : "", r.Why,
            r.RottenTomatoes?.ToString("0", CultureInfo.InvariantCulture) ?? "", r.PartlyWatched ? "yes" : "", string.Join(" + ", r.Item.Sources),
            r.Item.Tmdb ?? "", r.Item.Tvdb ?? "", r.Item.Imdb ?? "", r.Item.Trakt,
        }).ToList());
    File.WriteAllText(Path.Combine(home, "Reports", "latest-migrate.txt"), path);

    Console.WriteLine();
    foreach (var kind in new[] { true, false })
    {
        var rows = plan.Where(r => r.Item.IsMovie == kind).ToList();
        var adds = rows.Where(r => r.Action == "add").ToList();
        Console.WriteLine(kind ? "=== Movies ===" : "=== Shows ===");
        Console.WriteLine($"  In the Trakt lists:             {rows.Count}");
        foreach (var g in rows.Where(r => r.Action != "add").GroupBy(r => r.Action).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {g.Key,-28} {g.Count()}");
        }

        Console.WriteLine($"  To add:                         {adds.Count}");
        Console.WriteLine($"    monitored + search on add:    {adds.Count(r => r.Monitor)}");
        Console.WriteLine($"    added unmonitored, no search: {adds.Count(r => !r.Monitor)}");
        Console.WriteLine($"    ...of those with no RT score: {adds.Count(r => !r.Monitor && r.Item.Year < CutoffYear && r.RottenTomatoes == null)}");
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {path}");
    Console.WriteLine("Nothing was added or removed anywhere.");
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

static void AddItems(Dictionary<string, Item> items, JsonArray source, string sourceName)
{
    foreach (var entry in source)
    {
        var type = entry!["type"]!.GetValue<string>();
        if (type is not ("movie" or "show"))
        {
            continue;
        }

        var node = entry[type]!;
        var ids = node["ids"]!;
        var key = type + ":" + ids["trakt"];
        if (!items.TryGetValue(key, out var item))
        {
            items[key] = item = new Item
            {
                IsMovie = type == "movie",
                Title = node["title"]?.GetValue<string>() ?? "?",
                Year = node["year"]?.GetValue<int>() ?? 0,
                Trakt = ids["trakt"]!.ToString(),
                Tmdb = ids["tmdb"]?.ToString(),
                Tvdb = ids["tvdb"]?.ToString(),
                Imdb = ids["imdb"]?.ToString(),
            };
        }

        item.Sources.Add(sourceName);
    }
}

static HashSet<string> TraktIds(string file, string kind) =>
    JsonNode.Parse(File.ReadAllText(file))!.AsArray().Select(e => e![kind]!["ids"]!["trakt"]!.ToString()).ToHashSet();

static HashSet<string> PlexKeySet(List<JsonNode> plexItems)
{
    var set = new HashSet<string>();
    foreach (var p in plexItems)
    {
        var guids = p["Guid"]?.AsArray().Select(g => g?["id"]?.GetValue<string>()).ToList() ?? [];
        guids.Add(p["guid"]?.GetValue<string>());
        foreach (var g in guids)
        {
            foreach (var scheme in new[] { "imdb", "tmdb", "tvdb" })
            {
                if (g != null && g.StartsWith(scheme + "://", StringComparison.Ordinal))
                {
                    set.Add($"{scheme}:{g[(scheme.Length + 3)..]}");
                }
            }
        }
    }

    return set;
}

static HttpClient Arr(JsonElement cfg, string name)
{
    var section = cfg.GetProperty(name);
    var key = section.GetProperty("apiKey").GetString();
    if (string.IsNullOrWhiteSpace(key))
    {
        throw new InvalidOperationException($"{name}.apiKey is empty in {PlexConfig.FileName}.");
    }

    var client = new HttpClient { BaseAddress = new Uri(section.GetProperty("baseUrl").GetString()!.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
    client.DefaultRequestHeaders.Add("X-Api-Key", key);
    return client;
}

static async Task<int> ProfileAsync(HttpClient arr, string profileName, CancellationToken ct)
{
    var profiles = JsonNode.Parse(await arr.GetStringAsync("api/v3/qualityprofile", ct))!.AsArray();
    var hit = profiles.FirstOrDefault(p => string.Equals(p!["name"]!.GetValue<string>(), profileName, StringComparison.OrdinalIgnoreCase));
    return hit?["id"]?.GetValue<int>()
        ?? throw new InvalidOperationException($"Quality profile \"{profileName}\" not found on {arr.BaseAddress}. Profiles there: {string.Join(", ", profiles.Select(p => p!["name"]))}.");
}

static async Task<string> RootAsync(HttpClient arr, CancellationToken ct) =>
    JsonNode.Parse(await arr.GetStringAsync("api/v3/rootfolder", ct))!.AsArray().FirstOrDefault()?["path"]?.GetValue<string>()
        ?? throw new InvalidOperationException($"No root folder configured on {arr.BaseAddress}.");

static string LatestBackup(string home)
{
    var root = Path.Combine(home, "TraktBackup", "Backups");
    // smart-lists.json is the last file a backup writes, so a folder without it is unfinished or failed.
    return Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "smart-lists.json"))).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
        ?? throw new DirectoryNotFoundException($"No finished Trakt backups in {root}. Run TraktBackup first.");
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

/// <summary>Rotten Tomatoes percentage by IMDb id via omdbapi.com, cached on disk (a null result is cached too, so a title with no score is not asked about twice).</summary>
sealed class Omdb
{
    private readonly string _key;
    private readonly string _cachePath;
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, double?> _cache = new();

    public Omdb(JsonElement cfg, string cachePath)
    {
        _key = cfg.TryGetProperty("omdb", out var o) ? o.GetProperty("apiKey").GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(_key))
        {
            throw new InvalidOperationException("omdb.apiKey is missing in " + PlexConfig.FileName + ".");
        }

        _cachePath = cachePath;
        if (File.Exists(cachePath))
        {
            foreach (var kv in JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject())
            {
                _cache[kv.Key] = kv.Value?.GetValue<double>();
            }
        }
    }

    public int Calls { get; private set; }

    public int CacheHits { get; private set; }

    public bool LimitReached { get; private set; }

    public async Task<double?> RottenTomatoesAsync(string imdb, CancellationToken ct)
    {
        if (_cache.TryGetValue(imdb, out var cached))
        {
            CacheHits++;
            return cached;
        }

        if (LimitReached)
        {
            return null;
        }

        Calls++;
        var body = JsonNode.Parse(await _http.GetStringAsync($"https://www.omdbapi.com/?apikey={_key}&i={Uri.EscapeDataString(imdb)}", ct))!;
        if (body["Response"]?.GetValue<string>() != "True")
        {
            if ((body["Error"]?.GetValue<string>() ?? "").Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                LimitReached = true;
                Calls--;
                return null;
            }

            _cache[imdb] = null;
            return null;
        }

        var value = body["Ratings"]?.AsArray().FirstOrDefault(r => r?["Source"]?.GetValue<string>() == "Rotten Tomatoes")?["Value"]?.GetValue<string>();
        double? score = value != null && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        _cache[imdb] = score;
        return score;
    }

    public void Save()
    {
        var obj = new JsonObject();
        foreach (var kv in _cache.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = kv.Value;
        }

        File.WriteAllText(_cachePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed class Item
{
    public bool IsMovie { get; init; }
    public string Title { get; init; } = "";
    public int Year { get; init; }
    public string Trakt { get; init; } = "";
    public string? Tmdb { get; init; }
    public string? Tvdb { get; init; }
    public string? Imdb { get; init; }
    public SortedSet<string> Sources { get; } = [];

    public IEnumerable<string> Keys
    {
        get
        {
            if (Tmdb != null)
            {
                yield return "tmdb:" + Tmdb;
            }

            if (Tvdb != null)
            {
                yield return "tvdb:" + Tvdb;
            }

            if (Imdb != null)
            {
                yield return "imdb:" + Imdb;
            }
        }
    }
}

sealed class Row(Item item)
{
    public Item Item { get; } = item;
    public string Action { get; set; } = "";
    public bool Monitor { get; set; }
    public string Why { get; set; } = "";
    public double? RottenTomatoes { get; set; }
    public bool PartlyWatched { get; set; }
}
