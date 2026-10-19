using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>Address search and reverse geocoding through Azure Maps Search (v1).</summary>
internal static class GeocodingService
{
    private const string BaseUrl = "https://atlas.microsoft.com/search/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Where to bias results: the rider's home, else wherever the flyout's map was left, else nowhere (worldwide).</summary>
    private static GeoPoint? BiasPoint =>
        SettingsService.Places.FirstOrDefault(p => p.Kind == PlaceKind.Home)?.Location
        ?? (SettingsService.FlyoutMapView is { } view ? new GeoPoint(view.Lat, view.Lon) : null);

    /// <summary>False without an Azure Maps key; callers should fall back to device location.</summary>
    public static bool IsAvailable => Secrets.AzureMapsKey is not null;

    public static async Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (Secrets.AzureMapsKey is not { } key || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        string bias = BiasPoint is { } point ? string.Create(CultureInfo.InvariantCulture, $"&lat={point.Lat}&lon={point.Lon}") : string.Empty;
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}address/json?api-version=1.0&typeahead=true&limit=5{bias}&query={Uri.EscapeDataString(query.Trim())}&subscription-key={Uri.EscapeDataString(key)}");
        using JsonDocument doc = await GetJsonAsync(url, cancellationToken);

        List<GeocodeResult> results = [];
        if (doc.RootElement.TryGetProperty("results", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.TryGetProperty("position", out JsonElement pos)
                    && pos.TryGetProperty("lat", out JsonElement lat)
                    && pos.TryGetProperty("lon", out JsonElement lon)
                    && item.TryGetProperty("address", out JsonElement address)
                    && address.TryGetProperty("freeformAddress", out JsonElement text)
                    && text.GetString() is { Length: > 0 } label)
                {
                    results.Add(new GeocodeResult(label, new GeoPoint(lat.GetDouble(), lon.GetDouble())));
                }
            }
        }

        return results;
    }

    /// <summary>The street address nearest <paramref name="point"/>, or null if none is found.</summary>
    public static async Task<string?> ReverseAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        if (Secrets.AzureMapsKey is not { } key)
        {
            return null;
        }

        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}address/reverse/json?api-version=1.0&query={point.Lat},{point.Lon}&subscription-key={Uri.EscapeDataString(key)}");
        using JsonDocument doc = await GetJsonAsync(url, cancellationToken);
        return doc.RootElement.TryGetProperty("addresses", out JsonElement items)
            && items.GetArrayLength() > 0
            && items[0].TryGetProperty("address", out JsonElement address)
            && address.TryGetProperty("freeformAddress", out JsonElement text)
                ? text.GetString()
                : null;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
