namespace TrayTimes.Transit;

// Provider-neutral transit models. Agency-specific clients (MCTS BusTime today, possibly
// Transit.app later) translate their wire formats into these.

public readonly record struct GeoPoint(double Lat, double Lon);

public sealed record TransitRoute(string Id, string DisplayId, string Name, string? Color)
{
    public override string ToString() => string.Equals(DisplayId, Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{DisplayId} · {Name}";
}

public sealed record TransitDirection(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record TransitStop(string Id, string Name, GeoPoint Location)
{
    public override string ToString() => Name;
}

public enum ArrivalStatus
{
    Scheduled,
    Canceled,
    DropOffOnly,
}

/// <summary>
/// A predicted arrival (or departure, at the start of a trip) of one vehicle at one stop. When
/// <see cref="IsRealTime"/> is false it comes from the published timetable, with no bus behind it yet.
/// </summary>
public sealed record Arrival(
    string StopId,
    string RouteId,
    string RouteDisplayId,
    string DirectionId,
    string Destination,
    string? VehicleId,
    DateTimeOffset PredictedTime,
    bool IsDue,
    bool IsDelayed,
    bool IsDeparture,
    ArrivalStatus Status,
    bool IsRealTime = true,
    string? TripId = null)
{
    /// <summary>Whole minutes until arrival, never negative.</summary>
    public int MinutesAway(DateTimeOffset now) => IsDue ? 0 : Math.Max(0, (int)Math.Floor((PredictedTime - now).TotalMinutes));
}

/// <summary>When the bus an <see cref="Arrival"/> belongs to is expected at a later stop on its trip.</summary>
public sealed record TripStop(string StopId, DateTimeOffset ArrivesAt);

public sealed record Vehicle(string Id, string RouteId, string Destination, GeoPoint Location, int Heading, bool IsDelayed);

/// <summary>A route variation's shape (waypoints and stops, in travel order).</summary>
public sealed record RoutePattern(string Id, string RouteId, string DirectionId, IReadOnlyList<GeoPoint> Points);

/// <summary>Raised when the transit service rejects a request (bad key, rate limit, and so on).</summary>
public sealed class TransitApiException(string message) : Exception(message);
