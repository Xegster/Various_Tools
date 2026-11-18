using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using PlexManager.Core;
using PlexManager.Core.Clients;
using PlexManager.Core.Jobs;
using PlexManager.Core.Operations;
using PlexManager.Core.Settings;

namespace PlexManager.App.Web;

/// <summary>
/// The local API the screens talk to, on 127.0.0.1 only. Every long operation is started as a job so the screen
/// can poll its log and cancel it; everything destructive still goes through the dry run, review and apply steps.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>Plex and Trakt sign-ins in progress, keyed by a handle the browser holds while it polls.</summary>
    private static readonly ConcurrentDictionary<string, PlexPin> PlexPins = new();
    private static readonly ConcurrentDictionary<string, TraktDeviceCode> TraktCodes = new();

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapStatus(api);
        MapSettings(api);
        MapConnections(api);
        MapTraktLists(api);
        MapRuns(api);
        MapJobs(api);
        MapImport(api);
    }

    // ---------------------------------------------------------------- status

    private static void MapStatus(RouteGroupBuilder api) =>
        api.MapGet("/status", (SettingsStore store, AppPaths paths, RunStore runs, JobRunner jobs) =>
        {
            var settings = store.Current;
            var active = jobs.Active;
            return Results.Ok(new
            {
                version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                dataFolder = paths.Root,
                settingsFile = store.Path,
                reportsFolder = paths.Reports,
                backupsFolder = paths.Backups,
                setupComplete = settings.SetupComplete,
                services = new[]
                {
                    Service("Plex", settings.Plex.IsConfigured && settings.Plex.Token.HasValue, settings.Plex.IsConfigured ? settings.Plex.BaseUrl : "", settings.Plex.ServerName),
                    Service("Radarr", settings.Radarr.IsConfigured && settings.Radarr.ApiKey.HasValue, settings.Radarr.IsConfigured ? settings.Radarr.BaseUrl : "", settings.Radarr.QualityProfileName),
                    Service("Sonarr", settings.Sonarr.IsConfigured && settings.Sonarr.ApiKey.HasValue, settings.Sonarr.IsConfigured ? settings.Sonarr.BaseUrl : "", settings.Sonarr.QualityProfileName),
                    Service("Trakt", settings.Trakt.HasTokens, "api.trakt.tv", settings.Trakt.Username),
                    Service("TMDB", settings.Tmdb.ApiKey.HasValue, "api.themoviedb.org", settings.Tmdb.Region),
                    Service("OMDb", settings.Omdb.ApiKey.HasValue, "omdbapi.com", ""),
                },
                ratingThreshold = settings.Migration.RatingThreshold,
                releaseYearCutoff = settings.Migration.ReleaseYearCutoff,
                rottenTomatoesCutoff = settings.Migration.RottenTomatoesCutoff,
                lists = new
                {
                    source = settings.Migration.SourceLists.Select(l => l.ToString()),
                    removeFrom = new[] { "Watchlist (default)" }.Concat(settings.Migration.RemoveFromLists.Select(l => l.ToString())),
                    streaming = settings.Migration.StreamingTargets.Where(t => t.Enabled).Select(t => $"{t.Provider} -> {t.List}"),
                },
                lastBackup = LatestBackupInfo(paths),
                latestRuns = new[] { "scan", "migrate", "prune", "fix", "backup" }
                    .Select(kind => runs.Latest(kind))
                    .Where(r => r != null)
                    .Select(r => new { r!.Id, r.Kind, r.Title, r.FinishedAt, r.Summary }),
                activeJob = active == null ? null : new { active.Id, active.Operation, active.Description },
            });
        });

    private static object Service(string name, bool connected, string address, string detail) =>
        new { name, connected, address, detail };

    private static object? LatestBackupInfo(AppPaths paths)
    {
        if (!Directory.Exists(paths.Backups))
        {
            return null;
        }

        var newest = Directory.GetDirectories(paths.Backups)
            .Where(d => File.Exists(Path.Combine(d, "smart-lists.json")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .LastOrDefault();
        return newest == null
            ? null
            : new { folder = newest, at = Directory.GetLastWriteTime(newest) };
    }

    // ---------------------------------------------------------------- settings

    private static void MapSettings(RouteGroupBuilder api)
    {
        api.MapGet("/settings", (SettingsStore store) => Results.Json(SettingsStore.Redact(store.Current).RootElement));

        // The browser never receives a secret, so a field left at its hint is kept as it was.
        api.MapPut("/settings", async (HttpRequest request, SettingsStore store) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            var incoming = JsonSerializer.Deserialize<PlexManagerSettings>(
                document.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (incoming == null)
            {
                return Results.BadRequest(new { error = "Could not read the settings." });
            }

            var existing = store.Current;
            KeepSecret(incoming.Plex.Token, existing.Plex.Token);
            KeepSecret(incoming.Radarr.ApiKey, existing.Radarr.ApiKey);
            KeepSecret(incoming.Sonarr.ApiKey, existing.Sonarr.ApiKey);
            KeepSecret(incoming.Tmdb.ApiKey, existing.Tmdb.ApiKey);
            KeepSecret(incoming.Omdb.ApiKey, existing.Omdb.ApiKey);

            // Tokens are only ever written by a sign-in, never by the settings form.
            incoming.Trakt.AccessToken = existing.Trakt.AccessToken;
            incoming.Trakt.RefreshToken = existing.Trakt.RefreshToken;
            incoming.Trakt.CreatedAt = existing.Trakt.CreatedAt;
            incoming.Trakt.ExpiresIn = existing.Trakt.ExpiresIn;
            incoming.Trakt.Username = existing.Trakt.Username;

            store.Save(incoming);
            return Results.Json(SettingsStore.Redact(store.Current).RootElement);
        });

        api.MapPost("/settings/complete", (SettingsStore store) =>
        {
            store.Current.SetupComplete = true;
            store.Save();
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>A value the browser sent back unchanged (a hint such as "****1234", or empty) leaves the stored secret alone.</summary>
    private static void KeepSecret(Secret incoming, Secret existing)
    {
        if (incoming.Value.Length == 0 || incoming.Value.StartsWith("****", StringComparison.Ordinal))
        {
            incoming.Value = existing.Value;
        }
    }

    // ---------------------------------------------------------------- connections and sign-in

    private static void MapConnections(RouteGroupBuilder api)
    {
        // Test connection: one read-only call per service, with whatever the form has in it right now.
        api.MapPost("/test/{service}", async (string service, SettingsStore store, AppPaths paths, RunStore runs, CancellationToken ct) =>
        {
            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            try
            {
                switch (service.ToLowerInvariant())
                {
                    case "plex":
                        var identity = await context.Plex.IdentityAsync(ct);
                        var sections = await context.Plex.SectionsAsync(ct);
                        store.Current.Plex.ServerName = identity.Name;
                        store.Save();
                        return Results.Ok(new
                        {
                            ok = true,
                            message = $"Connected to {identity.Name} (Plex {identity.Version}).",
                            sections = sections.Select(s => new { s.Id, s.Title, s.Type }),
                        });

                    case "radarr":
                    case "sonarr":
                        var arr = service.Equals("radarr", StringComparison.OrdinalIgnoreCase) ? context.Radarr : context.Sonarr;
                        var status = await arr.StatusAsync(ct);
                        return Results.Ok(new
                        {
                            ok = true,
                            message = $"Connected to {status.AppName} {status.Version}.",
                            profiles = (await arr.QualityProfilesAsync(ct)).Select(p => new { p.Id, p.Name }),
                            rootFolders = await arr.RootFoldersAsync(ct),
                        });

                    case "trakt":
                        var trakt = await context.TraktAsync(ct);
                        var name = await trakt.AccountNameAsync(ct);
                        return Results.Ok(new { ok = true, message = $"Signed in to Trakt as {name}." });

                    case "tmdb":
                        return Results.Ok(new { ok = true, message = await context.Tmdb.TestAsync(ct) });

                    case "omdb":
                        return Results.Ok(new { ok = true, message = await context.Omdb.TestAsync(ct) });

                    default:
                        return Results.BadRequest(new { ok = false, message = $"Unknown service \"{service}\"." });
                }
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, message = ex.Message });
            }
        });

        // Radarr and Sonarr often hand out their own API key on the LAN, so the wizard need not ask for it.
        api.MapPost("/discover-key/{service}", async (string service, SettingsStore store, CancellationToken ct) =>
        {
            var settings = service.Equals("radarr", StringComparison.OrdinalIgnoreCase) ? store.Current.Radarr : store.Current.Sonarr;
            if (!settings.IsConfigured)
            {
                return Results.Ok(new { ok = false, message = "Fill in the address first." });
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var key = await ArrClient.TryDiscoverApiKeyAsync(settings.BaseUrl, http, ct);
            if (key == null)
            {
                return Results.Ok(new { ok = false, message = "The server did not hand out its API key; paste it instead (Settings, General in Radarr or Sonarr)." });
            }

            settings.ApiKey = new Secret(key);
            store.Save();
            return Results.Ok(new { ok = true, message = "Read the API key from the server.", hint = settings.ApiKey.Hint });
        });

        // ---- Sign in with Plex (the plex.tv PIN flow) ----
        api.MapPost("/signin/plex/start", async (AppPaths paths, CancellationToken ct) =>
        {
            using var http = new HttpClient();
            var pin = await new PlexTvAuth(http).StartAsync(PlexTvAuth.ClientIdentifier(paths), ct);
            var handle = Guid.NewGuid().ToString("N");
            PlexPins[handle] = pin;
            return Results.Ok(new { handle, code = pin.Code, url = pin.Url });
        });

        api.MapPost("/signin/plex/poll", async (string handle, SettingsStore store, AppPaths paths, CancellationToken ct) =>
        {
            if (!PlexPins.TryGetValue(handle, out var pin))
            {
                return Results.Ok(new { state = "failed", message = "That sign-in has gone. Start again." });
            }

            using var http = new HttpClient();
            var auth = new PlexTvAuth(http);
            string? token;
            try
            {
                token = await auth.PollAsync(pin, ct);
            }
            catch (Exception ex)
            {
                PlexPins.TryRemove(handle, out _);
                return Results.Ok(new { state = "failed", message = ex.Message });
            }

            if (token == null)
            {
                return Results.Ok(new { state = "pending", message = "Waiting for the code to be approved on plex.tv." });
            }

            PlexPins.TryRemove(handle, out _);
            var servers = await auth.ServersAsync(token, pin.ClientIdentifier, ct);

            // Prefer the server's own access token and local address when the account owns one.
            var server = servers.FirstOrDefault();
            store.Current.Plex.Token = new Secret(server?.Token ?? token);
            if (server != null && store.Current.Plex.Host.Length == 0)
            {
                store.Current.Plex.Host = server.Address;
                store.Current.Plex.Port = server.Port;
                store.Current.Plex.UseHttps = server.Https;
                store.Current.Plex.ServerName = server.Name;
            }

            store.Save();
            return Results.Ok(new
            {
                state = "signedin",
                message = server != null ? $"Signed in; found {server.Name} at {server.Address}:{server.Port}." : "Signed in to Plex.",
                servers = servers.Select(s => new { s.Name, s.Address, s.Port, s.Https }),
            });
        });

        // ---- Connect Trakt (the device-code flow) ----
        api.MapPost("/signin/trakt/start", async (SettingsStore store, AppPaths paths, RunStore runs, CancellationToken ct) =>
        {
            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            try
            {
                var device = await context.Auth.StartDeviceAsync(ct);
                var handle = Guid.NewGuid().ToString("N");
                TraktCodes[handle] = device;
                return Results.Ok(new
                {
                    handle,
                    code = device.UserCode,
                    url = device.VerificationUrl,
                    interval = device.Interval,
                    expiresIn = device.ExpiresIn,
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.Message });
            }
        });

        api.MapPost("/signin/trakt/poll", async (string handle, SettingsStore store, AppPaths paths, RunStore runs, CancellationToken ct) =>
        {
            if (!TraktCodes.TryGetValue(handle, out var device))
            {
                return Results.Ok(new { state = "failed", message = "That sign-in has gone. Start again." });
            }

            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            var result = await context.Auth.PollDeviceAsync(device.DeviceCode, ct);
            if (result.State == TraktDeviceState.Signedin)
            {
                TraktCodes.TryRemove(handle, out _);
                var client = new TraktClient(store, context.Auth, context.Http, NullOperationLog.Instance);
                var name = await client.AccountNameAsync(ct);
                return Results.Ok(new { state = "signedin", message = $"Signed in to Trakt as {name}." });
            }

            if (result.State == TraktDeviceState.Failed)
            {
                TraktCodes.TryRemove(handle, out _);
            }

            return Results.Ok(new { state = result.State.ToString().ToLowerInvariant(), message = result.Message });
        });

        api.MapPost("/signin/trakt/out", (SettingsStore store, AppPaths paths, RunStore runs) =>
        {
            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            context.Auth.SignOut();
            return Results.Ok(new { ok = true });
        });

        // The TMDB region drop-down.
        api.MapGet("/tmdb/regions", async (SettingsStore store, AppPaths paths, RunStore runs, CancellationToken ct) =>
        {
            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            try
            {
                return Results.Ok(new { ok = true, regions = (await context.Tmdb.RegionsAsync(ct)).Select(r => r.Name) });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, message = ex.Message, regions = Array.Empty<string>() });
            }
        });
    }

    // ---------------------------------------------------------------- Trakt lists (phase 3)

    private static void MapTraktLists(RouteGroupBuilder api) =>
        api.MapGet("/trakt/lists", async (SettingsStore store, AppPaths paths, RunStore runs, CancellationToken ct) =>
        {
            using var context = new OperationContext(store, paths, runs, NullOperationLog.Instance);
            try
            {
                var client = await context.TraktAsync(ct);
                var lists = await client.PersonalListsAsync(ct);
                return Results.Ok(new
                {
                    ok = true,
                    lists = lists.Select(l => new { l.Id, l.Slug, l.Name, l.ItemCount, l.IsWatchlist }),
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, message = ex.Message, lists = Array.Empty<object>() });
            }
        });

    // ---------------------------------------------------------------- runs (the review screens)

    private static void MapRuns(RouteGroupBuilder api)
    {
        api.MapGet("/runs", (RunStore runs, string? kind) => Results.Ok(
            runs.List(kind).Select(r => new
            {
                r.Id,
                r.Kind,
                r.Title,
                r.StartedAt,
                r.FinishedAt,
                r.ReviewedAt,
                r.Applied,
                r.Summary,
                r.CsvPath,
                r.HtmlPath,
                r.NextStep,
                rows = r.Rows.Count,
            })));

        api.MapGet("/runs/{id}", (string id, RunStore runs) =>
            runs.Load(id) is { } run ? Results.Ok(run) : Results.NotFound(new { error = $"No run {id}." }));

        api.MapPut("/runs/{id}/rows", async (string id, HttpRequest request, RunStore runs) =>
        {
            var rows = await JsonSerializer.DeserializeAsync<List<Dictionary<string, string>>>(request.Body);
            if (rows == null)
            {
                return Results.BadRequest(new { error = "No rows sent." });
            }

            try
            {
                return Results.Ok(runs.UpdateRows(id, rows));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Opens a report folder or file in Explorer, so "open folder" works from the window.
        api.MapPost("/reveal", (JsonObject body, AppPaths paths) =>
        {
            var target = body["path"]?.GetValue<string>() ?? paths.Reports;
            if (!IsInsideData(target, paths))
            {
                return Results.BadRequest(new { error = "That path is not in the data folder." });
            }

            var argument = File.Exists(target) ? $"/select,\"{target}\"" : $"\"{target}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            return Results.Ok(new { ok = true });
        });

        // Serves a report file (CSV or HTML) to the screen that asked for it.
        api.MapGet("/file", Results<FileContentHttpResult, BadRequest<object>, NotFound<object>> (string path, AppPaths paths) =>
        {
            if (!IsInsideData(path, paths))
            {
                return TypedResults.BadRequest<object>(new { error = "That path is not in the data folder." });
            }

            if (!File.Exists(path))
            {
                return TypedResults.NotFound<object>(new { error = "No such file." });
            }

            var type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".csv" => "text/csv; charset=utf-8",
                ".json" => "application/json",
                _ => "text/plain; charset=utf-8",
            };
            return TypedResults.File(File.ReadAllBytes(path), type, Path.GetFileName(path));
        });
    }

    private static bool IsInsideData(string path, AppPaths paths)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(paths.Root), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- jobs

    private static void MapJobs(RouteGroupBuilder api)
    {
        api.MapPost("/jobs", (JobStart body, JobRunner jobs, SettingsStore store, AppPaths paths, RunStore runs) =>
        {
            if (jobs.Active != null)
            {
                return Results.Conflict(new { error = $"\"{jobs.Active.Description}\" is still running. Wait for it or cancel it." });
            }

            try
            {
                var job = JobFactory.Start(body, jobs, store, paths, runs);
                return Results.Ok(new { job.Id, job.Operation, job.Description });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/jobs", (JobRunner jobs) => Results.Ok(jobs.Recent().Select(Describe)));

        api.MapGet("/jobs/{id}", (string id, int? from, JobRunner jobs) =>
        {
            var job = jobs.Get(id);
            if (job == null)
            {
                return Results.NotFound(new { error = $"No job {id}." });
            }

            var described = Describe(job);
            return Results.Ok(new
            {
                described.Id,
                described.Operation,
                described.Description,
                described.State,
                described.StartedAt,
                described.FinishedAt,
                described.Error,
                described.Percent,
                described.RunId,
                lines = job.LinesFrom(from ?? 0),
                total = job.LineCount,
            });
        });

        api.MapPost("/jobs/{id}/cancel", (string id, JobRunner jobs) =>
        {
            var job = jobs.Get(id);
            if (job == null)
            {
                return Results.NotFound(new { error = $"No job {id}." });
            }

            job.Cancel();
            return Results.Ok(new { ok = true });
        });
    }

    private static JobDescription Describe(Job job) => new(
        job.Id,
        job.Operation,
        job.Description,
        job.State.ToString().ToLowerInvariant(),
        job.StartedAt,
        job.FinishedAt,
        job.Error,
        job.Percent,
        job.Run?.Id);

    // ---------------------------------------------------------------- import (phase 6)

    private static void MapImport(RouteGroupBuilder api)
    {
        api.MapGet("/import/scan", (AppPaths paths, string? folder) => Results.Ok(new LegacyImporter(paths).Scan(folder)));

        api.MapPost("/import", (ImportRequest body, SettingsStore store, AppPaths paths, RunStore runs, JobRunner jobs) =>
        {
            try
            {
                var importer = new LegacyImporter(paths);
                var result = importer.Import(store, NullOperationLog.Instance, body.Folder, body.CopyData, body.Overwrite);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record JobDescription(
    string Id,
    string Operation,
    string Description,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    int? Percent,
    string? RunId);

/// <summary>What a screen sends to start an operation.</summary>
public sealed record JobStart(string Operation, bool Apply = false, string? Input = null, int? Threshold = null);

public sealed record ImportRequest(string? Folder = null, bool CopyData = true, bool Overwrite = false);
