# Transit API optimization plan

## Purpose and operating rules

Reduce transit API requests and time-to-useful-arrivals without losing arrival freshness, tracking alerts, bus locations, or the Favorites tray tooltip. Implement one step per session/request. This file is the persistent source of truth; a fresh session must not depend on the previous conversation or session-local todos.

Created: 2026-10-06. Implementation progress is recorded below.

- Read this entire file, repository instructions, relevant source, and current project files before implementing a step. Source anchors below describe the audit baseline and may move after edits.
- Resume an `in_progress` or `blocked` step before starting another. Otherwise select the lowest-numbered `pending` step. The table defines execution order, not permission to implement multiple steps.
- Update the progress table, current checkpoint, and execution log when starting, reaching a meaningful milestone, becoming blocked, and completing a step.
- Complete only the selected step and its directly necessary supporting changes. Do not opportunistically implement later steps.
- Mark a step `done` only after its acceptance criteria, relevant tests, app build, and applicable runtime verification pass. Record actual evidence, not just "verified."
- If interrupted, leave `in_progress` with files changed, outstanding work, and the exact next action. If blocked, record the blocker; do not skip ahead automatically.
- Steps 08 and 09 are decision-gated. A `deferred` outcome requires a documented user decision, not an automatic fallback. Stop that session after recording the decision.
- Never discard unrelated worktree changes or commit changes unless the user requests a commit.
- Update directly affected README descriptions when behavior changes; do not describe planned behavior as implemented.

Allowed statuses: `pending`, `in_progress`, `blocked`, `done`, `deferred`.

## Persistent progress

| Step | Deliverable | Status | Evidence / handoff |
|------|-------------|--------|--------------------|
| 01 | Establish request diagnostics and a reproducible baseline | done | 190/190 tests; ARM64 Debug build 0 warnings/errors; packaged responsive flyout (5/5 checks); baseline and repeat procedure below |
| 02 | Deduplicate vehicle-position requests | done | 138/138 targeted, 222/222 full; ARM64 Debug/Release 0 warnings/errors; packaged smoke 10/10; four routine vehicles per warm [0,60) |
| 03 | Share prediction requests and enforce safe batching | pending | |
| 04 | Separate place discovery from arrival refresh | pending | |
| 05 | Re-merge GTFS locally instead of refetching predictions | pending | |
| 06 | Add coordinated cancellation, failure cooldowns, and backoff | pending | |
| 07 | Share metadata caches and unblock the Settings picker | pending | |
| 08 | Evaluate and safely reduce passed-bus polling | pending | User decision required if tracking semantics change |
| 09 | Evaluate replacing the BusTime stop crawl with GTFS | pending | Compatibility and user-approval gate |
| 10 | Verify cumulative savings and finalize documentation | pending | |

### Current checkpoint

- Selected step: 02 (done); no later implementation step started.
- Next step: 03.
- Last completed step: 02.
- Changes so far: added Core `VehiclePositionsMonitor` and deterministic tests; wired all flyout vehicle calls through it, invalidated publication on hide/mode changes, removed loading/completion duplicates, preserved manual/F5 forcing, and updated README API-budget behavior. On the user's follow-up request, committed step 01 as `75d5398` and step 02 as `41df2ff`; no dependency changes.
- Verification so far: 106/106 pre-change baseline; final targeted suite 138/138 and full suite 222/222, no skips. ARM64 Debug and Release builds both passed with 0 warnings/errors. Warm replays: Favorites 6 total calls/minute, Commute/Nearby with Favorites 8; overlapping bursts with 0/2/14.999-second controlled responses each produce exactly four routine vehicle calls in [0,60). Final packaged smoke passed 10/10 after fixing session-script selectors/focus; all four final screenshots reviewed, including selected yellow bus and heading arrow. PID 39004 was verified responsive and hidden in the tray, with original tab restored; runtime was not repeated during the commit-only follow-up.
- Blockers: none. No live request-rate/latency profiling, cold crawl, or Release runtime verification was performed.
- Open decisions: safe passed-bus confirmation/recovery policy (08); whether GTFS stop-directory replacement is worth implementing (09).
- Next action: in a fresh request, read this entire plan and implement only 03: share prediction requests across Favorites/place/tracking consumers and enforce safe batching, preserving tooltip/tracking/manual/cancellation invariants. Do not start 04 or alter approval-gated behavior.

## Audit baseline

There are six BusTime endpoints across 12 direct service-call sites. They all send through `TrayTimes.Core\Transit\BusTime\BusTimeTransitService.cs`, whose `EndpointCalled` event feeds the saved endpoint tally. A separate GTFS HTTP request is not charged to the BusTime API key.

| Request path | Baseline triggers and behavior | Source anchors |
|--------------|--------------------------------|----------------|
| Favorites predictions | Every 30 seconds while any flyout tab is open; opening with data older than 15 seconds or changed stops; favorite add/remove/reorder; manual refresh/F5; timetable loaded. Up to 10 distinct stops per request. | `TrayTimes\Services\ArrivalsMonitor.cs:31-154`; `TrayTimes\Views\TimesFlyoutWindow.xaml.cs:1530` |
| Commute/Nearby predictions | Mode entry and reopen; 30-second timer; manual refresh/F5; destination change; commute place changes; retry action; timetable loaded; a refined location more than 25 metres from the rough fix can fetch again. Up to eight listed stops. | `TrayTimes\Views\TimesFlyoutWindow.xaml.cs:435-538,696-699,1000-1021` |
| Tracking predictions | Independent timer for one stop, including while hidden. Real-time intervals: 60 seconds above 10 minutes, 30 seconds above 5 minutes, otherwise 10 seconds. Scheduled-only arrivals skip requests until the lookahead/head-out window. | `TrayTimes\Services\BusTrackingService.cs:68-115`; `TrayTimes.Core\Tracking\TrackedBus.cs:88-105` |
| Vehicle positions | 15-second timer; Favorites mode entry; both loading and result notifications from Favorites; displaying place results; place-load completion. Up to 10 distinct vehicle IDs. No in-flight/freshness guard. | `TrayTimes\Views\TimesFlyoutWindow.xaml.cs:130,415,492,538,1045,1132-1150`; `TrayTimes\Services\ArrivalsMonitor.cs:117-118,162-166` |
| Route catalog | Settings open; all-route Favorites colors; Commute/Nearby warm-up/load. Cached task is shared, but flyout teardown clears it. | `TrayTimes\Services\ArrivalsMonitor.cs:78,84-96,169-172`; `TrayTimes\App.xaml.cs:238` |
| Settings directions/stops/patterns | Route selection fetches directions and auto-selects the first; direction selection fetches stops and patterns. Directions/stops are uncached. Pattern cache lasts only as long as the Settings window. Stop selection waits for patterns too. | `TrayTimes\Views\SettingsWindow.xaml.cs:333-383,512-522` |
| Flyout patterns | Favorites route lines and Commute direct-route matching. Six-hour cache is window-owned, lost after the one-minute hidden teardown. Cache contains results, not shared in-flight tasks. | `TrayTimes\Views\TimesFlyoutWindow.xaml.cs:49,72,595,1087,1111-1121`; `TrayTimes\App.xaml.cs:223-238` |
| Stop-directory crawl | Startup even while hidden; first use with missing/expired disk cache after unloading; Settings Update now. Seven-day disk cache. A complete rebuild makes `1 + routes + total directions` requests, roughly 100 per the README. | `TrayTimes\App.xaml.cs:120-139`; `TrayTimes\Services\StopIndexService.cs:43-145`; `TrayTimes\Views\SettingsWindow.xaml.cs:246-250` |
| GTFS feed | First timetable use; later load with a feed check older than seven days. Cached ZIP and conditional HTTP GET. An in-memory reload can raise Loaded even without downloading. | `TrayTimes\Services\ScheduleService.cs:59-78,104-155` |

