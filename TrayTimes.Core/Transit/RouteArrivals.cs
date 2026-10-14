namespace TrayTimes.Transit;

/// <summary>One route's buses at a stop, listed as a single row: the bus to catch, then the ones after it.</summary>
public sealed record RouteArrivals(Arrival Next, IReadOnlyList<Arrival> Later)
{
    /// <summary>
    /// Folds a stop's arrivals into one row per route and direction, so the list shows each route
    /// once with its upcoming times rather than repeating it. A row leads with the soonest bus the
    /// rider can still catch (else the soonest), or with <paramref name="pinned"/> if that comes
    /// first; buses before the lead are dropped. Rows are ordered by their lead.
    /// </summary>
    /// <param name="arrivals">One stop's arrivals, soonest first.</param>
    /// <param name="pinned">A bus to keep listed even once it can't be caught, such as the tracked one.</param>
    /// <param name="canCatch">Whether the rider could make a bus, e.g. it isn't canceled and they can walk there in time.</param>
    /// <param name="maxRows">The most routes to list.</param>
    /// <param name="maxPerRow">The most buses a row holds, its lead included.</param>
    public static IReadOnlyList<RouteArrivals> ByRoute(
        IEnumerable<Arrival> arrivals,
        Arrival? pinned,
        Func<Arrival, bool> canCatch,
        int maxRows,
        int maxPerRow)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(canCatch);

        List<RouteArrivals> rows = [];
        foreach (IGrouping<(string, string), Arrival> route in arrivals.GroupBy(a => (a.RouteId.ToUpperInvariant(), a.DirectionId.ToUpperInvariant())))
        {
            List<Arrival> buses = [.. route];
            int lead = buses.FindIndex(a => a == pinned || canCatch(a));
            lead = Math.Max(lead, 0);
            rows.Add(new RouteArrivals(buses[lead], [.. buses.Skip(lead + 1).Take(Math.Max(0, maxPerRow - 1))]));
        }

        return [.. rows.OrderBy(r => r.Next.PredictedTime).Take(maxRows)];
    }
}
