# PlexManager roadmap

Goal: make Trakt, Plex, and Radarr/Sonarr agree with each other, then retire the Trakt backlog.

Plex runs on another PC on the LAN (Plex Lifetime Pass). Trakt is the account being reconciled. Radarr/Sonarr are the destination for the backlog.

| # | Phase | Status |
|---|---|---|
| 0 | Local copy of Trakt | Done |
| 1 | Plex "watched" is also "watched" in Trakt | Done 2026-11-04: movies and episodes pushed; final scan clean apart from known exceptions; Trakt's Plex sync stays on and PlexScan is the checker |
| 2 | Delete Plex movies rated below 7/10 | Done 2026-11-04: 53 deleted from Plex (about 54 GB), 52 removed from Radarr with exclusions, 13 kept (`Reports/prune-delete-20261104-203417.csv`) |
| 3 | Move Trakt movies and series into Radarr/Sonarr, then remove them from Trakt | Not started |

Phases 1 to 3 run in order. Each later phase relies on the earlier one being trusted: phase 2 deletes files, and phase 3 deletes Trakt data, so neither should run on top of an unverified sync.

---

## Phase 0: Local copy of Trakt (complete)

Built as the `TraktBackup` console app in [TraktBackup/](TraktBackup/) (.NET, `HttpClient`, hand-written Trakt client). Research behind it is in docs `01` to `04`.

What exists:

- **Auth**: device-code OAuth against your own Trakt API app ([TraktAuth.cs](TraktBackup/TraktAuth.cs)). Config in `trakt.config.json`, tokens in `trakt.tokens.json`; both are git-ignored. See [trakt.config.example.json](TraktBackup/trakt.config.example.json).
- **Client**: paged GETs with rate-limit handling ([TraktClient.cs](TraktBackup/TraktClient.cs)).
- **Full backup** (`TraktBackup [output-root]`, [BackupRunner.cs](TraktBackup/BackupRunner.cs)): writes a timestamped folder under `Backups/` containing
  - account settings and `last_activities`
  - watched movies and shows (with every watched season and episode), full history
  - ratings for movies, shows, seasons, and episodes
  - watchlist, favorites, collection (movies and shows)
  - every personal list (`lists/<slug>.json`), liked lists, collaborations, smart lists
  - `titles.csv`, a flat file of ids and titles
- **Watchlist cleanup** (`TraktBackup clean [--apply]`, [WatchlistCleaner.cs](TraktBackup/WatchlistCleaner.cs)): dry run by default. Removes watched movies, and fully watched ended or canceled shows, from the watchlist and the overflow list "Watch list 2". `--apply` takes a backup first, then saves a new timestamped copy of the lists under `Current/<timestamp>/`.
- **Local copy of record**: `Backups/` (history) and `Current/<timestamp>/` (watchlist copies from each cleanup). Both are git-ignored because they hold personal data.

Backups from 2026-10-29 exist (three runs), so the full export path has been exercised against the real account.

Known gaps carried forward:

- The backup is a snapshot, not an incremental sync. `last_activities.json` is saved, but nothing uses it to fetch deltas yet.
- No local database. Phases 1 to 3 need to join Trakt, Plex, and Radarr/Sonarr data by id, so a queryable store (SQLite) or at least a shared id-matching module is the first piece of new work.

---

## Phase 1: Everything watched in Plex is watched in Trakt

**Why it needs work:** Trakt syncing (presumably the Plex-to-Trakt scrobbler/sync you have now) is unreliable, so Trakt's watched state cannot be assumed to match Plex. The first step is to measure the gap, not fix it blindly.

### Services on the LAN (192.168.1.12)

Reachability checked 2026-10-31 (no credentials needed for these endpoints):

| Service | Address | Result |
|---|---|---|
| Plex | `http://192.168.1.12:32400` | Responds, server is claimed, version 1.31.3.6868 |
| Sonarr | `http://192.168.1.12:8989` | Responds, version 3.0.10.1567 (v3 API) |
| Radarr | `http://192.168.1.12:7878` | Responds, status OK |

API keys for Sonarr and Radarr are in place and verified with read-only calls (2026-10-31): Sonarr 3.0.10 with root folder `F:\Plex Media\TV`, Radarr 5.27.5 with root folder `F:\Plex Media\Movies`, about 436 GB free on that drive. The Plex token is in place.

Plex libraries (read 2026-10-31): `Movies` (id 4, movie), `TV Shows` (id 1, show), `Adult` (id 8, movie, hidden, no metadata agent), `Other Videos` (id 5, no agent), plus Music, Audiobooks, and Photos. Only `Movies` and `TV Shows` carry tmdb/tvdb ids, so those are the libraries in scope for matching to Trakt. The server also lists other accounts besides yours (owner "Xegster"); the owner token reads only your watch state.

