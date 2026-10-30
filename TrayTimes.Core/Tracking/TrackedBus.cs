using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tracking;

/// <summary>Why the rider should hear about their tracked bus.</summary>
public enum TrackingAlert
{
    /// <summary>The bus is within the rider's lead time: leave now.</summary>
    HeadOut,

    /// <summary>After the head-out alert, the prediction slipped later.</summary>
    Later,

    /// <summary>After the head-out alert, the prediction moved earlier.</summary>
    Earlier,

    /// <summary>The trip was canceled; tracking ends.</summary>
    Canceled,
}

/// <summary>The result of feeding one arrivals poll to a <see cref="TrackedBus"/>.</summary>
public sealed record TrackingStep(TrackedBus Bus, TrackingAlert? Alert, bool Ended);

/// <summary>
/// The one bus the rider is watching at one stop. Immutable: each poll produces the next state via
/// <see cref="Advance"/>, along with any alert to raise and whether tracking is over.
/// </summary>
public sealed record TrackedBus(
    string StopId,
    string StopName,
    string RouteId,
    string RouteDisplayId,
    string DirectionId,
    string Destination,
    string? TripId,
    DateTimeOffset PredictedTime,
    DateTimeOffset? DueSince,
    DateTimeOffset? AlertedTime)
{
    private const int PassedConfirmationCount = 2;

    /// <summary>How long after the bus is due tracking keeps going.</summary>
    public static readonly TimeSpan PassedGrace = TimeSpan.FromMinutes(5);

    /// <summary>How long past its last known time a bus that never showed as due is kept.</summary>
    public static readonly TimeSpan LostGrace = TimeSpan.FromMinutes(10);

    /// <summary>How far a prediction must move after the head-out alert to alert again.</summary>
    public static readonly TimeSpan ShiftThreshold = TimeSpan.FromMinutes(3);

    /// <summary>How long before a scheduled time the rider's bus is first looked for in real-time data.</summary>
    public static readonly TimeSpan RealTimeLookahead = TimeSpan.FromMinutes(15);

    /// <summary>True while only the timetable knows this bus: no real-time prediction has matched it yet.</summary>
    public bool IsScheduled { get; init; }

    /// <summary>True after two distinct successful polls omit a due bus; cleared if it reappears.</summary>
    public bool HasPassed { get; init; }

    private int MissingPredictionCount { get; init; }

    private DateTimeOffset? LastPredictionFetchedAt { get; init; }

    public static TrackedBus Start(Arrival arrival, string stopName, int leadMinutes, DateTimeOffset now)
    {
        int minutes = arrival.MinutesAway(now);
        return new TrackedBus(
            arrival.StopId,
            stopName,
            arrival.RouteId,
            arrival.RouteDisplayId,
            arrival.DirectionId,
            arrival.Destination,
            arrival.TripId,
            arrival.PredictedTime,
            // A scheduled time can't say the bus is due; only a real-time prediction can.
            DueSince: minutes == 0 && arrival.IsRealTime ? now : null,
            // Already inside the lead window: the rider just picked it, so skip the head-out alert
            // but still alert if the time moves.
            AlertedTime: minutes <= leadMinutes ? arrival.PredictedTime : null)
        {
            IsScheduled = !arrival.IsRealTime,
        };
    }

    /// <summary>
    /// True once there's nothing left to watch for: the grace after the ride was due has run out,
    /// or (never seen due) it's long past its last known time. No poll can change that.
    /// </summary>
    public bool IsOver(DateTimeOffset now) =>
        DueSince is { } dueSince ? now >= dueSince + PassedGrace : now >= PredictedTime + LostGrace;

    /// <summary>Whole minutes until the bus arrives, pinned to zero once it has been due.</summary>
    public int MinutesAway(DateTimeOffset now) =>
        DueSince is not null ? 0 : Math.Max(0, (int)Math.Floor((PredictedTime - now).TotalMinutes));

    /// <summary>
    /// True while a scheduled bus is too far off to look for in real-time data: it's searched for from
    /// <see cref="RealTimeLookahead"/> (or the rider's lead time, if longer) before its scheduled time.
    /// </summary>
    public bool IsAwaitingRealTime(int leadMinutes, DateTimeOffset now) =>
        IsScheduled && now < PredictedTime - TimeSpan.FromMinutes(Math.Max(RealTimeLookahead.TotalMinutes, leadMinutes));

    /// <summary>
    /// Polls as rarely as the call budget needs while still catching a late change: every 3 minutes
    /// far away or while only the timetable knows the trip, then every 90 and 60 seconds, and every
    /// 90 seconds once passage is confirmed.
    /// </summary>
    public TimeSpan RefreshInterval(DateTimeOffset now)
    {
        if (HasPassed)
        {
            return TimeSpan.FromSeconds(90);
        }

        if (IsScheduled)
        {
            return TimeSpan.FromMinutes(3);
        }

        double minutes = (PredictedTime - now).TotalMinutes;
        return minutes > 10 ? TimeSpan.FromMinutes(3)
            : minutes > 5 ? TimeSpan.FromSeconds(90)
            : TimeSpan.FromSeconds(60);
    }

    /// <summary>This trip among a stop's departures (see <see cref="TripMatch.Find"/>).</summary>
    public Arrival? FindIn(IEnumerable<Arrival> arrivals) =>
        TripMatch.Find(arrivals, new StopLine(StopId, RouteId, DirectionId), TripId, PredictedTime);

    /// <summary>
    /// The row to light up in a list that may include timetable rows: this bus's real-time match, else,
    /// while it's still scheduled-only, the timetable row it was picked from.
    /// </summary>
    public Arrival? FindListed(IEnumerable<Arrival> arrivals) =>
        FindIn(arrivals) ?? (IsScheduled
            ? arrivals.FirstOrDefault(a =>
                !a.IsRealTime
                && a.StopId == StopId
                && a.PredictedTime == PredictedTime
                && string.Equals(a.RouteId, RouteId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.DirectionId, DirectionId, StringComparison.OrdinalIgnoreCase))
            : null);

    /// <param name="arrivals">The stop's latest arrivals, or null if the poll failed.</param>
    /// <param name="predictionFetchedAt">Original snapshot time, so reused predictions cannot confirm passage twice.</param>
    public TrackingStep Advance(IReadOnlyList<Arrival>? arrivals, int leadMinutes, DateTimeOffset now, DateTimeOffset? predictionFetchedAt = null)
    {
        TrackedBus bus = this;
        DateTimeOffset fetchedAt = predictionFetchedAt ?? now;
        if (arrivals is null)
        {
            bus = this with { MissingPredictionCount = 0 };
        }
        else if (LastPredictionFetchedAt is null || fetchedAt >= LastPredictionFetchedAt)
        {
            if (FindIn(arrivals) is { } match)
            {
                if (match.Status == ArrivalStatus.Canceled)
                {
                    return new TrackingStep(this, TrackingAlert.Canceled, Ended: true);
                }

                bus = this with
                {
                    IsScheduled = !match.IsRealTime,
                    HasPassed = false,
                    MissingPredictionCount = 0,
                    LastPredictionFetchedAt = fetchedAt,
                    TripId = match.TripId ?? TripId,
                    Destination = match.Destination,
                    PredictedTime = match.PredictedTime,
                    // Only a real-time departure can say the ride is due; a timetable time can't know it's late.
                    DueSince = DueSince ?? (match.IsRealTime && match.MinutesAway(now) == 0 ? now : null),
                };
            }
            else if (fetchedAt != LastPredictionFetchedAt)
            {
                int misses = DueSince is null ? 0 : Math.Min(PassedConfirmationCount, MissingPredictionCount + 1);
                bus = this with
                {
                    MissingPredictionCount = misses,
                    LastPredictionFetchedAt = fetchedAt,
                    HasPassed = HasPassed || misses == PassedConfirmationCount,
                };
            }
        }

        TrackingAlert? alert = null;
        if (bus.DueSince is null)
        {
            if (bus.AlertedTime is null && bus.MinutesAway(now) <= leadMinutes)
            {
                alert = TrackingAlert.HeadOut;
            }
            else if (bus.AlertedTime is { } alerted && (bus.PredictedTime - alerted).Duration() >= ShiftThreshold)
            {
                alert = bus.PredictedTime > alerted ? TrackingAlert.Later : TrackingAlert.Earlier;
            }

            if (alert is not null)
            {
                bus = bus with { AlertedTime = bus.PredictedTime };
            }
        }

        return new TrackingStep(bus, alert, bus.IsOver(now));
    }
}
