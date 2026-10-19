using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

public sealed record PlaceNotice(string Title, string Text, string? Button = null, Action? Action = null);

public sealed record PlaceContext(FlyoutMode Mode, IReadOnlyList<FavoritePlace> Places, string? DestinationId);

public sealed record PlaceLocation(GeoPoint? Point, Task<GeoPoint?>? Refined = null, string? Error = null);

/// <summary>Validated discovery metadata, independent of the age of live predictions.</summary>
/// <param name="Lines">The line directions each stop lists; null lists every route at the stop.</param>
/// <param name="Trips">For a commute, how each listed line gets to the destination.</param>
public sealed record PlaceStops(
    GeoPoint? Origin,
    IReadOnlyList<NearbyStop> Stops,
    PlaceNotice EmptyNotice,
    CommuteLeg? Leg = null,
    IReadOnlySet<StopLine>? Lines = null,
    IReadOnlyDictionary<StopLine, TripTiming>? Trips = null)
{
    public static PlaceStops Unavailable(PlaceNotice notice, CommuteLeg? leg = null) => new(null, [], notice, leg);
}

/// <summary>
/// One flyout's place state. Refresh predictions immediately from valid discovery while checking
/// location independently; only meaningful movement or context changes repeat discovery.
/// Call on the owning UI thread.
/// </summary>
public sealed class PlaceArrivalsMonitor(
    PredictionCoordinator predictions,
    Func<bool, Task<PlaceLocation>> locate,
    Func<PlaceContext, PlaceLocation, CancellationToken, Task<PlaceStops>> discover,
    TimeProvider? timeProvider = null)
{
    public const double MovedMeters = 25;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private PlaceContext? _context;
    private GeoPoint? _resolvedLocation;
    private bool _hasLocation;
    private bool _isForeground;
    private int _version;
    private int _loading;
    private bool _forceDiscovery;
    private Task? _discoveryTask;
    private CancellationTokenSource? _discoveryCancellation;
    private CancellationTokenSource? _predictionCancellation;

    public event EventHandler? Updated;

    public PlaceStops? Discovery { get; private set; }

    public PredictionSnapshot? Snapshot { get; private set; }

    public string? LocationError { get; private set; }

    public int Version => _version;

    public bool IsLoading => _isForeground && _loading > 0;

    public bool IsForeground
    {
        get => _isForeground;
        set
        {
            if (_isForeground == value)
            {
                return;
            }

            _isForeground = value;
            ++_version;
            _loading = 0;
            _forceDiscovery = false;
            _discoveryTask = null;
            _discoveryCancellation?.Cancel();
            _predictionCancellation?.Cancel();
            RegisterStops();
        }
    }

    /// <summary>Invalidate incompatible discovery before rendering, including guessed-trip changes.</summary>
    public void Configure(PlaceContext context)
    {
        if (context.Mode is not (FlyoutMode.Commute or FlyoutMode.Nearby))
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        bool same = _context?.Mode == context.Mode
            && (context.Mode == FlyoutMode.Nearby
                || (_context.DestinationId == context.DestinationId && _context.Places.SequenceEqual(context.Places)));
        if (same && Discovery is { } found)
        {
            same = context.Mode != FlyoutMode.Commute || found.Leg == Plan(context, _resolvedLocation);
        }

        _context = context with { Places = context.Places.ToArray() };
        if (!same)
        {
            ++_version;
            _loading = 0;
            _forceDiscovery = false;
            _discoveryTask = null;
            _discoveryCancellation?.Cancel();
            _predictionCancellation?.Cancel();
            Discovery = null;
            Snapshot = null;
            LocationError = null;
            RegisterStops();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Force affects predictions, not discovery validity. Failures propagate to the caller's
    /// existing last-good/error presentation.
    /// </summary>
    public async Task RefreshAsync(bool force = false, bool checkLocation = true)
    {
        if (!_isForeground || _context is null)
        {
            return;
        }

        int version = _version;
        _forceDiscovery |= force && Discovery is null;
        ++_loading;
        Updated?.Invoke(this, EventArgs.Empty);
        Task arrivals = Discovery is { } found
            ? RefreshPredictionsAsync(found, version, force)
            : Task.CompletedTask;
        Task discovery = Task.CompletedTask;
        try
        {
            if (checkLocation || Discovery is null)
            {
                discovery = _discoveryTask ??= DiscoverAsync(_context, version);
            }

            await Task.WhenAll(arrivals, discovery);
        }
        finally
        {
            if (ReferenceEquals(_discoveryTask, discovery))
            {
                _discoveryTask = null;
            }

            if (IsCurrent(version))
            {
                --_loading;
                Updated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task DiscoverAsync(PlaceContext context, int version)
    {
        using CancellationTokenSource cancellation = new();
        _discoveryCancellation = cancellation;
        try
        {
            await DiscoverCoreAsync(context, version, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded, hidden, or reconfigured: its network work stops instead of finishing unseen.
        }
        finally
        {
            if (ReferenceEquals(_discoveryCancellation, cancellation))
            {
                _discoveryCancellation = null;
            }
        }
    }

    private async Task DiscoverCoreAsync(PlaceContext context, int version, CancellationToken cancellationToken)
    {
        // A destination/configuration change can use the last validated location without
        // waiting for another Windows fix. Its new routes are still fully validated.
        if (Discovery is null && _hasLocation)
        {
            await ResolveAsync(context, new PlaceLocation(_resolvedLocation), version, cancellationToken);
        }

        if (!IsCurrent(version))
        {
            return;
        }

        PlaceLocation location = await locate(!_hasLocation);
        if (!IsCurrent(version))
        {
            return;
        }

        LocationError = location.Error;
        if (location.Point is null && location.Error is not null && Discovery?.Origin is not null && _resolvedLocation is not null)
        {
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (NeedsDiscovery(context, location.Point))
        {
            await ResolveAsync(context, location, version, cancellationToken);
        }

        if (location.Refined is not null && await location.Refined is { } precise
            && IsCurrent(version) && NeedsDiscovery(context, precise))
        {
            using IDisposable scope = TransitRequestDiagnostics.BeginScope(Consumer, TransitTrigger.RefinedLocation);
            await ResolveAsync(context, new PlaceLocation(precise), version, cancellationToken);
        }
    }

    private bool NeedsDiscovery(PlaceContext context, GeoPoint? point) =>
        Discovery is null || Discovery.Origin is null
        || (_resolvedLocation is null) != (point is null)
        || (_resolvedLocation is { } old && point is { } next && GeoMath.DistanceMeters(old, next) > MovedMeters)
        || (context.Mode == FlyoutMode.Commute && Discovery.Leg != Plan(context, point));

    private CommuteLeg? Plan(PlaceContext context, GeoPoint? point) =>
        CommutePlanner.Plan(context.Places, point, _time.GetLocalNow(),
            context.Places.FirstOrDefault(p => p.Id == context.DestinationId));

    private async Task ResolveAsync(PlaceContext context, PlaceLocation location, int version, CancellationToken cancellationToken)
    {
        PlaceStops found = await discover(context, location, cancellationToken);
        if (!IsCurrent(version))
        {
            return;
        }

        string[] ids = [.. found.Stops.Select(n => n.Stop.Id)];
        bool sameStops = Discovery is { } previous
            && previous.Stops.Select(n => n.Stop.Id).ToHashSet(StringComparer.Ordinal).SetEquals(ids);
        bool force = _forceDiscovery;
        _forceDiscovery = false;
        Discovery = found;
        _resolvedLocation = location.Point;
        _hasLocation = true;
        if (!sameStops)
        {
            Snapshot = null;
        }

        RegisterStops();
        Updated?.Invoke(this, EventArgs.Empty);
        await RefreshPredictionsAsync(found, version, force);
    }

    private async Task RefreshPredictionsAsync(PlaceStops found, int version, bool force)
    {
        if (!IsCurrent(version))
        {
            return;
        }

        using CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = _predictionCancellation;
        _predictionCancellation = cancellation;
        try
        {
            Task<PredictionSnapshot> pending = predictions.GetAsync(
                [.. found.Stops.Select(n => n.Stop.Id)], predictions.BrowsingInterval,
                force, combineBrowsing: true, cancellationToken: cancellation.Token);
            previous?.Cancel();
            PredictionSnapshot snapshot = await pending;
            if (IsCurrent(version) && ReferenceEquals(Discovery, found))
            {
                Snapshot = snapshot;
                Updated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Only this waiter was abandoned; shared tracking retains its own ownership.
        }
        finally
        {
            if (ReferenceEquals(_predictionCancellation, cancellation))
            {
                _predictionCancellation = null;
            }
        }
    }

    private bool IsCurrent(int version) => _isForeground && version == _version;

    private TransitConsumer Consumer => _context?.Mode == FlyoutMode.Commute ? TransitConsumer.Commute : TransitConsumer.Nearby;

    private void RegisterStops()
    {
        predictions.SetBrowsingStops(TransitConsumer.Commute, []);
        predictions.SetBrowsingStops(TransitConsumer.Nearby, []);
        if (_isForeground && Discovery is { } found)
        {
            predictions.SetBrowsingStops(Consumer, [.. found.Stops.Select(n => n.Stop.Id)]);
        }
    }
}
