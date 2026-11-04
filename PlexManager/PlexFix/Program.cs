using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlexScan;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Fixes Plex movies that have no metadata match (no imdb/tmdb/tvdb id), in two reviewed steps.
    // Usage: PlexFix candidates              ask Plex for match candidates for each unmatched movie; writes Reports\fix-<timestamp>\plex-match-candidates.csv (read-only)
    //        PlexFix delete <file-path-fragment> [--apply]   delete the one Plex movie whose file path contains the fragment; dry run unless --apply
    //        PlexFix apply <csv> [--apply]    for rows whose Approve column is "yes", match the movie in Plex; dry run unless --apply
    // <csv> may be "latest": the newest candidates file, via Reports\latest-fix.txt.
    var config = PlexConfig.Load();
    var home = PlexConfig.HomeDirectory();
    var reportsRoot = Path.Combine(home, "Reports");
    using var http = new HttpClient();
    var plex = new PlexClient(config, http);
    var ct = cts.Token;

    if (args.Length > 0 && args[0] == "candidates")
    {
        return await Candidates(plex, reportsRoot, ct);
    }

    if (args.Length > 1 && args[0] == "apply")
    {
        return await Apply(plex, ResolveCsv(args[1], reportsRoot), args.Contains("--apply"), reportsRoot, ct);
    }

    if (args.Length > 1 && args[0] == "delete")
    {
        return await Delete(plex, args[1], args.Contains("--apply"), ct);
    }

    Console.Error.WriteLine("Usage: PlexFix candidates | PlexFix apply <csv|latest> [--apply] | PlexFix delete <file-path-fragment> [--apply]");
    return 1;
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

static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

