using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrayTimes.Transit.BusTime;

/// <summary>
/// <see cref="ITransitService"/> over a Clever Devices BusTime Developer API v3 server, such as
/// MCTS's realtime.ridemcts.com. Assumes a single-feed system (no rtpidatafeed parameter).
/// </summary>
public sealed class BusTimeTransitService : ITransitService
{
    private const int MaxIdsPerRequest = 10;

    // BusTime reports these when a query is valid but has nothing to return right now.
    private static readonly string[] EmptyResultMessages =
    [
        "No data found for parameter",
        "No arrival times",
        "No service scheduled",
    ];

    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string? _apiKey;
    private readonly TimeZoneInfo _agencyTimeZone;

    /// <param name="http">The client to send requests with; omit to share one app-wide client.</param>
    public BusTimeTransitService(string agencyName, Uri baseUri, string? apiKey, TimeZoneInfo agencyTimeZone, HttpClient? http = null)
    {
        AgencyName = agencyName;
        _baseUri = baseUri;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _agencyTimeZone = agencyTimeZone;
        _http = http ?? SharedHttp;
    }

    /// <summary>Milwaukee County Transit System.</summary>
    public static BusTimeTransitService CreateMcts(string? apiKey, HttpClient? http = null) => new(
        "MCTS",
        new Uri("https://realtime.ridemcts.com/bustime/api/v3/"),
        apiKey,
        FindTimeZone("America/Chicago"),
        http);

    public string AgencyName { get; }

    public bool IsConfigured => _apiKey is not null;

    public int MaxStopsPerRequest => MaxIdsPerRequest;

    public async Task<IReadOnlyList<TransitRoute>> GetRoutesAsync(CancellationToken cancellationToken = default)
    {
        BusTimeResponse response = await GetAsync("getroutes", [], cancellationToken);
        return (response.Routes ?? [])
            .Where(r => !string.IsNullOrEmpty(r.Route))
            .Select(r => new TransitRoute(r.Route!, NonEmpty(r.DisplayDesignator) ?? r.Route!, NonEmpty(r.Name) ?? r.Route!, NonEmpty(r.Color)))
            .ToList();
    }

    public async Task<IReadOnlyList<TransitDirection>> GetDirectionsAsync(string routeId, CancellationToken cancellationToken = default)
    {
        BusTimeResponse response = await GetAsync("getdirections", [("rt", routeId)], cancellationToken);
        return (response.Directions ?? [])
            .Where(d => !string.IsNullOrEmpty(d.Id))
            .Select(d => new TransitDirection(d.Id!, NonEmpty(d.Name) ?? d.Id!))
            .ToList();
    }

    public async Task<IReadOnlyList<TransitStop>> GetStopsAsync(string routeId, string directionId, CancellationToken cancellationToken = default)
    {
        BusTimeResponse response = await GetAsync("getstops", [("rt", routeId), ("dir", directionId)], cancellationToken);
        return (response.Stops ?? [])
            .Where(s => !string.IsNullOrEmpty(s.StopId))
            .Select(s => new TransitStop(s.StopId!, NonEmpty(s.Name) ?? s.StopId!, new GeoPoint(s.Lat, s.Lon)))
            .ToList();
    }

