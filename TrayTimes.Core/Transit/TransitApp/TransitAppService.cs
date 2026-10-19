using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TrayTimes.Transit.TransitApp;

/// <summary>
/// <see cref="ITransitService"/> over the Transit API v4 (external.transitapp.com). The key goes
/// in the <c>apiKey</c> header, never the URL. Every method makes exactly one request, except
/// <see cref="GetRoutesNearAsync"/>, which makes two (networks, then their routes).
/// </summary>
public sealed class TransitAppService : ITransitService
{
    public const int MaxStopIdsPerRequest = 100;

    /// <summary>Departures returned per route direction at each stop; the flyout shows up to six per row.</summary>
    public const int DeparturesPerDirection = 6;

    private const int MaxNetworksPerRequest = 20;
    private const int MaxSearchResults = 20;
    private const int TripOptions = 3;

    private static readonly Uri DefaultBaseUri = new("https://external.transitapp.com/v4/");
    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string? _apiKey;
    private readonly string? _language;

    /// <param name="language">Preferred language for names and headsigns (an Accept-Language value), when feeds offer one.</param>
    /// <param name="http">The client to send requests with; omit to share one app-wide client.</param>
    public TransitAppService(string? apiKey, string? language = null, HttpClient? http = null, TransitRequestDiagnostics? diagnostics = null, Uri? baseUri = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _language = string.IsNullOrWhiteSpace(language) ? null : language;
        _http = http ?? SharedHttp;
        _baseUri = baseUri ?? DefaultBaseUri;
        Diagnostics = diagnostics ?? new TransitRequestDiagnostics();
    }

    /// <summary>Raised with the endpoint name (e.g. "stop_departures") each time a request is sent, whether or not it succeeds. May fire on any thread.</summary>
    public event EventHandler<string>? EndpointCalled;

    public string ProviderName => "Transit";

    public bool IsConfigured => _apiKey is not null;

    public int MaxStopsPerRequest => MaxStopIdsPerRequest;

    public TransitRequestDiagnostics Diagnostics { get; }

    public async Task<DepartureBoard> GetDeparturesAsync(IReadOnlyCollection<string> stopIds, bool includeShapes = false, CancellationToken cancellationToken = default)
    {
        string[] ids = ValidateIds(stopIds);
        if (ids.Length == 0)
        {
            return DepartureBoard.Empty;
        }

        DeparturesResponse response = await GetAsync(
            "public/stop_departures", "stop_departures", TransitEndpoint.Departures, ids.Length, TransitAppJsonContext.Default.DeparturesResponse,
            [
                ("global_stop_ids", string.Join(',', ids)),
                ("max_num_departures", DeparturesPerDirection.ToString(CultureInfo.InvariantCulture)),
                ("exclude_terminal_arrivals", "true"),
                ("include_stops_and_shapes", includeShapes ? "true" : "false"),
            ],
            cancellationToken);

        HashSet<string> requested = new(ids, StringComparer.Ordinal);
        List<Arrival> arrivals = [];
        List<RoutePattern> shapes = [];
        foreach (RouteDto dto in response.RouteDepartures ?? [])
        {
            if (MapRoute(dto) is not { } route)
            {
                continue;
            }

            foreach (MergedItineraryDto merged in dto.MergedItineraries ?? [])
            {
                string? stopId = NonEmpty(dto.GlobalStopId) ?? NonEmpty(merged.ClosestStop?.GlobalStopId);
                if (stopId is null || !requested.Contains(stopId))
                {
                    continue;
                }

                AddDepartures(arrivals, stopId, route, merged);
                if (includeShapes)
                {
                    AddShapes(shapes, route, merged.Itineraries, DirectionOf(merged.DirectionId));
                }
            }
        }

        return new DepartureBoard(SortDistinct(arrivals), shapes);
    }

