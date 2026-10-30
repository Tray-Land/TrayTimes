using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tracking;

/// <summary>Finds one trip among a stop's departures, for tracking a bus or following a trip.</summary>
public static class TripMatch
{
    /// <summary>How far apart predictions can be and still be the same trip, when there's no trip ID.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The trip among <paramref name="arrivals"/> at <paramref name="line"/>: by trip ID when known (real-time, or
    /// the timetable's, which still matches once the trip gains a real-time one), else the real-time departure
    /// nearest <paramref name="expected"/>.
    /// </summary>
    public static Arrival? Find(IEnumerable<Arrival> arrivals, StopLine line, string? tripId, DateTimeOffset expected)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        IEnumerable<Arrival> candidates = arrivals.Where(a => StopLine.Of(a) == line);
        candidates = tripId is not null
            ? candidates.Where(a => a.IsTrip(tripId))
            : candidates.Where(a => a.IsRealTime && Gap(a) <= Tolerance);
        return candidates.MinBy(Gap);

        TimeSpan Gap(Arrival arrival) => (arrival.PredictedTime - expected).Duration();
    }
}