Browsing timers stop immediately on dismissal, not after the one-minute teardown. Already-started work is not canceled and may continue issuing requests. Tracking and startup stop-directory warm-up are separate exceptions to hidden-window inactivity.

The existing Settings tally retains endpoint attempts for today, seven days, and 14 days. It includes failed attempts but does not identify trigger, duration, outcome, cache reuse, or whether the server billed the attempt. Preserve its existing meaning.

### Estimates to verify, not measured results

With stable live vehicles, successful fast requests, and no tracking, metadata, startup, or manual-refresh traffic:

| Scenario | Baseline nominal calls/minute | Potential target |
|----------|-------------------------------|------------------|
| Favorites | 2 predictions + about 8 vehicles = about 10 | 2 predictions + 4 vehicles = 6 |
| Commute/Nearby with saved Favorites | 2 active predictions + 2 Favorites predictions + about 8 vehicles = about 12 | 2 combined predictions + 4 vehicles = 6, when the stop union fits one batch |

These imply roughly 40% and 50% reductions respectively. The second target is conditional: Favorites also feeds the tray tooltip, and more than 10 distinct requested stops require more than one batch. Record actual results in step 10.

## Non-negotiable invariants

- Preserve the intended 15-second vehicle refresh and 30-second browsing prediction cadence under normal conditions. Remove redundant requests before proposing longer intervals.
- Preserve tracking's near-arrival freshness, head-out/time-change/cancellation alerts, scheduled-only lookahead, grace periods, and keep-awake behavior unless a change is explicitly approved.
- Keep the Favorites tray tooltip supplied. Do not simply stop Favorites while another tab is selected.
- Preserve manual refresh/F5. A forced refresh may bypass freshness reuse but must not create duplicate equivalent in-flight requests.
- Preserve last-good-data/error UX. Distinguish failed/canceled requests from a successful empty response; neither errors nor cancellation prove that a bus passed.
- Browsing must not start new network work while hidden. Canceling a shared browsing waiter must not cancel a request still needed by tracking or another live consumer.
- The client currently truncates IDs to 10 in `JoinIds`. A merged set must be split safely or rejected explicitly, never silently lose stops/vehicles. Cover 0, 1, 10, 11, and larger sets.
- Predictions and positions are different HTTP endpoints. Parallel requests are not request consolidation. Do not assume multi-route `getpatterns?rt=...` batching; verify provider documentation before changing API contracts.
- Keep logs in English. Never record keys, query-string URLs, addresses, coordinates, or raw exceptions that could contain credentials. Distinguish physical HTTP attempts from consumer calls/cache hits.
- Keep caches/diagnostics bounded. Put testable request coordination and policies in the existing Core/Tests structure where practical; avoid a generic scheduling or telemetry framework.
- Never use a large live transit crawl or repeated production calls merely to prove an optimization. Prefer fake HTTP handlers, controllable completion, and a controllable clock, plus a short runtime check when appropriate.

## Step specifications

### 01 - Establish request diagnostics and a reproducible baseline

**Scope:** Extend the existing request observation mechanism only enough to distinguish endpoint, trigger/consumer, start/end, elapsed duration, outcome, batch size, and reuse. Keep the existing saved Settings counts and their format compatible. Use bounded diagnostic retention or the existing diagnostic convention, not a new analytics backend or UI.

**Start here:** `BusTimeTransitService`, `ApiUsageService`, `ApiCallLog`, the three prediction consumers, vehicle refresh event paths, and existing `BusTimeTransitServiceTests`.

**Acceptance:**

- One physical HTTP attempt increments the existing endpoint tally exactly once; sharing/reusing results must not increment it again.
- Diagnostics distinguish success, HTTP/API failure, cancellation, and cache/in-flight reuse without leaking credentials.
- Obtain reproducible request counts for timer plus event bursts, Favorites, Commute/Nearby with Favorites, tracking, and timetable-loaded/reopen paths. Record which are tested observations versus code-derived estimates.
- Test the actual request path and relevant consumer wiring, not only an isolated counter that would miss duplicate calls.
- Document the diagnostic entry point and baseline procedure in the execution log so the next session can repeat it without chat history.

**Do not:** change polling frequency, caching, or request scheduling yet.

### 02 - Deduplicate vehicle-position requests

**Scope:** Coordinate `RefreshVehiclesAsync` callers so timer, mode entry, loading/result events, and place-load completion cannot issue redundant equivalent requests.

**Acceptance:**

- Equivalent overlapping vehicle requests share/skip one physical request; ordinary triggers reuse successful data younger than 15 seconds.
- Remove the loading-only Favorites vehicle fetch and the duplicate place-completion fetch, or make them harmless through the same shared guard.
- A changed vehicle set can obtain newly needed positions promptly. Forced refresh retains intentional semantics.
- Slow responses, failures, tab switches, dismissal, and reopen cannot overwrite newer state or permanently block subsequent requests. Do not cache a failed request as successful/fresh.
- Under a deterministic steady-state scenario with unchanged vehicle IDs, after the initial fetch, a half-open 60-second interval produces no more than four routine vehicle requests despite overlapping UI notifications.
- Preserve bus selection, heading arrows, map positions, and the intended refresh cadence.

### 03 - Share prediction requests and enforce safe batching

