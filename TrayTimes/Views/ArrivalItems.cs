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

    /// <summary>The directory stop behind a Commute or Nearby result, which the star button adds to favorites.</summary>
    public IndexedStop? Saveable { get; } = saveable;

    public bool IsSaved { get; } = isSaved;

    public Visibility SaveVisibility => Saveable is null ? Visibility.Collapsed : Visibility.Visible;

    public string SaveGlyph => IsSaved ? "" : ""; // FavoriteStarFill / FavoriteStar

    public string SaveLabel => IsSaved ? $"Remove {Title} from favorites" : $"Add {Title} to favorites";

    public string AutomationName => $"{Title}, {Subtitle}. Show on map";

    /// <summary>Identifies the stop header across list rebuilds, so only new ones animate in.</summary>
    public string GroupKey => $"stop|{StopId}";

    /// <summary>Identifies the star across list rebuilds, so one just clicked can pop.</summary>
    public string StarKey => $"star|{StopId}";

    public IEnumerable<string> Keys => Arrivals.Select(a => a.Key).Prepend(GroupKey);

    /// <summary>Everything the list shows for this stop; equal signatures mean the list needn't be rebuilt.</summary>
    public string Signature => $"{GroupKey}|{Title}|{Subtitle}|{IsSaved}|{EmptyText}|{string.Join(';', Arrivals.Select(a => a.Signature))}";
}

/// <summary>
/// One bus in a route row's strip of times: a chip with its own bell, so any upcoming bus (not
/// just the next one) can be tracked.
/// </summary>
public sealed class BusTimeItem
{
    public BusTimeItem(Arrival arrival, DateTimeOffset now, Arrival? tracked)
    {
        Arrival = arrival;
        IsTracked = arrival == tracked;
        int minutes = arrival.MinutesAway(now);
        MinutesText = minutes == 0 ? "Due" : $"{minutes} min";
        ClockText = arrival.PredictedTime.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        string kind = arrival.IsRealTime ? string.Empty : ", scheduled";
        TrackLabel = !CanTrack ? $"Route {RouteLabel} at {ClockText}{kind}, canceled"
            : IsTracked ? $"Stop tracking the {ClockText} route {RouteLabel} bus"
            : $"Track the {ClockText} route {RouteLabel} bus{kind}, in {MinutesText}, and notify me when it's time to leave";
    }

    public Arrival Arrival { get; }

    public string RouteLabel => Arrival.RouteDisplayId;

    /// <summary>"12 min", or "Due".</summary>
    public string MinutesText { get; }

    public string ClockText { get; }

    public bool IsTracked { get; }

    public bool CanTrack => Arrival.Status != ArrivalStatus.Canceled;

    /// <summary>Scheduled-only times are dimmed a little to set them apart from predictions.</summary>
    public double ChipOpacity => Arrival.IsRealTime ? 1 : 0.8;

    public string TrackLabel { get; }

    public Visibility TrackOutlineVisibility => IsTracked ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TrackedBellVisibility => IsTracked ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Identifies this bus at this stop across list rebuilds (a trip keeps its vehicle; a scheduled time has none yet).</summary>
    public string Key => $"{Arrival.StopId}|{Arrival.RouteId}|{Arrival.DirectionId}|{Arrival.VehicleId ?? $"t{Arrival.PredictedTime.ToUnixTimeSeconds() / 60}"}";

    public string Signature => $"{Key}|{MinutesText}|{ClockText}|{IsTracked}|{CanTrack}|{ChipOpacity}";
}

/// <summary>One route's row: its next bus (a prediction, or a scheduled time with no prediction yet), then a strip of its upcoming times.</summary>
public sealed class ArrivalItem
{
    /// <param name="later">The same route's buses after <paramref name="arrival"/>, soonest first.</param>
    /// <param name="tracked">The bus in tracking mode, if any, whose chip shows a gold bell.</param>
    /// <param name="destinationName">Where the rider is headed, with <paramref name="destinationEta"/> when they'd get there on this bus.</param>
    public ArrivalItem(
        Arrival arrival,
        GeoPoint stopLocation,
        Color routeColor,
        DateTimeOffset now,
        IReadOnlyList<Arrival>? later = null,
        Arrival? tracked = null,
        bool tooSoonToWalk = false,
        string? destinationName = null,
        DateTimeOffset? destinationEta = null)
    {
        Arrival = arrival;

        // Canceled buses after the next one aren't coming, so they're left out of the strip.
        Times = [new BusTimeItem(arrival, now, tracked), .. (later ?? []).Where(a => a.Status != ArrivalStatus.Canceled).Select(a => new BusTimeItem(a, now, tracked))];
        StopLocation = stopLocation;
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
        string kind = (arrival.IsRealTime, arrival.IsDeparture) switch
        {
            (false, true) => "Scheduled to depart",
            (false, false) => "Scheduled",
            (true, true) => "Departs",
            (true, false) => "Arrives",
        };
        DetailText = status is null ? $"{kind} {clock}" : $"{kind} {clock} · {status}";
        RowOpacity = arrival.Status == ArrivalStatus.Canceled || tooSoonToWalk ? 0.5 : 1;

        if (destinationEta is { } eta && destinationName is { Length: > 0 })
        {
            TripEtaText = $"{destinationName} by {eta.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
        }

        string when = minutes == 0 ? "due now" : $"in {minutes} minutes";
        string trip = TripEtaText.Length > 0 ? $" {TripEtaText}." : string.Empty;
        AutomationName = $"Route {RouteLabel} to {Destination}, {when}. {DetailText}.{trip} Show on map";
        TimesAutomationName = $"Upcoming route {RouteLabel} buses";
    }

    /// <summary>The chips under the row: this bus, then the route's later ones at the same stop.</summary>
    public IReadOnlyList<BusTimeItem> Times { get; }

    public string TimesAutomationName { get; }

    /// <summary>When the rider would reach where they're headed on this bus, e.g. "Home by 5:42 PM"; empty if unknown.</summary>
    public string TripEtaText { get; } = string.Empty;

    public Visibility TripEtaVisibility => TripEtaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Arrival Arrival { get; }

    public GeoPoint StopLocation { get; }

    public string RouteLabel => Arrival.RouteDisplayId;

    public string Destination => string.IsNullOrEmpty(Arrival.Destination) ? $"Route {Arrival.RouteDisplayId}" : Arrival.Destination;

    public SolidColorBrush RouteBackground { get; }

    public SolidColorBrush RouteForeground { get; }

    public string MinutesText { get; }

    public string UnitText { get; }

    public string DetailText { get; }

    public double RowOpacity { get; }

    /// <summary>Scheduled-only times are dimmed a little to set them apart from predictions.</summary>
    public double EtaOpacity => Arrival.IsRealTime ? 1 : 0.85;

    public string AutomationName { get; }

    /// <summary>
    /// Identifies this route's row at this stop across list rebuilds; it lists one row per route and
    /// direction, so the row stays put (and its minutes pop) as one bus leaves and the next takes the lead.
    /// </summary>
    public string Key => $"{Arrival.StopId}|{Arrival.RouteId.ToUpperInvariant()}|{Arrival.DirectionId.ToUpperInvariant()}";

    /// <summary>Everything the row shows; equal signatures mean the row needn't be rebuilt.</summary>
    public string Signature => $"{Key}|{RouteLabel}|{Destination}|{RouteBackground.Color}|{MinutesText}|{DetailText}|{TripEtaText}|{RowOpacity}|{string.Join(';', Times.Select(t => t.Signature))}";
}
