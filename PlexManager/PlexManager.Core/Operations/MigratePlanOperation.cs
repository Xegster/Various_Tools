using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PlexManager.Core.Settings;
using PlexManager.Core.Util;

namespace PlexManager.Core.Operations;

/// <summary>
/// Works out what to move from the chosen Trakt lists into Radarr and Sonarr, and what to drop instead. Read-only:
/// it writes a plan for review. Every rule it applies (rating threshold, release-year cutoff, Rotten Tomatoes
/// cutoff, which languages to keep, which streaming services count) comes from Settings.
/// </summary>
public sealed class MigratePlanOperation
{
    private static readonly string[] Columns =
    [
        "Action", "Type", "Title", "Year", "Monitor", "SearchOnAdd", "Why", "RottenTomatoes",
        "PartlyWatchedOnTrakt", "Streaming", "Source", "TmdbId", "TvdbId", "IMDb", "TraktId",
    ];

    private readonly OperationContext _context;

    public MigratePlanOperation(OperationContext context)
    {
        _context = context;
    }

    public async Task<RunRecord> RunAsync(string? backupFolder, CancellationToken ct)
    {
        var log = _context.Log;
        var started = DateTimeOffset.Now;
        var rules = _context.Values.Migration;
        var backup = backupFolder ?? _context.LatestBackup();
        log.Info($"Trakt data: {backup}");

        // ---- Trakt inventory: every chosen list, deduped by Trakt id ----
        var items = new Dictionary<string, PlanItem>(StringComparer.Ordinal);
        foreach (var source in rules.SourceLists)
        {
            var (name, array) = ReadListFromBackup(backup, source);
            AddItems(items, array, name);
            log.Detail($"{name}: {array.Count} items");
        }

        log.Detail($"Trakt inventory: {items.Values.Count(i => i.IsMovie)} movies, "
                   + $"{items.Values.Count(i => !i.IsMovie)} shows (after dedupe)");

        var watchedMovies = TraktIds(Path.Combine(backup, "watched-movies.json"), "movie");
        var watchedShows = TraktIds(Path.Combine(backup, "watched-shows.json"), "show");
        var ratedLow = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, kind) in new[] { ("ratings-movies.json", "movie"), ("ratings-shows.json", "show") })
        {
            foreach (var rating in JsonNode.Parse(File.ReadAllText(Path.Combine(backup, file)))!.AsArray())
            {
                var value = rating!["rating"]!.GetValue<int>();
                if (value < rules.RatingThreshold)
                {
                    ratedLow[kind + ":" + rating[kind]!["ids"]!["trakt"]] = value;
                }
            }
        }

        // ---- Plex: what is already owned ----
        log.Info("Reading Plex...");
        var plexMovies = PlexKeySet(await _context.Plex.GetAllAsync(_context.Plex.MoviesPath, ct));
        var plexShows = PlexKeySet(await _context.Plex.GetAllAsync(_context.Plex.ShowsPath, ct));

        var omdb = _context.Omdb;
        var tmdb = _context.Tmdb;
        var languages = _context.Languages;
        var providers = rules.StreamingTargets.Where(t => t.Enabled && t.Provider.Length > 0).ToList();
        log.Detail(!tmdb.IsConfigured || providers.Count == 0
            ? "Streaming check skipped: no TMDB API key, or no streaming services selected."
            : $"Streaming check on (region {tmdb.Region}): titles on {string.Join(" or ", providers.Select(p => p.Provider))} are skipped.");
        if (!omdb.IsConfigured)
        {
            log.Detail("Rotten Tomatoes check skipped: no OMDb API key. Older movies will be added unmonitored.");
        }

        // ---- Radarr and Sonarr ----
        var radarr = _context.Radarr;
        var sonarr = _context.Sonarr;
        var movieProfile = await ProfileIdAsync(radarr, _context.Values.Radarr, ct);
        var showProfile = await ProfileIdAsync(sonarr, _context.Values.Sonarr, ct);
        var movieRoot = await RootFolderAsync(radarr, _context.Values.Radarr, ct);
        var showRoot = await RootFolderAsync(sonarr, _context.Values.Sonarr, ct);
        log.Detail($"Radarr: profile {movieProfile}, root {movieRoot}");
        log.Detail($"Sonarr: profile {showProfile}, root {showRoot}");

        var radarrHave = await radarr.LibraryKeysAsync(ct);
        var sonarrHave = await sonarr.LibraryKeysAsync(ct);

        // ---- Decide ----
        var plan = new List<PlanRow>();
        var current = "";
        try
        {
            foreach (var item in items.Values.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var kind = item.IsMovie ? "movie" : "show";
                current = $"{item.Title} ({item.Year}) [{kind}]";
                var row = new PlanRow(item);
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

                // Series not originally in a kept language leave the process and are removed from the Trakt lists.
                if (!item.IsMovie && rules.KeepOriginalLanguages.Count > 0)
                {
                    var language = await languages.OriginalAsync(item.Trakt, item.Title, item.Year, ct);
                    if (language != null && !rules.KeepOriginalLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
                    {
                        row.Action = $"remove: original language {language}";
                        continue;
                    }
                }

                if (tmdb.IsConfigured && providers.Count > 0)
                {
                    row.Streaming = await tmdb.FlatrateAsync(item.IsMovie, item.Tmdb, item.Tvdb, item.Imdb, ct);
                    var blocker = providers.FirstOrDefault(p =>
                        row.Streaming.Any(n => n.Contains(p.Provider, StringComparison.OrdinalIgnoreCase)));
                    if (blocker != null)
                    {
                        row.Action = $"skip: on {blocker.Provider}";
                        continue;
                    }
                }

                if (item.Year >= rules.ReleaseYearCutoff)
                {
                    row.Monitor = true;
                    row.Why = $"released {item.Year}";
                    continue;
                }

                // Older than the cutoff: monitor only when Rotten Tomatoes (from OMDb, by IMDb id) is high enough.
                // Series are judged on release year alone: OMDb has no Rotten Tomatoes score for TV.
                row.RottenTomatoes = item.IsMovie && item.Imdb != null ? await omdb.RottenTomatoesAsync(item.Imdb, ct) : null;

                if (row.RottenTomatoes >= rules.RottenTomatoesCutoff)
                {
                    row.Monitor = true;
                    row.Why = $"Rotten Tomatoes {row.RottenTomatoes:0}%";
                }
                else
                {
                    row.Why = row.RottenTomatoes != null
                        ? $"Rotten Tomatoes {row.RottenTomatoes:0}% (under {rules.RottenTomatoesCutoff})"
                        : item.IsMovie
                            ? $"released {item.Year}, no Rotten Tomatoes score"
                            : $"released {item.Year} (series: release year only)";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                current.Length > 0 ? $"{ex.Message} (while processing {current})" : ex.Message, ex);
        }

        omdb.Save();
        languages.Save();
        tmdb.Save();
        if (omdb.IsConfigured)
        {
            log.Detail($"OMDb: {omdb.Calls} lookups this run, {omdb.CacheHits} from the cache"
                       + (omdb.LimitReached ? " (DAILY LIMIT REACHED: run it again tomorrow to fill the rest)" : ""));
        }

        var runId = RunRecord.NewId("migrate");
        var csvPath = Path.Combine(_context.Paths.Reports, runId, "migrate-plan.csv");
        var ordered = plan
            .OrderBy(r => r.Action == "add" ? 0 : 1)
            .ThenBy(r => r.Action, StringComparer.Ordinal)
            .ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = ordered.Select(ToRow).ToList();
        Csv.WriteTable(csvPath, Columns, rows);
        var htmlPath = Path.ChangeExtension(csvPath, ".html");
        WriteHtml(htmlPath, ordered);
        _context.WritePointer("latest-migrate.txt", csvPath);

        var summary = new List<string> { $"Trakt data: {backup}" };
        log.Info("");
        foreach (var isMovie in new[] { true, false })
        {
            var kindRows = plan.Where(r => r.Item.IsMovie == isMovie).ToList();
            var adds = kindRows.Where(r => r.Action == "add").ToList();
            log.Info(isMovie ? "=== Movies ===" : "=== Shows ===");
            log.Detail($"In the Trakt lists:             {kindRows.Count}");
            foreach (var group in kindRows.Where(r => r.Action != "add").GroupBy(r => r.Action).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                log.Detail($"  {group.Key,-28} {group.Count()}");
            }

            log.Detail($"To add:                         {adds.Count}");
            log.Detail($"  monitored + search on add:    {adds.Count(r => r.Monitor)}");
            log.Detail($"  added unmonitored, no search: {adds.Count(r => !r.Monitor)}");
            summary.Add($"{(isMovie ? "Movies" : "Shows")}: {kindRows.Count} in the lists, {adds.Count} to add");
        }

        var run = _context.Runs.Save(new RunRecord
        {
            Id = runId,
            Kind = "migrate",
            Title = "Migrate to Radarr and Sonarr",
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            Columns = Columns.ToList(),
            Rows = rows,
            KeyColumn = "TraktId",

            // The Action cell is the reviewed decision: leave it, blank it to remove the title from Trakt, or set "add".
            EditableColumns = ["Action", "Monitor", "SearchOnAdd"],
            CsvPath = csvPath,
            HtmlPath = htmlPath,
            Summary = summary,
            NextStep = "migrate apply latest --apply",
        });

        log.Success($"Wrote {csvPath}");
        log.Info("Nothing was added or removed anywhere.");
        return run;
    }

    private static Dictionary<string, string> ToRow(PlanRow row) => new()
    {
        ["Action"] = row.Action,
        ["Type"] = row.Item.IsMovie ? "movie" : "show",
        ["Title"] = row.Item.Title,
        ["Year"] = row.Item.Year.ToString(CultureInfo.InvariantCulture),
        ["Monitor"] = row.Action == "add" ? (row.Monitor ? "yes" : "no") : "",
        ["SearchOnAdd"] = row.Action == "add" ? (row.Monitor ? "yes" : "no") : "",
        ["Why"] = row.Why,
        ["RottenTomatoes"] = row.RottenTomatoes?.ToString("0", CultureInfo.InvariantCulture) ?? "",
        ["PartlyWatchedOnTrakt"] = row.PartlyWatched ? "yes" : "",
        ["Streaming"] = string.Join(", ", row.Streaming),
        ["Source"] = string.Join(" + ", row.Item.Sources),
        ["TmdbId"] = row.Item.Tmdb ?? "",
        ["TvdbId"] = row.Item.Tvdb ?? "",
        ["IMDb"] = row.Item.Imdb != null ? $"https://www.imdb.com/title/{row.Item.Imdb}/" : "",
        ["TraktId"] = row.Item.Trakt,
    };

    /// <summary>Finds a chosen list inside a backup folder: the watchlist file, or a list file by id and then by name.</summary>
    private static (string Name, JsonArray Items) ReadListFromBackup(string backup, TraktListRef reference)
    {
        if (reference.IsWatchlist)
        {
            var path = Path.Combine(backup, "watchlist.json");
            return ("watchlist", JsonNode.Parse(File.ReadAllText(path))!.AsArray());
        }

        var dir = Path.Combine(backup, "lists");
        if (!Directory.Exists(dir))
        {
            throw new FileNotFoundException($"The backup {backup} has no lists folder. Run a backup again.");
        }

        var documents = Directory.GetFiles(dir, "*.json").Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList();
        var match = documents.FirstOrDefault(d => d["list"]?["ids"]?["trakt"]?.GetValue<long>() == reference.Id)
                    ?? documents.FirstOrDefault(d => string.Equals(
                        d["list"]?["name"]?.GetValue<string>(), reference.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException(
                        $"The list \"{reference}\" is not in {backup}\\lists. Run a backup again, or pick the list again in Settings.");

        return (match["list"]?["name"]?.GetValue<string>() ?? reference.ToString(), match["items"]!.AsArray());
    }

    private static void AddItems(Dictionary<string, PlanItem> items, JsonArray source, string sourceName)
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
                items[key] = item = new PlanItem
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

    /// <summary>The configured quality profile id, checked against the server so a stale id fails loudly.</summary>
    internal static async Task<int> ProfileIdAsync(Clients.ArrClient client, ArrSettings settings, CancellationToken ct)
    {
        var profiles = await client.QualityProfilesAsync(ct);
        if (settings.QualityProfileId > 0 && profiles.Any(p => p.Id == settings.QualityProfileId))
        {
            return settings.QualityProfileId;
        }

        if (settings.QualityProfileName.Length > 0)
        {
            var byName = profiles.FirstOrDefault(p => string.Equals(p.Name, settings.QualityProfileName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName.Id;
            }
        }

        throw new SettingsIncompleteException(
            $"No quality profile chosen for {client.Name}. Pick one in Settings. Profiles there: {string.Join(", ", profiles.Select(p => p.Name))}.");
    }

    /// <summary>The configured root folder, checked against the server.</summary>
    internal static async Task<string> RootFolderAsync(Clients.ArrClient client, ArrSettings settings, CancellationToken ct)
    {
        var folders = await client.RootFoldersAsync(ct);
        if (settings.RootFolder.Length > 0 && folders.Contains(settings.RootFolder, StringComparer.OrdinalIgnoreCase))
        {
            return settings.RootFolder;
        }

        return folders.Count > 0
            ? folders[0]
            : throw new SettingsIncompleteException($"No root folder configured on {client.Name} ({client.BaseUrl}).");
    }

    private static HashSet<string> TraktIds(string file, string kind) =>
        JsonNode.Parse(File.ReadAllText(file))!.AsArray()
            .Select(e => e![kind]!["ids"]!["trakt"]!.ToString())
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> PlexKeySet(List<JsonNode> plexItems)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plexItems)
        {
            set.UnionWith(MediaIds.FromPlex(item));
        }

        return set;
    }

    /// <summary>A page of every series in the plan, alphabetical, each linked to its IMDb page.</summary>
    private static void WriteHtml(string path, List<PlanRow> plan)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var shows = plan.Where(r => !r.Item.IsMovie).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Migrate plan</title>");
        sb.AppendLine("<style>body{font-family:sans-serif;margin:1.5em}table{border-collapse:collapse}"
                      + "td,th{padding:.25em .8em;border-bottom:1px solid #ddd;text-align:left}</style></head><body>");
        sb.AppendLine($"<h1>Migrate plan</h1><p>{shows.Count} series, A to Z. Click a title to open it on IMDb.</p>");
        sb.AppendLine("<table><tr><th>Title</th><th>Year</th><th>Action</th></tr>");
        foreach (var row in shows.OrderBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase))
        {
            var title = row.Item.Imdb != null
                ? $"<a href=\"https://www.imdb.com/title/{H(row.Item.Imdb)}/\" target=\"_blank\">{H(row.Item.Title)}</a>"
                : H(row.Item.Title);
            sb.AppendLine($"<tr><td>{title}</td><td>{row.Item.Year}</td><td>{H(row.Action)}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>A title from the Trakt lists, with every id it is known by.</summary>
internal sealed class PlanItem
{
    public bool IsMovie { get; init; }

    public string Title { get; init; } = "";

    public int Year { get; init; }

    public string Trakt { get; init; } = "";

    public string? Tmdb { get; init; }

    public string? Tvdb { get; init; }

    public string? Imdb { get; init; }

    public SortedSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

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

internal sealed class PlanRow(PlanItem item)
{
    public PlanItem Item { get; } = item;

    public string Action { get; set; } = "";

    public bool Monitor { get; set; }

    public string Why { get; set; } = "";

    public double? RottenTomatoes { get; set; }

    public bool PartlyWatched { get; set; }

    public List<string> Streaming { get; set; } = [];
}
