# Radarr and Sonarr: adding items and watching the queue

Research date: 2026-10-20. Source of truth is each project's OpenAPI file:

- Radarr: <https://raw.githubusercontent.com/Radarr/Radarr/develop/src/Radarr.Api.V3/openapi.json> (v3.0.0, default `localhost:7878`)
- Sonarr: <https://raw.githubusercontent.com/Sonarr/Sonarr/develop/src/Sonarr.Api.V3/openapi.json> (default `localhost:8989`; the spec says "The v3 API docs apply to both v3 and v4 versions of Sonarr")

Docs sites (radarr.video/docs/api, sonarr.tv/docs/api, wiki.servarr.com) are JS-rendered and unreadable by the fetch tool; the OpenAPI files above are the usable references. Each running instance also serves its own docs, which are the safest check for your installed version. **(Instance docs path unverified; try `/api/v3/docs` or `/swagger` on your instance.)**

Fields marked **(from memory)** or **(unverified)** were not confirmed in the specs. In the specs, `required` arrays are absent; real validation happens in server code, so the minimum bodies below are community-confirmed, not schema-confirmed.

## 1. Basics

- Base path `/api/v3/...`.
- Auth: header `X-Api-Key: <key>` or query `?apikey=<key>`. Prefer the header (query keys leak into logs). Radarr's spec confirms both schemes; Sonarr's spec has no `securitySchemes` section but is believed identical **(unverified)**.
- Key location: Settings → General → Security.
- Reverse-proxy / auth-method settings (Forms, Basic, External) do not affect the API key.
- JSON bodies, `Content-Type: application/json`.
- No documented rate limits.
- No v5 API was seen in either spec as of this research. **(unverified; check GitHub releases)**

## 2. Prerequisites to fetch before adding

| Need | Radarr | Sonarr |
|---|---|---|
| Quality profiles | `GET /qualityprofile` | `GET /qualityprofile` **(not seen in the read part of the spec; widely used, treat as present)** |
| Root folders | `GET /rootfolder` (id, path, accessible, freeSpace) | same |
| Tags | `GET /tag` (id, label); `POST /tag` to create | same |
| Language profiles | n/a | `GET /languageprofile` exists but **every operation is deprecated**; ignore it on Sonarr v4 |
| Status / version | `GET /system/status` | same |
| Health | `GET /health` | **(not seen in read part)** |

The app should cache these and let the user pick defaults (profile, root folder, tags, monitor mode).

## 3. Radarr: add a movie

**Step 1: lookup.** Use the TMDB id from Trakt directly:

- `GET /movie/lookup/tmdb?tmdbId=272`
- `GET /movie/lookup/imdb?imdbId=tt0372784`
- `GET /movie/lookup?term=tmdb:272` (term search)

The lookup returns a full `MovieResource` (title, year, images, titleSlug, tmdbId, ...). The usual pattern is to take that object, set a few fields, and POST it back.

**Step 2: add.** `POST /movie` (returns **201**). Practical fields:

| Field | Notes |
|---|---|
| `tmdbId` | required |
| `title` | required in practice; copy from lookup |
| `qualityProfileId` | required; int from `/qualityprofile` |
| `rootFolderPath` | required (or `path`); must exactly match an existing root folder, otherwise "Invalid Path" error |
| `monitored` | bool |
| `minimumAvailability` | `announced`, `inCinemas`, `released` (`MovieStatusType`; enum values not captured, these are from memory) |
| `tags` | int array of tag ids |
| `addOptions.searchForMovie` | true triggers an immediate search |
| `addOptions.monitor` | `movieOnly`, `movieAndCollection`, `none` |
| `addOptions.addMethod` | `manual`, `list`, `collection` |

Minimal body (community example):

```json
{
  "title": "Batman Begins",
  "tmdbId": 272,
  "qualityProfileId": 1,
  "rootFolderPath": "/data/movies",
  "monitored": true,
  "minimumAvailability": "released",
  "addOptions": { "searchForMovie": true }
}
```

Duplicates: a movie already in the library produces a 400 validation error ("already been added"). Treat as "already there", not a failure. **(behavior from community reports; error text unverified)**

Other movie endpoints: `GET /movie` (library), `GET|PUT|DELETE /movie/{id}`, `PUT|DELETE /movie/editor` (bulk), `POST /movie/import`, `GET /movie/{id}/folder`.

## 4. Sonarr: add a series

**Step 1: lookup.** `GET /series/lookup?term=tvdb:81189` (also accepts a text term; has an optional `language`). Returns `SeriesResource` including `tvdbId`, `title`, `titleSlug`, `images`, `seasons`.

**Step 2: add.** `POST /series` (returns **200** in the spec, community reports 201; handle both). Fields:

| Field | Notes |
|---|---|
| `tvdbId` | required |
| `title` | required in practice |
| `qualityProfileId` | required |
| `rootFolderPath` | required; must match a configured root folder |
| `monitored` | bool |
| `seasonFolder` | bool |
| `seriesType` | `standard`, `daily`, `anime` **(from memory)** |
| `tags` | int array |
| `seasons` | array of `{seasonNumber, monitored}`; copy from lookup |
| `addOptions.monitor` | one of: `unknown`, `all`, `future`, `missing`, `existing`, `firstSeason`, `lastSeason`, `latestSeason`, `pilot`, `recent`, `monitorSpecials`, `unmonitorSpecials`, `none`, `skip` (from the spec) |
| `addOptions.searchForMissingEpisodes` | bool |
| `addOptions.searchForCutoffUnmetEpisodes` | bool |
| `addOptions.ignoreEpisodesWithFiles` / `ignoreEpisodesWithoutFiles` | bool |