Credentials go in `plexmanager.config.json` (git-ignored); copy [plexmanager.config.example.json](plexmanager.config.example.json) to create it. Sonarr and Radarr API keys are under Settings, General, Security.

### 1a. Read Plex
- Auth to the Plex server over the LAN with an `X-Plex-Token`. Store it in a git-ignored config next to `trakt.config.json`.
- List libraries (`/library/sections`), then every movie and show with `viewCount`, `lastViewedAt`, `userRating`, and external ids (`guid` entries for imdb, tmdb, tvdb).
- For shows, read per-episode watch state (`/library/metadata/{id}/allLeaves`), since Trakt tracks episodes.
- You are the only user on the server, so there is one watch history to read (the server owner's).
- Not yet researched: Plex's API (this folder's docs cover Trakt and Radarr/Sonarr only). A short research doc, `05-plex-api.md`, comes first.

### 1b. Match Plex to Trakt
- Match by external id (tmdb/tvdb/imdb), never by title. Report items with no usable id or with ids that disagree.
- Reuse `watched-movies.json` and `watched-shows.json` from the latest backup as the Trakt side.

### 1c. Report the gap (dry run)
- Output a report of: watched in Plex but not in Trakt (movies, and episodes by show), watched in Trakt but not in Plex (informational only, no action), and unmatched items.
- Run the report before every change. Review it by hand the first time.

**Result of the first gap report (2026-10-31, against the 2026-10-29 backup)**, built as [PlexScan](PlexScan/) (`dotnet run --project PlexScan`, read-only, writes CSVs to a new `Reports/scan-<timestamp>/` folder each run and records it in `Reports/latest.txt`; `TraktBackup push-movies latest` and `push-episodes latest` use that folder). API notes are in [05-plex-api.md](05-plex-api.md).

| | Count |
|---|---|
| Movies watched in Plex | 627 |
| ...already on Trakt | 480 |
| ...**missing from Trakt** | **144** |
| Plex movies with no usable id | 15 (mostly files with no metadata match) |
| Episodes watched in Plex | 3447 |
| ...already on Trakt | 2041 |
| ...missing, show known to Trakt | 674 across 23 shows |
| ...missing, show not in Trakt at all | 732 across 33 shows |

So Trakt's built-in sync is not catching everything: about a quarter of Plex-watched movies and a third of watched episodes are not on Trakt. Before pushing, check for false gaps. The biggest "missing" shows are long-running ones (The Simpsons 263, Family Guy 103, American Dad 74), where Plex and Trakt may number episodes differently, so some of those may be mismatches rather than unwatched episodes.

Findings that affect the design:
- `watched-shows.json` in the backup has **no season or episode detail** (the earlier description of the backup was wrong on that point). The scan uses `history.json` for Trakt episodes. The backup should be fixed or documented.
- Matching works across both Plex id generations (see 05).

**Movies pushed 2026-10-31:** `TraktBackup push-movies <csv> [--apply]` added 140 of the 144 movies to Trakt history (one play each, dated from Plex), after a fresh backup. Not found on Trakt: "Are You Afraid of the Dark? Part 1/2/3" (one shared imdb id, a single TV-movie entry in Plex split into three files) and "Obsession" (2026, imdb tt39365308, probably not on Trakt yet). The push log is in `Reports/`. Episodes are not yet pushed.

