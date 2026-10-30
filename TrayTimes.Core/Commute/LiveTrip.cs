using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>One part of a <see cref="LiveTrip"/>, from <see cref="Starts"/> to <see cref="Ends"/> at live times.</summary>
public abstract record LiveLeg(DateTimeOffset Starts, DateTimeOffset Ends);

/// <summary>A planned walk, moved to fit the trip's live rides.</summary>
public sealed record LiveWalk(WalkLeg Walk, DateTimeOffset Starts, DateTimeOffset Ends) : LiveLeg(Starts, Ends);

/// <summary>A ride on <see cref="Line"/>, boarding at <see cref="LiveLeg.Starts"/> and getting off at <see cref="LiveLeg.Ends"/>.</summary>
/// <param name="Departure">The departures batch's departure it boards, or null when the batch had none and the plan's was used.</param>
public sealed record LiveRide(RideLeg Ride, RideLine Line, DateTimeOffset Starts, DateTimeOffset Ends, Arrival? Departure) : LiveLeg(Starts, Ends);

/// <summary>
/// A planned trip at live times. The plan supplies the structure (which lines, where to board, where
/// to change), which stays good for hours; the shared departures batch supplies the times.
/// </summary>
/// <param name="LeaveBy">When to leave the origin: the first ride's departure less the walk to it.</param>
public sealed record LiveTrip(TripItinerary Itinerary, IReadOnlyList<LiveLeg> Legs, DateTimeOffset LeaveBy, DateTimeOffset Arrives, bool IsLive = true)
{
    /// <summary>How long the trip takes from leaving to arriving: its walks, its rides, and the waits for transfers between them.</summary>
    public TimeSpan Duration => Arrives - LeaveBy;

    /// <summary>Preserves a future plan's leg times instead of moving its walks or boarding a bus running now.</summary>
    public static LiveTrip Scheduled(TripItinerary itinerary)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        List<LiveLeg> legs = [];
        foreach (TripLeg leg in itinerary.Legs)
        {
            if (leg is WalkLeg walk)
            {
                legs.Add(new LiveWalk(walk, walk.Starts, walk.Ends));
            }
            else if (leg is RideLeg ride)
            {
                RideLine line = ride.Lines.FirstOrDefault(l => l.Departures.Any(d => d.Departs == ride.Starts && d.Alights == ride.Ends))
                    ?? ride.Lines[0];
                legs.Add(new LiveRide(ride, line, ride.Starts, ride.Ends, null));
            }
        }

