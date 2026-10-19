using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>Where the board is centered: near the device, near a saved place or stop, or on every saved stop.</summary>
public abstract record BoardCenter
{
    /// <summary>Where the board looks for lines: <paramref name="here"/> near you, else the place or stop; null for every saved stop.</summary>
    public abstract GeoPoint? PointFrom(GeoPoint? here);

    /// <summary>
    /// Where the board actually centers. Near you needs the device's location: without one it falls
    /// back to near Home, then to every saved stop, and with neither it stays near you (which then
    /// asks for location). The other centers don't need a location.
    /// </summary>
    public BoardCenter Resolve(GeoPoint? here, IReadOnlyList<FavoritePlace> places, IReadOnlyList<SavedStop> saved)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(saved);
        if (this is not NearYou || here is not null)
        {
            return this;
        }

        return places.FirstOrDefault(p => p.Kind == PlaceKind.Home) is { } home ? new NearPlace(home)
            : saved.Count > 0 ? new AllSavedStops()
            : this;
    }
}

/// <summary>The board around the device's location.</summary>
public sealed record NearYou : BoardCenter
{
    public override GeoPoint? PointFrom(GeoPoint? here) => here;
}

/// <summary>The board around a saved place, such as home.</summary>
public sealed record NearPlace(FavoritePlace Place) : BoardCenter
{
    public override GeoPoint? PointFrom(GeoPoint? here) => Place.Location;
}

/// <summary>The board around a saved stop.</summary>
public sealed record NearStop(SavedStop Stop) : BoardCenter
{
    public override GeoPoint? PointFrom(GeoPoint? here) => new GeoPoint(Stop.Lat, Stop.Lon);
}

/// <summary>Every saved stop, wherever it is: the board without discovery.</summary>
public sealed record AllSavedStops : BoardCenter
{
    public override GeoPoint? PointFrom(GeoPoint? here) => null;
}
