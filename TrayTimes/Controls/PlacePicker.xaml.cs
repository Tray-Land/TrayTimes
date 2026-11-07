using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.ViewModels;

namespace TrayTimes.Controls;

/// <summary>
/// Chooses a commuter place by address search or the device location. The search and location logic is in
/// <see cref="PlacePickerViewModel"/>; this control exposes the result as the two-way <see cref="Place"/> property.
/// </summary>
public sealed partial class PlacePicker : UserControl
{
    public static readonly DependencyProperty PlaceProperty = DependencyProperty.Register(
        nameof(Place), typeof(Place), typeof(PlacePicker), new PropertyMetadata(null, OnPlaceChanged));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(PlacePicker), new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty AddressAutomationNameProperty = DependencyProperty.Register(
        nameof(AddressAutomationName), typeof(string), typeof(PlacePicker), new PropertyMetadata(string.Empty));

    public PlacePicker()
    {
        ViewModel = new PlacePickerViewModel();
        ViewModel.PlaceChosen += (_, _) => Place = ViewModel.Place;
        InitializeComponent();
    }

    internal PlacePickerViewModel ViewModel { get; }

    public Place? Place
    {
        get => (Place?)GetValue(PlaceProperty);
        set => SetValue(PlaceProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>What screen readers call the address box; setting <see cref="Header"/> sets it to "{Header} address".</summary>
    public string AddressAutomationName
    {
        get => (string)GetValue(AddressAutomationNameProperty);
        set => SetValue(AddressAutomationNameProperty, value);
    }

    internal string SearchPlaceholder => GeocodingService.IsAvailable
        ? "Search for an address"
        : "Place search needs a Google Places or Azure Maps key";

    private static void OnPlaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PlacePicker)d).ViewModel.Place = e.NewValue as Place;

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PlacePicker)d).AddressAutomationName = $"{e.NewValue} address";

    private void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.UpdateSuggestionsCommand.Execute(sender.Text);
        }
    }

    private void AddressBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = args.SelectedItem?.ToString() ?? sender.Text;

    private async void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (await ViewModel.SubmitAsync(args.ChosenSuggestion as GeocodeResult, args.QueryText))
        {
            sender.Text = string.Empty;
        }
    }
}
