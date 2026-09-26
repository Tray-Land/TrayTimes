using TrayTimes.Transit;

namespace TrayTimes.Models;

/// <summary>The result of the most recent arrivals poll.</summary>
public sealed record ArrivalsSnapshot(DateTimeOffset? FetchedAt, IReadOnlyList<Arrival> Arrivals, string? Error)
{
    public static ArrivalsSnapshot Empty { get; } = new(null, [], null);

    /// <summary>Arrivals for one saved stop, honoring its route/direction filter.</summary>
    public IEnumerable<Arrival> For(SavedStop stop) => Arrivals.Where(a =>
        a.StopId == stop.StopId
        && (stop.AllRoutes
            || (string.Equals(a.RouteId, stop.RouteId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.DirectionId, stop.DirectionId, StringComparison.OrdinalIgnoreCase))));
}
