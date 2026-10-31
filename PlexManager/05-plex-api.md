# Plex Media Server API (what PlexManager uses)

Verified against the live server (1.31.3) on 2026-10-31. Read-only so far.

## Access

- Base URL `http://192.168.1.6:32400`. Send the token as header `X-Plex-Token`. Without it the server returns 401.
- Send `Accept: application/json` to get JSON (default is XML). The payload sits under `MediaContainer`; listed items are under `Metadata`.
- `GET /identity` needs no token. `GET /library/sections` lists libraries.

## Listings

| Need | Request |
|---|---|
| Movies | `/library/sections/4/all` |
| Shows | `/library/sections/1/all?type=2` |
| Episodes | `/library/sections/1/all?type=4` |
| Paging | `X-Plex-Container-Start=<n>&X-Plex-Container-Size=<n>`; `totalSize` in the container |
| External ids | add `includeGuids=1` |

Fields used: `ratingKey`, `title`, `year`, `viewCount` (absent when unwatched), `lastViewedAt` (Unix seconds), `Media[].Part[].file`, and for episodes `grandparentRatingKey`, `grandparentTitle`, `parentIndex` (season), `index` (episode).

## Ids: two generations in one library

- New agent items carry `Guid` children (`imdb://`, `tmdb://`, `tvdb://`) and a `plex://movie/<hex>` guid. Trakt records the same hex as `ids.plex.guid`.
- Older items still have a legacy `guid` attribute such as `com.plexapp.agents.themoviedb://611605?lang=en` and no `Guid` children. The scan parses `themoviedb`, `imdb`, and `thetvdb` out of it.
- Items from agent `none` (the Adult and Other Videos libraries) have no ids and are skipped.
- Matching key: any shared `tmdb:`, `imdb:`, `tvdb:`, or `plex:` value.

## Watch state

- One account (owner "Xegster"). The owner token returns the owner's `viewCount` and `lastViewedAt`.

## Not yet verified (needed for later phases)

- Marking items watched/unwatched: `/:/scrobble`, `/:/unscrobble`.
- Deleting: `DELETE /library/metadata/{ratingKey}`; whether the file is removed depends on the server setting "Allow media deletion" (Settings, Library). Check before Phase 2.
- Plex user ratings (`userRating`): not used; Trakt is the rating source.
