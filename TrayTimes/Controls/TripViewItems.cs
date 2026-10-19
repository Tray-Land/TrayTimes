using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Commute;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Controls;

public abstract class TripLegItem
{
    public abstract string Signature { get; }

    protected static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    protected static int Minutes(TimeSpan duration) => Math.Max(0, (int)Math.Ceiling(duration.TotalMinutes));
}

public sealed class TripWalkItem : TripLegItem
{
    public TripWalkItem(LiveWalk walk, LiveLeg? next, string destination)
    {
        int minutes = Minutes(walk.Ends - walk.Starts);
        Title = walk.Walk.Meters > 0 ? AppStrings.Format("TripWalkFormat", minutes, AppStrings.Distance(walk.Walk.Meters))
            : AppStrings.Format("TripWalkMinutesFormat", minutes);
        string to = next is LiveRide ride ? ride.Line.BoardAt.Name : destination;
        Detail = AppStrings.Format("TripWalkToFormat", to, Clock(walk.Ends));
        Wait = next is LiveRide waiting && waiting.Starts > walk.Ends
            ? AppStrings.Format("TripWaitFormat", Minutes(waiting.Starts - walk.Ends))
            : string.Empty;
    }

    public string Title { get; }

    public string Detail { get; }

    public string Wait { get; }

    public Visibility WaitVisibility => Wait.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public override string Signature => $"{Title}|{Detail}|{Wait}";
}

public sealed class TripRideItem : TripLegItem
{
    public TripRideItem(LiveRide ride, IReadOnlyList<BusTimeItem> times, DataTemplate timeTemplate)
    {
        RouteChipItem badge = new(ride.Line.Route.DisplayId, MapHelpers.ParseRouteColor(ride.Departure?.RouteColor ?? ride.Line.Route.Color), false);
        RouteLabel = badge.Label;
        RouteBackground = badge.Background;
        RouteForeground = badge.Foreground;
        Destination = ride.Departure?.Destination is { Length: > 0 } headsign ? headsign : ride.Line.Direction.Name;
        Board = AppStrings.Format("TripBoardFormat", ride.Line.BoardAt.Name, Clock(ride.Starts));
        string timing = AppStrings.Get(ride.Departure?.IsRealTime == true ? "TripLive" : "TripScheduled");
        if (ride.Departure is { IsDelayed: true, Delay: { } delay })
        {
            timing = AppStrings.Format("TripDelayedFormat", Minutes(delay));
        }

        Detail = ride.Line.Stops > 0 ? AppStrings.Format("TripRideDetailFormat", timing, AppStrings.Format(ride.Line.Stops == 1 ? "TripOneStopFormat" : "TripStopsFormat", ride.Line.Stops)) : timing;
        Alight = ride.Line.AlightAt is { } stop
            ? AppStrings.Format("TripAlightFormat", stop.Name, Clock(ride.Ends))
            : AppStrings.Format("TripAlightUnknownFormat", Clock(ride.Ends));
        Times = times;
        TimeTemplate = timeTemplate;
        TimesName = AppStrings.Format("TripTimesNameFormat", RouteLabel, ride.Line.BoardAt.Name);
    }

    public string RouteLabel { get; }

    public SolidColorBrush RouteBackground { get; }

    public SolidColorBrush RouteForeground { get; }

    public string Destination { get; }

    public string Board { get; }

    public string Detail { get; }

    public string Alight { get; }

    public IReadOnlyList<BusTimeItem> Times { get; }

    public DataTemplate TimeTemplate { get; }

    public string TimesName { get; }

    public Visibility TimesVisibility => Times.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public override string Signature => $"{RouteLabel}|{RouteBackground.Color}|{Destination}|{Board}|{Detail}|{Alight}|{string.Join(';', Times.Select(t => t.Signature))}";
}
