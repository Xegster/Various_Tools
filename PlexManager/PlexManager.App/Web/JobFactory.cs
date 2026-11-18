using PlexManager.Core;
using PlexManager.Core.Jobs;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.App.Web;

/// <summary>
/// Turns an operation name from a screen into the Core operation that does the work. The names match the CLI
/// verbs, so the window and the terminal run the same thing.
/// </summary>
public static class JobFactory
{
    public static Job Start(JobStart request, JobRunner jobs, SettingsStore store, AppPaths paths, RunStore runs)
    {
        var apply = request.Apply;
        var input = request.Input;

        return request.Operation switch
        {
            "backup" => jobs.Start("backup", "Back up Trakt", async (context, ct) =>
            {
                await new BackupOperation(context).RunAsync(ct);
                return runs.Latest("backup");
            }),

            "clean" => jobs.Start("clean", apply ? "Tidy the Trakt lists" : "Tidy the Trakt lists (dry run)",
                (context, ct) => Wrap(new WatchlistCleanOperation(context).RunAsync(apply, ct))),

            "scan" => jobs.Start("scan", "Compare Plex with Trakt", async (context, ct) =>
                (await new ScanOperation(context).RunAsync(Blank(input), ct)).Run),

            "push-movies" => jobs.Start("push-movies", apply ? "Push movie plays to Trakt" : "Push movie plays (dry run)",
                (context, ct) => Wrap(new PushMoviesOperation(context).RunAsync(
                    ScanCsv(context, input, "movies-watched-in-plex-not-trakt.csv"), apply, ct))),

            "push-episodes" => jobs.Start("push-episodes", apply ? "Push episode plays to Trakt" : "Push episode plays (dry run)",
                (context, ct) => Wrap(new PushEpisodesOperation(context).RunAsync(
                    ScanCsv(context, input, "episodes-watched-in-plex-not-trakt.csv"), apply, ct))),

            "fix-candidates" => jobs.Start("fix-candidates", "Find match candidates",
                (context, ct) => Wrap(new FixCandidatesOperation(context).RunAsync(ct))),

            "fix-apply" => jobs.Start("fix-apply", apply ? "Match movies in Plex" : "Match movies in Plex (dry run)",
                (context, ct) => Wrap(new FixApplyOperation(context).RunAsync(Pointer(context, input, "latest-fix.txt"), apply, ct))),

            "fix-delete" => jobs.Start("fix-delete", apply ? "Delete one Plex movie" : "Delete one Plex movie (dry run)",
                (context, ct) => Wrap(new FixDeleteOperation(context).RunAsync(
                    input ?? throw new InvalidOperationException("No file path fragment given."), apply, ct))),

            "prune-candidates" => jobs.Start("prune-candidates", "List low-rated movies",
                (context, ct) => Wrap(new PruneCandidatesOperation(context).RunAsync(request.Threshold, Blank(input), ct))),

            "prune-apply" => jobs.Start("prune-apply", apply ? "Delete low-rated movies" : "Delete low-rated movies (dry run)",
                (context, ct) => Wrap(new PruneApplyOperation(context).RunAsync(Pointer(context, input, "latest-prune.txt"), apply, ct))),

            "migrate-plan" => jobs.Start("migrate-plan", "Work out the migration plan",
                (context, ct) => Wrap(new MigratePlanOperation(context).RunAsync(Blank(input), ct))),

            "migrate-apply" => jobs.Start("migrate-apply", apply ? "Add to Radarr and Sonarr" : "Add to Radarr and Sonarr (dry run)",
                (context, ct) => Wrap(new MigrateApplyOperation(context).RunAsync(Pointer(context, input, "latest-migrate.txt"), apply, ct))),

            "migrate-verify" => jobs.Start("migrate-verify", "Check Radarr and Sonarr",
                (context, ct) => Wrap(new MigrateVerifyOperation(context).RunAsync(Pointer(context, input, "latest-migrate.txt"), ct))),

            "trakt-remove-lists" => jobs.Start("trakt-remove-lists", apply ? "Remove reviewed titles from Trakt" : "Remove reviewed titles (dry run)",
                (context, ct) => Wrap(new TraktRemoveOperation(context).RunAsync(
                    Pointer(context, input, "latest-migrate.txt"), TraktRemovalSource.ReviewedPlan, apply, ct))),

            "trakt-remove-verified" => jobs.Start("trakt-remove-verified", apply ? "Remove verified titles from Trakt" : "Remove verified titles (dry run)",
                (context, ct) => Wrap(new TraktRemoveOperation(context).RunAsync(
                    Pointer(context, input, "latest-verified.txt"), TraktRemovalSource.Verified, apply, ct))),

            "trakt-remove-leftovers" => jobs.Start("trakt-remove-leftovers", apply ? "Remove leftovers from Trakt" : "Remove leftovers (dry run)",
                (context, ct) => Wrap(new TraktRemoveOperation(context).RunAsync(
                    Pointer(context, input, "latest-migrate.txt"), TraktRemovalSource.Leftovers, apply, ct))),

            "move-streaming" => jobs.Start("move-streaming", apply ? "Move streaming titles" : "Move streaming titles (dry run)",
                (context, ct) => Wrap(new StreamingMoveOperation(context).RunAsync(Pointer(context, input, "latest-migrate.txt"), apply, ct))),

            _ => throw new InvalidOperationException($"Unknown operation \"{request.Operation}\"."),
        };
    }

    private static async Task<RunRecord?> Wrap(Task<RunRecord> work) => await work;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A screen sends either a path or nothing, which means "the newest one" through the pointer file.</summary>
    private static string Pointer(OperationContext context, string? input, string pointer) =>
        string.IsNullOrWhiteSpace(input) || input == "latest" ? context.ReadPointer(pointer) : input;

    private static string ScanCsv(OperationContext context, string? input, string fileName) =>
        string.IsNullOrWhiteSpace(input) || input == "latest"
            ? Path.Combine(context.ReadPointer("latest.txt"), fileName)
            : input;
}
