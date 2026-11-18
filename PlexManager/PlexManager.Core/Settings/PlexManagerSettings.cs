using System.Text.Json.Serialization;

namespace PlexManager.Core.Settings;

/// <summary>
/// Everything the app needs to run, in one file (<c>settings.json</c> in the data folder). Replaces
/// plexmanager.config.json, trakt.config.json, trakt.tokens.json and plexmanager.exclusions.json.
/// Each service keeps its own host, because Radarr and Sonarr need not share a machine with Plex.
/// </summary>
public sealed class PlexManagerSettings
{
    public int Version { get; set; } = 1;

    public PlexSettings Plex { get; set; } = new();

    public ArrSettings Radarr { get; set; } = new() { Port = 7878 };

    public ArrSettings Sonarr { get; set; } = new() { Port = 8989 };

    public TraktSettings Trakt { get; set; } = new();

    public TmdbSettings Tmdb { get; set; } = new();

    public OmdbSettings Omdb { get; set; } = new();

    public MigrationSettings Migration { get; set; } = new();

    public ExclusionSettings Exclusions { get; set; } = new();

    public FixSettings Fix { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    /// <summary>True once the first-run wizard has been completed (or an import stood in for it).</summary>
    public bool SetupComplete { get; set; }
}

/// <summary>Host and port of a service, or a full base URL when it sits behind a proxy or a different scheme.</summary>
public abstract class ServiceEndpoint
{
    public string Host { get; set; } = "";

    public int Port { get; set; }

    public bool UseHttps { get; set; }

    /// <summary>Optional path prefix, as Radarr and Sonarr call it ("radarr"). Ignored when empty.</summary>
    public string UrlBase { get; set; } = "";

    /// <summary>Set to override Host, Port, UseHttps and UrlBase completely.</summary>
    public string BaseUrlOverride { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrlOverride) || !string.IsNullOrWhiteSpace(Host);

    [JsonIgnore]
    public abstract string ServiceName { get; }

    /// <summary>The address the clients use, with no trailing slash.</summary>
    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BaseUrlOverride))
            {
                return BaseUrlOverride.TrimEnd('/');
            }

            if (string.IsNullOrWhiteSpace(Host))
            {
                throw new SettingsIncompleteException($"No address set for {ServiceName}. Open Settings and fill in its host.");
            }

            var prefix = string.IsNullOrWhiteSpace(UrlBase) ? "" : "/" + UrlBase.Trim('/');
            return $"{(UseHttps ? "https" : "http")}://{Host}:{Port}{prefix}";
        }
    }
}

public sealed class PlexSettings : ServiceEndpoint
{
    public PlexSettings()
    {
        Port = 32400;
    }

    [JsonIgnore]
    public override string ServiceName => "Plex";

    public Secret Token { get; set; } = new();

    /// <summary>Library section that holds movies (Plex calls it a section key). Filled in by the wizard.</summary>
    public int MovieSectionId { get; set; } = 4;

    /// <summary>Library section that holds TV shows.</summary>
    public int ShowSectionId { get; set; } = 1;

    public string MovieSectionName { get; set; } = "";

    public string ShowSectionName { get; set; } = "";

    public string ServerName { get; set; } = "";
}

public sealed class ArrSettings : ServiceEndpoint
{
    public string Kind { get; set; } = "";

    [JsonIgnore]
    public override string ServiceName => string.IsNullOrEmpty(Kind) ? "Radarr or Sonarr" : Kind;

    public Secret ApiKey { get; set; } = new();

    public int QualityProfileId { get; set; }

    /// <summary>Kept only so the UI can show the chosen profile without a round trip; the id is what is used.</summary>
    public string QualityProfileName { get; set; } = "";

    public string RootFolder { get; set; } = "";
}

public sealed class TraktSettings
{
    public string ClientId { get; set; } = "";

    /// <summary>Must match the redirect URI registered on the Trakt app; only sent on token refresh.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    public Secret AccessToken { get; set; } = new();

    public Secret RefreshToken { get; set; } = new();

    public long CreatedAt { get; set; }

    public int ExpiresIn { get; set; }

    /// <summary>Account name from /users/settings, shown in the status bar.</summary>
    public string Username { get; set; } = "";

    [JsonIgnore]
    public bool HasTokens => AccessToken.HasValue && RefreshToken.HasValue;

    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(CreatedAt + ExpiresIn);
}

public sealed class TmdbSettings
{
    public Secret ApiKey { get; set; } = new();