    public async Task<IReadOnlyList<Arrival>> GetArrivalsAsync(IReadOnlyCollection<string> stopIds, CancellationToken cancellationToken = default)
    {
        if (stopIds.Count == 0)
        {
            return [];
        }

        BusTimeResponse response = await GetAsync("getpredictions", [("stpid", JoinIds(stopIds))], cancellationToken);
        DateTimeOffset now = DateTimeOffset.Now;
        List<Arrival> arrivals = [];
        foreach (BusTimePrediction p in response.Predictions ?? [])
        {
            // 16/17: invalidated trips, which the API guide says must not be shown to riders.
            if (p.DynamicAction is 16 or 17 || string.IsNullOrEmpty(p.StopId) || string.IsNullOrEmpty(p.Route))
            {
                continue;
            }

            bool isDue = string.Equals(p.Countdown, "DUE", StringComparison.OrdinalIgnoreCase);
            DateTimeOffset predicted = ParseAgencyTime(p.PredictedTime)
                ?? (int.TryParse(p.Countdown, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ? now.AddMinutes(minutes) : now);

            arrivals.Add(new Arrival(
                p.StopId!,
                p.Route!,
                NonEmpty(p.RouteDisplay) ?? p.Route!,
                p.Direction ?? string.Empty,
                NonEmpty(p.Destination) ?? string.Empty,
                NonEmpty(p.VehicleId),
                predicted,
                isDue,
                p.Delayed || string.Equals(p.Countdown, "DLY", StringComparison.OrdinalIgnoreCase),
                string.Equals(p.Type, "D", StringComparison.OrdinalIgnoreCase),
                p.DynamicAction switch
                {
                    1 or 18 => ArrivalStatus.Canceled,
                    4 => ArrivalStatus.DropOffOnly,
                    _ => ArrivalStatus.Scheduled,
                },
                TripId: NonEmpty(p.TripId)));
        }

        arrivals.Sort((a, b) => a.PredictedTime.CompareTo(b.PredictedTime));
        return arrivals;
    }

    public async Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(IReadOnlyCollection<string> vehicleIds, CancellationToken cancellationToken = default)
    {
        if (vehicleIds.Count == 0)
        {
            return [];
        }

        BusTimeResponse response = await GetAsync("getvehicles", [("vid", JoinIds(vehicleIds))], cancellationToken);
        return (response.Vehicles ?? [])
            .Where(v => !string.IsNullOrEmpty(v.VehicleId) && double.IsFinite(v.Lat) && double.IsFinite(v.Lon))
            .Select(v => new Vehicle(v.VehicleId!, v.Route ?? string.Empty, v.Destination ?? string.Empty, new GeoPoint(v.Lat, v.Lon), v.Heading, v.Delayed))
            .ToList();
    }

    public async Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId, CancellationToken cancellationToken = default)
    {
        BusTimeResponse response = await GetAsync("getpatterns", [("rt", routeId)], cancellationToken);
        return (response.Patterns ?? [])
            .Where(p => p.Points is { Count: > 1 })
            .Select(p => new RoutePattern(
                p.PatternId ?? string.Empty,
                routeId,
                p.Direction ?? string.Empty,
                p.Points!.OrderBy(pt => pt.Sequence).Select(pt => new GeoPoint(pt.Lat, pt.Lon)).ToList()))
            .ToList();
    }

    private async Task<BusTimeResponse> GetAsync(string endpoint, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        if (_apiKey is null)
        {
            throw new TransitApiException($"No {AgencyName} API key. Add one to OAuth.resw and rebuild.");
        }

        StringBuilder query = new($"{endpoint}?format=json&key={Uri.EscapeDataString(_apiKey)}");
        foreach ((string name, string value) in parameters)
        {
            query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        using HttpResponseMessage http = await _http.GetAsync(new Uri(_baseUri, query.ToString()), cancellationToken);
        http.EnsureSuccessStatusCode();
        await using Stream stream = await http.Content.ReadAsStreamAsync(cancellationToken);

        BusTimeResponse response;
        try
        {
            response = (await JsonSerializer.DeserializeAsync(stream, BusTimeJsonContext.Default.BusTimeEnvelope, cancellationToken))?.Response
                ?? throw new TransitApiException($"{AgencyName} returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new TransitApiException($"{AgencyName} returned data this app couldn't read ({ex.Message}).");
        }

        // Errors can be per-ID alongside good data (e.g. one of several stops has no service).
        // Only fail when every error is a real one and there's no data at all.
        string? failure = response.Errors?
            .Select(e => e.Message)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && !EmptyResultMessages.Any(benign => m.Contains(benign, StringComparison.OrdinalIgnoreCase)));
        if (failure is not null && !HasData(response))
        {
            throw new TransitApiException(failure);
        }

        return response;
    }

    private static bool HasData(BusTimeResponse r) =>
        r.Routes is { Count: > 0 } || r.Directions is { Count: > 0 } || r.Stops is { Count: > 0 }
        || r.Predictions is { Count: > 0 } || r.Vehicles is { Count: > 0 } || r.Patterns is { Count: > 0 };

    /// <summary>Parses BusTime's "YYYYMMDD HH:MM[:SS]" agency-local timestamps.</summary>
    private DateTimeOffset? ParseAgencyTime(string? value)
    {
        if (value is null
            || !DateTime.TryParseExact(value, ["yyyyMMdd HH:mm", "yyyyMMdd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return null;
        }

        return new DateTimeOffset(local, _agencyTimeZone.GetUtcOffset(local));
    }

    private static string JoinIds(IReadOnlyCollection<string> ids) => string.Join(',', ids.Take(MaxIdsPerRequest));

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TrayTimes/1.0");
        return client;
    }
}
