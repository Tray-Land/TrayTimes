# Transit API migration plan

Replace the Milwaukee-only MCTS BusTime and GTFS data sources with the [Transit API v4](https://api-doc.transitapp.com/v4.html) so Tray Times works wherever Transit has data. Calls are scarce, so every design choice below is judged by how many requests it costs.

Created: 2026-10-15. Branch: `feature/transit-app-api`.

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | No sign-in. Swap every data source to Transit, add a call budget, and keep the Favorites, Commute, Nearby, tracking, and Settings features. | implemented, awaiting developer testing |
| 2 | Sign-in feasibility study and the features it would enable. See [transit-login-feasibility.md](transit-login-feasibility.md). | plan written; recommends bring-your-own-key first; not started |

## The budget that drives the design

Transit's partner page grants **5 calls per minute and 1,500 calls per month** per key (the free tier). That's about 50 calls a day, while the BusTime build made about 6 a minute with the flyout open. Polling as before would use a month's quota in about four hours of browsing.

The quota belongs to the **key**, not the user. A key compiled into a Store build would be shared by every install, so 1,500 calls a month for everyone. Phase 1 is for development and testing with one key. Distribution needs per-user keys, a negotiated tier, or a caching proxy; Phase 2 covers this.

Design rules, in priority order:

1. One request per refresh, whatever the tab. `stop_departures` takes up to 100 stops, so Favorites, Commute, Nearby, and tracking share one batched call.
2. Discovery is cached and persisted. Which stops are near you, which trip gets you to work, and which routes run in an area rarely change, and a desktop PC rarely moves. They're kept in LocalState and reused across restarts.
3. Decorative data never costs a request. Route lines are drawn only from shapes already returned by calls the app made anyway.
4. Vehicle positions are fetched on demand, when a row is selected, rather than polled for every listed bus.
5. A client-side budget governor paces automatic refreshes to the monthly quota and never exceeds the per-minute limit. Manual refreshes and tracking can still go through when automatic refreshes are paused.

## Endpoint review

Every v4 endpoint, what it's for, and whether Phase 1 uses it.

| Endpoint | What it returns | Phase 1 use | Cache |
|----------|-----------------|-------------|-------|
| `GET /v4/public/stop_departures` | Upcoming departures for up to 100 stops, grouped by route and direction, merged real-time and scheduled (`is_real_time`), with route names, colors, alerts, and optionally stops and shapes | **Every live time**: Favorites, Commute boarding stops, Nearby stops, tracking. Shapes are requested only for stops not seen with shapes in the last 7 days. | Per-stop snapshots in `PredictionCoordinator`; freshness set by the budget |
| `GET /v4/public/nearby_routes` | Routes within ≤1.5 km, each direction's closest stop, departures, and optionally itinerary stops and shapes | **Nearby discovery**: one call gives the stops, their lines, and the shapes to draw | Persisted; reused within 150 m for 24 h |
| `GET /v4/public/plan` | Multimodal trip plans with transfers, walking legs, departure options, and per-departure alighting times | **Commute**: replaces the shape-matching direct-route search, so transfers now work | Persisted; reused within 150 m of the origin, same destination, for 3 h |
| `GET /v4/public/search_stops` | Stops matching a name or stop code near a point | **Settings**: stop search, run on submit only (no typeahead) | In memory, 1 day |
| `GET /v4/public/available_networks` | Networks serving a point | **Settings**: route browser, first step | Combined with routes below |
| `GET /v4/public/routes_for_networks` | Every route of up to 20 networks | **Settings**: route browser list | Persisted, 7 days, per area |
| `GET /v4/public/route_details` | One route's itineraries (directions/branches), ordered stops, and shapes | **Settings**: direction and stop pickers and the route line | Persisted, 7 days |
| `GET /v4/vehicles` | Live vehicle positions for one route, optionally one direction, with `rt_trip_id`, label, and occupancy | **Flyout**: positions for the selected row's route only | 30 s in memory |
| `GET /v4/public/nearby_stops` | Stops near a point, without routes | Not used: `nearby_routes` gives stops *and* lines in the same single call | — |
| `GET /v4/public/stop_details` | One stop's details (time zone, network) | Not used: other responses already carry what the app shows | — |
| `GET /v4/public/estimate_plan_duration` | A single trip duration | Not used: `plan` costs the same one call and returns departures too | — |
| `GET /v4/public/schedule_for_dates` (preview) | Static timetable for a route at a stop | Not used: `stop_departures` already includes scheduled times | — |
| `GET /v4/public/trips_for_dates` (preview) | Static trips for a route | Not used | — |
| `GET /v4/public/trip_details` | A trip's stop-by-stop times | Not used yet. Candidate for a "where is my ride" view while tracking (Phase 1.x). | — |
| `GET /v4/public/alerts_for_networks` | All alerts for networks | Not used: alerts already arrive with each route's departures. Displaying them is a free follow-up. | — |
| `GET /v4/public/latest_update_for_network` | When a network's data last changed | Not used: cache lifetimes are short enough without it | — |
| `GET /map_layers/placemarks`, `/map_layers/available_networks` | Bikeshare docks, scooters | Out of scope | — |

Notes from the spec that shape the client:

- Authentication is an `apiKey` request **header**, so keys never appear in URLs or logs.
- Times are UNIX seconds, so there's no agency time-zone parsing. The local clock and culture format everything.
- `route_color` comes without a leading `#`. The client normalizes it to `#rrggbb`.
- Shapes are Google encoded polylines (precision 1e5).
- The server's rate-limit and quota responses are undocumented. The client treats HTTP 429 (and 503 with `Retry-After`) as a rate limit and 401/403 as a rejected key, and the governor keeps the app below both limits so it shouldn't hit them.

## What goes away

| Removed | Why it's no longer needed |
|---------|---------------------------|
| `BusTime/` client and DTOs | Replaced by `TransitApp/` |
| GTFS download, parser, and `ScheduleService` (9 MB zip, weekly) | `stop_departures` merges scheduled and real-time departures itself |
| `ArrivalMerge` | Same reason |
| Stop-directory crawl (`StopDirectoryBuilder`, `StopIndexService`; 148 requests a week) | `nearby_routes` and `plan` answer "what's near me" and "what goes there" directly |
| Shape-based direct-route matching in `CommutePlanner` | `plan` decides which trips work, including transfers |
| Polling every listed bus's position every 15 s | On-demand `vehicles` for the selected row |
| Milwaukee address bias and US-only geocoding | Biased to the user's own places instead; worldwide |

Saved MCTS favorites are dropped on first run (developer decision, 2026-10-15). Favorite places (home, work, school, custom) are plain coordinates and carry over unchanged.

## Request budget governor

`TransitBudget` (Core) sits in the existing failure gate (`CooldownTransitService`), so a request has to clear both the failure cooldown and the budget before it is sent. Cache hits never reach it.

- **Per minute:** a sliding 60 s window of sent requests. Automatic requests leave 2 of the 5 slots free for user actions and tracking. Manual requests and tracking wait for a slot (at most a minute, cancellable) instead of failing.
- **Per month:** `daily allowance = remaining this month / days left`. Today's share of it sets the level:

| Level | When | Browsing refresh | Reopen reuses data younger than | Tracking polls |
|-------|------|------------------|----------------------------------|----------------|
| Normal | < 60% of today's allowance used | 60 s | 60 s | base schedule |
| Conserve | 60–100% | 3 min | 2 min | 2× slower |
| Low | today's allowance used | paused (manual only) | 5 min | 2× slower |
| Reserve | ≤ 2% of the month left | paused | 10 min | 2× slower; manual refresh still works |
| Exhausted | month used up | nothing is sent until next month | — | stops |

Limits are editable in Settings (defaults 5/min and 1,500/month). Usage counts come from the existing saved per-endpoint tally, now kept for 62 days so a calendar month is always covered. Days and months are local calendar ones, like the tally. Transit doesn't document when its quota resets, so the 2% reserve also absorbs a few hours of mismatch.

Tracking base schedule (was 60/30/10 s for BusTime): 3 min when over 10 minutes away (or while only the timetable knows the trip), 90 s at 5–10, 60 s under 5, and 90 s once two separate checks confirm the ride is gone. Timetable-only trips still get no requests until 15 minutes before. The check at the end of the 5-minute grace is skipped, because no answer could change the outcome. A ride tracked from 15 minutes out costs 12 requests (measured in `RequestBudgetTests`).

## Request sharing

- `PredictionCoordinator` keeps per-stop snapshots and in-flight sharing, now with batches of 100.
- **Coalescing window (new):** a new batch waits 750 ms before sending, and other consumers' stops requested in that time join it. Opening the flyout in Commute or Nearby therefore usually costs one call for both the tab's stops and the Favorites tooltip.
- **Filler threshold (changed):** a request fills with browsing stops that are more than half a refresh interval old, instead of a full interval. Favorites' and Commute/Nearby's timers drift into phase, so one call serves both.
- Tracking can now fill its request with due browsing stops too. With 100-stop batches this costs nothing, and it keeps the open flyout fresh.

## Caches and persistence

`MetadataTransitService` holds bounded, TTL-limited, in-flight-shared caches for area routes, route details, nearby lines, trip plans, stop searches, and shapes (by route and direction). `TransitCacheService` (app) saves a snapshot to `LocalState\transit-cache.json` on change (debounced) and restores it at startup, so restarts and the one-minute flyout teardown never repeat discovery.

## Expected calls (free tier, typical day)

Rows marked † are measured by `TrayTimes.Tests/Services/RequestBudgetTests.cs` through the real monitors, coordinator, caches, and client, using fake HTTP and a fake clock.

| Scenario | BusTime build | Transit build |
|----------|---------------|---------------|
| Open the flyout on Favorites, look for 20 s | 2–4 | 1; 0 if reopened within 60 s † |
| Flyout open 5 minutes | ~30 | 5 at Normal † (~2 at Conserve) |
| Open Nearby (known location) with Favorites | 2 + vehicles | 1 shared call † |
| Nearby and Favorites timers out of phase | 2 per 30 s | 1 per minute † |
| Nearby at a new location | directory (148/week) + 2/30 s | 1 discovery + 1 departures |
| Commute, first time today | patterns + predictions | 1 plan + 1 departures |
| Track one ride from 15 minutes out | ~30 | 12 † |
| Weekly stop directory | 148 | 0 |
| Favorites route lines | 1 per route per 6 h | 0 (from shapes already returned) |
| Bus positions | 4 per minute | 1 per row selection, then 1/min while selected (Normal level only) |

## Phase 1 verification so far (2026-10-15)

- `dotnet build -c Debug -p:Platform=ARM64`: app and tests build with 0 warnings and 0 errors.
- `dotnet test -c Debug -p:Platform=ARM64`: 400 of 400 pass, and six repeated runs were stable. New suites cover the Transit client (`TransitAppServiceTests`), the budget (`TransitBudgetTests`), the caches and their persistence (`MetadataTransitServiceTests`), coalescing (`PredictionCoordinatorTests`), discovery boards (`PlaceBoardTests`), and the scenario counts above (`RequestBudgetTests`). The BusTime, GTFS, and directory tests went with the code they tested.
- Not yet verified: anything against the live Transit API (no key was available while building) and the WinUI flyout and Settings at runtime. The checklist below covers both.

## Phase 1 acceptance checklist (developer)

1. Add `TransitApiKey` to `TrayTimes\OAuth.resw` (see the sample), build, and run.
2. First run: old MCTS favorites are gone; Settings shows the Transit API key as configured and a usage card with monthly totals.
3. Settings → Add a stop: search a stop by name or code near the map, add it, and see it in Favorites with live and scheduled times. Browse by route: load routes for the map's area, pick a route, direction, and stop.
4. Favorites flyout: times, scheduled times ("Scheduled"), canceled trips, the "late" detail, route colors, and the route line on the map.
5. Select a row: that route's vehicles appear, and the trip's own vehicle is highlighted.
6. Nearby: stops near the PC with each line at its closest stop and route lines drawn. Reopen within a minute and confirm no new discovery request in the debug output.
7. Commute: Home → Work trip options, including one with a transfer if your commute has one, with "by" ETAs.
8. Tracking: ring a bell; the tray ETA counts down, alerts fire, and tracking stops after the ride passes.
9. Usage: after a normal day, the usage card should show well under 50 calls.

## Open questions

- Does Transit require visible attribution ("Powered by Transit") or a logo in the UI? The API docs and terms say nothing about it. Settings credits Transit for now; confirm with partners@transit.app before distributing.
- When does the monthly quota reset (UTC or local midnight, calendar month or rolling 30 days)? The governor uses local calendar months.
- What are the error bodies for 401/403/429? The client handles them by status code only.
