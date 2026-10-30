# Options and recommendation

Research date: 2026-10-20. Based on `01`, `02`, and `03` in this folder. No code has been written.

## 1. The three jobs

1. **Get data out of Trakt** (lists, watchlist, favorites, history, ratings).
2. **Modify Trakt lists** (create, add, remove, reorder, delete).
3. **Add items to Radarr/Sonarr** and see what is in their queues.

## 2. Options for job 1 and 2: talking to Trakt

| # | Option | Gets data out | Edits lists | Needs own Trakt API app (VIP) | Verdict |
|---|---|---|---|---|---|
| A | **Own Trakt API app, device-code or PKCE OAuth** | Everything | Everything | **Yes** | The only option with full control. Gated by VIP since ~2026-07-31. |
| B | One-shot export with **traktexport** | Everything, read-only JSON | No | Yes (needs an app) | Good as a backup, not a manager. |
| C | Use **someone else's connected app** (MDBList, PlexTraktSync) as a middleman | MDBList: yes via lists/API | Unconfirmed | No (their app) | Counts as your one community app for a free account. Limited, indirect. |
| D | Trakt web UI + Radarr/Sonarr built-in Trakt import lists | N/A | Manual in the website | No | Zero build. Covers "Trakt list → Radarr/Sonarr" already. |
| E | Scrape trakt.tv | Fragile | No | No | Do not. |
| F | Move off Trakt (Simkl, MDBList lists, Plex watchlist, Letterboxd, a local DB) | Varies | Varies | Varies | Worth considering if VIP is a non-starter. Not researched in depth. |

## 3. Options for job 3: Radarr/Sonarr

| # | Option | Notes |
|---|---|---|
| X | **Push via REST** (`POST /movie`, `POST /series`) | Full control: preview, dedupe, per-item profile and folder, tags. ~12 endpoints. |
| Y | **Pull via import lists** (configure Radarr/Sonarr to read a Trakt list, optionally created/managed through their `/importlist` API) | No per-item control, but robust and low maintenance; the app only maintains the Trakt list. |
| Z | Hybrid: app edits Trakt lists (job 2); Radarr/Sonarr import lists consume them. Use direct push only for one-off adds. | Least code, uses each system as designed. |

## 4. Architecture sketches (docs only)

**Sketch 1: full manager (A + X).** WPF or web UI → local SQLite cache of Trakt data (`/sync/last_activities` for deltas) → staging area for edits → batch writes to Trakt (≤1/sec) and Radarr/Sonarr. Highest value, highest effort. Needs VIP.

**Sketch 2: Trakt list editor + built-in sync (A + Y/Z).** UI only edits Trakt lists. Radarr/Sonarr poll those lists. The app shows queue status from `GET /queue`. Smaller surface, fewer failure modes, same VIP requirement.

**Sketch 3: no Trakt API (D).** Do nothing custom for Trakt; build only a small dashboard for Radarr/Sonarr queue and calendar. No VIP, no OAuth, nothing to maintain on the Trakt side. Gives up programmatic list editing.

## 5. Recommendation

1. **Decide on VIP first.** It is a go/no-go for any option that uses your own Trakt API app (A, B, Sketches 1 and 2). If you will not pay, pick Sketch 3 or option F.
2. **If VIP is acceptable, start with Sketch 2**, then grow toward Sketch 1 only if import lists feel too coarse. Reasons:
   - Radarr/Sonarr already do reliable Trakt polling, dedupe via list exclusions, and cleanup.
   - Push requires you to handle id gaps (no tvdb / no tmdb), duplicates, root-folder and profile mismatches.
   - Trakt writes are limited to 1/sec, so a single large "sync everything" run is slow but fine in batches.
3. **Regardless of path, spend 30 minutes with the real instances first** (on the user's own network; see Open questions) to answer the unknowns in §6. That avoids building on guesses.
4. **Tech stack suggestion** (user already works in .NET/WPF per recent commit `ImageStripper`): .NET 10, `HttpClient` typed clients. For Trakt, use `Trakt.NET.Ex` only if you accept a one-maintainer dependency; otherwise a hand-written client for ~15 endpoints is small. For Radarr/Sonarr, generate your own client from the official `openapi.json` files, or use the Soenneker packages (.NET 10 only, auto-generated).

## 6. Open questions and unverified items

Needs a human check (UI/instance), not further web research:

| Question | How to resolve |
|---|---|
| Is your Trakt account VIP, and is VIP worth it for this? | Trakt settings; decision. |
| What are your actual list sizes, watchlist size, and list count? | Compare with the free limits (250 items, 5 lists). |
| Do existing Radarr/Sonarr Trakt import lists already do what you need? | Settings → Import Lists → Trakt; run `GET /importlist/schema`. |
| Radarr/Sonarr versions, and the exact spec on your instance | Open the instance's own API docs page. |
| Does the Sonarr Trakt watchlist import work now (old forum reports said no)? | Test it. |
| Search command bodies (`MoviesSearch`, `SeriesSearch`) | Check on your instance. |
| Does a Trakt API app survive VIP lapse? | Ask in the Trakt forum / issue #902 thread. |
| Does Trakt offer an official data export? | Trakt web UI settings. |
| Is Plex itself in scope (watchlist, libraries, collections)? | Decision. If yes, a separate research pass on Plex's APIs is needed. |
| Where to run it: desktop, or a Docker/web service? | Decision. Desktop device-code auth is simplest; web needs a redirect URI. |
| Is a "temporary" Trakt anti-abuse VIP gate going to be relaxed? | Watch <https://github.com/trakt/trakt-api> and the Trakt forum. |

Source-quality notes:

- Official-repo sources (Trakt contracts and guides, Radarr/Sonarr OpenAPI, Servarr wiki source) are the most reliable here.
- Facts about the July 2026 VIP gate and community-app limit partly rest on third-party reports (GitHub issue, TROYPOINT), backed by one maintainer reply in trakt-api issue #902.
- The Trakt Apiary docs, the Radarr/Sonarr docs sites, the Servarr wiki site, and developer.trakt.tv could not be read (JS-rendered), so the GitHub sources above replaced them.
- Several fetch summaries were model-generated for long pages. Endpoint lists should be re-verified against a generated OpenAPI file before coding.
