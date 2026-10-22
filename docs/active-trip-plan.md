# Active trip mode (plan)

Press **Go** on a trip and Tray Times follows it until you arrive. The tray icon counts down to your next action. A notification, and the flyout itself, tell you when to leave, get off, and transfer, and the times update when the live departures change. Opening the flyout during a trip shows its progress, not a fresh board.

Created: 2026-10-19. Branch: `feature/transit-app-api` (or a branch from it). Status: D1–D7 confirmed (2026-10-19), with D6 changed to ask before resuming and D7 to as few checks as possible. It builds on the completed [board and trip planner plan](board-and-trip-planner-plan.md) and replaces its "Tracking across a transfer" follow-up.

## Why

The trip view already knows every step of a trip and keeps it live from the shared departures batch. But it does so only while the flyout is open, and only the first ride can be tracked (`TrackedBus`). Once the rider leaves their desk, the app knows nothing about the transfer, the stop to get off at, or a late second bus.

The rider is on a laptop. It's online through a phone's hotspot or a cellular modem, and the lid closes and opens along the way. So this is a timeline of when to act, not live navigation:

- **The app doesn't know where the rider is.** Windows location on a laptop is Wi-Fi based and often missing on a hotspot. Steps advance by the clock and the live departure times, and the rider can say "I missed it".
- **The connection comes and goes.** The trip keeps counting down from what it last heard, and checks again when the network comes back or the PC wakes.
- **Calls are scarce** (about 50 a day). The app checks only around the moments where a fresh answer could change what the rider should do.

## Target design

### Starting a trip

The trip view's header gains the time control and **Go** (the developer's sketch, 2026-10-19). The time control's own row goes away, so the options and legs move up:

```
┌──────────────────────────────────────────┐
│       [map: the selected option]         │
├─────────────────── ══ ───────────────────┤
│ ←  To Work          [Leave now ▾] [ GO ] │  one header row
│    arrive 6:00 PM                        │
│ 6:05 PM   6:00 PM   6:03 PM · 1 transfer │  options, unchanged
│ 🚶 Walk 10 min · 0.4 mi                  │
│ [15] Chicago / Drexel · leaves 5:39 PM   │
│ …                                        │
└──────────────────────────────────────────┘
```

- **Go** (accent style) starts the selected option at the selected time: Leave now, Leave at, or Arrive by. Its accessible name says what it starts ("Start trip to Work, leaving 5:29 PM").
- The flyout is 400 px wide, so the dropdown shows a short label in the header ("Leave now", "Leave 8:00 AM", "Arrive by Tue 9:00 AM"), with the full date and time in its tooltip and accessible name. The heading trims long destination names (searched addresses) instead of wrapping into the buttons.
- Leave now starts at once. A future trip is **scheduled**: no calls until its first check, just before the Head out alert, and the normal tray icon until it's within 99 minutes. Then it runs like Leave now (D5).
- When the origin was guessed (no location), Go names the place it starts from ("From Home"), since every time depends on it.
- Go replaces a tracked bus. Ringing a bell during a trip asks before ending the trip (D1).

### The steps

A trip is a list of steps, each with the time it needs the rider to act. They come from the itinerary's legs at live times (`LiveTrip`):

| Step | Shown as | Action time | Alerts |
|---|---|---|---|
| Get ready | "Leave in 6 min" · Walk 4 min to Oak & 5th for the 14 | Leave by (first departure − the walk) | **Head out** at the rider's lead time before leaving (the existing *Head out* setting). **Leave now** at leave-by |
| To the stop | "The 14 leaves in 3 min" | The ride's departure | **Ride changed** when it moves ≥ 3 min after Head out. **Connection changed** when it's canceled |
| Ride | "Get off at State St in 9 min" | Getting off | **Get off soon** 2 min before, with what comes next: the walk and the wait for a transfer ("Then wait 4 min for the 30 at 8:30", "Then walk 2 min and wait 3 min for the 30 at 8:30"), else "Then walk 1 min to the 30 at 8:30" |
| Transfer | "The 30 leaves in 4 min" | The next ride's departure | **Connection changed** when a late first bus misses it ("Take the 8:45 instead · arrive 9:03") |
| Last walk | "Arrive at Work at 8:42" | Arrival | none |

- **Advancing.** A step ends by the clock. A ride counts as taken once its last known departure time passes. The app doesn't check that the bus left, and never asks "Did you get on?" (D4).
- **I missed it.** The current step, its notification, and the tray menu offer **I missed it**. It re-times the trip from the next departure at that stop, using data already held (free). When no departure can still make the trip, it re-plans from that stop.
- **Re-timing.** The next ride keeps its trip (by trip ID) while it can still be caught. Later rides are re-timed the way `LiveTrip.Compute` does today, so a late first bus moves the transfer to the next departure, and the rider hears about it once. Before Head out, shifts just move the countdown. An arrival that moves updates the status text, not a notification of its own.
- **Live get-off time.** The alighting stop joins the watched stops, so the same trip's predicted time there corrects "Get off at…". A route's last stop may list no departure for it; then the ride's departure plus the planned ride time is used. Step 02's live check: the stop to get off at listed the same trip IDs as the boarding stop (3 of 6, 16 minutes later, the planned ride time); the other 3 were beyond its 6-departure window.
- **Ending.** The trip ends quietly 5 minutes after the arrival time, or when the rider chooses **End trip**.

### Tray icon, tooltip, and menu

- The badge (`EtaTrayIcon`) shows minutes to the next action time. It's blue, and turns red when it's time to act: within the head-out lead before leaving, when the bus is due, and 2 min before getting off. Over 99 minutes, or while a future trip is scheduled, the app icon stays.
- The tooltip's first line is the trip: "Trip to Work: get off at State St in 9 min (8:25)". The rest is as today.
- The menu's **Show trip**, **I missed it**, and **End trip** replace "Stop tracking route 14" while a trip is active.

### Notifications

- Trips and tracking share one notification tag, so a newer alert replaces the older one in Action Center, as tracking does now.
- Buttons: **I missed it** on boarding and transfer alerts, and **End trip** on all of them. Selecting the notification opens the flyout to the trip.
- After sleep or a long offline stretch, alerts that came due meanwhile collapse into the latest one. An alert more than 5 minutes stale never pops the flyout.
- **Words follow where the rider is.** A transfer ride's departure isn't news while the rider is still on the ride before it: they need where to get off and how long they'll wait after. So when the next ride moves during a ride, the text is "Get off at State St in 8 min. Then wait 3 min for the 30 at 8:25. You'll arrive at Work at 8:38."; heading to its stop, it's "It leaves Lake St in 3 min." A wait of under a minute isn't mentioned.

### The flyout pops up

On **Leave now**, **Get off soon**, and **Connection changed** (D3), the flyout opens beside the tray on its own (D2):

- It doesn't take focus. It shows on top without activation, so typing stays in the rider's app. It hides itself after 30 seconds unless the rider clicks into it. Once clicked, it's the normal flyout with light-dismiss.
- It doesn't pop up when Windows says the user is busy (`SHQueryUserNotificationState`: full-screen, presentation mode, busy). The notification still goes through, under Windows' own rules. Whether that call reports Do Not Disturb is checked in step 04.
- Settings → Ride alerts gets **Pop up for trip steps**, on by default.

### Opening the flyout during a trip

```
┌──────────────────────────────────────────┐
│ ● Departures                         ⚙   │  the board's places selector hides during progress
├──────────────────────────────────────────┤
│       [map: the trip, current leg bold]  │
├─────────────────── ══ ───────────────────┤
│ ←  Trip to Work               [End trip] │  the same header row; End trip where Go was
│    arrive 8:42                           │
│ ┌──────────────────────────────────────┐ │
│ │ Leave in 6 min                       │ │  the current step, large
│ │ Walk 4 min to Oak & 5th for the 14   │ │
│ │                        [I missed it] │ │  only when a ride is next
│ └──────────────────────────────────────┘ │
│ ● Walk 4 min to Oak & 5th · 8:08         │  current
│ ○ [14] Downtown · leaves 8:12 · live     │
│ ○ Get off at State St · 8:25             │
│ ○ Walk 1 min · [30] leaves 8:30          │
│ ○ Work · 8:42                            │
└──────────────────────────────────────────┘
```

- The header keeps the trip view's layout, so the trip's main action stays in the same place: **End trip** (standard style, not accent) takes Go's spot, and the time dropdown is gone because the trip's time is set.
- Every open during an active trip shows its progress, not the board. Back goes to the board, where the suggestion card becomes the active trip ("Trip to Work · get off in 9 min ›"). Selecting the card returns to the trip. The next open shows progress again.
- The map draws the trip as the trip view does (`TripMapRenderer`), with the current leg emphasized. Its camera is kept for the active trip, including after the hidden flyout is torn down and recreated. Ordinary progress updates do not reframe it.
- While the flyout is open, the map also shows the upcoming or current ride's own vehicle when the agency publishes one, through the existing `onlyThisTrip` path and only at the Normal budget level. This includes Get ready within the existing 30-minute live window.
- On the board, the places selector sits beside Settings in the top header. Refresh and fit are in the map's context menu, with F5 and Ctrl+0 shortcuts. **I missed it** is a compact subtle action rather than a prominent button.
- Done steps are dimmed with a check. The current step is marked, and Narrator announces it when it changes (`LiveSetting`).

