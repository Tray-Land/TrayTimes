using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;
using Windows.System;

namespace TrayTimes.Controls;

public sealed partial class DestinationSearchView : UserControl
{
    private CancellationTokenSource? _request;
    private DestinationSearch? _search;
    private GeoPoint? _near;

    public DestinationSearchView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(Search_KeyDown), handledEventsToo: true);
    }

    public event EventHandler? BackRequested;

    public event EventHandler<DestinationResult>? DestinationChosen;

    public void Open(ITransitService service, GeoPoint? near)
    {
        Cancel();
        _near = near;
        _search = new DestinationSearch(GeocodingService.IsAvailable ? GeocodingService.SearchAsync : null, service.SearchStopsAsync);
        QueryBox.Text = string.Empty;
        ResultsList.ItemsSource = null;
        ShowHelp();
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void Cancel()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }

    private void ShowHelp() => StatusText.Text = AppStrings.Get(GeocodingService.IsAvailable ? "DestinationSearchHelp" : "DestinationSearchStopsOnly");

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueryBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        Cancel(); // Editing invalidates an in-flight submit, but never starts another request.
        ResultsList.ItemsSource = null;
        ShowHelp();
    }

    private async void QueryBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_search is null || string.IsNullOrWhiteSpace(args.QueryText))
        {
            StatusText.Text = AppStrings.Get("DestinationSearchEnterQuery");
            return;
        }

        if (args.QueryText.Trim().Length > DestinationSearch.MaxQueryLength)
        {
            StatusText.Text = AppStrings.Format("DestinationSearchTooLongFormat", DestinationSearch.MaxQueryLength);
            return;
        }

        Cancel();
        CancellationTokenSource request = _request = new();
        ResultsList.ItemsSource = null;
        StatusText.Text = AppStrings.Get("DestinationSearchLoading");
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, TransitTrigger.PickerSelection);
        try
        {
            DestinationSearchResults results = await _search.SearchAsync(args.QueryText, _near, request.Token);
            if (!ReferenceEquals(_request, request))
            {
                return;
            }

            ResultsList.ItemsSource = results.Items.Select(r => new DestinationSearchItem(r)).ToArray();
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

            StatusText.Text = string.Join(" ", status);
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

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DestinationSearchItem item)
        {
            DestinationChosen?.Invoke(this, item.Result);
        }
    }
}

public sealed class DestinationSearchItem(DestinationResult result)
{
    public DestinationResult Result { get; } = result;

    public string Label => Result.Name;

    public string Detail => Result.StopId is null ? AppStrings.Get("DestinationSearchAddress") : AppStrings.Format("DestinationSearchStopFormat", Result.StopId);

    public string AutomationName => AppStrings.Format("DestinationSearchResultNameFormat", Label, Detail);
}
