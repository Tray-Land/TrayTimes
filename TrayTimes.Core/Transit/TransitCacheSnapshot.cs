using System.Text.Json.Serialization;

namespace TrayTimes.Transit;

// What MetadataTransitService saves between runs, so a restart or flyout teardown never repeats
// discovery. Shapes are stored once, as encoded polylines; cached results don't repeat them.

public sealed record CachedAreaRoutes(GeoPoint Point, DateTimeOffset FetchedAt, IReadOnlyList<TransitRoute> Routes);

public sealed record CachedRouteDetails(DateTimeOffset FetchedAt, TransitRoute Route, IReadOnlyList<RouteItinerary> Itineraries);

public sealed record CachedNearbyLines(GeoPoint Point, int RadiusMeters, DateTimeOffset FetchedAt, IReadOnlyList<NearbyLine> Lines);

public sealed record CachedTripPlan(GeoPoint From, GeoPoint To, DateTimeOffset FetchedAt, IReadOnlyList<TripOption> Options);

public sealed record CachedShape(string Id, string RouteId, string DirectionId, string Polyline, DateTimeOffset FetchedAt);

/// <summary>A stop whose departures were last fetched with shapes at <see cref="CoveredAt"/>.</summary>
public sealed record CachedShapeCoverage(string StopId, DateTimeOffset CoveredAt);

public sealed record TransitCacheSnapshot(
    int Version,
    IReadOnlyList<CachedAreaRoutes> AreaRoutes,
    IReadOnlyList<CachedRouteDetails> RouteDetails,
    IReadOnlyList<CachedNearbyLines> NearbyLines,
    IReadOnlyList<CachedTripPlan> TripPlans,
    IReadOnlyList<CachedShape> Shapes,
    IReadOnlyList<CachedShapeCoverage> ShapeCoverage)
{
    public const int CurrentVersion = 1;
}

[JsonSerializable(typeof(TransitCacheSnapshot))]
public sealed partial class TransitCacheJsonContext : JsonSerializerContext;
