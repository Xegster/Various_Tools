# Prior art and client libraries

Research date: 2026-10-20. Version/update data was read from NuGet and GitHub pages. Where a page did not show a date, that is stated.

## 1. Existing tools that overlap with this idea

| Tool | What it does | Edits Trakt lists? | Pushes to Radarr/Sonarr? | Status |
|---|---|---|---|---|
| **Radarr / Sonarr built-in Trakt import lists** | Poll Trakt lists and add items | No | Yes (pull) | Maintained with the apps. See `02-radarr-sonarr-api.md` §7. |
| **[Traktarr](https://github.com/l3uddz/traktarr)** (Python, GPL-3.0, 624 stars) | Reads Trakt trending/popular/anticipated/boxoffice/watched/played, public lists, watchlists, private lists (per-user OAuth) and adds to Sonarr/Radarr with filters (country, language, genre, runtime, year, networks, blacklists, Rotten Tomatoes via OMDb). Notifications via Slack/Pushover/Apprise. | No | Yes (push) | Not archived, but no visible last-commit date, and Docker image reported as 4+ years old. Likely stale. Uses Trakt's older app model. |
| **[MDBList](https://mdblist.com)** (hosted, closed source; [docs](https://docs.mdblist.com)) | Dynamic filter lists, import of Trakt/IMDb lists, syncs watchlist, ratings, progress with Trakt. Radarr/Sonarr import it via Custom Lists URL. API at `https://api.mdblist.com`. Free key = 1,000 requests/day. | Not confirmed | Yes via list URL (pull) | Active. Could not confirm whether its API edits Trakt lists. |
| **[PlexTraktSync](https://github.com/Taxel/PlexTraktSync)** (Python, MIT, 2.2k stars) | Two-way sync Plex ↔ Trakt: collection, ratings, watched, liked lists, watchlists. Device-flow auth, live `watch` scrobbling. No Plex Pass or Trakt VIP needed. | Watchlist sync only; not general list editing | No | Active (contributor-maintained). |
| **[trakt-scrobbler](https://github.com/iamkroot/trakt-scrobbler)** (GPL-2.0) | Scrobbles playback from VLC, Plex, MPV, MPC-HC to Trakt | No | No | Not relevant to this goal. |
| **[traktexport](https://github.com/purarue/traktexport)** (Python, MIT) | Read-only dump of all Trakt data to JSON | No | No | Small project; good for backups. |
| **[Kometa](https://github.com/Kometa-Team/Kometa)** (formerly Plex Meta Manager, MIT, 3.5k stars) | Builds Plex collections/overlays; can add missing items to Radarr/Sonarr. | Not confirmed | Yes (missing items) | Very active. Trakt builder not confirmed on the README page read (TMDb, IMDb, MDBList are). |
| **[Seerr](https://github.com/seerr-team/seerr)** (MIT, 12.9k stars) | Request manager for Plex/Jellyfin/Emby; pushes to Radarr/Sonarr. Has a local watchlist feature. No Trakt on the README. | No | Yes | Very active. Successor to Overseerr (**archived 2026-02-15**) and Jellyseerr. |
| **Sharr** (Docker, thom1606/sharr) | Exposes a Plex watchlist as a list that Radarr/Sonarr can read | No | Yes (pull) | Mentioned in search results only; not read. **(unverified)** |
| Simkl, Listrr | Not retrievable (502, 404 during research). | ? | ? | Unknown. |

**Takeaway:** nothing found does all three of (a) edit Trakt lists, (b) show a review UI, (c) push to Radarr/Sonarr from one place. The gap is real but narrow. Anything that only needs Trakt → Radarr/Sonarr is already solved by import lists.

## 2. .NET client libraries

### Trakt

| Package | Status | Notes |
|---|---|---|
| **[Trakt.NET.Ex](https://www.nuget.org/packages/Trakt.NET.Ex)** | **2.0.0 stable, 2026-09-09.** MIT. Single maintainer (José Fariña). Only ~975 total downloads. | Fork of Henrik Fröhling's library. net5.0–net10.0 + netstandard2.0/2.1. System.Text.Json source-gen, AOT/trim friendly. Modules: Auth (OAuth + device flow), Sync, Lists, Users, Search by IMDb/TMDB/TVDB, Scrobble, Calendar, Recommendations, SmartLists, Notes, SocialRecommendations. Companion `Trakt.NET.Ex.HttpClientFactory`. Source: <https://github.com/josefarinna/Trakt.NET>, docs: <https://josefarinna.github.io/Trakt.NET>. |
| [henrikfroehling/Trakt.NET](https://github.com/henrikfroehling/Trakt.NET) (`Trakt.NET` 1.4.0) | **Archived 2025-03-16.** | Dead upstream. Avoid. |
| Hand-rolled `HttpClient` | Option | The API is plain REST/JSON. Needed endpoints for this project are ~15. A typed client with `System.Text.Json` is small, and avoids dependence on a one-person fork. |

No other .NET Trakt libraries were searched.

### Radarr / Sonarr

| Package | Status | Notes |
|---|---|---|
| **Soenneker.Radarr.OpenApiClient** 4.0.62 (updated 2026-10-18), plus `.OpenApiClientUtil` and `.HttpClients` | Active, auto-regenerated near daily | Kiota-generated from the OpenAPI spec. **.NET 10 only.** License not checked. |
| **Soenneker.Sonarr.OpenApiClient** 4.0.59 (updated 2026-10-15), plus util and HttpClients | Active | Same pattern. |
| RadarrSharp 1.0.31 (2019), RadarrApiWrapper (2022), SonarrSharp 1.2.1 (2018), APIv3SonarrDotcore (2023) | Stale | Avoid. |
| Own client generated with **Kiota** or **NSwag** from the official `openapi.json` files | Option | Same technique Soenneker uses, but you control version and target framework. Spec URLs are in `02-radarr-sonarr-api.md`. |
| Hand-rolled `HttpClient` | Option | Needed surface is ~12 endpoints: lookup, add, root folders, quality profiles, tags, queue, queue/status, calendar, command, history, system/status. |

Reference (non-.NET): Go [`golift/starr`](https://github.com/golift/starr) (MIT, covers Radarr/Sonarr/Lidarr/Prowlarr/Readarr; used by Notifiarr) and Python [`totaldebug/pyarr`](https://github.com/totaldebug/pyarr) (v5.0.0; license inconsistent between GitHub panel (MIT) and README (CC BY-NC-SA 4.0), so do not copy code from it).

## 3. Plex relevance

The folder is named PlexManager, so a few points worth deciding up front:

- Plex's **Discover watchlist** is a separate source. Research did not fetch Plex docs. Known options **(from memory, unverified)**: the Plex Watchlist RSS feed (Plex Pass needed for the RSS URL), the `discover.provider.plex.tv` API using the user's Plex token, and the `plexapi` Python library as a reference implementation.
- Seerr and Sharr already bridge Plex watchlist → Radarr/Sonarr.
- Trakt has Plex-specific endpoints (`/users/settings/plex/*`, `/users/syncs`) for Trakt's own Plex integration (connect, list servers, sync now). These are exposed to apps via the OAuth scopes and could be read, but are mostly VIP features (the Plex webhook URL is null unless VIP).
- Deciding whether Plex itself (library, watched state, collections) is in scope is an open question; see `04-options-and-recommendation.md`.