    public async Task<NearbyLines> GetNearbyLinesAsync(GeoPoint point, int radiusMeters, CancellationToken cancellationToken = default)
    {
        // Departures come from the shared stop_departures batch instead, so ask for as little as possible here.
        NearbyRoutesResponse response = await GetAsync(
            "public/nearby_routes", "nearby_routes", TransitEndpoint.NearbyRoutes, 1, TransitAppJsonContext.Default.NearbyRoutesResponse,
            [
                .. Location(point),
                ("max_distance", Math.Clamp(radiusMeters, 1, 1500).ToString(CultureInfo.InvariantCulture)),
                ("include_stops_and_shapes", "true"),
                ("should_update_realtime", "false"),
                ("max_num_departures", "1"),
            ],
            cancellationToken);

        List<NearbyLine> lines = [];
        List<RoutePattern> shapes = [];
        foreach (RouteDto dto in response.NearbyRoutes ?? [])
        {
            if (MapRoute(dto) is not { } route)
            {
                continue;
            }

            foreach (MergedItineraryDto merged in dto.MergedItineraries ?? [])
            {
                StopDto? closest = merged.ClosestStop ?? merged.Itineraries?.Select(i => i.ClosestStop).FirstOrDefault(s => s is not null);
                if (MapStop(closest) is not { } stop)
                {
                    continue;
                }

                string directionId = DirectionOf(merged.DirectionId ?? merged.Itineraries?.FirstOrDefault()?.DirectionId);
                lines.Add(new NearbyLine(route, new TransitDirection(directionId, DirectionName(merged.Itineraries?.FirstOrDefault(), route)), stop,
                    closest!.Distance is { } meters && double.IsFinite(meters) ? meters : GeoMath.DistanceMeters(point, stop.Location)));
                AddShapes(shapes, route, merged.Itineraries, directionId);
            }
        }

        return new NearbyLines(lines, shapes);
    }

