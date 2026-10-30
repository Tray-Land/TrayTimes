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

/// <summary>The stops a Commute, Nearby, or board list shows, and which line directions each one lists.</summary>
/// <param name="Lines">The line directions to list at each stop.</param>
/// <param name="Trips">For a commute, how each listed line gets to the destination.</param>
/// <param name="EveryLineAt">Stops that list every line, such as a stop saved with All routes.</param>
public sealed record PlaceBoardResult(
    IReadOnlyList<NearbyStop> Stops,
    IReadOnlySet<StopLine> Lines,
    IReadOnlyDictionary<StopLine, TripTiming>? Trips = null,
    IReadOnlySet<string>? EveryLineAt = null)
{
    /// <summary>Whether the list shows <paramref name="line"/> at its stop.</summary>
    public bool Lists(StopLine line) => Lines.Contains(line) || (EveryLineAt?.Contains(line.StopId) ?? false);
}

/// <summary>Turns nearby lines, saved stops, and trip plans into the flyout's stops.</summary>
public static class PlaceBoard
{
    /// <summary>How far from its center the board looks for lines and pins saved stops: about a 12-minute walk.</summary>
    public const int RadiusMeters = 1000;

    /// <summary>How many of a plan's trips stay live from the shared batch: the trip view's options.</summary>
    public const int TripOptions = 3;

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
    /// The board around <paramref name="center"/>: saved stops within <see cref="RadiusMeters"/> come first,
    /// nearest first, each listing the lines it was saved for (every line, if saved with All routes).
    /// Then the nearest other lines, as <see cref="Nearby"/> lists them, leaving out lines a pinned stop
    /// was saved for. A nearby line whose closest stop is pinned is listed at that stop.
    /// </summary>
    public static PlaceBoardResult Around(NearbyLines nearby, IReadOnlyList<SavedStop> saved, GeoPoint center, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(nearby);
        ArgumentNullException.ThrowIfNull(saved);
        List<(IGrouping<string, SavedStop> Saved, double Meters)> pins = [.. saved
            .GroupBy(s => s.StopId, StringComparer.Ordinal)
            .Select(g => (Saved: g, Meters: GeoMath.DistanceMeters(center, new GeoPoint(g.First().Lat, g.First().Lon))))
            .Where(p => p.Meters <= RadiusMeters)
            .OrderBy(p => p.Meters)
            .Take(Math.Max(0, maxStops))];
        HashSet<string> pinnedIds = new(pins.Select(p => p.Saved.Key), StringComparer.Ordinal);
        ILookup<string, NearbyLine> atPins = nearby.Lines.Where(l => pinnedIds.Contains(l.Stop.Id)).ToLookup(l => l.Stop.Id, StringComparer.Ordinal);
        HashSet<RouteDirection> pinnedLines = [.. pins
            .SelectMany(p => p.Saved)
            .Where(s => s.RouteId.Length > 0)
            .Select(s => new RouteDirection(s.RouteId, s.DirectionId))];
        NearbyLines others = nearby with
        {
            Lines = [.. nearby.Lines.Where(l => !pinnedIds.Contains(l.Stop.Id) && !pinnedLines.Contains(new RouteDirection(l.Route.Id, l.Direction.Id)))],
        };

        PlaceBoardResult pinned = Pin(pins, atPins);
        PlaceBoardResult rest = Nearby(others, center, maxStops - pins.Count);
        return new PlaceBoardResult([.. pinned.Stops, .. rest.Stops], new HashSet<StopLine>(pinned.Lines.Concat(rest.Lines)), EveryLineAt: pinned.EveryLineAt);
    }

    /// <summary>
    /// Puts the saved stops back first after <paramref name="ranked"/> reordered a board (by walk time, say),
    /// keeping the new order within the saved stops and within the rest, then keeps the first <paramref name="maxStops"/>.
    /// </summary>
    public static IReadOnlyList<NearbyStop> SavedFirst(IEnumerable<NearbyStop> ranked, IReadOnlyList<SavedStop> saved, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(saved);
        HashSet<string> savedIds = new(saved.Select(s => s.StopId), StringComparer.Ordinal);
        return [.. ranked.OrderBy(n => !savedIds.Contains(n.Stop.Id)).Take(Math.Max(0, maxStops))];
    }

    /// <summary>
    /// Every saved stop, in saved order, each listing the lines it was saved for (every line, if saved
    /// with All routes). It needs no discovery, and has no center, so each stop's distance is 0.
    /// </summary>
    public static PlaceBoardResult Saved(IReadOnlyList<SavedStop> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return Pin(saved.GroupBy(s => s.StopId, StringComparer.Ordinal).Select(g => (g, 0d)), Enumerable.Empty<NearbyLine>().ToLookup(l => l.Stop.Id));
    }

