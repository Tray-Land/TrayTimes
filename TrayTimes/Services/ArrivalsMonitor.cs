using System.Net.Http;
using Microsoft.UI.Dispatching;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>
/// Polls arrivals for the saved stops: often while the flyout is open, and slowly in the
/// background so the tray tooltip stays current. Lives on the UI thread and raises
/// <see cref="Updated"/> there.
/// </summary>
internal sealed class ArrivalsMonitor
{
    private static readonly TimeSpan ForegroundInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(60);

    private readonly DispatcherQueueTimer _timer;
    private Task<IReadOnlyDictionary<string, TransitRoute>>? _routesTask;
    private bool _isForeground;
    private int _refreshGeneration;

    public ArrivalsMonitor(ITransitService service, DispatcherQueue dispatcher)
    {
        Service = service;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = BackgroundInterval;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SettingsService.StopsChanged += (_, _) => _ = RefreshAsync();
    }

    public event EventHandler? Updated;

    public ITransitService Service { get; }

    public ArrivalsSnapshot Latest { get; private set; } = ArrivalsSnapshot.Empty;

    public bool IsLoading { get; private set; }

    /// <summary>True while the flyout is open: poll faster, and refresh now if the data is stale.</summary>
    public bool IsForeground
    {
        get => _isForeground;
        set
        {
            _isForeground = value;
            _timer.Interval = value ? ForegroundInterval : BackgroundInterval;
            if (value && (Latest.FetchedAt is not { } at || DateTimeOffset.Now - at > TimeSpan.FromSeconds(15)))
            {
                _ = RefreshAsync();
            }
        }
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

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
        try
        {
            string[] stopIds = stops.Select(s => s.StopId).Distinct().Take(Service.MaxStopsPerRequest).ToArray();
            IReadOnlyList<Arrival> arrivals = await Service.GetArrivalsAsync(stopIds);
            result = new ArrivalsSnapshot(DateTimeOffset.Now, arrivals, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            string message = ex switch
            {
                TransitApiException api => api.Message,
                _ when !ConnectivityService.IsInternetAvailable => "No internet connection. Times will update when you're back online.",
                _ => $"Can't reach {Service.AgencyName} right now. Retrying shortly.",
            };

            // Keep showing the last good arrivals alongside the error.
            result = Latest with { Error = message };
        }

        if (generation == _refreshGeneration)
        {
            Publish(result);
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
