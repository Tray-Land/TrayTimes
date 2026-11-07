using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Commute;
using TrayTimes.Controls;
using TrayTimes.Services;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.ViewModels;

/// <summary>
/// A trip's live options and walking/ride legs, with when to travel and Go in its header. It presents existing data and
/// never requests any; Go is raised for the flyout to start the trip.
/// </summary>
internal sealed partial class TripViewModel : ObservableObject
{
    private TripTime _time;

    public event EventHandler? BackRequested;

    public event EventHandler<TripItinerary>? OptionChosen;

    public event EventHandler<TripTime>? TimeRequested;

    /// <summary>Raised when the rider presses Go to start the selected option at the chosen time.</summary>
    public event EventHandler? GoRequested;

    public ObservableCollection<TripLegItem> Legs { get; } = [];

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Arrival { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GoName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanGo { get; set; }

    [ObservableProperty]
    public partial bool ShowsDestination { get; set; }

    [ObservableProperty]
    public partial string DestinationGlyph { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DestinationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    /// <summary>Whether the notice is a warning (the route could not be found), rather than information.</summary>
    [ObservableProperty]
    public partial bool IsNoticeWarning { get; set; }

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = string.Empty;

    /// <summary>The trip options to choose between, with the selected one, for the view's option bar.</summary>
    public IReadOnlyList<TripOptionItem> Options { get; private set; } = [];

    /// <param name="alerts">Where each ride's route's service alerts come from.</param>
    public void Show(CommuteLeg leg, TripSelection selection, IReadOnlyList<Arrival> arrivals, TrackedBus? tracked, AlertBook alerts,
        DateTimeOffset now, Microsoft.UI.Xaml.DataTemplate timeTemplate, bool isReplanning, string? error, TripTime time = default)
    {
        _time = time;
        TimeLabel = TripMessages.TimeLabel(time, now);
        TimeName = TripMessages.TimeName(time);
        Heading = AppStrings.Format("TripHeadingFormat", leg.To.Name);
        LiveTrip? trip = selection.Selected;
        Arrival = trip is null ? string.Empty : TripMessages.Arrival(leg, trip.Arrives, now);
        CanGo = trip is not null && !isReplanning;
        if (trip is not null)
        {
            GoName = TripMessages.GoName(trip, leg, now);
        }

        ShowsDestination = trip is not null;
        DestinationGlyph = MapHelpers.PlaceGlyph(leg.To.Kind);
        DestinationText = trip is null ? string.Empty : AppStrings.Format("TripDestinationFormat", leg.To.Name, Clock(trip.Arrives));
        IsNoticeOpen = isReplanning || error is not null || trip is null || !trip.IsLive;
        IsNoticeWarning = error is not null;
        NoticeMessage = isReplanning ? AppStrings.Get("TripFindingRoute") : error
            ?? (trip is null ? AppStrings.Format("SuggestionNoTripFormat", leg.To.Name)
                : !trip.IsLive ? AppStrings.Get("TripFutureScheduled") : string.Empty);
        Options = selection.Options.Select(t => new TripOptionItem(t.Itinerary,
            TripMessages.Option(t, spoken: false),
            AppStrings.Format("TripOptionNameFormat", TripMessages.Option(t, spoken: true)),
            ReferenceEquals(t, selection.Selected))).ToList();
        OnPropertyChanged(nameof(Options));
        ShowLegs(trip, leg, arrivals, tracked, alerts, now, timeTemplate);
    }

    private void ShowLegs(LiveTrip? trip, CommuteLeg leg, IReadOnlyList<Arrival> arrivals, TrackedBus? tracked, AlertBook alerts,
        DateTimeOffset now, Microsoft.UI.Xaml.DataTemplate timeTemplate)
    {
        List<TripLegItem> items = [];
        if (trip is not null)
        {
            IReadOnlyList<Arrival> departures = trip.FirstRideDepartures(arrivals, 6);
            Arrival? current = tracked?.FindListed(departures);
            bool firstRide = true;
            for (int i = 0; i < trip.Legs.Count; i++)
            {
                if (trip.Legs[i] is LiveWalk walk)
                {
                    items.Add(new TripWalkItem(walk, i + 1 < trip.Legs.Count ? trip.Legs[i + 1] : null, leg.To.Name));
                }
                else if (trip.Legs[i] is LiveRide ride)
                {
                    BusTimeItem[] times = firstRide
                        ? [.. departures.Select(a => new BusTimeItem(a, ride.Line.BoardAt.Location, now, current, ride.Line.BoardAt.Name))]
                        : [];
                    items.Add(new TripRideItem(ride, times, timeTemplate, alerts.For(ride.Line.Route.Id, now)));
                    firstRide = false;
                }
            }
        }

        TripLegItem.Sync(Legs, items);
    }

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    // The time editor (leave at / arrive by).

    private TripTimeMode _editingMode;

    /// <summary>Raised when the time editor should open.</summary>
    public event EventHandler? EditorOpenRequested;

    /// <summary>Raised when the time editor should close, its time having been accepted or cancelled.</summary>
    public event EventHandler? EditorCloseRequested;

    [ObservableProperty]
    public partial string EditorHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? EditorDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EditorClock { get; set; }

    /// <summary>The earliest date the editor offers: today.</summary>
    [ObservableProperty]
    public partial DateTimeOffset EditorMinDate { get; set; }

    /// <summary>What is wrong with the time entered; empty when nothing is.</summary>
    [ObservableProperty]
    public partial string ValidationText { get; set; } = string.Empty;

    public bool HasValidation => ValidationText.Length > 0;

    partial void OnValidationTextChanged(string value) => OnPropertyChanged(nameof(HasValidation));

    [RelayCommand]
    private void EditLeaveAt() => StartEditing(TripTimeMode.LeaveAt, DateTimeOffset.Now);

    [RelayCommand]
    private void EditArriveBy() => StartEditing(TripTimeMode.ArriveBy, DateTimeOffset.Now);

    /// <summary>Starts the editor at the time already chosen if it is still ahead, else half an hour from now.</summary>
    internal void StartEditing(TripTimeMode mode, DateTimeOffset now)
    {
        _editingMode = mode;
        DateTimeOffset at = _time.Mode != TripTimeMode.Now && _time.At > now ? _time.At.ToLocalTime() : now.AddMinutes(30);
        EditorMinDate = now.Date;
        EditorDate = at.Date;
        EditorClock = new TimeSpan(at.Hour, (at.Minute / 5) * 5, 0);
        EditorHeading = AppStrings.Get(mode == TripTimeMode.LeaveAt ? "TripLeaveAt/Text" : "TripArriveBy/Text");
        ValidationText = string.Empty;
        EditorOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for the time the rider entered, closing the editor when it is accepted and saying what is wrong with it when not.</summary>
    [RelayCommand]
    private void ApplyTime()
    {
        string? errorKey = Validate(out TripTime requested);
        if (errorKey is not null)
        {
            ValidationText = AppStrings.Get(errorKey);
            return;
        }

        ValidationText = string.Empty;
        TimeRequested?.Invoke(this, requested);
        EditorCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelEditing() => EditorCloseRequested?.Invoke(this, EventArgs.Empty);

    /// <returns>The resource key of what is wrong with the time entered, or null when it is acceptable.</returns>
    private string? Validate(out TripTime requested)
    {
        requested = default;
        try
        {
            if (EditorDate is not { } day || EditorClock is not { } time)
            {
                return "TripTimeRequired";
            }

            requested = TripTime.FromLocal(_editingMode, day.Date, time, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return "TripTimeAmbiguous";
        }

        return requested.At <= DateTimeOffset.Now ? "TripTimeMustBeFuture" : null;
    }

    [RelayCommand]
    private void LeaveNow() => TimeRequested?.Invoke(this, default);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Go() => GoRequested?.Invoke(this, EventArgs.Empty);

    public void ChooseOption(TripItinerary itinerary) => OptionChosen?.Invoke(this, itinerary);
}

/// <summary>One trip option in the option bar.</summary>
internal sealed record TripOptionItem(TripItinerary Itinerary, string Text, string Name, bool IsSelected);
