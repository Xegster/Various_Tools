# PlexManager

One Windows program that keeps Plex, Trakt, Radarr and Sonarr in agreement: compare them, review what it found,
and act on it. It replaced the five console tools (`TraktBackup`, `PlexScan`, `PlexFix`, `PlexPrune`,
`PlexMigrate`) on 2026-11-12; see [PLAN-exe.md](PLAN-exe.md) for the plan it was built to and
[ROADMAP.md](ROADMAP.md) for the work the console tools did first.

## Build and run

```
dotnet build PlexManager.slnx
dotnet publish PlexManager.App -p:PublishProfile=exe -c Release
```

The publish writes a single `PlexManager.exe` (about 130 MB) to `PlexManager.App/bin/publish/`. There is nothing
to install: copy that one file anywhere and run it. It needs no .NET on the machine, and the window uses the
WebView2 runtime that ships with Windows 11 and updated Windows 10; without it the app opens the same UI in the
default browser. An unsigned exe can trip SmartScreen on first run ("More info", "Run anyway").

Run it with no arguments and it opens the window. The first launch offers to import an existing PlexManager
folder, which fills in every setting including the Trakt sign-in.

## Where its data lives

`%APPDATA%\PlexManager`:

| | |
|---|---|
| `settings.json` | every setting, with tokens and API keys encrypted for your Windows account (DPAPI) |
| `Reports\` | the CSV and HTML reports, in the same layout the console tools wrote |
| `Backups\` | Trakt backups, one timestamped folder each |
| `Runs\` | what each run found, so the review screens can show and edit it |
| `Caches\` | the OMDb, streaming and language caches |
| `Logs\` | one log file per operation |

Replacing the exe keeps all of it. A `portable.flag` file next to the exe, or `--data <folder>`, puts everything
beside the exe instead.

## Screens

| Screen | What it does |
|---|---|
| Dashboard | what is connected, when the last backup ran, the usual order of work |
| Backup | pull the whole Trakt account into a folder; every Trakt write runs one first |
| Plex vs Trakt | find what Plex has watched and Trakt has not, then push movies and episodes |
| Fix unmatched | ask Plex for match candidates for movies with no metadata, approve one each, apply |
| Prune low-rated | list Plex movies you rated low on Trakt, mark the keepers, delete the rest |
| Migrate | plan what to hand to Radarr and Sonarr, add it, verify it, then clear the Trakt lists |
| Streaming lists | move titles already on a service you pay for onto their own Trakt list |
| Tidy lists | drop watched titles and duplicates from the chosen lists |
| Reports | every run, with its rows and its CSV |
| Settings | each service with a Test connection button; also the first-run wizard |

Every screen keeps the dry run, review, apply order. Nothing destructive starts from a list that has not been
reviewed, and a Plex deletion or a Trakt removal asks for a typed confirmation.

## Command line

The same exe runs headless, over the same Core, the same settings and the same data folder. Dry run by default,
`--apply` to act, exit code 0 or non-zero.

```
PlexManager --help
```

```
PlexManager scan
```

```
PlexManager migrate plan
```

```
PlexManager migrate apply latest --apply
```

## How it is put together

| Project | Contents |
|---|---|
| `PlexManager.Core` | the settings store, the Plex, Trakt, Radarr, Sonarr, TMDB and OMDb clients, id matching, CSV, and every operation as a class that reports progress through `IOperationLog` |
| `PlexManager.App` | the exe: the CLI front end, a Kestrel server bound to `127.0.0.1`, the minimal API, and the WebView2 window. The UI is embedded in the assembly |

No operation writes to the console or reads `args`; both front ends call the same classes with typed arguments.

## Research notes

Docs 01 to 05 predate the code and are kept for reference.

| Doc | Contents |
|---|---|
| [01-trakt-api.md](01-trakt-api.md) | VIP gate on API apps, limits, auth flows, endpoints for reading data and editing lists, rate limits, gotchas |
| [02-radarr-sonarr-api.md](02-radarr-sonarr-api.md) | Lookup and add for movies and series, queue, history, calendar, built-in Trakt import lists |
| [03-prior-art-and-libraries.md](03-prior-art-and-libraries.md) | Existing tools (Traktarr, MDBList, PlexTraktSync, Seerr) and .NET client libraries |
| [04-options-and-recommendation.md](04-options-and-recommendation.md) | Option matrix, architecture sketches, recommendation, open questions |
| [05-plex-api.md](05-plex-api.md) | Plex Media Server endpoints used here |

Two things worth remembering from them: creating a Trakt API app needs an active VIP membership (apps owned by
free accounts were disabled around 2026-07-31), and Trakt writes are limited to one call per second.
