using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>A route direction at one stop. IDs compare case-insensitively.</summary>
public readonly record struct StopLine(string StopId, string RouteId, string DirectionId)
{
    public static StopLine Of(Arrival arrival) => new(arrival.StopId, arrival.RouteId, arrival.DirectionId);

    public bool Equals(StopLine other) =>
        string.Equals(StopId, other.StopId, StringComparison.Ordinal)
        && string.Equals(RouteId, other.RouteId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DirectionId, other.DirectionId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(StopId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(RouteId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(DirectionId));
}

/// <summary>How a commute continues after boarding a line: the rest of the trip, and how many changes it takes.</summary>
public sealed record TripTiming(TimeSpan RideToDestination, int Transfers);

/// <summary>The stops a Commute or Nearby list shows, and which line directions each one lists.</summary>
/// <param name="Lines">The line directions to list at each stop.</param>
/// <param name="Trips">For a commute, how each listed line gets to the destination.</param>
public sealed record PlaceBoardResult(
    IReadOnlyList<NearbyStop> Stops,
    IReadOnlySet<StopLine> Lines,
    IReadOnlyDictionary<StopLine, TripTiming>? Trips = null);

/// <summary>Turns nearby lines and trip plans into the flyout's stops.</summary>
public static class PlaceBoard
{
    /// <summary>
    /// The nearest stops with a line near <paramref name="origin"/>, each line direction listed once,
    /// at its closest stop, so a route on both sides of the street isn't repeated.
    /// </summary>
    public static PlaceBoardResult Nearby(NearbyLines nearby, GeoPoint origin, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(nearby);
        List<NearbyStop> stops = [.. nearby.Lines
            .GroupBy(l => l.Stop.Id, StringComparer.Ordinal)
            .Select(g => new NearbyStop(ToIndexed(g.First().Stop, g.Select(l => (l.Route, l.Direction))), g.Min(l => l.Meters)))
            .OrderBy(n => n.Meters)
            .ThenBy(n => GeoMath.DistanceMeters(origin, n.Stop.Location))
            .Take(Math.Max(0, maxStops))];
        HashSet<string> kept = [.. stops.Select(s => s.Stop.Id)];
        HashSet<StopLine> lines = [.. nearby.Lines.Where(l => kept.Contains(l.Stop.Id)).Select(l => new StopLine(l.Stop.Id, l.Route.Id, l.Direction.Id))];
        return new PlaceBoardResult(stops, lines);
    }

    /// <summary>
    /// Where to board for each way the planner found, best trip first. Each line direction at a stop
    /// keeps its quickest trip, with the walk to the stop from the plan.
    /// </summary>
    public static PlaceBoardResult Commute(TripPlan plan, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<StopLine, TripOption> best = [];
        foreach (TripOption option in plan.Options)
        {
            StopLine line = new(option.BoardAt.Id, option.Route.Id, option.Direction.Id);
            if (!best.TryGetValue(line, out TripOption? current) || option.Arrives < current.Arrives)
            {
                best[line] = option;
            }
        }

        List<NearbyStop> stops = [.. best.Values
            .GroupBy(o => o.BoardAt.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Min(o => o.Arrives))
            .Take(Math.Max(0, maxStops))
            .Select(g =>
            {
                TripOption first = g.MinBy(o => o.Walk)!;
                return new NearbyStop(ToIndexed(first.BoardAt, g.Select(o => (o.Route, o.Direction))), first.WalkMeters, first.Walk.TotalSeconds, first.WalkMeters);
            })];
        HashSet<string> kept = [.. stops.Select(s => s.Stop.Id)];
        Dictionary<StopLine, TripTiming> trips = best
            .Where(b => kept.Contains(b.Key.StopId))
            .ToDictionary(b => b.Key, b => new TripTiming(b.Value.RideToDestination, b.Value.Transfers));
        return new PlaceBoardResult(stops, new HashSet<StopLine>(trips.Keys), trips);
    }

    /// <summary>When the rider reaches the destination on <paramref name="arrival"/>, if its line has a trip.</summary>
    public static DateTimeOffset? ArrivalAtDestination(Arrival arrival, IReadOnlyDictionary<StopLine, TripTiming>? trips) =>
        trips is not null && trips.TryGetValue(StopLine.Of(arrival), out TripTiming? timing)
            ? arrival.PredictedTime + timing.RideToDestination
            : null;

    private static IndexedStop ToIndexed(TransitStop stop, IEnumerable<(TransitRoute Route, TransitDirection Direction)> lines) => new(
        stop.Id,
        stop.Name,
        stop.Location.Lat,
        stop.Location.Lon,
        [.. lines
            .Select(l => new StopRouteInfo(l.Route.Id, l.Route.DisplayId, l.Route.Color, l.Direction.Id, l.Direction.Name))
            .DistinctBy(r => (r.RouteId, r.DirectionId))]);
}
