using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.ViewModels;

/// <summary>
/// Searches for a trip's destination: places by address and stops by name or code, run only when the rider submits,
/// with the recently chosen destinations listed while the box is empty.
/// </summary>
internal sealed partial class DestinationSearchViewModel : ObservableObject
{
    private CancellationTokenSource? _request;
    private DestinationSearch? _search;
    private GeoPoint? _near;

    public event EventHandler? BackRequested;

    public event EventHandler<DestinationResult>? DestinationChosen;

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<DestinationSearchItem>? Results { get; set; }

    [ObservableProperty]
    public partial bool ShowsRecent { get; set; }

    public void Open(ITransitService service, GeoPoint? near)
    {
        Cancel();
        _near = near;
        _search = new DestinationSearch(GeocodingService.IsAvailable ? GeocodingService.SearchAsync : null, service.SearchStopsAsync);
        QueryText = string.Empty;
        ShowIdle();
    }

    /// <summary>Stops a search in flight (editing, navigating away, or hiding the flyout).</summary>
    public void Cancel()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }

    partial void OnQueryTextChanged(string value)
    {
        Cancel(); // Editing invalidates an in-flight submit, but never starts another request.
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowIdle();
            return;
        }

        ShowsRecent = false;
        Results = null;
        ShowHelp();
    }

    /// <summary>The help text, with the recently chosen destinations below it while the box is empty.</summary>
    private void ShowIdle()
    {
        ShowHelp();
        DestinationSearchItem[] recents = [.. SettingsService.RecentDestinations.Select(r => new DestinationSearchItem(r.ToResult()))];
        Results = recents.Length == 0 ? null : recents;
        ShowsRecent = recents.Length > 0;
    }

    private void ShowHelp() => Status = AppStrings.Get(GeocodingService.IsAvailable ? "DestinationSearchHelp" : "DestinationSearchStopsOnly");

    [RelayCommand]
    private void ClearRecent()
    {
        SettingsService.ClearRecentDestinations();
        ShowIdle();
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Choose(DestinationSearchItem? item)
    {
        if (item is not null)
        {
            SettingsService.AddRecentDestination(RecentDestination.From(item.Result));
            DestinationChosen?.Invoke(this, item.Result);
        }
    }

    [RelayCommand]
    private async Task SubmitAsync(string? queryText)
    {
        if (_search is null || string.IsNullOrWhiteSpace(queryText))
        {
            Status = AppStrings.Get("DestinationSearchEnterQuery");
            return;
        }

        if (queryText.Trim().Length > DestinationSearch.MaxQueryLength)
        {
            Status = AppStrings.Format("DestinationSearchTooLongFormat", DestinationSearch.MaxQueryLength);
            return;
        }

        Cancel();
        CancellationTokenSource request = _request = new();
        ShowsRecent = false;
        Results = null;
        Status = AppStrings.Get("DestinationSearchLoading");
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, TransitTrigger.PickerSelection);
        try
        {
            DestinationSearchResults results = await _search.SearchAsync(queryText, _near, request.Token);
            if (!ReferenceEquals(_request, request))
            {
                return;
            }

            Results = results.Items.Select(r => new DestinationSearchItem(r)).ToArray();
            List<string> status = [AppStrings.Get(results.Items.Count == 0 ? "DestinationSearchNoResults" : "DestinationSearchChoose")];
            if (!GeocodingService.IsAvailable)
            {
                status.Add(AppStrings.Get("DestinationSearchStopsOnly"));
            }

            if (results.StopsNeedLocation)
            {
                status.Add(AppStrings.Get("DestinationSearchNeedsLocation"));
            }

            if (results.AddressFailed || results.StopsFailed)
            {
                status.Add(AppStrings.Get(results.AddressFailed && results.StopsFailed ? "DestinationSearchFailed"
                    : results.AddressFailed ? "DestinationSearchAddressFailed" : "DestinationSearchStopsFailed"));
            }

            Status = string.Join(" ", status);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // Editing, navigation, or hiding canceled this search.
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
            }

            request.Dispose();
        }
    }
}

public sealed class DestinationSearchItem(DestinationResult result)
{
    public DestinationResult Result { get; } = result;

    /// <summary>A place's name (or the whole address when it has none).</summary>
    public string Label => Result.Title ?? Result.Name;

    /// <summary>A place's address, else "Address", else the stop code.</summary>
    public string Detail => Result.Subtitle
        ?? (Result.StopId is null ? AppStrings.Get("DestinationSearchAddress") : AppStrings.Format("DestinationSearchStopFormat", Result.StopId));

    public string AutomationName => AppStrings.Format("DestinationSearchResultNameFormat", Label, Detail);
}
