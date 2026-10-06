using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Services;

/// <summary>
/// Tracking mode: follows the one bus the rider rang the bell on, independent of the flyout.
/// Polls just that stop (faster as the bus gets close; a scheduled-only bus is looked for in real-time data every minute once it's within 15 minutes of its time), sends head-out and time-change alerts,
/// optionally keeps the PC awake, and stops on its own once the bus has passed. Lives on the UI
/// thread and raises <see cref="Changed"/> there.
/// </summary>
internal sealed class BusTrackingService
{
    private readonly ITransitService _service;
    private readonly DispatcherQueueTimer _timer;
    private int _generation;

    public BusTrackingService(ITransitService service, DispatcherQueue dispatcher)
    {
        _service = service;
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
    public bool IsTimeToGo => Current is { } bus && bus.MinutesAway(DateTimeOffset.Now) <= SettingsService.HeadOutMinutes;

    /// <summary>Starts tracking <paramref name="arrival"/>, replacing any bus already tracked.</summary>
    public void Start(Arrival arrival, string stopName)
    {
        NotificationService.Clear();
        DateTimeOffset now = DateTimeOffset.Now;
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
        if (Current is not { } bus)
        {
            return;
        }

        int generation = _generation;
        IReadOnlyList<Arrival>? arrivals;
        try
        {
            // A scheduled bus has no real-time data until shortly before it's due, so don't ask yet.
            arrivals = bus.IsAwaitingRealTime(SettingsService.HeadOutMinutes, DateTimeOffset.Now)
                ? null
                : await _service.GetArrivalsAsync([bus.StopId]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            arrivals = null; // Keep counting down from the last prediction; the next poll retries.
        }

        if (generation != _generation || Current is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TrackingStep step = Current.Advance(arrivals, SettingsService.HeadOutMinutes, now);
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
        _timer.Interval = Current!.RefreshInterval(now);
        _timer.Start();
    }

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
            TrackingAlert.HeadOut => ("Time to head out", $"{route} reaches {bus.StopName} {inMinutes} ({clock})."),
            TrackingAlert.Later => ("Your bus is running later", $"{route} now reaches {bus.StopName} at {clock} ({inMinutes})."),
            TrackingAlert.Earlier => ("Your bus is coming sooner", $"{route} now reaches {bus.StopName} at {clock} ({inMinutes})."),
            _ => ("Your bus was canceled", $"{route} at {bus.StopName} was canceled. Tracking has stopped."),
        };
        NotificationService.Show(title, text, canStopTracking: alert != TrackingAlert.Canceled);
    }

    private static void SetKeepAwake(bool keepAwake) =>
        PInvoke.SetThreadExecutionState(keepAwake
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS);
}
