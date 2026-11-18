using PlexManager.Core;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.App.Cli;

/// <summary>
/// The headless front end (phase 5b): the same Core operations as the window, the same settings and data folder,
/// dry run by default, exit code 0 on success and non-zero on failure.
/// </summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(CommandLine command, AppPaths paths, CancellationToken ct)
    {
        if (command.Help || command.Verb == "help")
        {
            Console.WriteLine(CommandLine.Usage);
            return 0;
        }

        var store = new SettingsStore(paths);
        var runs = new RunStore(paths);
        var log = new ConsoleOperationLog();

        if (command.Verb == "import")
        {
            var importer = new LegacyImporter(paths);
            importer.Import(store, log, command.Positional.FirstOrDefault());
            return 0;
        }

        if (command.Verb == "settings")
        {
            return ShowSettings(store, paths);
        }

        if (!store.Exists)
        {
            Console.Error.WriteLine($"No settings yet ({store.Path}).");
            Console.Error.WriteLine("Run PlexManager with no arguments to set it up, or \"PlexManager import\" to bring the old files over.");
            return 1;
        }

        // The console can show a Trakt sign-in code and wait for it, so the CLI is allowed to run the device flow.
        using var context = new OperationContext(store, paths, runs, log) { AllowInteractiveSignIn = true };

        switch (command.Verb)
        {
            case "backup":
                await new BackupOperation(context).RunAsync(ct);
                return 0;

            case "clean":
                await new WatchlistCleanOperation(context).RunAsync(command.Apply, ct);
                return 0;

            case "scan":
                await new ScanOperation(context).RunAsync(command.Positional.FirstOrDefault(), ct);
                return 0;

            case "push-movies":
                await new PushMoviesOperation(context).RunAsync(
                    ResolveScanCsv(context, command.At(0, "latest"), "movies-watched-in-plex-not-trakt.csv"),
                    command.Apply,
                    ct);
                return 0;

            case "push-episodes":
                await new PushEpisodesOperation(context).RunAsync(
                    ResolveScanCsv(context, command.At(0, "latest"), "episodes-watched-in-plex-not-trakt.csv"),
                    command.Apply,
                    ct);
                return 0;

            case "fix":
                return await FixAsync(context, command, ct);

            case "prune":
                return await PruneAsync(context, command, ct);

            case "migrate":
                return await MigrateAsync(context, command, ct);

            case "trakt":
                return await TraktAsync(context, command, ct);

            default:
                Console.Error.WriteLine($"Unknown verb \"{command.Verb}\".");
                Console.Error.WriteLine(CommandLine.Usage);
                return 1;
        }
    }

    private static async Task<int> FixAsync(OperationContext context, CommandLine command, CancellationToken ct)
    {
        switch (command.Sub)
        {
            case "candidates":
                await new FixCandidatesOperation(context).RunAsync(ct);
                return 0;
            case "apply":
                await new FixApplyOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-fix.txt"),
                    command.Apply,
                    ct);
                return 0;
            case "delete":
                if (command.Positional.Count == 0)
                {
                    Console.Error.WriteLine("Usage: PlexManager fix delete <file-path-fragment> [--apply]");
                    return 1;
                }

                await new FixDeleteOperation(context).RunAsync(command.Positional[0], command.Apply, ct);
                return 0;
            default:
                Console.Error.WriteLine("Usage: PlexManager fix candidates | fix apply <csv|latest> [--apply] | fix delete <fragment> [--apply]");
                return 1;
        }
    }

    private static async Task<int> PruneAsync(OperationContext context, CommandLine command, CancellationToken ct)
    {
        switch (command.Sub)
        {
            case "candidates":
                // A bare number is still accepted as the threshold, the way PlexPrune took it.
                var threshold = command.IntValue("threshold")
                                ?? (command.Positional.Count > 0 && int.TryParse(command.Positional[0], out var parsed) ? parsed : null);
                var backup = command.Positional.FirstOrDefault(p => !int.TryParse(p, out _));
                await new PruneCandidatesOperation(context).RunAsync(threshold, backup, ct);
                return 0;
            case "apply":
                var run = await new PruneApplyOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-prune.txt"),
                    command.Apply,
                    ct);
                return run.Summary.Any(s => s.Contains("problems: 0", StringComparison.Ordinal)) || !command.Apply ? 0 : 1;
            default:
                Console.Error.WriteLine("Usage: PlexManager prune candidates [--threshold <n>] [<backup>] | prune apply <csv|latest> [--apply]");
                return 1;
        }
    }

    private static async Task<int> MigrateAsync(OperationContext context, CommandLine command, CancellationToken ct)
    {
        switch (command.Sub)
        {
            case "plan":
                await new MigratePlanOperation(context).RunAsync(command.Positional.FirstOrDefault(), ct);
                return 0;
            case "apply":
                await new MigrateApplyOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-migrate.txt"),
                    command.Apply,
                    ct);
                return 0;
            case "verify":
                var run = await new MigrateVerifyOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-migrate.txt"),
                    ct);

                // Non-zero when something the plan said should be there is not, as the old tool did.
                return run.Summary.Contains("Missing: 0") ? 0 : 1;
            default:
                Console.Error.WriteLine("Usage: PlexManager migrate plan [<backup>] | migrate apply <csv|latest> [--apply] | migrate verify <csv|latest>");
                return 1;
        }
    }

    private static async Task<int> TraktAsync(OperationContext context, CommandLine command, CancellationToken ct)
    {
        switch (command.Sub)
        {
            case "remove-lists":
                await new TraktRemoveOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-migrate.txt"),
                    TraktRemovalSource.ReviewedPlan,
                    command.Apply,
                    ct);
                return 0;
            case "remove-verified":
                await new TraktRemoveOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-verified.txt"),
                    TraktRemovalSource.Verified,
                    command.Apply,
                    ct);
                return 0;
            case "remove-leftovers":
                await new TraktRemoveOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-migrate.txt"),
                    TraktRemovalSource.Leftovers,
                    command.Apply,
                    ct);
                return 0;
            case "move-streaming":
                await new StreamingMoveOperation(context).RunAsync(
                    Resolve(context, command.At(0, "latest"), "latest-migrate.txt"),
                    command.Apply,
                    ct);
                return 0;
            default:
                Console.Error.WriteLine(
                    "Usage: PlexManager trakt remove-lists|remove-verified|remove-leftovers|move-streaming <csv|latest> [--apply]");
                return 1;
        }
    }

    /// <summary>"latest" means the path in the given pointer file; anything else is used as given.</summary>
    private static string Resolve(OperationContext context, string argument, string pointer) =>
        argument == "latest" ? context.ReadPointer(pointer) : argument;

    /// <summary>"latest" for a scan report means that file inside the folder the newest scan recorded.</summary>
    private static string ResolveScanCsv(OperationContext context, string argument, string fileName) =>
        argument == "latest" ? Path.Combine(context.ReadPointer("latest.txt"), fileName) : argument;

    private static int ShowSettings(SettingsStore store, AppPaths paths)
    {
        var settings = store.Current;
        Console.WriteLine($"Data folder:  {paths.Root}");
        Console.WriteLine($"Settings:     {store.Path}{(store.Exists ? "" : " (not created yet)")}");
        Console.WriteLine($"Reports:      {paths.Reports}");
        Console.WriteLine($"Backups:      {paths.Backups}");
        Console.WriteLine();
        Console.WriteLine($"Plex:    {Describe(settings.Plex.IsConfigured, settings.Plex.IsConfigured ? settings.Plex.BaseUrl : "", settings.Plex.Token.HasValue)}");
        Console.WriteLine($"Radarr:  {Describe(settings.Radarr.IsConfigured, settings.Radarr.IsConfigured ? settings.Radarr.BaseUrl : "", settings.Radarr.ApiKey.HasValue)}");
        Console.WriteLine($"Sonarr:  {Describe(settings.Sonarr.IsConfigured, settings.Sonarr.IsConfigured ? settings.Sonarr.BaseUrl : "", settings.Sonarr.ApiKey.HasValue)}");
        Console.WriteLine($"Trakt:   {(settings.Trakt.HasTokens ? $"signed in as {settings.Trakt.Username}" : settings.Trakt.ClientId.Length > 0 ? "Client ID set, not signed in" : "not set up")}");
        Console.WriteLine($"TMDB:    {(settings.Tmdb.ApiKey.HasValue ? $"key set, region {settings.Tmdb.Region}" : "not set (no streaming check)")}");
        Console.WriteLine($"OMDb:    {(settings.Omdb.ApiKey.HasValue ? "key set" : "not set (no Rotten Tomatoes check)")}");
        Console.WriteLine();
        Console.WriteLine("Source lists:     " + string.Join(", ", settings.Migration.SourceLists.Select(l => l.ToString())));
        Console.WriteLine("Removed from:     Watchlist (default)"
                          + (settings.Migration.RemoveFromLists.Count > 0
                              ? ", " + string.Join(", ", settings.Migration.RemoveFromLists.Select(l => l.ToString()))
                              : ""));
        Console.WriteLine($"Rating threshold: below {settings.Migration.RatingThreshold}");
        Console.WriteLine($"Year cutoff:      {settings.Migration.ReleaseYearCutoff} and later are monitored");
        Console.WriteLine($"Rotten Tomatoes:  {settings.Migration.RottenTomatoesCutoff}% or more for older movies");
        return 0;
    }

    private static string Describe(bool configured, string url, bool hasSecret) =>
        !configured ? "not set up" : $"{url}{(hasSecret ? "" : " (no token or key yet)")}";
}
