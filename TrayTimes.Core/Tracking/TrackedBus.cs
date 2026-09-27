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
    string? VehicleId,
    DateTimeOffset PredictedTime,
    DateTimeOffset? DueSince,
    DateTimeOffset? AlertedTime)
{
    /// <summary>How long after the bus is due tracking keeps going.</summary>
    public static readonly TimeSpan PassedGrace = TimeSpan.FromMinutes(5);

    /// <summary>How long past its last known time a bus that never showed as due is kept.</summary>
    public static readonly TimeSpan LostGrace = TimeSpan.FromMinutes(10);

    /// <summary>How far a prediction must move after the head-out alert to alert again.</summary>
    public static readonly TimeSpan ShiftThreshold = TimeSpan.FromMinutes(3);

    /// <summary>How far apart predictions can be and still be the same trip, when there's no vehicle ID.</summary>
    public static readonly TimeSpan MatchTolerance = TimeSpan.FromMinutes(10);

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
            arrival.VehicleId,
            arrival.PredictedTime,
            DueSince: minutes == 0 ? now : null,
            // Already inside the lead window: the rider just picked it, so skip the head-out alert
            // but still alert if the time moves.
            AlertedTime: minutes <= leadMinutes ? arrival.PredictedTime : null);
    }

    /// <summary>Whole minutes until the bus arrives, pinned to zero once it has been due.</summary>
    public int MinutesAway(DateTimeOffset now) =>
        DueSince is not null ? 0 : Math.Max(0, (int)Math.Floor((PredictedTime - now).TotalMinutes));

    /// <summary>Polls less often while the bus is far away.</summary>
    public TimeSpan RefreshInterval(DateTimeOffset now)
    {
        double minutes = (PredictedTime - now).TotalMinutes;
        return minutes > 10 ? TimeSpan.FromMinutes(1)
            : minutes > 5 ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// This bus among a stop's arrivals: by vehicle when known, else the nearest prediction for the
    /// same route and direction. Scheduled-only arrivals never match; there's no bus to follow.
    /// </summary>
    public Arrival? FindIn(IEnumerable<Arrival> arrivals)
    {
        IEnumerable<Arrival> candidates = arrivals.Where(a =>
            a.IsRealTime
            && a.StopId == StopId
            && string.Equals(a.RouteId, RouteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.DirectionId, DirectionId, StringComparison.OrdinalIgnoreCase));

        candidates = VehicleId is not null
            ? candidates.Where(a => a.VehicleId == VehicleId)
            : candidates.Where(a => Gap(a) <= MatchTolerance);
        return candidates.MinBy(Gap);
    }

    /// <param name="arrivals">The stop's latest arrivals, or null if the poll failed.</param>
    public TrackingStep Advance(IReadOnlyList<Arrival>? arrivals, int leadMinutes, DateTimeOffset now)
    {
        TrackedBus bus = this;
        if (arrivals is not null && FindIn(arrivals) is { } match)
        {
            if (match.Status == ArrivalStatus.Canceled)
            {
                return new TrackingStep(this, TrackingAlert.Canceled, Ended: true);
            }

            bus = this with
            {
                VehicleId = match.VehicleId ?? VehicleId,
                Destination = match.Destination,
                PredictedTime = match.PredictedTime,
                DueSince = DueSince ?? (match.MinutesAway(now) == 0 ? now : null),
            };
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

        bool ended = bus.DueSince is { } dueSince
            ? now >= dueSince + PassedGrace
            : now >= bus.PredictedTime + LostGrace;
        return new TrackingStep(bus, alert, ended);
    }

    private TimeSpan Gap(Arrival arrival) => (arrival.PredictedTime - PredictedTime).Duration();
}
