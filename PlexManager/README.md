# PlexManager

Exploration notes for an app that manages Trakt.tv lists and feeds the Radarr/Sonarr queue. **Research only; no code.**

Researched 2026-10-20.

| Doc | Contents |
|---|---|
| [01-trakt-api.md](01-trakt-api.md) | VIP gate on API apps, limits, auth flows, every endpoint for reading data and editing lists, rate limits, gotchas |
| [02-radarr-sonarr-api.md](02-radarr-sonarr-api.md) | Lookup + add for movies and series, queue/history/calendar endpoints, built-in Trakt import lists |
| [03-prior-art-and-libraries.md](03-prior-art-and-libraries.md) | Existing tools (Traktarr, MDBList, PlexTraktSync, Seerr...) and .NET client libraries |
| [04-options-and-recommendation.md](04-options-and-recommendation.md) | Option matrix, architecture sketches, recommendation, open questions |

## Summary

- **Trakt data out and list edits are fully available via the official API** (device-code or PKCE OAuth). Lists, watchlist, favorites, history, ratings, and smart lists can all be read and modified. Writes are limited to 1 call per second.
- **Big catch:** since about 2026-07-31, creating a Trakt API app requires an active **VIP** membership, and apps owned by free accounts were disabled. Free accounts are also limited to 250 items per list/watchlist and 5 lists.
- **Radarr and Sonarr** both have a v3 REST API: look up by `tmdb:` / `tvdb:`, `POST /movie` or `POST /series`, then read `/queue`. Radarr and Sonarr can also poll Trakt lists themselves with built-in import lists.
- **Nothing existing does everything:** edit Trakt lists + review + push to Radarr/Sonarr. Trakt → Radarr/Sonarr alone is already covered.
- **Recommendation:** decide on VIP first. If yes, build a Trakt list editor and let Radarr/Sonarr import lists consume the lists, adding direct push later if needed. If no, build only a Radarr/Sonarr queue/calendar dashboard.

Some details (marked unverified in the docs) need a check against your own Trakt account and Radarr/Sonarr instances.
