using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayTimes.Transit.BusTime;

// Wire format of the Clever Devices BusTime Developer API v3 (format=json). BusTime is loose
// about types (numbers and booleans sometimes arrive as strings), so the context below reads
// numbers from strings and uses lenient string/bool converters.

internal sealed class BusTimeEnvelope
{
    [JsonPropertyName("bustime-response")]
    public BusTimeResponse? Response { get; set; }
}

internal sealed class BusTimeResponse
{
    [JsonPropertyName("error")]
    public List<BusTimeError>? Errors { get; set; }

    [JsonPropertyName("routes")]
    public List<BusTimeRoute>? Routes { get; set; }

    [JsonPropertyName("directions")]
    public List<BusTimeDirection>? Directions { get; set; }

    [JsonPropertyName("stops")]
    public List<BusTimeStop>? Stops { get; set; }

    [JsonPropertyName("prd")]
    public List<BusTimePrediction>? Predictions { get; set; }

    [JsonPropertyName("vehicle")]
    public List<BusTimeVehicle>? Vehicles { get; set; }

    [JsonPropertyName("ptr")]
    public List<BusTimePattern>? Patterns { get; set; }
}

internal sealed class BusTimeError
{
    [JsonPropertyName("msg")]
    public string? Message { get; set; }
}

internal sealed class BusTimeRoute
{
    [JsonPropertyName("rt")]
    public string? Route { get; set; }

    [JsonPropertyName("rtnm")]
    public string? Name { get; set; }

    [JsonPropertyName("rtclr")]
    public string? Color { get; set; }

    [JsonPropertyName("rtdd")]
    public string? DisplayDesignator { get; set; }
}

internal sealed class BusTimeDirection
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class BusTimeStop
{
    [JsonPropertyName("stpid")]
    public string? StopId { get; set; }

    [JsonPropertyName("stpnm")]
    public string? Name { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

internal sealed class BusTimePrediction
{
    [JsonPropertyName("typ")]
    public string? Type { get; set; }

    [JsonPropertyName("stpid")]
    public string? StopId { get; set; }

    [JsonPropertyName("vid")]
    public string? VehicleId { get; set; }

    [JsonPropertyName("tatripid")]
    public string? TripId { get; set; }

    [JsonPropertyName("rt")]
    public string? Route { get; set; }

    [JsonPropertyName("rtdd")]
    public string? RouteDisplay { get; set; }

    [JsonPropertyName("rtdir")]
    public string? Direction { get; set; }

    [JsonPropertyName("des")]
    public string? Destination { get; set; }

    [JsonPropertyName("prdtm")]
    public string? PredictedTime { get; set; }

    [JsonPropertyName("prdctdn")]
    public string? Countdown { get; set; }

    [JsonPropertyName("dly")]
    public bool Delayed { get; set; }

    [JsonPropertyName("dyn")]
    public int DynamicAction { get; set; }
}

internal sealed class BusTimeVehicle
{
    [JsonPropertyName("vid")]
    public string? VehicleId { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("hdg")]
    public int Heading { get; set; }

    [JsonPropertyName("rt")]
    public string? Route { get; set; }

    [JsonPropertyName("des")]
    public string? Destination { get; set; }

    [JsonPropertyName("dly")]
    public bool Delayed { get; set; }
}

internal sealed class BusTimePattern
{
    [JsonPropertyName("pid")]
    public string? PatternId { get; set; }

    [JsonPropertyName("rtdir")]
    public string? Direction { get; set; }

    [JsonPropertyName("pt")]
    public List<BusTimePatternPoint>? Points { get; set; }
}

internal sealed class BusTimePatternPoint
{
    [JsonPropertyName("seq")]
    public int Sequence { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

/// <summary>Reads a JSON string, number, or boolean as a string.</summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => SkipAndReturnNull(ref reader),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

/// <summary>Reads a JSON boolean or a "true"/"false" string as a bool.</summary>
internal sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String => bool.TryParse(reader.GetString(), out bool b) && b,
        JsonTokenType.Number => reader.TryGetInt32(out int i) && i != 0,
        _ => false,
    };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    Converters = [typeof(LenientStringConverter), typeof(LenientBoolConverter)])]
[JsonSerializable(typeof(BusTimeEnvelope))]
internal sealed partial class BusTimeJsonContext : JsonSerializerContext;