static async Task<int> Candidates(PlexClient plex, string reportsRoot, CancellationToken ct)
{
    Console.WriteLine("Reading Plex movies...");
    var movies = await plex.GetAllAsync("/library/sections/4/all", ct);
    var unmatched = movies.Where(m => !HasExternalId(m)).ToList();
    Console.WriteLine($"  {unmatched.Count} of {movies.Count} movies have no usable id");

    var rows = new List<string[]>();
    foreach (var m in unmatched)
    {
        var key = m["ratingKey"]!.GetValue<string>();
        var title = m["title"]?.GetValue<string>() ?? "";
        var file = m["Media"]?.AsArray().FirstOrDefault()?["Part"]?.AsArray().FirstOrDefault()?["file"]?.GetValue<string>() ?? "";
        var known = Known.Expected.TryGetValue(title, out var e);
        var (searchTitle, searchYear) = known ? e : GuessTitle(title, m["year"]?.ToString(), file);
        var url = $"/library/metadata/{key}/matches?manual=1&title={Uri.EscapeDataString(searchTitle)}"
            + (searchYear != "" ? $"&year={searchYear}" : "");
        var container = await plex.GetAsync(url, ct);
        var results = container["SearchResult"]?.AsArray() ?? [];
        Console.WriteLine($"  {title}: searched \"{searchTitle}\" {searchYear}, {results.Count} candidates");

        var rank = 0;
        foreach (var r in results.Take(5))
        {
            rank++;
            var name = r!["name"]?.GetValue<string>() ?? "";
            var year = r["year"]?.ToString() ?? "";
            var score = r["score"]?.ToString() ?? "";
            // Pre-approve only an unambiguous first hit: same year and a high score. Everything else is left for you to decide.
            // Known movies are approved on an exact name and year match at any rank; others only on a clear first hit.
            var approve = known
                ? (year == e.Year && Norm(name) == Norm(e.Name) && !rows.Any(x => x[0] == key && x[10] == "yes") ? "yes" : "")
                : (rank == 1 && searchYear != "" && year == searchYear && int.TryParse(score, out var s) && s >= 90 ? "yes" : "");
            rows.Add([key, title, file, searchTitle, searchYear, rank.ToString(), name, year, r["guid"]?.GetValue<string>() ?? "", score, approve]);
        }

        if (results.Count == 0)
        {
            rows.Add([key, title, file, searchTitle, searchYear, "0", "", "", "", "", ""]);
        }
    }

    var dir = Path.Combine(reportsRoot, "fix-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "plex-match-candidates.csv");
    WriteCsv(path, ["RatingKey", "PlexTitle", "File", "SearchTitle", "SearchYear", "Rank", "CandidateName", "CandidateYear", "CandidateGuid", "Score", "Approve"], rows);
    File.WriteAllText(Path.Combine(reportsRoot, "latest-fix.txt"), path);
    Console.WriteLine();
    Console.WriteLine($"Wrote {path}");
    Console.WriteLine("Review it: set Approve to \"yes\" on exactly one row per movie you want matched (clear any you do not), then run PlexFix apply latest.");
    return 0;
}

static async Task<int> Delete(PlexClient plex, string fragment, bool apply, CancellationToken ct)
{
    var movies = await plex.GetAllAsync("/library/sections/4/all", ct);
    var hits = movies.Where(m => m["Media"]?.AsArray().Any(med => med?["Part"]?.AsArray().Any(p =>
        (p?["file"]?.GetValue<string>() ?? "").Contains(fragment, StringComparison.OrdinalIgnoreCase)) == true) == true).ToList();
    if (hits.Count != 1)
    {
        throw new InvalidOperationException($"Expected exactly one movie whose file contains \"{fragment}\", found {hits.Count}. Nothing deleted.");
    }

    var movie = hits[0];
    var part = movie["Media"]![0]!["Part"]![0]!;
    Console.WriteLine($"Movie: {movie["title"]} (ratingKey {movie["ratingKey"]})");
    Console.WriteLine($"File:  {part["file"]}");
    Console.WriteLine($"Size:  {(part["size"]?.GetValue<long>() ?? 0) / 1_048_576} MB");

    var prefs = await plex.GetAsync("/:/prefs", ct);
    var allowed = prefs["Setting"]?.AsArray().FirstOrDefault(s => s?["id"]?.GetValue<string>() == "allowMediaDeletion")?["value"]?.ToString();
    Console.WriteLine($"Plex 'Allow media deletion': {allowed ?? "unknown"}");

    if (!apply)
    {
        Console.WriteLine("Dry run: nothing was deleted. Re-run with --apply to delete this movie and its file.");
        return 0;
    }

    if (!string.Equals(allowed, "True", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Plex's 'Allow media deletion' is not on, so Plex would only drop the library entry and leave the file. Turn it on in Plex (Settings, Library) and re-run.");
    }

    await plex.DeleteAsync($"/library/metadata/{movie["ratingKey"]}", ct);
    Console.WriteLine("Deleted.");
    return 0;
}

static async Task<int> Apply(PlexClient plex, string csvPath, bool apply, string reportsRoot, CancellationToken ct)
{
    var approved = new List<List<string>>();
    foreach (var line in File.ReadAllLines(csvPath).Skip(1).Where(l => l.Length > 0))
    {
        var cells = SplitCsv(line);
        if (cells.Count >= 11 && cells[10].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            approved.Add(cells);
        }
    }

    var dupes = approved.GroupBy(c => c[0]).Where(g => g.Count() > 1).Select(g => g.First()[1]).ToList();
    if (dupes.Count > 0)
    {
        throw new InvalidOperationException($"More than one row is approved for: {string.Join("; ", dupes)}. Approve one row per movie.");
    }

    Console.WriteLine($"{approved.Count} matches approved in {csvPath}");
    foreach (var c in approved)
    {
        Console.WriteLine($"  {c[1]}  ->  {c[6]} ({c[7]})  {c[8]}");
    }

    if (!apply)
    {
        Console.WriteLine("Dry run: nothing was changed. Re-run with --apply to match these in Plex.");
        return 0;
    }

    var log = new StringBuilder("RatingKey,PlexTitle,MatchedTo,Guid,Result\n");
    foreach (var c in approved)
    {
        var url = $"/library/metadata/{c[0]}/match?guid={Uri.EscapeDataString(c[8])}&name={Uri.EscapeDataString(c[6])}&year={c[7]}";
        string result;
        try
        {
            await plex.PutAsync(url, ct);
            result = "matched";
        }
        catch (HttpRequestException ex)
        {
            result = "failed: " + ex.Message;
        }

        Console.WriteLine($"  {c[1]}: {result}");
        log.Append($"{c[0]},\"{c[1].Replace("\"", "\"\"")}\",\"{c[6].Replace("\"", "\"\"")}\",{c[8]},{result}\n");
    }

    var logPath = Path.Combine(reportsRoot, "plex-match-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
    File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(true));
    Console.WriteLine($"Done. Log: {logPath}");
    Console.WriteLine("Plex refreshes metadata in the background; give it a minute, then re-run PlexScan.");
    return 0;
}

