using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>A trip the flyout suggests: where the rider is, and where they're most likely headed next.</summary>
/// <param name="From">The favorite place the rider is at, if any.</param>
/// <param name="Origin">Where the trip starts.</param>
/// <param name="To">Where the rider is headed.</param>
/// <param name="IsGuess">True when the device location was unknown and the trip was guessed from the time of day.</param>
public sealed record CommuteLeg(FavoritePlace? From, GeoPoint Origin, FavoritePlace To, bool IsGuess)
{
    public bool IsAlreadyThere => GeoMath.DistanceMeters(Origin, To.Location) <= CommutePlanner.AtPlaceMeters;
}

/// <summary>A route in one direction of travel. IDs compare case-insensitively.</summary>
public readonly record struct RouteDirection(string RouteId, string DirectionId)
{
    public bool Equals(RouteDirection other) =>
        string.Equals(RouteId, other.RouteId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DirectionId, other.DirectionId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(RouteId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(DirectionId));
}

/// <summary>Works out the rider's likely next trip: away from home, head home; at home, head out.</summary>
public static class CommutePlanner
{
    /// <summary>How close the rider must be to a favorite place to count as being there.</summary>
    public const double AtPlaceMeters = 400;

    private const int MorningEndsHour = 12;

    /// <summary>Where the rider goes when they leave home: work, else school, else their first custom place.</summary>
    public static FavoritePlace? AwayPlace(IReadOnlyList<FavoritePlace> places) =>
        places.FirstOrDefault(p => p.Kind == PlaceKind.Work)
        ?? places.FirstOrDefault(p => p.Kind == PlaceKind.School)
        ?? places.FirstOrDefault(p => p.Kind == PlaceKind.Custom);

    /// <summary>The closest favorite place within <see cref="AtPlaceMeters"/> of <paramref name="point"/>.</summary>
    public static FavoritePlace? PlaceAt(IReadOnlyList<FavoritePlace> places, GeoPoint point) => places
        .Select(p => (Place: p, Meters: GeoMath.DistanceMeters(point, p.Location)))
        .Where(p => p.Meters <= AtPlaceMeters)
        .OrderBy(p => p.Meters)
        .Select(p => p.Place)
        .FirstOrDefault();

    /// <summary>
    /// The rider's likely next trip. At home, it heads to <see cref="AwayPlace"/>; anywhere else, it heads
    /// home. Without a location, mornings are assumed to start at home and afternoons to end there.
    /// Returns null when there's not enough to go on (no home, or nowhere to go from home).
    /// </summary>
    /// <param name="destination">Heads here instead of the likely place, when the rider has picked one.</param>
    public static CommuteLeg? Plan(IReadOnlyList<FavoritePlace> places, GeoPoint? here, DateTimeOffset now, FavoritePlace? destination = null)
    {
        FavoritePlace? home = places.FirstOrDefault(p => p.Kind == PlaceKind.Home);
        FavoritePlace? away = AwayPlace(places);

        if (here is { } point)
        {
            FavoritePlace? at = PlaceAt(places, point);
            destination ??= at?.Kind == PlaceKind.Home ? away : home;
            return destination is null ? null : new CommuteLeg(at, at?.Location ?? point, destination, IsGuess: false);
        }

        destination ??= now.Hour < MorningEndsHour ? away : home;
        FavoritePlace? from = destination?.Kind == PlaceKind.Home ? away : home;
        return destination is null || from is null ? null : new CommuteLeg(from, from.Location, destination, IsGuess: true);
    }
}
