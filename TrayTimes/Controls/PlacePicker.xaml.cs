using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Devices.Geolocation;

namespace TrayTimes.Controls;

/// <summary>Chooses a commuter place by address search (Azure Maps) or the device location.</summary>
public sealed partial class PlacePicker : UserControl
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _searchCts;
    private Place? _place;

    public PlacePicker()
    {
        InitializeComponent();
        if (!GeocodingService.IsAvailable)
        {
            AddressBox.IsEnabled = false;
            AddressBox.PlaceholderText = "Address search needs an Azure Maps key";
        }

        ShowPlace();
    }

    /// <summary>Raised when the user picks a new place or clears it; read <see cref="Place"/>.</summary>
    public event EventHandler? PlaceChosen;

    public string Header
    {
        get => AddressBox.Header as string ?? string.Empty;
        set
        {
            AddressBox.Header = value;
            AddressAutomationName = $"{value} address";
        }
    }

    /// <summary>What screen readers call the address box; setting <see cref="Header"/> sets it to "{Header} address".</summary>
    public string AddressAutomationName
    {
        get => AutomationProperties.GetName(AddressBox);
        set => AutomationProperties.SetName(AddressBox, value);
    }

    public Place? Place
    {
        get => _place;
        set
        {
            _place = value;
            ShowPlace();
        }
    }

    private void ShowPlace()
    {
        CurrentText.Text = _place is null ? "Not set" : $"Saved: {_place.Address}";
        ClearButton.Visibility = _place is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Choose(Place? place)
    {
        Place = place;
        PlaceChosen?.Invoke(this, EventArgs.Empty);
    }

    private async void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _searchCts?.Cancel();
        if (sender.Text.Trim().Length < 3)
        {
            sender.ItemsSource = null;
            return;
        }

        CancellationTokenSource cts = _searchCts = new CancellationTokenSource();
        try
        {
            // Debounce so typing doesn't fire a request per keystroke.
            await Task.Delay(SearchDelay, cts.Token);
            sender.ItemsSource = await GeocodingService.SearchAsync(sender.Text, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Superseded by newer typing, or the search failed; keep the old suggestions.
        }
    }

    private void AddressBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = args.SelectedItem?.ToString() ?? sender.Text;

    private async void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        GeocodeResult? result = args.ChosenSuggestion as GeocodeResult;
        if (result is null && !string.IsNullOrWhiteSpace(args.QueryText))
        {
            try
            {
                result = (await GeocodingService.SearchAsync(args.QueryText)).FirstOrDefault();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                CurrentText.Text = "Address search isn't responding. Try again.";
                return;
            }
        }

        if (result is null)
        {
            CurrentText.Text = "No matching address found.";
            return;
        }

        sender.Text = string.Empty;
        sender.ItemsSource = null;
        Choose(new Place(result.Address, result.Location.Lat, result.Location.Lon));
    }

    private async void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        LocateButton.IsEnabled = false;
        CurrentText.Text = "Finding your location…";
        string? error = null;
        GeoPoint? point = await LocationService.RequestAccessAsync(message => error = message) == GeolocationAccessStatus.Allowed
            ? await LocationService.TryGetDeviceLocationAsync(TimeSpan.FromSeconds(15), message => error = message)
            : null;
        LocateButton.IsEnabled = true;
        if (point is not { } p)
        {
            CurrentText.Text = error ?? "Couldn't determine your location.";
            return;
        }

        string? address = null;
        try
        {
            address = await GeocodingService.ReverseAsync(p);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Fall back to a generic label below.
        }

        Choose(new Place(address ?? "My location when set", p.Lat, p.Lon));
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => Choose(null);
}
