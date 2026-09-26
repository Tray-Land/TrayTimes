namespace TrayTimes.Transit;

/// <summary>Real-time data for one transit agency.</summary>
public interface ITransitService
{
    /// <summary>Short agency name for display (e.g. "MCTS").</summary>
    string AgencyName { get; }

    /// <summary>False when the service can't make requests (e.g. no API key was provided).</summary>
    bool IsConfigured { get; }

    /// <summary>The most stop IDs one <see cref="GetArrivalsAsync"/> call accepts.</summary>
    int MaxStopsPerRequest { get; }

    Task<IReadOnlyList<TransitRoute>> GetRoutesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransitDirection>> GetDirectionsAsync(string routeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransitStop>> GetStopsAsync(string routeId, string directionId, CancellationToken cancellationToken = default);

    /// <summary>Upcoming arrivals at the given stops, soonest first.</summary>
    Task<IReadOnlyList<Arrival>> GetArrivalsAsync(IReadOnlyCollection<string> stopIds, CancellationToken cancellationToken = default);

    /// <summary>Current positions of the given vehicles (at most <see cref="MaxStopsPerRequest"/>).</summary>
    Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(IReadOnlyCollection<string> vehicleIds, CancellationToken cancellationToken = default);

    /// <summary>The active shapes of a route, for drawing it on a map.</summary>
    Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId, CancellationToken cancellationToken = default);
}
