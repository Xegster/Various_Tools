# Trakt: getting data out and modifying lists

Research date: 2026-10-20. Sources are the official `trakt/trakt-api` GitHub repo (docs guides + ts-rest endpoint contracts, `master` branch), the Trakt forum, and GitHub issues. Items marked **(unverified)** came from a single secondary source or from memory.

## 1. The big gate: you probably need Trakt VIP to build this

Everything that follows assumes you have an API app (a `client_id`). That is now the hard part.

| Date | Change | Source |
|---|---|---|
| 2026-02-27 | New limits announced. Free watchlist/list items raised from 100 to 250 immediately. Other limits roll out later. | [Trakt forum: Updating Trakt Limits for 2026](https://forums.trakt.tv/t/updating-trakt-limits-for-2026/101592) |
| 2026-07-22 | Free accounts can connect only **one** third-party "community app" at a time. Official Trakt apps do not count. | [TROYPOINT summary](https://troypoint.com/trakt-limits-users-to-one-connection/) (links the official forum post, not read) |
| ~2026-07-31 | Existing API apps owned by free accounts were disabled. **Creating an API app now requires active VIP.** A Trakt contributor confirmed this on 2026-08-07. Staff reportedly called it a "temporary anti-abuse measure". | [trakt-api issue #902](https://github.com/trakt/trakt-api/issues/902), [tuliprox#853](https://github.com/euzu/tuliprox/issues/853) |

Consequences for this project:

- **Reading public data also needs a `client_id`** (the `trakt-api-key` header). Making a list public does not avoid this.
- Per the Trakt guides, creating an app also "requires a verified GitHub account" (create-an-app guide, updated 2026-10-01). The guide does not mention VIP; the issue #902 reply does. Trust the reply: it is newer and from a maintainer.
- Unknown: what happens to an app if VIP later lapses. The reporter asked; the reply did not answer.
- Unknown: whether an app created while VIP and then used by a free account keeps working. Assume the app owner and the user are the same person (you), so VIP covers both.
- Trakt also sells a separate **Premium developer plan** (higher rate limits, Limited Access endpoints, exempt from the community-app limit). That is not the same as VIP and is not needed for a personal tool.
- VIP price reported as about $4-6/month, or $60/year **(unverified, secondary sources)**.

### Account limits (what you can store on Trakt)

From the Feb 2026 forum post. Some are "not enforced yet"; check `GET /users/settings` → `limits` for your account's real numbers.

| Item | Free | VIP |
|---|---|---|
| Watchlist items | 250 | 5,000 |
| Items per personal list | 250 | 5,000 |
| Total list items (all personal lists) | 1,000 | 100,000 |
| Personal lists | 5 | 100 |
| Dynamic (filter) lists | 5 | 100 |
| Watched history | 100K | 100K |
| Ratings | 10K | 20K |

Exceeding a limit returns HTTP **420** (Account Limit Exceeded); retrying will not help. VIP-only methods return **426** with an `X-Upgrade-URL` header. If your lists are big, VIP is the only way to keep them on Trakt anyway.

## 2. API basics

- Base URL: `https://api.trakt.tv` (standard apps). Premium developer apps use `https://apiz.trakt.tv`.
- OAuth host: `https://auth.trakt.tv`.
- Developer portal: <https://developer.trakt.tv/> (JS-rendered). App management: <https://app.trakt.tv/settings/apps/api>.
- Required headers on every call:
  - `Content-Type: application/json`
  - `User-Agent: MyApp/1.0.0`
  - `trakt-api-key: <client_id>`
  - `trakt-api-version: 2`
  - `Authorization: Bearer <access_token>` for OAuth endpoints. All POST/PUT/DELETE require it.
- Ids on media objects: movie `{trakt, slug, imdb, tmdb}`; show `{trakt, slug, imdb, tmdb, tvdb}`; episode `{trakt, tvdb, tmdb, imdb}`.
  - **Radarr wants `tmdbId`, Sonarr wants `tvdbId`.** Trakt supplies both for most titles, so the join is easy. Missing-id titles need a fallback (see the Radarr/Sonarr doc).
- Pagination: `?page=&limit=` (max usually 250, clamped). Response headers `X-Pagination-Page`, `-Limit`, `-Page-Count`, `-Item-Count`.
- `?extended=full` for full metadata, `images` for artwork.
- Metadata inside list/sync responses is a cached snapshot and may lag. The docs recommend: take ids and user state from sync/list endpoints, hydrate metadata from `/movies/{id}?extended=full` or `/shows/{id}?extended=full`.

### Rate limits (standard apps, rate-limiting guide updated 2026-10-13)

| Scope | Limit |
|---|---|
| Authenticated POST/PUT/DELETE | **1 call per second** |
| Authenticated GET | 500 per 5 minutes |
| Unauthenticated GET (client id + IP) | 500 per 5 minutes |

429 responses carry `Retry-After` (seconds) and sometimes an `X-Ratelimit` JSON header; some security-layer 429s carry neither. Practical effect: bulk writes must be batched (the add/remove endpoints take arrays) and paced at about 1 request/second.

## 3. Authentication options

| Option | Notes |
|---|---|
| **Device code flow** | Best fit for a desktop/WPF tool. `POST /oauth/device/code` → show `user_code` + `https://auth.trakt.tv/activate` → poll `POST /oauth/device/token` at `interval` until `expires_in`. Poll statuses: 200 ok, 400 pending, 404 invalid, 409 already used, 410 expired, 418 denied, 429 slow down. |
| **Authorization code + PKCE** | Recommended by Trakt for sign-in; no client secret needed. `GET /oauth/authorize` with `code_challenge` (S256), exchange at `POST /oauth/token` with `code_verifier`. Needs a redirect URI (loopback listener works for desktop). |
| Client secret | Marked deprecated for user sign-in; server-to-server only. |

Token facts:

- Access token valid **7 days**.
- Refresh tokens are **single-use**: every refresh returns a new access+refresh pair and invalidates the old refresh token immediately. Persist the new pair atomically or you lock yourself out and must re-authorize.
- Refresh tokens issued before Trakt's recent auth migration no longer work (one-time re-auth).
- Revoke: `POST /oauth/revoke`.
- Store tokens with DPAPI / Windows Credential Manager, not in plain config.

## 4. Getting your data out

Auth'd endpoints (`/sync/*`) return your own data. `/users/{id}/...` endpoints work for any user the viewer can see (use `me` for yourself).

| Data | Endpoint(s) |
|---|---|
| Change detection | `GET /sync/last_activities`. Cache the timestamps and only re-fetch sections that changed (`watchlist.updated_at`, `lists.updated_at`, `movies.watched_at`, ...). Essential given the 500/5min GET limit. |
| Watchlist | `GET /sync/watchlist/{type}/{sort_by}/{sort_how}` (type = movies, shows, or `movie,show`). Also `GET /users/{id}/watchlist/...`. |
| Personal lists (index) | `GET /users/{id}/lists` and `/users/{id}/lists/collaborations` |
| One list + items | `GET /users/{id}/lists/{list_id}`, `GET /users/{id}/lists/{list_id}/items/{type}/{sort_by}/{sort_how}` |
| Liked lists | `GET /users/likes/lists` |
| Public lists | `GET /lists/trending`, `/lists/popular`, `GET /lists/{id}`, `GET /lists/{id}/items/...` |
| Dynamic/smart lists | `GET /users/{id}/smart-lists`, `GET /smart-lists/{list_id}/items` (resolved items) |
| Favorites | `GET /sync/favorites/{type}/{sort_by}/{sort_how}` |
| Watched / history | `GET /sync/watched/{type}`, `GET /sync/history/{type}/{id}`, `GET /sync/progress/watched` |
| Ratings | `GET /sync/ratings/{type}/{rating}` |
| Collection | `GET /sync/collection/{type}`, plus `/sync/collection/minimal/...` |
| Up next / playback | `GET /sync/progress/up_next`, `GET /sync/playback` |
| Calendar (airing / releases) | `GET /calendars/my/shows/{start_date}/{days}`, `/movies/`, `/streaming/`, `/dvd/` |
| Recommendations | `GET /recommendations/movies`, `/recommendations/shows` |
| Notes (VIP) | `GET /users/{id}/notes/{type}` |
| Settings + limits | `GET /users/settings` |
| ID lookup | `GET /search/{id_type}/{id}` (`imdb`, `tmdb`, `tvdb`, `trakt`) and `GET /search/{type}?query=` |

### Non-API ways to get data out

| Method | Verdict |
|---|---|
| **[traktexport](https://github.com/purarue/traktexport)** (Python, MIT) | Read-only dump via OAuth to one JSON file: watchlist, ratings, history, lists, collection, comments, etc. Good for a one-time backup. Still needs an API app, so same VIP gate. |
| Trakt website VIP data export | Not found. Could not confirm any official export. Check Settings → Data in the Trakt web UI yourself. **(unverified)** |
| Scraping trakt.tv HTML | Not recommended: against the API terms spirit, brittle, and blocked by anti-bot. |
| Third-party sync tools (PlexTraktSync, MDBList) | They hold the Trakt connection so you do not need your own app, but they count against the one-community-app limit for free accounts. See `03-prior-art-and-libraries.md`. |

## 5. Modifying your lists

All writes need OAuth and obey the 1 call/second limit.

| Action | Endpoint | Notes |
|---|---|---|
| Create list | `POST /users/{id}/lists` | Body: `name*`, `description`, `privacy` (`private` default, `link`, `friends`, `public`), `display_numbers`, `allow_comments`, `sort_by`, `sort_how`. 201. 420 if over the list limit. VIP Enhanced. |
| Update list | `PUT /users/{id}/lists/{list_id}` | Same fields, all optional. Slug is retained on rename. |
| Delete list | `DELETE /users/{id}/lists/{list_id}` | 204. Permanent. |
| Reorder lists | `POST /users/{id}/lists/reorder` | |
| Add items | `POST /users/{id}/lists/{list_id}/items` | Bulk body below. 201. 420 if over limit. Optional per-item `notes` is VIP only. |
| Remove items | `POST /users/{id}/lists/{list_id}/items/remove` | Same bulk body. |
| Reorder items | `POST /users/{id}/lists/{list_id}/items/reorder` | Ordered list-item ids. |
| Update one item | `PUT /users/{id}/lists/{list_id}/items/{list_item_id}` | `notes`, `rank`. 204. |
| Watchlist add | `POST /sync/watchlist` | Same bulk body. |
| Watchlist remove | `POST /sync/watchlist/remove` | Same bulk body. |
| Watchlist reorder | `POST /sync/watchlist/reorder` | |
| Favorites add/remove | `POST /sync/favorites`, `/sync/favorites/remove` | Movies and shows only. |
| Ratings | `POST /sync/ratings`, `/sync/ratings/remove` | `rating` 1-10. |
| Mark watched / unwatch | `POST /sync/history`, `/sync/history/remove` | Add does not de-duplicate. |
| Collection | `POST /sync/collection`, `/sync/collection/remove` | |
| Like / unlike a list | `POST|DELETE /users/{id}/lists/{list_id}/like` | |
| Hide a recommendation | `DELETE /recommendations/movies/{id}` (or `shows`) | |
| Smart list create/update/delete | `POST|PUT|DELETE /users/{id}/smart-lists[/{list_id}]` | Create body: `name*`, `source*` (trending, popular, anticipated, recommendations, discover, watchlist, library), `media_type*`, `filters`, `privacy`. VIP Enhanced. |

Shared bulk body for add/remove (watchlist, list items, history, collection):

```json
{
  "movies":   [ { "ids": { "tmdb": 272 } }, { "title": "Batman Begins", "year": 2005 } ],
  "shows":    [ { "ids": { "tvdb": 81189 } } ],
  "seasons":  [ { "ids": { "tvdb": 12345 } } ],
  "episodes": [ { "ids": { "trakt": 1 } } ]
}
```

- Send at least one id per item (movie: trakt/slug/imdb/tmdb; show: trakt/slug/imdb/tmdb/tvdb). `{title, year}` also works but is less reliable.
- Passing only a show adds only the show, not its episodes.
- Responses report `added`, `existing`, and `not_found` counts. Always surface `not_found` to the user.
- Notes: the body shape above is from the repo's request schemas; exact response shapes were not read.

Things that look like they belong here but are not available to third-party apps: `PUT /users/avatar` and `PUT /users/set_cover` are Limited Access (401 for non-first-party apps).

## 6. Realistic workflows for the app

1. **First run:** device-code auth → `GET /users/settings` (record limits, VIP flag) → `GET /sync/last_activities` → pull lists, watchlist, favorites. Cache locally (SQLite).
2. **Refresh:** poll `last_activities` (cheap, one call); only re-fetch changed sections.
3. **Edit lists:** make changes in a local staging area, then push in batches at ≤1 write/sec, honoring 420/429.
4. **Queue to Radarr/Sonarr:** take the `tmdb`/`tvdb` ids straight from the cached list items (no Trakt call needed).

## 7. Gotchas

- Single-use refresh tokens: crash between "got new tokens" and "saved them" = forced re-auth. Write tokens before using them.
- Free accounts: 250-item lists/watchlist and 5 lists will bite quickly if the goal is a big library manager.
- The docs site and Apiary docs are JS-rendered and the older `trakt.docs.apiary.io` is no longer the source of truth; use the GitHub contracts at <https://github.com/trakt/trakt-api/tree/master/projects/api/src/contracts>.
- The repo can generate a full OpenAPI file (`bun run openapi:generate`), which is not committed. Generating it would give a machine-readable spec for client generation.
- Trakt's guides say: use only documented methods, do not create extra apps to dodge rate limits, follow branding rules, watch the GitHub repo for announcements. Treat the terms as binding.
- Endpoint and policy details have shifted several times in 2026. Re-check `/users/settings` limits and the developer guides before building.
