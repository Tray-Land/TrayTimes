using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Devices.Geolocation;

namespace TrayTimes.ViewModels;

/// <summary>Chooses a commuter place by address search or the device location.</summary>
internal sealed partial class PlacePickerViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _searchCts;

    public PlacePickerViewModel()
    {
        IsSearchAvailable = GeocodingService.IsAvailable;
        StatusText = DescribePlace(null);
    }

    /// <summary>Raised when the user picks a new place or clears it (not when <see cref="Place"/> is set by the host).</summary>
    public event EventHandler? PlaceChosen;

    public bool IsSearchAvailable { get; }

    [ObservableProperty]
    public partial Place? Place { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GeocodeResult>? Suggestions { get; set; }

    public bool CanClear => Place is not null;

    partial void OnPlaceChanged(Place? value)
    {
        StatusText = DescribePlace(value);
        OnPropertyChanged(nameof(CanClear));
    }

    private static string DescribePlace(Place? place) => place is null ? "Not set" : $"Saved: {place.Address}";

    private void Choose(Place? place)
    {
        Place = place;
        PlaceChosen?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks up suggestions for the typed text, debounced so typing doesn't fire a request per keystroke.</summary>
    [RelayCommand]
    private async Task UpdateSuggestionsAsync(string? text)
    {
        _searchCts?.Cancel();
        string query = text?.Trim() ?? string.Empty;
        if (query.Length < 3)
        {
            Suggestions = null;
            return;
        }

        CancellationTokenSource cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDelay, cts.Token);
            Suggestions = await GeocodingService.SearchAsync(query, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Superseded by newer typing, or the search failed; keep the old suggestions.
        }
    }

    /// <summary>Chooses the suggestion, or the best match for the typed text when none was picked. Returns whether a place was chosen.</summary>
    public async Task<bool> SubmitAsync(GeocodeResult? chosen, string? queryText)
    {
        GeocodeResult? result = chosen;
        if (result is null && !string.IsNullOrWhiteSpace(queryText))
        {
            try
            {
                result = (await GeocodingService.SearchAsync(queryText)).FirstOrDefault();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                StatusText = "Address search isn't responding. Try again.";
                return false;
            }
        }

        if (result is null)
        {
            StatusText = "No matching address found.";
            return false;
        }

        Suggestions = null;
        Choose(new Place(result.Address, result.Location.Lat, result.Location.Lon));
        return true;
    }

    [RelayCommand]
    private async Task LocateAsync()
    {
        StatusText = "Finding your location…";
        string? error = null;
        GeoPoint? point = await LocationService.RequestAccessAsync(message => error = message) == GeolocationAccessStatus.Allowed
            ? await LocationService.TryGetDeviceLocationAsync(TimeSpan.FromSeconds(15), message => error = message)
            : null;
        if (point is not { } p)
        {
            StatusText = error ?? "Couldn't determine your location.";
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

    [RelayCommand]
    private void Clear() => Choose(null);
}
