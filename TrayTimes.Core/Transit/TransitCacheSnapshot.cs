using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayTimes.Transit;

// What MetadataTransitService saves between runs, so a restart or flyout teardown never repeats
// discovery. Shapes are stored once, as encoded polylines; cached results don't repeat them, except
// for a trip's own walk and ride paths, which are encoded the same way.

public sealed record CachedAreaRoutes(GeoPoint Point, DateTimeOffset FetchedAt, IReadOnlyList<TransitRoute> Routes);

public sealed record CachedRouteDetails(DateTimeOffset FetchedAt, TransitRoute Route, IReadOnlyList<RouteItinerary> Itineraries);

public sealed record CachedNearbyLines(GeoPoint Point, int RadiusMeters, DateTimeOffset FetchedAt, IReadOnlyList<NearbyLine> Lines);

/// <param name="Itineraries">Null in a plan saved by a build that kept only each trip's first ride; such plans aren't reused.</param>
public sealed record CachedTripPlan(GeoPoint From, GeoPoint To, DateTimeOffset FetchedAt, IReadOnlyList<TripItinerary>? Itineraries,
    string? DestinationStopId = null, TripTime Time = default);

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

/// <summary>Saves a path as an encoded polyline, as shapes are; anything but a string reads as no path.</summary>
internal sealed class EncodedPathJsonConverter : JsonConverter<IReadOnlyList<GeoPoint>>
{
    public override IReadOnlyList<GeoPoint> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return GeoMath.DecodePolyline(reader.GetString());
        }

        reader.Skip();
        return [];
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<GeoPoint> value, JsonSerializerOptions options) =>
        writer.WriteStringValue(GeoMath.EncodePolyline(value));
}

[JsonSerializable(typeof(TransitCacheSnapshot))]
public sealed partial class TransitCacheJsonContext : JsonSerializerContext;
