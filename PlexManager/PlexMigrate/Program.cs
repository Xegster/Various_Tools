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
var current = "";

try
{
    // Phase 3: works out what to move from Trakt (watchlist + "Watch list 2") into Radarr and Sonarr.
    // Usage: PlexMigrate plan [trakt-backup-folder]   read-only; writes Reports\migrate-<timestamp>\migrate-plan.csv
    // Usage: PlexMigrate apply <migrate-plan.csv|latest> [--apply]   adds every "add" row to Radarr/Sonarr; dry run unless --apply. Never touches Trakt.
    if (args.Length > 0 && args[0] == "apply")
    {
        return await ApplyAsync(args, cts.Token);
    }

    // Usage: PlexMigrate verify <migrate-plan.csv|latest>   read-only; re-reads Radarr/Sonarr and checks each "add" and "already in" row is really there.
    if (args.Length > 0 && args[0] == "verify")
    {
        return await VerifyAsync(args, cts.Token);
    }

    if (args.Length == 0 || args[0] != "plan")
    {
        Console.Error.WriteLine("       PlexMigrate verify <migrate-plan.csv|latest>");
        Console.Error.WriteLine("Usage: PlexMigrate plan [trakt-backup-folder]");
        Console.Error.WriteLine("       PlexMigrate apply <migrate-plan.csv|latest> [--apply]");
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
    var languages = new ShowLanguages(Path.Combine(home, "trakt-language-cache.json"), Path.Combine(home, "TraktBackup", "trakt.config.json"));
    var streaming = Streaming.TryCreate(cfg, Path.Combine(home, "streaming-cache.json"));
    Console.WriteLine(streaming == null
        ? "  Streaming check skipped: no tmdb.apiKey in " + PlexConfig.FileName + "."
        : $"  Streaming check on (region {streaming.Region}): titles on Netflix or Prime Video are skipped.");

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
        current = $"{item.Title} ({item.Year}) [{kind}]";
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

        // Series not originally in English are dropped from the process and removed from the Trakt lists (TraktBackup remove-lists).
        if (!item.IsMovie)
        {
            var language = await languages.OriginalAsync(item, ct);
            if (language != null && language != "en")
            {
                row.Action = $"remove: original language {language}";
                continue;
            }
        }

        if (streaming != null)
        {
            row.Streaming = await streaming.FlatrateAsync(item, ct);
            var blocker = row.Streaming.FirstOrDefault(n => n.Contains("Netflix", StringComparison.OrdinalIgnoreCase));
            blocker ??= row.Streaming.FirstOrDefault(n => n.Contains("Prime Video", StringComparison.OrdinalIgnoreCase));
            if (blocker != null)
            {
                row.Action = $"skip: on {(blocker.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ? "Netflix" : "Prime Video")}";
                continue;
            }
        }

        if (item.Year >= CutoffYear)
        {
            row.Monitor = true;
            row.Why = $"released {item.Year}";
            continue;
        }

        // Older than the cutoff: monitor only if Rotten Tomatoes (from OMDb, by IMDb id) says 80% or more.
        // Series are judged on release year only: OMDb has no Rotten Tomatoes score for TV.
        row.RottenTomatoes = item.IsMovie && item.Imdb != null ? await omdb.RottenTomatoesAsync(item.Imdb, ct) : null;

        if (row.RottenTomatoes >= 80)
        {
            row.Monitor = true;
            row.Why = $"Rotten Tomatoes {row.RottenTomatoes:0}%";
        }
        else
        {
            row.Why = row.RottenTomatoes != null ? $"Rotten Tomatoes {row.RottenTomatoes:0}% (under 80)" : (item.IsMovie ? $"released {item.Year}, no Rotten Tomatoes score" : $"released {item.Year} (series: release year only)");
        }
    }

    omdb.Save();
    languages.Save();
    streaming?.Save();
    Console.WriteLine($"  OMDb: {omdb.Calls} lookups this run, {omdb.CacheHits} answered from omdb-cache.json{(omdb.LimitReached ? " (DAILY LIMIT REACHED: re-run tomorrow to fill the rest)" : "")}");

    var dir = Path.Combine(home, "Reports", "migrate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "migrate-plan.csv");
    WriteCsv(path, ["Action", "Type", "Title", "Year", "Monitor", "SearchOnAdd", "Why", "RottenTomatoes", "PartlyWatchedOnTrakt", "Streaming", "Source", "TmdbId", "TvdbId", "IMDb", "TraktId"],
        plan.OrderBy(r => r.Action == "add" ? 0 : 1).ThenBy(r => r.Action, StringComparer.Ordinal).ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase).Select(r => new[]
        {
            r.Action, r.Item.IsMovie ? "movie" : "show", r.Item.Title, r.Item.Year.ToString(CultureInfo.InvariantCulture),
            r.Action == "add" ? (r.Monitor ? "yes" : "no") : "", r.Action == "add" ? (r.Monitor ? "yes" : "no") : "", r.Why,
            r.RottenTomatoes?.ToString("0", CultureInfo.InvariantCulture) ?? "", r.PartlyWatched ? "yes" : "", string.Join(", ", r.Streaming), string.Join(" + ", r.Item.Sources),
            r.Item.Tmdb ?? "", r.Item.Tvdb ?? "", r.Item.Imdb != null ? $"https://www.imdb.com/title/{r.Item.Imdb}/" : "", r.Item.Trakt,
        }).ToList());
    WriteHtml(Path.ChangeExtension(path, ".html"), plan);
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
    Console.WriteLine($"Wrote {Path.ChangeExtension(path, ".html")} (every series A to Z with its IMDb link)");
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
    Console.Error.WriteLine(current.Length > 0 ? $"{ex.Message} (while processing {current})" : ex.Message);
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

static async Task<int> ApplyAsync(string[] args, CancellationToken ct)
{
    var apply = args.Contains("--apply");
    var home = PlexConfig.HomeDirectory();
    var arg = args.Length > 1 && args[1] != "--apply" ? args[1] : "latest";
    var planPath = arg == "latest" ? File.ReadAllText(Path.Combine(home, "Reports", "latest-migrate.txt")).Trim() : arg;
    Console.WriteLine($"Plan: {planPath}");

    var lines = File.ReadAllLines(planPath, Encoding.UTF8);
    var header = SplitCsv(lines[0].TrimStart('﻿'));
    var rows = lines.Skip(1).Where(l => l.Length > 0).Select(SplitCsv)
        .Select(c => header.Select((h, i) => (h, v: i < c.Count ? c[i] : "")).ToDictionary(x => x.h, x => x.v)).ToList();
    var adds = rows.Where(r => r["Action"] == "add").ToList();
    Console.WriteLine($"  {adds.Count(r => r["Type"] == "movie")} movies and {adds.Count(r => r["Type"] == "show")} series to add.");

    var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, PlexConfig.FileName))).RootElement;
    using var radarr = Arr(cfg, "radarr");
    using var sonarr = Arr(cfg, "sonarr");
    var movieProfile = await ProfileAsync(radarr, RadarrProfileName, ct);
    var showProfile = await ProfileAsync(sonarr, SonarrProfileName, ct);
    var movieRoot = await RootAsync(radarr, ct);
    var showRoot = await RootAsync(sonarr, ct);
    int? languageProfile = null;
    using (var lp = await sonarr.GetAsync("api/v3/languageprofile", ct))
    {
        if (lp.IsSuccessStatusCode)
        {
            languageProfile = JsonNode.Parse(await lp.Content.ReadAsStringAsync(ct))!.AsArray().FirstOrDefault()?["id"]?.GetValue<int>();
        }
    }

    Console.WriteLine($"  Radarr: \"{RadarrProfileName}\" ({movieProfile}), root {movieRoot}");
    Console.WriteLine($"  Sonarr: \"{SonarrProfileName}\" ({showProfile}), root {showRoot}, language profile {languageProfile?.ToString() ?? "none"}");
    if (!apply)
    {
        Console.WriteLine("Dry run: nothing will be added (lookups only). Re-run with --apply to add.");
    }

    var log = new List<string[]>();
    int added = 0, existing = 0, failed = 0;
    foreach (var r in adds)
    {
        var isMovie = r["Type"] == "movie";
        var label = $"{r["Title"]} ({r["Year"]})";
        var monitor = r["Monitor"] == "yes";
        var search = r["SearchOnAdd"] == "yes";
        string result;
        try
        {
            var client = isMovie ? radarr : sonarr;
            var term = isMovie ? "tmdb:" + r["TmdbId"] : "tvdb:" + r["TvdbId"];
            var lookupUrl = (isMovie ? "api/v3/movie/lookup?term=" : "api/v3/series/lookup?term=") + Uri.EscapeDataString(term);
            var found = JsonNode.Parse(await client.GetStringAsync(lookupUrl, ct))!.AsArray().FirstOrDefault();
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
                    found["addOptions"] = new JsonObject { ["monitor"] = monitor ? "all" : "none", ["searchForMissingEpisodes"] = search, ["searchForCutoffUnmetEpisodes"] = false };
                }

                using var resp = await client.PostAsync(isMovie ? "api/v3/movie" : "api/v3/series", new StringContent(found.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    result = "added";
                    added++;
                }
                else if (body.Contains("already been added", StringComparison.OrdinalIgnoreCase) || body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    result = "already present";
                    existing++;
                }
                else
                {
                    result = $"failed: {(int)resp.StatusCode} {(body.Length > 200 ? body[..200] : body).Replace('\n', ' ').Replace('\r', ' ')}";
                    failed++;
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

        Console.WriteLine($"  [{r["Type"]}] {label}: {result}");
        log.Add([r["Type"], r["Title"], r["Year"], r["TmdbId"], r["TvdbId"], r["TraktId"], monitor ? "yes" : "no", search ? "yes" : "no", result]);
    }

    var logPath = Path.Combine(home, "Reports", (apply ? "migrate-apply-" : "migrate-apply-dryrun-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
    WriteCsv(logPath, ["Type", "Title", "Year", "TmdbId", "TvdbId", "TraktId", "Monitored", "Search", "Result"], log);
    Console.WriteLine();
    Console.WriteLine(apply ? $"Added {added}, already present {existing}, failed {failed}." : $"Dry run: {adds.Count - failed} found, {failed} failed lookup.");
    Console.WriteLine($"Log: {logPath}");
    Console.WriteLine("Trakt was not changed. Removing the added titles from Trakt is a separate step.");
    return 0;
}

/// <summary>Step 3d: re-reads Radarr and Sonarr and confirms every row that should be there is. Writes verified (safe to remove from Trakt) and missing (exceptions) lists. Read-only.</summary>
static async Task<int> VerifyAsync(string[] args, CancellationToken ct)
{
    var home = PlexConfig.HomeDirectory();
    var arg = args.Length > 1 ? args[1] : "latest";
    var planPath = arg == "latest" ? File.ReadAllText(Path.Combine(home, "Reports", "latest-migrate.txt")).Trim() : arg;
    Console.WriteLine($"Plan: {planPath}");

    var lines = File.ReadAllLines(planPath, Encoding.UTF8);
    var header = SplitCsv(lines[0].TrimStart('﻿'));
    var rows = lines.Skip(1).Where(l => l.Length > 0).Select(SplitCsv)
        .Select(c => header.Select((h, i) => (h, v: i < c.Count ? c[i] : "")).ToDictionary(x => x.h, x => x.v)).ToList();

    var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, PlexConfig.FileName))).RootElement;
    using var radarr = Arr(cfg, "radarr");
    using var sonarr = Arr(cfg, "sonarr");

    var radarrHave = new HashSet<string>();
    foreach (var m in JsonNode.Parse(await radarr.GetStringAsync("api/v3/movie", ct))!.AsArray())
    {
        radarrHave.Add("tmdb:" + m!["tmdbId"]);
    }

    var sonarrHave = new HashSet<string>();
    foreach (var s in JsonNode.Parse(await sonarr.GetStringAsync("api/v3/series", ct))!.AsArray())
    {
        sonarrHave.Add("tvdb:" + s!["tvdbId"]);
    }

    Console.WriteLine($"  Radarr has {radarrHave.Count} movies, Sonarr has {sonarrHave.Count} series.");

    var verified = new List<string[]>();
    var missing = new List<string[]>();
    foreach (var r in rows.Where(r => r["Action"] is "add" or "already in Radarr" or "already in Sonarr"))
    {
        var isMovie = r["Type"] == "movie";
        var present = isMovie ? radarrHave.Contains("tmdb:" + r["TmdbId"]) : sonarrHave.Contains("tvdb:" + r["TvdbId"]);
        var line = new[] { r["Type"], r["Title"], r["Year"], r["TmdbId"], r["TvdbId"], r["TraktId"], r["Action"] };
        (present ? verified : missing).Add(line);
    }

    string[] cols = ["Type", "Title", "Year", "TmdbId", "TvdbId", "TraktId", "PlanAction"];
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var verifiedPath = Path.Combine(home, "Reports", $"migrate-verified-{stamp}.csv");
    var missingPath = Path.Combine(home, "Reports", $"migrate-missing-{stamp}.csv");
    WriteCsv(verifiedPath, cols, verified);
    WriteCsv(missingPath, cols, missing);
    File.WriteAllText(Path.Combine(home, "Reports", "latest-verified.txt"), verifiedPath);

    Console.WriteLine();
    Console.WriteLine($"Verified present: {verified.Count(v => v[0] == "movie")} movies, {verified.Count(v => v[0] == "show")} series.");
    Console.WriteLine($"Missing:          {missing.Count(v => v[0] == "movie")} movies, {missing.Count(v => v[0] == "show")} series.");
    foreach (var m in missing)
    {
        Console.WriteLine($"  MISSING [{m[0]}] {m[1]} ({m[2]}) - plan said \"{m[6]}\"");
    }

    var other = rows.Where(r => r["Action"] is not ("add" or "already in Radarr" or "already in Sonarr")).GroupBy(r => r["Action"].Split(':')[0]).OrderBy(g => g.Key);
    Console.WriteLine("Not checked here (not meant for Radarr/Sonarr): " + string.Join(", ", other.Select(g => $"{g.Key} {g.Count()}")));
    Console.WriteLine($"Wrote {verifiedPath}");
    Console.WriteLine($"Wrote {missingPath}");
    Console.WriteLine("Nothing was changed anywhere.");
    return missing.Count == 0 ? 0 : 1;
}

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

    var client = new HttpClient { BaseAddress = new Uri(PlexConfig.ServiceUrl(cfg, name) + "/"), Timeout = TimeSpan.FromMinutes(2) };
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

