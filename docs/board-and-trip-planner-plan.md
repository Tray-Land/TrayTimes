# One board and a trip planner (plan)

Replace the flyout's three tabs (Favorites, Commute, Nearby) with one departures board centered on where you are, a trip suggestion above it, and a full trip planner behind the suggestion. Favorites become places to jump the board to.

Created: 2026-10-18. Branch: `feature/transit-app-api` (or a branch from it). Status: steps 01–03 done (2026-10-18); D1–D7 confirmed as recommended (2026-10-18); step 04 built and tested, waiting on the developer's packaged checks (2026-10-18). It builds on Phase 1 in [transit-app-migration.md](transit-app-migration.md) and keeps its call budget rules.

## Why

The three tabs are one board seen from three places:

| Tab today | What it really is | What it becomes |
|-----------|-------------------|-----------------|
| Nearby | The board where you are | **The board.** What every open shows first |
| Favorites | The board for stops you picked, wherever you are | **Jump targets.** Pick a saved stop or place to move the board there. Saved stops near you are pinned to the top of the board |
| Commute | A guess at where you're going, and where to board | **The suggestion card** above the board, opening a **trip view** with every leg |

The trip planner already returns every leg of every option, but `PlanTripAsync` keeps only the first ride and folds the rest into an arrival time and a transfer count (`TrayTimes.Core/Transit/TransitApp/TransitAppService.cs:163-171`). Building it out is mostly modeling and UI, not new endpoints.

## Target design

### The board (what opens)

```
┌──────────────────────────────────────────┐
│ ● Departures             ⟳   ⛶   ⚙       │  toolbar, unchanged
│ 📍 Near you ▾                            │  place chip: the jump menu
├──────────────────────────────────────────┤
│                  [map]                   │
├─────────────────── ══ ───────────────────┤
│ 💼 To Work · leave in 6 min          ›   │  suggestion card → trip view
│    14 → 30 · arrive 8:42 · 1 transfer    │
│                             [Going to ▾] │
│ ★ Oak & 5th · Northbound                 │  saved stops near the center, pinned first
│   [14] Downtown                   4 min  │
│ Maple & 3rd · 250 m · 3 min walk         │  then the nearest other lines
│   [30] Airport                    9 min  │
└──────────────────────────────────────────┘
```

- **Center.** Every open starts at **Near you** (device location), the way Commute now guesses its destination afresh on each open (`TimesFlyoutWindow.xaml.cs:246`). Without a location it falls back to **Near Home**, then to **All saved stops**, then to the location-permission empty state.
- **Place chip (jump menu).** A `DropDownButton` replaces the `SelectorBar`. Its menu lists: Near you; each saved place ("Near Work"); each saved stop with its next departure ("★ Oak & 5th · 14 in 4 min"); All saved stops; Manage favorites… (opens Settings). The next-departure text is free, because Favorites' departures already ride in every batch while the flyout is open (`ArrivalsMonitor.IsForeground`, `TrayTimes/Services/ArrivalsMonitor.cs:59-89`).
- **Jumping** moves the board's center: the map frames the new spot and the list shows the board there. Walk times and "too soon to walk" dimming apply only at Near you, the same rule that today switches them off for a guessed commute (`TimesFlyoutWindow.xaml.cs:1129`).
- **Board contents.** Saved stops within the radius (1 km) of the center come first, each showing its saved route and direction, or every route if it was saved with All routes. Then the nearest other lines, each listed once at its closest stop, as Nearby does now. A line already shown at a pinned saved stop isn't repeated at a closer one. The total stays at 8 stops (`MaxNearbyStops`).
- **Rows that start the suggested trip** keep today's "Work by 8:42 · 1 transfer" detail (`ArrivalItem`, `TrayTimes/Views/ArrivalItems.cs:154-163`).
- Row selection, vehicles, tracking bells, stars, refresh, fit, and the splitter all carry over unchanged.

### The suggestion card

- The destination comes from `CommutePlanner` exactly as today: at home it's work, else school, else the first custom place; anywhere else it's home; without a location, the time of day decides (`TrayTimes.Core/Commute/CommutePlanner.cs:56-71`).
- It shows the best option, live: the place glyph, "To Work · leave in 6 min", then route chips, the arrival time, and transfers. "Leave in" counts down to the first ride's departure minus the walk to its stop.
- **Going to ▾** sits beside the card, not inside it, so there's no button inside a button. Its menu lists: Where I'm likely headed; saved places; Search for a place… (step 06); Edit places….
- When you're already at the destination, the card reads "You're at Work" and keeps Going to. With no Home saved, it shows a one-line setup prompt with a **Set up places** button.
- Selecting the card opens the trip view.

### The trip view

```
┌──────────────────────────────────────────┐
│ ←  To Work                arrive 8:42    │
│ [8:42 · 1 transfer] [8:51] [9:03 · walk] │  options, as a SelectorBar
├──────────────────────────────────────────┤
│              [map: the legs]             │
├─────────────────── ══ ───────────────────┤
│ 🚶 Walk 4 min · 300 m to Oak & 5th       │
│ [14] Downtown · leaves 8:12 · 6 stops    │
│      🔔 8:12   8:24   8:36               │  the same time chips and bells
│      Get off at State St · 8:25          │
│ 🚶 Walk 1 min · wait 4 min               │
│ [30] Airport · leaves 8:30 · 4 stops     │
│      Get off at Airport Rd · 8:38        │
│ 🚶 Walk 4 min                            │
│ 💼 Work · 8:42                           │
└──────────────────────────────────────────┘
```

- The trip view replaces the list pane. The map stays, and the toolbar's Fit frames the whole trip.
- **Options:** up to 3 (`num_result`), labeled by arrival time. A walk-only option appears when walking is about as fast (`walk_fallback`).
- **Map:** each ride is drawn in its route color, clipped to the part you ride, and each walk leg comes from the leg's own polyline, drawn thinner and gray (dashed, if the map control supports it). Transfer stops get pins, and the origin and destination get the place markers already used by Commute.
- **Tracking:** the first ride's chips track exactly as rows do now (`TrackedBus`). Tracking across a transfer is out of scope (see Follow-ups).
- **Going back:** a Back button, and Escape, Backspace, or Alt+Left, return to the board. Escape on the board still hides the flyout.

### The tray tooltip

It shows the tracked bus, then the suggestion ("Work: leave in 6 min, 14 at 8:12"), then as many saved stops near the center as fit in 127 characters. As now, times appear only while the flyout is in use (`TrayTimes/App.xaml.cs:332-369`).

## What the API offers (checked against the v4 spec, 2026-10-18)

The spec is embedded in `https://api-doc.transitapp.com/v4.html` as OpenAPI 3.1.

**`GET /v4/public/plan` parameters worth using**

| Parameter | Spec | Use |
|-----------|------|-----|
| `from_lat`/`from_lon`, `to_lat`/`to_lon` | Or `from_global_stop_ids`/`to_global_stop_ids` | As today. Stop IDs let a trip start at a saved stop without the first walk (step 06) |
| `leave_time`, `arrival_time` | UNIX time; when both are sent, `leave_time` wins | Leave at / arrive by (step 07) |
| `num_result` | Default 3 | Keep 3 |
| `max_num_departures` | Default 3, per stop | Keep 3 |
| `max_num_legs` | Default 3 (1–6) | Keep the default |
| `should_update_realtime` | Default false | Keep true |
| `walk_fallback` | Adds a walk-only result when the walk is ≤ 30 min and transit is slower or missing | Turn on (step 01) |
| `consider_downtimes` | Routes around outages known from service alerts | Turn on (step 01) |
| `walk_speed`, `walk_reluctance`, `accessibility_need` | 0.5–5 m/s; ≥ 1.0; `none`/`strict`/`prioritize_step_free` | Follow-ups (rider settings) |
| `should_include_directions` | Turn-by-turn steps | Not needed |

**Response fields**

- `results[]` have `start_time`, `end_time`, `duration`, and `legs[]`.
- A walk leg (`leg_mode` = `walk`) has `start_time`, `end_time`, `duration`, `distance`, and an encoded `polyline`.
- A transit leg (`leg_mode` = `transit`) has `start_time`, `end_time`, `routes[]` (each with `itineraries[]` carrying `plan_details.start_stop_offset` and a shape), and `departures[]`. Each departure has `departure_time`, `is_real_time`, `rt_trip_id`, `trip_search_key`, and `plan_details` with `global_route_id`, `internal_itinerary_id`, `arrival_schedule_item` (the alighting time), and `stop_schedule_items` (every stop from boarding to alighting).
- The DTOs (`TransitAppDtos.cs:192-214`) already read most of this. They lack `start_time` on results and legs, and they read the ride's stops only to find the boarding stop (`BoardingStop`, `TransitAppService.cs:398-415`). The alighting stop is the last `stop_schedule_items` entry, and the stop count is that list's length.

