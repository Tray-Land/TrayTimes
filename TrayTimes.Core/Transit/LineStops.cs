using System.Globalization;

namespace TrayTimes.Transit;

/// <summary>Another route that serves a stop on the line being looked at.</summary>
public sealed record StopTransfer(string RouteId, string DisplayId, string? Color);

/// <summary>What a stop on a line's direction has to show: the routes that cross it there, and where the line's buses are.</summary>
/// <param name="Vehicles">How many of the line's buses are nearest this stop.</param>
public sealed record LineStop(TransitStop Stop, IReadOnlyList<StopTransfer> Transfers, int Vehicles);

/// <summary>Works out, for one direction of a line, its stops' transfers and which stop each bus is nearest to.</summary>
public static class LineStops
{
    /// <summary>
    /// Every stop of <paramref name="stops"/> in order. <paramref name="departures"/> are the departures at those stops
    /// (any routes); the routes other than <paramref name="routeId"/> listed at a stop are its transfers.
    /// </summary>
    public static IReadOnlyList<LineStop> Build(
        string routeId,
        IReadOnlyList<TransitStop> stops,
        IReadOnlyList<Arrival> departures,
        IReadOnlyList<Vehicle> vehicles)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(departures);
        ArgumentNullException.ThrowIfNull(vehicles);

        ILookup<string, Arrival> byStop = departures.ToLookup(a => a.StopId, StringComparer.Ordinal);
        int[] nearest = NearestStops(stops, vehicles);
        List<LineStop> result = new(stops.Count);
        for (int i = 0; i < stops.Count; i++)
        {
            TransitStop stop = stops[i];
            Arrival[] here = [.. byStop[stop.Id]];
            StopTransfer[] transfers = [.. here
                .Where(a => !Same(a.RouteId, routeId))
                .Select(a => new StopTransfer(a.RouteId, a.RouteDisplayId, a.RouteColor))
                .DistinctBy(t => t.RouteId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, TransferOrder.Instance)];
            int index = i;
            result.Add(new LineStop(stop, transfers, nearest.Count(n => n == index)));
        }

        return result;
    }

    /// <summary>The index in <paramref name="stops"/> of the stop each vehicle is closest to; empty when there are no stops.</summary>
    public static int[] NearestStops(IReadOnlyList<TransitStop> stops, IReadOnlyList<Vehicle> vehicles)
    {
        if (stops.Count == 0)
        {
            return [];
        }

        return [.. vehicles.Select(v =>
        {
            int best = 0;
            double bestMeters = double.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                double meters = GeoMath.DistanceMeters(v.Location, stops[i].Location);
                if (meters < bestMeters)
                {
                    best = i;
                    bestMeters = meters;
                }
            }

            return best;
        })];
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Numbered routes first, in number order, then the rest by name.</summary>
    private sealed class TransferOrder : IComparer<StopTransfer>
    {
        public static TransferOrder Instance { get; } = new();

        public int Compare(StopTransfer? x, StopTransfer? y)
        {
            if (x is null || y is null)
            {
                return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            }

            bool xNumber = int.TryParse(x.DisplayId, NumberStyles.None, CultureInfo.InvariantCulture, out int xValue);
            bool yNumber = int.TryParse(y.DisplayId, NumberStyles.None, CultureInfo.InvariantCulture, out int yValue);
            if (xNumber && yNumber)
            {
                return xValue.CompareTo(yValue);
            }

            if (xNumber != yNumber)
            {
                return xNumber ? -1 : 1;
            }

            return string.Compare(x.DisplayId, y.DisplayId, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
