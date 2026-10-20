using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Services;

/// <summary>
/// Active trip mode: follows a trip the rider started (Go) until they arrive, independent of the flyout.
/// There's no polling cadence: it checks the trip's departures only shortly before each decision
/// (<see cref="TripProgress.NextCheck"/>), raises the trip's alerts as notifications, plans the rest again
/// when a connection is lost, and saves the trip so a restart can offer to resume it. It wakes for each
/// check, each alert, and each minute of the tray countdown (and at least every 15 minutes), and reads the
/// clock afresh every time, so a timer that fires late after sleep catches up on its own. A request that
/// doesn't reach the service is tried once more when the network changes. Starting a trip stops a tracked
/// bus: the tray shows one at a time. Lives on the UI thread and raises <see cref="Changed"/> there.
/// </summary>
internal sealed class ActiveTripService
{
    /// <summary>The most minutes the tray countdown shows; further out, the app icon stays.</summary>
    public const int MaxBadgeMinutes = 99;

    /// <summary>How old the shared departures may be and still stand in for a check (doubled when the call budget is tight).</summary>
    private static readonly TimeSpan FreshEnough = TimeSpan.FromMinutes(1);

    /// <summary>The longest the timer waits, so a long wait still re-reads the clock when a sleep went unreported.</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

    /// <summary>How soon to wake again after an unexpected failure.</summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>The timer's shortest interval, for a wake that's due now.</summary>
    private static readonly TimeSpan SoonestWake = TimeSpan.FromMilliseconds(1);

    private readonly DispatcherQueueTimer _timer;
    private readonly TimeProvider _time;
    private readonly MetadataTransitService _metadata;
    private readonly PredictionCoordinator _predictions;
    private readonly BusTrackingService _tracker;
    private readonly ActiveTripStore _store;
    private readonly TransitBudget? _budget;

    // Per trip; ResetTripState clears them on Go, End, a restore, and Resume.
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _lastReplan;
    private bool _retryWhenNetworkChanges;
    private bool _replanUnreached;
    private bool _toldNoConnection;
    private bool _toldReplanWait;

    private bool _keepingAwake;
    private int _generation;
    private bool _refreshing;

    public ActiveTripService(MetadataTransitService metadata, BusTrackingService tracker, ActiveTripStore store, DispatcherQueue dispatcher,
        TimeProvider? timeProvider = null, PredictionCoordinator? predictions = null, TransitBudget? budget = null)
    {
        _metadata = metadata;
        _tracker = tracker;
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _budget = budget;
        _predictions = predictions ?? new PredictionCoordinator(metadata, _time);
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RefreshAsync(retry: false);
        _tracker.Changed += (_, _) => OnTrackerChanged();
        ConnectivityService.NetworkStatusChanged += (_, _) => dispatcher.TryEnqueue(OnNetworkStatusChanged);
    }

    /// <summary>Raised when a trip starts, ends, or waits to be resumed, and when its progress or countdown changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with each alert the rider is sent and when it came due, after its notification.</summary>
    public event EventHandler<TripUpdate>? Alerted;

    /// <summary>The trip, or null when there's none.</summary>
    public ActiveTrip? Current { get; private set; }

    /// <summary>The progress map's last camera, kept across flyout teardown for this trip only.</summary>
    internal MapView? MapView { get; set; }

    /// <summary>
    /// True while a trip saved before a restart waits for the rider to <see cref="ResumeAsync"/> or <see cref="End"/>
    /// it: no checks, no alerts, and the app icon in the tray. It's forgotten once it would have been over.
    /// </summary>
    public bool IsPendingResume { get; private set; }

    /// <summary>True while a trip is being followed: started, or resumed.</summary>
    public bool IsActive => Current is not null && !IsPendingResume;

    /// <summary>Where the rider is in the active trip now; null without one.</summary>
    public TripStep? Step => IsActive ? Current!.Progress.Step(_time.GetLocalNow(), SettingsService.HeadOutMinutes) : null;

    /// <summary>
    /// The minutes the tray countdown shows for <paramref name="step"/>; null for the app icon: arrived, waiting
    /// for a new plan, or over <see cref="MaxBadgeMinutes"/> away.
    /// </summary>
    public static int? BadgeMinutes(TripStep step, DateTimeOffset now) =>
        step.Kind != TripStepKind.Done && !step.AwaitsReplan && step.MinutesUntil(now) is var minutes && minutes <= MaxBadgeMinutes ? minutes : null;