/// <summary>Simple page of every series in the plan (movies left out), alphabetical, each linked to its IMDb page.</summary>
static void WriteHtml(string path, List<Row> plan)
{
    static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
    var shows = plan.Where(r => !r.Item.IsMovie).ToList();
    var sb = new StringBuilder();
    sb.AppendLine("<!doctype html>");
    sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Migrate plan</title>");
    sb.AppendLine("<style>body{font-family:sans-serif;margin:1.5em}table{border-collapse:collapse}td,th{padding:.25em .8em;border-bottom:1px solid #ddd;text-align:left}</style></head><body>");
    sb.AppendLine($"<h1>Migrate plan</h1><p>{shows.Count} series, A to Z. Click a title to open it on IMDb.</p>");
    sb.AppendLine("<table><tr><th>Title</th><th>Year</th><th>Action</th></tr>");
    foreach (var r in shows.OrderBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase))
    {
        var title = r.Item.Imdb != null ? $"<a href=\"https://www.imdb.com/title/{H(r.Item.Imdb)}/\" target=\"_blank\">{H(r.Item.Title)}</a>" : H(r.Item.Title);
        sb.AppendLine($"<tr><td>{title}</td><td>{r.Item.Year}</td><td>{H(r.Action)}</td></tr>");
    }

    sb.AppendLine("</table></body></html>");
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
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
            Save();
            return null;
        }

        var value = body["Ratings"]?.AsArray().FirstOrDefault(r => r?["Source"]?.GetValue<string>() == "Rotten Tomatoes")?["Value"]?.GetValue<string>();
        double? score = value != null && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        _cache[imdb] = score;
        Save();
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
    public List<string> Streaming { get; set; } = [];
}

