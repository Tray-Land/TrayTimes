using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

public sealed record PlaceNotice(string Title, string Text, string? Button = null, Action? Action = null);

/// <summary>What the trip suggestion plans for: the saved places, and the destination the rider picked (null to guess).</summary>
public sealed record SuggestionContext(IReadOnlyList<FavoritePlace> Places, string? DestinationId)
{
    /// <summary>The trip to suggest from <paramref name="here"/> (null when unknown); see <see cref="CommutePlanner.Plan"/>.</summary>
    public CommuteLeg? Leg(GeoPoint? here, DateTimeOffset now) =>
        CommutePlanner.Plan(Places, here, now, Places.FirstOrDefault(p => p.Id == DestinationId));

    public bool Equals(SuggestionContext? other) =>
        other is not null && DestinationId == other.DestinationId && Places.SequenceEqual(other.Places);

    public override int GetHashCode() => HashCode.Combine(DestinationId, Places.Count);
}

public sealed record PlaceLocation(GeoPoint? Point, Task<GeoPoint?>? Refined = null, string? Error = null);

/// <summary>Validated discovery metadata, independent of the age of live predictions.</summary>
/// <param name="Lines">The line directions each stop lists; null lists every route at the stop.</param>
/// <param name="Trips">For a commute, how each listed line gets to the destination.</param>
/// <param name="Plan">For a commute, the planner's whole trips.</param>
/// <param name="EveryLineAt">For a board, the stops that list every line (saved with All routes).</param>
/// <param name="Center">For a board, where it's actually centered, after <see cref="BoardCenter.Resolve"/>.</param>
/// <param name="Revision">Which version of its inputs (such as the saved stops) discovery read, so an isOutdated hook can tell when they've changed.</param>
public sealed record PlaceStops(
    GeoPoint? Origin,
    IReadOnlyList<NearbyStop> Stops,
    PlaceNotice EmptyNotice,
    CommuteLeg? Leg = null,
    IReadOnlySet<StopLine>? Lines = null,
    IReadOnlyDictionary<StopLine, TripTiming>? Trips = null,
    TripPlan? Plan = null,
    IReadOnlySet<string>? EveryLineAt = null,
    BoardCenter? Center = null,
    int Revision = 0)
{
    /// <summary>
    /// The stops whose departures to keep fresh: the listed stops, plus where every ride of the plan's
    /// trip options boards, so a whole trip stays live from the shared batch.
    /// </summary>
    public IReadOnlyList<string> StopIds => [.. Stops
        .Select(n => n.Stop.Id)
        .Concat(Plan is null ? [] : PlaceBoard.BoardingStopIds(Plan))
        .Distinct(StringComparer.Ordinal)];

    public static PlaceStops Unavailable(PlaceNotice notice, CommuteLeg? leg = null) => new(null, [], notice, leg);

    /// <summary>
    /// Which lines the list shows at each stop, now that <paramref name="arrivals"/> say which lines serve
    /// the stops in <see cref="EveryLineAt"/>: every line there, and elsewhere the stop's <see cref="Lines"/>
    /// (all of them when null). A line departing from a stop in EveryLineAt isn't listed again at a stop
    /// that isn't <paramref name="saved"/>, so it isn't repeated.
    /// </summary>
    public Func<StopLine, bool> Listing(IEnumerable<Arrival> arrivals, IReadOnlySet<string> saved)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(saved);
        IReadOnlySet<string> everyLine = EveryLineAt ?? new HashSet<string>();
        HashSet<RouteDirection> shownThere = [.. arrivals
            .Where(a => everyLine.Contains(a.StopId))
            .Select(a => new RouteDirection(a.RouteId, a.DirectionId))];
        return line => everyLine.Contains(line.StopId)
            || ((Lines?.Contains(line) ?? true)
                && (saved.Contains(line.StopId) || !shownThere.Contains(new RouteDirection(line.RouteId, line.DirectionId))));
    }
}

/// <summary>A place monitor's state, whatever it discovers stops for.</summary>
public interface IPlaceArrivalsMonitor
{
    event EventHandler? Updated;

