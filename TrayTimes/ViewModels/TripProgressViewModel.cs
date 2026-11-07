using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Commute;
using TrayTimes.Controls;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.ViewModels;

/// <summary>
/// The active trip's progress: the current step with what to do next (and I missed it while heading to or on a ride),
/// and every step, done, current, or upcoming. A trip saved before a restart shows the Resume card instead. It presents
/// the trip as <see cref="ActiveTripService"/> has it and never requests anything; the flyout acts on its events.
/// </summary>
internal sealed partial class TripProgressViewModel : ObservableObject
{
    public event EventHandler? BackRequested;

    public event EventHandler? EndRequested;

    public event EventHandler? MissedRequested;

    public event EventHandler? ResumeRequested;

    /// <summary>Raised with the index, in the trip's legs, of the step the rider clicked.</summary>
    public event EventHandler<int>? StepInvoked;

    public ObservableCollection<TripLegItem> Legs { get; } = [];

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Arrival { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DestinationGlyph { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DestinationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = string.Empty;

    /// <summary>What Narrator reads for the status: the whole card.</summary>
    [ObservableProperty]
    public partial string StatusName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanMiss { get; set; }

    /// <summary>Whether the trip waits to be resumed (a saved trip after a restart): the Resume card shows instead of the steps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStep))]
    public partial bool IsWaitingToResume { get; set; }

    public bool HasStep => !IsWaitingToResume;

    /// <summary>Changes when the step does (not each minute of the countdown), so the view can have Narrator read the status.</summary>
    public string StepKey { get; private set; } = string.Empty;

    /// <summary>Shows <paramref name="trip"/> at <paramref name="now"/>.</summary>
    /// <param name="step">Where the rider is; null for a trip waiting to be resumed (the Resume card).</param>
    /// <param name="alerts">Where each ride's route's service alerts come from.</param>
    public void Show(ActiveTrip trip, TripStep? step, AlertBook alerts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        LiveTrip live = trip.Progress.Trip;
        Heading = AppStrings.Format("TripProgressHeadingFormat", trip.Leg.To.Name);
        Arrival = TripMessages.Arrival(trip.Leg, live.Arrives, now);
        DestinationGlyph = MapHelpers.PlaceGlyph(trip.Leg.To.Kind);
        DestinationText = AppStrings.Format("TripDestinationFormat", trip.Leg.To.Name, TripMessages.When(live.Arrives, now));

        TripStatus status = step is null ? TripMessages.Resume(trip) : TripMessages.Status(trip, step, now);
        StatusTitle = status.Title;
        StatusDetail = status.Detail;
        StatusName = status.Spoken;
        CanMiss = step is { CanMiss: true };
        IsWaitingToResume = step is null;
        StepKey = step is null ? "resume" : $"{step.Kind}|{step.Ride}|{step.AwaitsReplan}";
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

        TripLegItem.Sync(Legs, items);
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void End() => EndRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Missed() => MissedRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Resume() => ResumeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Every leg is one step, in order, so a step's place in the list is its leg's index.</summary>
    [RelayCommand]
    private void InvokeStep(TripLegItem? item)
    {
        if (item is not null && Legs.IndexOf(item) is >= 0 and int leg)
        {
            StepInvoked?.Invoke(this, leg);
        }
    }
}