/// <summary>Subscription ("flatrate") streaming services for a title in one region, from TMDB's watch-provider data (JustWatch). Cached on disk.</summary>
sealed class Streaming
{
    private readonly string _key;
    private readonly string _cachePath;
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, List<string>> _cache = new();

    private Streaming(string key, string region, string cachePath)
    {
        _key = key;
        Region = region;
        _cachePath = cachePath;
        if (File.Exists(cachePath))
        {
            foreach (var kv in JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject())
            {
                _cache[kv.Key] = kv.Value!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            }
        }
    }

    public string Region { get; }

    public static Streaming? TryCreate(JsonElement cfg, string cachePath)
    {
        if (!cfg.TryGetProperty("tmdb", out var t) || string.IsNullOrWhiteSpace(t.GetProperty("apiKey").GetString()))
        {
            return null;
        }

        var region = t.TryGetProperty("region", out var r) ? r.GetString() ?? "US" : "US";
        return new Streaming(t.GetProperty("apiKey").GetString()!, region, cachePath);
    }

    public async Task<List<string>> FlatrateAsync(Item item, CancellationToken ct)
    {
        var key = (item.IsMovie ? "movie:" : "show:") + (item.IsMovie ? item.Tmdb : item.Imdb ?? item.Tvdb);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var names = new List<string>();
        var id = item.IsMovie ? item.Tmdb : await FindTvIdAsync(item, ct);
        if (id != null)
        {
            var kind = item.IsMovie ? "movie" : "tv";
            var body = await GetJsonOrNullAsync($"https://api.themoviedb.org/3/{kind}/{id}/watch/providers?api_key={_key}", ct);
            foreach (var p in body?["results"]?[Region]?["flatrate"]?.AsArray() ?? [])
            {
                names.Add(p!["provider_name"]!.GetValue<string>());
            }
        }

        _cache[key] = names;
        Save();
        return names;
    }

