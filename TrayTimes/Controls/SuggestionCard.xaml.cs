using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Views;

namespace TrayTimes.Controls;

/// <summary>
/// The trip the flyout suggests, above the board: where the rider is likely headed and when to
/// leave, or what's in the way (setting up places, already there, no trip). Going to, beside it,
/// picks another destination.
/// </summary>
public sealed partial class SuggestionCard : UserControl
{
    public SuggestionCard()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the rider selects the trip.</summary>
    public event EventHandler? TripInvoked;

    /// <summary>Raised when the rider picks where they're going: a favorite place's ID, or null to let the card guess.</summary>
    public event EventHandler<string?>? DestinationChosen;

    public SuggestionCardItem Item { get; private set; } = SuggestionCardItem.Empty;

    /// <summary>The place picked from Going to, checked in its menu; null when the card guesses.</summary>
    public string? DestinationId { get; set; }

    /// <summary>Shows <paramref name="item"/>, animating it in when it's a new trip or notice rather than new times.</summary>
    public void Show(SuggestionCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Signature == Item.Signature)
        {
            return;
        }

        bool entering = item.Key != Item.Key;
        Item = item;
        Bindings.Update();
        if (entering)
        {
            Motion.Enter(this, rise: 8);
        }
    }

    private void TripButton_Click(object sender, RoutedEventArgs e) => TripInvoked?.Invoke(this, EventArgs.Empty);

    private void ActionButton_Click(object sender, RoutedEventArgs e) => Item.Action?.Invoke();

    private void DestinationMenu_Opening(object sender, object e)
    {
        DestinationMenu.Items.Clear();
        DestinationMenu.Items.Add(DestinationItem(AppStrings.Get("DestinationLikely"), null, null));
        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (FavoritePlace place in SettingsService.Places)
        {
            DestinationMenu.Items.Add(DestinationItem(place.Name, place.Id, MapHelpers.PlaceGlyph(place.Kind)));
        }

        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem edit = new() { Text = AppStrings.Get("DestinationEditPlaces"), Icon = new FontIcon { Glyph = "" } }; // Edit
        edit.Click += (_, _) => App.Current.ShowSettings();
        DestinationMenu.Items.Add(edit);
    }

    private RadioMenuFlyoutItem DestinationItem(string text, string? placeId, string? glyph)
    {
        RadioMenuFlyoutItem item = new()
        {
            Text = text,
            GroupName = "Destination",
            IsChecked = placeId == DestinationId,
            Icon = glyph is null ? null : new FontIcon { Glyph = glyph },
        };
        item.Click += (_, _) => DestinationChosen?.Invoke(this, placeId);
        return item;
    }
}
