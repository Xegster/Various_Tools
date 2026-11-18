# Plan: PlexManager as a single .exe

**Status: Implemented — 2026-11-12**

All six phases are built. See [What was built](#what-was-built-2026-11-12) at the end for where each piece
lives, the two places the plan was deliberately departed from, and what still needs a run against the live
services (nothing here has been run; that is yours).

Goal: replace the five console tools (PlexScan, PlexFix, PlexPrune, PlexMigrate, TraktBackup) with one Windows program that a person can install, set up through screens, and use without a terminal or hand-edited JSON/CSV files.

## Decisions to confirm

| Decision | Recommendation | Why |
|---|---|---|
| UI technology | **Local web UI hosted inside the exe**: ASP.NET Core (Kestrel, bound to `127.0.0.1`) serving a small HTML/JS front end, shown in a WebView2 window (falls back to the default browser) | The roadmap already wants HTML reports with review controls instead of CSVs; one codebase for UI and reports. WPF is the alternative if you want native controls, but every review table would be built twice. |
| Packaging | **No installer.** `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`; copy the one exe anywhere and run it | Nothing to install, no .NET needed. Expected size is roughly 70 to 150 MB, far under the 1 GB limit, so no trimming is needed. |
| Where settings live | `%APPDATA%\PlexManager\` (config, tokens, caches, Reports, Backups); an optional `portable.flag` file or `--data <folder>` switch keeps everything next to the exe instead | Today files are found by walking up from the exe/working dir; an installed exe cannot rely on that. |
| Secrets | Tokens and API keys encrypted with Windows DPAPI (`ProtectedData`, current user) | Today they are plain JSON. |

## Target shape

```
PlexManager.sln
  PlexManager.Core      models, id matching, config store, clients, all operations (no console, no UI)
  PlexManager.App       the exe: Kestrel + minimal API + wwwroot (HTML/JS) + WebView2 shell
  (old console projects kept until each verb has moved, then removed)
```

The existing verbs become **operations** in Core that take typed arguments and report progress through an interface (`IProgress<OperationEvent>`), instead of writing to `Console` and reading `args`. The UI calls them; a thin CLI can stay as a second front end over the same Core if wanted.

## Phases

### 1. Shared Core (the "pause and clean up" item from the roadmap)
- Create `PlexManager.Core`; move `TraktClient`, `TraktAuth`, `PlexClient`, plus new `RadarrClient` and `SonarrClient` (today these calls live inline in `PlexMigrate/Program.cs`, 908 lines).
- Extract the duplicated helpers: CSV/JSON reading and writing, Plex id matching, backup lookup, config loading, the `plexmanager.exclusions.json` handling.
- Replace `Console.WriteLine` with an `IOperationLog`/progress sink, and replace `args` parsing with option records.
- Replace the hard-coded folder search (`TraktConfig.FindFile`) with one `AppPaths` class pointing at `%APPDATA%\PlexManager`.
- Acceptance: all existing verbs compile against Core with no behaviour change. (You run them to verify; nothing is run from here.)

### 2. Settings store and first-run setup wizard
One `settings.json` replacing `plexmanager.config.json`, `trakt.config.json` and `trakt.tokens.json`. A first-run wizard walks through each service, with a **Test connection** button on each step that makes a read-only call and shows the result:

| Step | Fields | Test call |
|---|---|---|
| Plex | host/IP, port (32400), token (paste, or sign in with plex.tv PIN flow so the token need not be found by hand) | `/identity`, then list libraries |
| Radarr | host/IP, port (7878), API key, optional URL base and HTTPS toggle | `/api/v3/system/status`; then load root folders and quality profiles into drop-downs |
| Sonarr | host/IP, port (8989), API key, same extras | same, v3 API |
| Trakt | Client ID (link to the Trakt app page explaining how to create one), then **Connect** button: device-code sign-in showing the code and a link, polling until done | `/users/settings` and show the account name |
| TMDB / OMDb (optional) | API keys, TMDB region (drop-down of countries) | One lookup each; explain what each unlocks (streaming checks, Rotten Tomatoes) |

Each service gets its own host (today one `host` covers all three; Radarr/Sonarr may not share a machine with Plex). Settings also hold: Radarr quality profile and root folder (drop-downs, replacing the name `SD + 720`), Sonarr quality profile and root folder, rating threshold (7), "monitor and search" rules (release-year cutoff, Rotten Tomatoes cutoff), and excluded-title list (editable table instead of JSON). The Settings screen is the wizard again, reachable any time; the app shows which services are connected in a status bar.

Trakt note: today the app uses the device-code flow with only a Client ID. Keep that; confirm it still works for a new user with a fresh Trakt API app (the sign-in needs a redirect URI on the app; the wizard should show the exact value to enter).

#### How the data folder gets filled (no hand-edited files)
The app creates `%APPDATA%\PlexManager\` and every file in it itself; nothing is ever typed into a file. Values come from:

| Value | How it is set |
|---|---|
| Existing setup on this machine | First launch finds the current `plexmanager.config.json`, `trakt.config.json`, `trakt.tokens.json`, exclusions, caches, Backups and Reports and offers a one-click **Import** (step 6), so this machine needs no re-entry at all |
| Plex token | **Sign in with Plex** button (plex.tv PIN flow); the token is saved by the app |
| Trakt tokens | **Connect Trakt** button (device-code sign-in); tokens saved and refreshed by the app |
| Trakt Client ID | Pasted once into a form field (Trakt has no way to create an app automatically); the form links to the Trakt app page and shows the redirect value to enter. Alternative: ship a built-in Client ID so even this is skipped |
| Radarr / Sonarr | IP and port typed in the form; the app then tries to read the API key itself from the server (works when the server allows local access without a login) and otherwise shows one key field to paste. Profiles and root folders are picked from drop-downs |
| TMDB / OMDb keys | Optional, pasted into form fields |
| Exclusions, thresholds, list choices | Form controls and drop-downs on the Settings screen |
| Caches, backups, reports, logs | Written by the app as it runs |

### 3. List selection from Trakt "Personal" lists
Replace every hard-coded list name (`"Watch list 2"`, `"Watch on Netflix"`, `"Watch on Prime"`) with a user choice, populated live from Trakt (`GET /users/me/lists`, the personal lists only; liked lists are excluded by the endpoint itself).

- A reusable **List picker** control: drop-down of the account's personal lists (name, item count), a refresh button, and the default Trakt watchlist as a fixed first entry labelled **"Watchlist (default)"** (it is not a personal list, but is part of today's workflow). Personal lists show just their name and count. Multi-select where an operation reads several lists.
- Stored by Trakt list **id/slug**, not name, so a rename on Trakt does not break the setting. Show the name; keep the id.
- Settings keys replace the constants:
  - source lists for migration/cleanup (default: watchlist + any chosen personal lists; today `Watch list 2`)
  - Netflix destination list and Prime destination list (drop-down or "Create new list…" with a name box, which the code already supports)
  - lists to leave untouched are simply the ones not selected (today "Test" and "Christmas" are protected only by being unmentioned)
- Operations take list ids as arguments: `WatchlistCleaner`, `PlexMigrate plan`, `remove-lists`, `remove-verified`, `remove-leftovers`, `move-streaming` all read their lists from the selection instead of constants. Backups (`BackupRunner`) already cover every personal list.
- Safety: any screen that will remove from lists shows the exact selected lists in its confirmation.

### 4. Screens for the existing workflows
Each workflow keeps the **dry run → review → `--apply`** rule; the UI makes it a two-button flow and shows a fresh Trakt backup being taken before any Trakt write.

| Screen | Replaces | Review UI |
|---|---|---|
| Dashboard | — | Connection status, last scan/backup time, counts, "run scan" |
| Backup | `TraktBackup` | Run, list previous backups, open folder |
| Plex vs Trakt (scan and push) | `PlexScan`, `push-movies`, `push-episodes` | Gap tables (movies, episodes by show) with checkboxes, push selected |
| Fix unmatched | `PlexFix` | Candidate table with poster/IMDb link, approve per row |
| Prune low-rated movies | `PlexPrune` | Table with size/watched/IMDb link and a Keep toggle; shows total reclaimed; typed confirmation before delete |
| Migrate to Radarr/Sonarr | `PlexMigrate plan/apply/verify`, `remove-*` | Plan table with the Action column editable (add / skip / remove), IMDb links, then Apply, Verify, Remove-from-Trakt as numbered steps |
| Streaming lists | `move-streaming` | Choose destination lists (section 3), preview, apply |
| Reports | `Reports/` folder | History of past runs, open as HTML, export CSV |

The reviewed CSVs become a database/JSON store behind the screens (this is the "rewrite as HTML" roadmap item); keep CSV export so existing reports stay readable. Long operations run as background jobs with a live log pane and a Cancel button; destructive operations (Plex delete, Trakt remove) are never started without a reviewed list from an earlier step, as today.

### 5. Packaging (no installation)
- Publish profile: win-x64, self-contained, single file, **not trimmed** (size is not a concern and it avoids JSON-reflection breakage). Target well under 1 GB; check the published size at the end of the step.
- WebView2 runtime ships with Windows 11 (and updated Windows 10), so the window needs nothing installed; if it is missing the app opens the default browser on the localhost address instead.
- App icon, version stamp, and a log folder under `%APPDATA%\PlexManager\logs`.
- Single-instance guard and a fixed (or auto-picked and remembered) localhost port.
- Update path: replace the exe; settings and data are kept because they live outside it.
- Unsigned exes can trigger a SmartScreen warning on first run ("More info", "Run anyway"); signing is optional and out of scope.

### 5b. Command line (kept, per decision)
The same exe also runs headless: `PlexManager.exe scan`, `migrate plan`, `migrate apply [--apply]`, `trakt remove-verified [--apply]` and so on, one verb per operation, calling the same Core code and using the same settings and data folder as the UI. Same rules as now: dry run by default, `--apply` to act, output to the console, exit code 0 or non-zero. This keeps operations runnable by Claude from a terminal; no further CLI features are planned.

### 6. Migrating this machine's existing data
- One-time import: reads current `plexmanager.config.json`, `trakt.config.json`, `trakt.tokens.json`, `plexmanager.exclusions.json`, caches, `Backups/`, `Reports/` into `%APPDATA%\PlexManager\` so nothing is re-entered.

## Order of work and rough size

| Step | Size | Depends on |
|---|---|---|
| 1 Core extraction | Largest; mostly mechanical | — |
| 2 Settings and wizard | Medium | 1 |
| 3 List picker and de-hard-coding | Small to medium | 1, 2 |
| 4 Screens | Large; build one workflow at a time, starting with Backup and Migrate | 1 to 3 |
| 5 Packaging | Small | 4 |
| 6 Import of existing data | Small | 2 |

Suggested first deliverable (usable on its own): Core + Settings wizard + List picker + Backup screen + CLI verbs for the same, packaged as an exe. Then add one workflow screen at a time.

## Working constraints
- Updated 2026-11-11: this machine is on the LAN and can reach Plex, Radarr and Sonarr (and Trakt), so the wizard's "Test connection" calls and the new clients can be checked against the live services while building. Use read-only calls for checking; anything that writes (Trakt, Plex delete, Radarr/Sonarr add) still goes through the dry-run and `--apply` flow and needs your go-ahead.
- Unit tests are still not run from here (global rule); the user runs them.
- Secrets stay out of git; `%APPDATA%` storage and DPAPI keep them out of the repo by construction.

## Decisions made
1. **UI: web UI in a window** (decided 2026-11-11).
2. **No installation**: one self-contained exe, size limit 1 GB, bloat is acceptable (decided 2026-11-11).
3. **CLI kept** as a thin front end over the same Core so operations can still be run from a terminal; no extra CLI work beyond that (decided 2026-11-11).
4. **Watchlist stays selectable**, shown as "Watchlist (default)" at the top of the list picker (decided 2026-11-11).

## Open questions
None blocking. Still worth deciding before the wizard is built: whether anyone besides you will use it (decides how much hand-holding the Trakt app-creation and plex.tv sign-in steps need).

---

## What was built (2026-11-12)

Implemented in full: phases 1 to 6, plus 5b. Nothing in it has been run — static checks only (it compiles with
no warnings, every front-end module parses, and every JS import resolves to a real export). Running it against
Plex, Trakt, Radarr and Sonarr is yours.

### Shape

```
PlexManager.slnx
  PlexManager.Core      settings, clients, id matching, CSV, every operation; no Console, no args
  PlexManager.App       the exe: CLI + Kestrel on 127.0.0.1 + minimal API + WebView2 window + embedded wwwroot
```

The five console projects were deleted, as the plan's target shape says to do once each verb has moved. Their
code is in git history; their untracked data (`TraktBackup/Backups`, `trakt.config.json`, `trakt.tokens.json`,
`Reports/`) is still on disk, because the one-time import reads it.

### Phase 1, shared Core

- `AppPaths` replaces the walk-up-the-tree file search. `%APPDATA%\PlexManager` by default; a `portable.flag`
  file next to the exe, or `--data <folder>`, keeps everything beside the exe.
- Clients: `PlexClient`, `PlexTvAuth` (the plex.tv PIN flow), `TraktClient`, `TraktAuth`, `ArrClient` (one class
  for Radarr and Sonarr, replacing the calls that were inline in `PlexMigrate`), `OmdbClient`, `TmdbClient`,
  `ShowLanguageLookup`.
- The duplicated helpers are single copies now: `Util/Csv.cs` (was four copies) and `Util/MediaIds.cs` (was four).
- `IOperationLog` and `OperationEvent` replace `Console.WriteLine`; option records and typed arguments replace
  `args`. `OperationContext` hands an operation its settings, paths, run store, log and lazily built clients.
- Every verb is a class: `BackupOperation`, `WatchlistCleanOperation`, `ScanOperation`, `PushMoviesOperation`,
  `PushEpisodesOperation`, `FixCandidatesOperation`, `FixApplyOperation`, `FixDeleteOperation`,
  `PruneCandidatesOperation`, `PruneApplyOperation`, `MigratePlanOperation`, `MigrateApplyOperation`,
  `MigrateVerifyOperation`, `TraktRemoveOperation`, `StreamingMoveOperation`.
- Behaviour was kept as it was, including the odd corners: the pre-approval rules in Fix, the
  re-read-before-delete check in Prune, the fill-and-verify-before-removing order in the streaming move, and
  `smart-lists.json` written last so its presence marks a finished backup.

### Phase 2, settings and wizard

One `settings.json` replaces `plexmanager.config.json`, `trakt.config.json`, `trakt.tokens.json` and
`plexmanager.exclusions.json`. Secrets are DPAPI-encrypted for the current user and written as
`"dpapi:<base64>"`; a plain value is still read and re-encrypted on the next save, which is how the import works.
The browser is only ever sent a hint such as `****1234`, and a field sent back unchanged keeps the stored secret.

Each service has its own host, a **Test connection** button that makes one read-only call, and drop-downs filled
from that call: the Plex movie and TV libraries (whose section numbers were compiled into `PlexScan`), and the
Radarr and Sonarr quality profiles and root folders (the names `SD + 720` and `SD` were constants). Plex has
**Sign in with Plex** (the PIN flow, which also offers the server's address and its own token), Trakt has
**Connect** (device code, with the redirect URI to register shown on screen). TMDB and OMDb are optional, and the
plan now says so in its log instead of failing when a key is missing. The rating threshold, release-year cutoff,
Rotten Tomatoes cutoff, kept original languages, the exclusion lists and the known-title corrections are all
settings.

The Settings screen *is* the wizard; there is no separate one-time flow, because every step has to be reachable
again anyway and a second screen would be the same form twice.

### Phase 3, list selection

`TraktClient.PersonalListsAsync` returns the account's personal lists with the default watchlist pinned first as
"Watchlist (default)". The `listPicker` control in `ui.js` is the reusable one: drop-down, refresh, multi-select,
and "use this name" for a list that does not exist yet. Lists are stored as id, slug and name together, and
resolved by id first and name second, so a rename on Trakt does not break a setting.

The three hard-coded names are gone. `Migration.SourceLists` replaces `"Watch list 2"` as the source,
`Migration.RemoveFromLists` is what a dealt-with title is removed from (the watchlist is always included), and
`Migration.StreamingTargets` pairs a provider with its list, replacing `"Watch on Netflix"` and
`"Watch on Prime"`. A list nobody selects is never touched, which is what used to protect "Test" and "Christmas"
by accident.

### Phase 4, screens

Dashboard, Backup, Plex vs Trakt, Fix unmatched, Prune low-rated, Migrate, Streaming lists, Tidy lists, Reports,
Settings. Each workflow is numbered steps with dry-run and apply buttons; the reviewable ones (Fix's Approve,
Prune's Keep, Migrate's Action) are edited in the table and saved. `RunStore` is the store behind them: every run
is recorded with its rows under `Runs\`, and saving an edit writes the rows back to the same CSV the apply step
reads, so the screens and the CLI cannot disagree. Long operations run as jobs with a live log and a Cancel
button, one at a time. A Plex deletion or a Trakt removal needs the word typed out first.

### Phase 5, packaging

`dotnet publish PlexManager.App -p:PublishProfile=exe -c Release` writes **one** file: `PlexManager.exe`,
130 MB, self-contained, single file, untrimmed. The UI is an embedded resource served through
`ManifestEmbeddedFileProvider`, so nothing sits beside the exe; a Debug build also copies `wwwroot` and the host
prefers that copy, so editing a screen needs no rebuild of the resources. Also in: a single-instance mutex per
data folder (a second launch opens the window that is already up), a remembered loopback port, an app manifest
with per-monitor DPI and long paths, a version stamp, and a `Logs` folder under the data folder.

### Phase 5b, command line

`PlexManager <verb>` runs headless over the same Core, settings and data folder; no verb opens the window.
Every old verb is there, `latest` still resolves through the same pointer files, dry run is still the default and
`--apply` still acts. `PlexManager --help` lists them. `migrate verify` and `prune apply` keep their non-zero
exit codes. Added: `import`, `settings show`, `--data`, `--port`, `--no-window`.

### Phase 6, importing this machine

Settings shows an **Import** card when it finds an existing PlexManager folder (it looks up the tree from the exe
and in the usual development paths). It brings over the service addresses and keys, the Trakt Client ID *and*
tokens (so there is no need to sign in again), the exclusion lists, and optionally the caches, backups and
reports; it also seeds the lists the old tools had hard-coded and the 14 known-title corrections that were a
dictionary inside `PlexFix`. `PlexManager import` does the same from a terminal.

### Departures from the plan

1. **No database.** The plan said the reviewed CSVs "become a database/JSON store"; `RunStore` is a JSON store
   per run, not a database. It is enough for the screens, and it keeps the CSVs authoritative, so nothing had to
   be migrated and the CLI kept working unchanged.
2. **The wizard is the Settings screen**, not a separate first-run flow (see phase 2 above).

### Worth knowing before the first run

- Trakt has to be connected from Settings before any Trakt operation in the window: a job will not run the
  device flow itself, because the code would be buried in a job log. The CLI still does, since the console can
  show it.
- The migrate plan needs a quality profile and a root folder chosen for both Radarr and Sonarr. A stale or unset
  profile now fails with a list of what the server actually has, instead of quietly using the first one.
- The data folder moved. Reports and backups written before the import stay where they are; the import copies
  them in, and the app writes new ones under `%APPDATA%\PlexManager`.
- The exe is unsigned, so SmartScreen may ask on first run ("More info", "Run anyway").

### The open question in this plan

Still open, and still not blocking: whether anyone besides you will use it. It was built as though someone might
(the wizard explains each key, links to where to get it, and tests it), so answering "no" costs nothing.
