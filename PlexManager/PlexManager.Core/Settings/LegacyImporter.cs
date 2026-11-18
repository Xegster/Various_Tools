using System.Text.Json;
using PlexManager.Core.Operations;

namespace PlexManager.Core.Settings;

/// <summary>
/// Phase 6: finds the old console tools' files on this machine and copies them into the data folder, so nothing
/// has to be typed in again. It reads plexmanager.config.json, trakt.config.json, trakt.tokens.json,
/// plexmanager.exclusions.json, the caches, the Backups folder and the Reports folder.
/// </summary>
public sealed class LegacyImporter
{
    private static readonly string[] CacheFiles = ["omdb-cache.json", "streaming-cache.json", "trakt-language-cache.json"];

    private readonly AppPaths _paths;

    public LegacyImporter(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>What an import would find, without changing anything.</summary>
    public LegacyScan Scan(string? folder = null)
    {
        var home = folder ?? FindLegacyHome();
        if (home == null)
        {
            return new LegacyScan(null, false, false, false, false, 0, 0);
        }

        var backups = Path.Combine(home, "TraktBackup", "Backups");
        var reports = Path.Combine(home, "Reports");
        return new LegacyScan(
            home,
            File.Exists(Path.Combine(home, "plexmanager.config.json")),
            File.Exists(Path.Combine(home, "TraktBackup", "trakt.config.json")) || File.Exists(Path.Combine(home, "trakt.config.json")),
            File.Exists(Path.Combine(home, "TraktBackup", "trakt.tokens.json")) || File.Exists(Path.Combine(home, "trakt.tokens.json")),
            File.Exists(Path.Combine(home, "plexmanager.exclusions.json")),
            Directory.Exists(backups) ? Directory.GetDirectories(backups).Length : 0,
            Directory.Exists(reports) ? Directory.GetFileSystemEntries(reports).Length : 0);
    }

    /// <summary>Imports what it finds. Existing files in the data folder are left alone unless overwrite is set.</summary>
    public LegacyImportResult Import(SettingsStore store, IOperationLog log, string? folder = null, bool copyData = true, bool overwrite = false)
    {
        var scan = Scan(folder);
        if (scan.Folder == null)
        {
            throw new FileNotFoundException(
                "No existing PlexManager folder found. It is the folder that holds plexmanager.config.json.");
        }

        var home = scan.Folder;
        var settings = store.Current;
        var notes = new List<string>();
        log.Info($"Importing from {home}");

        // ---- plexmanager.config.json: one host for everything, separate ports ----
        var configPath = Path.Combine(home, "plexmanager.config.json");
        if (File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var host = root.TryGetProperty("host", out var hostValue) ? hostValue.GetString() ?? "" : "";

            ImportService(root, "plex", host, settings.Plex, 32400, out var plexToken);
            if (plexToken.Length > 0)
            {
                settings.Plex.Token = new Secret(plexToken);
            }

            ImportService(root, "radarr", host, settings.Radarr, 7878, out var radarrKey);
            if (radarrKey.Length > 0)
            {
                settings.Radarr.ApiKey = new Secret(radarrKey);
            }

            ImportService(root, "sonarr", host, settings.Sonarr, 8989, out var sonarrKey);
            if (sonarrKey.Length > 0)
            {
                settings.Sonarr.ApiKey = new Secret(sonarrKey);
            }

            if (root.TryGetProperty("omdb", out var omdb) && omdb.TryGetProperty("apiKey", out var omdbKey))
            {
                settings.Omdb.ApiKey = new Secret(omdbKey.GetString() ?? "");
            }

            if (root.TryGetProperty("tmdb", out var tmdb))
            {
                if (tmdb.TryGetProperty("apiKey", out var tmdbKey))
                {
                    settings.Tmdb.ApiKey = new Secret(tmdbKey.GetString() ?? "");
                }

                if (tmdb.TryGetProperty("region", out var region))
                {
                    settings.Tmdb.Region = region.GetString() ?? settings.Tmdb.Region;
                }
            }

            notes.Add("Plex, Radarr, Sonarr, OMDb and TMDB settings");
        }

        // ---- trakt.config.json and trakt.tokens.json ----
        var traktConfig = FirstExisting(home, "TraktBackup/trakt.config.json", "trakt.config.json");
        if (traktConfig != null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(traktConfig));
            settings.Trakt.ClientId = node?["ClientId"]?.GetValue<string>() ?? settings.Trakt.ClientId;
            settings.Trakt.RedirectUri = node?["RedirectUri"]?.GetValue<string>() ?? settings.Trakt.RedirectUri;
            notes.Add("Trakt Client ID");
        }

        var traktTokens = FirstExisting(home, "TraktBackup/trakt.tokens.json", "trakt.tokens.json");
        if (traktTokens != null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(traktTokens));
            var access = node?["access_token"]?.GetValue<string>() ?? "";
            var refresh = node?["refresh_token"]?.GetValue<string>() ?? "";
            if (access.Length > 0 && refresh.Length > 0)
            {
                settings.Trakt.AccessToken = new Secret(access);
                settings.Trakt.RefreshToken = new Secret(refresh);
                settings.Trakt.ExpiresIn = node?["expires_in"]?.GetValue<int>() ?? 0;
                settings.Trakt.CreatedAt = node?["created_at"]?.GetValue<long>() ?? 0;
                notes.Add("Trakt sign-in (no need to sign in again)");
            }
        }