    /// <summary>
    /// Where every ride of the plan's first <see cref="TripOptions"/> transit trips boards, transfers and
    /// alternative lines included, so watching these stops keeps those whole trips live.
    /// </summary>
    public static IReadOnlyList<string> BoardingStopIds(TripPlan plan, Func<TripItinerary, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Itineraries
            .Where(i => i.Legs.OfType<RideLeg>().Any())
            .Take(TripOptions)
            .Where(i => include?.Invoke(i) ?? true)
            .SelectMany(i => i.Legs.OfType<RideLeg>())
            .SelectMany(r => r.Lines)
            .Select(l => l.BoardAt.Id)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Where to board for each way the planner found, best trip first. Each line direction at a stop
    /// keeps its quickest trip, with the walk to the stop from the plan. Walk-only trips are left out.
    /// </summary>
    public static PlaceBoardResult Commute(TripPlan plan, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<StopLine, Boarding> best = [];
        foreach (Boarding boarding in plan.Itineraries.SelectMany(Boardings))
        {
            if (!best.TryGetValue(boarding.Line, out Boarding? current) || boarding.Arrives < current.Arrives)
            {
                best[boarding.Line] = boarding;
            }
        }

        List<NearbyStop> stops = [.. best.Values
            .GroupBy(b => b.Stop.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Min(b => b.Arrives))
            .Take(Math.Max(0, maxStops))
            .Select(g =>
            {
                Boarding first = g.MinBy(b => b.Walk)!;
                return new NearbyStop(ToIndexed(first.Stop, g.Select(b => (b.Route, b.Direction))), first.WalkMeters, first.Walk.TotalSeconds, first.WalkMeters);
            })];
        HashSet<string> kept = [.. stops.Select(s => s.Stop.Id)];
        Dictionary<StopLine, TripTiming> trips = best
            .Where(b => kept.Contains(b.Key.StopId))
            .ToDictionary(b => b.Key, b => new TripTiming(b.Value.Arrives - b.Value.Departs, b.Value.Transfers));
        return new PlaceBoardResult(stops, new HashSet<StopLine>(trips.Keys), trips);
    }

    /// <summary>When the rider reaches the destination on <paramref name="arrival"/>, if its line has a trip.</summary>
    public static DateTimeOffset? ArrivalAtDestination(Arrival arrival, IReadOnlyDictionary<StopLine, TripTiming>? trips) =>
        trips is not null && trips.TryGetValue(StopLine.Of(arrival), out TripTiming? timing)
            ? arrival.PredictedTime + timing.RideToDestination
            : null;

    /// <summary>
    /// Each way to start a trip: every line of its first ride, on that line's first departure, after
    /// the walk to it. A line that leaves at another time shifts the arrival by the same amount.
    /// </summary>
    private static IEnumerable<Boarding> Boardings(TripItinerary itinerary)
    {
        if (itinerary.Legs.OfType<RideLeg>().FirstOrDefault() is not { } ride)
        {
            yield break;
        }

        WalkLeg[] walks = [.. itinerary.Legs.TakeWhile(l => l is not RideLeg).OfType<WalkLeg>()];
        TimeSpan walk = walks.Aggregate(TimeSpan.Zero, (total, w) => total + w.Duration);
        TimeSpan afterRide = itinerary.Arrives - ride.Ends;
        foreach (RideLine line in ride.Lines)
        {
            if (line.Departures.MinBy(d => d.Departs) is { } departure)
            {
                yield return new Boarding(
                    new StopLine(line.BoardAt.Id, line.Route.Id, line.Direction.Id), line.BoardAt, line.Route, line.Direction,
                    walks.Sum(w => w.Meters), walk, departure.Departs, departure.Alights + afterRide, itinerary.Transfers);
            }
        }
    }

    /// <summary>
    /// Saved stops as board stops, each listing the lines saved there (every line, if saved with All
    /// routes) and the nearby lines whose closest stop it is.
    /// </summary>
    private static PlaceBoardResult Pin(IEnumerable<(IGrouping<string, SavedStop> Saved, double Meters)> pins, ILookup<string, NearbyLine> nearbyHere)
    {
        List<NearbyStop> stops = [];
        HashSet<StopLine> lines = [];
        HashSet<string> everyLine = new(StringComparer.Ordinal);
        foreach ((IGrouping<string, SavedStop> saved, double meters) in pins)
        {
            SavedStop first = saved.First();
            NearbyLine[] here = [.. nearbyHere[saved.Key]];
            stops.Add(new NearbyStop(new IndexedStop(first.StopId, first.DisplayName, first.Lat, first.Lon, [.. saved
                .Where(s => s.RouteId.Length > 0)
                .Select(s => new StopRouteInfo(s.RouteId, s.RouteDisplayId, s.RouteColor, s.DirectionId, s.DirectionName))
                .Concat(here.Select(l => RouteInfo(l.Route, l.Direction)))
                .DistinctBy(r => (r.RouteId, r.DirectionId))]), meters));
            lines.UnionWith(saved.Where(s => !s.AllRoutes).Select(s => new StopLine(saved.Key, s.RouteId, s.DirectionId)));
            lines.UnionWith(here.Select(l => new StopLine(saved.Key, l.Route.Id, l.Direction.Id)));
            if (saved.Any(s => s.AllRoutes))
            {
                everyLine.Add(saved.Key);
            }
        }

        return new PlaceBoardResult(stops, lines, EveryLineAt: everyLine);
    }

    private static IndexedStop ToIndexed(TransitStop stop, IEnumerable<(TransitRoute Route, TransitDirection Direction)> lines) => new(
        stop.Id,
        stop.Name,
        stop.Location.Lat,
        stop.Location.Lon,
        [.. lines
            .Select(l => RouteInfo(l.Route, l.Direction))
            .DistinctBy(r => (r.RouteId, r.DirectionId))]);

    private static StopRouteInfo RouteInfo(TransitRoute route, TransitDirection direction) =>
        new(route.Id, route.DisplayId, route.Color, direction.Id, direction.Name);

    /// <summary>Boarding <see cref="Line"/> at <see cref="Departs"/>, after the walk there, reaches the destination at <see cref="Arrives"/>.</summary>
    private sealed record Boarding(
        StopLine Line,
        TransitStop Stop,
        TransitRoute Route,
        TransitDirection Direction,
        double WalkMeters,
        TimeSpan Walk,
        DateTimeOffset Departs,
        DateTimeOffset Arrives,
        int Transfers);
}