## Architecture

### 1. Trip progress (Core, pure)

`TrayTimes.Core/Commute/TripProgress.cs`, immutable like `TrackedBus`: each check or clock tick produces the next state. As built in step 01:

```csharp
public enum TripStepKind { GetReady, ToStop, Ride, Transfer, LastWalk, Done }
public enum TripAlert { HeadOut, LeaveNow, RideChanged, ConnectionChanged, GetOffSoon }

public sealed record RideChoice(int Line, string? TripId, DateTimeOffset Departs, DateTimeOffset Alights);   // one per RideLeg
public sealed record TripStep(TripStepKind Kind, int Ride, int Leg, DateTimeOffset ActionAt, bool IsUrgent)
{
    public bool AwaitsReplan { get; init; }                          // step 02: waiting for a new plan at the stranded ride's stop (or the origin); no countdown
    public int MinutesUntil(DateTimeOffset now);                     // step 02: rounded up, so it reads 2 as the step turns urgent
}
public sealed record TripUpdate(TripProgress Progress, TripAlert? Alert = null, DateTimeOffset? DueAt = null);

public sealed record TripProgress(TripItinerary Itinerary, IReadOnlyList<RideChoice> Rides, DateTimeOffset NotBefore)
{
    // State: LeftAt, Known + KnownAt (departures at the trip's stops), MissedTrips, StrandedRide (NeedsReplan is
    // derived from it), and what the rider was told: HeadOutSent, LeaveNowSent, GetOffAlerts, AnnouncedRide + AnnouncedDeparture.
    public LiveTrip Trip { get; }                                    // derived; the trip view's leg types
    public static TripProgress Start(LiveTrip trip, TripTime time, PredictionSnapshot? snapshot, int leadMinutes, DateTimeOffset now);
    public TripUpdate Advance(PredictionSnapshot? snapshot, int leadMinutes, DateTimeOffset now);   // null snapshot: a clock tick
    public TripProgress Missed(DateTimeOffset now);
    public TripStep Step(DateTimeOffset now, int leadMinutes);
    public IReadOnlyList<string> WatchedStops(DateTimeOffset now);
    public DateTimeOffset? NextCheck(int leadMinutes, DateTimeOffset now, DateTimeOffset? lastAttempt = null);
    public DateTimeOffset? NextAlertAt(int leadMinutes);
    public bool IsOver(DateTimeOffset now);                         // arrival + 5 min
    public GeoPoint? ReplanFrom(DateTimeOffset now);                // step 02: the stop of the first ride not taken; null before leaving (the origin)
    public TripProgress? Continue(TripPlan plan, DateTimeOffset now); // step 02: null when the plan has no trip left
}

public sealed record ActiveTrip(TripProgress Progress, CommuteLeg Leg, TripDestination Destination)   // step 02: what's saved
{
    public bool IsValid();                                           // a damaged file can't crash the trip
}
```

- `Trip` is derived from the choices, so only the inputs are saved. The trip view's `TripWalkItem` and `TripRideItem` can show it as they show a `LiveTrip` now.
- Uses `LiveTrip`'s boarding rules: `Board` and `RideTime` became `internal`, and `Board` takes trips to skip, by `LiveTrip.TripKey` (trip ID, else time). `FirstRideDepartures` uses the same key.
- Finds a chosen trip in the departures with `TripMatch.Find` (`TrayTimes.Core/Tracking/TripMatch.cs`), extracted from `TrackedBus.FindIn`, which now calls it. `TrackedBus.MatchTolerance` moved to `TripMatch.Tolerance`. There's no passage check (D4).
- `NeedsReplan` is a property, not an alert: the service re-plans and tells the rider about the result. As built in step 02, `Continue` times the new plan's best trip (`LiveTrip.Best`) from when the rider can be at `ReplanFrom`'s stop, keeps the rides taken and the walks to that stop in front of the new legs, and keeps what the rider was told; before leaving, the new trip replaces the old one. A missed ride counts as not taken, so I missed it with nothing left re-plans from that ride's stop.
- Stranded rides (step 02 review): `StrandedRide` (saved; `NeedsReplan` is derived from it) is the first ride no departure can make. From it on, rides raise no Get off soon and get no checks, and `Step` is To the stop or Transfer at that ride's stop with `AwaitsReplan` (its `ActionAt` is when the rider gets there; no countdown). Before the rider has left, a stranded trip sends no Head out or Leave now and doesn't set `LeftAt`: they wait at the origin (`GetReady` with `AwaitsReplan`). I missed it does nothing while the step already awaits a plan.
- A ride counts as taken (by the clock) only after the rides before it, and only if the rider could have been at its stop by its departure. Before, a later ride whose time passed counted as taken even when an earlier ride ran too late to reach it, which also un-stranded a stranded trip.
- `NotBefore` is the leave-at time for a Leave at trip, else when Go was pressed, so a re-timed trip never boards before the rider said they'd leave.
- Changes (`RideChanged`, `ConnectionChanged`) are raised only after the first alert or a Go within the lead time, when the rider has been told a time. Before that, they only move the countdown.
- At most one alert per `Advance`. When several came due at once (after sleep), the latest wins, and `DueAt` says when it came due, for step 04's staleness rule.
- `TripProgressJsonContext` (source-generated, as Release builds are trimmed) saves it for D6.

### 2. Active trip service (app)

`TrayTimes/Services/ActiveTripService.cs`, beside `BusTrackingService` and shaped like it: UI thread, `DispatcherQueueTimer`, `TimeProvider`, a `Changed` event. The tests link it in the way they link `BusTrackingService`.

- Polls `PredictionCoordinator.GetAsync(WatchedStops, maxAge, combineBrowsing: true)` under a new `TransitConsumer.ActiveTrip`, which `CurrentPriority` maps to `Urgent`, like `Tracking`. While the flyout is open, the board's stops still ride along.
- Re-plans with `MetadataTransitService.ReplanTripAsync` from the current stop (or the origin, before leaving), under the existing 15-minute `ReplanGuard`. Then `LiveTrip.Best` gives a new `TripProgress`, which keeps the rides already taken for display.
- Schedules from `NextCheck`. Without a connection, a check fails, and one more is made when `NetworkStatusChanged` fires. (Planned as skipping checks while `ConnectivityService.IsInternetAvailable` is false; as built, that flag is only a hint and never stops a check. See the as-built notes below.)
- Re-evaluates as soon as the PC resumes (`Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged`), then checks if online. A suspended `DispatcherQueueTimer` fires late, so the clock is always read anew.
- Saves the trip to `LocalState\active-trip.json` on every change and deletes it at the end. Its contents are never logged.
- **Resuming after a restart asks first (D6).** On launch, a saved trip that's already over is deleted. One that isn't over waits for the rider, making no checks and leaving the app icon in the tray. A manual launch opens the flyout to a "Resume your trip to Work?" card with **Resume** and **End trip**. A Windows startup launch, which opens no window, sends a notification with the same two buttons. Resume re-times the trip by the clock and checks once. When the trip's arrival passes unanswered, it's deleted. Sleep isn't a restart: the app keeps running, so the trip goes on by itself.
- Applies the *Keep the PC awake while tracking* setting. That can't stop a lid-close sleep; the resume path covers that.

As built in step 02 (the API step 03 uses):

```csharp
internal sealed class ActiveTripService      // App.Current.Trips
{
    public const int MaxBadgeMinutes = 99;
    public event EventHandler? Changed;                  // start, end, pending, progress, and each minute of the countdown
    public event EventHandler<TripUpdate>? Alerted;      // after each alert's notification, with DueAt (step 04's pop-up)
    public ActiveTrip? Current { get; }                  // Progress, Leg (names), Destination (re-planning)
    public bool IsPendingResume { get; }                 // a saved trip waiting for Resume or End trip (the Resume card)
    public bool IsActive { get; }                        // started or resumed
    public TripStep? Step { get; }                       // now; null unless IsActive
    public static int? BadgeMinutes(TripStep step, DateTimeOffset now);   // null: the app icon
    public void Start(LiveTrip trip, CommuteLeg leg, TripDestination destination, TripTime time, PredictionSnapshot? snapshot);
    public void End();                                   // also discards a pending trip
    public Task MissedAsync();
    public Task ResumeAsync();
    public Task WakeAsync();                             // the App calls it on PowerManager resume
    public Task RestoreAsync(bool offerResume);          // at launch; true for a startup-task launch
    public void ApplyKeepAwakeSetting();
    public Task FlushAsync();                            // before exit
}
```