        // ---- plexmanager.exclusions.json ----
        var exclusions = Path.Combine(home, "plexmanager.exclusions.json");
        if (File.Exists(exclusions))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(exclusions));
            settings.Exclusions.Shows = (node?["excludedShows"]?.AsArray() ?? [])
                .Select(n => n!.GetValue<string>()).ToList();
            settings.Exclusions.Episodes = (node?["excludedEpisodes"]?.AsArray() ?? [])
                .Select(n => n!.GetValue<string>()).ToList();
            notes.Add($"{settings.Exclusions.Shows.Count} excluded shows and {settings.Exclusions.Episodes.Count} excluded episodes");
        }

        // ---- The lists the old tools had hard-coded ----
        if (settings.Migration.RemoveFromLists.Count == 0 && settings.Migration.OverflowList == null)
        {
            var overflow = new TraktListRef { Name = "Watch list 2" };
            settings.Migration.OverflowList = overflow;
            settings.Migration.RemoveFromLists.Add(overflow);
            if (settings.Migration.SourceLists.All(l => l.IsWatchlist))
            {
                settings.Migration.SourceLists.Add(overflow);
            }

            notes.Add("the lists the old tools used (watchlist and \"Watch list 2\"); confirm them in Settings");
        }

        // ---- The titles PlexFix had compiled in ----
        if (settings.Fix.KnownTitles.Count == 0)
        {
            settings.Fix.KnownTitles = LegacyKnownTitles.All();
            notes.Add($"{settings.Fix.KnownTitles.Count} known-title corrections for the Fix screen");
        }

        var copiedCaches = 0;
        var copiedBackups = 0;
        var copiedReports = 0;
        if (copyData)
        {
            foreach (var cache in CacheFiles)
            {
                var from = Path.Combine(home, cache);
                var to = _paths.Cache(cache);
                if (File.Exists(from) && (overwrite || !File.Exists(to)))
                {
                    Directory.CreateDirectory(_paths.Caches);
                    File.Copy(from, to, overwrite);
                    copiedCaches++;
                }
            }

            copiedBackups = CopyTree(Path.Combine(home, "TraktBackup", "Backups"), _paths.Backups, overwrite, log);
            copiedReports = CopyTree(Path.Combine(home, "Reports"), _paths.Reports, overwrite, log);
            CopyTree(Path.Combine(home, "TraktBackup", "Current"), _paths.CurrentLists, overwrite, log);

            if (copiedCaches > 0)
            {
                notes.Add($"{copiedCaches} cache files");
            }

            if (copiedBackups > 0)
            {
                notes.Add($"{copiedBackups} files of Trakt backups");
            }

            if (copiedReports > 0)
            {
                notes.Add($"{copiedReports} report files");
            }
        }

        settings.SetupComplete = true;
        store.Save(settings);

        foreach (var note in notes)
        {
            log.Detail("imported: " + note);
        }

        log.Success($"Import done. Settings are now in {store.Path}.");
        return new LegacyImportResult(home, notes, copiedCaches, copiedBackups, copiedReports);
    }

    /// <summary>
    /// Looks for the old PlexManager folder: beside the exe and up the tree (the way the console tools searched),
    /// then in the usual development locations.
    /// </summary>
    public static string? FindLegacyHome()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "plexmanager.config.json")))
                {
                    return dir.FullName;
                }
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Development\Various_Tools\PlexManager",
                     @"C:\Code\Various_Tools\PlexManager",
                 })
        {
            if (File.Exists(Path.Combine(candidate, "plexmanager.config.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void ImportService(
        JsonElement root,
        string name,
        string host,
        ServiceEndpoint endpoint,
        int defaultPort,
        out string secret)
    {
        secret = "";
        if (!root.TryGetProperty(name, out var section))
        {
            return;
        }

        if (section.TryGetProperty("baseUrl", out var baseUrl) && !string.IsNullOrWhiteSpace(baseUrl.GetString()))
        {
            endpoint.BaseUrlOverride = baseUrl.GetString()!;
        }
        else
        {
            endpoint.Host = host;
            endpoint.Port = section.TryGetProperty("port", out var port) ? port.GetInt32() : defaultPort;
        }

        foreach (var key in new[] { "token", "apiKey" })
        {
            if (section.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                secret = value.GetString()!;
            }
        }
    }

    private static string? FirstExisting(string home, params string[] relative) =>
        relative.Select(r => Path.Combine(home, r.Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);

    private static int CopyTree(string from, string to, bool overwrite, IOperationLog log)
    {
        if (!Directory.Exists(from) || Path.GetFullPath(from).Equals(Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, source));
            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(source, target, overwrite);
                copied++;
            }
            catch (IOException ex)
            {
                log.Warn($"could not copy {source}: {ex.Message}");
            }
        }

        return copied;
    }
}

public sealed record LegacyScan(
    string? Folder,
    bool HasConfig,
    bool HasTraktConfig,
    bool HasTraktTokens,
    bool HasExclusions,
    int BackupFolders,
    int ReportEntries)
{
    public bool Found => Folder != null;
}

public sealed record LegacyImportResult(string Folder, List<string> Imported, int Caches, int BackupFiles, int ReportFiles);

/// <summary>
/// The title corrections PlexFix carried as a compiled-in dictionary. They are imported once into Settings, where
/// they can be edited, and are not used anywhere else.
/// </summary>
internal static class LegacyKnownTitles
{
    public static List<KnownTitle> All() =>
    [
        New("Arthur the King", "Arthur the King", "2024"),
        New("Breathe", "Breathe", "2024"),
        New("Dead Sea", "Dead Sea", "2024"),
        New("Feast.2005.1080p.BluRay.x265-RARBG", "Feast", "2005"),
        New("Five Nights at Freddy's", "Five Nights at Freddy's", "2023"),
        New("From Up on Poppy Hill~kokuriko Zaka Kara Storyboards Blu Ray 8bit", "From Up on Poppy Hill", "2011"),
        New("Insidious", "Insidious", "2010"),
        New("Krampus the Christmas Devil", "Krampus: The Christmas Devil", "2013"),
        New("Leprechaun 2", "Leprechaun 2", "1994"),
        New("Leprechaun 3", "Leprechaun 3", "1995"),
        New("No Way Up", "No Way Up", "2024"),
        New("Sharknado 2: The Second One", "Sharknado 2: The Second One", "2014"),
        New("The Super Mario Bros Movie", "The Super Mario Bros. Movie", "2023"),
        New("TRON Ares", "TRON: Ares", "2025"),
    ];

    private static KnownTitle New(string plexTitle, string name, string year) =>
        new() { PlexTitle = plexTitle, Name = name, Year = year };
}
