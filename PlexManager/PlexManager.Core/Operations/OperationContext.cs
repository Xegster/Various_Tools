using PlexManager.Core.Clients;
using PlexManager.Core.Settings;

namespace PlexManager.Core.Operations;

/// <summary>
/// Everything an operation needs: the settings, the data folder, the run store, a log to report to, and lazily
/// created clients. One context per operation run; disposing it closes the HTTP clients.
/// </summary>
public sealed class OperationContext : IDisposable
{
    private readonly HttpClient _http;
    private PlexClient? _plex;
    private ArrClient? _radarr;
    private ArrClient? _sonarr;
    private TraktClient? _trakt;
    private OmdbClient? _omdb;
    private TmdbClient? _tmdb;
    private ShowLanguageLookup? _languages;

    public OperationContext(SettingsStore settings, AppPaths paths, RunStore runs, IOperationLog log)
    {
        Settings = settings;
        Paths = paths;
        Runs = runs;
        Log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PlexManager/1.0");
        Auth = new TraktAuth(settings, _http);
    }

    public SettingsStore Settings { get; }

    public PlexManagerSettings Values => Settings.Current;

    public AppPaths Paths { get; }

    public RunStore Runs { get; }

    public IOperationLog Log { get; }

    public TraktAuth Auth { get; }

    /// <summary>
    /// Whether an operation may run the Trakt device flow itself, printing a code and waiting for it to be
    /// approved. True for the CLI, where the console is there to show it; false for the window, where Settings
    /// has a Connect button and a blocked job would just look stuck.
    /// </summary>
    public bool AllowInteractiveSignIn { get; set; }

    public HttpClient Http => _http;

    public PlexClient Plex => _plex ??= new PlexClient(Values.Plex, _http);

    public ArrClient Radarr => _radarr ??= new ArrClient(Values.Radarr, isRadarr: true);

    public ArrClient Sonarr => _sonarr ??= new ArrClient(Values.Sonarr, isRadarr: false);

    public OmdbClient Omdb => _omdb ??= new OmdbClient(Values.Omdb, Paths.Cache("omdb-cache.json"), _http);

    public TmdbClient Tmdb => _tmdb ??= new TmdbClient(Values.Tmdb, Paths.Cache("streaming-cache.json"), _http, Log);

    public ShowLanguageLookup Languages =>
        _languages ??= new ShowLanguageLookup(Values.Trakt.ClientId, Paths.Cache("trakt-language-cache.json"), _http);

    /// <summary>A signed-in Trakt client. Signing in may run the device flow, which prints a code to the log.</summary>
    public async Task<TraktClient> TraktAsync(CancellationToken ct)
    {
        if (_trakt != null)
        {
            return _trakt;
        }

        if (!Auth.IsSignedIn && !AllowInteractiveSignIn)
        {
            throw new SettingsIncompleteException(
                "Trakt is not connected. Open Settings and press Connect Trakt, which shows a code to approve on trakt.tv.");
        }

        await Auth.EnsureSignedInAsync(Log, ct);
        return _trakt = new TraktClient(Settings, Auth, _http, Log);
    }

    /// <summary>The newest finished Trakt backup folder, the default input for scan, prune and migrate.</summary>
    public string LatestBackup()
    {
        if (!Directory.Exists(Paths.Backups))
        {
            throw new DirectoryNotFoundException($"No Trakt backups in {Paths.Backups}. Run a backup first.");
        }

        // smart-lists.json is the last file a backup writes, so a folder without it is unfinished or failed.
        return Directory.GetDirectories(Paths.Backups)
                   .Where(d => File.Exists(Path.Combine(d, "smart-lists.json")))
                   .OrderBy(d => d, StringComparer.Ordinal)
                   .LastOrDefault()
               ?? throw new DirectoryNotFoundException($"No finished Trakt backups in {Paths.Backups}. Run a backup first.");
    }

    /// <summary>Writes the "latest" pointer files the CLI uses to resolve <c>latest</c>.</summary>
    public void WritePointer(string name, string value)
    {
        Directory.CreateDirectory(Paths.Reports);
        File.WriteAllText(Path.Combine(Paths.Reports, name), value);
    }

    /// <summary>Reads one of those pointers.</summary>
    public string ReadPointer(string name)
    {
        var path = Path.Combine(Paths.Reports, name);
        return File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : throw new FileNotFoundException($"{path} not found. Run the step that produces it first.");
    }

    public void Dispose()
    {
        _radarr?.Dispose();
        _sonarr?.Dispose();
        _http.Dispose();
    }
}
