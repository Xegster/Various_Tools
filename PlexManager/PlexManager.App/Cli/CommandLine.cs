namespace PlexManager.App.Cli;

/// <summary>
/// The parsed command line: global options, a verb, a sub-verb and positional arguments.
/// Every verb maps to one Core operation, with the same "dry run unless --apply" rule as the old console tools.
/// </summary>
public sealed class CommandLine
{
    private static readonly string[] KnownVerbs =
    [
        "ui", "backup", "clean", "scan", "push-movies", "push-episodes",
        "fix", "prune", "migrate", "trakt", "import", "settings", "help",
    ];

    private CommandLine(string verb, string? sub, List<string> positional, Dictionary<string, string?> options, string? unknownVerb)
    {
        Verb = verb;
        Sub = sub;
        Positional = positional;
        Options = options;
        UnknownVerb = unknownVerb;
    }

    public string Verb { get; }

    /// <summary>The first argument when it was meant to be a verb but is not one, so a typo does not open the window.</summary>
    public string? UnknownVerb { get; }

    public string? Sub { get; }

    public List<string> Positional { get; }

    public Dictionary<string, string?> Options { get; }

    public bool Apply => Has("apply");

    public bool Help => Has("help") || Has("h") || Has("?");

    /// <summary>The data folder override (<c>--data</c>), or null for the default.</summary>
    public string? DataFolder => Value("data");

    public bool Has(string name) => Options.ContainsKey(name);

    /// <summary>True when there is no verb at all, so the window should open.</summary>
    public bool OpensWindow => Verb == "ui" && UnknownVerb == null;

    public string? Value(string name) => Options.GetValueOrDefault(name);

    public int? IntValue(string name) =>
        int.TryParse(Value(name), out var value) ? value : null;

    /// <summary>The positional argument at <paramref name="index"/>, or <paramref name="fallback"/>.</summary>
    public string At(int index, string fallback) => index < Positional.Count ? Positional[index] : fallback;

    public static CommandLine Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg.TrimStart('-');
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                options[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            // --data <folder> and --port <n> take a value; the flags do not.
            if (name is "data" or "port" && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                options[name] = args[++i];
            }
            else
            {
                options[name] = null;
            }
        }

        var verb = "ui";
        string? unknownVerb = null;
        if (positional.Count > 0)
        {
            if (KnownVerbs.Contains(positional[0]))
            {
                verb = positional[0];
                positional.RemoveAt(0);
            }
            else
            {
                unknownVerb = positional[0];
            }
        }

        string? sub = null;
        if (verb is "fix" or "prune" or "migrate" or "trakt" or "settings" && positional.Count > 0)
        {
            sub = positional[0];
            positional.RemoveAt(0);
        }

        return new CommandLine(verb, sub, positional, options, unknownVerb);
    }

    public const string Usage = """
        PlexManager - Plex, Trakt, Radarr and Sonarr housekeeping.

        Run it with no arguments to open the window. Every verb below does the same work as the window,
        is read-only or a dry run by default, and needs --apply to change anything.

          PlexManager                                  open the app
          PlexManager backup                           back up the whole Trakt account
          PlexManager clean [--apply]                  tidy the chosen Trakt lists (watched, duplicates)
          PlexManager scan [<trakt-backup-folder>]     compare Plex with the newest Trakt backup
          PlexManager push-movies <csv|latest> [--apply]    add Plex-watched movies to the Trakt history
          PlexManager push-episodes <csv|latest> [--apply]  add Plex-watched episodes to the Trakt history
          PlexManager fix candidates                   find match candidates for unmatched Plex movies
          PlexManager fix apply <csv|latest> [--apply] match the approved candidates in Plex
          PlexManager fix delete <path-fragment> [--apply]  delete one Plex movie and its file
          PlexManager prune candidates [--threshold <n>] [<backup>]   list movies rated below the threshold
          PlexManager prune apply <csv|latest> [--apply]    delete the ones not marked Keep
          PlexManager migrate plan [<trakt-backup-folder>]  work out what to move to Radarr and Sonarr
          PlexManager migrate apply <csv|latest> [--apply]  add the "add" rows to Radarr and Sonarr
          PlexManager migrate verify <csv|latest>      check every added title really arrived
          PlexManager trakt remove-lists <csv|latest> [--apply]      remove the rows you blanked
          PlexManager trakt remove-verified <csv|latest> [--apply]   remove the titles verify confirmed
          PlexManager trakt remove-leftovers <csv|latest> [--apply]  remove the ones never meant for Radarr/Sonarr
          PlexManager trakt move-streaming <csv|latest> [--apply]    move titles already streaming to their lists
          PlexManager import [<old-folder>]            import the old console tools' settings and data
          PlexManager settings show                    print where the settings live and what is connected

        Options
          --apply            carry the change out instead of a dry run
          --data <folder>    use another data folder (the default is %APPDATA%\PlexManager)
          --port <n>         loopback port for the window's web UI
          --no-window        start the web UI without opening a window, and print the address
          --threshold <n>    prune: the rating to stay below (the default comes from Settings)
          --help             this text
        """;
}
