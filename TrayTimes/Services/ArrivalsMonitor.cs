using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>
/// Polls arrivals for the saved stops while the flyout is open (browsing mode) and not at all
/// otherwise, filling in scheduled times beyond the predictions. Lives on the UI thread and raises
/// <see cref="Updated"/> there.
/// </summary>
internal sealed class ArrivalsMonitor
{
    private static readonly TimeSpan RefreshInterval = PredictionCoordinator.BrowsingInterval;

    private readonly DispatcherQueueTimer _timer;
    private readonly TimeProvider _time;
    private IReadOnlyList<Arrival> _live = [];
    private IReadOnlySet<string>? _liveStopIds;
    private bool _isForeground;
    private bool _stopsChanged;
    private int _refreshGeneration;
    private CancellationTokenSource? _refreshCancellation;

    public ArrivalsMonitor(ITransitService service, ScheduleService schedule, DispatcherQueue dispatcher, TimeProvider? timeProvider = null, PredictionCoordinator? predictions = null)
    {
        Metadata = service as MetadataTransitService ?? new MetadataTransitService(service, timeProvider);
        Service = Metadata;
        Schedule = schedule;
        _time = timeProvider ?? TimeProvider.System;
        Predictions = predictions ?? new PredictionCoordinator(Service, _time);
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = RefreshInterval;
        _timer.Tick += (_, _) => _ = RefreshAsync(TransitTrigger.Timer);
        SettingsService.StopsChanged += OnStopsChanged;
        schedule.Loaded += (_, _) => dispatcher.TryEnqueue(OnScheduleLoaded);
    }

    public event EventHandler? Updated;

    public ITransitService Service { get; }

    public MetadataTransitService Metadata { get; }

    public ScheduleService Schedule { get; }

    public PredictionCoordinator Predictions { get; }

    public ArrivalsSnapshot Latest { get; private set; } = ArrivalsSnapshot.Empty;

    public bool IsLoading { get; private set; }

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
            _timer.Start();
            if (_stopsChanged || Latest.FetchedAt is not { } at || _time.GetLocalNow() - at > TimeSpan.FromSeconds(15))
            {
                _ = RefreshAsync(TransitTrigger.FlyoutOpened);
            }
            else
            {
                RemergeSchedule();
                using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened);
                Service.Diagnostics?.RecordReuse(TransitEndpoint.Predictions, inFlight: false,
                    batchSize: Math.Min(SavedStop.DistinctStopCount(SettingsService.Stops), Service.MaxStopsPerRequest));
            }
        }
    }

    /// <summary>Stops polling and drops cached arrivals; shared metadata outlives the flyout.</summary>
    public void Clear()
    {
        IsForeground = false;
        ++_refreshGeneration; // Drop any in-flight refresh.
        _live = [];
        _liveStopIds = null;
        Publish(ArrivalsSnapshot.Empty);
    }

    /// <summary>Shared route metadata (display names and colors) keyed by route ID.</summary>
    public async Task<IReadOnlyDictionary<string, TransitRoute>> GetRouteCatalogAsync(
        TransitConsumer consumer = TransitConsumer.Favorites, CancellationToken cancellationToken = default)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(consumer, TransitTrigger.Metadata);
        try
        {
            IReadOnlyList<TransitRoute> routes = await Metadata.GetRoutesAsync(cancellationToken);
            return routes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or TransitApiException)
        {
            System.Diagnostics.Debug.WriteLine($"Route catalog load failed: {ex.GetType().Name}. Metadata remains retryable.");
            return new Dictionary<string, TransitRoute>();
        }
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
            Publish(new ArrivalsSnapshot(null, [], $"No {Service.AgencyName} API key. Add MctsApiKey to OAuth.resw and rebuild."));
            return;
        }

        if (stops.Count == 0)
        {
            _refreshCancellation?.Cancel();
            Predictions.SetBrowsingStops(TransitConsumer.Favorites, []);
            _live = [];
            _liveStopIds = null;
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
            Task<PredictionSnapshot> pending = Predictions.GetAsync(stopIds,
                trigger == TransitTrigger.FlyoutOpened ? TimeSpan.FromSeconds(15) : RefreshInterval,
                force: trigger is TransitTrigger.ManualRefresh or TransitTrigger.Retry,
                combineBrowsing: true, cancellationToken: cancellation.Token);
            previous?.Cancel(); // Attach the replacement waiter before releasing its shared batch.
            PredictionSnapshot snapshot = await pending;
            IReadOnlyList<Arrival> live = snapshot.Arrivals;
            if (generation == _refreshGeneration)
            {
                _live = live;
                _liveStopIds = stopIds.ToHashSet(StringComparer.Ordinal);
            }

            result = new ArrivalsSnapshot(snapshot.FetchedAt, Schedule.AddScheduled(live, stopIds), null);
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
                _ => $"Can't reach {Service.AgencyName} right now. Retrying shortly.",
            };

            // Keep showing the last good predictions, and the schedule, alongside the error.
            result = new ArrivalsSnapshot(Latest.FetchedAt, Schedule.AddScheduled(_live, stopIds), message);
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

    private void OnScheduleLoaded()
    {
        if (_isForeground && !RemergeSchedule() && !IsLoading)
        {
            _ = RefreshAsync(TransitTrigger.TimetableLoaded);
        }
    }

    private bool RemergeSchedule()
    {
        string[] stopIds = [.. SettingsService.Stops.Select(s => s.StopId).Distinct()];
        if (_stopsChanged || Latest.FetchedAt is null || _liveStopIds is null || !_liveStopIds.SetEquals(stopIds))
        {
            return false;
        }

        // Local enrichment must not reset a pending refresh, its error, or the HTTP timestamp.
        Latest = Latest with { Arrivals = Schedule.AddScheduled(_live, stopIds) };
        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Publish(ArrivalsSnapshot snapshot)
    {
        IsLoading = false;
        Latest = snapshot;
        Updated?.Invoke(this, EventArgs.Empty);
    }

}
