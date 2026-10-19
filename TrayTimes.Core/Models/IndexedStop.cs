using System.Text.Json.Serialization;
using TrayTimes.Transit;

namespace TrayTimes.Models;

/// <summary>A route and direction that serve a stop.</summary>
public sealed record StopRouteInfo(string RouteId, string RouteDisplayId, string? RouteColor, string DirectionId, string DirectionName);

/// <summary>A stop found near a place, with the route directions it's listed for there.</summary>
public sealed record IndexedStop(string Id, string Name, double Lat, double Lon, List<StopRouteInfo> Routes)
{
    [JsonIgnore]
    public GeoPoint Location => new(Lat, Lon);
}

/// <summary>
/// A stop and its distance from a point. <see cref="WalkSeconds"/> and <see cref="WalkMeters"/>
/// come from a walking route when one was looked up; otherwise only the straight-line <see cref="Meters"/> is known.
/// </summary>
public sealed record NearbyStop(IndexedStop Stop, double Meters, double? WalkSeconds = null, double? WalkMeters = null)
{
    /// <summary>The walking distance if routed, else the straight-line distance.</summary>
    public double DistanceMeters => WalkMeters ?? Meters;

    /// <summary>Whole minutes to walk here: the routed time if known, else estimated from the straight-line distance.</summary>
    public int WalkMinutes(double fallbackMetersPerMinute) =>
        (int)Math.Ceiling(WalkSeconds is { } seconds ? seconds / 60 : Meters / fallbackMetersPerMinute);

    /// <summary>Re-orders by walk time, shortest first; stops without a routed time sort after those with one, by distance.</summary>
    public static IReadOnlyList<NearbyStop> OrderByWalk(IEnumerable<NearbyStop> stops) => [.. stops
        .OrderBy(n => n.WalkSeconds is null)
        .ThenBy(n => n.WalkSeconds ?? 0)
        .ThenBy(n => n.Meters)];
}
