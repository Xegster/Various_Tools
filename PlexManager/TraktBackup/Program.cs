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
    //        TraktBackup push-movies <csv> [--apply]   add Plex-watched movies (PlexScan CSV) to Trakt history; --apply backs up first
    var config = TraktConfig.Load();
    var cleaning = args.Length > 0 && args[0] == "clean";
    var pushing = args.Length > 1 && args[0] == "push-movies";
    var root = !cleaning && !pushing && args.Length > 0 ? args[0] : Path.Combine(TraktConfig.HomeDirectory(), "Backups");

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
        await new MovieHistoryPusher(client, reports).RunAsync(args[1], args.Contains("--apply"), Backup, cts.Token);
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
