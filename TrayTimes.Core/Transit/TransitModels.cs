using System.Text.Json.Serialization;

namespace TrayTimes.Transit;

// Provider-neutral transit models. The Transit API client (TransitApp/) translates its wire
// format into these.

public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>A line, such as a bus route or a metro line. <see cref="Mode"/> is what riders there call it ("Bus", "Métro").</summary>
public sealed record TransitRoute(string Id, string DisplayId, string Name, string? Color, string? Mode = null)
{
    public override string ToString() => string.Equals(DisplayId, Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{DisplayId} · {Name}";
}

/// <summary>One way along a route: <see cref="Id"/> is the provider's direction ("0" or "1"); <see cref="Name"/> is the sign riders see.</summary>
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
}

/// <summary>
/// One trip's departure from one stop. When <see cref="IsRealTime"/> is false the time comes from
/// the published timetable, with no live vehicle behind it yet.
/// </summary>
/// <param name="TripId">The provider's trip ID, which stays the same as a scheduled trip gains real-time data.</param>
/// <param name="ScheduledTime">The timetable time, for working out how late a real-time departure is running.</param>
/// <param name="Mode">What riders call this kind of vehicle, e.g. "Bus".</param>
public sealed record Arrival(
    string StopId,
    string RouteId,
    string RouteDisplayId,
    string DirectionId,
    string Destination,
    DateTimeOffset PredictedTime,
    ArrivalStatus Status = ArrivalStatus.Scheduled,
    bool IsRealTime = true,
    string? TripId = null,
    DateTimeOffset? ScheduledTime = null,
    string? RouteColor = null,
    string? Mode = null)
{
    /// <summary>How far behind its timetable a real-time departure must be to count as delayed.</summary>
    public static readonly TimeSpan DelayThreshold = TimeSpan.FromMinutes(2);

    /// <summary>How far behind its timetable this departure is running; null without real-time data or a timetable time.</summary>
    public TimeSpan? Delay => IsRealTime && ScheduledTime is { } scheduled ? PredictedTime - scheduled : null;

    public bool IsDelayed => Delay >= DelayThreshold;

    /// <summary>Whole minutes until departure, never negative.</summary>
    public int MinutesAway(DateTimeOffset now) => Math.Max(0, (int)Math.Floor((PredictedTime - now).TotalMinutes));
}

/// <summary>Departures at several stops, plus the route shapes that came with them when asked for.</summary>
public sealed record DepartureBoard(IReadOnlyList<Arrival> Arrivals, IReadOnlyList<RoutePattern> Shapes)
{
    public static DepartureBoard Empty { get; } = new([], []);
}

public enum VehicleOccupancy
{
    Unknown,
    NotCrowded,
    SomeCrowding,
    Crowded,
}

/// <summary>A live vehicle. <see cref="Heading"/> is degrees clockwise from north, or -1 when unknown.</summary>
/// <param name="TripId">The trip it's running, to match it with a departure.</param>
/// <param name="Label">The operator's name for the vehicle, such as its fleet number.</param>
public sealed record Vehicle(
    string Id,
    string RouteId,
    string DirectionId,
    GeoPoint Location,
    int Heading = -1,
    string? TripId = null,
    string? Label = null,
    VehicleOccupancy Occupancy = VehicleOccupancy.Unknown);

/// <summary>A route variation's shape, in travel order.</summary>
public sealed record RoutePattern(string Id, string RouteId, string DirectionId, IReadOnlyList<GeoPoint> Points);

/// <summary>One direction of a line and the stop nearest the searched point to catch it at.</summary>
public sealed record NearbyLine(TransitRoute Route, TransitDirection Direction, TransitStop Stop, double Meters);

/// <summary>The lines near a point, and the shapes to draw them with.</summary>
public sealed record NearbyLines(IReadOnlyList<NearbyLine> Lines, IReadOnlyList<RoutePattern> Shapes);

/// <summary>A route variation riders can pick a stop from, with its stops in travel order.</summary>
public sealed record RouteItinerary(TransitDirection Direction, IReadOnlyList<TransitStop> Stops);

public sealed record RouteDetails(TransitRoute Route, IReadOnlyList<RouteItinerary> Itineraries, IReadOnlyList<RoutePattern> Shapes);

/// <summary>A trip planner's whole trips for one origin and destination, best first, and the shapes of the lines they ride.</summary>
public sealed record TripPlan(IReadOnlyList<TripItinerary> Itineraries, IReadOnlyList<RoutePattern> Shapes);

/// <summary>One way to make a trip, leg by leg: leave at <see cref="Departs"/>, arrive at <see cref="Arrives"/>.</summary>
public sealed record TripItinerary(IReadOnlyList<TripLeg> Legs, DateTimeOffset Departs, DateTimeOffset Arrives)
{
    /// <summary>Changes from one ride to the next; a walk-only trip has none.</summary>
    [JsonIgnore]
    public int Transfers => Math.Max(0, Legs.OfType<RideLeg>().Count() - 1);
}

/// <summary>One part of a trip, from <see cref="Starts"/> to <see cref="Ends"/> as planned.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(WalkLeg), "walk")]
[JsonDerivedType(typeof(RideLeg), "ride")]
public abstract record TripLeg(DateTimeOffset Starts, DateTimeOffset Ends)
{
    [JsonIgnore]
    public TimeSpan Duration => Ends - Starts;
}

/// <summary>A walk of <see cref="Meters"/> along <see cref="Path"/> (empty when the plan has no path).</summary>
public sealed record WalkLeg(
    DateTimeOffset Starts,
    DateTimeOffset Ends,
    double Meters,
    [property: JsonConverter(typeof(EncodedPathJsonConverter))] IReadOnlyList<GeoPoint> Path) : TripLeg(Starts, Ends);

/// <summary>
/// A ride on any of <see cref="Lines"/>, which the planner offers as alternatives, soonest first: the
/// leg's planned times are for the first line's first departure.
/// </summary>
public sealed record RideLeg(DateTimeOffset Starts, DateTimeOffset Ends, IReadOnlyList<RideLine> Lines) : TripLeg(Starts, Ends);

/// <summary>
/// One line a ride can take: where it boards, where it gets off (null when the plan doesn't say), how
/// many stops it goes (0 when unknown), the ridden part of its shape, and its departures, soonest first.
/// </summary>
public sealed record RideLine(
    TransitRoute Route,
    TransitDirection Direction,
    TransitStop BoardAt,
    TransitStop? AlightAt,
    int Stops,
    [property: JsonConverter(typeof(EncodedPathJsonConverter))] IReadOnlyList<GeoPoint> Path,
    IReadOnlyList<PlannedDeparture> Departures);

/// <summary>One of the plan's departures for a ride: when it leaves, when it gets you off, and its trip.</summary>
public sealed record PlannedDeparture(DateTimeOffset Departs, DateTimeOffset Alights, bool IsRealTime, string? TripId);

/// <summary>Raised when the transit service rejects a request (bad key, rate limit, and so on).</summary>
public sealed class TransitApiException(string message, TransitApiFailureKind kind = TransitApiFailureKind.Other) : Exception(message)
{
    public TransitApiFailureKind Kind { get; } = kind;
}

/// <summary>How the app should treat a service-reported failure when deciding whether to retry.</summary>
public enum TransitApiFailureKind
{
    /// <summary>A problem with this one query (unknown stop, unreadable data); says nothing about the service's health.</summary>
    Other,

    /// <summary>The key is missing or rejected; retrying cannot help until the configuration changes.</summary>
    Authentication,

    /// <summary>The app held the request back to stay within its own call budget; nothing was sent.</summary>
    Budget,
}
