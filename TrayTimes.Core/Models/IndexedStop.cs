using System.Text.Json.Serialization;
using TrayTimes.Transit;

namespace TrayTimes.Models;

/// <summary>A route and direction that serve a stop.</summary>
public sealed record StopRouteInfo(string RouteId, string RouteDisplayId, string? RouteColor, string DirectionId, string DirectionName);

/// <summary>A stop in the agency-wide directory, with every route/direction that serves it.</summary>
public sealed record IndexedStop(string Id, string Name, double Lat, double Lon, List<StopRouteInfo> Routes)
{
    [JsonIgnore]
    public GeoPoint Location => new(Lat, Lon);
}

/// <summary>A directory stop and its distance from a point.</summary>
public sealed record NearbyStop(IndexedStop Stop, double Meters)
{
    /// <summary>
    /// Stops within <paramref name="radiusMeters"/>, closest first. If none are that close, the
    /// nearest few within <paramref name="fallbackRadiusMeters"/> instead.
    /// </summary>
    public static IReadOnlyList<NearbyStop> Find(IReadOnlyList<IndexedStop> stops, GeoPoint origin, int max, double radiusMeters = 800, double fallbackRadiusMeters = 2000)
    {
        List<NearbyStop> sorted = stops
            .Select(s => new NearbyStop(s, GeoMath.DistanceMeters(origin, s.Location)))
            .Where(n => n.Meters <= fallbackRadiusMeters)
            .OrderBy(n => n.Meters)
            .ToList();
        List<NearbyStop> close = sorted.Where(n => n.Meters <= radiusMeters).Take(max).ToList();
        return close.Count > 0 ? close : sorted.Take(Math.Min(max, 4)).ToList();
    }
}