    /// <summary>Two-letter region for the streaming check (TMDB watch providers).</summary>
    public string Region { get; set; } = "AU";
}

public sealed class OmdbSettings
{
    public Secret ApiKey { get; set; } = new();
}

/// <summary>A Trakt list the user picked. Stored by id so a rename on Trakt does not break the setting.</summary>
public sealed class TraktListRef
{
    /// <summary>Trakt list id, or 0 for the account's default watchlist.</summary>
    public long Id { get; set; }

    public string Slug { get; set; } = "";

    /// <summary>Name as it was when picked; for display, and to create the list if it does not exist yet.</summary>
    public string Name { get; set; } = "";

    [JsonIgnore]
    public bool IsWatchlist => Id == 0 && Slug == "watchlist";

    public static TraktListRef Watchlist() => new() { Id = 0, Slug = "watchlist", Name = "Watchlist (default)" };

    public override string ToString() => IsWatchlist ? "Watchlist (default)" : Name.Length > 0 ? Name : $"list {Id}";
}

public sealed class MigrationSettings
{
    /// <summary>A Trakt rating below this means "do not bother" (the plan skips it); also the prune threshold.</summary>
    public int RatingThreshold { get; set; } = 7;

    /// <summary>Titles released in this year or later are monitored and searched on add.</summary>
    public int ReleaseYearCutoff { get; set; } = 2025;

    /// <summary>Older movies are monitored only at or above this Rotten Tomatoes percentage.</summary>
    public int RottenTomatoesCutoff { get; set; } = 80;

    /// <summary>Drop series whose original language is not one of these (empty keeps every language).</summary>
    public List<string> KeepOriginalLanguages { get; set; } = ["en"];

    /// <summary>Lists read as the source of what to migrate and clean. Defaults to the watchlist alone.</summary>
    public List<TraktListRef> SourceLists { get; set; } = [TraktListRef.Watchlist()];

    /// <summary>
    /// Lists titles are removed from once they are dealt with. The watchlist is always included; these are the
    /// extra personal lists kept in step with it (what used to be hard-coded as "Watch list 2").
    /// </summary>
    public List<TraktListRef> RemoveFromLists { get; set; } = [];

    /// <summary>Where titles already on a streaming service are moved to.</summary>
    public List<StreamingTarget> StreamingTargets { get; set; } =
    [
        new() { Provider = "Netflix", List = new TraktListRef { Name = "Watch on Netflix" } },
        new() { Provider = "Prime Video", List = new TraktListRef { Name = "Watch on Prime" } },
    ];

    /// <summary>The overflow list the watchlist cleaner keeps a duplicate in, when there is one.</summary>
    public TraktListRef? OverflowList { get; set; }
}

/// <summary>A streaming service that makes a title "already available", and the Trakt list it is moved to.</summary>
public sealed class StreamingTarget
{
    public string Provider { get; set; } = "";

    public TraktListRef List { get; set; } = new();

    /// <summary>Off means the plan ignores this provider.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class ExclusionSettings
{
    /// <summary>Shows never reported or pushed, by Plex title.</summary>
    public List<string> Shows { get; set; } = [];

    /// <summary>Single episodes Trakt does not recognise, as "Show|S01E02".</summary>
    public List<string> Episodes { get; set; } = [];
}

public sealed class FixSettings
{
    /// <summary>
    /// The real title and year for a Plex movie whose own title is unusable (scene junk, a wrong name).
    /// A candidate matching one of these exactly is pre-approved, whatever its rank. Editable on the Fix screen;
    /// this used to be a dictionary compiled into PlexFix.
    /// </summary>
    public List<KnownTitle> KnownTitles { get; set; } = [];
}

/// <summary>A Plex title mapped to what it really is.</summary>
public sealed class KnownTitle
{
    /// <summary>The title as Plex shows it, which is what the mapping is keyed on.</summary>
    public string PlexTitle { get; set; } = "";

    public string Name { get; set; } = "";

    public string Year { get; set; } = "";
}

public sealed class UiSettings
{
    /// <summary>Loopback port for the local web UI; 0 picks a free one and remembers it.</summary>
    public int Port { get; set; }

    /// <summary>Open the window with WebView2 when it is available, else the default browser.</summary>
    public bool PreferWebView { get; set; } = true;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 860;
}

/// <summary>Thrown when an operation needs a setting the user has not filled in yet.</summary>
public sealed class SettingsIncompleteException(string message) : Exception(message);