    public async Task<TripPlan> PlanTripAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default)
    {
        PlanResponse response = await GetAsync(
            "public/plan", "plan", TransitEndpoint.Plan, 1, TransitAppJsonContext.Default.PlanResponse,
            [
                ("from_lat", Format(from.Lat)), ("from_lon", Format(from.Lon)),
                ("to_lat", Format(to.Lat)), ("to_lon", Format(to.Lon)),
                ("mode", "transit"),
                ("num_result", TripOptions.ToString(CultureInfo.InvariantCulture)),
                ("max_num_departures", "3"),
                ("should_update_realtime", "true"),
            ],
            cancellationToken);

        List<TripOption> options = [];
        List<RoutePattern> shapes = [];
        foreach (PlanResultDto result in response.Results ?? [])
        {
            List<LegDto> legs = result.Legs ?? [];
            int first = legs.FindIndex(l => string.Equals(l.LegMode, "transit", StringComparison.OrdinalIgnoreCase));
            if (first < 0 || result.EndTime is not { } end)
            {
                continue; // A walk-only plan; the flyout lists transit departures.
            }

            List<LegDto> before = [.. legs.Take(first)];
            LegDto leg = legs[first];
            int transfers = legs.Count(l => string.Equals(l.LegMode, "transit", StringComparison.OrdinalIgnoreCase)) - 1;
            double afterLeg = leg.EndTime is { } legEnd ? end - legEnd : 0;
            double walkSeconds = before.Sum(l => l.Duration ?? 0);
            double walkMeters = before.Sum(l => l.Distance ?? 0);
            GeoPoint? walkEnd = before.Count > 0 && GeoMath.DecodePolyline(before[^1].Polyline) is { Count: > 0 } path ? path[^1] : null;

            foreach (RouteDto routeDto in leg.Routes ?? [])
            {
                if (MapRoute(routeDto) is not { } route)
                {
                    continue;
                }

                ScheduleItemDto? departure = (leg.Departures ?? [])
                    .Where(d => d.DepartureTime is not null
                        && (d.PlanDetails?.GlobalRouteId is null || string.Equals(d.PlanDetails.GlobalRouteId, route.Id, StringComparison.Ordinal)))
                    .MinBy(d => d.DepartureTime);
                ItineraryDto? itinerary = routeDto.Itineraries?.FirstOrDefault(i =>
                    departure?.PlanDetails?.InternalItineraryId is not { } id || i.InternalItineraryId == id || i.PlanDetails?.InternalItineraryId == id)
                    ?? routeDto.Itineraries?.FirstOrDefault();
                if (departure is null || itinerary is null || BoardingStop(itinerary, departure, walkEnd) is not { } stop)
                {
                    continue;
                }

                double alight = departure.PlanDetails?.ArrivalScheduleItem is { } alighting
                    ? alighting.DepartureTime ?? alighting.ArrivalTime ?? leg.EndTime ?? end
                    : leg.EndTime ?? end;
                string directionId = DirectionOf(itinerary.DirectionId);
                options.Add(new TripOption(
                    stop,
                    route,
                    new TransitDirection(directionId, DirectionName(itinerary, route)),
                    walkMeters,
                    TimeSpan.FromSeconds(walkSeconds),
                    FromUnix(departure.DepartureTime!.Value),
                    FromUnix(alight + afterLeg),
                    transfers));
            }

            foreach (LegDto transit in legs.Where(l => string.Equals(l.LegMode, "transit", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (RouteDto routeDto in transit.Routes ?? [])
                {
                    if (MapRoute(routeDto) is { } route)
                    {
                        foreach (ItineraryDto itinerary in routeDto.Itineraries ?? [])
                        {
                            AddShapes(shapes, route, [itinerary], DirectionOf(itinerary.DirectionId));
                        }
                    }
                }
            }
        }

        return new TripPlan(options, shapes);
    }

    public async Task<IReadOnlyList<TransitStop>> SearchStopsAsync(GeoPoint near, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        SearchStopsResponse response = await GetAsync(
            "public/search_stops", "search_stops", TransitEndpoint.SearchStops, 1, TransitAppJsonContext.Default.SearchStopsResponse,
            [
                .. Location(near),
                ("query", query.Trim()),
                ("max_num_results", MaxSearchResults.ToString(CultureInfo.InvariantCulture)),
                ("pickup_dropoff_filter", "PickupAllowedOnly"),
            ],
            cancellationToken);

        return [.. (response.Results ?? []).Select(MapStop).OfType<TransitStop>().DistinctBy(s => s.Id, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<TransitRoute>> GetRoutesNearAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        NetworksResponse networks = await GetAsync(
            "public/available_networks", "available_networks", TransitEndpoint.Networks, 1, TransitAppJsonContext.Default.NetworksResponse,
            [.. Location(point), ("include_network_geometry", "false")],
            cancellationToken);

        string[] ids = [.. (networks.Networks ?? [])
            .Where(n => !n.NetworkInBeta)
            .Select(n => NonEmpty(n.NetworkId))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaxNetworksPerRequest)];
        if (ids.Length == 0)
        {
            return [];
        }

        RoutesResponse routes = await GetAsync(
            "public/routes_for_networks", "routes_for_networks", TransitEndpoint.Routes, ids.Length, TransitAppJsonContext.Default.RoutesResponse,
            [("network_ids", string.Join(',', ids)), .. Location(point)],
            cancellationToken);

        return [.. (routes.Routes ?? []).Select(MapRoute).OfType<TransitRoute>().DistinctBy(r => r.Id, StringComparer.Ordinal)];
    }

    public async Task<RouteDetails> GetRouteDetailsAsync(string routeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        RouteDetailsResponse response = await GetAsync(
            "public/route_details", "route_details", TransitEndpoint.RouteDetails, 1, TransitAppJsonContext.Default.RouteDetailsResponse,
            [("global_route_id", routeId)],
            cancellationToken);

        TransitRoute route = MapRoute(response.Route) ?? throw new TransitApiException("Transit returned a route without an ID.");
        List<ItineraryDto> all = response.Itineraries ?? [];
        List<ItineraryDto> shown = [.. all.Where(i => i.CanonicalItinerary == true && i.IsActive != false)];
        if (shown.Count == 0)
        {
            shown = [.. all.Where(i => i.IsActive != false)];
        }

        List<RouteItinerary> itineraries = [];
        foreach (ItineraryDto itinerary in shown)
        {
            TransitDirection direction = new(DirectionOf(itinerary.DirectionId), DirectionName(itinerary, route));
            TransitStop[] stops = [.. (itinerary.Stops ?? []).Select(MapStop).OfType<TransitStop>()];
            int existing = itineraries.FindIndex(i => i.Direction == direction);
            if (existing < 0)
            {
                itineraries.Add(new RouteItinerary(direction, stops));
            }
            else if (stops.Length > itineraries[existing].Stops.Count)
            {
                itineraries[existing] = new RouteItinerary(direction, stops);
            }
        }

        List<RoutePattern> shapes = [];
        foreach (ItineraryDto itinerary in all)
        {
            AddShapes(shapes, route, [itinerary], DirectionOf(itinerary.DirectionId));
        }

        return new RouteDetails(route, itineraries, shapes);
    }

    public async Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(string routeId, string? directionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        (string Name, string Value)[] parameters = directionId is "0" or "1"
            ? [("global_route_id", routeId), ("direction_id", directionId)]
            : [("global_route_id", routeId)];
        VehiclesResponse response = await GetAsync(
            "vehicles", "vehicles", TransitEndpoint.Vehicles, 1, TransitAppJsonContext.Default.VehiclesResponse, parameters, cancellationToken);

        return [.. (response.Vehicles ?? [])
            .Where(v => NonEmpty(v.VehicleId) is not null
                && v.Latitude is { } lat && double.IsFinite(lat) && lat is >= -90 and <= 90
                && v.Longitude is { } lon && double.IsFinite(lon) && lon is >= -180 and <= 180)
            .Select(v => new Vehicle(
                v.VehicleId!.Trim(),
                routeId,
                v.DirectionId is { } d ? DirectionOf(d) : directionId ?? string.Empty,
                new GeoPoint(v.Latitude!.Value, v.Longitude!.Value),
                TripId: NonEmpty(v.RtTripId),
                Label: NonEmpty(v.VehicleLabel),
                Occupancy: (int?)v.OccupancyStatus switch
                {
                    1 => VehicleOccupancy.NotCrowded,
                    2 => VehicleOccupancy.SomeCrowding,
                    3 => VehicleOccupancy.Crowded,
                    _ => VehicleOccupancy.Unknown,
                }))
            .DistinctBy(v => v.Id, StringComparer.Ordinal)];
    }

    private static void AddDepartures(List<Arrival> arrivals, string stopId, TransitRoute route, MergedItineraryDto merged)
    {
        Dictionary<string, ItineraryDto> byId = (merged.Itineraries ?? [])
            .Where(i => !string.IsNullOrEmpty(i.InternalItineraryId))
            .DistinctBy(i => i.InternalItineraryId)
            .ToDictionary(i => i.InternalItineraryId!, StringComparer.Ordinal);
        ItineraryDto? fallback = merged.Itineraries?.FirstOrDefault();
        foreach (ScheduleItemDto item in merged.ScheduleItems ?? [])
        {
            if ((item.DepartureTime ?? item.ScheduledDepartureTime) is not { } time || !double.IsFinite(time))
            {
                continue;
            }

            ItineraryDto? itinerary = item.InternalItineraryId is { } id && byId.TryGetValue(id, out ItineraryDto? match) ? match : fallback;
            arrivals.Add(new Arrival(
                stopId,
                route.Id,
                route.DisplayId,
                DirectionOf(merged.DirectionId ?? itinerary?.DirectionId),
                NonEmpty(itinerary?.Headsign) ?? NonEmpty(itinerary?.DirectionHeadsign) ?? string.Empty,
                FromUnix(time),
                item.IsCancelled ? ArrivalStatus.Canceled : ArrivalStatus.Scheduled,
                item.IsRealTime,
                NonEmpty(item.RtTripId) ?? NonEmpty(item.TripSearchKey),
                item.ScheduledDepartureTime is { } scheduled && double.IsFinite(scheduled) ? FromUnix(scheduled) : null,
                route.Color,
                route.Mode));
        }
    }

    private static void AddShapes(List<RoutePattern> shapes, TransitRoute route, IEnumerable<ItineraryDto>? itineraries, string directionId)
    {
        foreach (ItineraryDto itinerary in itineraries ?? [])
        {
            IReadOnlyList<GeoPoint> points = GeoMath.DecodePolyline(itinerary.Shape);
            if (points.Count < 2)
            {
                continue;
            }

            string direction = itinerary.DirectionId is { } own ? DirectionOf(own) : directionId;
            string id = NonEmpty(itinerary.InternalItineraryId) ?? $"{route.Id}|{direction}|{shapes.Count}";
            if (!shapes.Any(s => s.RouteId == route.Id && s.Id == id))
            {
                shapes.Add(new RoutePattern(id, route.Id, direction, points));
            }
        }
    }

    /// <summary>Where a plan's first ride starts: the stop its departure lists first, else its offset in the itinerary's stops.</summary>
    private static TransitStop? BoardingStop(ItineraryDto itinerary, ScheduleItemDto departure, GeoPoint? walkEnd)
    {
        List<StopDto> stops = itinerary.Stops ?? [];
        string? id = NonEmpty(departure.PlanDetails?.StopScheduleItems?.FirstOrDefault()?.GlobalStopId) ?? NonEmpty(departure.GlobalStopId);
        StopDto? found = id is null ? null : stops.FirstOrDefault(s => s.GlobalStopId == id);
        if (found is null && itinerary.PlanDetails?.StartStopOffset is { } offset && offset >= 0 && offset < stops.Count)
        {
            found = stops[(int)offset];
        }

        if (MapStop(found) is { } stop)
        {
            return stop;
        }

        // The stop list can be abridged; the walk there still says where it is.
        return id is not null && walkEnd is { } point ? new TransitStop(id, string.Empty, point) : null;
    }

    private static TransitRoute? MapRoute(RouteDto? dto)
    {
        if (NonEmpty(dto?.GlobalRouteId) is not { } id)
        {
            return null;
        }

        string? display = NonEmpty(dto!.RouteDisplayShortName?.Elements is { Count: 3 } elements ? elements[1] : null)
            ?? NonEmpty(dto.RouteShortName);
        string? name = NonEmpty(dto.RouteLongName);
        return new TransitRoute(id, display ?? name ?? id, name ?? display ?? id, NormalizeColor(dto.RouteColor), NonEmpty(dto.ModeName));
    }

    private static TransitStop? MapStop(StopDto? dto)
    {
        if (NonEmpty(dto?.GlobalStopId) is not { } id
            || dto!.StopLat is not { } lat || !double.IsFinite(lat) || lat is < -90 or > 90
            || dto.StopLon is not { } lon || !double.IsFinite(lon) || lon is < -180 or > 180)
        {
            return null;
        }

        return new TransitStop(id, NonEmpty(dto.StopName) ?? id, new GeoPoint(lat, lon));
    }

    private static string DirectionName(ItineraryDto? itinerary, TransitRoute route) =>
        NonEmpty(itinerary?.DirectionHeadsign) ?? NonEmpty(itinerary?.Headsign) ?? route.Name;

    private static string DirectionOf(double? directionId) =>
        directionId is { } d && double.IsFinite(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"f37d00" or "#F37D00" as "#f37d00"; anything else as null.</summary>
    private static string? NormalizeColor(string? color)
    {
        string? hex = NonEmpty(color)?.TrimStart('#');
        return hex is { Length: 6 } && hex.All(char.IsAsciiHexDigit) ? $"#{hex.ToLowerInvariant()}" : null;
    }

    private static IReadOnlyList<Arrival> SortDistinct(List<Arrival> arrivals)
    {
        List<Arrival> distinct = [.. arrivals.DistinctBy(a => (a.StopId, a.RouteId, a.DirectionId, a.TripId, a.PredictedTime))];
        distinct.Sort((a, b) => a.PredictedTime.CompareTo(b.PredictedTime));
        return distinct;
    }

    private static DateTimeOffset FromUnix(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));

    private static (string Name, string Value)[] Location(GeoPoint point) => [("lat", Format(point.Lat)), ("lon", Format(point.Lon))];

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(
        string path,
        string endpointName,
        TransitEndpoint diagnosticEndpoint,
        int batchSize,
        JsonTypeInfo<T> typeInfo,
        (string Name, string Value)[] parameters,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_apiKey is null)
        {
            throw new TransitApiException("No Transit API key. Add TransitApiKey to OAuth.resw and rebuild.", TransitApiFailureKind.Authentication);
        }

        StringBuilder query = new(path);
        for (int i = 0; i < parameters.Length; i++)
        {
            query.Append(i == 0 ? '?' : '&').Append(parameters[i].Name).Append('=').Append(Uri.EscapeDataString(parameters[i].Value));
        }

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_baseUri, query.ToString()));
        request.Headers.TryAddWithoutValidation("apiKey", _apiKey);
        if (_language is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", _language);
        }

        TransitRequestDiagnostics.Attempt attempt = Diagnostics.Start(diagnosticEndpoint, batchSize);
        TransitRequestOutcome outcome = TransitRequestOutcome.Failure;
        int? httpStatus = null;
        try
        {
            EndpointCalled?.Invoke(this, endpointName);
            using HttpResponseMessage http = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            httpStatus = (int)http.StatusCode;
            ThrowIfRateLimited(http);
            ThrowIfRejected(http.StatusCode);
            http.EnsureSuccessStatusCode();
            await using Stream stream = await http.Content.ReadAsStreamAsync(cancellationToken);

            T response;
            try
            {
                response = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
                    ?? throw new TransitApiException($"{ProviderName} returned an empty response.");
            }
            catch (JsonException ex)
            {
                throw new TransitApiException($"{ProviderName} returned data this app couldn't read ({ex.Message}).");
            }

            outcome = TransitRequestOutcome.Success;
            return response;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? TransitRequestOutcome.Canceled : TransitRequestOutcome.Timeout;
            throw;
        }
        catch (HttpRequestException ex)
        {
            httpStatus ??= ex.StatusCode is { } status ? (int)status : null;
            outcome = httpStatus is null ? TransitRequestOutcome.TransportFailure : TransitRequestOutcome.HttpFailure;
            throw;
        }
        catch (TransitApiException)
        {
            outcome = TransitRequestOutcome.ApiFailure;
            throw;
        }
        finally
        {
            Diagnostics.Complete(attempt, outcome, httpStatus);
        }
    }

    private static void ThrowIfRateLimited(HttpResponseMessage http)
    {
        System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter = http.Headers.RetryAfter;
        TimeSpan? wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (http.StatusCode == HttpStatusCode.TooManyRequests
            || (http.StatusCode == HttpStatusCode.ServiceUnavailable && wait is not null))
        {
            throw new TransitRateLimitException(http.StatusCode, wait);
        }
    }

    /// <summary>A rejected key needs a configuration change; a rejected query (unknown stop or route) says nothing about the service.</summary>
    private void ThrowIfRejected(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TransitApiException($"{ProviderName} rejected the API key (HTTP {(int)status}). Check TransitApiKey in OAuth.resw.", TransitApiFailureKind.Authentication);
        }

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new TransitApiException($"{ProviderName} couldn't answer that request (HTTP {(int)status}).");
        }
    }

    private static string[] ValidateIds(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("IDs must not be empty.", nameof(ids));
        }

        string[] distinct = [.. ids.Distinct(StringComparer.Ordinal)];
        if (distinct.Length > MaxStopIdsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(ids), "Split IDs into batches within the provider's request limit.");
        }

        return distinct;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TrayTimes/2.0");
        return client;
    }
}
