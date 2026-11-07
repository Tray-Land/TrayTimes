using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>
/// A trip's live options and walking/ride legs, with when to travel and Go in its header. What it shows and does is in
/// <see cref="TripViewModel"/>; this view fills the option bar, runs the time editor's flyout, and moves focus.
/// </summary>
public sealed partial class TripView : UserControl
{
    private bool _updating;

    public TripView()
    {
        ViewModel = new TripViewModel();
        ViewModel.BackRequested += (_, e) => BackRequested?.Invoke(this, e);
        ViewModel.OptionChosen += (_, itinerary) => OptionChosen?.Invoke(this, itinerary);
        ViewModel.TimeRequested += (_, time) => TimeRequested?.Invoke(this, time);
        ViewModel.GoRequested += (_, e) => GoRequested?.Invoke(this, e);
        ViewModel.EditorOpenRequested += OnEditorOpenRequested;
        ViewModel.EditorCloseRequested += OnEditorCloseRequested;
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    public event EventHandler<TripItinerary>? OptionChosen;

    public event EventHandler<TripTime>? TimeRequested;

    /// <summary>Raised when the rider presses Go to start the selected option at the chosen time.</summary>
    public event EventHandler? GoRequested;

    internal TripViewModel ViewModel { get; }

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    /// <param name="alerts">Where each ride's route's service alerts come from.</param>
    public void Show(CommuteLeg leg, TripSelection selection, IReadOnlyList<Arrival> arrivals, TrackedBus? tracked, AlertBook alerts,
        DateTimeOffset now, DataTemplate timeTemplate, bool isReplanning, string? error, TripTime time = default)
    {
        ViewModel.Show(leg, selection, arrivals, tracked, alerts, now, timeTemplate, isReplanning, error, time);
        ShowOptions(ViewModel.Options);
    }

    /// <summary>The option bar has no items source, so it is kept in step with the view model's options here.</summary>
    private void ShowOptions(IReadOnlyList<TripOptionItem> options)
    {
        _updating = true;
        try
        {
            if (!OptionsBar.Items.Select(i => i.Tag).SequenceEqual(options.Select(o => o.Itinerary)))
            {
                OptionsBar.Items.Clear();
                foreach (TripOptionItem trip in options)
                {
                    SelectorBarItem option = new() { Tag = trip.Itinerary };
                    AutomationProperties.SetAutomationId(option, $"TripOption{OptionsBar.Items.Count}");
                    OptionsBar.Items.Add(option);
                }
            }

            for (int i = 0; i < options.Count; i++)
            {
                SelectorBarItem option = OptionsBar.Items[i];
                option.Text = options[i].Text;
                AutomationProperties.SetName(option, options[i].Name);
                if (options[i].IsSelected)
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

    private void OnEditorOpenRequested(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => TimeEditorFlyout.ShowAt(TimeButton));

    private void OnEditorCloseRequested(object? sender, EventArgs e)
    {
        TimeEditorFlyout.Hide();
        TimeButton.Focus(FocusState.Programmatic);
    }

    private void OptionsBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_updating && sender.SelectedItem?.Tag is TripItinerary itinerary)
        {
            ViewModel.ChooseOption(itinerary);
        }
    }
}
