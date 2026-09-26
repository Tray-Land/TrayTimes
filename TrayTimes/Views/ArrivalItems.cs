using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Models;
using TrayTimes.Transit;
using Windows.UI;

namespace TrayTimes.Views;

/// <summary>One stop and its upcoming arrivals, as shown in the flyout list.</summary>
public sealed class StopGroupItem(
    string stopId,
    string title,
    string subtitle,
    GeoPoint location,
    IReadOnlyList<ArrivalItem> arrivals,
    string emptyText,
    IndexedStop? saveable = null,
    bool isSaved = false)
{
    public string StopId { get; } = stopId;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public GeoPoint Location { get; } = location;

    public IReadOnlyList<ArrivalItem> Arrivals { get; } = arrivals;

    public string EmptyText { get; } = emptyText;

    public Visibility EmptyVisibility => Arrivals.Count == 0 && EmptyText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The directory stop behind a nearby result, which the star button saves.</summary>
    public IndexedStop? Saveable { get; } = saveable;

    public bool IsSaved { get; } = isSaved;

    public Visibility SaveVisibility => Saveable is null ? Visibility.Collapsed : Visibility.Visible;

    public string SaveGlyph => IsSaved ? "" : ""; // FavoriteStarFill / FavoriteStar

    public string SaveLabel => IsSaved ? $"Remove {Title} from your stops" : $"Save {Title} to your stops";

    public string AutomationName => $"{Title}, {Subtitle}. Show on map";
}

/// <summary>One predicted arrival row.</summary>
public sealed class ArrivalItem
{
    public ArrivalItem(Arrival arrival, GeoPoint stopLocation, Color routeColor, DateTimeOffset now, bool tooSoonToWalk = false)
    {
        Arrival = arrival;
        StopLocation = stopLocation;
        RouteColor = routeColor;
        RouteBackground = new SolidColorBrush(routeColor);
        RouteForeground = new SolidColorBrush(MapHelpers.ContrastingText(routeColor));

        int minutes = arrival.MinutesAway(now);
        MinutesText = minutes == 0 ? "Due" : minutes.ToString(CultureInfo.CurrentCulture);
        UnitText = minutes == 0 ? string.Empty : "min";

        string clock = arrival.PredictedTime.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        string? status = arrival.Status switch
        {
            ArrivalStatus.Canceled => "Canceled",
            ArrivalStatus.DropOffOnly => "Drop-off only",
            _ when arrival.IsDelayed => "Delayed",
            _ when tooSoonToWalk => "Too soon to walk there",
            _ => null,
        };
        string kind = arrival.IsDeparture ? "Departs" : "Arrives";
        DetailText = status is null ? $"{kind} {clock}" : $"{kind} {clock} · {status}";
        RowOpacity = arrival.Status == ArrivalStatus.Canceled || tooSoonToWalk ? 0.5 : 1;

        string when = minutes == 0 ? "due now" : $"in {minutes} minutes";
        AutomationName = $"Route {RouteLabel} to {Destination}, {when}. {DetailText}";
    }

    public Arrival Arrival { get; }

    public GeoPoint StopLocation { get; }

    public Color RouteColor { get; }

    public string RouteLabel => Arrival.RouteDisplayId;

    public string Destination => string.IsNullOrEmpty(Arrival.Destination) ? $"Route {Arrival.RouteDisplayId}" : Arrival.Destination;

    public SolidColorBrush RouteBackground { get; }

    public SolidColorBrush RouteForeground { get; }

    public string MinutesText { get; }

    public string UnitText { get; }

    public string DetailText { get; }

    public double RowOpacity { get; }

    public string AutomationName { get; }
}