        return new LiveTrip(itinerary, legs, itinerary.Departs, itinerary.Arrives, IsLive: false);
    }

    /// <summary>
    /// Times <paramref name="itinerary"/> from <paramref name="now"/>, leg by leg. Each ride takes the
    /// departure, of any of its lines, that gets off soonest once the rider can be at that line's stop:
    /// the batch's next uncanceled departure there, else the plan's next one for a trip the batch
    /// doesn't know. It rides as long as the plan rides that trip, else the line's planned trip nearest
    /// in time. Walks before the first ride end as it leaves, so the rider waits at the origin.
    /// </summary>
    /// <param name="arrivals">The shared departures batch.</param>
    /// <returns>Null when a ride has no departure left: the trip is infeasible and needs a new plan.</returns>
    public static LiveTrip? Compute(TripItinerary itinerary, IEnumerable<Arrival> arrivals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        ArgumentNullException.ThrowIfNull(arrivals);
        ILookup<StopLine, Arrival> batch = arrivals.ToLookup(StopLine.Of);
        List<LiveLeg> legs = [];
        DateTimeOffset ready = now;
        bool boarded = false;
        foreach (TripLeg leg in itinerary.Legs)
        {
            if (leg is WalkLeg walk)
            {
                legs.Add(new LiveWalk(walk, ready, ready + walk.Duration));
                ready += walk.Duration;
            }
            else if (leg is RideLeg ride)
            {
                if (Board(ride, batch, ready) is not { } next)
                {
                    return null;
                }

                if (!boarded)
                {
                    TimeSpan wait = next.Starts - ready;
                    for (int i = 0; i < legs.Count; i++)
                    {
                        legs[i] = legs[i] with { Starts = legs[i].Starts + wait, Ends = legs[i].Ends + wait };
                    }

                    boarded = true;
                }

                legs.Add(next);
                ready = next.Ends;
            }
        }

        return new LiveTrip(itinerary, legs, legs.Count > 0 ? legs[0].Starts : now, ready);
    }

    /// <summary>
    /// The plan's best trip at live times: of its first <see cref="PlaceBoard.TripOptions"/> trips with a
    /// ride, the one that gets there first (then the one with fewer transfers, then the one leaving later).
    /// When none of those can still be made, its walk-only trip.
    /// </summary>
    /// <returns>Null when the plan has no trip left to make.</returns>
    public static LiveTrip? Best(TripPlan plan, IEnumerable<Arrival> arrivals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arrivals);
        Arrival[] batch = [.. arrivals];
        LiveTrip? ride = plan.Itineraries
            .Where(i => i.Legs.OfType<RideLeg>().Any())
            .Take(PlaceBoard.TripOptions)
            .Select(i => Compute(i, batch, now))
            .OfType<LiveTrip>()
            .OrderBy(t => t.Arrives)
            .ThenBy(t => t.Itinerary.Transfers)
            .ThenByDescending(t => t.LeaveBy)
            .FirstOrDefault();
        return ride ?? (plan.Itineraries.FirstOrDefault(i => !i.Legs.OfType<RideLeg>().Any()) is { } walk ? Compute(walk, batch, now) : null);
    }

    /// <summary>
    /// The first ride's chosen departure and later departures of that line for tracking chips.
    /// Planned fallbacks retain their trip IDs but are scheduled-only; canceled or superseded trips
    /// known to the batch are never restored from the plan.
    /// </summary>
    public IReadOnlyList<Arrival> FirstRideDepartures(IEnumerable<Arrival> arrivals, int maxDepartures)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepartures);
        if (!IsLive || Legs.OfType<LiveRide>().FirstOrDefault() is not { } ride)
        {
            return [];
        }

        RideLine line = ride.Line;
        Arrival[] known = [.. arrivals.Where(a => StopLine.Of(a) == new StopLine(line.BoardAt.Id, line.Route.Id, line.Direction.Id))];
        Arrival Scheduled(PlannedDeparture d) => new(line.BoardAt.Id, line.Route.Id, line.Route.DisplayId, line.Direction.Id,
            line.Direction.Name, d.Departs, IsRealTime: false, TripId: d.TripId, ScheduledTime: d.Departs, RouteColor: line.Route.Color, Mode: line.Route.Mode, VehicleKind: line.Route.VehicleKind);
        Arrival first = ride.Departure ?? Scheduled(new PlannedDeparture(ride.Starts, ride.Ends, false,
            line.Departures.FirstOrDefault(d => d.Departs == ride.Starts)?.TripId));
        IEnumerable<Arrival> planned = line.Departures.Where(d => !known.Any(a =>
            d.TripId is not null && a.TripId is not null ? a.IsTrip(d.TripId) : d.Departs == (a.ScheduledTime ?? a.PredictedTime))).Select(Scheduled);
        return [.. known.Concat(planned).Prepend(first)
            .Where(a => a.Status != ArrivalStatus.Canceled && a.PredictedTime >= ride.Starts)
            .DistinctBy(a => TripKey(a.TripId, a.PredictedTime))
            .OrderBy(a => a.PredictedTime)
            .Take(maxDepartures)];
    }

    /// <summary>Names one departure: its trip ID, else its time, for a feed without trip IDs.</summary>
    internal static string TripKey(string? tripId, DateTimeOffset departs) =>
        tripId ?? $"@{departs.ToUnixTimeSeconds()}";

    /// <summary>
    /// Names <paramref name="arrival"/> by what doesn't change from one check to the next: the timetable's trip ID,
    /// which stays when the trip gains a real-time one, else its trip ID, else its timetable time, which (unlike
    /// its prediction) doesn't move.
    /// </summary>
    internal static string TripKey(Arrival arrival) =>
        arrival.ScheduleTripId ?? TripKey(arrival.TripId, arrival.ScheduledTime ?? arrival.PredictedTime);

    /// <summary>True when <paramref name="skipped"/> names <paramref name="arrival"/> (by <see cref="TripKey(Arrival)"/>, or its trip ID or prediction).</summary>
    internal static bool IsSkipped(IReadOnlySet<string>? skipped, Arrival arrival) =>
        skipped is not null && (skipped.Contains(TripKey(arrival)) || skipped.Contains(TripKey(arrival.TripId, arrival.PredictedTime)));

    /// <summary>
    /// The departure of any of the ride's lines, at or after <paramref name="ready"/>, that gets off soonest,
    /// leaving out <paramref name="skipped"/> trips (by <see cref="TripKey"/>).
    /// </summary>
    internal static LiveRide? Board(RideLeg ride, ILookup<StopLine, Arrival> batch, DateTimeOffset ready, IReadOnlySet<string>? skipped = null) => ride.Lines
        .Select(line => Next(ride, line, batch[new StopLine(line.BoardAt.Id, line.Route.Id, line.Direction.Id)], ready, skipped))
        .OfType<LiveRide>()
        .OrderBy(r => r.Ends)
        .ThenBy(r => r.Starts)
        .FirstOrDefault();

    /// <summary>The line's next departure at or after <paramref name="ready"/>: the batch's, else the plan's.</summary>
    private static LiveRide? Next(RideLeg ride, RideLine line, IEnumerable<Arrival> batch, DateTimeOffset ready, IReadOnlySet<string>? skipped)
    {
        Arrival[] known = [.. batch];
        if (known.Where(a => a.Status != ArrivalStatus.Canceled && a.PredictedTime >= ready && !IsSkipped(skipped, a))
            .MinBy(a => a.PredictedTime) is { } departure)
        {
            return new LiveRide(ride, line, departure.PredictedTime, departure.PredictedTime + RideTime(ride, line, departure), departure);
        }

        // The batch may not reach this far ahead or cover this stop. A trip it does know (canceled,
        // or already gone) isn't taken from the plan.
        HashSet<string> knownTrips = [.. known.SelectMany(a => new[] { a.TripId, a.ScheduleTripId }).OfType<string>()];
        return line.Departures
            .Where(d => d.Departs >= ready && (d.TripId is null || !knownTrips.Contains(d.TripId))
                && !(skipped?.Contains(TripKey(d.TripId, d.Departs)) ?? false))
            .MinBy(d => d.Departs) is { } planned
            ? new LiveRide(ride, line, planned.Departs, planned.Alights, null)
            : null;
    }

    /// <summary>How long the plan rides <paramref name="departure"/>'s trip, else the line's planned trip nearest in time, else the leg.</summary>
    internal static TimeSpan RideTime(RideLeg ride, RideLine line, Arrival departure)
    {
        PlannedDeparture? planned = line.Departures.FirstOrDefault(d => d.TripId is not null && departure.IsTrip(d.TripId))
            ?? line.Departures.MinBy(d => (d.Departs - departure.PredictedTime).Duration());
        return planned is null ? ride.Duration : planned.Alights - planned.Departs;
    }
}