**Episodes pushed 2026-11-03:** 1102 of 1110 episode plays added, after a fresh backup (`Backups/trakt-20261103-164713`). No shows were missing on Trakt; 8 episodes were not added because Trakt did not recognise them (numbering differs; Trakt's reply echoes whole shows, so which episodes is not yet identified; the log is `Reports/trakt-episode-push-20261103-165003.csv`). Re-scan (`Reports/scan-20261103-175113`) found 11 episodes left: the 8 rejected ones (Henry Danger S02E19 and S03E20, Lost S04E14 and S06E18, South Park S27E06, Swarm S01E08, Sarah Jane Adventures S01E00, Doctor Who (2005) S14E02; mostly merged finales and specials) are now in `excludedEpisodes` in [plexmanager.exclusions.json](plexmanager.exclusions.json), and 3 Sabrina episodes watched on 2026-11-03 are genuinely new. No show is missing from Trakt any more.

How the push works: `TraktBackup push-episodes <csv> [--apply]` (built 2026-10-31) ([EpisodeHistoryPusher.cs](TraktBackup/EpisodeHistoryPusher.cs)) sends every missing episode, including shows that are not on Trakt yet (Trakt creates their history). It skips episodes already in live Trakt history, sends whole shows per request by tvdb id (tmdb, then imdb as fallbacks), and logs to `Reports/trakt-episode-push-*.csv`. Shows you chose not to log are listed in [plexmanager.exclusions.json](plexmanager.exclusions.json) and PlexScan drops them from every report: Keeping Up with the Kardashians, Dance Moms, America's Next Top Model, Toddlers & Tiaras. Add titles there to exclude more.

**Unmatched Plex movies (15):** [PlexFix](PlexFix/) fixes these in two reviewed steps. `PlexFix candidates` asks Plex for match candidates (read-only) and writes `Reports/fix-<timestamp>/plex-match-candidates.csv` with the clear-cut ones pre-approved; `PlexFix apply latest [--apply]` matches the rows marked `yes`. Untested against the live server. The 14 keepers have their real titles and years built in (`Known.Expected` in PlexFix/Program.cs), so exact matches come pre-approved. `PlexFix delete <file-path-fragment> [--apply]` removes one movie and its file (refuses unless Plex's "Allow media deletion" is on); it is for "The Nun German DL". **Done 2026-11-03:** all 14 matched in Plex (log `Reports/plex-match-20261103-193911.csv`) and The Nun German DL deleted. Next: re-run PlexScan and push whatever now shows as watched.

### 1d. Push the missing watches to Trakt (done: movies 2026-10-31, episodes 2026-11-03)
- `POST /sync/history` with `watched_at` taken from Plex `lastViewedAt` so the date is true, not "now". Batch the writes; Trakt allows 1 write call per second.
- Never remove Trakt history in this phase. Only add.
- Take a backup first (the `Backup` path already exists) and re-run the report afterwards. The target is zero "watched in Plex, not in Trakt".

### 1e. Stop the drift (decided 2026-11-03: keep Trakt's built-in Plex sync on; this tool is the checker, run PlexScan periodically and push any gap)
- You don't know whether the sync offered in Trakt's settings (the Plex connection) works. The gap report from 1c answers that: if it shows many Plex-watched items missing from Trakt, the built-in sync is not working. Likely causes to check: unmatched ids, partially watched shows, rewatches.
- Then choose: turn the built-in sync off and let this tool be the periodic reconciler, or keep it and use this tool only as a checker. Don't run both writing to Trakt at once without knowing what each does.

**Done when:** a fresh Plex-vs-Trakt report shows no Plex-watched item missing from Trakt, and the cause of drift is known.

**Closed 2026-11-04** (`Reports/scan-20261104-205514`). What remains is expected: 4 movies Trakt cannot take (three "Are You Afraid of the Dark?" parts that share one entry, and Obsession, not on Trakt yet), 8 episodes Trakt does not recognise (excluded in `plexmanager.exclusions.json`), and episodes watched hours earlier that Trakt's own sync had not yet caught. No movie or show is missing for want of an id. Going forward, run PlexScan now and then and push any gap.

---

## Phase 2: Delete Plex movies rated below 7/10

**Decisions made:**
- **Rating source is Trakt.** All ratings were made in Trakt (`ratings-movies.json`, 1 to 10 scale). "Below 7/10" means Trakt rating < 7. Any Plex ratings are ignored.
- Unrated movies are **never** deleted. Only an explicit Trakt rating below 7 qualifies.
- **Delete from disk**, not just from the Plex library. Because the file is removed, Radarr must also drop the movie (or it will grab it again); see step 5.
- Because Trakt is the rating source, **Trakt ratings must stay intact** through Phase 3. They are the permanent record that stops these movies being re-added.

### Steps
1. **List candidates**: Trakt-rated movies with rating < 7 that exist in Plex (matched by tmdb/imdb id), with title, year, rating, file path, and size. Built as [PlexPrune](PlexPrune/): `PlexPrune candidates` (read-only) writes `Reports/prune-<timestamp>/prune-candidates.csv` with an IMDb link, whether it is watched in Plex, size, and a blank `Keep` column. Run, and keepers marked. `PlexPrune apply latest [--apply]` does steps 3 to 6: dry run, then per-movie delete from Plex, removal from Radarr with an import exclusion, and a log in `Reports/prune-delete-<timestamp>.csv`. Each item is re-checked against the listed file path before it is deleted.
2. **Review**: write the list to a CSV and let you strike out any keepers. The CSV is the approved set.
3. **Dry run** the delete against the approved CSV and show total count and disk space reclaimed.
4. **Delete** only with `--apply`, one item at a time, logging each result. Use Plex's delete endpoint (`DELETE /library/metadata/{id}`), which removes the file only if the server's "Allow media deletion" setting is on. Media deletion is confirmed on in Plex; confirm the file is gone after each delete.
5. **Radarr cleanup**: for each deleted movie that is in Radarr, remove it (or set unmonitored) so it is not downloaded again. Consider adding it to Radarr's import-list exclusions.
6. **Keep a record** of what was deleted (log file, git-ignored) so a mistake can be re-added later.

**Done when:** no rated-below-7 movie remains in Plex, the deletions are logged, and Radarr will not re-download them.

---

## Phase 3: Trakt to Radarr/Sonarr, then clear Trakt

**Defaults decided 2026-11-04:**
- Quality profile: the custom `SD + 720` profile in Radarr and `SD` in Sonarr (looked up by name; the tool stops if it is not found).
- Movies: **monitored and searched on add** only if released since 2025-01-01 (including not yet released), or if Rotten Tomatoes is 80% or higher (scores come from OMDb by IMDb id; Trakt's API has none). Every other movie is added **unmonitored, no search**.
- Series: same rule as movies (first aired since 2025-01-01, or Rotten Tomatoes 80% or higher, from OMDb). Those are monitored for the whole series, all seasons, and searched on add; the rest are added unmonitored.
- Built as [PlexMigrate](PlexMigrate/): `PlexMigrate plan` (read-only) writes `Reports/migrate-<timestamp>/migrate-plan.csv`. Not yet run.

Targets: every movie and series in the Trakt account (watchlist, personal lists, and any other category you decide counts) is added to Radarr (movies) or Sonarr (series). After all are confirmed present, remove them from Trakt.

This phase deletes Trakt data, so the order matters and nothing is removed until everything is verified.

### 3a. Build the inventory
- Scope (confirmed): the Trakt **watchlist** and the custom list **"Watch list 2"**. These are the only two collections involved. Your other lists ("Test", "Christmas") are not touched. History, ratings, favorites, and collection are not touched either (history and ratings are the permanent record, and Phase 2 depends on the ratings).
- Exclude anything with a Trakt rating below 7 (you've judged it not worth having) and anything already watched.
- Dedupe by Trakt/tmdb/tvdb id. Split movies from shows.

### 3b. Connect to Radarr and Sonarr
- Config for base URL and API key (git-ignored). You plan to give me access to your Sonarr instance over the LAN; Radarr gets the same treatment. Read root folders, quality profiles, and tags first. Confirm versions and the real API spec on your instances (open items in `04` §6).

### 3c. Add (dry run first)
- Movies: lookup by `tmdb:` id, `POST /api/v3/movie`. Shows: lookup by `tvdb:` id, `POST /api/v3/series`.
- Report the plan before sending: new adds, already present, and unmatched (movies with no tmdb id, shows with no tvdb id).
- Decide defaults: root folder, quality profile, monitored state, and whether to search immediately. For a large backlog, adding with search turned **off** or **on** has big bandwidth implications. Decide deliberately.
- Skip titles you already own (present in Plex). Send only new ones.

### 3d. Verify
- Re-read Radarr and Sonarr. Every inventory item must be either present in Radarr/Sonarr, already in Plex, or on an explicit exception list (unmatched ids). Produce the exception list for manual handling.

### 3e. Remove from Trakt
- Only items verified in 3d. Use the Trakt remove endpoints (`/sync/watchlist/remove`, `/users/me/lists/{id}/items/remove`) in batches at 1 write per second.
- Take a fresh backup immediately before. Do not touch history, ratings, or collection unless explicitly chosen in 3a.
- Existing helpers: `TraktClient` already handles auth and paging; add write methods with the same rate-limit handling.

**Done when:** every in-scope Trakt item is in Radarr/Sonarr (or on the exception list), and the in-scope Trakt lists are empty.

---

## Cross-cutting rules

- **Dry run by default**, `--apply` to act. This is the convention `TraktBackup clean` already uses; keep it.
- **Back up Trakt before any write.** The `Backup` path exists; call it first.
- **Match by id, not title.**
- **Secrets stay out of git**: Plex token, Radarr/Sonarr keys, Trakt config and tokens. Extend `.gitignore` as each config file is added.
- **Destructive steps (Plex delete, Trakt remove) need a reviewed list** produced by an earlier step, not a live query.
- **Rate limits**: Trakt writes at most 1 per second.
- **Testing**: runtime verification against live Trakt, Plex, and the *arr apps is run by you on the LAN. Code is written and compiled here; it is not run here.

## Suggested structure as the code grows

Today `TraktBackup` is the only project. The new work spans three services, so consider renaming or adding projects once Phase 1 starts:

- `PlexManager.Core`: id matching, local store, models
- Clients: `TraktClient` (exists), `PlexClient`, `RadarrClient`, `SonarrClient`
- One CLI entry point with verbs, for example `backup`, `clean`, `plex-sync`, `prune-rated`, `migrate`

## Open questions

| Question | Needed for |
|---|---|
| What does the Trakt-side Plex sync actually do, and does it work? | Phase 1 (answered by the gap report) |
| Is "Allow media deletion" enabled on the Plex server? | Phase 2 |
| Radarr/Sonarr root folder, profile, monitored, and search-on-add defaults? | Phase 3 |
| Disk and bandwidth capacity for the full backlog | Phase 3 |