**Scope:** Add the smallest shared coordination mechanism and wire Favorites, Commute/Nearby, and tracking to it. Reuse equivalent or sufficiently fresh covering results; combine compatible due requests where useful. Prevent silent truncation.

**Acceptance:**

- Equivalent overlapping requests create one physical request. Consumers receive arrivals scoped to their own stop IDs.
- Browsing consumers due together can share a union of at most 10 distinct stops. Larger unions fetch all required stops in batches of at most 10.
- Tracking can reuse compatible browsing results without accepting data older than its current tracking interval. A faster tracked-stop cadence must not cause unnecessary extra batches for unrelated browsing stops.
- Preserve Favorites tooltip updates, alerts, manual refresh, and independent hidden tracking. Do not add a noticeable aggregation delay to urgent tracking requests.
- Test overlapping/disjoint IDs, mixed freshness requirements, more than 10 IDs, empty results, errors, and cancellation ownership.
- Treat successful empty results as valid fresh data. Shared results must not cause duplicate tracking alerts or treat an error as a vanished bus.
- Record the achieved request counts for both one-batch and multi-batch cases.

### 04 - Separate place discovery from arrival refresh

**Scope:** Stop redoing location, walking routes, and commute route selection before every prediction refresh. Retain valid place results across quick reopen. Separate discovery invalidation from prediction freshness.

**Acceptance:**

- With unchanged location/destination and a valid stop set, a routine refresh gets predictions without first waiting for location or walking-route requests.
- Show valid last-known results immediately on reopen; refresh only what is stale. A different destination/place configuration must never display the old commute as current.
- Re-resolve stops when meaningful movement, destination/place changes, directory changes, or missing discovery data requires it. Reuse the existing 25-metre movement policy where appropriate.
- A rough-to-precise location improvement that produces the same stop IDs does not immediately repeat a fresh prediction request merely to update walking metadata.
- Walking estimates, too-soon dimming, direction filtering, and destination ETAs still update correctly. Do not display unvalidated direct routes as confirmed.
- Verify the reduction in discovery requests and time-to-useful-data using the step 01 procedure, including slow location/walking responses and quick reopen.

### 05 - Re-merge GTFS locally instead of refetching predictions

**Scope:** Change timetable-loaded handling to merge the newly available schedule with cached raw live predictions and recompute relevant derived display data.

**Acceptance:**

- GTFS Loaded does not itself cause `getpredictions` for Favorites or Commute/Nearby when an applicable live snapshot exists.
- Scheduled rows and commute destination ETAs become available without waiting for another HTTP response.
- Preserve the live snapshot's actual fetch timestamp, errors, and freshness age. Do not label a local merge as a fresh network result.
- Preserve normal timed/manual refreshes. If no applicable snapshot exists, use the normal refresh path without issuing duplicate requests.
- Test first feed load, in-memory reload from disk, empty live data, failed live fetch with last-good data, and a destination/tab change during load.

### 06 - Add coordinated cancellation, failure cooldowns, and backoff

**Scope:** Cancel abandoned browsing/picker/discovery work before it starts later request stages; prevent failed index rebuild loops and overlapping retries; apply documented failure/rate-limit handling to shared request paths.

**Acceptance:**

- Dismissal, teardown, mode/destination changes, and superseded picker selections cannot continue starting unnecessary downstream requests.
- Generation checks still protect UI state. Request lifetime and cancellation ownership respect shared consumers, particularly hidden tracking.
- Ordinary network failures retry with a bounded cooldown/backoff, success resets it, and provider rate-limit retry/reset information is honored where available.
- A failed directory rebuild does not immediately repeat a full crawl for every subsequent caller.
- Define and record distinct rules for automatic retry, manual retry, permanent API/configuration errors, and tracking timeliness. Obtain approval before changing tracking freshness/alert guarantees beyond unavoidable provider rate limiting.
- Surface failures through repository-standard diagnostics and existing user-visible status; do not silently convert failures into successful empty data.
- Test slow/in-flight requests, rapid hide/show, cancellation races, transient errors, 429 responses, and recovery without generating a live request storm.

### 07 - Share metadata caches and unblock the Settings picker

**Scope:** Move route/direction/stop/pattern reuse out of disposable windows. Share successful data and in-flight loads across the directory crawl, Settings, and flyout where appropriate. Make stop selection independent of decorative pattern completion.

**Acceptance:**

- Recreating either window within a valid cache lifetime does not refetch unchanged metadata solely because the old window was destroyed.
- Preserve the existing intended lifetimes as starting points: six hours for patterns and seven days for directory-derived data. Use bounded caches; justify any change.
- Reuse complete valid stop-directory metadata where it preserves IDs, direction labels, and picker behavior. Fall back explicitly when data is stale/incomplete or required ordering is unavailable.
- Concurrent metadata misses share one task; failures are not retained as successful data.
- Enable/populate the stop picker immediately when stops arrive; draw route lines when patterns arrive. Pattern failure must not block stop selection.
- Consider bounded parallel loading for Favorites route lines to remove serial waits, but do not count parallelism as API savings.
- Test fresh/expired caches, window recreation, concurrent misses, partial directory builds, manual index rebuild invalidation, and slow/failed pattern requests.

### 08 - Evaluate and safely reduce passed-bus polling

**Scope:** Review tracking behavior after DueSince/HasPassed and quantify the remaining network requests during the five-minute grace period. This is not authorization to stop after a single missing prediction.

**Decision gate:** Establish whether network polling can be reduced without losing cancellation/reappearance behavior or confusing transient prediction disappearance with passage. If behavior must change, ask the user for the confirmation/recovery policy and record the decision before implementation.

**Acceptance:**

- Keep the intended grace-period display and lifecycle, and never treat a failed/canceled fetch as evidence of passage.
- Test disappearance, reappearance, cancellations, empty successful responses, errors, and grace-period completion.
- Quantify the before/after per-trip savings; the audit's upper estimate is almost 30 remaining requests, not guaranteed savings.
- If the safe tradeoff is not worthwhile or cannot preserve required behavior, obtain approval to defer and document why. Do not claim full savings without verifying the relevant cases.

### 09 - Evaluate replacing the BusTime stop crawl with GTFS

**Scope:** Assess whether the already downloaded GTFS feed can supply the directory's stop coordinates, route membership, and usable direction labels/IDs, replacing all or part of the weekly crawl.

**Decision gate:** First document compatibility evidence and the smallest viable approach. Ask for approval before replacing directory construction. The existing timetable reader depends on the BusTime directory for ID translation; do not introduce a circular load dependency or guess numeric direction mappings.

**Acceptance for an implementation:**

