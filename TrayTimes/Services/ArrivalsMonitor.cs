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
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueueTimer _timer;
    private Task<IReadOnlyDictionary<string, TransitRoute>>? _routesTask;
    private IReadOnlyList<Arrival> _live = [];
    private bool _isForeground;
    private bool _stopsChanged;
    private int _refreshGeneration;

    public ArrivalsMonitor(ITransitService service, ScheduleService schedule, DispatcherQueue dispatcher)
    {
        Service = service;
        Schedule = schedule;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = RefreshInterval;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SettingsService.StopsChanged += OnStopsChanged;
        schedule.Loaded += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_isForeground)
            {
                _ = RefreshAsync();
            }
        });
    }

    public event EventHandler? Updated;

    public ITransitService Service { get; }

    public ScheduleService Schedule { get; }

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
                return;
            }

            _timer.Start();
            if (_stopsChanged || Latest.FetchedAt is not { } at || DateTimeOffset.Now - at > TimeSpan.FromSeconds(15))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Stops polling and drops the cached arrivals and route catalog (the flyout was closed).</summary>
    public void Clear()
    {
        IsForeground = false;
        ++_refreshGeneration; // Drop any in-flight refresh.
        _routesTask = null;
        _live = [];
        Publish(ArrivalsSnapshot.Empty);
    }

    /// <summary>Route metadata (display names and colors) keyed by route ID; fetched once per session.</summary>
    public async Task<IReadOnlyDictionary<string, TransitRoute>> GetRouteCatalogAsync()
    {
        _routesTask ??= LoadRoutesAsync();
        try
        {
            return await _routesTask;
        }
        catch
        {
            // Try again next time rather than caching the failure.
            _routesTask = null;
            return new Dictionary<string, TransitRoute>();
        }
    }

    public async Task RefreshAsync()
    {
        IReadOnlyList<SavedStop> stops = SettingsService.Stops;
        int generation = ++_refreshGeneration;
        _stopsChanged = false;

        if (!Service.IsConfigured)
        {
            Publish(new ArrivalsSnapshot(null, [], $"No {Service.AgencyName} API key. Add MctsApiKey to OAuth.resw and rebuild."));
            return;
        }

        if (stops.Count == 0)
        {
            Publish(ArrivalsSnapshot.Empty);
            return;
        }

        IsLoading = true;
        Updated?.Invoke(this, EventArgs.Empty);
        ArrivalsSnapshot result;
        string[] stopIds = [.. stops.Select(s => s.StopId).Distinct().Take(Service.MaxStopsPerRequest)];
        try
        {
            IReadOnlyList<Arrival> live = await Service.GetArrivalsAsync(stopIds);
            if (generation == _refreshGeneration)
            {
                _live = live;
            }

            result = new ArrivalsSnapshot(DateTimeOffset.Now, Schedule.AddScheduled(live, stopIds), null);
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

        if (generation == _refreshGeneration)
        {
            Publish(result);
        }
    }

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        if (_isForeground)
        {
            _ = RefreshAsync();
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

    private async Task<IReadOnlyDictionary<string, TransitRoute>> LoadRoutesAsync()
    {
        IReadOnlyList<TransitRoute> routes = await Service.GetRoutesAsync();
        return routes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
    }
}
