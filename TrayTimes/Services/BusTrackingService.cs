using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Services;

/// <summary>
/// Tracking mode: follows the one ride the rider rang the bell on, independent of the flyout.
/// Polls that stop (more often as the ride gets close, and less often when the call budget is
/// tight; a scheduled-only trip isn't looked for until 15 minutes before its time), sends head-out
/// and time-change alerts, optionally keeps the PC awake, and stops on its own once the ride has
/// passed. Its requests carry any due browsing stops along. Lives on the UI thread and raises
/// <see cref="Changed"/> there.
/// </summary>
internal sealed class BusTrackingService
{
    private readonly DispatcherQueueTimer _timer;
    private readonly TimeProvider _time;
    private readonly PredictionCoordinator _predictions;
    private readonly TransitBudget? _budget;
    private int _generation;
    private bool _refreshing;

    public BusTrackingService(ITransitService service, DispatcherQueue dispatcher, TimeProvider? timeProvider = null, PredictionCoordinator? predictions = null, TransitBudget? budget = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _budget = budget;
        _predictions = predictions ?? new PredictionCoordinator(service, _time);
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Raised when tracking starts, stops, or the tracked bus's prediction updates.</summary>
    public event EventHandler? Changed;

    /// <summary>The bus being tracked, or null in browsing mode.</summary>
    public TrackedBus? Current { get; private set; }

    public bool IsTracking => Current is not null;

    /// <summary>True once the tracked bus is within the rider's head-out time.</summary>
    public bool IsTimeToGo => Current is { } bus && bus.MinutesAway(_time.GetLocalNow()) <= SettingsService.HeadOutMinutes;

    /// <summary>Starts tracking <paramref name="arrival"/>, replacing any bus already tracked.</summary>
    public void Start(Arrival arrival, string stopName)
    {
        NotificationService.Clear();
        DateTimeOffset now = _time.GetLocalNow();
        Current = TrackedBus.Start(arrival, stopName, SettingsService.HeadOutMinutes, now);
        ++_generation;
        SetKeepAwake(SettingsService.KeepAwakeWhileTracking);
        ScheduleNext(now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (Current is null)
        {
            return;
        }

        NotificationService.Clear();
        End();
    }

    /// <summary>Applies a changed "keep awake" setting to the current trip.</summary>
    public void ApplyKeepAwakeSetting() => SetKeepAwake(IsTracking && SettingsService.KeepAwakeWhileTracking);

    private async Task RefreshAsync()
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Tracking, TransitTrigger.Timer);
        if (Current is not { } bus || _refreshing)
        {
            return;
        }

        int generation = _generation;
        _refreshing = true;
        IReadOnlyList<Arrival>? arrivals;
        DateTimeOffset? predictionFetchedAt = null;
        try
        {
            // A scheduled ride has no real-time data until shortly before it's due, so don't ask yet;
            // and once its grace is over no answer could change anything, so don't spend a call.
            if (bus.IsAwaitingRealTime(SettingsService.HeadOutMinutes, _time.GetLocalNow()) || bus.IsOver(_time.GetLocalNow()))
            {
                arrivals = null;
            }
            else
            {
                TimeSpan maxAge = Interval(bus, _time.GetLocalNow());
                PredictionSnapshot snapshot = await _predictions.GetAsync(new[] { bus.StopId }, maxAge, combineBrowsing: true);
                TimeSpan currentInterval = Interval(bus, _time.GetLocalNow());
                if (currentInterval < maxAge && generation == _generation)
                {
                    snapshot = await _predictions.GetAsync(new[] { bus.StopId }, currentInterval, combineBrowsing: true);
                }

                arrivals = snapshot.Arrivals;
                predictionFetchedAt = snapshot.FetchedAt;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            arrivals = null; // Keep counting down from the last prediction; the next poll retries.
        }
        finally
        {
            _refreshing = false;
        }

        if (generation != _generation || Current is null)
        {
            if (Current is not null)
            {
                ScheduleNext(_time.GetLocalNow());
            }

            return;
        }

        DateTimeOffset now = _time.GetLocalNow();
        TrackingStep step = Current.Advance(arrivals, SettingsService.HeadOutMinutes, now, predictionFetchedAt);
        Current = step.Bus;
        if (step.Alert is { } alert)
        {
            Notify(step.Bus, alert, now);
        }

        if (step.Ended)
        {
            End();
            return;
        }

        ScheduleNext(now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleNext(DateTimeOffset now)
    {
        _timer.Stop();
        TrackedBus bus = Current!;
        TimeSpan interval = Interval(bus, now);
        if (bus.DueSince is { } dueSince)
        {
            TimeSpan remaining = dueSince + TrackedBus.PassedGrace - now;
            if (remaining > TimeSpan.Zero && remaining < interval)
            {
                interval = remaining;
            }
        }

        _timer.Interval = interval;
        _timer.Start();
    }

    /// <summary>The trip's polling schedule, slowed when the call budget is tight.</summary>
    private TimeSpan Interval(TrackedBus bus, DateTimeOffset now) => bus.RefreshInterval(now) * (_budget?.TrackingSlowdown ?? 1);

    private void End()
    {
        ++_generation;
        _timer.Stop();
        Current = null;
        SetKeepAwake(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Notify(TrackedBus bus, TrackingAlert alert, DateTimeOffset now)
    {
        string route = $"Route {bus.RouteDisplayId}{(string.IsNullOrEmpty(bus.Destination) ? string.Empty : $" to {bus.Destination}")}";
        string clock = bus.PredictedTime.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        int minutes = bus.MinutesAway(now);
        string inMinutes = minutes == 0 ? "now" : minutes == 1 ? "in 1 min" : $"in {minutes} min";

        (string title, string text) = alert switch
        {
            TrackingAlert.HeadOut => ("Time to head out", $"{route} leaves {bus.StopName} {inMinutes} ({clock})."),
            TrackingAlert.Later => ("Your ride is running later", $"{route} now leaves {bus.StopName} at {clock} ({inMinutes})."),
            TrackingAlert.Earlier => ("Your ride is coming sooner", $"{route} now leaves {bus.StopName} at {clock} ({inMinutes})."),
            _ => ("Your ride was canceled", $"{route} at {bus.StopName} was canceled. Tracking has stopped."),
        };
        NotificationService.Show(title, text, alert == TrackingAlert.Canceled ? Array.Empty<NotificationAction>() : new[] { NotificationAction.StopTracking });
    }

    private static void SetKeepAwake(bool keepAwake) =>
        PInvoke.SetThreadExecutionState(keepAwake
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS);
}