- Preserve stop IDs, realtime/GTFS route and direction matching, Nearby results, Commute direct-route filtering, and Settings picker behavior.
- Break the existing directory -> timetable dependency safely if GTFS becomes a directory source.
- Verify mappings using representative fixture data, including suffixed route IDs and both directions; use only a minimal live sample if genuinely necessary.
- Retain an explicit fallback for missing/incompatible feeds, first-run/offline behavior, and incomplete mappings. Do not silently present an incomplete directory as a complete success.
- Record requests required for a cold build, warm load, update, and fallback. Count GTFS bandwidth separately from BusTime calls.
- If approved for deferral, record the evidence, residual weekly cost, and decision instead of implementing an unverified migration.

### 10 - Verify cumulative savings and finalize documentation

**Scope:** Re-run the reproducible scenarios from step 01 against the completed changes, close documentation gaps, and finish the handoff.

**Acceptance:**

- Steps 01-07 are done; 08 and 09 are done or explicitly deferred by the user.
- Record baseline versus final physical requests by endpoint and scenario, observation duration, cache state, stop/vehicle counts, failures, and whether measurements are deterministic or live.
- Verify the step 02 vehicle bound and quantify prediction sharing separately for unions within and beyond 10 stops. Do not promise the conditional 50% reduction in every configuration.
- Cover Favorites, Nearby, Commute, quick reopen, hidden idle, hidden tracking, manual refresh, directory/feed load, and error recovery.
- Verify user-visible arrivals, tooltip, map selection/headings, walking/destination estimates, tracking alerts, and Settings picker responsiveness.
- Run the full test suite for cumulative cross-cutting changes, build Debug and Release, and verify a packaged launch. Record any environmental blocker plainly.
- Update README API-budget/network descriptions to match actual behavior, and leave no unresolved `in_progress` rows or unexplained validation gaps.

## Verification and session handoff

Read the current `.csproj` files for framework, platform support, package versions, and packaging. Use the existing MSTest project; do not add dependencies unless the selected step needs them. Follow `AGENTS.md` and applicable `.github\instructions\` files, including their required references.

From the repository root, detect the machine architecture in each fresh shell before build/test commands:

```powershell
$arch = $env:PROCESSOR_ARCHITECTURE
$Platform = if ($arch -eq 'AMD64') { 'x64' } else { $arch }

dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "<actual related classes/namespaces>"
dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform
```

Replace the test filter with the actual affected tests, combine related selectors in one run, and expand to the full suite for cross-cutting changes. Each shell is fresh: re-detect `$Platform` rather than assuming it persists. Confirm that packaged launch reaches the expected responsive tray/flyout state; launcher exit alone is not proof. Stop only a known app PID if a running instance blocks a rebuild.

Every implementation session must append a concise execution-log entry containing:

- Date, selected step, and final status.
- Changes and important design/user decisions, including any revised source anchors.
- Files touched and new test names/seams.
- Exact build/test/run commands, outcomes, and request-count/latency evidence. State explicitly what was not run or could not be measured.
- Remaining work, blockers, and exact next action. Set the table and checkpoint consistently before ending the session.

## Execution log

### 2026-10-06 - Commit handoff

- User explicitly requested "make logical commits" after step 02 completed. This follow-up only organizes the already completed work; step 03 remains pending and no new implementation or approval-gated behavior was started.
- Commit `75d5398` - `Instrument transit requests and establish reproducible baselines`: bounded diagnostics, endpoint/consumer labels, existing tally integration, optional system-default clock seams, linked production-service fixtures, baseline tests, and the README diagnostic entry.
- Commit `41df2ff` - `Deduplicate vehicle-position refreshes`: Core vehicle monitor, flyout wiring/manual-refresh/visibility guards, vehicle lifecycle/failure/boundary tests, updated request-count replays, and README API-budget behavior.
- Mixed flyout/README/baseline-test changes were staged separately using isolated copies and Git index blobs, without changing the final working sources. The exact first-commit index was exported to an owned temporary `.commit-check` snapshot; it contains no vehicle-coordinator dependency and passed all 190 tests plus the ARM64 Debug app build with 0 warnings/errors.
- Exact staged-snapshot commands after architecture detection: `dotnet test .\.commit-check\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --verbosity quiet` (190/190, no skips, 126 ms) and `dotnet build .\.commit-check\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform --verbosity quiet` (0 warnings/errors, 24.87 s).
- Final vehicle-commit commands after architecture detection: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --verbosity quiet` (222/222, no skips, 139 ms) and `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform --verbosity quiet` (0 warnings/errors, 22.14 s). Existing Debug/Release and packaged-runtime evidence remains applicable because no production code changed during commit preparation.
- Reviewed the commit boundaries and WinUI changes before committing; no blocking issue found. Both implementation commits include the required Copilot co-author trailer. Ignored `OAuth.resw`, build outputs, and runtime/session artifacts were not staged.
- Removed only the owned temporary validation snapshot and three mixed-file staging copies. The remaining documentation commit records this persistent plan, both completed steps, verification evidence, and handoff; no push was requested.
- Blockers: none. Next action: in a fresh implementation request, read the entire plan and select only step 03.

### 2026-10-06 - Step 02 (done)

