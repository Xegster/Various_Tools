using TraktBackup;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Usage: TraktBackup [output-root]          back up everything (defaults to Backups\ next to trakt.config.json)
    //        TraktBackup clean [--apply]         preview (or perform) the watchlist cleanup; --apply backs up first
    //        (for push-*, <csv> may be "latest": the file from the newest PlexScan run, via Reports\latest.txt)
    //        TraktBackup remove-lists <migrate-plan.csv|latest> [--apply]   remove titles whose Action cell is blank from the watchlist and Watch list 2; --apply backs up first
    //        TraktBackup remove-verified <migrate-verified.csv|latest> [--apply]   remove titles confirmed present in Radarr/Sonarr (PlexMigrate verify) from the watchlist and Watch list 2; --apply backs up first
    //        TraktBackup remove-leftovers <migrate-plan.csv|latest> [--apply]   remove plan rows marked "skip: in Plex", "skip: watched" or "exception", plus the titles in the newest migrate-missing-*.csv; --apply backs up first
    //        TraktBackup move-streaming <migrate-plan.csv|latest> [--apply]   move titles marked "skip: on Netflix"/"skip: on Prime Video" to the lists "Watch on Netflix"/"Watch on Prime"; --apply backs up first
    //        TraktBackup push-movies <csv> [--apply]   add Plex-watched movies (PlexScan CSV) to Trakt history; --apply backs up first
    //        TraktBackup push-episodes <csv> [--apply]  add Plex-watched episodes (PlexScan CSV) to Trakt history; --apply backs up first
    var config = TraktConfig.Load();
    var cleaning = args.Length > 0 && args[0] == "clean";
    var pushing = args.Length > 1 && args[0] == "push-movies";
    var pushingEpisodes = args.Length > 1 && args[0] == "push-episodes";
    var removingLists = args.Length > 1 && args[0] == "remove-lists";
    var movingStreaming = args.Length > 1 && args[0] == "move-streaming";
    var removingVerified = args.Length > 1 && args[0] == "remove-verified";
    var removingLeftovers = args.Length > 1 && args[0] == "remove-leftovers";
    var root = !cleaning && !pushing && !pushingEpisodes && !removingLists && !movingStreaming && !removingVerified && !removingLeftovers && args.Length > 0 ? args[0] : Path.Combine(TraktConfig.HomeDirectory(), "Backups");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("TraktBackup/1.0");

    var auth = new TraktAuth(config, http);
    await auth.EnsureSignedInAsync(cts.Token);

    var client = new TraktClient(config, auth, http);
    Task Backup(CancellationToken ct) =>
        new BackupRunner(client, Path.Combine(root, "trakt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))).RunAsync(ct);

    if (cleaning)
    {
        await new WatchlistCleaner(client, TraktConfig.HomeDirectory()).RunAsync(args.Contains("--apply"), Backup, cts.Token);
    }
    else if (pushing)
    {
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        await new MovieHistoryPusher(client, reports).RunAsync(ResolveCsv(args[1], "movies-watched-in-plex-not-trakt.csv", reports), args.Contains("--apply"), Backup, cts.Token);
    }
    else if (pushingEpisodes)
    {
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        await new EpisodeHistoryPusher(client, reports).RunAsync(ResolveCsv(args[1], "episodes-watched-in-plex-not-trakt.csv", reports), args.Contains("--apply"), Backup, cts.Token);
    }
    else if (removingLists)
    {
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        var plan = args[1] == "latest" ? File.ReadAllText(Path.Combine(reports, "latest-migrate.txt")).Trim() : args[1];
        await new TraktListRemover(client, reports).RunAsync(plan, args.Contains("--apply"), Backup, cts.Token);
    }
    else if (removingLeftovers)
    {
        // Leftovers: plan rows that were never meant for Radarr/Sonarr (in Plex, watched, no id) plus the titles verify found missing.
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        var plan = args[1] == "latest" ? File.ReadAllText(Path.Combine(reports, "latest-migrate.txt")).Trim() : args[1];
        var missing = Directory.GetFiles(reports, "migrate-missing-*.csv").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        var missingIds = new HashSet<string>();
        if (missing != null)
        {
            var ml = File.ReadAllLines(missing);
            var mh = MovieHistoryPusher.SplitCsv(ml[0].TrimStart('﻿'));
            foreach (var l in ml.Skip(1).Where(l => l.Trim().Length > 0))
            {
                missingIds.Add(MovieHistoryPusher.SplitCsv(l)[mh.IndexOf("TraktId")]);
            }
        }

        var pl = File.ReadAllLines(plan);
        var ph = MovieHistoryPusher.SplitCsv(pl[0].TrimStart('﻿'));
        int a = ph.IndexOf("Action"), ty = ph.IndexOf("Type"), ti = ph.IndexOf("Title"), ye = ph.IndexOf("Year"), tr = ph.IndexOf("TraktId");
        var csv = new System.Text.StringBuilder("Action,Type,Title,Year,TraktId\n");
        foreach (var l in pl.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var c = MovieHistoryPusher.SplitCsv(l);
            var act = c[a];
            if (act.StartsWith("skip: in Plex", StringComparison.Ordinal) || act.StartsWith("skip: watched", StringComparison.Ordinal) || act.StartsWith("exception", StringComparison.Ordinal) || missingIds.Contains(c[tr]))
            {
                csv.Append($",{c[ty]},\"{c[ti].Replace("\"", "\"\"")}\",{c[ye]},{c[tr]}\n");
            }
        }

        var leftovers = Path.Combine(reports, "migrate-leftovers-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        File.WriteAllText(leftovers, csv.ToString(), new System.Text.UTF8Encoding(true));
        Console.WriteLine($"Leftovers list: {leftovers}");
        await new TraktListRemover(client, reports).RunAsync(leftovers, args.Contains("--apply"), Backup, cts.Token);
    }
    else if (removingVerified)
    {
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        var verified = args[1] == "latest" ? File.ReadAllText(Path.Combine(reports, "latest-verified.txt")).Trim() : args[1];
        await new TraktListRemover(client, reports).RunAsync(verified, args.Contains("--apply"), Backup, cts.Token);
    }
    else if (movingStreaming)
    {
        var reports = Path.GetFullPath(Path.Combine(TraktConfig.HomeDirectory(), "..", "Reports"));
        var plan = args[1] == "latest" ? File.ReadAllText(Path.Combine(reports, "latest-migrate.txt")).Trim() : args[1];
        await new StreamingListMover(client, reports).RunAsync(plan, args.Contains("--apply"), Backup, cts.Token);
    }
    else
    {
        await Backup(cts.Token);
    }

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

/// <summary>"latest" means the CSV inside the folder PlexScan last recorded in Reports\latest.txt; anything else is used as given.</summary>
static string ResolveCsv(string arg, string fileName, string reportsDir)
{
    if (arg != "latest")
    {
        return arg;
    }

    var pointer = Path.Combine(reportsDir, "latest.txt");
    if (!File.Exists(pointer))
    {
        throw new FileNotFoundException($"{pointer} not found. Run PlexScan first.");
    }

    return Path.Combine(File.ReadAllText(pointer).Trim(), fileName);
}