- Go passes the trip view's selected `LiveTrip`, its `CommuteLeg`, `SuggestionContext.Target(leg.To)`, the chosen `TripTime`, and the monitor's `PredictionSnapshot` (fresh departures stand in for the first check).
- `ActiveTripStore` writes `LocalState\active-trip.json` through `ActiveTripJsonContext` (source-generated; `CommuteLeg.IsAlreadyThere` is now `[JsonIgnore]`). It saves on Go, on I missed it (before any re-plan), and whenever anything saved changes (not when only the clock moved), and deletes the file at the end. At launch, a file that can't be read, isn't whole (`ActiveTrip.IsValid`: legs and choices in range, every time in order and within 30 days of `NotBefore`, counters in range), or can't be evaluated is deleted, as is a leftover `active-trip.json.tmp`; `RestoreAsync` never faults. Nothing about its contents is logged.
- `TripMessages` builds the tooltip line and the notification texts (`Strings/en-us/Resources.resw`, `Trip*`, `Notification*`, `Tray*`). Step 03's status card can reuse its wording. While a step `AwaitsReplan`, the tooltip reads "Trip to Work: looking for another way" and the badge shows the app icon.
- Checks run under `TransitConsumer.ActiveTrip` (urgent). Shared departures under a minute old stand in for a check; that window doubles with `TransitBudget.TrackingSlowdown`, and no check is made once the month is used up (`Exhausted`). An attempt, even a failed one, counts as the decision's check, so it isn't retried at once.
- Connectivity (step 02 review): `ConnectivityService.IsInternetAvailable` is only a hint (hotspots and VPNs report constrained or local access), so it never stops a check or a re-plan. A check or re-plan that doesn't reach the service (no HTTP status, or a timeout) is tried once more on the next `NetworkStatusChanged`; a re-plan retried that way may run within the 15 minutes. A disconnect event can therefore cost one failed (counted) attempt.
- Re-planning: at most once per `ReplanGuard.Interval` in the service as well as in `MetadataTransitService`. A new plan raises Connection changed only when the rider had been told a time; when no trip can be made, "No way to Work right now" is sent once per trip (until a plan works again). When I missed it needs a new plan that the 15 minutes hold back, "Looking for another way to Work" says when it will plan again, once. A re-plan that returns after End trip or a new Go is dropped silently, and the per-trip flags reset on Go, End trip, a restore, and Resume.
- I missed it is offered on alerts sent while the rider is heading to a ride (step To the stop or Transfer: Leave now, Ride changed, Connection changed), not on Head out or Get off soon; the tray menu offers it during To the stop, Transfer, and Ride, but not while the step awaits a plan. Choosing it also sends Connection changed with the new time (and raises `Alerted`).
- The countdown rounds up (`TripStep.MinutesUntil`), so the badge turns red exactly when it reads the lead (or 2). Keep-awake applies while the countdown shows (within 99 minutes) or the trip awaits a plan, so a trip set for tomorrow doesn't keep the PC up overnight. Tracking shares the PC's state: when tracking stops during a trip, the trip sets it again; when a trip ends during tracking, tracking's is restored. `App.ApplyKeepAwakeSetting` applies the setting to tracking, then the trip.
- The timer never waits more than 15 minutes, so a long wait re-reads the clock even if the suspend event never came. An unexpected exception in a wake keeps the trip as it was and wakes again a minute later (only its type is logged).
- A pending trip wakes only to see whether it's over, and is forgotten when it would be (arrival + 5 min, as at launch), not at the arrival itself.
- Notification arguments parse case-insensitively, so a toast from before step 02 ("stopTracking") still stops tracking.
- `PowerManager.SystemSuspendStatusChanged` exists in the Windows App SDK (documented for 1.0 through 2.0; the project uses 2.5.1). `App` subscribes to it and calls `WakeAsync` on AutoResume or ManualResume; the unit tests call `WakeAsync` directly.

### 3. One tracking slot (app)

- `App` owns both services and keeps one of them active. `ActiveTripService.Start` stops `BusTrackingService`, and a bell during a trip asks first (D1).
- `UpdateTrayIcon`, `UpdateTooltip`, and the context menu read whichever one is active.
- `NotificationService` gains the trip's buttons (`missedIt`, `endTrip`) and keeps one tag.
- The *Head out* setting's description changes to cover trips ("before you need to leave").

As built in step 02: `ActiveTripService` takes the `BusTrackingService` and stops it on Go. A bell during a trip didn't ask yet; step 03 added the D1 question (see §4's as-built notes). `NotificationService.Show(title, text, buttons)` takes `NotificationAction`s (`StopTracking`, `MissedIt`, `EndTrip`, `ResumeTrip`), and its one `ActionRequested` event replaces `StopTrackingRequested`. Exit keeps the trip saved (it waits to be resumed at the next launch) and stops tracking as before.

### 4. Flyout (app)

