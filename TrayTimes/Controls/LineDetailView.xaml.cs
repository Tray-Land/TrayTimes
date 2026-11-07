using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>
/// One bus line in detail: a direction to pick and that direction's stops, with the routes that cross each and the buses
/// nearest it. What it shows is in <see cref="LineDetailViewModel"/>; this view fills the direction bar, opens the menu a
/// tapped stop offers, and moves focus.
/// </summary>
public sealed partial class LineDetailView : UserControl
{
    private bool _updating;

    public LineDetailView()
    {
        ViewModel = new LineDetailViewModel();
        ViewModel.BackRequested += (_, e) => BackRequested?.Invoke(this, e);
        ViewModel.DirectionsChanged += (_, _) => ShowDirections();
        ViewModel.MapChanged += (_, map) => MapChanged?.Invoke(this, map);
        ViewModel.StopChosen += (_, stop) => StopChosen?.Invoke(this, stop);
        ViewModel.TripRequested += (_, stop) => TripRequested?.Invoke(this, stop);
        ViewModel.FavoriteRequested += (_, stop) => FavoriteRequested?.Invoke(this, stop);
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    /// <summary>Raised when the line, or the direction shown, changes and the map should draw it.</summary>
    internal event EventHandler<LineMap>? MapChanged;

    /// <summary>Raised when a stop is tapped, to show it on the map.</summary>
    public event EventHandler<TransitStop>? StopChosen;

    /// <summary>Raised when the rider asks for a trip to a stop.</summary>
    public event EventHandler<TransitStop>? TripRequested;

    /// <summary>Raised when the rider asks to favorite a stop, or to unfavorite one that is.</summary>
    public event EventHandler<TransitStop>? FavoriteRequested;

    internal LineDetailViewModel ViewModel { get; }

    public void FocusBack() => BackButton.Focus(FocusState.Programmatic);

    internal void Open(ITransitService service, LineRequest line) => ViewModel.Open(service, line);

    public void Cancel() => ViewModel.Cancel();

    /// <summary>The direction bar has no items source, so it is kept in step with the view model's directions here.</summary>
    private void ShowDirections()
    {
        _updating = true;
        try
        {
            TransitDirection[] directions = ViewModel.Directions;
            if (!DirectionsBar.Items.Select(i => i.Tag).SequenceEqual(directions.Select(d => (object)d.Id)))
            {
                DirectionsBar.Items.Clear();
                foreach (TransitDirection direction in directions)
                {
                    SelectorBarItem item = new() { Text = direction.Name, Tag = direction.Id };
                    AutomationProperties.SetAutomationId(item, $"LineDirection{DirectionsBar.Items.Count}");
                    AutomationProperties.SetName(item, direction.Name);
                    DirectionsBar.Items.Add(item);
                }
            }

            DirectionsBar.SelectedItem = DirectionsBar.Items.FirstOrDefault(i =>
                string.Equals(i.Tag as string, ViewModel.DirectionId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updating = false;
        }
    }

    private void DirectionsBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_updating && sender.SelectedItem?.Tag is string directionId)
        {
            ViewModel.ChooseDirection(directionId);
        }
    }

    /// <summary>A tapped stop is shown on the map, and offers a trip there and a favorite.</summary>
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineStopItem item } anchor)
        {
            return;
        }

        ViewModel.ChooseStop(item);
        MenuFlyoutItem trip = new()
        {
            Text = AppStrings.Get("LineStopTripMenu"),
            Icon = new FontIcon { Glyph = "" }, // MapPin
        };
        AutomationProperties.SetAutomationId(trip, "LineStopTrip");
        trip.Click += (_, _) => ViewModel.RequestTrip(item.Stop);
        MenuFlyoutItem favorite = new()
        {
            Text = AppStrings.Get(item.IsFavorite ? "LineStopRemoveFavoriteMenu" : "LineStopAddFavoriteMenu"),
            Icon = new FontIcon { Glyph = item.IsFavorite ? "" : "" }, // FavoriteStarFill / FavoriteStar
        };
        AutomationProperties.SetAutomationId(favorite, "LineStopFavorite");
        favorite.Click += (_, _) => ViewModel.RequestFavorite(item.Stop);

        MenuFlyout menu = new();
        menu.Items.Add(trip);
        menu.Items.Add(favorite);
        menu.ShowAt(anchor);
    }
}