    PlaceStops? Discovery { get; }

    PredictionSnapshot? Snapshot { get; }

    string? LocationError { get; }

    int Version { get; }

    bool IsLoading { get; }

    bool IsForeground { get; set; }

    Task RefreshAsync(bool force = false, bool checkLocation = true);
}

/// <summary>
/// One flyout place's state, such as the board or the trip suggestion. Refresh predictions
/// immediately from valid discovery while checking location independently; only meaningful
/// movement or context changes repeat discovery. Call on the owning UI thread.
/// </summary>
/// <typeparam name="TContext">What stops are discovered for. A context that isn't equal to the last one invalidates discovery.</typeparam>
/// <param name="consumer">Whose browsing stops these are in the shared batch and the request log: each instance registers only its own.</param>
/// <param name="holds">
/// Whether a discovery still answers its context from a location, beyond being near it (a guessed
/// trip can change with the time of day); null when it always does. One that doesn't is dropped at once.
/// </param>
/// <param name="isOutdated">
/// Whether a discovery that still holds should be found again on the next refresh, staying shown
/// until the new one arrives (such as an expired trip plan); null when it never is.
/// </param>
public sealed class PlaceArrivalsMonitor<TContext>(
    PredictionCoordinator predictions,
    TransitConsumer consumer,
    Func<bool, Task<PlaceLocation>> locate,
    Func<TContext, PlaceLocation, CancellationToken, Task<PlaceStops>> discover,
    Func<TContext, PlaceStops, GeoPoint?, bool>? holds = null,
    Func<PlaceStops, bool>? isOutdated = null) : IPlaceArrivalsMonitor
    where TContext : class
{
    private const double MovedMeters = 25;

    private readonly TransitConsumer _consumer = consumer is TransitConsumer.Commute or TransitConsumer.Nearby
        ? consumer
        : throw new ArgumentOutOfRangeException(nameof(consumer));

    private TContext? _context;
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
    public void Configure(TContext context)
    {
        bool same = _context is { } old && EqualityComparer<TContext>.Default.Equals(old, context)
            && (Discovery is not { } found || Holds(context, found, _resolvedLocation));
        _context = context;
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
        if (!_isForeground || _context is not { } context)
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
                discovery = _discoveryTask ??= DiscoverAsync(context, version);
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

    private async Task DiscoverAsync(TContext context, int version)
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

    private async Task DiscoverCoreAsync(TContext context, int version, CancellationToken cancellationToken)
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
            using IDisposable scope = TransitRequestDiagnostics.BeginScope(_consumer, TransitTrigger.RefinedLocation);
            await ResolveAsync(context, new PlaceLocation(precise), version, cancellationToken);
        }
    }

    private bool NeedsDiscovery(TContext context, GeoPoint? point) =>
        Discovery is null || Discovery.Origin is null
        || (_resolvedLocation is null) != (point is null)
        || (_resolvedLocation is { } old && point is { } next && GeoMath.DistanceMeters(old, next) > MovedMeters)
        || !Holds(context, Discovery, point)
        || (isOutdated?.Invoke(Discovery) ?? false);

    private bool Holds(TContext context, PlaceStops found, GeoPoint? point) => holds?.Invoke(context, found, point) ?? true;

    private async Task ResolveAsync(TContext context, PlaceLocation location, int version, CancellationToken cancellationToken)
    {
        PlaceStops found = await discover(context, location, cancellationToken);
        if (!IsCurrent(version))
        {
            return;
        }

        bool sameStops = Discovery is { } previous
            && previous.StopIds.ToHashSet(StringComparer.Ordinal).SetEquals(found.StopIds);
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
                found.StopIds, predictions.BrowsingInterval,
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

    /// <summary>Registers this monitor's stops under its own consumer only, so another monitor's stay registered.</summary>
    private void RegisterStops() =>
        predictions.SetBrowsingStops(_consumer, _isForeground && Discovery is { } found ? found.StopIds : []);
}
