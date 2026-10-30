using System.Text.Json.Serialization;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>
/// A trip the rider started: its progress, the leg it was planned for (for the place names), and where it
/// goes (to plan it again). This is what's saved so a restart can offer to resume it.
/// </summary>
public sealed record ActiveTrip(TripProgress Progress, CommuteLeg Leg, TripDestination Destination)
{
    /// <summary>How far from <see cref="TripProgress.NotBefore"/> any of a saved trip's times may be: an arrive-by trip can be days ahead.</summary>
    private static readonly TimeSpan PlausibleSpan = TimeSpan.FromDays(30);

    /// <summary>
    /// True when a saved trip is whole enough to follow: every leg a walk or a ride, one choice for each ride
    /// on one of its lines, the counters in range, and every time in order and within
    /// <see cref="PlausibleSpan"/> of when the trip began. A damaged file can hold anything, so nothing else is assumed.
    /// </summary>
    public bool IsValid()
    {
        if (Progress is not { Itinerary.Legs: { } legs, Rides: { } rides, Known: { } known, MissedTrips: { } missed } progress
            || Leg?.To is not { Place: not null, Name: not null } || Destination is null || progress.NotBefore.Year is < 2000 or > 9000)
        {
            return false;
        }

        DateTimeOffset origin = progress.NotBefore;
        bool Near(DateTimeOffset at) => (at - origin).Duration() <= PlausibleSpan;
        bool NearOrNone(DateTimeOffset? at) => at is not { } value || Near(value);
        bool Span(DateTimeOffset from, DateTimeOffset to) => Near(from) && Near(to) && to >= from;

        RideLeg[] rideLegs = [.. legs.OfType<RideLeg>()];
        return legs.All(l => l is WalkLeg { Path: not null } or RideLeg && Span(l.Starts, l.Ends))
            && Span(progress.Itinerary.Departs, progress.Itinerary.Arrives)
            && rides.Count == rideLegs.Length
            && rideLegs.Zip(rides).All(pair => pair.Second is { } choice && Span(choice.Departs, choice.Alights) && IsValid(pair.First, choice.Line, Span))
            && known.All(a => a is { StopId: not null, RouteId: not null, DirectionId: not null } && Near(a.PredictedTime) && NearOrNone(a.ScheduledTime))
            && missed.All(m => m is not null)
            && NearOrNone(progress.LeftAt) && NearOrNone(progress.KnownAt) && NearOrNone(progress.AnnouncedDeparture)
            && progress.GetOffAlerts >= 0 && progress.GetOffAlerts <= rides.Count
            && progress.AnnouncedRide >= -1 && progress.AnnouncedRide < rides.Count
            && progress.StrandedRide >= -1 && progress.StrandedRide < rides.Count;
    }

    private static bool IsValid(RideLeg ride, int line, Func<DateTimeOffset, DateTimeOffset, bool> span) =>
        ride.Lines is { } lines && line >= 0 && line < lines.Count
        && lines.All(l => l is { Route.Id: not null, Route.DisplayId: not null, Direction.Id: not null, BoardAt.Id: not null, Path: not null, Departures: { } departures }
            && departures.All(d => d is not null && span(d.Departs, d.Alights)));
}

[JsonSerializable(typeof(ActiveTrip))]
public sealed partial class ActiveTripJsonContext : JsonSerializerContext;
