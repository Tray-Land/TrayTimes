using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>
/// Polls departures for the saved stops while the flyout is open (browsing mode) and not at all
/// otherwise. The provider merges scheduled and real-time times itself. How often it polls, and
/// how old data may be when the flyout reopens, follow the call budget. Lives on the UI thread
/// and raises <see cref="Updated"/> there.
/// </summary>
internal sealed class ArrivalsMonitor
{
    private readonly DispatcherQueueTimer _timer;
    private readonly TimeProvider _time;
    private readonly TransitBudget? _budget;
    private bool _isForeground;
    private bool _stopsChanged;
    private int _refreshGeneration;
    private CancellationTokenSource? _refreshCancellation;

    /// <param name="budget">Sets the refresh cadence; without one the coordinator's interval is used.</param>
    public ArrivalsMonitor(ITransitService service, DispatcherQueue dispatcher, TimeProvider? timeProvider = null, PredictionCoordinator? predictions = null, TransitBudget? budget = null)
    {
        Metadata = service as MetadataTransitService ?? new MetadataTransitService(service, timeProvider);
        Service = Metadata;
        _time = timeProvider ?? TimeProvider.System;
        _budget = budget;
        Predictions = predictions ?? new PredictionCoordinator(Service, _time);
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = RefreshInterval;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = RefreshInterval; // The budget may have moved on since the last tick.
            _ = RefreshAsync(TransitTrigger.Timer);
        };
        SettingsService.StopsChanged += OnStopsChanged;
    }

    public event EventHandler? Updated;

    public ITransitService Service { get; }

    public MetadataTransitService Metadata { get; }

    public PredictionCoordinator Predictions { get; }

    public ArrivalsSnapshot Latest { get; private set; } = ArrivalsSnapshot.Empty;

    public bool IsLoading { get; private set; }

    /// <summary>How often the open flyout refreshes on its own (when the budget pauses that, its timer refusals say why).</summary>
    public TimeSpan RefreshInterval => _budget?.AutomaticRefreshInterval ?? Predictions.BrowsingInterval;

    /// <summary>True while the flyout is open: poll, and refresh now if the data is stale.</summary>
    public bool IsForeground
    {
        get => _isForeground;
        set
        {
            _isForeground = value;
            if (!value)
            {
                _timer.Stop();
                ++_refreshGeneration;
                _refreshCancellation?.Cancel();
                Predictions.SetBrowsingStops(TransitConsumer.Favorites, []);
                IsLoading = false;
                return;
            }

            Predictions.SetBrowsingStops(TransitConsumer.Favorites, [.. SettingsService.Stops.Select(s => s.StopId)]);
            _timer.Interval = RefreshInterval;
            _timer.Start();
            if (_stopsChanged || Latest.FetchedAt is not { } at || _time.GetLocalNow() - at >= Predictions.BrowsingInterval)
            {
                _ = RefreshAsync(TransitTrigger.FlyoutOpened);
            }
            else
            {
                using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened);
                Service.Diagnostics?.RecordReuse(TransitEndpoint.Departures, inFlight: false,
                    batchSize: Math.Min(SavedStop.DistinctStopCount(SettingsService.Stops), Service.MaxStopsPerRequest));
            }
        }
    }

    /// <summary>Stops polling and drops cached arrivals; shared metadata outlives the flyout.</summary>
    public void Clear()
    {
        IsForeground = false;
        ++_refreshGeneration; // Drop any in-flight refresh.
        Publish(ArrivalsSnapshot.Empty);
    }

    public async Task RefreshAsync(TransitTrigger trigger = TransitTrigger.ManualRefresh)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, trigger);
        if (!_isForeground)
        {
            return;
        }

        IReadOnlyList<SavedStop> stops = SettingsService.Stops;
        int generation = ++_refreshGeneration;
        _stopsChanged = false;

        if (!Service.IsConfigured)
        {
            _refreshCancellation?.Cancel();
            Publish(new ArrivalsSnapshot(null, [], "No Transit API key. Add TransitApiKey to OAuth.resw and rebuild."));
            return;
        }

        if (stops.Count == 0)
        {
            _refreshCancellation?.Cancel();
            Predictions.SetBrowsingStops(TransitConsumer.Favorites, []);
            Publish(ArrivalsSnapshot.Empty);
            return;
        }

        IsLoading = true;
        Updated?.Invoke(this, EventArgs.Empty);
        ArrivalsSnapshot result;
        string[] stopIds = [.. stops.Select(s => s.StopId).Distinct()];
        Predictions.SetBrowsingStops(TransitConsumer.Favorites, stopIds);
        using CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = _refreshCancellation;
        _refreshCancellation = cancellation;
        try
        {
            Task<PredictionSnapshot> pending = Predictions.GetAsync(stopIds, Predictions.BrowsingInterval,
                force: trigger is TransitTrigger.ManualRefresh or TransitTrigger.Retry,
                combineBrowsing: true, cancellationToken: cancellation.Token);
            previous?.Cancel(); // Attach the replacement waiter before releasing its shared batch.
            PredictionSnapshot snapshot = await pending;
            result = new ArrivalsSnapshot(snapshot.FetchedAt, snapshot.Arrivals, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            string message = ex switch
            {
                TransitApiException api => api.Message,
                _ when !ConnectivityService.IsInternetAvailable => "No internet connection. Times will update when you're back online.",
                _ => $"Can't reach {Service.ProviderName} right now. Retrying shortly.",
            };

            // Keep showing the last good departures alongside the error.
            result = Latest with { Error = message };
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
            }
        }

        if (generation == _refreshGeneration)
        {
            Publish(result);
        }
    }

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        if (_isForeground)
        {
            _ = RefreshAsync(TransitTrigger.StopsChanged);
        }
        else
        {
            _stopsChanged = true;
        }
    }

    private void Publish(ArrivalsSnapshot snapshot)
    {
        IsLoading = false;
        Latest = snapshot;
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
