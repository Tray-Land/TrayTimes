using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>
/// The active trip's progress: the current step with what to do next (and I missed it while heading to or on a
/// ride), and every step, done, current, or upcoming. A trip saved before a restart shows the Resume card
/// instead. It presents the trip as <see cref="ActiveTripService"/> has it and never requests anything; the
/// flyout acts on its events.
/// </summary>
public sealed partial class TripProgressView : UserControl
{
    private readonly ObservableCollection<TripLegItem> _legs = [];
    private string? _statusKey;
    private bool _announceNext;

    public TripProgressView()
    {
        InitializeComponent();
        StepsList.ItemsSource = _legs;
    }

    public event EventHandler? BackRequested;

    public event EventHandler? EndRequested;

    public event EventHandler? MissedRequested;

    public event EventHandler? ResumeRequested;

    /// <summary>Raised with the index, in the trip's legs, of the step the rider clicked.</summary>
    public event EventHandler<int>? StepInvoked;

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    /// <summary>Shows <paramref name="trip"/> at <paramref name="now"/>.</summary>
    /// <param name="step">Where the rider is; null for a trip waiting to be resumed (the Resume card).</param>
    /// <param name="alerts">Where each ride's route's service alerts come from.</param>
    public void Show(ActiveTrip trip, TripStep? step, AlertBook alerts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        LiveTrip live = trip.Progress.Trip;
        HeadingText.Text = AppStrings.Format("TripProgressHeadingFormat", trip.Leg.To.Name);
        ToolTipService.SetToolTip(HeadingText, HeadingText.Text);
        ArrivalText.Text = TripMessages.Arrival(trip.Leg, live.Arrives, now);
        DestinationGlyph.Glyph = MapHelpers.PlaceGlyph(trip.Leg.To.Kind);
        DestinationText.Text = AppStrings.Format("TripDestinationFormat", trip.Leg.To.Name, TripMessages.When(live.Arrives, now));

        TripStatus status = step is null ? TripMessages.Resume(trip) : TripMessages.Status(trip, step, now);
        StatusText.Text = status.Title;
        StatusDetail.Text = status.Detail;
        AutomationProperties.SetName(StatusText, status.Spoken);
        bool canMiss = step is { CanMiss: true };
        bool losesFocus = (!canMiss && HasFocus(MissedItButton)) || (step is not null && (HasFocus(ResumeButton) || HasFocus(DiscardButton)));
        SetVisible(MissedItButton, canMiss);
        SetVisible(ResumeButton, step is null);
        SetVisible(DiscardButton, step is null);
        SetVisible(EndTripButton, step is not null);
        SetVisible(StepsList, step is not null);
        if (losesFocus)
        {
            FocusBack(); // The button pressed went away (resumed, or the ride now waits for a new plan).
        }

        Announce(step is null ? "resume" : $"{step.Kind}|{step.Ride}|{step.AwaitsReplan}");
        if (step is not null)
        {
            ShowSteps(live, step, trip.Leg.To.Name, alerts, now);
        }
    }

    private void ShowSteps(LiveTrip live, TripStep step, string destination, AlertBook alerts, DateTimeOffset now)
    {
        List<TripLegItem> items = [];
        for (int i = 0; i < live.Legs.Count; i++)
        {
            bool hasNext = i + 1 < live.Legs.Count;
            if (live.Legs[i] is LiveWalk walk)
            {
                items.Add(new TripWalkItem(walk, hasNext ? live.Legs[i + 1] : null, destination, step.StateOf(i)) { JoinsPrevious = i > 0, JoinsNext = hasNext });
            }
            else if (live.Legs[i] is LiveRide ride)
            {
                items.Add(new TripRideItem(ride, [], timeTemplate: null, alerts.For(ride.Line.Route.Id, now), step.StateOf(i)) { JoinsPrevious = i > 0, JoinsNext = hasNext });
            }
        }

        TripLegItem.Sync(_legs, items);
    }

    /// <summary>Has Narrator read the status when the step changes, or the rider said they missed the ride, not each minute of the countdown.</summary>
    private void Announce(string key)
    {
        bool announce = _announceNext || (_statusKey is not null && _statusKey != key);
        _statusKey = key;
        _announceNext = false;
        if (announce && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)
            && (FrameworkElementAutomationPeer.FromElement(StatusText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusText)) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private static bool HasFocus(Control control) => control.FocusState != FocusState.Unfocused;

    private static void SetVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void EndTripButton_Click(object sender, RoutedEventArgs e) => EndRequested?.Invoke(this, EventArgs.Empty);

    private void MissedItButton_Click(object sender, RoutedEventArgs e)
    {
        _announceNext = true; // The step stays the same, re-timed: say its new time.
        MissedRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        _announceNext = true;
        ResumeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every leg is one step, in order, so a step's place in the list is its leg's index.</summary>
    private void StepsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TripLegItem item && _legs.IndexOf(item) is >= 0 and int leg)
        {
            StepInvoked?.Invoke(this, leg);
        }
    }
}