    /// <summary>Starts following <paramref name="trip"/> (Go), in place of a tracked bus or another trip.</summary>
    /// <param name="destination">Where the trip goes, to plan it again from where the rider is.</param>
    /// <param name="snapshot">The departures the trip was timed from, if any; fresh ones stand in for the first check.</param>
    public void Start(LiveTrip trip, CommuteLeg leg, TripDestination destination, TripTime time, PredictionSnapshot? snapshot)
    {
        _tracker.Stop();
        NotificationService.Clear();
        DateTimeOffset now = _time.GetLocalNow();
        Begin(new ActiveTrip(TripProgress.Start(trip, time, snapshot, SettingsService.HeadOutMinutes, now), leg, destination), pendingResume: false);
        _ = _store.SaveAsync(Current!);
        ScheduleNext(now);
        UpdateKeepAwake(now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rider ends the trip, or doesn't resume a saved one: it's forgotten.</summary>
    public void End()
    {
        if (Current is null)
        {
            return;
        }

        NotificationService.Clear();
        Finish();
    }

    /// <summary>
    /// I missed it: re-times the trip from the next departure at the stop the rider is heading to (or was
    /// assumed to have left from), from the departures already held, so it's free. When none is left, it
    /// plans the rest again from that stop, or, when it planned again under 15 minutes ago, says when it will.
    /// </summary>
    /// <returns>Completes once the trip is re-timed or planned again.</returns>
    public Task MissedAsync()
    {
        if (!IsActive)
        {
            return Task.CompletedTask;
        }

        DateTimeOffset now = _time.GetLocalNow();
        TripProgress before = Current!.Progress;
        TripProgress after = before.Missed(now);
        if (ReferenceEquals(after, before))
        {
            return Task.CompletedTask;
        }

        ++_generation;
        if (!after.NeedsReplan)
        {
            Apply(new TripUpdate(after, TripAlert.ConnectionChanged, now), before, save: true);
            return Task.CompletedTask;
        }

        Current = Current with { Progress = after };
        _ = _store.SaveAsync(Current);
        if (!CanReplan(now, retry: false))
        {
            TellReplanWait();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return RefreshAsync(retry: false);
    }

    /// <summary>Resumes a trip saved before a restart: re-times it by the clock and checks its departures once.</summary>
    /// <returns>Completes once the trip is re-timed.</returns>
    public Task ResumeAsync()
    {
        if (!IsPendingResume)
        {
            return Task.CompletedTask;
        }

        IsPendingResume = false;
        ++_generation;
        ResetTripState();
        NotificationService.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return RefreshAsync(retry: true);
    }

    /// <summary>
    /// Re-evaluates the trip at once, e.g. when the PC wakes from sleep: alerts that came due meanwhile
    /// collapse into the latest, and an overdue check runs.
    /// </summary>
    public Task WakeAsync() => RefreshAsync(retry: false);

    /// <summary>
    /// At launch: a saved trip that's over is deleted; one that isn't waits for the rider (<see cref="IsPendingResume"/>).
    /// One that can't be read or followed is deleted too, so it can't fail at every launch.
    /// </summary>
    /// <param name="offerResume">True for a launch with no window (Windows startup): ask in a notification.</param>
    public async Task RestoreAsync(bool offerResume)
    {
        ActiveTrip? saved = await _store.LoadAsync();
        if (saved is null || Current is not null)
        {
            return;
        }

        try
        {
            DateTimeOffset now = _time.GetLocalNow();
            if (saved.Progress.IsOver(now))
            {
                _ = _store.DeleteAsync();
                return;
            }

            Begin(saved, pendingResume: true);
            ScheduleNext(now);
        }
        catch (Exception ex)
        {
            // A whole-looking file whose times still don't add up: forget it rather than leave the trip half restored.
            System.Diagnostics.Debug.WriteLine($"Saved trip not restored: {ex.GetType().Name}.");
            ++_generation;
            _timer.Stop();
            Current = null;
            IsPendingResume = false;
            _ = _store.DeleteAsync();
            return;
        }

        if (offerResume)
        {
            (string title, string text) = TripMessages.ResumePrompt(saved);
            NotificationService.Show(title, text, [NotificationAction.ResumeTrip, NotificationAction.EndTrip]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a changed "keep awake" setting to the active trip; without one, tracking's state is left alone.</summary>
    public void ApplyKeepAwakeSetting()
    {
        if (IsActive)
        {
            UpdateKeepAwake(_time.GetLocalNow(), force: true);
        }
    }

    /// <summary>Completes once the trip's saved state is written, e.g. before the app exits.</summary>
    public Task FlushAsync() => _store.FlushAsync();

    private void Begin(ActiveTrip trip, bool pendingResume)
    {
        ++_generation;
        Current = trip;
        MapView = null;
        IsPendingResume = pendingResume;
        ResetTripState();
    }

    private void ResetTripState()
    {
        _lastAttempt = null;
        _lastReplan = null;
        _retryWhenNetworkChanges = false;
        _replanUnreached = false;
        _toldNoConnection = false;
        _toldReplanWait = false;
    }

    /// <summary>One wake. An unexpected failure keeps the trip as it was and wakes again soon, rather than stopping it.</summary>
    /// <param name="retry">Check now if any decision is still ahead, not only when one is due, and plan again now if the last try didn't reach the service (resuming, the network changed).</param>
    private async Task RefreshAsync(bool retry)
    {
        if (Current is null || _refreshing)
        {
            return;
        }

        try
        {
            await AdvanceAsync(retry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Trip refresh failed: {ex.GetType().Name}. Trying again shortly.");
            WakeAgainAfterFailure();
        }
    }

    /// <summary>Ends an over trip; otherwise checks if a check is due, advances, and plans again if needed.</summary>
    private async Task AdvanceAsync(bool retry)
    {
        ActiveTrip trip = Current!;
        DateTimeOffset now = _time.GetLocalNow();
        if (trip.Progress.IsOver(now))
        {
            Finish();
            return;
        }

        if (IsPendingResume)
        {
            ScheduleNext(now);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        int generation = _generation;
        TripProgress before = trip.Progress;
        TripUpdate update;
        _refreshing = true;
        try
        {
            PredictionSnapshot? snapshot = IsCheckDue(before, now, retry) ? await CheckAsync(before, now) : null;
            now = _time.GetLocalNow();
            update = before.Advance(snapshot, SettingsService.HeadOutMinutes, now);
            if (generation == _generation && update.Progress.NeedsReplan && CanReplan(now, retry || snapshot is not null))
            {
                update = await ReplanAsync(trip with { Progress = update.Progress }, update, now, generation);
            }
        }
        finally
        {
            _refreshing = false;
        }

        if (generation != _generation)
        {
            // The rider acted meanwhile (I missed it, End, Go); that state stands, and this wake's work is dropped.
            if (Current is not null)
            {
                ScheduleNext(_time.GetLocalNow());
            }

            return;
        }

        Apply(update, before);
    }

    private bool IsCheckDue(TripProgress progress, DateTimeOffset now, bool retry) => retry
        ? progress.NextCheck(SettingsService.HeadOutMinutes, now) is not null
        : progress.NextCheck(SettingsService.HeadOutMinutes, now, _lastAttempt) <= now;

    /// <summary>
    /// One check of the stops that can still change the trip, sharing the departures batch. None once the
    /// month's calls are used up. Windows' connection status is only a hint, so it doesn't stop a check: one
    /// that doesn't reach the service is tried once more when the network changes.
    /// </summary>
    private async Task<PredictionSnapshot?> CheckAsync(TripProgress progress, DateTimeOffset now)
    {
        _lastAttempt = now;
        IReadOnlyList<string> stops = progress.WatchedStops(now);
        if (stops.Count == 0 || _budget?.Level == TransitBudgetLevel.Exhausted)
        {
            return null; // The budget gate would refuse it anyway; the trip runs on its last times.
        }

        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.ActiveTrip, TransitTrigger.Timer);
        try
        {
            PredictionSnapshot snapshot = await _predictions.GetAsync(stops, FreshEnough * (_budget?.TrackingSlowdown ?? 1), combineBrowsing: true);
            _retryWhenNetworkChanges = false;
            return snapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            System.Diagnostics.Debug.WriteLine($"Trip check failed: {ex.GetType().Name}. Keeping the last times.");
            _retryWhenNetworkChanges |= IsUnreached(ex);
            return null; // Keep counting down from the last times; the next decision's check tries again.
        }
    }

    /// <summary>
    /// True when a re-plan may run now: none yet, the last was <see cref="ReplanGuard.Interval"/> ago, or (when
    /// <paramref name="retry"/>) the last didn't reach the service.
    /// </summary>
    private bool CanReplan(DateTimeOffset now, bool retry) =>
        _lastReplan is not { } last || now - last >= ReplanGuard.Interval || (retry && _replanUnreached);

    /// <summary>
    /// Plans the rest of the trip again from where the rider is and continues on it, telling the rider the new
    /// connection. When no trip can be made, says so once. Nothing is said once the rider has acted meanwhile.
    /// </summary>
    /// <returns>The continued trip and its alert; else <paramref name="update"/> unchanged.</returns>
    private async Task<TripUpdate> ReplanAsync(ActiveTrip trip, TripUpdate update, DateTimeOffset now, int generation)
    {
        _lastReplan = now;
        TripPlan plan;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.ActiveTrip, TransitTrigger.Timer))
        {
            try
            {
                plan = await _metadata.ReplanTripAsync(trip.Progress.ReplanFrom(now) ?? trip.Leg.Origin, trip.Destination);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
            {
                System.Diagnostics.Debug.WriteLine($"Trip re-plan failed: {ex.GetType().Name}. Keeping the last times.");
                if (generation == _generation)
                {
                    _replanUnreached = IsUnreached(ex);
                    _retryWhenNetworkChanges |= _replanUnreached;
                }

                return update;
            }
        }

        if (generation != _generation)
        {
            return update;
        }

        _replanUnreached = false;
        _retryWhenNetworkChanges = false;
        DateTimeOffset at = _time.GetLocalNow();
        if (trip.Progress.Continue(plan, at) is { } continued)
        {
            _toldNoConnection = false;
            _toldReplanWait = false;
            return new TripUpdate(continued, continued.HeadOutSent || continued.LeaveNowSent ? TripAlert.ConnectionChanged : null, at);
        }

        if (!_toldNoConnection)
        {
            _toldNoConnection = true;
            (string title, string text) = TripMessages.NoConnection(trip);
            NotificationService.Show(title, text, [NotificationAction.EndTrip]);
        }

        return update;
    }

    /// <summary>A request that never got an answer (no connection, a timeout) rather than one the service refused.</summary>
    private static bool IsUnreached(Exception ex) =>
        ex is TaskCanceledException || (ex is HttpRequestException { StatusCode: null } and not TransitCooldownException);

    /// <summary>I missed it needs a new plan that the 15-minute guard holds back: says when it will plan again, once.</summary>
    private void TellReplanWait()
    {
        if (_toldReplanWait || _lastReplan is not { } last)
        {
            return;
        }

        _toldReplanWait = true;
        (string title, string text) = TripMessages.ReplanWait(Current!, last + ReplanGuard.Interval);
        NotificationService.Show(title, text, [NotificationAction.EndTrip]);
    }

    /// <summary>Takes on the trip's next state: sends its alert, saves it when it changed, and schedules the next wake.</summary>
    /// <param name="save">True to save even when nothing saved seems to have changed.</param>
    private void Apply(TripUpdate update, TripProgress before, bool save = false)
    {
        DateTimeOffset now = _time.GetLocalNow();
        Current = Current! with { Progress = update.Progress };
        TripStep step = update.Progress.Step(now, SettingsService.HeadOutMinutes);
        if (update.Alert is { } alert)
        {
            Notify(alert, step, before, now);
            Alerted?.Invoke(this, update);
        }

        if (save || !IsSameState(before, update.Progress))
        {
            _ = _store.SaveAsync(Current);
        }

        ScheduleNext(now);
        UpdateKeepAwake(now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when nothing that's saved differs: only the clock moved.</summary>
    private static bool IsSameState(TripProgress a, TripProgress b) =>
        ReferenceEquals(a.Itinerary, b.Itinerary) && a.Rides.SequenceEqual(b.Rides) && a.LeftAt == b.LeftAt && a.KnownAt == b.KnownAt
        && a.MissedTrips.Count == b.MissedTrips.Count && a.StrandedRide == b.StrandedRide && a.HeadOutSent == b.HeadOutSent
        && a.LeaveNowSent == b.LeaveNowSent && a.GetOffAlerts == b.GetOffAlerts && a.AnnouncedRide == b.AnnouncedRide
        && a.AnnouncedDeparture == b.AnnouncedDeparture;

    /// <summary>The alert, with I missed it while the rider is heading to a ride, and End trip on every one.</summary>
    private void Notify(TripAlert alert, TripStep step, TripProgress before, DateTimeOffset now)
    {
        (string title, string text) = TripMessages.Alert(Current!, alert, before, now);
        bool canMiss = alert != TripAlert.GetOffSoon && !step.AwaitsReplan && step.Kind is TripStepKind.ToStop or TripStepKind.Transfer;
        NotificationService.Show(title, text, canMiss ? [NotificationAction.MissedIt, NotificationAction.EndTrip] : [NotificationAction.EndTrip]);
    }

    /// <summary>
    /// Wakes for the earliest of: the next check, the next alert, the next change of the tray countdown, a
    /// re-plan that's allowed again, and the trip's end, but never more than <see cref="LongestWait"/> away.
    /// A trip waiting to resume wakes only to see whether it's over.
    /// </summary>
    private void ScheduleNext(DateTimeOffset now)
    {
        _timer.Stop();
        if (Current?.Progress is not { } progress)
        {
            return;
        }

        DateTimeOffset next = progress.Trip.Arrives + TripProgress.EndGrace;
        if (!IsPendingResume)
        {
            int lead = SettingsService.HeadOutMinutes;
            next = Earliest(next, progress.NextCheck(lead, now, _lastAttempt));
            next = Earliest(next, progress.NextAlertAt(lead));
            next = Earliest(next, NextBadgeChange(progress.Step(now, lead), now));
            if (progress.NeedsReplan)
            {
                next = Earliest(next, CanReplan(now, retry: false) ? now : _lastReplan + ReplanGuard.Interval);
            }
        }

        TimeSpan wait = next - now;
        _timer.Interval = wait < SoonestWake ? SoonestWake : wait > LongestWait ? LongestWait : wait;
        _timer.Start();
    }

    private void WakeAgainAfterFailure()
    {
        _timer.Stop();
        if (Current is null)
        {
            return;
        }

        _timer.Interval = AfterFailure;
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>When the countdown to the step's action next changes: at each whole minute before it, or when it comes within <see cref="MaxBadgeMinutes"/>.</summary>
    private static DateTimeOffset? NextBadgeChange(TripStep step, DateTimeOffset now)
    {
        TimeSpan left = step.ActionAt - now;
        if (step.Kind == TripStepKind.Done || step.AwaitsReplan || left <= TimeSpan.Zero)
        {
            return null;
        }

        if (left > TimeSpan.FromMinutes(MaxBadgeMinutes))
        {
            return step.ActionAt - TimeSpan.FromMinutes(MaxBadgeMinutes);
        }

        long intoMinute = left.Ticks % TimeSpan.TicksPerMinute;
        return now + TimeSpan.FromTicks(intoMinute > 0 ? intoMinute : TimeSpan.TicksPerMinute);
    }

    private static DateTimeOffset Earliest(DateTimeOffset at, DateTimeOffset? other) => other < at ? other.Value : at;

    private void Finish()
    {
        ++_generation;
        _timer.Stop();
        Current = null;
        MapView = null;
        IsPendingResume = false;
        ResetTripState();
        _ = _store.DeleteAsync();
        UpdateKeepAwake(_time.GetLocalNow());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The network changed: one more try for a check or re-plan that didn't reach the service.</summary>
    private void OnNetworkStatusChanged()
    {
        if (IsActive && _retryWhenNetworkChanges)
        {
            _ = RefreshAsync(retry: true);
        }
    }

    /// <summary>Tracking shares the PC's keep-awake state; when it stops, it lets the PC sleep, so the trip's state is set again.</summary>
    private void OnTrackerChanged()
    {
        if (IsActive && _keepingAwake && !_tracker.IsTracking)
        {
            UpdateKeepAwake(_time.GetLocalNow(), force: true);
        }
    }

    /// <summary>
    /// Applies the "keep awake" setting while the trip is under way or within 99 minutes, so a trip set for
    /// tomorrow doesn't keep the PC up overnight. Sleep from closing the lid can't be stopped; <see cref="WakeAsync"/>
    /// covers that. Letting go hands the state back to tracking, which shares it.
    /// </summary>
    /// <param name="force">Set the state even when it seems unchanged.</param>
    private void UpdateKeepAwake(DateTimeOffset now, bool force = false)
    {
        bool keepAwake = SettingsService.KeepAwakeWhileTracking && IsActive
            && Current!.Progress.Step(now, SettingsService.HeadOutMinutes) is { } step && (step.AwaitsReplan || BadgeMinutes(step, now) is not null);
        if (keepAwake == _keepingAwake && !force)
        {
            return;
        }

        _keepingAwake = keepAwake;
        PInvoke.SetThreadExecutionState(keepAwake
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS);
        if (!keepAwake && _tracker.IsTracking)
        {
            _tracker.ApplyKeepAwakeSetting();
        }
    }
}
