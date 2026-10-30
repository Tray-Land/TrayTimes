using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayTimes.Transit.TransitApp;

// Wire format of the Transit API v4 (external.transitapp.com). Only the fields the app reads are
// declared. Numbers are read as doubles (some integer fields arrive as decimals) and strings
// leniently, so a type change in one field can't fail a whole response.

internal sealed class DeparturesResponse
{
    public List<RouteDto>? RouteDepartures { get; set; }
}

internal sealed class NearbyRoutesResponse
{
    public List<RouteDto>? NearbyRoutes { get; set; }
}

internal sealed class RoutesResponse
{
    public List<RouteDto>? Routes { get; set; }
}

internal sealed class RouteDetailsResponse
{
    public RouteDto? Route { get; set; }

    public List<ItineraryDto>? Itineraries { get; set; }
}

internal sealed class NetworksResponse
{
    public List<NetworkDto>? Networks { get; set; }
}

internal sealed class SearchStopsResponse
{
    public List<StopDto>? Results { get; set; }
}

internal sealed class VehiclesResponse
{
    public List<VehicleDto>? Vehicles { get; set; }
}

internal sealed class PlanResponse
{
    public List<PlanResultDto>? Results { get; set; }
}

/// <summary>A route, as returned on its own or carrying departures (stop_departures, nearby_routes) or plan itineraries.</summary>
internal sealed class RouteDto
{
    public string? GlobalRouteId { get; set; }

    public string? RouteShortName { get; set; }

    public string? RouteLongName { get; set; }

    public string? RouteColor { get; set; }

    public string? ModeName { get; set; }

    public DisplayShortNameDto? RouteDisplayShortName { get; set; }

    public string? GlobalStopId { get; set; }

    public List<MergedItineraryDto>? MergedItineraries { get; set; }

    public List<ItineraryDto>? Itineraries { get; set; }
}

internal sealed class DisplayShortNameDto
{
    public List<string?>? Elements { get; set; }
}

internal sealed class MergedItineraryDto
{
    public double? DirectionId { get; set; }

    public StopDto? ClosestStop { get; set; }

    public List<ItineraryDto>? Itineraries { get; set; }

    public List<ScheduleItemDto>? ScheduleItems { get; set; }
}

internal sealed class ItineraryDto
{
    public double? DirectionId { get; set; }

    public string? Headsign { get; set; }

    public string? DirectionHeadsign { get; set; }

    public string? Shape { get; set; }

    public List<StopDto>? Stops { get; set; }

    public string? InternalItineraryId { get; set; }

    public bool? CanonicalItinerary { get; set; }

    public bool? IsActive { get; set; }

    public StopDto? ClosestStop { get; set; }

    public ItineraryPlanDetailsDto? PlanDetails { get; set; }
}

internal sealed class ItineraryPlanDetailsDto
{
    public double? StartStopOffset { get; set; }

    /// <summary>-1 when the ride continues on another trip without getting off.</summary>
    public double? EndStopOffset { get; set; }

    /// <summary>The encoded shape of just the ridden part, from the start offset to the end offset.</summary>
    public string? PlanShape { get; set; }

    public string? InternalItineraryId { get; set; }
}

internal sealed class ScheduleItemDto
{
    public double? DepartureTime { get; set; }

    public double? ScheduledDepartureTime { get; set; }

    public double? ArrivalTime { get; set; }

    public bool IsCancelled { get; set; }

    public bool IsRealTime { get; set; }

    public string? RtTripId { get; set; }

    public string? TripSearchKey { get; set; }

    public string? InternalItineraryId { get; set; }

    public string? GlobalStopId { get; set; }

    public SchedulePlanDetailsDto? PlanDetails { get; set; }
}

internal sealed class SchedulePlanDetailsDto
{
    public string? InternalItineraryId { get; set; }

    public string? GlobalRouteId { get; set; }

    /// <summary>The alighting stop's times.</summary>
    public ScheduleItemDto? ArrivalScheduleItem { get; set; }

    /// <summary>Every stop the ride visits, from boarding to alighting (both included).</summary>
    public List<ScheduleItemDto>? StopScheduleItems { get; set; }
}

internal sealed class StopDto
{
    public string? GlobalStopId { get; set; }

    public string? StopName { get; set; }

    public double? StopLat { get; set; }

    public double? StopLon { get; set; }

    public double? Distance { get; set; }
}

internal sealed class NetworkDto
{
    public string? NetworkId { get; set; }

    public bool NetworkInBeta { get; set; }
}

internal sealed class VehicleDto
{
    public string? VehicleId { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? DirectionId { get; set; }

    public string? VehicleLabel { get; set; }

    public double? OccupancyStatus { get; set; }

    public string? RtTripId { get; set; }
}

internal sealed class PlanResultDto
{
    public double? StartTime { get; set; }

    public double? EndTime { get; set; }

    public List<LegDto>? Legs { get; set; }
}

internal sealed class LegDto
{
    public string? LegMode { get; set; }

    public double? StartTime { get; set; }

    public double? EndTime { get; set; }

    public double? Duration { get; set; }

    public double? Distance { get; set; }

    public string? Polyline { get; set; }

    public List<ScheduleItemDto>? Departures { get; set; }

    public List<RouteDto>? Routes { get; set; }
}

/// <summary>Reads any JSON scalar as a string (numbers keep their text); objects and arrays read as null.</summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.StartObject or JsonTokenType.StartArray => SkipAndReturnNull(ref reader),
        _ => null,
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

/// <summary>Reads a JSON boolean, a "true"/"false" string, or a 0/1 number as a bool; anything else is false.</summary>
internal sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool HandleNull => true;

    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.String:
                return bool.TryParse(reader.GetString(), out bool b) && b;
            case JsonTokenType.Number:
                return reader.TryGetDouble(out double d) && d != 0;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return false;
            default:
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    Converters = [typeof(LenientStringConverter), typeof(LenientBoolConverter)])]
[JsonSerializable(typeof(DeparturesResponse))]
[JsonSerializable(typeof(NearbyRoutesResponse))]
[JsonSerializable(typeof(RoutesResponse))]
[JsonSerializable(typeof(RouteDetailsResponse))]
[JsonSerializable(typeof(NetworksResponse))]
[JsonSerializable(typeof(SearchStopsResponse))]
[JsonSerializable(typeof(VehiclesResponse))]
[JsonSerializable(typeof(PlanResponse))]
internal sealed partial class TransitAppJsonContext : JsonSerializerContext;
