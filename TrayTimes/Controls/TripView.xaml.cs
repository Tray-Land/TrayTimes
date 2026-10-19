using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Commute;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>A trip's live options and walking/ride legs. It presents existing data and never requests any.</summary>
public sealed partial class TripView : UserControl
{
    private readonly ObservableCollection<TripLegItem> _legs = [];
    private string? _legsSignature;
    private bool _updating;
    private TripTime _time;
    private TripTimeMode _editingMode;

    public TripView()
    {
        InitializeComponent();
        LegsList.ItemsSource = _legs;
    }

    public event EventHandler? BackRequested;

    public event EventHandler<TripItinerary>? OptionChosen;

    public event EventHandler<TripTime>? TimeRequested;

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    public void Show(CommuteLeg leg, TripSelection selection, IReadOnlyList<Arrival> arrivals, TrackedBus? tracked,
        DateTimeOffset now, DataTemplate timeTemplate, bool isReplanning, string? error, TripTime time = default)
    {
        _time = time;
        TimeButtonText.Text = time.Mode == TripTimeMode.Now ? AppStrings.Get("TripLeaveNow/Text")
            : AppStrings.Format(time.Mode == TripTimeMode.LeaveAt ? "TripLeaveAtFormat" : "TripArriveByFormat",
                time.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        AutomationProperties.SetName(TimeButton, TimeButtonText.Text);
        HeadingText.Text = AppStrings.Format("TripHeadingFormat", leg.To.Name);
        LiveTrip? trip = selection.Selected;
        ArrivalText.Text = trip is null ? string.Empty : AppStrings.Format("SuggestionArriveFormat",
            trip.Arrives.ToLocalTime().ToString(time.Mode == TripTimeMode.Now ? "t" : "g", CultureInfo.CurrentCulture));
        DestinationFooter.Visibility = trip is null ? Visibility.Collapsed : Visibility.Visible;
        DestinationGlyph.Glyph = MapHelpers.PlaceGlyph(leg.To.Kind);
        DestinationText.Text = trip is null ? string.Empty : AppStrings.Format("TripDestinationFormat", leg.To.Name, Clock(trip.Arrives));
        NoticeBar.IsOpen = isReplanning || error is not null || trip is null || !trip.IsLive;
        NoticeBar.Severity = error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        NoticeBar.Message = isReplanning ? AppStrings.Get("TripFindingRoute") : error
            ?? (trip is null ? AppStrings.Format("SuggestionNoTripFormat", leg.To.Name)
                : !trip.IsLive ? AppStrings.Get("TripFutureScheduled") : string.Empty);
        ShowOptions(selection);
        ShowLegs(trip, leg, arrivals, tracked, now, timeTemplate);
    }

    private void ShowOptions(TripSelection selection)
    {
        _updating = true;
        try
        {
            if (!OptionsBar.Items.Select(i => i.Tag).SequenceEqual(selection.Options.Select(t => t.Itinerary)))
            {
                OptionsBar.Items.Clear();
                foreach (LiveTrip trip in selection.Options)
                {
                    SelectorBarItem option = new() { Tag = trip.Itinerary };
                    AutomationProperties.SetAutomationId(option, $"TripOption{OptionsBar.Items.Count}");
                    OptionsBar.Items.Add(option);
                }
            }

            for (int i = 0; i < selection.Options.Count; i++)
            {
                LiveTrip trip = selection.Options[i];
                SelectorBarItem option = OptionsBar.Items[i];
                string arrival = Clock(trip.Arrives);
                string label = !trip.Legs.OfType<LiveRide>().Any() ? AppStrings.Format("TripWalkOptionFormat", arrival)
                    : trip.Itinerary.Transfers == 0 ? arrival
                    : AppStrings.Format("TripTransferOptionFormat", arrival, trip.Itinerary.Transfers == 1
                        ? AppStrings.Get("SuggestionOneTransfer") : AppStrings.Format("SuggestionTransfersFormat", trip.Itinerary.Transfers));
                option.Text = label;
                AutomationProperties.SetName(option, AppStrings.Format("TripOptionNameFormat", label));
                if (ReferenceEquals(trip, selection.Selected))
                {
                    OptionsBar.SelectedItem = option;
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void ShowLegs(LiveTrip? trip, CommuteLeg leg, IReadOnlyList<Arrival> arrivals, TrackedBus? tracked,
        DateTimeOffset now, DataTemplate timeTemplate)
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
                    items.Add(new TripRideItem(ride, times, timeTemplate));
                    firstRide = false;
                }
            }
        }

        string signature = string.Join('\n', items.Select(i => i.Signature));
        if (_legsSignature == signature)
        {
            return;
        }

        _legsSignature = signature;
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= _legs.Count)
            {
                _legs.Add(items[i]);
            }
            else if (_legs[i].Signature != items[i].Signature)
            {
                _legs[i] = items[i];
            }
        }

        while (_legs.Count > items.Count)
        {
            _legs.RemoveAt(_legs.Count - 1);
        }
    }

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private void LeaveNow_Click(object sender, RoutedEventArgs e) => TimeRequested?.Invoke(this, default);

    private void LeaveAt_Click(object sender, RoutedEventArgs e) => EditTime(TripTimeMode.LeaveAt);

    private void ArriveBy_Click(object sender, RoutedEventArgs e) => EditTime(TripTimeMode.ArriveBy);

    private void EditTime(TripTimeMode mode)
    {
        _editingMode = mode;
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset at = _time.Mode != TripTimeMode.Now && _time.At > now ? _time.At.ToLocalTime() : now.AddMinutes(30);
        TravelDatePicker.MinDate = now.Date;
        TravelDatePicker.Date = at.Date;
        TravelClockPicker.SelectedTime = new TimeSpan(at.Hour, (at.Minute / 5) * 5, 0);
        TimeEditorHeading.Text = AppStrings.Get(mode == TripTimeMode.LeaveAt ? "TripLeaveAt/Text" : "TripArriveBy/Text");
        TimeValidationText.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() => TimeEditorFlyout.ShowAt(TimeAnchor));
    }

    private void TimeApply_Click(object sender, RoutedEventArgs e)
    {
        TripTime requested;
        try
        {
            if (TravelDatePicker.Date is not { } date || TravelClockPicker.SelectedTime is not { } clock)
            {
                ShowTimeError("TripTimeRequired");
                return;
            }

            requested = TripTime.FromLocal(_editingMode, date.Date, clock, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            ShowTimeError("TripTimeAmbiguous");
            return;
        }

        if (requested.At <= DateTimeOffset.Now)
        {
            ShowTimeError("TripTimeMustBeFuture");
            return;
        }

        TimeEditorFlyout.Hide();
        TimeRequested?.Invoke(this, requested);
        TimeButton.Focus(FocusState.Programmatic);
    }

    private void ShowTimeError(string key)
    {
        TimeValidationText.Text = AppStrings.Get(key);
        TimeValidationText.Visibility = Visibility.Visible;
    }

    private void TimeCancel_Click(object sender, RoutedEventArgs e) => TimeEditorFlyout.Hide();

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OptionsBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_updating && sender.SelectedItem?.Tag is TripItinerary itinerary)
        {
            OptionChosen?.Invoke(this, itinerary);
        }
    }
}
