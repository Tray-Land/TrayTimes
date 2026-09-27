using System.Text.Json.Serialization;
using TrayTimes.Transit;

namespace TrayTimes.Models;

public enum PlaceKind
{
    Home,
    Work,
    School,
    Custom,
}

/// <summary>
/// A place the user goes, such as home or work. Home, Work, and School are single slots whose
/// <see cref="Id"/> is the kind's name; custom places get a generated ID and a user-chosen name.
/// </summary>
public sealed record FavoritePlace(string Id, PlaceKind Kind, string Name, Place Place)
{
    [JsonIgnore]
    public GeoPoint Location => new(Place.Lat, Place.Lon);

    public static FavoritePlace ForKind(PlaceKind kind, Place place) => new(kind.ToString(), kind, kind.ToString(), place);
}
