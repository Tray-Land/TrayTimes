namespace TrayTimes.Transit;

/// <summary>
/// Where a trip ends. A stop ID routes to that stop rather than its coordinates. Unsaved search
/// destinations are reusable in memory but must not be included in a persistent cache snapshot.
/// </summary>
public sealed record TripDestination(GeoPoint Location, string? StopId = null, bool Persist = true);
