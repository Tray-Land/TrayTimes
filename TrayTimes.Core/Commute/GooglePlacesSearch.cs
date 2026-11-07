using System.Globalization;
using System.Text.Json;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>
/// Request and response handling for Google Places API (New) Text Search. It finds businesses and landmarks
/// by name as well as street addresses. Only identity and location fields are requested (the cheap SKU).
/// </summary>
public static class GooglePlacesSearch
{
    public const string Endpoint = "https://places.googleapis.com/v1/places:searchText";
    public const string FieldMask = "places.displayName,places.shortFormattedAddress,places.formattedAddress,places.location";
    public const int MaxResults = 8;
    private const double BiasRadiusMeters = 30000;

    /// <summary>The JSON body for a text search, biased (not restricted) toward <paramref name="bias"/> when known.</summary>
    public static string BuildRequestBody(string query, GeoPoint? bias)
    {
        using MemoryStream body = new();
        using (Utf8JsonWriter w = new(body))
        {
            w.WriteStartObject();
            w.WriteString("textQuery", query);
            w.WriteNumber("maxResultCount", MaxResults);
            if (bias is { } point)
            {
                w.WriteStartObject("locationBias");
                w.WriteStartObject("circle");
                w.WriteStartObject("center");
                w.WriteNumber("latitude", point.Lat);
                w.WriteNumber("longitude", point.Lon);
                w.WriteEndObject();
                w.WriteNumber("radius", BiasRadiusMeters);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(body.ToArray());
    }

    public static IReadOnlyList<GeocodeResult> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        List<GeocodeResult> results = [];
        if (!doc.RootElement.TryGetProperty("places", out JsonElement places) || places.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (JsonElement place in places.EnumerateArray())
        {
            if (!place.TryGetProperty("location", out JsonElement loc)
                || !TryGetNumber(loc, "latitude", out double lat)
                || !TryGetNumber(loc, "longitude", out double lon))
            {
                continue;
            }

            string? name = place.TryGetProperty("displayName", out JsonElement dn) ? GetString(dn, "text") : null;
            string? address = GetString(place, "shortFormattedAddress") ?? GetString(place, "formattedAddress");
            string? label = Label(name, address);
            if (label is null)
            {
                continue;
            }

            // A named place shows its name over its address; a plain address (whose text already holds the name) stays one line.
            bool named = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(address)
                && !address.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase);
            results.Add(named
                ? new GeocodeResult(label, new GeoPoint(lat, lon), name!.Trim(), address!.Trim())
                : new GeocodeResult(label, new GeoPoint(lat, lon)));
        }

        return results;
    }

    /// <summary>The name and address together ("Cafe, 12 Main St"), or just the address when it already says the name.</summary>
    public static string? Label(string? name, string? address)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return name.Trim();
        }

        return address.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)
            ? address.Trim()
            : string.Create(CultureInfo.CurrentCulture, $"{name.Trim()}, {address.Trim()}");
    }

    /// <summary>The message Google puts in an error body, or null if there isn't one.</summary>
    public static string? ParseErrorMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out JsonElement error) ? GetString(error, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement n) && n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out value);
    }
}