**Checked against a recorded live plan (step 01, 2026-10-18).** The spec's examples are abridged, so one real transfer plan was recorded (`TrayTimes.Tests/Transit/TransitApp/Fixtures/plan-transfer-montreal.json`). It showed:

- `stop_schedule_items` includes both the boarding and the alighting stop (offsets 0→10 give 11 items). The alighting stop is its last entry, which matches `stops[end_stop_offset]`. Stops ridden is the item count **minus 1**, not the count. The list can be missing (the spec's second example), so offsets are the fallback.
- A transit leg's `routes[]` can hold **alternative lines** ("Array of Route that can be used to travel that leg"). The recorded trip offers the 121 and the 171 for one ride, and they board at **different stop IDs** with the same name. So boarding stop, alighting stop, stop count, and ridden shape belong to each line, not to the ride.
- One line's departures can reference several itineraries (the REM's two patterns) that share a boarding stop. They join one line.
- `arrival_schedule_item.arrival_time` is the alighting time and equals the leg's `end_time`. `departure_time` can be a minute later (dwell), so arrival is read first.
- Each ridden itinerary's `plan_details.plan_shape` is the encoded shape of just the ridden part, between `start_stop_offset` and `end_stop_offset`. The trip view can draw it directly (see §5).
- No departure carries `global_stop_id`. The boarding stop comes from the first `stop_schedule_items` entry, else `stops[start_stop_offset]`.

`trip_details` (by `trip_search_key`) gives a whole trip's stop times. The plan's `stop_schedule_items` already cover the ridden part, so it isn't needed.

## Architecture changes

### 1. Itinerary model (Core)

`TripOption` is replaced by whole trips (`TrayTimes.Core/Transit/TransitModels.cs`, done in step 01). As built:

```csharp
public sealed record TripPlan(IReadOnlyList<TripItinerary> Itineraries, IReadOnlyList<RoutePattern> Shapes);

public sealed record TripItinerary(IReadOnlyList<TripLeg> Legs, DateTimeOffset Departs, DateTimeOffset Arrives)
{
    [JsonIgnore] public int Transfers => Math.Max(0, Legs.OfType<RideLeg>().Count() - 1);   // 0 for walk-only
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(WalkLeg), "walk")]
[JsonDerivedType(typeof(RideLeg), "ride")]
public abstract record TripLeg(DateTimeOffset Starts, DateTimeOffset Ends)
{
    [JsonIgnore] public TimeSpan Duration => Ends - Starts;
}

public sealed record WalkLeg(DateTimeOffset Starts, DateTimeOffset Ends, double Meters, IReadOnlyList<GeoPoint> Path) : TripLeg(Starts, Ends);

/// Alternative lines for one ride, soonest first; the leg's times are for Lines[0]'s first departure.
public sealed record RideLeg(DateTimeOffset Starts, DateTimeOffset Ends, IReadOnlyList<RideLine> Lines) : TripLeg(Starts, Ends);

public sealed record RideLine(
    TransitRoute Route, TransitDirection Direction,
    TransitStop BoardAt, TransitStop? AlightAt,   // AlightAt is null when the plan doesn't say
    int Stops,                                    // stops ridden; 0 when unknown
    IReadOnlyList<GeoPoint> Path,                 // plan_shape: the ridden part only
    IReadOnlyList<PlannedDeparture> Departures);  // soonest first, canceled ones left out

public sealed record PlannedDeparture(DateTimeOffset Departs, DateTimeOffset Alights, bool IsRealTime, string? TripId);
```

- Why it differs from the first sketch: the live plan showed alternative lines per ride, each with its own boarding stop (see "Checked against a recorded live plan"). A ride with one line is just `Lines.Count == 1`.
- `TripId` is `rt_trip_id`, else `trip_search_key`, the same rule as `Arrival.TripId`, so step 02 can match a planned departure to a live one.
- A result with a leg in another mode, or a ride with no usable departure, is left out whole.
- `PlaceBoard.Commute` turns each line of each itinerary's first `RideLeg` into a board row, using that line's first departure. Its output for the board is unchanged. A walk-only itinerary (from `walk_fallback`) has no `RideLeg`. The board ignores it, and the trip view shows it as an option.

### 2. Live trip timing (Core, pure)

A plan is reused for up to 3 hours (`MetadataTransitService.PlanLifetime`), so its times go stale. Its structure (which lines, where to board, where to change) doesn't go stale as quickly. So the plan supplies the structure, and the shared departures batch supplies the times, at no extra cost. As built in step 02 (`TrayTimes.Core/Commute/LiveTrip.cs`):

```
LiveTrip.Compute(itinerary, batchArrivals, now) → LiveTrip?:
  ready = now
  for each leg:
    walk → LiveWalk(ready, ready + its duration); ready += its duration
    ride → for each of its lines, the next departure at or after ready:
             the batch's next uncanceled departure at StopLine(line.BoardAt, line.Route, line.Direction),
             else the line's PlannedDepartures, leaving out trips the batch knows (canceled or gone);
           it rides as long as the plan rides that trip (by trip ID), else the line's planned trip nearest in time;
           take the line that gets off soonest (ties: the one leaving first); ready = when it gets off
    no departure for a ride → null (infeasible: re-plan)
  walks before the first ride are moved to end as it leaves, so the rider waits at the origin, not the stop
  → LiveTrip(Itinerary, Legs, LeaveBy, Arrives)
```

