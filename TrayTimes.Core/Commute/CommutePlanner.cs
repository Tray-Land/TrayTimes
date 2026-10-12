using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>A trip the flyout suggests: where the rider is, and where they're most likely headed next.</summary>
/// <param name="From">The favorite place the rider is at, if any.</param>
/// <param name="Origin">Where to look for stops to board at.</param>
/// <param name="To">Where the rider is headed.</param>
/// <param name="IsGuess">True when the device location was unknown and the trip was guessed from the time of day.</param>
public sealed record CommuteLeg(FavoritePlace? From, GeoPoint Origin, FavoritePlace To, bool IsGuess)
{
    public bool IsAlreadyThere => GeoMath.DistanceMeters(Origin, To.Location) <= CommutePlanner.AtPlaceMeters;
}

/// <summary>A route in one direction of travel. IDs compare case-insensitively.</summary>
public readonly record struct RouteDirection(string RouteId, string DirectionId)
{
    public bool Equals(RouteDirection other) =>
        string.Equals(RouteId, other.RouteId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DirectionId, other.DirectionId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(RouteId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(DirectionId));
}

/// <summary>
/// Works out the rider's likely next trip (away from home, head home; at home, head out) and which
/// buses make it in one ride.
/// </summary>
public static class CommutePlanner
{
    /// <summary>How close the rider must be to a favorite place to count as being there.</summary>
    public const double AtPlaceMeters = 400;

    /// <summary>How far the rider will walk to or from a stop (about ten minutes).</summary>
    public const double WalkRadiusMeters = 800;

    // Pattern shapes include every stop's exact position, so a served stop sits right on the line.
    private const double OnRouteMeters = 75;
    private const int MorningEndsHour = 12;

    /// <summary>Where the rider goes when they leave home: work, else school, else their first custom place.</summary>
    public static FavoritePlace? AwayPlace(IReadOnlyList<FavoritePlace> places) =>
        places.FirstOrDefault(p => p.Kind == PlaceKind.Work)
        ?? places.FirstOrDefault(p => p.Kind == PlaceKind.School)
        ?? places.FirstOrDefault(p => p.Kind == PlaceKind.Custom);

    /// <summary>The closest favorite place within <see cref="AtPlaceMeters"/> of <paramref name="point"/>.</summary>
    public static FavoritePlace? PlaceAt(IReadOnlyList<FavoritePlace> places, GeoPoint point) => places
        .Select(p => (Place: p, Meters: GeoMath.DistanceMeters(point, p.Location)))
        .Where(p => p.Meters <= AtPlaceMeters)
        .OrderBy(p => p.Meters)
        .Select(p => p.Place)
        .FirstOrDefault();

    /// <summary>
    /// The rider's likely next trip. At home, it heads to <see cref="AwayPlace"/>; anywhere else, it heads
    /// home. Without a location, mornings are assumed to start at home and afternoons to end there.
    /// Returns null when there's not enough to go on (no home, or nowhere to go from home).
    /// </summary>
    /// <param name="destination">Heads here instead of the likely place, when the rider has picked one.</param>
    public static CommuteLeg? Plan(IReadOnlyList<FavoritePlace> places, GeoPoint? here, DateTimeOffset now, FavoritePlace? destination = null)
    {
        FavoritePlace? home = places.FirstOrDefault(p => p.Kind == PlaceKind.Home);
        FavoritePlace? away = AwayPlace(places);

        if (here is { } point)
        {
            FavoritePlace? at = PlaceAt(places, point);
            destination ??= at?.Kind == PlaceKind.Home ? away : home;
            return destination is null ? null : new CommuteLeg(at, at?.Location ?? point, destination, IsGuess: false);
        }

        destination ??= now.Hour < MorningEndsHour ? away : home;
        FavoritePlace? from = destination?.Kind == PlaceKind.Home ? away : home;
        return destination is null || from is null ? null : new CommuteLeg(from, from.Location, destination, IsGuess: true);
    }

    /// <summary>
    /// When the rider reaches the destination: the earliest, over the stops the bus calls at near
    /// it, of the bus's arrival there plus the walk the rest of the way. Null if none of those stops
    /// has a known walk.
    /// </summary>
    /// <param name="downstream">Where the bus is expected after the boarding stop.</param>
    /// <param name="walkSecondsFromStop">Walking time from each stop near the destination, by stop ID.</param>
    public static DateTimeOffset? ArrivalAtDestination(IEnumerable<TripStop> downstream, IReadOnlyDictionary<string, double> walkSecondsFromStop)
    {
        DateTimeOffset? earliest = null;
        foreach (TripStop stop in downstream)
        {
            if (walkSecondsFromStop.TryGetValue(stop.StopId, out double walk))
            {
                DateTimeOffset arrival = stop.ArrivesAt.AddSeconds(walk);
                if (earliest is null || arrival < earliest)
                {
                    earliest = arrival;
                }
            }
        }

        return earliest;
    }

    /// <summary>Route directions that serve both a stop in <paramref name="originStops"/> and one in <paramref name="destinationStops"/>.</summary>
    public static IReadOnlySet<RouteDirection> SharedRoutes(IEnumerable<IndexedStop> originStops, IEnumerable<IndexedStop> destinationStops)
    {
        HashSet<RouteDirection> atDestination = destinationStops.SelectMany(Serving).ToHashSet();
        return originStops.SelectMany(Serving).Where(atDestination.Contains).ToHashSet();
    }

    /// <summary>
    /// The shared route directions that reach a destination stop after passing an origin stop, so one
    /// ride gets there. A route whose shape is unknown cannot confirm a direct ride.
    /// </summary>
    public static IReadOnlySet<RouteDirection> DirectRoutes(
        IReadOnlyList<IndexedStop> originStops,
        IReadOnlyList<IndexedStop> destinationStops,
        IReadOnlyDictionary<string, IReadOnlyList<RoutePattern>> patternsByRoute)
    {
        HashSet<RouteDirection> direct = [];
        foreach (RouteDirection route in SharedRoutes(originStops, destinationStops))
        {
            List<RoutePattern> shapes = (patternsByRoute.TryGetValue(route.RouteId, out IReadOnlyList<RoutePattern>? patterns) ? patterns : [])
                .Where(p => string.Equals(p.DirectionId, route.DirectionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<GeoPoint> boardAt = StopsServedBy(originStops, route);
            List<GeoPoint> alightAt = StopsServedBy(destinationStops, route);
            if (shapes.Any(p => RunsFromTo(p, boardAt, alightAt)))
            {
                direct.Add(route);
            }
        }

        return direct;
    }

    /// <summary>Whether <paramref name="pattern"/> passes one of <paramref name="boardAt"/> before one of <paramref name="alightAt"/>.</summary>
    public static bool RunsFromTo(RoutePattern pattern, IEnumerable<GeoPoint> boardAt, IEnumerable<GeoPoint> alightAt)
    {
        int firstBoard = boardAt.Select(p => IndexOn(pattern, p)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();
        int lastAlight = alightAt.Select(p => IndexOn(pattern, p)).DefaultIfEmpty(-1).Max();
        return firstBoard < lastAlight;
    }

    private static IEnumerable<RouteDirection> Serving(IndexedStop stop) =>
        stop.Routes.Select(r => new RouteDirection(r.RouteId, r.DirectionId));

    private static List<GeoPoint> StopsServedBy(IEnumerable<IndexedStop> stops, RouteDirection route) => stops
        .Where(s => Serving(s).Contains(route))
        .Select(s => s.Location)
        .ToList();

    /// <summary>The index of the pattern point nearest <paramref name="point"/>, or -1 if the route doesn't pass it.</summary>
    private static int IndexOn(RoutePattern pattern, GeoPoint point)
    {
        int best = -1;
        double bestMeters = OnRouteMeters;
        for (int i = 0; i < pattern.Points.Count; i++)
        {
            double meters = GeoMath.DistanceMeters(pattern.Points[i], point);
            if (meters <= bestMeters)
            {
                best = i;
                bestMeters = meters;
            }
        }

        return best;
    }
}
