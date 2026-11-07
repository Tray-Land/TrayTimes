using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>A destination the user picked from search, kept so it can be chosen again. Typed queries are never kept.</summary>
public sealed record RecentDestination(string Name, double Lat, double Lon, string? StopId = null, string? Title = null, string? Subtitle = null)
{
    public const int Max = 8;

    public static RecentDestination From(DestinationResult result) =>
        new(result.Name, result.Location.Lat, result.Location.Lon, result.StopId, result.Title, result.Subtitle);

    public DestinationResult ToResult() => new(Name, new GeoPoint(Lat, Lon), StopId, Title, Subtitle);

    /// <summary>The same place as <paramref name="other"/>: the same stop, or the same name at the same spot.</summary>
    public bool IsSamePlace(RecentDestination other) =>
        StopId is not null || other.StopId is not null
            ? string.Equals(StopId, other.StopId, StringComparison.Ordinal)
            : string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(Lat - other.Lat) < 0.0005 && Math.Abs(Lon - other.Lon) < 0.0005;

    /// <summary>Puts <paramref name="chosen"/> first, drops any earlier copy, and keeps at most <see cref="Max"/>.</summary>
    public static IReadOnlyList<RecentDestination> WithChosen(IEnumerable<RecentDestination> recents, RecentDestination chosen) =>
        [chosen, .. recents.Where(r => !r.IsSamePlace(chosen))
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && double.IsFinite(r.Lat) && double.IsFinite(r.Lon))
            .Take(Max - 1)];
}