- `Legs` are `LiveWalk` (the `WalkLeg` at live times) and `LiveRide` (the `RideLeg`, the chosen `RideLine`, and `Departure`: the batch's `Arrival`, or null when timed from the plan). Step 05 can read real-time, delay, and the trip to track from `Departure`.
- `LeaveBy` is the first leg's live start: the first ride's departure less the walk to it, or `now` for a walk-only trip. There's no separate `Departs`, because it would always equal `LeaveBy`.
- Alternative lines are compared by when they get you off, not when they leave, so a later express beats an earlier local.
- Every ride's boarding stops (transfers and alternative lines included) join the board's batch, so a trip with a transfer costs nothing extra. With up to 20 favorites, 8 board stops, and 3 options × 3 rides × 2 alternative lines = 18 trip stops, a batch has at most 46 stops, well under the 100-stop limit.
- **Re-plan** only when a trip becomes infeasible (the last connection is gone), automatically at most once per 15 minutes per origin and destination, or when the plan expires. As built:
  - Infeasible: `MetadataTransitService.ReplanTripAsync` drops the cached plan and fetches again, guarded by `ReplanGuard` (15 minutes, with the cache's same-place rules: 150 m for the origin, 50 m for the destination). Inside the interval it returns the cached plan. It has to skip the cache, because `PlanTripAsync` would hand back the same dead plan for up to 3 hours.
  - Expired: `PlanTripAsync` fetches again after `PlanLifetime`, so whatever holds a plan must ask the cache again on refresh (free while it's fresh) instead of keeping its copy. As built in step 03: `MetadataTransitService.IsCurrentPlan(from, to, plan)` says, without a request, whether a held plan is still the cached one. The suggestion monitor's `isOutdated` hook uses it, so an expired or re-planned plan is found again on the next refresh. The old plan stays shown until the new one arrives, so offline the rider keeps the last good trip.

### 3. Two place monitors instead of one mode-switched monitor

`PlaceArrivalsMonitor` (`TrayTimes.Core/Commute/PlaceArrivalsMonitor.cs`) handles one discovery chosen by `FlyoutMode`. The flyout now needs two at the same time: the board (nearby lines at the center) and the suggestion (a plan).

- Make it generic over its context and discovery result (the result needs an `Origin` and the stop IDs to watch), and give each instance its own `TransitConsumer`. Keep `Nearby` for the board and `Commute` for the suggestion, so diagnostics and the log stay readable.
- **Fix:** `RegisterStops` clears both the Commute and Nearby consumers before setting its own (`PlaceArrivalsMonitor.cs:297-305`). With two instances, each must clear only its own.
- The commute-specific validity checks (`Configure`, `:92-98`; `NeedsDiscovery`, `:225`) become a hook that the suggestion instance supplies.
- **One location fix per refresh:** both monitors call `locate`, so the flyout must share one quick fix and its refinement between them, not ask Windows twice.
- The 750 ms coalescing window already merges the two monitors' departure requests, and Favorites', into one call.
- **As built (step 03):**
  - `PlaceArrivalsMonitor<TContext>` is generic over its context only. Both discoveries still return `PlaceStops`, because the Commute tab renders it until step 04.
  - `PlaceStops` gained `Plan` and `StopIds`. `StopIds` is the listed stops plus `PlaceBoard.BoardingStopIds(Plan)`.
  - The constructor takes the instance's `TransitConsumer` (Nearby or Commute) and two optional hooks:
    - `holds`: a discovery that no longer holds is dropped at once. The suggestion's is `found.Leg == context.Leg(here, now)`.
    - `isOutdated`: a discovery is found again in the background and stays shown until then. The suggestion's is "its plan isn't `IsCurrentPlan`".
  - Contexts: `BoardCenter` for the board, and `SuggestionContext(Places, DestinationId)`, which compares its places by sequence, for the suggestion.
  - `IPlaceArrivalsMonitor` is the non-generic view the flyout uses for "the current tab's monitor".
  - `SharedLocation` shares one lookup per refresh: the flyout calls `BeginRefresh()`, then refreshes the monitors. The first request decides whether it's a quick fix, and its refinement is shared.

### 4. Board composition (Core, pure)

Add `PlaceBoard.Around(NearbyLines nearby, IReadOnlyList<SavedStop> saved, GeoPoint center, int maxStops)`:

- Saved stops within `NearbyRadiusMeters` of the center come first, ordered by distance, each listing its saved lines (or all of them).
- After that, nearby lines not already covered by a pinned stop, each at its closest stop.
- **All saved stops** is the board without discovery: every saved stop, in saved order. It's the old Favorites tab, kept one click away at no cost.
- A board centered on a saved stop or place uses the same nearby-lines call at that point, cached for 24 hours within 150 m.
- **As built (step 03):**
  - `PlaceBoard.Around(nearby, saved, center, maxStops)`, `PlaceBoard.Saved(saved)`, and `PlaceBoard.RadiusMeters` (1 km, moved from the flyout).
  - `PlaceBoardResult.EveryLineAt` marks All routes stops, and `Lists(StopLine)` answers "is this line shown here".
  - A nearby line whose closest stop is a pinned stop is listed at that stop, rather than dropped.
  - Centers are `NearYou`, `NearPlace(FavoritePlace)`, `NearStop(SavedStop)`, and `AllSavedStops`. `BoardCenter.Resolve(here, places, saved)` applies D1, and `PointFrom(here)` is where the board looks for lines.
- **As built (step 04):**
  - The board monitor's context stays the picked `BoardCenter`. Its discovery (`TimesFlyoutWindow.FindBoardStopsAsync`) resolves the center and records it in `PlaceStops.Center`, with `EveryLineAt`.
  - Saved stops and places feed the pins and the fallback through an explicit invalidation, not the context. The flyout bumps a revision when either changes, discovery stamps `PlaceStops.Revision`, and the board's `isOutdated` hook compares the two. So the old board stays shown until the new one arrives, rather than blanking.
  - Near you, `PlaceBoard.SavedFirst` puts pinned stops back first after walk ranking.
  - `PlaceStops.Listing(arrivals, saved)` hides, at stops that aren't saved, the lines departing from an All routes stop (the step 03 "All routes duplicates" handoff). A stop left with no line to list is dropped from the board.

### 5. Flyout UI (app)

The flyout code-behind is already 1,863 lines, so new UI goes in user controls rather than growing it:

- `Controls/SuggestionCard.xaml` holds the card, and `Controls/TripView.xaml` holds the legs, options, and Back. The trip view loads with `x:Load` on first open (see the performance instructions).
- `TimesFlyoutWindow` keeps the map, the monitors, vehicles, and tracking, and switches between the board and the trip view.
- Remove: the `SelectorBar` (`TimesFlyoutWindow.xaml:322-342`), the commute header (`:435-476`), `ModeBar_SelectionChanged`/`SelectMode`/`EnterMode`'s mode branches (`TimesFlyoutWindow.xaml.cs:415-488`), the mode switches in `Render`, `OnPatternsChanged`, `FrameListedStopsAsync`, `RebuildStopPins`, and `Refresh`, plus `FlyoutMode` (`TrayTimes.Core/Models/FlyoutMode.cs`) and its setting (`SettingsService.FlyoutMode`, `TrayTimes/Services/SettingsService.cs:66-76`; the stored value is just ignored).
- Map layers: ~~the board uses `_commuteRouteLayer` for nearby lines and `_routeLayer` for pinned saved stops' lines.~~ As built in step 04: one layer, `_routeLayer`, draws the line of every departure the board lists, pinned saved stops included (`DrawRoutes`). The Favorites-only layer and `DrawFavoriteRoutes` are gone. The trip view clears `_routeLayer` and draws the itinerary in it. Each `RideLine.Path` is already the ridden part (`plan_shape`), so no clipping is needed when it's present. A `GeoMath` helper that projects the boarding and alighting stops onto the full shape is needed only as a fallback for an empty `Path`.
- New strings go in `Strings/en-us/Resources.resw` with `x:Uid` or `ResourceLoader`, and use format placeholders, not concatenation. Existing hard-coded strings move to resources only where a step rewrites them.

### 6. Persistence

- `CachedTripPlan` (`TransitCacheSnapshot.cs:14`) gains an optional `Itineraries` list. Entries without it (saved by older builds) are skipped on import. Keep `TransitCacheSnapshot.CurrentVersion` at 1: bumping it would throw away every cached nearby result and route detail (`MetadataTransitService.Import`, `:182`), costing calls for no reason. (Done in step 01. Walk and ride paths are saved as encoded polylines by `EncodedPathJsonConverter`, like shapes. An itinerary that fails validation is skipped, and its plan is fetched again.)
- Plans to **searched** destinations (step 06) stay in memory and aren't written to `transit-cache.json`. Only saved places, which the rider chose to store, persist.
- With `leave_time`/`arrival_time`, the plan cache key gains the requested time, rounded to 5 minutes. "Now" plans keep the current key.

### 7. Launch and Settings

- First manual launch with nothing saved opens the **flyout** (Near you works without setup), not Settings. The card's setup prompt leads to Settings (`App.ShowLaunchWindow`, `TrayTimes/App.xaml.cs:174-187`). See decision D2.
- Settings: Favorite places are described as trip destinations and board centers, and Favorite stops as jump targets pinned on the board. Remove copy that mentions tabs (for example `TimesFlyoutWindow.xaml.cs:951`).
- README: rewrite the Favorites, Commute, and Nearby bullets once the behavior ships, not before.

## Call budget

Free tier: 5 calls a minute, 1,500 a month (about 50 a day). Expected physical calls, with warm caches unless noted:

| Scenario | Today | After |
|----------|-------|-------|
| Open the flyout at a known place | 1 departures call (Nearby or Commute tab, plus Favorites) | 1 departures call (board, suggestion stops including transfers, and Favorites) |
| First open of the day at that place | + 1 `nearby_routes` (Nearby) or + 1 `plan` (Commute) | + 1 `nearby_routes` + 1 `plan` (both are shown now) |
| Open the trip view, switch options | n/a | 0 |
| Jump to a saved stop or place | (switching tabs) 1 departures call | 1 departures call, + 1 `nearby_routes` the first time that day |
| All saved stops | (Favorites tab) 0–1 | 0–1 |
| Pick another destination | 1 `plan` (cached 3 h) | 1 `plan` (cached 3 h) |
| Search an address | n/a | Azure Maps only, + 1 `plan` |
| Leave at / arrive by | n/a | 1 `plan` per distinct time (cached) |
| Keeping a trip's times live | n/a | 0 (its stops join the batch) |
| A trip becomes infeasible | n/a | ≤ 1 `plan` per 15 min |

The new cost is the second discovery on the first open of the day (about +1 call a day) plus exploring trips on purpose. Step 08 measures these through `RequestBudgetTests`.

## Decisions (confirmed 2026-10-18: every recommended default)

| # | Question | Recommended (chosen) | Alternative |
|---|----------|-------------|-------------|
| D1 | Where does each open start? | **Near you**, falling back to Near Home, then All saved stops | Remember the last jump |
| D2 | First launch with nothing saved | **Open the flyout**; the card prompts for places | Keep opening Settings first |
| D3 | Jumping to a saved stop | **Board around it** (nearby lines too; 1 cached call per spot per day) | Only the saved stops there (0 calls) |
| D4 | Keep "All saved stops" in the jump menu? | **Yes** (free, and it's the old Favorites tab) | Fallback only, not in the menu |
| D5 | Pinned saved stops on the board | **Their saved line only** (or all, if saved with All routes) | Always every line |
| D6 | Leave at / arrive by | **In scope, last** (step 07; can be skipped) | Leave it out |
| D7 | Walk-only option | **Show it** (`walk_fallback`) | Transit only, as today |

## Steps

### Operating rules

- Do one step per session, and resume an `in_progress` step before starting another. Update the table, the checkpoint, and the execution log when starting, when blocked, and when done.
- Before writing code, read this file, `AGENTS.md`, and the instruction files each step names. UI steps need accessibility, globalization, performance, and WinUI best practices. Every step needs design principles, code quality, and testing.
- Mark a step `done` only after its acceptance criteria, tests, and build pass. Record the evidence, not just "verified".
- Keep logs in English, and never record keys, URLs, stops, addresses, or coordinates (the existing log rule).
- Update the README only for behavior that has shipped.

Allowed statuses: `pending`, `in_progress`, `blocked`, `done`, `deferred`.

| Step | Deliverable | Size | Status | Evidence / handoff |
|------|-------------|------|--------|--------------------|
| 01 | Itinerary model, full plan parsing, persistence | M | done | 2026-10-18. Full suite 418/418 (was 408), app build 0 warnings. Model as built in §1 (lines per ride). Live fixture recorded (1 call). See the execution log |
| 02 | Live trip timing and re-plan rule | M | done | 2026-10-18. Step tests 64/64; full suite 438/438 (was 418); app build 0 warnings. `LiveTrip`, `ReplanGuard`, `MetadataTransitService.ReplanTripAsync` (§2). No API calls. See the execution log |
| 03 | Two place monitors, board composition, shared location | M | done | 2026-10-18. Step tests 108/108; full suite 467/467 (was 438); app build 0 warnings. Generic monitor, `SharedLocation`, `BoardCenter`, `PlaceBoard.Around`/`Saved`/`BoardingStopIds`, `IsCurrentPlan` (§2–§4). Tabs still work, on two monitors. No API calls. See the execution log |
| 04 | One board in the flyout: place chip, suggestion card, tooltip, launch | L | in_progress | 2026-10-18. Code complete. Full suite 482/482 (was 467); app build 0 warnings. Live smoke check passed (chip, card, Going to, D1 fallback, jump to Near Home). Left: the developer's packaged checks (see the checkpoint), then mark `done`. See the execution log |
| 05 | Trip view: legs, options, map, tracking, back | L | pending | Needs D7 |
| 06 | Destinations: search, stops, save as place | M | pending | |
| 07 | Leave at / arrive by | S | pending | Needs D6; may be `deferred` |
| 08 | Budget measurements, docs, acceptance checklist | S | pending | |

### Current checkpoint

- Step 03 is done (2026-10-18). D1–D7 were confirmed as recommended (2026-10-18).
- Step 04 is built and tested (2026-10-18), and stays `in_progress` until the developer runs the packaged checks below. Next action: those checks, then mark step 04 `done` and start step 05.
- **Step 04 packaged checks for the developer** (from its acceptance, not yet done by hand):
  1. With saved stops: open the jump menu, and check each stop shows its next departure with no request in the log. Jump to a saved stop and to All saved stops.
  2. Jump to Near Work (costs 1 `nearby_routes` the first time that day).
  3. Turn Windows location off: the board falls back to Near Home, the card says "guessed from the time of day", and the location-permission bar works when you pick Near you.
  4. Keyboard only: Tab reaches the place chip, then the card, then Going to. Escape hides the flyout. (Escape was sent by script, and the flyout hid, but the tool warned its key may not reach WinUI, so confirm by hand.)
  5. Narrator reads the card as "To Work, leave in 6 minutes, arrive 8:42, 1 transfer" (the name was checked through UI Automation). Check High Contrast.
  6. Star a stop near you: it moves to the top of the board without the list blanking.
- **Step 05 handoffs from step 04:**
  - **The card's click** (`SuggestionCard.TripInvoked`) now shows the first ride's vehicles, like a row (`TimesFlyoutWindow.SuggestionCard_TripInvoked`), or frames a walk. Step 05 replaces it with opening the trip view. The card's `Item.Trip` is the shown `LiveTrip`, and `Item.Leg` its origin and destination.
  - **Re-plan:** `TimesFlyoutWindow.SuggestionItem` calls `LiveTrip.Best`. When the plan has ride options but none can still be made and there's no walk-only trip, step 04's `LoadPlaceAsync` calls `ReplanTripAsync` under the guard and a diagnostics scope, then refreshes the suggestion if a new plan arrives. Step 05 should show "Finding a new route…" while that refresh is pending; it doesn't need to own the re-plan.
  - **Map:** there's one route layer now (`_routeLayer`, see §5). The trip view clears it and draws the itinerary there; returning to the board calls `EnterBoard` or `DrawRoutes` again.
  - **Back:** Escape still hides the flyout (`Escape_Invoked`). Step 05 makes it return to the board while the trip view is open.

### 01 — Itinerary model, full plan parsing, persistence

**Start here:** `TransitAppService.PlanTripAsync` (`:144-227`) and `BoardingStop` (`:398-415`), `TransitAppDtos.cs:192-214`, `TransitModels.cs:109-129`, `PlaceBoard.Commute`, `MetadataTransitService` (`:115-119`, `:174`, `:212-218`, `:271-272`), `TransitCacheSnapshot.cs`, `TransitAppServiceTests`, `MetadataTransitServiceTests`, `PlaceBoardTests`.

**Scope:**
- Add `start_time` to the result and leg DTOs. Map every leg into `TripItinerary`, finding the alighting stop and stop count from `stop_schedule_items`, and keep each departure's `rt_trip_id` and alighting time.
- Send `walk_fallback=true` and `consider_downtimes=true`.
- Move `PlaceBoard.Commute` to itineraries, with the board's output unchanged.
- Persist itineraries as an optional field without changing the snapshot version.

**Acceptance:**
- A fixture plan with a walk, two rides, a transfer walk, and a final walk maps to 5 legs with the right stops, times, stop counts, trip IDs, and `Transfers == 1`.
- A walk-only result maps to one `WalkLeg` and is ignored by `PlaceBoard.Commute`.
- `PlaceBoardTests` still pass with the same commute rows and ETAs.
- A snapshot round-trips itineraries. A snapshot from an older build (no itineraries) imports its other caches and skips its plans.
- Build the fixture from the spec's field names. When a key is available, record one real transfer plan, strip identifiers you don't need, and add it as a second fixture. That costs one call.

**Do not:** change any UI.

### 02 — Live trip timing and re-plan rule

**Start here:** the new model, `PlaceBoard.ArrivalAtDestination` (`PlaceBoard.cs:88-91`), `StopLine`, and `TrackedBus.MatchTolerance` (for matching departures without a trip ID).

**Scope:** `LiveTrip.Compute` as described above, plus a small re-plan policy (infeasible → at most once per 15 minutes per origin and destination; expired → re-plan), both in Core with a `TimeProvider`.

**Acceptance (tests):**
- Live departures replace planned ones for every ride.
- A late first bus pushes the transfer to the next departure of the second line.
- A canceled departure is skipped.
- Missing live data falls back to planned departures.
- No feasible connection returns Infeasible.
- LeaveBy equals the first ride's departure minus the walk before it.
- The re-plan guard holds across repeated infeasible results.

### 03 — Two place monitors, board composition, shared location

**Start here:** `PlaceArrivalsMonitor.cs` (all of it), `PlaceArrivalsMonitorTests`, `PlaceBoard.Nearby` (`:40-52`), `TimesFlyoutWindow.xaml.cs:490-519` (`LocatePlaceAsync`) and `:701-761` (discovery), and `PredictionCoordinator.SetBrowsingStops`.

**Scope:**
- Make `PlaceArrivalsMonitor` generic and set per consumer, and fix `RegisterStops` so each instance clears only its own consumer.
- Add `PlaceBoard.Around` and the All saved stops board.
- Add a board-center type (Here, saved place, saved stop, all saved) with D1's fallback order.
- Share one location fix per refresh.
- Suggestion discovery returns the plan and the stop IDs of every ride in the top options.

**Acceptance (tests):**
- Two instances keep both consumers' stops registered.
- Hiding one doesn't clear the other's.
- A refresh with both monitors due makes one departures call (extend `RequestBudgetTests`).
- A saved stop inside the radius is pinned first and suppresses the same line at a closer stop.
- A saved stop outside the radius isn't shown.
- An AllRoutes saved stop lists every line.
- The fallback order follows D1.
- One location request serves both monitors.

**Do not:** touch the XAML yet. Keep the three tabs working on top of the new monitor until step 04 removes them.

### 04 — One board in the flyout

**Start here:** `TimesFlyoutWindow.xaml`/`.xaml.cs` (the anchors in Architecture §5), `ArrivalItems.cs`, `App.xaml.cs:174-187` and `:332-369`, `SettingsService.cs:66-76`, `SettingsWindow.xaml` (the "Your commute" copy), `Strings/en-us/Resources.resw`, and `FlyoutMode.cs`.

**Scope:**
- Remove the tabs and `FlyoutMode`. Add the place chip and jump menu, and the suggestion card (a user control) with Going to (saved places only for now).
- Keep the "Work by" detail on board rows that start the suggested trip.
- The flyout exposes a suggestion summary for the tooltip, and the tooltip changes as described above.
- Make the launch change (D2) and update the Settings copy.

**Acceptance:**
- The open-to-board flow works for every center and fallback.
- The jump menu shows the next departures without making a request (check in the debug output).
- The card counts down and updates when the batch refreshes.
- Escape still hides the flyout, and the location-permission bar still works at Near you.
- Accessibility: every new control has `AutomationProperties.Name` and a stable `AutomationId` (`PlaceChip`, `SuggestionCard`, `DestinationButton`). Keyboard-only use reaches the chip, the card, and Going to in a logical order. Narrator announces the card as "To Work, leave in 6 minutes, arrive 8:42, 1 transfer". Text is readable in High Contrast.
- No new hard-coded strings.
- The developer runs the packaged checks (see Verification).

### 05 — Trip view

**Start here:** step 01–02 types, `MapHelpers`, `GeoMath`, the flyout's map layers and `FrameListedStopsAsync`, `TrackButton_Click`/`StartTracking` (`TimesFlyoutWindow.xaml.cs:1702-1733`), and the performance instructions (`x:Load`, `x:Bind`).

**Scope:**
- The `TripView` user control, with option switching, a templated legs list (a template selector for walk and ride), and live times from `LiveTrip`.
- First-ride time chips with bells, using the existing tracking.
- Draw the itinerary on the map (each ride's `RideLine.Path`, walk paths, transfer pins); Fit frames the trip.
- Back via the button, Escape, Backspace, and Alt+Left. Show the walk-only option (D7).

**Acceptance:**
- Opening and switching options makes no request.
- Times follow the shared batch.
- An infeasible trip shows "Finding a new route…" while the step 04 re-plan runs under the step 02 guard.
- Tracking from the trip view behaves like tracking from a row.
- If a fallback for an empty `RideLine.Path` is added, its `GeoMath` clipping helper has tests (stops off the line, repeated loops, a reversed direction).
- The accessibility, globalization, and keyboard checks from step 04 apply here too.

### 06 — Destinations

**Start here:** `GeocodingService.SearchAsync` (Azure Maps), `SearchStopsAsync`, `SettingsService` place APIs, and the security instructions (handling user input).

**Scope:**
- Going to → Search for a place… opens a search box. Results are addresses (Azure Maps, which costs no Transit call), plus stops when the query looks like a stop name or code (one `search_stops` call, on submit only, as Settings does).
- A chosen result plans a trip, and **Save as place** adds it to favorite places.
- A trip to a stop uses `to_global_stop_ids`.
- Searched destinations stay in memory only.

**Acceptance:**
- No request is made per keystroke.
- Without an Azure Maps key, search falls back to stops only and says why.
- Saving a result adds a custom place and updates the card's menu.
- Search queries and results are never logged.

### 07 — Leave at / arrive by (optional, D6)

**Scope:**
- A "Leave now ▾" control in the trip view: Leave now, Leave at…, Arrive by… (date and time pickers). It sends `leave_time` or `arrival_time`, and the cache key includes the time rounded to 5 minutes.
- A planned future trip shows its times without live departures (they're too far ahead), and switches to live as it gets near.

**Acceptance:**
- One plan call per distinct rounded time.
- Arrive-by options are sorted by departure, latest first.
- Returning to Leave now restores the live suggestion without a call when its plan is still cached.

### 08 — Budget measurements, docs, acceptance checklist

**Scope:**
- Add `RequestBudgetTests` cases for every Call budget row above (fake HTTP and clock), and correct that table with the measured numbers.
- Update the README (Favorites/Commute/Nearby → board, jump menu, suggestion, trip view) and the `plan` row in the migration doc's endpoint table.
- Leave no `in_progress` rows.

**Developer acceptance checklist:**
1. Open at home: the board shows nearby lines, with saved stops pinned first. The card shows the trip to work, with a countdown.
2. The jump menu shows next departures. Jumping to Work, a saved stop, and All saved stops each moves the board and the map.
3. Open the card. Switch options. Check the legs against the Transit app for the same trip. Track the first ride.
4. A trip with a transfer: the second ride's time changes when the first bus runs late.
5. Turn off Windows location: the board falls back to Near Home and the card says it guessed from the time of day.
6. Search an address. Plan to it. Save it as a place.
7. (If step 07 shipped) Arrive by 9:00 tomorrow.
8. Narrator and keyboard-only pass over the chip, card, trip view, and Back.
9. After a normal day, Settings' usage card shows well under 50 calls.

## Verification

From the repository root, in each fresh shell:

```powershell
$arch = $env:PROCESSOR_ARCHITECTURE
$Platform = if ($arch -eq 'AMD64') { 'x64' } else { $arch }

dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "<the step's test classes>"
dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
```

Run the full suite for steps 03, 04, and 08, which touch shared request paths. Name tests `MethodName_Scenario_ExpectedResult` and use the existing fake HTTP handler and clock (`RequestTestSupport.cs`), never live calls. If the developer has asked that the agent not build, test, or launch in a session, hand them these commands and record that in the log.

Each session appends an execution-log entry with the date, step, and status; the changes and decisions; files and new tests; the exact commands and their results (including anything not run); and the next action.

## Follow-ups (not in this plan)

- **Tracking across a transfer:** alert when to leave the first bus, and track the second.
- **Rider settings** for `walk_speed`, `accessibility_need`, and "minimize walking" (`walk_reluctance`).
- **Showing service alerts** on board rows and trip legs. They already arrive with departures.
- **Learning destinations** from use instead of the home/work rule.
- **Renaming** the `Commute` namespace and folder to match the new terms.

## Risks and open questions

- **Stale structure.** A 3-hour-old plan can miss a faster line that only runs at certain times. The infeasibility re-plan catches dead connections but not missed better ones. If that shows up in testing, shorten `PlanLifetime` for the suggestion only.
- ~~**Alighting stop.**~~ Resolved in step 01: the recorded plan confirms the last `stop_schedule_items` entry is the alighting stop (it matches `stops[end_stop_offset]`).
- **Live departures for later legs.** If `stop_departures` doesn't return far enough ahead for a transfer, `LiveTrip` falls back to the plan's own departures (3 per leg). Check this with a real trip that has a long transfer wait.
- **Flyout height.** At 680 px, the map, card, and trip legs compete for space. The splitter already lets riders trade map space for list space. Check a three-ride trip at the default map height.

## Execution log

### 2026-10-18 — Step 01: itinerary model, full plan parsing, persistence — done

**Changes**

- `TransitModels.cs`: replaced `TripOption` with `TripItinerary`, `TripLeg` (`WalkLeg`, `RideLeg`), `RideLine`, and `PlannedDeparture`. `TripPlan` now holds `Itineraries`.
- `TransitAppDtos.cs`: added `start_time` (results and legs), plus `end_stop_offset` and `plan_shape` on an itinerary's plan details.
- `TransitAppService.PlanTripAsync`:
  - Sends `walk_fallback=true` and `consider_downtimes=true`.
  - Maps every leg. `BoardingStop` became `PlanStop`, which serves both ends of a ride: it looks the stop up by ID, then by offset, then places the ID at the adjoining walk's end or the ridden shape's end.
  - Canceled departures are left out.
  - A result with another leg mode is left out whole.
- `PlaceBoard.Commute`: reads each itinerary's first ride. Every line of that ride is a way to board, on its first departure, with the walk before it. Board output is unchanged.
- Persistence:
  - `CachedTripPlan.Options` became an optional `Itineraries` list. Snapshot version stays 1.
  - `EncodedPathJsonConverter` saves walk and ride paths as encoded polylines.
  - Legs carry a `Kind` discriminator.
  - `MetadataTransitService.Import` validates every leg and line.
- No UI changes. The Commute tab compiles against the same `PlanTripAsync` and `PlaceBoard.Commute` signatures.

**Decisions**

- One ride can have several alternative lines (`RideLeg.Lines`), each with its own boarding stop, alighting stop, stop count, path, and departures. The live plan showed two buses on one ride boarding at different stop IDs, and the old code already listed each as its own row.
- `Transfers` is never negative; a walk-only trip has 0.
- `AlightAt` is nullable, and `Stops` is 0 when unknown, rather than dropping a trip the board could still show.
- Alighting reads `arrival_time` before `departure_time`, a small fix: the old code read the departure first, which can be a dwell minute later.
- Stops ridden is `stop_schedule_items` count − 1, else end offset − start offset. The plan's original "list length" rule would overcount by one.

**Small behavior changes in the Commute tab (from the request and parsing, not UI)**

- Routes avoid known outages.
- Canceled planned departures no longer count.
- The arrival estimate uses the alighting arrival time.
- Walk-only results are fetched but ignored until step 05.

**Live fixture**

- One `plan` call was made with the developer's key, between two public metro stations in another city (not saved places).
- It was trimmed to two trips and only the fields the client reads, with each itinerary's stops cut after the alighting stop.
- Saved as `TrayTimes.Tests/Transit/TransitApp/Fixtures/plan-transfer-montreal.json`. The test project copies it to the output folder.

**Tests**

- `TransitAppServiceTests`, replacing the two old plan tests:
  - `PlanTripAsync_Request_AsksForAWalkOnlyOptionAndRoutesAroundOutages`
  - `PlanTripAsync_TransferTrip_MapsEveryLegWithItsStopsTimesAndTrips` (5 legs, stops, times, stop counts, trip IDs, `Transfers == 1`, canceled departure left out)
  - `PlanTripAsync_WalkOnlyResult_MapsOneWalkLeg`
  - `PlanTripAsync_LegOfAnotherMode_LeavesOutThatTrip`
  - `PlanTripAsync_AbridgedStopList_FindsBoardingStopFromTheWalkThere` (adapted)
  - `PlanTripAsync_RecordedMetroTransfer_GetsOffAtTheLastScheduledStop`
  - `PlanTripAsync_RecordedRideWithAlternativeBuses_KeepsEachLineAtItsOwnStop`
- `PlaceBoardTests`: the existing commute tests are ported to itineraries with the same expected rows and ETAs. Added `Commute_RideWithAlternativeLines_ListsEachLineAtItsOwnStopShiftedByItsDeparture` and `Commute_WalkOnlyTrip_IsLeftOut`.
- `MetadataTransitServiceTests`:
  - The round trip now checks legs, paths, the alighting stop, and departures, and that the JSON has `Kind` and encoded paths.
  - Added `Import_SnapshotFromAnOlderBuild_KeepsItsOtherCachesAndSkipsItsPlans`.
  - The malformed-entries test now includes an itinerary that fails validation.
- `TransitModelsTests`: `Transfers_TwoRidesWithWalks_IsOne`, `Transfers_WalkOnly_IsZero`, and `Duration_Leg_IsEndLessStart` replace the `RideToDestination` test.

**Commands (ARM64, from the repository root)**

- Baseline before changes: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 408 passed, 0 failed.
- `dotnet test ... --filter "FullyQualifiedName~TransitAppServiceTests|FullyQualifiedName~MetadataTransitServiceTests|FullyQualifiedName~PlaceBoardTests|FullyQualifiedName~TransitModelsTests"` gave 86 passed, 0 failed.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 418 passed, 0 failed.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64` succeeded with 0 warnings and 0 errors.
- Not run: `dotnet run` (no UI changed in this step).

**Next action:** step 02 (`LiveTrip.Compute` and the re-plan guard), matching each ride per line.

### 2026-10-18 — Step 02: live trip timing and re-plan rule — done

**Changes**

- `TrayTimes.Core/Commute/LiveTrip.cs` (new): `LiveTrip.Compute`, plus the `LiveLeg`, `LiveWalk`, and `LiveRide` records. It's pure and takes `now` as a parameter, so it needs no `TimeProvider`. The algorithm as built is in §2.
- `TrayTimes.Core/Transit/ReplanGuard.cs` (new): allows a re-plan once per 15 minutes per origin and destination, with a `TimeProvider`. Entries expire, so memory stays bounded.
- `MetadataTransitService`:
  - Added `ReplanTripAsync`, which skips the cached plan when the guard allows.
  - Added `MetadataCache.Forget`. It keeps a pending load, because that load is already new.
  - `PlanTripAsync` and `ReplanTripAsync` share `PlanKey` and `LoadPlan`. `PlanTripAsync`'s behavior is unchanged.
- No UI changes. Nothing calls `LiveTrip` or `ReplanTripAsync` yet (steps 03–05).

**Decisions**

- **Re-plan skips the cache.** The plan didn't say how a re-plan gets past the 3-hour plan cache. Through `PlanTripAsync`, it would return the same dead plan. The guard lives in `MetadataTransitService`, next to the cache, so no caller can re-plan without it.
- **Expiry needs no new code.** The cache already fetches again after `PlanLifetime`. The handoff for step 03 says whatever holds a plan must ask again.
- **Fallback to the plan is per line, not per ride.** A line falls back to its planned departures when the batch has none for it at or after the ready time. That happens when the stop isn't in the batch, or when the batch doesn't reach far enough ahead (the "Live departures for later legs" risk). Planned departures for trips the batch knows are left out, so a canceled trip doesn't come back from the plan.
- **How the ride is chosen and timed.** The ride takes the line that gets off soonest, not the one that leaves first. Ride time comes from the plan's ride on that trip ID, else on the line's planned trip nearest in time. That nearest-trip rule replaces both the `MatchTolerance` window and the `leg.Ends − leg.Starts` fallback: it's never worse than either, and it follows time-of-day ride lengths.
- **Real-time comes from the batch only.** A ride timed from the plan has a null `Departure`, because the plan's own real-time flag can be hours old.
- **No separate `Departs`.** `LeaveBy` is the live trip's start.

**Tests (20 new)**

- `LiveTripTests` (13):
  - `Compute_LiveDeparturesForEveryRide_ReplaceThePlannedOnes`
  - `Compute_LateFirstBus_TakesTheSecondLinesNextDeparture`
  - `Compute_CanceledDeparture_TakesTheNextOne`
  - `Compute_CanceledDepartureTheBatchEndsWith_IsNotTakenFromThePlan`
  - `Compute_NoLiveDepartures_UsesThePlannedOnes`
  - `Compute_BatchWithoutTheTransferStop_TimesOnlyThatRideFromThePlan`
  - `Compute_LateFirstBusMissesTheLastConnection_ReturnsNull`
  - `Compute_EveryPlannedDepartureGone_ReturnsNull`
  - `Compute_WalkBeforeTheFirstRide_LeavesByItsDepartureLessTheWalk`
  - `Compute_RideWithAlternativeLines_TakesTheLineThatGetsOffSoonest`
  - `Compute_LateTrip_RidesAsLongAsThePlanRidesThatTrip`
  - `Compute_DepartureWithoutAPlannedTrip_RidesAsLongAsTheNearestPlannedTrip`
  - `Compute_WalkOnlyTrip_LeavesNowAndArrivesAfterTheWalk`
- `ReplanGuardTests` (5):
  - `TryAcquire_RepeatedForTheSameTrip_AllowsOncePerInterval`
  - `TryAcquire_AfterATrip_SharesItsGuardOnlyWithAboutTheSamePlaces` (4 data rows: origin and destination inside and outside the same-place distances)
- `MetadataTransitServiceTests` (2), counting HTTP attempts with the fake handler and clock:
  - `ReplanTripAsync_FreshCachedPlan_PlansAgainAndCachesTheNewPlan`
  - `ReplanTripAsync_TripStaysInfeasible_PlansAgainOncePerInterval`
- Acceptance coverage: live replaces planned; a late first bus pushes the transfer; a canceled departure is skipped; missing live data falls back to the plan; no connection returns null; LeaveBy is the departure less the walk; the guard holds across repeated infeasible results (both alone and through the service, measured in calls).

**Commands (ARM64, from the repository root)**

- Baseline before changes: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 418 passed, 0 failed.
- `dotnet test ... --filter "FullyQualifiedName~LiveTripTests|FullyQualifiedName~ReplanGuardTests|FullyQualifiedName~MetadataTransitServiceTests|FullyQualifiedName~PlaceBoardTests"` gave 64 passed, 0 failed, with no build warnings.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 438 passed, 0 failed.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64` succeeded with 0 warnings and 0 errors.
- Not run: `dotnet run` (no UI changed in this step). No Transit API calls were made.

**Next action:** confirm D1–D7 with the developer, then start step 03. Steps 03 and 05 have handoff notes in the checkpoint.

### 2026-10-18 — Step 03: two place monitors, board composition, shared location — done

**Decisions confirmed:** the developer took every recommended default for D1–D7.

**Changes**

- `PlaceArrivalsMonitor.cs`:
  - The monitor is now `PlaceArrivalsMonitor<TContext>`. Each instance has its own `TransitConsumer`, and `RegisterStops` sets only that consumer's stops. That's the fix: the old code cleared both Commute and Nearby, so a second instance would wipe out the first one's stops.
  - The commute checks became the `holds` hook. Added the `isOutdated` hook and `IPlaceArrivalsMonitor`.
  - `PlaceContext` (mode, places, destination) was replaced by `SuggestionContext` for the suggestion and `BoardCenter` for the board. The monitor no longer needs a `TimeProvider`, because the hooks bring their own clock.
  - `PlaceStops` gained `Plan` and `StopIds`.
- `SharedLocation.cs` (new): one location lookup per refresh, refinement included.
- `BoardCenter.cs` (new): `NearYou`, `NearPlace`, `NearStop`, and `AllSavedStops`, with `Resolve` (D1) and `PointFrom`.
- `PlaceBoard`:
  - Added `Around`, `Saved`, `BoardingStopIds`, `RadiusMeters` (moved from the flyout), and `TripOptions`.
  - `PlaceBoardResult` gained `EveryLineAt` and `Lists`.
- `MetadataTransitService.IsCurrentPlan` makes no request, backed by `MetadataCache.TryGetFresh`.
- `TimesFlyoutWindow`:
  - Two monitors: the board serves the Nearby tab, at `NearYou` with unchanged discovery, and the suggestion serves the Commute tab. `PlaceMonitor` picks the current tab's monitor.
  - `LoadPlaceAsync` calls `SharedLocation.BeginRefresh()` before refreshing.
  - The permission-state guard moved from the monitor's version to a lookup sequence, because the lookup is now shared.
  - The Commute discovery passes its plan along.
  - No XAML changed.

**Decisions**

- **Generic over the context only.** Both discoveries return `PlaceStops`, because the Commute tab renders it until step 04. A second result type would only add an adapter now. Step 04 can split it if the card needs to.
- **`holds` and `isOutdated` are separate hooks.**
  - A changed trip (another destination, or a guess that crosses noon) is wrong, so it's dropped at once.
  - An expired or re-planned plan is stale but still useful, so it's found again in the background and stays shown until then. Offline, the rider keeps the last good trip instead of an empty list.
  - This closes step 02's handoff that a held plan must be replaced after it expires.
- **One location lookup per refresh, not per time window.** The flyout starts each refresh with `BeginRefresh()`, so no timing rule is needed. A permission retry starts a refresh too, so it always looks again.
- **A pinned stop absorbs the nearby lines whose closest stop it is,** rather than dropping them. Otherwise D5 ("saved line only") would hide a nearby line from the whole board.
- **Plan stops watched:** every line of every ride in the plan's first 3 itineraries that have a ride (`TripOptions`), plus the listed stops. The Commute tab's batch now includes transfer stops, at no extra call.
- **Kept for step 04** (see the checkpoint): the Nearby tab doesn't pin saved stops or fall back yet, and the board's context doesn't yet include the saved stops and places.

**Small behavior changes in the Commute tab (no UI change)**

- Its departures batch includes every ride's boarding stop.
- A plan held past its lifetime, or replaced by a re-plan, is fetched again on the next refresh.

**Tests (29 new; full suite 438 → 467)**

- `PlaceArrivalsMonitorTests`:
  - Ported to the generic monitor. Tests that ran per mode now run for the board and the suggestion. Tests that switched modes now switch the board's center: `Configure_CenterChange_DoesNotPresentTheOldBoardAsTheNewOne` replaces `Configure_ModeChange_...`.
  - New: `Configure_SamePlacesInANewList_KeepsTheTrip`
  - New: `RefreshAsync_OutdatedTrip_FindsItAgainWhileStillShowingTheOldOne`
  - New: `RefreshAsync_SuggestionWithATransfer_FetchesEveryRidesBoardingStop`
  - New: `IsForeground_BoardAndSuggestionTogether_KeepBothConsumersStopsInTheBatch`
  - New: `IsForeground_HidingTheBoard_KeepsTheSuggestionsStopsRegistered`
  - New: `RefreshAsync_BoardAndSuggestionInOneRefresh_ShareOneLocationLookupAndItsRefinement`
  - New: `Constructor_ConsumerThatIsNotAPlace_Throws`
- `PlaceBoardTests`:
  - `Around_SavedStopInsideTheRadius_IsPinnedFirstAndSuppressesItsLineAtACloserStop`
  - `Around_SavedStopOutsideTheRadius_IsNotShown`
  - `Around_SavedStopForOneLine_ListsJustThatLine`
  - `Around_AllRoutesSavedStop_ListsEveryLine`
  - `Around_NearbyLineWhoseClosestStopIsPinned_IsListedThere`
  - `Around_TwoLinesSavedAtOneStop_PinsTheStopOnceWithBoth`
  - `Around_MoreStopsThanMax_KeepsThePinnedStopsFirstWithinTheTotal`
  - `Saved_SeveralSavedStops_ListsEachOnceInSavedOrderWithItsLines`
  - `BoardingStopIds_TransferTripWithAlternativeLines_ListsEveryRidesBoardingStop`
  - `BoardingStopIds_MoreTripsThanTheOptions_WatchesOnlyTheTopTransitTrips`
- `BoardCenterTests` (new, 6): the D1 order (Near you → Near Home → All saved stops → Near you to ask for location), picked centers ignoring location, and `PointFrom`.
- `SharedLocationTests` (new, 2): one lookup per refresh, shared even after it arrives, and a new lookup after `BeginRefresh`.
- `MetadataTransitServiceTests`:
  - `IsCurrentPlan_FreshPlanFromAboutTheSamePlace_IsTrueWithoutARequest`
  - `IsCurrentPlan_AfterThePlanLifetime_IsFalse`
  - `IsCurrentPlan_AfterAReplan_IsFalseForTheOldPlanOnly`
- `RequestBudgetTests`:
  - `BoardSuggestionAndFavorites_DueTogether_ShareOneCallWithTheTripsTransferStop`: 1 call on open, 1 a minute later, and the batch holds the Favorites, board, boarding, and transfer stops.
  - The existing place-monitor helpers moved to the generic monitor.
- Acceptance coverage:
  - Two instances keep both consumers' stops, and hiding one keeps the other's. Both were checked against the old `RegisterStops`: with it put back, those 2 tests failed; then the fix was restored.
  - Both monitors due together make one departures call.
  - A pinned stop suppresses its line at a closer stop, a stop outside the radius isn't shown, and an All routes stop lists every line.
  - D1's fallback order holds, and one location request serves both monitors.

**Commands (ARM64, from the repository root)**

- Baseline before changes: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 438 passed, 0 failed.
- `dotnet test ... --filter "FullyQualifiedName~PlaceArrivalsMonitorTests|FullyQualifiedName~PlaceBoardTests|FullyQualifiedName~BoardCenterTests|FullyQualifiedName~SharedLocationTests|FullyQualifiedName~RequestBudgetTests|FullyQualifiedName~MetadataTransitServiceTests"` gave 108 passed, 0 failed, with no build warnings.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 467 passed, 0 failed.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64` succeeded with 0 warnings and 0 errors.
- Not run: `dotnet run`. No XAML or visible UI changed, but the Nearby and Commute tabs now run on the two new monitors, so the developer should open both tabs once to smoke-test them. No Transit API calls were made.

**Next action:** step 04 (one board in the flyout). Start with the step 03 handoffs in the checkpoint.

### 2026-10-18 — Step 04: one board in the flyout — in_progress (built and tested; developer checks left)

**Changes**

- **Tabs gone.** The `SelectorBar`, the commute header, the mode switches, and `FlyoutMode` (file and `SettingsService.FlyoutMode`) are removed. A stored mode is just ignored. `TransitTrigger.ModeChanged` became `CenterChanged`.
- **Place chip** (`PlaceChip`, a `DropDownButton`).
  - It shows where the board is: the resolved center, so a fallback reads "Near Home".
  - Its jump menu lists Near you, "Near {place}", each saved stop with its next departure ("Oak & 5th · 14 in 4 min"), All saved stops (only when stops are saved), and Manage favorites….
  - Next departures come from the favorites' snapshot (`ArrivalsMonitor.Latest`), so opening the menu costs nothing.
  - Each open starts near you (D1).
- **Board.**
  - `FindBoardStopsAsync` resolves the center, then builds the board with `PlaceBoard.Around`, or `PlaceBoard.Saved` for All saved stops.
  - Near you, stops are ranked by walk and `SavedFirst` keeps the pins first. Rows there get walk times and too-soon dimming; at other centers they show only the distance from the center, and All saved stops shows none.
  - Saved stops show up to 4 routes, other stops 3.
  - Rows that start the suggested trip keep "Work by 8:42 · 1 transfer", from the suggestion's `Trips`.
- **Suggestion card** (`Controls/SuggestionCard.xaml`, items in `Controls/SuggestionCardItems.cs`).
  - It shows `LiveTrip.Best` at live times: "To Work · leave in 6 min", the route chips, and "arrive 8:42 · 1 transfer", plus "From Home · guessed from the time of day" without a location.
  - Its notices: the setup prompt with **Set up places**, "You're at Work", "No trip to Work right now", "Planning a trip…", and "Can't plan a trip right now".
  - **Going to** sits beside the card (saved places, Where I'm likely headed, Edit places…). Changing the destination re-plans only the card; the board keeps its list.
  - A 15-second clock re-renders, so the countdown and the rows' minutes move between refreshes. A render rebuilds only what changed.
- **Refreshing.** `LoadPlaceAsync` calls `BeginRefresh()`, then refreshes both monitors together, each under its own diagnostics scope (Nearby, Commute). A board error shows in the status bar; a suggestion error shows on the card. Manual refresh also refreshes the favorites, which join the same batch.
- **Map.**
  - One route layer for every listed line (§5).
  - A center marker near you or at a saved place.
  - The map view is kept for the next open only when the board was near you; otherwise the next open frames the board near you.
- **Tooltip.** The flyout exposes `TripSummary` ("Work: leave in 6 min, 14 at 8:12") and `SavedStopIds` (the saved stops on the board, in board order), and raises `SummaryChanged`. `App.UpdateTooltip` shows the tracked bus, then the trip, then as many of those saved stops as fit in 127 characters (`TrayTooltip.Compose`, in Core).
- **Launch (D2).** A manual launch always opens the flyout. `ShowLaunchWindow` and `SettingsService.HasShownLaunchWindow` are removed.
- **Settings copy.**
  - The section heading is now "Your places and stops".
  - The places help describes the trip suggestion and showing departures near a place, not the Commute tab.
  - The saved stops description says they're pinned near you and a click away, replacing "The first three appear in the tray tooltip".
- **Strings.** Every new or rewritten string is in `Resources.resw`, read through a new `Views/AppStrings.cs` (one shared `ResourceLoader`, culture-aware `Format`). Strings this step didn't rewrite (tracking captions, the tray menu) are unchanged.
- **Fixes found on the way.** Commit `1597e98` had stripped three private-use glyphs to empty strings: the save star (`U+E734`/`U+E735`), so stars rendered blank, and the custom place's Delete button in Settings (`U+E74D`). They're restored as `\u` escapes, so an editor can't strip them again. The place glyphs moved to `MapHelpers.PlaceGlyph`, also as escapes.

**Decisions**

- **Invalidating the board on saved changes** uses a revision and the `isOutdated` hook, not a bigger context (§4). With the context approach, starring a stop would have blanked the board until the walk ranking came back.
- **The card's best option** is the transit trip that arrives first at live times (ties: fewer transfers, then leaving later). The walk-only trip shows only when no transit option can still be made. The trip view (step 05) shows the walk as an option (D7).
- **Re-planning.** When a plan has ride options but none can still be made and there's no walk-only trip, the load path asks for a new plan under the step 02 guard, then refreshes the suggestion. Until step 05 adds a progress notice, the card shows "No trip to … right now" while that happens.
- **The card is a button now** (keyboard and Narrator need it focusable). Until the trip view exists, selecting it shows the first ride's vehicles, as a row does.
- **One map layer** instead of two (§5): the board draws every listed departure's line, so pinned stops' lines need no layer of their own.
- **The tooltip lists no saved stops until the board has resolved its center**, rather than guessing them.
- **The setup prompt** shows whenever there's no Home, read from settings directly, so it doesn't wait for a location.

**Files**

- New: `TrayTimes/Controls/SuggestionCard.xaml(.cs)`, `TrayTimes/Controls/SuggestionCardItems.cs`, `TrayTimes/Views/AppStrings.cs`, `TrayTimes.Core/Models/TrayTooltip.cs`.
- Deleted: `TrayTimes.Core/Models/FlyoutMode.cs`.
- Changed: `TimesFlyoutWindow.xaml(.cs)`, `App.xaml.cs`, `SettingsService.cs`, `MapHelpers.cs`, `ArrivalItems.cs`, `SettingsWindow.xaml.cs` (glyph), `Resources.resw`, `PlaceArrivalsMonitor.cs` (`PlaceStops.EveryLineAt`, `Center`, `Revision`, `Listing`), `PlaceBoard.cs` (`SavedFirst`), `LiveTrip.cs` (`Best`), `TransitRequestDiagnostics.cs` (trigger rename).

**Tests (15 new; full suite 467 → 482)**

- `LiveTripTests`:
  - `Best_SeveralOptions_TakesTheOneThatGetsThereFirstAtLiveTimes`
  - `Best_SameArrival_TakesTheOneWithFewerTransfers`
  - `Best_FasterOptionAfterTheFirstThree_IsNotConsidered`
  - `Best_NoRideLeft_FallsBackToTheWalkOnlyTrip`
  - `Best_WalkOnlyTripAndAFeasibleRide_TakesTheRide`
  - `Best_NothingLeftToMake_ReturnsNull`
- `PlaceStopsTests` (new):
  - `Listing_LineDepartingFromAnAllRoutesStop_IsNotListedAgainAtAnotherStop`
  - `Listing_LineDepartingFromAnAllRoutesStop_IsStillListedAtAnotherSavedStop`
  - `Listing_OtherDirectionOfThatLine_IsStillListed`
  - `Listing_StopThatListsEveryLine_ListsLinesItWasntSavedFor`
  - `Listing_NoLineFilter_ListsEveryLine`
- `PlaceBoardTests`: `SavedFirst_WalkRankingMovedANearbyStopAhead_PutsTheSavedStopsBackFirstInTheirNewOrder`
- `TrayTooltipTests` (new):
  - `Compose_RoomForSomeOptionalLines_AddsThemInOrderUntilOneDoesNotFit`
  - `Compose_RequiredLinesTooLong_CutsThemShortWithAnEllipsis`
  - `Compose_EverythingFits_JoinsEveryLine`
- The flyout and the card are UI code-behind (exempt from unit tests). Their logic that can be tested lives in Core and is covered above.

**Commands (ARM64, from the repository root)**

- Baseline before changes: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 467 passed, 0 failed.
- `dotnet test ... --filter "FullyQualifiedName~LiveTripTests|FullyQualifiedName~PlaceBoardTests|FullyQualifiedName~PlaceStopsTests|FullyQualifiedName~TrayTooltipTests"` gave 50 passed, 0 failed.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64`:
  - First run: succeeded with 16 `WUI2011` warnings. The card's `x:Bind`s default to OneTime, which is intended, because `SuggestionCard.Show` calls `Bindings.Update()`. Declared with `x:DefaultBindMode="OneTime"`, as the flyout's templates already do.
  - Then: 0 warnings, 0 errors.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64` gave 482 passed, 0 failed.
- A search of the new XAML and code found no new hard-coded user-facing strings.
- `dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64 --no-build` launched the packaged app (no instance was running before). It was then checked with `winapp ui`:
  - On launch, before the location arrived: the chip read "Where to show departures: Near Home" (the D1 fallback), and the card "To Home, leave in 7 minutes, arrive 12:12 AM, no transfers. From Work · guessed …".
  - Once the location arrived: "Near you", with walk times, the restored stars, a "Work by" row detail, and the card "To Work · leave in 1 min" with a route chip and Going to beside it. Screenshots are in the session scratchpad, not the repository.
  - The jump menu listed Near you (checked), Near Home, Near Work, and Manage favorites…. This machine has no saved stops, so their entries and All saved stops didn't appear, as designed.
  - Jumping to Near Home: the chip read "Near Home", the map showed the home marker, rows showed distance without walk times, and the countdown had moved on to "leave now".
  - After Escape was sent, the flyout hid and the process kept responding. The tool warned its key may not reach WinUI, so this is checklist item 4.
  - No crash log was written.
- Transit calls during the run, from the app's log: `NearbyRoutes` ×1 (around Home, for the fallback before the location arrived), `Plan` ×1 (the guessed trip), and `Departures` ×3. That's within the plan's first-open estimate (+1 `nearby_routes`, +1 `plan`).
  - Two of the `Departures` batches were labeled `trigger=Retry`. Only the Grant-location-permission button and the "Try again" notice start a Retry. The scripted checks invoked neither, and the location became available between the first check and the screenshot. So they were most likely the location permission being granted on the visible flyout. Worth a look during the checks, not a known bug.
- The app was left running in the tray for the developer's checks.

**Next action:** the developer runs the step 04 packaged checks in the checkpoint, then marks step 04 `done`. Then step 05, starting from its handoffs.
