using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Commute;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>One leg of a trip in the trip and progress views. In the progress view it has a state: done, current, or upcoming.</summary>
public abstract class TripLegItem(TripLegState? state)
{
    private const double DoneOpacity = 0.6;

    /// <summary>Where the leg is relative to the rider; null in the trip view, which shows no state.</summary>
    public TripLegState? State { get; } = state;

    public Visibility StateVisibility => State is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DoneVisibility => State == TripLegState.Done ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CurrentVisibility => State == TripLegState.Current ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UpcomingVisibility => State == TripLegState.Upcoming ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Done legs are dimmed (their check mark says so too, not the dimming alone).</summary>
    public double RowOpacity => State == TripLegState.Done ? DoneOpacity : 1;

    /// <summary>What Narrator reads for the leg, starting with its state.</summary>
    public string AutomationName => State switch
    {
        TripLegState.Done => AppStrings.Format("TripLegDoneNameFormat", Description),
        TripLegState.Current => AppStrings.Format("TripLegCurrentNameFormat", Description),
        _ => Description,
    };

    public abstract string Signature { get; }

    /// <summary>The leg in words, without its state.</summary>
    protected abstract string Description { get; }

    /// <summary>Replaces only the legs that changed, so unchanged rows (and their time chips) keep their place.</summary>
    public static void Sync(ObservableCollection<TripLegItem> shown, IReadOnlyList<TripLegItem> items)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(items);
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= shown.Count)
            {
                shown.Add(items[i]);
            }
            else if (shown[i].Signature != items[i].Signature)
            {
                shown[i] = items[i];
            }
        }

        while (shown.Count > items.Count)
        {
            shown.RemoveAt(shown.Count - 1);
        }
    }

    /// <summary>The list item's name for UI Automation, which reads a data item's text.</summary>
    public override string ToString() => AutomationName;

    protected static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    protected static int Minutes(TimeSpan duration) => Math.Max(0, (int)Math.Ceiling(duration.TotalMinutes));
}

public sealed class TripWalkItem : TripLegItem
{
    public TripWalkItem(LiveWalk walk, LiveLeg? next, string destination, TripLegState? state = null)
        : base(state)
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

    public override string Signature => $"{State}|{Title}|{Detail}|{Wait}";

    protected override string Description => Wait.Length == 0
        ? AppStrings.Format("TripLegNameFormat", Title, Detail)
        : AppStrings.Format("TripLegWaitNameFormat", Title, Detail, Wait);
}

public sealed class TripRideItem : TripLegItem
{
    /// <param name="times">The first ride's departures to track (the trip view); empty in the progress view.</param>
    public TripRideItem(LiveRide ride, IReadOnlyList<BusTimeItem> times, DataTemplate? timeTemplate, TripLegState? state = null)
        : base(state)
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

    public DataTemplate? TimeTemplate { get; }

    public string TimesName { get; }

    public Visibility TimesVisibility => Times.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public override string Signature => $"{State}|{RouteLabel}|{RouteBackground.Color}|{Destination}|{Board}|{Detail}|{Alight}|{string.Join(';', Times.Select(t => t.Signature))}";

    protected override string Description => AppStrings.Format("TripRideNameFormat", RouteLabel, Destination, Board, Detail, Alight);
}
