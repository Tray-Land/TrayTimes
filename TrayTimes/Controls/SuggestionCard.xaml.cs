using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.ViewModels;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>
/// The trip the flyout suggests, above the board. What it shows and does is in <see cref="SuggestionCardViewModel"/>;
/// this view animates a new trip in, builds the Going to menu, and moves focus.
/// </summary>
public sealed partial class SuggestionCard : UserControl
{
    public SuggestionCard()
    {
        ViewModel = new SuggestionCardViewModel();
        ViewModel.Entering += (_, _) => Motion.Enter(this, rise: 8);
        ViewModel.TripInvoked += (_, e) => TripInvoked?.Invoke(this, e);
        ViewModel.DestinationChosen += (_, placeId) => DestinationChosen?.Invoke(this, placeId);
        ViewModel.SearchRequested += (_, e) => SearchRequested?.Invoke(this, e);
        ViewModel.SaveDestinationRequested += (_, e) => SaveDestinationRequested?.Invoke(this, e);
        InitializeComponent();
    }

    /// <summary>Raised when the rider selects the trip.</summary>
    public event EventHandler? TripInvoked;

    /// <summary>Raised when the rider picks where they're going: a favorite place's ID, or null to let the card guess.</summary>
    public event EventHandler<string?>? DestinationChosen;

    public event EventHandler? SearchRequested;

    public event EventHandler? SaveDestinationRequested;

    internal SuggestionCardViewModel ViewModel { get; }

    public SuggestionCardItem Item => ViewModel.Item;

    /// <summary>The place picked from Going to, checked in its menu; null when the card guesses.</summary>
    public string? DestinationId
    {
        get => ViewModel.DestinationId;
        set => ViewModel.DestinationId = value;
    }

    public void ShowSaveDestination(bool canSave, bool isPlanning) => ViewModel.ShowSaveDestination(canSave, isPlanning);

    /// <summary>Shows <paramref name="item"/>, animating it in when it's a new trip or notice rather than new times.</summary>
    public void Show(SuggestionCardItem item) => ViewModel.Show(item);

    public void FocusDestination() => DestinationButton.Focus(FocusState.Programmatic);

    public void FocusTrip() => TripButton.Focus(FocusState.Programmatic);

    private void DestinationMenu_Opening(object sender, object e)
    {
        DestinationMenu.Items.Clear();
        IReadOnlyList<DestinationOption> options = ViewModel.DestinationOptions();
        DestinationMenu.Items.Add(DestinationItem(options[0]));
        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (DestinationOption place in options.Skip(1))
        {
            DestinationMenu.Items.Add(DestinationItem(place));
        }

        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem search = new() { Text = AppStrings.Get("DestinationSearchMenu"), Icon = new SymbolIcon(Symbol.Find), Command = ViewModel.SearchDestinationCommand };
        AutomationProperties.SetAutomationId(search, "DestinationSearchMenu");
        DestinationMenu.Items.Add(search);
        DestinationMenu.Items.Add(new MenuFlyoutItem
        {
            Text = AppStrings.Get("DestinationEditPlaces"),
            Icon = new FontIcon { Glyph = "" }, // Edit
            Command = ViewModel.EditPlacesCommand,
        });
    }

    private RadioMenuFlyoutItem DestinationItem(DestinationOption option) => new()
    {
        Text = option.Text,
        GroupName = "Destination",
        IsChecked = option.IsChecked,
        Icon = option.Glyph is null ? null : new FontIcon { Glyph = option.Glyph },
        Command = ViewModel.ChooseDestinationCommand,
        CommandParameter = option.PlaceId,
    };
}
