namespace TrayTimes.Transit;

/// <summary>Real-time and planning data from a transit data provider.</summary>
public interface ITransitService
{
    /// <summary>Short provider name for display (e.g. "Transit").</summary>
    string ProviderName { get; }

    /// <summary>False when the service can't make requests (e.g. no API key was provided).</summary>
    bool IsConfigured { get; }

    /// <summary>The most stop IDs one <see cref="GetDeparturesAsync"/> call accepts.</summary>
    int MaxStopsPerRequest { get; }

    /// <summary>Optional bounded request observations; separate from saved endpoint attempts.</summary>
    TransitRequestDiagnostics? Diagnostics => null;

    /// <summary>
    /// Upcoming departures at the given stops (at most <see cref="MaxStopsPerRequest"/>), soonest
    /// first, scheduled and real-time together. A caching layer may ask for shapes on the caller's behalf.
    /// </summary>
    Task<DepartureBoard> GetDeparturesAsync(IReadOnlyCollection<string> stopIds, bool includeShapes = false, CancellationToken cancellationToken = default);

    /// <summary>Lines within <paramref name="radiusMeters"/> of a point, each direction with its closest stop.</summary>
    Task<NearbyLines> GetNearbyLinesAsync(GeoPoint point, int radiusMeters, CancellationToken cancellationToken = default);

    /// <summary>Ways to get from one point to another by transit, transfers included, leaving now.</summary>
    Task<TripPlan> PlanTripAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default);

    /// <summary>Stops near a point whose name or stop code matches <paramref name="query"/>, best match first.</summary>
    Task<IReadOnlyList<TransitStop>> SearchStopsAsync(GeoPoint near, string query, CancellationToken cancellationToken = default);

    /// <summary>Every route of the networks that serve a point.</summary>
    Task<IReadOnlyList<TransitRoute>> GetRoutesNearAsync(GeoPoint point, CancellationToken cancellationToken = default);

    /// <summary>A route's variations (direction and stops) and shapes.</summary>
    Task<RouteDetails> GetRouteDetailsAsync(string routeId, CancellationToken cancellationToken = default);

    /// <summary>Live vehicles on a route, optionally in one direction.</summary>
    Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(string routeId, string? directionId = null, CancellationToken cancellationToken = default);
}