    /// <summary>GET and parse JSON; a 404 (TMDB does not know that id) gives null so the title is treated as not streaming.</summary>
    private async Task<JsonNode?> GetJsonOrNullAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"  TMDB has no record at {url[..url.IndexOf('?')]} (treated as not streaming)");
            return null;
        }

        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task<string?> FindTvIdAsync(Item item, CancellationToken ct)
    {
        var (source, id) = item.Tvdb != null ? ("tvdb_id", item.Tvdb) : ("imdb_id", item.Imdb);
        if (id == null)
        {
            return null;
        }

        var body = await GetJsonOrNullAsync($"https://api.themoviedb.org/3/find/{id}?external_source={source}&api_key={_key}", ct);
        return body?["tv_results"]?.AsArray().FirstOrDefault()?["id"]?.ToString();
    }

    public void Save()
    {
        var obj = new JsonObject();
        foreach (var kv in _cache.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = new JsonArray(kv.Value.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        }

        File.WriteAllText(_cachePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Original language of a show from Trakt (public lookup with the client id in trakt.config.json), cached on disk.</summary>
sealed class ShowLanguages
{
    private readonly string _cachePath;
    private readonly string _clientId;
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, string?> _cache = new();

    public ShowLanguages(string cachePath, string traktConfigPath)
    {
        _cachePath = cachePath;
        _clientId = JsonNode.Parse(File.ReadAllText(traktConfigPath))!["ClientId"]!.GetValue<string>();
        if (File.Exists(cachePath))
        {
            foreach (var kv in JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject())
            {
                _cache[kv.Key] = kv.Value?["language"]?.GetValue<string>();
            }
        }
    }

    public async Task<string?> OriginalAsync(Item show, CancellationToken ct)
    {
        if (_cache.TryGetValue(show.Trakt, out var cached))
        {
            return cached;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/shows/{show.Trakt}?extended=full");
        req.Headers.Add("trakt-api-key", _clientId);
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.UserAgent.ParseAdd("PlexManager/1.0");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        _full[show.Trakt] = new JsonObject { ["title"] = show.Title, ["year"] = show.Year, ["language"] = body["language"]?.DeepClone(), ["country"] = body["country"]?.DeepClone() };
        var language = body["language"]?.GetValue<string>();
        _cache[show.Trakt] = language;
        Save();
        await Task.Delay(300, ct);
        return language;
    }

    private readonly Dictionary<string, JsonObject> _full = new();

    /// <summary>Writes the cache, keeping existing entries as they were and adding the ones fetched this run.</summary>
    public void Save()
    {
        var obj = File.Exists(_cachePath) ? JsonNode.Parse(File.ReadAllText(_cachePath))!.AsObject() : new JsonObject();
        foreach (var kv in _full)
        {
            obj[kv.Key] = kv.Value;
        }

        File.WriteAllText(_cachePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
