using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>Walking times from Azure Maps Route Directions (pedestrian), used to pick the stop that's quickest to reach on foot.</summary>
internal static class WalkingRouteService
{
    private const string BaseUrl = "https://atlas.microsoft.com/route/directions/json";
    private const int MaxConcurrentRequests = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Throttle = new(MaxConcurrentRequests);
    private static readonly ConcurrentDictionary<string, WalkingRoute> Cache = new();

    /// <summary>False without an Azure Maps key; callers keep the straight-line estimate.</summary>
    public static bool IsAvailable => Secrets.AzureMapsKey is not null;

    /// <summary>
    /// Adds a walking time and distance to each stop, then orders them quickest first. Stops whose route
    /// can't be looked up keep their straight-line distance and sort after the routed ones.
    /// </summary>
    public static async Task<IReadOnlyList<NearbyStop>> RankByWalkAsync(
        GeoPoint origin,
        IReadOnlyList<NearbyStop> candidates,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || candidates.Count == 0)
        {
            return candidates;
        }

        NearbyStop[] routed = await Task.WhenAll(candidates.Select(async n =>
            await GetRouteAsync(origin, n.Stop.Location, cancellationToken) is { } route
                ? n with { WalkSeconds = route.Seconds, WalkMeters = route.Meters }
                : n));
        return NearbyStop.OrderByWalk(routed);
    }

    private static async Task<WalkingRoute?> GetRouteAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken)
    {
        if (Secrets.AzureMapsKey is not { } key)
        {
            return null;
        }

        // About a metre of precision, so a rider jittering in place reuses the same route.
        string cacheKey = string.Create(CultureInfo.InvariantCulture, $"{from.Lat:0.00001},{from.Lon:0.00001}>{to.Lat:0.00001},{to.Lon:0.00001}");
        if (Cache.TryGetValue(cacheKey, out WalkingRoute? cached))
        {
            return cached;
        }

        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}?api-version=1.0&travelMode=pedestrian&routeType=shortest&query={from.Lat},{from.Lon}:{to.Lat},{to.Lon}&subscription-key={Uri.EscapeDataString(key)}");

        await Throttle.WaitAsync(cancellationToken);
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("routes", out JsonElement routes)
                && routes.GetArrayLength() > 0
                && routes[0].TryGetProperty("summary", out JsonElement summary)
                && summary.TryGetProperty("travelTimeInSeconds", out JsonElement seconds)
                && summary.TryGetProperty("lengthInMeters", out JsonElement meters))
            {
                WalkingRoute route = new(seconds.GetDouble(), meters.GetDouble());
                Cache[cacheKey] = route;
                return route;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            && !cancellationToken.IsCancellationRequested)
        {
            // Offline, throttled or unroutable: the caller falls back to the straight-line estimate.
            return null;
        }
        finally
        {
            Throttle.Release();
        }
    }

    private sealed record WalkingRoute(double Seconds, double Meters);
}