static string ResolveCsv(string arg, string reportsRoot)
{
    if (arg != "latest")
    {
        return arg;
    }

    var pointer = Path.Combine(reportsRoot, "latest-fix.txt");
    return File.Exists(pointer)
        ? File.ReadAllText(pointer).Trim()
        : throw new FileNotFoundException($"{pointer} not found. Run PlexFix candidates first.");
}

static bool HasExternalId(JsonNode item)
{
    var guids = item["Guid"]?.AsArray().Select(g => g?["id"]?.GetValue<string>()).ToList() ?? [];
    guids.Add(item["guid"]?.GetValue<string>());
    return guids.Any(g => g != null && (g.StartsWith("imdb://") || g.StartsWith("tmdb://") || g.StartsWith("tvdb://")
        || g.StartsWith("plex://") || Regex.IsMatch(g, @"agents\.(themoviedb|imdb|thetvdb)://")));
}

/// <summary>Best guess at the real title and year: the movie's folder name ("Title (2024)") when it has one, otherwise the Plex title with scene junk trimmed.</summary>
static (string Title, string Year) GuessTitle(string plexTitle, string? plexYear, string file)
{
    var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
    var m = Regex.Match(folder, @"^(?<t>.+?)\s*\((?<y>\d{4})\)$");
    if (m.Success)
    {
        return (m.Groups["t"].Value, m.Groups["y"].Value);
    }

    var title = Regex.Replace(plexTitle, @"[.\-_~](?=\S)", " ");
    var year = Regex.Match(title, @"\b(19|20)\d{2}\b");
    title = Regex.Replace(title, @"\b(19|20)\d{2}\b.*$", "").Trim();
    return (title.Length > 0 ? title : plexTitle, year.Success ? year.Value : plexYear ?? "");
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

static List<string> SplitCsv(string line)
{
    var cells = new List<string>();
    var sb = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (quoted)
        {
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        else if (c == '"')
        {
            quoted = true;
        }
        else if (c == ',')
        {
            cells.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(c);
        }
    }

    cells.Add(sb.ToString());
    return cells;
}

static class Known
{
    // Best-known real title and year for each unmatched movie, keyed by the title Plex shows for it. Candidates matching these exactly are pre-approved.
    public static readonly Dictionary<string, (string Name, string Year)> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arthur the King"] = ("Arthur the King", "2024"),
        ["Breathe"] = ("Breathe", "2024"),
        ["Dead Sea"] = ("Dead Sea", "2024"),
        ["Feast.2005.1080p.BluRay.x265-RARBG"] = ("Feast", "2005"),
        ["Five Nights at Freddy's"] = ("Five Nights at Freddy's", "2023"),
        ["From Up on Poppy Hill~kokuriko Zaka Kara Storyboards Blu Ray 8bit"] = ("From Up on Poppy Hill", "2011"),
        ["Insidious"] = ("Insidious", "2010"),
        ["Krampus the Christmas Devil"] = ("Krampus: The Christmas Devil", "2013"),
        ["Leprechaun 2"] = ("Leprechaun 2", "1994"),
        ["Leprechaun 3"] = ("Leprechaun 3", "1995"),
        ["No Way Up"] = ("No Way Up", "2024"),
        ["Sharknado 2: The Second One"] = ("Sharknado 2: The Second One", "2014"),
        ["The Super Mario Bros Movie"] = ("The Super Mario Bros. Movie", "2023"),
        ["TRON Ares"] = ("TRON: Ares", "2025"),
    };
}