- Selected the lowest-numbered pending step (02); no in-progress/blocked step existed. Updated progress/checkpoint before coding.
- Read the entire persistent plan, `AGENTS.md`, all applicable `.github\instructions\` files, and current project files. App remains packaged WinUI (`net10.0-windows10.0.26100.0`, Windows App SDK `2.5.1`, x64/ARM64); Core/Tests remain `net10.0`, existing MSTest `4.4.1`. Detected machine architecture: ARM64.
- Existing dirty/untracked files match the recorded step 01 work and plan; preserve them. No commits, dependency changes, tracking/prediction scheduling changes, or later-step implementation authorized.
- Scope: vehicle-only equivalent in-flight sharing, successful-data reuse strictly younger than 15 seconds, changed-set/forced-refresh correctness, and stale UI protection; retain the 15-second timer and current map/selection/heading behavior.
- Milestone: existing diagnostic/baseline/tracking tests passed, 106/106, no skips, using `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "FullyQualifiedName~RequestBaselineTests|FullyQualifiedName~TransitRequestDiagnosticsTests|FullyQualifiedName~BusTimeTransitServiceTests|FullyQualifiedName~ApiCallLogTests|FullyQualifiedName~TrackedBusTests" --verbosity quiet` after architecture detection (ARM64).
- Design: use one flyout-owned, UI-free `VehiclePositionsMonitor` in Core, exercised against the real fake-handler BusTime path. Retain only one successful vehicle-set snapshot (at most the existing map limit), share equivalent outstanding tasks, remove them on completion, and publish only the latest visible generation. Changed sets start immediately rather than waiting for unrelated slow requests. Measure the 15-second cache age from request start with a monotonic clock, so slow responses cannot extend the intended freshness window.
- Wiring decision: skip Favorites loading notifications, remove the place-completion duplicate, and force one vehicle refresh of the displayed set on manual refresh/F5; subsequent result notifications use the same guard. Hide/tab/reset invalidate pending publication without canceling shared equivalent waiters. Broader request cancellation/backoff stays in step 06. Reject oversized coordinator inputs explicitly rather than relying on `JoinIds` truncation; the UI continues intentionally displaying only its soonest ten live vehicles. No prediction batching is implemented.
- Milestone: implemented production wiring, monitor, README update, and tests covering reordered/duplicate equivalent IDs, in-flight forcing, 15-second freshness boundary, slow responses, changed-set out-of-order completion, set round trips, empty success, errors/timeouts, hide/reopen/invalidation, provider result scoping, and 0/1/10/11/25 input boundaries. One test-fixture raw-string delimiter compile error was corrected; the same expanded targeted command then passed 134/134, no skips.
- Deterministic evidence so far: the real Favorites monitor + Core vehicle monitor + fake BusTime HTTP path now produce 2 predictions + 4 routine vehicles = 6 calls in [0,60), versus the recorded 10. Place boundary replays (not full location/UI pipeline measurements) with foreground Favorites produce 4 predictions + 4 routine vehicles = 8 versus 12, for both Commute and Nearby. The replay deliberately retains an extra place-completion trigger to prove the shared guard also makes it harmless; production no longer issues that trigger. Predictions remain unoptimized.
- Next action: run added overlapping-burst tests (0/2/14.999-second controlled response times), build Debug, and perform the short cache-safe packaged runtime check before deciding final status.
- Milestone: expanded targeted command passed 138/138 (170 ms test duration); full regression command `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --no-build --verbosity quiet` passed 222/222 (272 ms), no failures/skips. Burst tests explicitly overlap four equivalent UI triggers per tick; after one initial fetch outside [0,60), all three response delays (0, 2, 14.999 seconds) produce four physical routine requests and 12 in-flight reuse observations.
- Build evidence: `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform --verbosity quiet` passed with 0 warnings/errors in 36.70 s; `dotnet build .\TrayTimes\TrayTimes.csproj -c Release -p:Platform=$Platform --no-restore --verbosity quiet` passed with 0 warnings/errors in 69.60 s. Architecture re-detected for the build shell: ARM64.
- Runtime preparation: confirmed development package `Tray-Times_vek3qaanxbakt`; stopped only verified existing PID 36536 whose executable matched that package's layout before rebuilding. Its stop directory is still fresh (3,714 stops, built 2026-10-04) and GTFS ZIP/check information is fresh (checked 2026-10-04). Sandbox snapshot reports no running/attached/input-ready guest; use the existing local profile for the short smoke check rather than a cold guest crawl. No keys/settings/cache files were deliberately changed.
- Next action: launch the final Debug package and script responsive flyout/map, manual/F5, selection (if a live bus is present), dismissal, and quick reopen checks; inspect screenshots, then record outcome and any runtime limitations before marking 02 done.
- Runtime milestone: `dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform` launched the registered Debug package, PID 39004; launcher remains attached while the tray process runs. Confirmed the process matches the package layout and responds; the single-instance event opens a rendered Commute flyout with enabled refresh/map and live/scheduled rows (initial screenshot reviewed).
- First smoke batch reported 5 passes/5 failures caused by harness selectors/foreground handling, not app failures: text labels made `Favorites`/`Commute` invocation ambiguous, and keyboard injection correctly refused a nonforeground window. A read-only/focus diagnostic successfully focused the intended app. Refined only the session script to resolve tab slugs from `ListItem` nodes and explicitly verify focus before keyboard input. No production changes were needed.
- Next action: run the corrected smoke batch with fresh evidence paths; verify final screenshots and final hidden responsive tray state before marking 02 done.
- Completion: corrected runtime batch `& 'C:\Users\josep\.copilot\session-state\545c6eb1-7217-4fb6-98c0-4db5c99c5815\files\step02-runtime-check.ps1' -AppPid 39004 -EvidencePrefix 'step02-retry1'` passed 10/10, no failures. Checked registered-package/output-path identity, responding process, visible nonempty UIA tree/map, tab selection, a live arrival's map action, manual refresh, F5 via real keyboard input, Escape dismissal, quick Favorites reopen, original Commute tab restoration, and continued tray responsiveness.
- Visual evidence: reviewed all four `step02-retry1-01-initial.png` through `step02-retry1-04-reopened.png` screenshots from the JSON report. They show rendered Commute/Favorites arrivals, map route/stop content, a selected live bus with yellow backdrop and heading arrow, and a correctly repopulated map on reopen. Existing selection reset on mode entry/reopen is unchanged; map icon placement/update, selection/highlight, and arrow methods were moved without altering their rendering logic. A normal owned tooltip appears in the selection screenshot; no new clipping/layout/theming defect was observed.
- Runtime artifacts: the session `files` folder contains `step02-runtime-check.ps1`, the initial failed report/screenshot, and final `step02-retry1-runtime-results.json`/four PNGs. Temporary raw UI-tree JSON was removed; no raw network URLs, credentials, coordinates, or exception dumps were retained in request diagnostics. Final `winapp ui list-windows -a 39004 --json` returned `[]`; package-path match and `Responding=True` were checked again. The app remains running in the tray.
- Cache post-check: stop directory still has 3,714 stops and the same `BuiltAt` (`2026-10-04T23:07:09.484839-05:00`); GTFS `CheckedAt` is unchanged (`2026-10-04T23:08:37.5927025-05:00`). No startup crawl/feed update occurred. No deliberate key/settings/cache edits; normal tally/view-state saves remained active. The smoke flow used ordinary live app requests only, not repeated calls to measure savings.
- Files changed for 02: `TrayTimes.Core\Transit\VehiclePositionsMonitor.cs`; `TrayTimes.Tests\Transit\VehiclePositionsMonitorTests.cs`; `TrayTimes.Tests\Services\RequestBaselineTests.cs`; `TrayTimes\Views\TimesFlyoutWindow.xaml.cs`; `README.md`; this plan. Session runtime script/evidence are not repository changes. Existing step 01 modifications were preserved.
- Revised production anchors: `VehiclePositionsMonitor.cs:60-161` owns freshness/sharing/publication/failure cleanup; `TimesFlyoutWindow.xaml.cs:131,203,227,399` wires the unchanged timer and visibility/mode generation invalidation; `:499-505` no longer fetches on place completion; `:1037-1060` skips Favorites loading-only fetches; `:1147-1220` coordinates positions and retains map rendering; `:1537-1549` preserves manual/F5 forcing.
- Test seams/names: existing fake BusTime HTTP/diagnostic/tally path and monotonic `RequestTestClock`, no new dependencies/platform doubles. The new Core monitor accepts a synchronous map-publication callback and optional system-default clock. Key tests: `RefreshAsync_EquivalentOverlappingAndForcedRequests_SharesOneAttemptAndPublishesLatest`, `RefreshAsync_SuccessAgeBoundary_ReusesOnlyDataYoungerThanFifteenSeconds`, `RefreshAsync_SteadyStateOverlappingBursts_ProducesFourRoutineRequestsInHalfOpenMinute`, `RefreshAsync_ChangedSetAndOutOfOrderCompletion_PublishesAndCachesOnlyNewerState`, `RefreshAsync_FailedRequest_PreservesLastGoodPositionsAndAllowsImmediateRetry`, `IsForeground_QuickReopenDuringFetch_SharesPendingRequestWithoutPublishingOldWaiter`, `Invalidate_TabRoundTripWithoutAnotherFetch_DropsOldResultAndAllowsFreshReuse`, and `RefreshAsync_RequestSizeBoundary_NeverSilentlyTruncatesIds`.
- Baseline replay test names now end in `CountsTwoPredictionsAndFourVehicles` / `CountsFourPredictionsAndFourVehicles`; historical step 01 names/counts below describe the original pre-optimization run, not current assertions. The raw-client diagnostic test that observes two uncoordinated direct vehicle calls is deliberately unchanged: deduplication belongs to the flyout's production monitor, not the provider client.

#### Step 02 request-count evidence (deterministic, not production rates)

| Scenario | Step 01 baseline | Step 02 result | Conditions / observation |
|----------|------------------|----------------|--------------------------|
| Warm Favorites | 2 predictions + 8 vehicles = 10 | 2 predictions + 4 vehicles = 6 | Actual Favorites monitor events plus production Core vehicle coordinator and fake HTTP; [0,60), one stop/one stable live vehicle, initial prediction/position fetches excluded |
| Warm Commute with Favorites | 4 predictions + 8 vehicles = 12 | 4 predictions + 4 vehicles = 8 | Place result/completion/timer boundary replay; [0,60), two place stops with one Favorites overlap, one stable live vehicle; warm discovery, no metadata/manual traffic |
| Warm Nearby with Favorites | 4 predictions + 8 vehicles = 12 | 4 predictions + 4 vehicles = 8 | Same fixture bounds as Commute; not a measured location/walking/UI pipeline |
| Four overlapping vehicle triggers at each due boundary | Uncoordinated calls duplicate | Exactly 4 routine physical requests; 12 in-flight reuses | Initial successful position outside [0,60); controlled response duration 0, 2, or 14.999 s; unchanged IDs; all three cases pass |
| Changed set while old fetch is slow | New request allowed, stale-state risk | New set starts immediately; old result cannot overwrite newer map/cache | Controlled out-of-order fake HTTP; set round trip still shares the original equivalent outstanding task |
| Forced refresh of fresh positions | Fetches intentionally | Bypasses successful-data freshness, shares any equivalent outstanding request | One setup attempt + one forced attempt; ordinary/forced overlap adds no attempt; successful empty responses remain fresh and failures/timeouts do not |

- Final decision: all step 02 acceptance criteria pass with deterministic actual request-path/lifecycle/failure evidence, Debug/Release builds, and applicable packaged runtime/visual verification. Normal 15-second vehicle cadence, 30-second predictions, Favorites tooltip, tracking behavior, last-good/error UX, and map selection/headings are preserved. No prediction sharing/batching or later-step behavior was implemented.
- Not measured/run: production per-minute savings or latency, a long live timer/selection soak, injected live failures, Nearby location runtime, Release runtime, or other-architecture builds. Failure/slow-response/tab/hide/reopen behavior is covered deterministically rather than provoking production errors; these omissions do not replace or weaken the specified deterministic vehicle bound.
- Blockers/remaining work for 02: none. Next action: read this plan in a fresh request and implement only 03. Steps 03-10 remain pending; approval gates 08/09 remain untouched. No commits.

### 2026-10-06 - Step 01 (done)

- Selected the lowest-numbered pending step; no prior implementation status to resume.
- Read the full plan, `AGENTS.md`, all applicable instruction files, and the three current projects. App: packaged WinUI, `net10.0-windows10.0.26100.0`, Windows App SDK `2.5.1`, x64/ARM64; Core/Tests: `net10.0`, existing MSTest `4.4.1`.
- Preserve `EndpointCalled` and the saved `ApiCallLog` format/attempt semantics. Add observation only: no polling, caching, scheduling, tracking, or approval-gated behavior changes.
- Current worktree contains untracked `docs` content; do not discard it or commit.
- Next action: audit remaining consumer triggers and test seams, establish the existing-test baseline, then implement diagnostics.
- Milestone: existing request/tally baseline passed, 30 tests, using `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "FullyQualifiedName~BusTimeTransitServiceTests|FullyQualifiedName~ApiCallLogTests" --verbosity quiet` after architecture detection (ARM64).
- Milestone: implemented a 256-observation, in-memory journal with enum-only labels, correlated starts/completions, monotonic elapsed duration, HTTP status and outcomes; no URLs, IDs, keys, exception messages, addresses, or coordinates. `EndpointCalled` and persisted `ApiCallLog` remain unchanged. Added labels to all six endpoint paths and their consumers; existing route/pattern reuse emits observations but no attempt tally.
- Scope decision: existing duplicate calls remain visible, not fixed. Add actual consumer tests by linking Windows-dependent services with test-only platform doubles rather than introducing production scheduling abstractions or a telemetry backend.
- Milestone: 104 targeted tests passed with filter `FullyQualifiedName~TransitRequestDiagnosticsTests|FullyQualifiedName~RequestBaselineTests|FullyQualifiedName~BusTimeTransitServiceTests|FullyQualifiedName~ApiCallLogTests|FullyQualifiedName~TrackedBusTests`. Actual Favorites timer/reopen/timetable/stop-change wiring, route cache/in-flight reuse, tracking timers/lookahead/failure behavior, and the unchanged app tally execute against fake HTTP. Flyout location/map stages are not linked: their audited calls are replayed explicitly and must not be reported as measured UI or production traffic.
- Deterministic baseline so far: warm Favorites boundary replay = 10 calls/minute; warm Commute and Nearby with foreground Favorites = 12; tracking first minute (0,60] = 1/2/6 prediction attempts at >10 / (5,10] / <=5 minutes away; fresh Favorites reopen = 0 prediction attempts, stale reopen = 1, visible timetable event = 1, hidden timetable/timer = 0; concurrent route catalog miss + cached consumer = 1 physical request plus separate in-flight/cache reuse observations.
- Next action: finish coverage additions, run full tests and Debug build, then obtain package-identity and responsive tray/flyout evidence without changing keys/settings or provoking a directory crawl.
- Milestone: `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --verbosity quiet` passed all 190 tests, 0 failures/skips. `dotnet build .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform --verbosity quiet` succeeded, 0 warnings/errors, 27.23 seconds (architecture re-detected in each shell: ARM64).
- Runtime preparation: stopped only the known existing app PID 39980 after confirming its exact output path. Existing development package directory cache is fresh (3,714 stops, built 2026-10-04) and GTFS cache was checked 2026-10-04. Sandbox snapshot reports no running/attached desktop; choosing the existing local package for this short check avoids a cold-profile startup crawl and preserves user settings. No cache/key/settings files were changed.
- Completion: `dotnet run --project .\TrayTimes\TrayTimes.csproj -c Debug -p:Platform=$Platform` rebuilt and launched the registered package `Tray-Times_vek3qaanxbakt`, PID 11276. A local scripted check confirmed package/output-path match and responding process, the single-instance event opening the flyout, enabled refresh control, a nonempty UI Automation tree, and a delivered screenshot (5 passed, 0 failed). Screenshot review confirmed rendered Commute tabs/map/arrival content; no XAML/layout changes were made. Dismissed with Escape, confirmed no visible flyout and a responsive process still running in the tray.
- Runtime artifacts (not required to repeat tests): session `files\step01-runtime-check.ps1`, `files\step01-runtime-results.json`, and `files\step01-flyout.png`. Executed `& '<session folder>\files\step01-runtime-check.ps1' -AppPid 11276`; used `winapp ui send-keys esc -a 11276 --target 'Refresh arrival times' --via send-input --json` and `winapp ui list-windows -a 11276 --json` for dismissal evidence.
- Cache post-check: directory `BuiltAt` remained `2026-10-04T23:07:09.4848390-05:00`, 3,714 stops; feed `CheckedAt` remained `2026-10-04T23:08:37.5927025-05:00`. No startup directory crawl or feed update occurred. No deliberate edits to keys/settings/cache files; normal app tally and view-state saves remained active.
- Final fixture refinement: steady-state boundary replays now return a live prediction and vehicle; the Favorites display-event listener derives its vehicle IDs from the production monitor's actual snapshot. Re-ran the full test command: 190 passed, 0 failures/skips, 119 ms test duration. No production source changed after the packaged run (only test fixtures and this plan).
- Files changed: `TrayTimes.Core\Transit\TransitRequestDiagnostics.cs`, `ITransitService.cs`, `BusTime\BusTimeTransitService.cs`; `TrayTimes\Services\ApiUsageService.cs`, `ArrivalsMonitor.cs`, `BusTrackingService.cs`, `StopIndexService.cs`; `TrayTimes\App.xaml.cs`; `TrayTimes\Views\TimesFlyoutWindow.xaml.cs`, `SettingsWindow.xaml.cs`; `TrayTimes.Tests\TrayTimes.Tests.csproj`, `Services\RequestBaselineTests.cs`, `Services\ServicePlatformDoubles.cs`, `Transit\RequestTestSupport.cs`, `Transit\TransitRequestDiagnosticsTests.cs`; `README.md`; this plan. Existing `ApiCallLog` and its JSON format are untouched. No commits or dependency changes.
- Test seams: production monitor/tracker/tally are compiled as linked sources in the existing MSTest project. Only DispatcherQueue/timers, settings persistence, schedule availability, connectivity, notification output, and keep-awake interop are doubled; actual BusTime HTTP/JSON logic runs with a fake handler. The two services accept an optional `TimeProvider` with system-default behavior, preserving production intervals and local timestamps. Test-only platform doubles use multiple block namespaces in one file to match those external APIs; production files retain file-scoped namespaces.
- New key tests: `GetRequests_AllEndpoints_ObservesTheActualHttpPathAndCountsOncePerEndpoint`, `GetVehiclesAsync_SlowTimerAndResultOverlap_ObservesTwoUnoptimizedPhysicalAttempts`, `GetArrivalsAsync_Success_RecordsCorrelatedTimingAndOneAttempt`, `GetRoutesAsync_Failure_RecordsSafeOutcomeAndCountsOneAttempt`, `GetArrivalsAsync_CanceledInFlight_RecordsCancellationNotEmptySuccess`, `BeginScope_ConcurrentRequestsAndNestedScope_PreservesEachConsumersLabels`, `RecordReuse_ManyObservations_RetainsBoundedSnapshotAndRaisesEveryEvent`, and the baseline scenarios in the table below. Boundaries 0/1/10/11/25 are covered without changing the existing truncation behavior; safe batching remains step 03.

#### Diagnostic entry point and repeat procedure

1. From the repository root, re-detect architecture using the commands in Verification and session handoff. Run `dotnet test .\TrayTimes.Tests\TrayTimes.Tests.csproj -c Debug -p:Platform=$Platform --filter "FullyQualifiedName~RequestBaselineTests|FullyQualifiedName~TransitRequestDiagnosticsTests|FullyQualifiedName~BusTimeTransitServiceTests|FullyQualifiedName~ApiCallLogTests|FullyQualifiedName~TrackedBusTests" --verbosity quiet`. Use the unfiltered command for cross-cutting changes. No API key, Windows identity, wall-clock sleeps, or live HTTP is needed.
2. `TransitRequestDiagnosticsTests` exercise all six physical endpoints, errors (including 429/API/malformed-response/transport/timeout), successful empty results, cancellation, batch boundaries, concurrent scoped labels, and bounded retention. The controlled two-second response yields exactly two seconds elapsed on the completion observation; this proves the clock/measurement seam, not a live latency improvement.
3. `RequestBaselineTests` execute the actual Favorites/tracking/tally services with fake platform boundaries. Warm replay scenarios use one stable vehicle, one Favorites stop, and two requested place stops with one overlapping Favorites stop; successful responses, no metadata/startup/manual traffic, and warm discovery are assumed. One setup prediction is excluded from the 60-second scenario totals but included in saved-tally assertions. The 60-second window starts at coincident due timer ticks and excludes its right endpoint.
4. For app inspection, use `App.Current.Monitor.Service.Diagnostics.Snapshot` in the debugger, or subscribe to the service's `Diagnostics.Observed`. `ApiUsageService.Observe` emits the safe English `Transit request` lines through `Debug.WriteLine` in Debug builds. Retention is 256 observations (starts and completions are separate records), not 256 requests; `Observed` streams every observation even if the bounded snapshot later evicts it.
5. Count only `Phase=Started` with nonzero `RequestId` as physical attempts. Pair completion records by ID for duration/outcome. `CacheReuse`/`InFlightReuse` use ID 0 and do not increment the saved tally. This is client-attempt accounting, not proof of provider billing. Enum-only fields prevent credential/location/exception-message logging.
6. An optional short packaged smoke check must first confirm a valid local directory/feed cache (or use fixture HTTP in tests); never create a cold profile or repeatedly reopen solely to collect rates. Preserve settings and keys, avoid "Update now", and retain only safe request observations rather than raw debugger/exception output. Production time-to-useful-data, full UI event rates, map-selection/alert UX, and failure recovery must be measured as relevant in later optimization steps; they were not claimed here.

#### Reproducible baseline evidence

| Scenario / test | Physical predictions | Physical vehicles | Evidence and bounds |
|-----------------|----------------------|-------------------|---------------------|
| `RefreshAsync_WarmFavoritesTimerAndDisplayEventBursts_CountsTwoPredictionsAndEightVehicles` | 2 | 8 | Tested [0,60) replay: actual monitor loading/result events produce 4 vehicle boundary calls, plus 4 timer calls. No opening/setup traffic included. |
| `GetArrivalsAsync_PlaceBoundaryReplayWithForegroundFavorites_CountsFourPredictionsAndEightVehicles` (Commute and Nearby) | 4 | 8 | Tested [0,60) replay: 2 actual Favorites monitor calls plus 2 place-boundary calls; place result/completion produce 4 vehicle calls, plus 4 timer calls. Full location/map pipeline is code-derived and replayed, not UI-measured. |
| `Start_TrackingTimerWithoutFlyout_ObservesExistingCadence` | 1 / 2 / 6 | 0 | Actual tracker wiring, first minute (0,60], bus >10 / (5,10] / <=5 minutes away; 60/30/10-second intervals unchanged. |
| `Start_ScheduledOnlyOutsideLookahead_MakesNoHttpAttemptUntilLookahead` | 0, then 1 | 0 | Actual tracker: skipped tick before lookahead, one fetch at its threshold. |
| `IsForeground_ReopenTimetableAndHiddenEvents_RecordsActualRefreshWiring` | Fresh reopen 0; stale (>15 s) reopen 1; visible timetable event 1; hidden timer/timetable 0; visible stop change 1 | Not exercised | Actual monitor wiring, one event at a time. Hidden stop changes require the next reopen to fetch. |
| `RefreshAsync_SlowOverlappingTimetableAndReopen_ObservesBothAttemptsWithoutChangingScheduling` | 2 | Not exercised | Actual monitor: overlapping open and timetable event still send independently; generation protection is retained. |
| `GetVehiclesAsync_SlowTimerAndResultOverlap_ObservesTwoUnoptimizedPhysicalAttempts` | 0 | 2 | Actual HTTP path: identical IDs, unresolved timer/result requests overlap; step 02 must remove this duplication. |
| Concurrent route catalog miss, then cached consumer | 0 | 0 | Actual monitor: 1 `getroutes` attempt, 1 in-flight reuse, 1 cache reuse; actual tally remains 1. |
| Favorites timetable/reopen vehicle effects | As above | Visible timetable: 2; warm reopen: 1 entry call, plus 2 if Favorites also refetches | Code-derived from loading/result + mode-entry wiring, conditional on an existing displayed vehicle and warm metadata. Not live measured. |
| Place timetable/reopen effects with Favorites | Visible timetable: 2; fresh reopen: 1 place call; stale reopen: 1 place + 1 Favorites call | 2 per place load | Code-derived from both timetable listeners and place result/completion paths; assumes one location fix, nonempty stops, warm metadata. A >25 m refinement adds another prediction and result vehicle call. |

- Final decision: acceptance criteria pass for observational instrumentation, actual HTTP/tally and relevant linked-consumer wiring, explicitly classified baseline evidence, build, and applicable packaged smoke verification. No claimed API savings yet. Release/per-architecture builds, prolonged live profiling, cold crawls, and comprehensive tracking/map UI interactions were not run and are not step 01 acceptance requirements.
- Blockers/remaining work for 01: none. Next action: implement only step 02's equivalent in-flight vehicle guard and 15-second successful-data reuse, using the baseline recorder and fake-HTTP/clock seams to verify the <=4 routine vehicle attempts per warm half-open minute. Steps 02-10 remain pending; approval gates 08/09 remain untouched.

### 2026-10-06 - Planning

- Created this persistent plan from the source-code audit.
- All implementation steps remain pending; no application behavior changed.
- Request-rate estimates are not production measurements. Step 01 will establish the reproducible baseline.
- Next action: start step 01 with the prompt below.

## Reusable fresh-session prompt

Copy this prompt for each step. It deliberately does not name a step number; the progress file selects or resumes the right one.

```text
In D:\source\tray-times, read docs\transit-api-optimization-plan.md in full.
Treat that file as the persistent source of truth; do not rely on prior chat.

Resume any in_progress or blocked step first. Otherwise implement only the
lowest-numbered pending step. Read AGENTS.md, applicable instruction files,
the current project files, and relevant source before changing code.

Mark the selected step in_progress and update the plan's current checkpoint
before coding. Implement only that step and its necessary supporting changes,
preserving the plan's invariants. Respect the decision gates; ask for approval
when required rather than silently changing UX or tracking semantics.

Add appropriate deterministic tests, verify actual request counts and wiring
where required, build for the detected platform, run the relevant tests, and
verify the packaged app when applicable. Do not burn API budget for testing.
Do not discard unrelated changes or commit unless I ask.

Update the plan as meaningful progress occurs. Before ending, update its
progress table, checkpoint, and execution log with files changed, decisions,
exact verification outcomes, blockers, and the next action. Mark done only
when acceptance criteria pass; otherwise leave an honest resumable status.

Stop after this one step, even if more context remains. Briefly report the
completed step or blocker and the recorded next step.
```