- `ShowPopup(bool activate = true)`: with an active trip, it opens the progress view instead of resetting to the board. With `activate: false`, it shows via `AppWindow.Show(false)` and starts a 30 s hide timer; activation switches it to the normal light-dismiss.
- `Controls/TripProgressView.xaml`: the header with **End trip**, the status card with **I missed it**, and the steps list. The leg templates move from `TripView.xaml` to a shared resource dictionary so both views use them. `TripWalkItem` and `TripRideItem` gain a state: done, current, or next.
- **Steps zoom the map.** Each step is clickable and frames its leg: a walk from point to point, or a ride from boarding to getting off, with a little margin (`TripStepZoom` picks the view). Clicking the ride the rider is on alternates between the bus about where it is now (its reported position, else where the ride's times put it along the route) and the whole ride. A step's connector line is solid once the rider has passed it and dashed while it is still ahead.
- **The tray badge shows the step.** The icon (a walking figure or a bus, for the current leg) and the countdown take turns every 5 seconds, starting with the icon whenever the step changes.
- `TripView`'s header becomes Back, the heading, the time dropdown, and **Go**, and its separate time row (`TimeAnchor`) goes away. The time editor flyout anchors to the dropdown instead. `SuggestionCard` gets an active-trip state.
- The flyout still closes a minute after it's hidden. The trip and its camera live in the service, so the next open rebuilds the view from them. Camera state is memory-only for that trip, not restored after the application exits.
- New strings go in `Strings/en-us/Resources.resw`. Every new control has a name and an `AutomationId` (`GoButton`, `TripProgress`, `TripStatus`, `MissedItButton`, `EndTripButton`).
- Network work while hidden stays limited to the tray's own state, as for tracking: the trip is opt-in, its reason is written here, and its checks are as few as the table below allows.

As built in step 03:

- **Trip view header.** `Controls/TripHeaderPanel` lays out Back, the heading (with "arrive 6:00 PM" under it), and the actions (time dropdown and **Go**) on one row; the heading takes what's left, wraps to two lines, and trims. When that leaves the heading under 64 epx (a long time label at large text sizes), the actions move to a second row, under the heading, rather than clipping. The progress view uses the same panel with **End trip** as its action. The dropdown's short label is "Leave now", "Leave 8:00 AM", or "Arrive by Tue 9:00 AM" (weekday within a week, else "Oct 28"); its tooltip and name give the full date ("Leave at Tuesday, October 20, 2026 8:00 AM"). The time editor flyout opens from the dropdown. The `TimeAnchor` row is gone.
- **Go** (`GoButton`, accent) is named "Start trip to Work, leaving 5:29 PM". It calls `Trips.Start` with the selected option, `SuggestionContext.Target(leg.To)`, the chosen `TripTime`, and the snapshot of the monitor the option was timed from (the suggestion's for Leave now, the timed trip's otherwise), then shows the progress view with a caption: "Following your trip to Work…", or for a trip over 99 minutes away, "Your trip to Work is scheduled to leave Tue 8:00 AM…". When the origin was guessed, the arrival line ("from Home · arrive 6:00 PM"), Go's name, and the caption all name it.
- **Progress view** (`Controls/TripProgressView`, created on first use like `TripView`): header (Back, "Trip to Work", the arrival, **End trip**), a status card, and the steps. The card's text comes from `TripMessages.Status` (see the table in §The steps), with a spoken form that spells the units out for Narrator ("Leave in 6 minutes. Walk 4 minutes to Oak & 5th for the 14"), set as the status text's name (`TripStatus`, `LiveSetting="Polite"`). A `LiveRegionChanged` event is raised when the step changes (kind, ride, or waiting for a plan) and after I missed it or Resume, not on each minute of the countdown. **I missed it** (`MissedItButton`, compact `SubtleButtonStyle`) shows when `TripStep.CanMiss` (To the stop, Transfer, or Ride, not waiting for a plan; the tray menu and `TripProgress.Missed` use the same property). A scheduled trip reads "Leave Tue 8:00 AM · Scheduled…"; a trip waiting for a plan, "Looking for another way…".
- **Resume card (D6)** is the progress view without a step: "Resume your trip to Work?" with **Resume** (accent) and **End trip** in the card; the header's End trip and the steps are hidden. A manual launch now waits for `RestoreAsync` before opening the flyout, so it opens on the card.
- **Steps.** The leg templates moved to `Controls/TripLegTemplates.xaml` (an `x:Class` resource dictionary, for `x:Bind`), merged by both views. `TripLegItem` has a `TripLegState?` (null in the trip view): a check and 60% opacity when done, a filled accent dot when current, a ring when upcoming. `TripStep.StateOf(leg)` gives the state from the step's leg index (Core). Each item's `ToString()` is its accessible name ("Now: Walk 2 min · 250 ft. To Humboldt / Burleigh…"); before, the trip view's legs were read as their class name.
- **Opening during a trip.** `ShowPopup` now ends in `StartShowing()`, what every open shows however the window appears (step 04 adds only the `activate: false` appearance). It opens or retains progress (or the Resume card) first, then resets and loads the board beneath it without clearing the trip's map layers or changing its camera. Back, Escape, Backspace, and Alt+Left go to the board, where the card is the trip ("Trip to Work · get off in 9 min", read with its status); selecting it returns. During a trip the card has no Going to, and Save as place is hidden: one trip at a time, so the rider ends it to plan another.
- **Map.** `TripMapRenderer.Emphasize(leg)` draws the current leg 3 px thicker and the others at 110 alpha, without reframing. Go carries the planning camera into progress; closing progress or hiding saves center, zoom, heading, and pitch to `ActiveTripService.MapView`. Reopening restores that view, including after window teardown; a new trip or trip end clears it. A first fit waits for a nonzero viewport rather than choosing a near-world zoom. Fit in the map context menu (Ctrl+0) still frames the whole trip. `FollowRide` uses `TripStep.CanFollowVehicle` to show the upcoming or current ride's vehicle (Get ready within the live window, To the stop, Transfer, or Ride, with a trip ID), through `onlyThisTrip`, without moving the map, at Normal budget only. The existing foreground vehicle monitor reuses fresh positions and refreshes on its existing timer; no hidden vehicle polling is added.
- **Home header.** The places selector is beside Settings, with its old row and the refresh/fit toolbar buttons removed. Both actions remain in the map context menu; F5 refreshes and Ctrl+0 fits. The selector trims long labels to leave space for the status and Settings.
- **D1.** A bell, or a lead chosen from its right-click menu, during a trip (or while one waits to be resumed) asks "End your trip to Work and track this bus instead?" (`Controls/ConfirmFlyout`: **End trip and track**, **Keep trip**).
- **Lifetime.** The flyout subscribes to `Trips.Changed` in `StartShowing` and unsubscribes in `HidePopup` and `CloseWindow`; it renders on each change (start, end, resume, each minute).
- **Wording.** The notification's "Then walk 1 min to the 30 at 8:30" texts are shared with the status card (`TripMessages.AfterRide`), so they lost their trailing period. `TripFutureScheduled` no longer says tracking is unavailable for a future trip.

## Call budget

Active-trip checks are urgent: they wait for a slot rather than fail, and continue at Low and Reserve, like tracking. They slow down by `TransitBudget.TrackingSlowdown` when the budget is tight, and stop at Exhausted, where the trip runs on its last times.

**As few checks as possible (D7).** A check is worth a call only when a fresh answer could change what the rider does next. So there's no steady cadence: one check shortly before each decision, timed so its alert fires on fresh times. Between checks, alerts fire by the clock from the last known times.

| Decision | Check | What it can change |
|---|---|---|
| Head out | 1 min before the alert | Whether, and when, to head out |
| Leave now | 2 min before leave-by | The leave time, a canceled first ride |
| Boarding | 3 min before the departure, only if the walk to the stop is over 5 min | A ride moved or canceled while walking |
| Getting off (each ride) | 5 min before getting off, only if the ride is over 8 min | The get-off time; for a transfer, whether the next ride can still be made |
| Transfer departure | 3 min before it, only if the wait is over 5 min | The next ride moved or canceled |

- A check due within 2 minutes of the previous one is merged into it, and a check is skipped when the shared departures for every watched stop are under a minute old (the flyout was open, or Go was just pressed).
- Offline: each decision's check is tried once (and fails); one more when the network changes, if a decision is still ahead. As built in step 02, Windows' connection status is a hint and doesn't stop checks.
- A scheduled trip makes no checks until its first one.

Target: **about 6 calls**, and never over 15, for a 45-minute trip with one transfer, started 10 minutes before leaving: 2 before leaving, 1 on the walk, 1 before the first stop to get off (which also covers the transfer), 0–1 at the transfer, and 1 on the second ride. Step 05 measures it with `RequestBudgetTests`. For comparison, tracking one bus from 15 minutes out costs about 12. If field use shows changes being heard too late, add checks one decision at a time, not a cadence.

Every watched stop shares the one `stop_departures` call per check. With the flyout open, the board's stops ride along in it.

## Decisions (confirmed 2026-10-19)

| # | Question | Chosen | Alternative not taken |
|---|----------|--------|-----------------------|
| D1 | A trip and a tracked bus | **One tracking slot.** Go replaces a tracked bus; a bell during a trip asks before ending it | Run both (the tray icon can show only one) |
| D2 | How the flyout pops up | **On top without focus**, hiding after 30 s unless clicked; not when Windows says busy or full-screen; a setting turns it off | Take focus (Windows often refuses, and keystrokes can land in the flyout) · Notifications only |
| D3 | Which steps pop it up | **Leave now, Get off soon, Connection changed.** The rest are notifications only | Every step |
| D4 | Knowing the rider got on | **Assume the rider is on it** once its time passes, unless they choose **I missed it**, which re-times or re-plans | Ask "Did you get on?" after every departure · Check that the bus left |
| D5 | Go on a future trip | **Schedule it:** silent and free until its first check | Go only for Leave now |
| D6 | Restarts | **Ask before resuming** a saved trip that could still be in progress (changed from resuming on its own). Sleep continues by itself | Resume silently · Memory only |
| D7 | How closely to follow | **As few checks as possible:** one before each decision, about 6 for a typical trip, never over 15 | A steady cadence (about 15–25) |

## Steps

### Operating rules

- Do one step per session, and resume an `in_progress` step before starting another. Update the table and the execution log when starting, when blocked, and when done.
- Before writing code, read this file, `AGENTS.md`, and the instruction files each step names. Every step needs design principles, code quality, and testing.
- Mark a step `done` only after its acceptance criteria, tests, and build pass. Record the evidence, not just "verified".
- Keep logs in English, and never record keys, URLs, stops, addresses, or coordinates.
- Update the README only for behavior that has shipped.

Allowed statuses: `pending`, `in_progress`, `blocked`, `done`, `deferred`.

| Step | Deliverable | Size | Status | Evidence / handoff |
|------|-------------|------|--------|--------------------|
| 01 | `TripProgress`: steps, re-timing, alerts, I missed it, serialization | M | done | 2026-10-19. 42 new tests; full suite 601/601 (was 559); ARM64 app build 0 warnings/errors. Measured checks: 6 for the target trip, 4 for a trip with a short walk and wait. See the execution log |
| 02 | `ActiveTripService`: checks, re-plan, persistence, sleep and network, one tracking slot, tray, notifications | L | done | 2026-10-19. 33 new tests; full suite 634/634 (was 601); ARM64 app build 0 warnings/errors. Live probe (1 call): the stop to get off at listed 3 of 6 boarding-stop trip IDs, 16 min later (the planned ride time). Review fixes the same day: 4 defects and 5 design points; full suite 663/663, none skipped. Not run: the app, by hand. See the execution log |
| 03 | Flyout: Go, progress view, open to the trip, active card, map | L | done | 2026-10-19. 44 new tests; full suite 707/707 (was 663); ARM64 app build 0 warnings/errors. Packaged app, by hand: header row, Go → progress, Back → active card → progress, Escape twice hides, reopen shows progress, bell asks (D1), End trip; no request from any of them (one trip check, by design). Not run by hand: I missed it, the Resume card, the long label and searched name, 150% text, Narrator, High Contrast (the developer's packaged checks below). See the execution log |
| 04 | Pop-up without focus, busy check, setting | M | pending | |
| 05 | Budget measurements, docs, acceptance | S | pending | |

### 01 — Trip progress (Core)

**Start here:** `LiveTrip.cs`, `TrackedBus.cs`, `TripSelection.cs`, `TripTime.cs`, `TransitModels.cs` (itinerary JSON), `LiveTripTests`, `TrackedBusTests`.

**Scope:** `TripProgress` and its step and alert types, as in §1. Extract the shared boarding and matching helpers from `LiveTrip` and `TrackedBus` without changing their behavior.

**Acceptance (tests):**
- Steps and action times for a walk → ride → transfer walk → ride → walk trip at a fixed clock.
- Head out fires at the lead, Leave now at leave-by, and Get off soon 2 min before getting off, each once.
- The committed ride moving ≥ 3 min after Head out alerts once; a smaller move doesn't.
- A late first ride moves the transfer to the next departure, with one Connection changed alert and the new arrival.
- A canceled committed ride moves to the next departure; with none left, the result is Replan.
- A ride counts as taken once its last known departure passes, with no check; an older snapshot than the last one applied is ignored.
- I missed it re-times from the next departure at that stop without new data; with none left, the result is Replan.
- Several alerts due at once (after sleep) give only the latest.
- `NextCheck` follows the check table: one check per decision, the conditional ones skipped for short walks, rides, and waits, near ones merged, and none for a scheduled trip before its first.
- The target trip (45 min, one transfer, Go 10 min before leaving) needs about 6 checks, counted from `NextCheck` alone.
- A trip round-trips through JSON.
- `LiveTripTests` and `TrackedBusTests` pass unchanged.

### 02 — Active trip service (app)

**Start here:** `BusTrackingService.cs` and its tests, `App.xaml.cs`, `NotificationService.cs`, `EtaTrayIcon.cs`, `ConnectivityService.cs`, `MetadataTransitService.ReplanTripAsync`, `TransitRequestDiagnostics.CurrentPriority`, and `ServicePlatformDoubles.cs`. Read security (the saved trip) and performance.

**Scope:** §2 and §3. No flyout UI beyond a temporary way to start a trip for testing.

**Acceptance:**
- Tests: checks follow `NextCheck`, one call per check, as consumer `ActiveTrip` at urgent priority. A check is skipped when fresh shared departures cover it. Offline skips checks, and reconnecting makes one. Waking from sleep re-evaluates at once. A re-plan happens at most once per 15 min. Starting a trip stops a tracked bus.
- Tests: a saved trip that isn't over waits after a restart, with no checks, until Resume (which checks once) or End trip (which deletes it). One that's already over is deleted at launch.
- One live `stop_departures` call (with the developer's key) shows whether a downstream stop lists an en-route trip by its trip ID. Record the answer here, without identifiers.
- The tray badge, tooltip, and menu show the trip. Notifications carry the buttons and replace each other.

### 03 — Flyout

**Start here:** `TimesFlyoutWindow.xaml(.cs)` (`ShowPopup`, `SuggestionCard_TripInvoked`, `RenderTrip`, `CloseTrip`, `StartTracking`), `TripView.xaml(.cs)`, `TripViewItems.cs`, `SuggestionCard`, and `TripMapRenderer`. Read accessibility, globalization, performance, and WinUI best practices.

**Scope:** §4, except the pop-up.

**Acceptance:**
- The trip view's header matches the sketch: Back, the heading, the time dropdown, and Go on one row. At 400 px it fits a long searched destination and "Arrive by Tue 9:00 AM" without overlap, and at 150% text scaling it wraps the heading rather than clipping the buttons.
- Go starts the selected option at the chosen time. A future trip shows as scheduled.
- Every open during a trip shows its progress. Back shows the board with the active card, and the card returns to the trip.
- Opening and navigating make no request beyond what the board already makes.
- After a restart with a saved trip, the manual launch's flyout shows the Resume card; a Windows startup launch sends the Resume notification.
- I missed it and End trip work from the view, the notification, and the tray menu.
- Narrator reads the status ("Leave in 6 minutes. Walk 4 minutes to Oak and 5th for the 14") and announces step changes. The keyboard reaches Go, I missed it, and End trip. Text is readable in High Contrast.
- The developer runs the packaged checks.

### 04 — Pop-up

**Start here:** the flyout's activation and light-dismiss (`OnWindowActivated`, `HideIfFocusLeftWindow`), `WindowPlacementService`, `NativeMethods.txt`, and Settings' Ride alerts. Read accessibility and WinUI best practices.

**Scope:** §4's `activate: false` path, the busy check (`SHQueryUserNotificationState`), and the setting.

**Acceptance:**
- The three D3 alerts show the flyout without taking focus from a text editor. It hides after 30 s; clicking it keeps it open with light-dismiss.
- Nothing pops up in full-screen or presentation mode, or with the setting off; the notification still shows. Record whether Do Not Disturb is detected.
- An alert more than 5 minutes stale doesn't pop up.

### 05 — Budget, docs, acceptance

**Scope:** `RequestBudgetTests` cases for the target trip, a scheduled trip, an offline stretch, and a re-plan. Correct the call budget section with the measured numbers. Update the README (active trip, tracking, API budget) and the board plan's follow-up.

**Developer acceptance checklist:**
1. Plan to Work, choose an option, and press Go. The tray counts down to leaving, and the tooltip says what's next.
2. Head out and Leave now notifications arrive. Leave now pops up the flyout without taking focus.
3. Let the flyout close, then reopen it: it shows the trip's progress, not the board. Back, then the card, returns to it.
4. Close the lid on the walk and open it on the bus: the step has moved on, with one current notification and no burst.
5. Turn off Wi-Fi mid-ride: times keep counting down. Turn it back on: one check updates them.
6. Get off soon pops up about 2 minutes before the stop.
7. I missed it at the stop re-times the trip to the next bus.
8. Leave at 8:00 tomorrow and press Go: no calls, and the app icon until it's within 99 minutes.
9. Restart the app mid-trip: it asks to resume. Resume continues the trip; End trip forgets it.
10. After a trip, Settings' usage card shows about 6 calls for it.

## Risks and open questions

- **Downstream stop times.** If the alighting stop doesn't list an en-route trip, "Get off" uses the planned ride time from the actual departure, which is as good as the schedule. Step 02's one live call (a metro line, scheduled data only) found the same trip IDs downstream, but only for trips within that stop's 6 departures per direction, so a long ride, or a frequent line, may still fall back. Whether real-time trips keep their IDs downstream is unchecked.
- **Assuming the rider boarded.** If they missed it and don't say so, the steps run ahead of them. The notification's I missed it is one click away; field use will show whether that's enough.
- **Few checks.** A change after a decision's check is heard at the next check, or not at all; a bus that slips 3 minutes after the Leave now check means a longer wait at the stop. That's the cost of D7. Add checks per decision if it matters in practice.
- **Windows location** can't confirm progress on most laptops, so the plan doesn't rely on it.
- **Do Not Disturb** sends notifications to Action Center silently, so a rider in a focus session could miss Leave now.
- **Flyout height.** Check a three-ride trip plus the status card at the default map height, with the splitter.

## Follow-ups (not in this plan)

- Use Windows location, when it's available, to advance or end steps (at the destination early → end; still at the origin after leave-by → "Still here?").
- A setting for the Get off soon lead.
- Windows' urgent notification scenario, which can break through Do Not Disturb with the rider's permission.
- Rebuild bell tracking as a one-ride trip, so one engine follows both.

## Verification

From the repository root, in each fresh shell:

```powershell
$arch = $env:PROCESSOR_ARCHITECTURE
$Platform = if ($arch -eq 'AMD64') { 'x64' } else { $arch }

dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "<the step's test classes>"
dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
```

Run the full suite for steps 02 and 05, which touch shared request paths. Name tests `MethodName_Scenario_ExpectedResult`, and use the existing fake HTTP handler and clock (`RequestTestSupport.cs`), never live calls (step 02's one recorded probe aside).

Each session appends an execution-log entry with the date, step, and status; the changes and decisions; files and new tests; the exact commands and their results (including anything not run); and the next action.

## Execution log

### 2026-10-19 — Plan and decisions

- Plan written from the developer's request: Go starts a trip; notifications and a pop-up flyout at each step; opening the flyout during a trip shows its progress; the tray icon counts down as tracking does.
- The developer's sketch placed the time dropdown and **Go** in the trip view's header row; §Starting a trip and §4 follow it.
- D1–D5 confirmed as recommended. D4 clarified: assume the rider is on the bus unless they choose I missed it. D6 changed: ask before resuming after a restart. D7 changed: as few checks as possible ("15 calls is fine, but if you can do even less to start that would be awesome"), so the steady cadence became one check per decision.

### 2026-10-19 — Step 01: trip progress (Core) — done

**Changes**

- `TrayTimes.Core/Commute/TripProgress.cs`: `TripProgress`, `RideChoice`, `TripStep`, `TripUpdate`, `TripStepKind`, `TripAlert`, and `TripProgressJsonContext`, as built in §1.
- `TrayTimes.Core/Tracking/TripMatch.cs`: the trip-matching rule from `TrackedBus.FindIn`, shared. `TrackedBus.FindIn` calls it; `MatchTolerance` moved to `TripMatch.Tolerance` (its one test reference updated).
- `LiveTrip`: `Board` and `RideTime` are `internal`; `Board` takes trips to skip; `TripKey` names a departure by trip ID, else time, and `FirstRideDepartures` uses it. Behavior unchanged (`LiveTripTests` pass as they were).

**Decisions**

- The state is the inputs (choices, departures, what was told), and `Trip` is derived, so a saved trip can't hold stale leg times.
- A walk before the first ride ends as the ride leaves until leave-by passes; from then (`LeftAt`), the rider has left and waits at the stop if the bus is late, rather than being told to leave again.
- Get off soon is due at the later of boarding and 2 minutes before getting off, so a very short ride never alerts before the rider boards.
- Check timing as built: head out − 1 min; leave-by − 2 min; boarding − 3 min after a walk over 5 min; getting off − 5 min on a ride over 8 min; a transfer's departure − 3 min after a wait over 5 min. A check within 2 minutes after the last data or attempt is skipped, and an overdue one is due now.

**Tests** (`TrayTimes.Tests/Commute/TripProgressTests.cs`, `TrayTimes.Tests/Tracking/TripMatchTests.cs`; 42 cases)

- Steps by the clock (10 rows); each clock alert once; Go within the lead skips Head out.
- Ride changed at 3 minutes, not 2, and only after Head out; a late first ride, a later time at the stop to get off, and a cancellation each give one Connection changed with the new times; no connection left needs a re-plan; a leave-at trip never boards early; a passed departure counts as taken without a check; an older snapshot is ignored; alerts due while asleep give only the latest.
- I missed it re-times from the next departure, needs a re-plan when none is left, and does nothing before leaving.
- `NextCheck`: the target trip (45 min, one transfer, Go 10 min before leaving) checks at 8:04, 8:08, 8:13, 8:26, 8:37, and 8:45 (**6 checks**); the shorter fixture checks 4 times; fresh data and failed attempts aren't repeated within 2 minutes; an overdue check is due now; a scheduled trip waits for its first decision; a walk-only trip has none.
- `NextAlertAt`, `WatchedStops`, `IsOver`, the snapshot filter, and a JSON round trip.
- `TripMatch.Find`: by trip ID, nearest real-time departure without one, and other stops or directions ignored.

**Commands** (ARM64, from the repository root)

- `dotnet build .\TrayTimes.Core\TrayTimes.Core.csproj -c Debug -p:Platform=ARM64`: 0 warnings, 0 errors.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --filter "FullyQualifiedName~TripProgressTests|FullyQualifiedName~TripMatchTests|FullyQualifiedName~LiveTripTests|FullyQualifiedName~TrackedBusTests"`: 122/122.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --no-build`: 601/601.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64`: 0 warnings, 0 errors.
- Not run: the app (no UI changed in this step). No API calls were made.

**Next:** step 02, `ActiveTripService`. It needs a way to continue a trip on a new plan (from `NeedsReplan`), and the one live `stop_departures` probe to see whether a stop to get off at lists an en-route trip.

### 2026-10-19 — Step 02: active trip service (app) — done

**Changes**

- `TrayTimes/Services/ActiveTripService.cs` (new): follows the trip on the UI thread with one `DispatcherQueueTimer`, waking at the earliest of the next check, the next alert, the next minute of the countdown, an allowed re-plan, and the trip's end. Checks under `TransitConsumer.ActiveTrip`; offline, it skips them and checks once when `NetworkStatusChanged` says it's back; re-plans with `MetadataTransitService.ReplanTripAsync`; sends alerts through `NotificationService` and raises `Alerted`; saves through `ActiveTripStore`; restores a saved trip as pending at launch. API in §2's as-built notes.
- `TrayTimes/Services/ActiveTripStore.cs` (new): the saved trip's file, written in order off the UI thread, replaced only once fully written, and deleted when damaged.
- `TrayTimes/Services/TripMessages.cs` (new): the tooltip line and notification texts. `TrayTimes/Services/NotificationAction.cs` (new): the notification buttons.
- `TrayTimes.Core/Commute/TripProgress.cs`: `ReplanFrom` and `Continue` (continue on a new plan, keeping the rides taken and what the rider was told); `TripStep.MinutesUntil`. `TrayTimes.Core/Commute/ActiveTrip.cs` (new): the saved record, `IsValid`, and `ActiveTripJsonContext`. `CommuteLeg.IsAlreadyThere` is `[JsonIgnore]`.
- `TransitConsumer.ActiveTrip`, mapped to `TransitPriority.Urgent` in `CurrentPriority`. No other switch on `TransitConsumer` needed it (browsing consumers still reject it).
- `NotificationService`: `Show(title, text, buttons)` with `NotificationAction`s and one `ActionRequested` event (replacing `StopTrackingRequested`); button labels come from Resources.resw. `BusTrackingService` passes `[StopTracking]` as before.
- `App.xaml.cs`: owns `Trips`; tray badge, tooltip line, and menu (Show trip, I missed it, End trip) for the trip; notification buttons; `PowerManager.SystemSuspendStatusChanged` calls `WakeAsync`; `RestoreAsync` at launch (a startup-task launch asks in a notification); exit flushes the saved trip. `SettingsWindow` applies keep-awake through `App.ApplyKeepAwakeSetting`.
- Resources.resw: the *Head out* setting now reads "Notify me this many minutes before I need to leave on a trip, or before a tracked ride leaves." (and its help text); new `Trip*`, `Notification*`, and `Tray*` strings.

**Decisions**

- The countdown rounds up, so red and the number agree; keep-awake only while the countdown shows; saves on real changes, not every minute; re-plans at most every 15 minutes in the service too, and says "no way" once. See §2's as-built notes.
- Pending resume: no checks, no keep-awake, the app icon; forgotten when it would be over (arrival + 5 min). Resume re-times by the clock and checks once if a decision is still ahead.
- `PowerManager.SystemSuspendStatusChanged` was checked in the Windows App SDK API reference (the event and `SystemSuspendStatus` exist from 1.0 through the 2.0 docs) before use; no decompiling. It stays in `App`, untested; the tests drive `WakeAsync`, and a late timer catches up by itself.
- No temporary UI to start a trip was added; step 03's Go is the first caller.

**Live probe** (the one acceptance call)

- A throwaway file-based C# app in the session scratchpad parsed the recorded Montreal plan fixture with the app's own client (no network), took its first ride with a stop to get off at (a 10-stop metro ride, planned 16 min), read the key from `OAuth.resw` without printing it, and made **one** `stop_departures` call for that ride's two stops, as `TransitAppService` sends it.
- Result: 1 call, 30 departures. The boarding stop listed 6 departures on the line, the stop to get off at 6; all scheduled, none real-time. **Partial yes:** the stop to get off at listed 3 of the 6 boarding-stop trip IDs, each exactly 16.0 min later, the planned ride time. The other 3 were past its 6-departure window (it lists trips already on the way). So `TripProgress`'s alighting lookup by trip ID works when the trip is within that window; otherwise it falls back to the departure plus the planned ride time, as built. Real-time IDs weren't available on this line.

**Tests** (33 new)

- `TrayTimes.Tests/Services/ActiveTripServiceTests.cs` (18): checks at 7:54, 7:58, 8:15, and 8:30 for the transfer trip, one call each, as `ActiveTrip` at urgent priority; each alert once with its buttons and due time; fresh shared departures skip a check; offline skips checks and reconnecting checks once (twice told, once checked); waking after sleep gives only the latest alert and one check; I missed it re-times with no call; a lost connection re-plans once and continues on the new trip; a stranded trip re-plans at 8:06, 8:21, and 8:36 only and says "no way" once; Go stops a tracked bus; keep-awake on Go and off at End; a trip for tomorrow keeps the app icon, makes no calls, and lets the PC sleep until 99 minutes before; no checks once the month is used up; a restart waits with no checks until Resume (one check); a startup launch asks with Resume and End trip; End deletes a pending trip; a pending trip is forgotten when it would be over; an over trip and a damaged file are deleted at launch.
- `TrayTimes.Tests/Commute/TripProgressTests.cs` (+10): `Continue` at a transfer, before leaving, after missing the last ride, and with no trip left; `MinutesUntil` (6 rows).
- `TrayTimes.Tests/Commute/ActiveTripTests.cs` (4): JSON round trip with the leg, places, and an unsaved destination; `IsValid` for a bad line, missing choices, and a missing leg.
- `TransitRequestDiagnosticsTests`: `ActiveTrip` is urgent (1 row).

**Commands** (ARM64, from the repository root)

- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64`: 0 warnings, 0 errors.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --filter "FullyQualifiedName~ActiveTripServiceTests|FullyQualifiedName~ActiveTripTests|FullyQualifiedName~TripProgressTests|FullyQualifiedName~BusTrackingServiceTests|FullyQualifiedName~TransitRequestDiagnosticsTests"`: 106/106.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64`: 634/634.
- `dotnet build .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --no-incremental`: 0 warnings, 0 errors.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Release -p:Platform=ARM64` (trim analysis): 0 errors; 4 warnings, all already there (`WindowPlacementService.cs` IL2075 and SDK assemblies' IL2104), none from this step.
- `dotnet run probe.cs` (scratchpad): the one live call above.
- Not run: the app (`dotnet run`), so the tray badge, tooltip, menu, notifications, and resume after a real sleep or restart are untested by hand; there's no UI to start a trip until step 03.

**Notes for later steps**

- Step 03: call `Trips.Start` from Go; show the Resume card while `IsPendingResume` (manual launch); ask before a bell ends a trip (D1), which isn't enforced yet.
- Step 04: after sleep or Resume, the latest due alert is still sent as a notification even when it's stale (e.g. Leave now, 11 minutes late, while the rider is assumed to be on the bus). The pop-up's 5-minute rule may want a notification counterpart.

**Next:** step 03, the flyout.

### 2026-10-19 — Step 02: review fixes — done

An independent review added 12 tests to `ActiveTripServiceTests` (8 passing, 4 ignored for defects). All 4 now pass un-ignored, and the 5 design points are in.

**Defects fixed**

1. A stranded trip still sent Get off soon for the missed ride. `TripProgress` now keeps `StrandedRide` (`NeedsReplan` is derived from it): from that ride on there are no alerts or checks, and the step waits at its stop with `AwaitsReplan`. Before leaving, a stranded trip sends no Head out or Leave now. While fixing it, a step-01 rule turned out wrong: a later ride whose departure passed counted as taken even when an earlier ride ran too late to reach it (which also un-stranded a stranded trip). Now a ride counts as taken only after the rides before it, and only if the rider could have been at its stop.
2. A re-plan that came back after End trip (or a new Go) still sent "No way…" and its flag leaked into the next trip. `ReplanAsync` now checks the generation after its await, and every per-trip flag resets on Go, End trip, a restore, and Resume.
3. A failed check wasn't retried when the network returned. A check or re-plan that doesn't reach the service now asks for one more try on the next `NetworkStatusChanged`, and that flag clears only when a request gets through.
4. A parseable but damaged file (a ride with no departure time) passed `IsValid`, then made `RestoreAsync` throw, and was never deleted. `IsValid` now checks every time (in order, within 30 days of `NotBefore`) and every counter; reading catches any exception; and restore deletes the file on any failure to evaluate it.

**Design fixes**

5. `IsInternetAvailable` no longer stops checks or re-plans (it's a hint, and hotspots, this feature's target, often report constrained access). An unreached request retries on the next network change; a re-plan retried that way may run within the 15 minutes. Known cost: a disconnect event can trigger one failed (counted) attempt. The service has no message that needs the flag.
6. The timer waits at most 15 minutes, for an active trip and for a pending one.
7. An unexpected exception in a wake or re-plan is caught (type logged), and the timer re-arms for a minute later; the trip keeps its state.
8. I missed it saves the trip at once, before any re-plan, and Apply saves whenever anything saved changed (not only on a step change). When I missed it needs a new plan the 15-minute guard holds back, "Looking for another way to {place}" says when it will plan again, once.
9. Notification actions parse case-insensitively; a leftover `active-trip.json.tmp` is deleted at launch (and with the trip); `App.ApplyKeepAwakeSetting` applies to tracking, then the trip (which leaves tracking's state alone without an active trip); when tracking stops during a trip, the trip sets keep-awake again; when a trip ends during tracking, tracking's state is restored.

**Tests**

- Un-ignored: `MissedAsync_NoLaterDepartureOrPlan_SendsNoGetOffSoonForTheMissedRide`, `End_WhileAReplanIsInFlight_SendsNoNoConnectionNotice`, `OnNetworkStatusChanged_CheckFailedThenNetworkReturns_ChecksOnce`, `RestoreAsync_SavedRidesWithoutTheirDepartureTime_DeletesTheFile` (unchanged).
- Changed because fix 5 removed their premise (the flag used to mean "skip"): `MissedAsync_NoLaterDepartureWhileOffline_WaitsForTheNetworkWithoutSpinning` (offline is now failing requests; it asserts a try at 8:06, 8:21, and 8:36 only), `OnNetworkStatusChanged_StrandedTripBackOnline_PlansAgainOnce` (offline via failing requests; a minute passes on the clock instead of a tick, which would now reach the 8:21 try), `ResumeAsync_Offline_ChecksOnceWhenBackOnline` (Resume's check is tried and fails, then one more), and step 02's `Refresh_Offline_…`, now `OnNetworkStatusChanged_ChecksFailedWithoutAConnection_ChecksOnceWhenItComesBack`. The fixture's `GoOffline`/`GoOnline` simulate the connection.
- Changed for fix 6: `RestoreAsync_SavedTripNotOver_…` (the pending wait is 15 minutes), `RestoreAsync_ArrivalPassesUnanswered_…` (runs to the end instead of one tick), `Start_TripTomorrow_…` (wakes every 15 minutes until the countdown shows).
- New (17): `Refresh_WindowsReportsNoInternet_StillChecks`, `Refresh_UnexpectedFailure_KeepsTheTripAndTriesAgainInAMinute`, `MissedAsync_PlannedAgainMinutesAgo_SaysWhenItWillPlanAgainOnce`, `MissedAsync_NothingLeft_SavesTheMissedRideBeforePlanningAgain`, `Start_WhileTheLastTripsReplanIsInFlight_StillSaysWhenTheNewOneHasNoWay`, `RestoreAsync_UnfinishedSave_DeletesItsTempFile`, `TrackerStop_DuringATrip_KeepsThePCAwakeForTheTrip`, `End_WhileTrackingABus_HandsKeepAwakeBackToTracking`, `ApplyKeepAwakeSetting_TrackingWithoutATrip_LeavesTrackingsStateAlone`; in `TripProgressTests`, `Missed_NoLaterDeparture_WaitsAtTheStopWithoutGetOffSoon`, `Advance_NoConnectionBeforeLeaving_DoesNotSendTheRiderOff`, `Advance_TransferLeftBeforeTheFirstRideGotThere_TakesTheNextDeparture`; in `ActiveTripTests`, `IsValid_RideGettingOffBeforeItBoards_IsFalse`, `IsValid_KnownDepartureAtTheEndOfTime_IsFalse`, `IsValid_CounterPastTheRides_IsFalse` (3 rows).

**Commands** (ARM64, from the repository root)

- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64`: 0 warnings, 0 errors.
- `dotnet build .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --no-incremental`: 0 warnings, 0 errors.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64`: 663/663, none skipped (646 before, with 4 skipped); run 4 times, all green.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Release -p:Platform=ARM64`: 0 errors; the same 4 earlier trim warnings (`WindowPlacementService.cs`, SDK assemblies), none new.
- Not run: the app.

**Next:** step 03, the flyout. It should hide I missed it and show "looking for another way" while `Step.AwaitsReplan`.

### 2026-10-19 — Step 03: flyout — done

**Changes** (as built in §4)

- `TrayTimes/Controls/TripHeaderPanel.cs` (new): the trip views' header layout (Back, heading, actions on one row; the actions move under the heading when it would get under 64 epx).
- `TrayTimes/Controls/TripView.xaml(.cs)`: the header is Back · heading/arrival · time dropdown · **Go**; the `TimeAnchor` row is gone and the time editor opens from the dropdown; `GoRequested`; Go's name and the dropdown's full time as tooltip and name.
- `TrayTimes/Controls/TripProgressView.xaml(.cs)` (new): header with End trip, the status card (I missed it; or Resume and End trip for a saved trip), and the steps; raises Back, End, Missed, and Resume for the flyout to act on.
- `TrayTimes/Controls/TripLegTemplates.xaml(.cs)` (new): the walk and ride templates, moved out of `TripView.xaml`, with the state marker. `TripViewItems.cs`: `TripLegItem` state, accessible name (`ToString()`), and `Sync` (shared by both views).
- `TrayTimes/Controls/ConfirmFlyout.cs` (new): the D1 question. `SuggestionCard(.xaml, Items.cs)`: `ForActiveTrip`, the trip button's tooltip from the item.
- `TrayTimes/Views/TimesFlyoutWindow.xaml(.cs)`: `ProgressHost`; `StartShowing` (from `ShowPopup`); `OpenProgress`, `CloseProgress`, `RenderProgress`, `ShowBoardAgain` (shared with `CloseTrip`); `OnTripsChanged` (subscribed while shown); Go; `FollowRide`; `TrackOrAsk` (D1); Escape/Backspace/Alt+Left from the progress view; `IsTripShown` wherever the board left the map alone for the trip view. Takes `ActiveTripService`.
- `TrayTimes/Views/TripMapRenderer.cs`: `Emphasize(leg)`.
- `TrayTimes/Services/TripMessages.cs`: `Status`, `Resume`, `CardTitle`, `CardName`, `TimeLabel`, `TimeName`, `When`, `Day`, `Arrival`, `GoName`, `Started`, `TrackInstead`, and the `TripStatus` record; `AfterRide` and `WalkTo` shared with the notifications.
- `TrayTimes.Core/Commute/TripProgress.cs`: `TripLegState`, `TripStep.CanMiss`, `TripStep.StateOf`; `Missed` uses `CanMiss`.
- `TrayTimes/App.xaml.cs`: the flyout gets `Trips`; a manual launch restores the saved trip before opening the flyout; the tray menu's I missed it uses `CanMiss`.
- `Strings/en-us/Resources.resw`: Go, time label, status, card, Resume, D1, and leg-name strings (all with placeholders and comments); `SuggestionTripButton`'s tooltip key became `SuggestionTripTooltip`; the four "Then…"/"You'll arrive…" notification texts take a duration phrase and have no trailing period; `TripFutureScheduled` reworded.

**Decisions**

- The Resume card is the progress view without a step, so a saved trip opens like an active one (and Back shows its card on the board: "Trip to Work · resume or end it?").
- During a trip, the board's card is the trip and Going to is hidden: one trip at a time, ended before planning another. A bell asks before ending a waiting (not yet resumed) trip too.
- Narrator: the status text's name is the spoken status; `LiveRegionChanged` only on a step change, I missed it, or Resume, so the countdown doesn't talk every minute.
- The current ride's vehicle is the one request the progress view adds (Normal budget only), as §Opening the flyout during a trip allows; it doesn't move the map.

**Tests** (44 new)

- `TrayTimes.Tests/Services/TripMessagesTests.cs` (new, 33 cases), against the app's English strings: the status at each step (before leaving, one minute left, walking, waiting for the transfer, each ride, the last walk, arrived, waiting for a plan, scheduled for tomorrow) with its spoken form ("Leave in 6 minutes. Walk 5 minutes to Oak & 5th for the 14"); the card title (4 steps, and resume) and name; the Resume prompt; the time label and full name; `Day` (today, tomorrow, 6 and 7 days ahead, yesterday); Go's name (known and guessed start, leave time passed); the arrival line; the started caption (soon, and scheduled from a guessed start); the D1 question; the Get off soon notification text after the shared `AfterRide`.
- `TrayTimes.Tests/Commute/TripProgressTests.cs` (+11 cases): `StateOf` at four times; `CanMiss` for each step kind and while waiting for a plan.
- Test support: the `ResourceLoader` double (`LocationPlatformDoubles.cs`) still returns keys, and inside `ResourceLoader.UseEnglish()` returns the app's own strings (the `.resw` is copied to the test output) with the en-US culture, per test flow.

**Commands** (ARM64, from the repository root)

- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64`: 0 warnings, 0 errors (before the change too).
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --filter "FullyQualifiedName~TripMessagesTests|FullyQualifiedName~TripProgressTests|FullyQualifiedName~ActiveTripServiceTests"`: 135/135.
- `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64`: 707/707, none skipped (663 before).
- `winapp run .\TrayTimes\TrayTimes.csproj -c Debug --arch arm64 -p Platform=ARM64 --no-build --detach`, then `winapp ui inspect/invoke/search/screenshot/send-keys` (2× display scale, so 800×1360 px for the 400×680 epx flyout):
  - Trip view header at 400 px with Leave now: one row; heading 155 epx, dropdown 105 epx, Go 52 epx; Go named "Start trip to Work, leaving 11:03 PM" (with a guessed start, "Start trip from Work to Home, leaving 10:57 PM"). The legs read as text ("Walk 4 min · 0.2 mi. To …"), no state markers.
  - Go: the progress view with "Following your trip to Work…", status "Leave in 10 min" over "Walk 2 min to Humboldt / Burleigh for the 14" (named "Leave in 10 minutes. Walk 2 minutes to Humboldt / Burleigh for the 14", `TripStatus`), the walk current (filled dot, "Now: …") and the rest upcoming (rings), End trip where Go was, the ride line fainter than the walk.
  - Back: the board with "Trip to Work · leave in 9 min" (14, arrive 11:23 PM), no Going to, tooltip "Show your trip's progress", named "Trip to Work. Leave in 9 minutes. …". The card: back to the progress view.
  - A bell on the board during the trip: "End your trip to Work and track this bus instead?" with End trip and track (focused) and Keep trip; dismissing it kept the trip and tracked nothing.
  - Escape on the progress view: the board; Escape on the board: hidden. Launching again: the progress view ("Leave in 8 minutes…"), focus on Back.
  - End trip (from the view, 7 minutes after Go): the board with the suggestion and Going to again; `active-trip.json` deleted.
  - Requests (the app's request log): none from Go, the progress view, Back, the card, the bell question, Escape, or reopening; one `consumer=ActiveTrip` check at the head-out decision (with the board's stops in its batch), which found the 14 running 5 minutes late and moved the countdown. Screenshots in the session scratchpad (`shots\01`–`08`).
  - Left behind: no saved trip; the app stopped (`taskkill /IM TrayTimes.exe /F`).
- Not run by hand (the developer said they'd test it): I missed it in the view (it needs the To the stop step), the Resume card after a restart, the scheduled trip, the "Arrive by Tue 9:00 AM" label and a long searched destination at 400 px, the tray menu's and notification's I missed it and End trip during this step's UI, and the checks below.

**Packaged checks for the developer**

1. Keyboard only: open during a trip; Tab reaches Back, End trip, and I missed it (on the walk to the stop); Escape, Backspace, and Alt+Left go back to the board; the card (Enter) returns; Escape on the board hides. In the trip view, Tab reaches the time dropdown and Go.
2. Narrator: the progress view's status reads like "Leave in 6 minutes. Walk 4 minutes to Oak & 5th for the 14"; the next step is announced once when it changes (and after I missed it or Resume), not every minute; the steps read "Done: …", "Now: …"; the bell's question is read with its buttons.
3. High Contrast (each theme): the status card, the state markers (check, dot, ring), dimmed done legs, and the End trip/I missed it/Go buttons are readable.
4. Text size 150% (Settings → Accessibility → Text size): with Leave now the header stays one row with the heading wrapped; with "Arrive by Tue 9:00 AM" and a long searched destination, the dropdown and Go move under the heading and nothing is clipped. At 100%, the long label and name fit on one row with the heading trimmed.
5. Restart mid-trip (Exit from the tray, then launch): the flyout opens on "Resume your trip to Work?"; Back shows "Trip to Work · resume or end it?"; Resume follows the trip (one check); End trip forgets it.
6. Leave at tomorrow → Go: the caption and status say it's scheduled ("Leave Tue 8:00 AM"); no calls; End trip.
7. On the walk to the stop: I missed it in the view re-times the trip (status announced); the vehicle of the ride shows on the map at the Normal budget level.

**Next:** step 04, the pop-up without focus. `ShowPopup` ends in `StartShowing()`, which holds what every open shows; the `activate: false` path only needs a different window appearance (`AppWindow.Show(false)`, no `Activate`/`SetForegroundWindow`) and the 30 s hide timer. `Trips.Alerted` isn't subscribed by the flyout yet; `App` should open the flyout on Leave now, Get off soon, and Connection changed.

### 2026-10-20 — Step 03 feedback: map continuity and flyout refinements

**Changes**

- Kept the trip's camera in the long-lived service, separately from the near-you board camera. Preserved the planning camera on Go, avoided progress-update refits and intermediate board framing, and deferred the first fit until the map has a viewport. The one-minute hidden-window teardown remains intentional.
- Followed the selected ride's bus during Get ready as well as walking, transfers, and riding, subject to the existing live window, trip-ID match, foreground lifetime, and Normal budget.
- Made **I missed it** a smaller subtle button without changing when it is available.
- Moved the home places dropdown into the top header beside Settings, replacing the refresh and fit buttons. Kept both actions in the map context menu and keyboard shortcuts.

**Evidence**

- Targeted regression tests: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=ARM64 --no-restore --filter "FullyQualifiedName~TripProgressTests|FullyQualifiedName~VehiclePositionsMonitorTests|FullyQualifiedName~ActiveTripServiceTests|FullyQualifiedName~MapViewTests" --verbosity quiet`: 136 passed, none failed or skipped. The new cases cover vehicle eligibility, camera lifetime/reset, and backward-compatible camera JSON.
- `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=ARM64 --no-restore --verbosity quiet`: 0 warnings, 0 errors. Changed C# files also passed whitespace verification.
- Local packaged UI checks (Windows Sandbox unavailable): the places dropdown and Settings are aligned without overlap; the map context menu retains refresh/fit; the smaller missed action is visible and focusable.
- Camera screenshots: Back/card round-trip and full hidden-window teardown/recreation each had 0% changed sampled map pixels. A separate warm hide/reopen kept the same main HWND and 0% changed sampled pixels outside the temporary startup-caption overlay. Initial harness failures came from single-instance activation PIDs, tooltip HWND selection, a hidden starting window, and that overlay; these were distinguished from camera movement.
- An actual trip's bus was visible before leaving, with successful `consumer=ActiveTrip` vehicle requests and subsequent timer updates. Agency feeds still determine whether the selected ride has a published vehicle.
- To check the missed action without waiting for a distant departure, advanced only the UI test's own saved trip to the walking state and resumed it. Ended all test-created trips afterward; no saved test trip remains, and the packaged app is left running. Did not change location permissions or user settings.

Step 04 has not been started.

### 2026-10-20 — Deferred fix: scheduled arrive-by trip checked early

- **Outstanding fix:** An early check for an arrive-by trip scheduled for tomorrow can select today's departure when the GTFS trip ID repeats daily, shifting the trip off its intended travel date.
- Preserve the scheduled departure on the intended date when matching repeated trip IDs during an early check. Then remove `[Ignore]` from `ScheduledTripCheckTests.Advance_ArriveByTomorrowCheckedTodayWithTheSameTripId_KeepsTomorrowsDeparture` and verify it passes.
- This regression test remains skipped until the fix is made; the defect is intentionally deferred.