Gotcha from the Sonarr forum: `addOptions.monitor` must be the string (e.g. `"all"`), not a boolean, and must be inside `addOptions`. A plain `GET` with `?tvdbId=` on `/series` only lists library entries and never adds.

Duplicates return 400 with a "series already added" style validator error. **(from community reports)**

Other series endpoints: `GET /series` (optional `tvdbId` filter, handy for "is it already in Sonarr?"), `GET|PUT|DELETE /series/{id}`, `PUT|DELETE /series/editor`, `POST /series/import`, `POST /seasonpass`, `GET /episode`, `PUT /episode/monitor`.

## 5. Triggering a search

`POST /command` (Radarr returns 201, Sonarr 200). `CommandResource` fields: `name`, `status` (queued, started, completed, failed, aborted, cancelled, orphaned), etc. Search commands (`MoviesSearch` with `movieIds`, `SeriesSearch` with `seriesId`) are **from memory**, not confirmed in the spec; check on your instance. Setting `addOptions.searchForMovie` / `searchForMissingEpisodes` on add avoids needing this.

## 6. Reading the queue and state

| Need | Endpoint |
|---|---|
| Active downloads | `GET /queue` (paged: `page`, `pageSize`, `sortKey`, `sortDirection`; filters `protocol`, `languages`, `quality`, `status`; Radarr `includeMovie`, `movieIds`; Sonarr `includeSeries`, `includeEpisode`, `seriesIds`) |
| Lighter queue view | `GET /queue/details` |
| Queue summary counts | `GET /queue/status` (totalCount, count, unknownCount, errors, warnings, ...) |
| Remove from queue | `DELETE /queue/{id}` (`removeFromClient` default true, `blocklist`, `skipRedownload`, `changeCategory`), `DELETE /queue/bulk` |
| Force grab | `POST /queue/grab/{id}`, `POST /queue/grab/bulk` |
| History | `GET /history`, `GET /history/since`, `GET /history/movie` (Radarr), `POST /history/failed/{id}` |
| Upcoming | `GET /calendar?start=&end=&unmonitored=` (Radarr returns movies, Sonarr episodes); iCal feed `/feed/v3/calendar/radarr.ics` |
| Command progress | `GET /command`, `GET /command/{id}` |
| Backups, restart | `/system/backup`, `/system/restart` (not needed) |

Polling is the only option via REST for the queue. Both apps also support **webhooks** (Settings → Connect) for grab/import/health events, which could push updates to the app instead of polling **(not researched; mentioned for completeness)**. Radarr/Sonarr also expose a SignalR stream used by their own UI **(unverified)**.

## 7. Import lists: the built-in alternative

Both apps can poll lists themselves, which may remove the need to push anything from the app:

- Settings → Import Lists → `+` → Trakt providers. Trakt requires authenticating with your Trakt account through the Servarr auth flow (done in the Radarr/Sonarr UI).
- Radarr's wiki: lists follow a creator and add what is on the list; can set quality profile, "add automatically" and "monitor". "List Update Interval" is configurable per provider.
- Radarr has **Clean Library Level**: Disabled (recommended), Log Only, Keep and Unmonitor Movie, Remove Movie and Keep Files, Remove Movie and Delete Files (dangerous). Sonarr equivalents: Disabled, Log Only, Keep and Unmonitor Series, Keep and Tag Series.
- **List Exclusions** (`/exclusions` in Radarr, importlistexclusion) stop a removed title from being re-added.
- Wiki warning: review a list before saving; do not enable "Search on add" until the list is right.
- Which Trakt list types exist (watchlist, custom user list, popular, trending, ...) was not extractable from the wiki text. `GET /importlist/schema` on your own instance lists every provider and its fields. Older forum reports (2021) said Sonarr could not import the default Trakt watchlist, only custom lists; this may be fixed. **Check in your instance.**
- Import-list API: `GET|POST /importlist`, `GET|PUT|DELETE /importlist/{id}`, `/importlist/schema`, `/importlist/test`. In theory the app could create/configure these via API instead of pushing movies one by one.

Design implication: there are two ways to get "Trakt list → Radarr/Sonarr":

1. **Pull (built-in):** point Radarr/Sonarr import lists at a Trakt list. No code. Limits: you need an item to be on a Trakt list; no per-item quality/folder override; no UI for review. Works with Trakt's own OAuth in the Servarr apps, so it does not need your own API app, but it counts toward the free account's one-community-app limit **(inference; not verified)**.
2. **Push (your app):** the app reads Trakt and calls `POST /movie` / `POST /series` itself, with full control (preview, per-item profile, dedupe, skip already-in-library, exclusions). Needs your own Trakt API app (VIP).

## 8. Gotchas

- Root folder strings must match exactly, including slashes and case; for Docker installs the path is the container path, not the host path.
- Sonarr's language profile is deprecated; do not require it.
- Remember to dedupe: check `GET /series?tvdbId=` and the `/movie` library (or `GET /movie?tmdbId=`) before POSTing, and treat 400 duplicate errors as informational.
- Trakt shows without a `tvdb` id cannot be added to Sonarr directly; look up by IMDB or title with `GET /series/lookup?term=imdb:tt...` and verify.
- Movies without a `tmdb` id cannot be looked up by tmdb; fall back to `imdb:`.
- Both apps are commonly behind a reverse proxy or on a LAN; because of the user's environment (no VPN while using Claude), runtime testing against the real instances has to be done by the user.
