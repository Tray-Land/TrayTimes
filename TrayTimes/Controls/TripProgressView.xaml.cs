using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.ViewModels;

namespace TrayTimes.Controls;

/// <summary>
/// The active trip's progress. What it shows is in <see cref="TripProgressViewModel"/>; this view keeps focus where the
/// rider left it when a button goes away, and has Narrator read the status when the step changes.
/// </summary>
public sealed partial class TripProgressView : UserControl, ILineLauncher
{
    private string? _statusKey;
    private bool _announceNext;

    public TripProgressView()
    {
        ViewModel = new TripProgressViewModel();
        ViewModel.BackRequested += (_, e) => BackRequested?.Invoke(this, e);
        ViewModel.EndRequested += (_, e) => EndRequested?.Invoke(this, e);
        ViewModel.MissedRequested += (_, e) =>
        {
            _announceNext = true; // The step stays the same, re-timed: say its new time.
            MissedRequested?.Invoke(this, e);
        };
        ViewModel.ResumeRequested += (_, e) =>
        {
            _announceNext = true;
            ResumeRequested?.Invoke(this, e);
        };
        ViewModel.StepInvoked += (_, leg) => StepInvoked?.Invoke(this, leg);
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    public event EventHandler? EndRequested;

    public event EventHandler? MissedRequested;

    public event EventHandler? ResumeRequested;

    /// <summary>Raised with the index, in the trip's legs, of the step the rider clicked.</summary>
    public event EventHandler<int>? StepInvoked;

    /// <summary>Raised when the rider presses a ride's route badge, to see that line.</summary>
    internal event EventHandler<LineRequest>? LineRequested;

    internal TripProgressViewModel ViewModel { get; }

    void ILineLauncher.RequestLine(LineRequest line) => LineRequested?.Invoke(this, line);

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    /// <summary>Shows <paramref name="trip"/> at <paramref name="now"/>.</summary>
    /// <param name="step">Where the rider is; null for a trip waiting to be resumed (the Resume card).</param>
    /// <param name="alerts">Where each ride's route's service alerts come from.</param>
    public void Show(ActiveTrip trip, TripStep? step, AlertBook alerts, DateTimeOffset now)
    {
        bool canMiss = step is { CanMiss: true };
        bool losesFocus = (!canMiss && HasFocus(MissedItButton)) || (step is not null && (HasFocus(ResumeButton) || HasFocus(DiscardButton)));
        ViewModel.Show(trip, step, alerts, now);
        if (losesFocus)
        {
            FocusBack(); // The button pressed went away (resumed, or the ride now waits for a new plan).
        }

        Announce(ViewModel.StepKey);
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

    private void StepsList_ItemClick(object sender, ItemClickEventArgs e) => ViewModel.InvokeStepCommand.Execute(e.ClickedItem as TripLegItem);
}
