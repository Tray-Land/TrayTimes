using System.Diagnostics.CodeAnalysis;

namespace TrayTimes.Transit;

/// <summary>
/// App-lifetime reuse of everything except live times: area routes, route details, nearby lines,
/// trip plans, stop searches, and route shapes. Lookups by place reuse a result for a nearby
/// point. Each caller cancels only its own waiter on a shared load. Departures pass through, but
/// ask for shapes when a stop hasn't been seen with them lately, so drawing routes never costs a
/// request of its own, and leave their routes' service alerts in <see cref="Alerts"/>. Results
/// (except alerts, which are live) can be exported and seeded across runs.
/// </summary>
public sealed class MetadataTransitService : ITransitService
{
    public static readonly TimeSpan AreaRoutesLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan RouteDetailsLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan NearbyLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromHours(3);
    public static readonly TimeSpan SearchLifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan ShapeLifetime = TimeSpan.FromDays(7);

    /// <summary>How far apart two places can be and still share discovery (nearby lines, a trip's origin).</summary>
    public const double SamePlaceMeters = 150;

    /// <summary>How far apart two trip destinations can be and still share a plan.</summary>
    public const double SameDestinationMeters = 50;

    /// <summary>How far apart two areas can be and still share their route list or a stop search.</summary>
    public const double SameAreaMeters = 5000;

    public const int RetainedAreas = 8;
    public const int RetainedRouteDetails = 64;
    public const int RetainedNearby = 16;
    public const int RetainedPlans = 16;
    public const int RetainedSearches = 32;
    public const int RetainedShapeDirections = 256;
    public const int RetainedCoverage = 512;
    public const int ShapesPerDirection = 6;

    private readonly ITransitService _inner;
    private readonly TimeProvider _time;
    private readonly MetadataCache<GeoPoint, IReadOnlyList<TransitRoute>> _areaRoutes;
    private readonly MetadataCache<string, RouteDetails> _routeDetails;
    private readonly MetadataCache<(GeoPoint Point, int Radius), NearbyLines> _nearby;
    private readonly MetadataCache<(GeoPoint From, TripDestination To, TripTime Time), TripPlan> _plans;
    private readonly MetadataCache<(GeoPoint Near, string Query), IReadOnlyList<TransitStop>> _searches;
    private readonly ReplanGuard _replans;
    private readonly Lock _shapeGate = new();
    private readonly Dictionary<(string Route, string Direction), ShapeEntry> _shapes = [];
    private readonly Dictionary<string, DateTimeOffset> _coverage = new(StringComparer.Ordinal);
    private long _shapeSequence;

    public MetadataTransitService(ITransitService inner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _time = timeProvider ?? TimeProvider.System;
        _areaRoutes = new(_time, AreaRoutesLifetime, RetainedAreas, Diagnostics, TransitEndpoint.Routes, OnLoaded);
        _routeDetails = new(_time, RouteDetailsLifetime, RetainedRouteDetails, Diagnostics, TransitEndpoint.RouteDetails, OnLoaded);
        _nearby = new(_time, NearbyLifetime, RetainedNearby, Diagnostics, TransitEndpoint.NearbyRoutes, OnLoaded);
        _plans = new(_time, PlanLifetime, RetainedPlans, Diagnostics, TransitEndpoint.Plan, OnLoaded);
        _searches = new(_time, SearchLifetime, RetainedSearches, Diagnostics, TransitEndpoint.SearchStops, null);
        _replans = new(_time);
    }

    /// <summary>Raised after a cached result or shape is added, so the app can save a snapshot. May fire on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after new route shapes arrive, so maps can draw lines they were missing. May fire on any thread.</summary>
    public event EventHandler? PatternsChanged;

    public string ProviderName => _inner.ProviderName;

    public bool IsConfigured => _inner.IsConfigured;

    public int MaxStopsPerRequest => _inner.MaxStopsPerRequest;

    public TransitRequestDiagnostics? Diagnostics => _inner.Diagnostics;

    /// <summary>The service alerts of every route seen in departures lately.</summary>
    public AlertBook Alerts { get; } = new();

    public async Task<DepartureBoard> GetDeparturesAsync(IReadOnlyCollection<string> stopIds, bool includeShapes = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stopIds);
        bool withShapes = includeShapes || stopIds.Any(id => !IsCovered(id));
        DepartureBoard board = await _inner.GetDeparturesAsync(stopIds, withShapes, cancellationToken);
        Alerts.Record(board.Alerts);
        if (withShapes)
        {
            DateTimeOffset now = _time.GetUtcNow();
            lock (_shapeGate)
            {
                foreach (string id in stopIds)
                {
                    _coverage[id] = now;
                }

                while (_coverage.Count > RetainedCoverage)
                {
                    _coverage.Remove(_coverage.MinBy(c => c.Value).Key);
                }
            }

            if (board.Shapes.Count > 0)
            {
                AddShapes(board.Shapes);
            }
            else
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        return board;
    }

    public Task<NearbyLines> GetNearbyLinesAsync(GeoPoint point, int radiusMeters, CancellationToken cancellationToken = default)
    {
        (GeoPoint, int) key = _nearby.Nearest(k => k.Radius == radiusMeters ? Within(k.Point, point, SamePlaceMeters) : null, (point, radiusMeters));
        return _nearby.GetAsync(key, token => WithShapesAsync(_inner.GetNearbyLinesAsync(point, radiusMeters, token), r => r.Shapes), cancellationToken);
    }

    public Task<TripPlan> PlanTripAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default) =>
        PlanTripAsync(from, new TripDestination(to), cancellationToken);

    public Task<TripPlan> PlanTripAsync(GeoPoint from, TripDestination to, CancellationToken cancellationToken = default) =>
        PlanTripAsync(from, to, default, cancellationToken);

    public Task<TripPlan> PlanTripAsync(GeoPoint from, TripDestination to, TripTime time, CancellationToken cancellationToken = default) =>
        _plans.GetAsync(PlanKey(from, to, time), LoadPlan(from, to, time), cancellationToken);

    /// <summary>
    /// Plans a trip again in place of its cached plan, for when that plan's trips can no longer be made
    /// (the last connection is gone). To protect the call budget, a trip is planned again at most once
    /// per <see cref="ReplanGuard.Interval"/>; until then this returns the cached plan. An expired plan
    /// doesn't need this: <see cref="PlanTripAsync"/> plans it again.
    /// </summary>
    public Task<TripPlan> ReplanTripAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default)
        => ReplanTripAsync(from, new TripDestination(to), cancellationToken);

    public Task<TripPlan> ReplanTripAsync(GeoPoint from, TripDestination to, CancellationToken cancellationToken = default) =>
        ReplanTripAsync(from, to, default, force: false, cancellationToken);

    /// <summary>
    /// Plans a trip again now, for a caller that keeps its own once-per-<see cref="ReplanGuard.Interval"/> guard (an
    /// active trip), so another caller's replan of about the same places can't hand it back the plan it gave up on.
    /// It still holds the shared guard for other callers.
    /// </summary>
    /// <param name="time">When the trip leaves, so a leave-at time the rider hasn't reached yet is kept.</param>
    public Task<TripPlan> ForceReplanTripAsync(GeoPoint from, TripDestination to, TripTime time, CancellationToken cancellationToken = default) =>
        ReplanTripAsync(from, to, time, force: true, cancellationToken);

    /// <summary>
    /// Whether <paramref name="plan"/> is still the fresh cached plan for about this trip. Whoever holds a
    /// plan checks this on refresh, and asks <see cref="PlanTripAsync"/> again once it isn't (it expired,
    /// or the trip was planned again). Never starts a request.
    /// </summary>
    public bool IsCurrentPlan(GeoPoint from, GeoPoint to, TripPlan plan) =>
        IsCurrentPlan(from, new TripDestination(to), plan);

    public bool IsCurrentPlan(GeoPoint from, TripDestination to, TripPlan plan) =>
        IsCurrentPlan(from, to, default, plan);

    public bool IsCurrentPlan(GeoPoint from, TripDestination to, TripTime time, TripPlan plan) =>
        _plans.TryGetFresh(PlanKey(from, to, time), out TripPlan? cached) && ReferenceEquals(cached, plan);

    /// <summary>Retains a searched destination's fresh plan after explicit Save as place, without a new request.</summary>
    public void SaveDestinationPlan(GeoPoint from, TripDestination to)
    {
        foreach (var entry in _plans.Completed().Where(e => e.Key == PlanKey(from, to with { Persist = false }, e.Key.Time)))
        {
            _plans.Seed((entry.Key.From, entry.Key.To with { Persist = true }, entry.Key.Time), entry.Value, _time.GetUtcNow() - entry.FetchedAt);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<IReadOnlyList<TransitStop>> SearchStopsAsync(GeoPoint near, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        (GeoPoint, string) key = _searches.Nearest(k => k.Query == normalized ? Within(k.Near, near, SameAreaMeters) : null, (near, normalized));
        return _searches.GetAsync(key, token => _inner.SearchStopsAsync(near, query, token), cancellationToken);
    }

    public Task<IReadOnlyList<TransitRoute>> GetRoutesNearAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        GeoPoint key = _areaRoutes.Nearest(k => Within(k, point, SameAreaMeters), point);
        return _areaRoutes.GetAsync(key, token => _inner.GetRoutesNearAsync(point, token), cancellationToken);
    }

    public Task<RouteDetails> GetRouteDetailsAsync(string routeId, CancellationToken cancellationToken = default) =>
        _routeDetails.GetAsync(routeId, token => WithShapesAsync(_inner.GetRouteDetailsAsync(routeId, token), r => r.Shapes), cancellationToken);

    public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(string routeId, string? directionId = null, CancellationToken cancellationToken = default) =>
        _inner.GetVehiclesAsync(routeId, directionId, cancellationToken);

    /// <summary>Cached shapes for a route, in one direction or (with null) all of them. Never starts a request.</summary>
    public IReadOnlyList<RoutePattern> GetCachedPatterns(string routeId, string? directionId = null)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_shapeGate)
        {
            return [.. _shapes
                .Where(s => string.Equals(s.Key.Route, routeId, StringComparison.OrdinalIgnoreCase)
                    && (directionId is null || string.Equals(s.Key.Direction, directionId, StringComparison.OrdinalIgnoreCase))
                    && now - s.Value.FetchedAt < ShapeLifetime)
                .SelectMany(s => s.Value.Patterns)];
        }
    }

    /// <summary>A copy of every fresh result worth keeping across runs.</summary>
    public TransitCacheSnapshot Export()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<CachedShape> shapes;
        List<CachedShapeCoverage> coverage;
        lock (_shapeGate)
        {
            shapes = [.. _shapes.Values
                .Where(e => now - e.FetchedAt < ShapeLifetime)
                .SelectMany(e => e.Patterns.Select(p => new CachedShape(p.Id, p.RouteId, p.DirectionId, GeoMath.EncodePolyline(p.Points), e.FetchedAt)))];
            coverage = [.. _coverage.Where(c => now - c.Value < ShapeLifetime).Select(c => new CachedShapeCoverage(c.Key, c.Value))];
        }

        return new TransitCacheSnapshot(
            TransitCacheSnapshot.CurrentVersion,
            [.. _areaRoutes.Completed().Select(e => new CachedAreaRoutes(e.Key, e.FetchedAt, e.Value))],
            [.. _routeDetails.Completed().Select(e => new CachedRouteDetails(e.FetchedAt, e.Value.Route, e.Value.Itineraries))],
            [.. _nearby.Completed().Select(e => new CachedNearbyLines(e.Key.Point, e.Key.Radius, e.FetchedAt, e.Value.Lines))],
            [.. _plans.Completed().Where(e => e.Key.To.Persist)
                .Select(e => new CachedTripPlan(e.Key.From, e.Key.To.Location, e.FetchedAt, e.Value.Itineraries, e.Key.To.StopId, e.Key.Time))],
            shapes,
            coverage);
    }

    /// <summary>Seeds results saved by <see cref="Export"/>; stale or malformed entries are skipped, and newer loads win.</summary>
    public void Import(TransitCacheSnapshot? snapshot)
    {
        if (snapshot is not { Version: TransitCacheSnapshot.CurrentVersion })
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (CachedAreaRoutes entry in snapshot.AreaRoutes ?? [])
        {
            if (entry?.Routes is { } routes && routes.All(IsValid))
            {
                _areaRoutes.Seed(entry.Point, routes, now - entry.FetchedAt);
            }
        }

        foreach (CachedRouteDetails entry in snapshot.RouteDetails ?? [])
        {
            if (entry?.Route is { } route && IsValid(route) && entry.Itineraries is { } itineraries && itineraries.All(IsValid))
            {
                _routeDetails.Seed(route.Id, new RouteDetails(route, itineraries, []), now - entry.FetchedAt);
            }
        }

        foreach (CachedNearbyLines entry in snapshot.NearbyLines ?? [])
        {
            if (entry?.Lines is { } lines && lines.All(IsValid))
            {
                _nearby.Seed((entry.Point, entry.RadiusMeters), new NearbyLines(lines, []), now - entry.FetchedAt);
            }
        }

        foreach (CachedTripPlan entry in snapshot.TripPlans ?? [])
        {
            if (entry?.Itineraries is { } itineraries && itineraries.All(IsValid))
            {
                _plans.Seed((entry.From, new TripDestination(entry.To, entry.DestinationStopId), entry.Time), new TripPlan(itineraries, []), now - entry.FetchedAt);
            }
        }

        lock (_shapeGate)
        {
            foreach (IGrouping<(string, string), CachedShape> group in (snapshot.Shapes ?? [])
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.RouteId) && s.DirectionId is not null && now - s.FetchedAt < ShapeLifetime)
                .GroupBy(s => (s.RouteId, s.DirectionId)))
            {
                RoutePattern[] patterns = [.. group
                    .Select(s => new RoutePattern(s.Id ?? string.Empty, s.RouteId, s.DirectionId, GeoMath.DecodePolyline(s.Polyline)))
                    .Where(p => p.Points.Count > 1)
                    .Take(ShapesPerDirection)];
                if (patterns.Length > 0 && !_shapes.ContainsKey(group.Key))
                {
                    _shapes[group.Key] = new ShapeEntry(patterns, group.Max(s => s.FetchedAt), ++_shapeSequence);
                }
            }

            foreach (CachedShapeCoverage entry in snapshot.ShapeCoverage ?? [])
            {
                if (entry is { StopId.Length: > 0 } && now - entry.CoveredAt < ShapeLifetime && !_coverage.ContainsKey(entry.StopId))
                {
                    _coverage[entry.StopId] = entry.CoveredAt;
                }
            }

            TrimShapes();
        }
    }

    private static double? Within(GeoPoint a, GeoPoint b, double meters)
    {
        double distance = GeoMath.DistanceMeters(a, b);
        return distance <= meters ? distance : null;
    }

    private static bool IsValid(GeoPoint point) =>
        double.IsFinite(point.Lat) && point.Lat is >= -90 and <= 90 && double.IsFinite(point.Lon) && point.Lon is >= -180 and <= 180;

    private static bool IsValid(TransitRoute? route) =>
        route is not null && !string.IsNullOrWhiteSpace(route.Id) && !string.IsNullOrWhiteSpace(route.DisplayId) && route.Name is not null;

    private static bool IsValid(TransitStop? stop) =>
        stop is not null && !string.IsNullOrWhiteSpace(stop.Id) && stop.Name is not null && IsValid(stop.Location);

    private static bool IsValid(TransitDirection? direction) => direction is { Id: not null, Name: not null };

    private static bool IsValid(RouteItinerary? itinerary) =>
        itinerary is not null && IsValid(itinerary.Direction) && itinerary.Stops is not null && itinerary.Stops.All(IsValid);

    private static bool IsValid(NearbyLine? line) =>
        line is not null && IsValid(line.Route) && IsValid(line.Direction) && IsValid(line.Stop) && double.IsFinite(line.Meters);

    private static bool IsValid(TripItinerary? itinerary) =>
        itinerary is { Legs: { Count: > 0 } legs } && legs.All(IsValid) && itinerary.Arrives >= itinerary.Departs;

    private static bool IsValid(TripLeg? leg) => leg switch
    {
        WalkLeg walk => walk.Ends >= walk.Starts && double.IsFinite(walk.Meters) && walk.Path is not null,
        RideLeg ride => ride.Ends >= ride.Starts && ride.Lines is { Count: > 0 } lines && lines.All(IsValid),
        _ => false,
    };

    private static bool IsValid(RideLine? line) =>
        line is not null && IsValid(line.Route) && IsValid(line.Direction) && IsValid(line.BoardAt)
        && (line.AlightAt is null || IsValid(line.AlightAt)) && line.Stops >= 0 && line.Path is not null
        && line.Departures is { Count: > 0 } departures && departures.All(d => d is not null && d.Alights >= d.Departs);

    /// <summary>The cached or pending plan's key for about the same origin and destination, else this trip's own.</summary>
    private (GeoPoint From, TripDestination To, TripTime Time) PlanKey(GeoPoint from, TripDestination to, TripTime time = default) =>
        _plans.Nearest(k => k.Time == time && k.To.StopId == to.StopId && k.To.Persist == to.Persist
            && Within(k.To.Location, to.Location, SameDestinationMeters) is not null
                ? Within(k.From, from, SamePlaceMeters) : null, (from, to, time));

    private Task<TripPlan> ReplanTripAsync(GeoPoint from, TripDestination to, TripTime time, bool force, CancellationToken cancellationToken)
    {
        var key = PlanKey(from, to, time);
        if (_replans.TryAcquire(from, to.Location) || force)
        {
            _plans.Forget(key);
        }

        return _plans.GetAsync(key, LoadPlan(from, to, time), cancellationToken);
    }

    private Func<CancellationToken, Task<TripPlan>> LoadPlan(GeoPoint from, TripDestination to, TripTime time = default) =>
        token => WithShapesAsync(_inner.PlanTripAsync(from, to, time, token), r => r.Shapes);

    private bool IsCovered(string stopId)
    {
        lock (_shapeGate)
        {
            return _coverage.TryGetValue(stopId, out DateTimeOffset at) && _time.GetUtcNow() - at < ShapeLifetime;
        }
    }

    private async Task<T> WithShapesAsync<T>(Task<T> load, Func<T, IReadOnlyList<RoutePattern>> shapes)
    {
        T result = await load.ConfigureAwait(false);
        AddShapes(shapes(result));
        return result;
    }

    /// <summary>New shapes replace older ones with the same ID; each route direction keeps its newest few.</summary>
    private void AddShapes(IReadOnlyList<RoutePattern> patterns)
    {
        if (patterns.Count == 0)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_shapeGate)
        {
            foreach (IGrouping<(string, string), RoutePattern> group in patterns.GroupBy(p => (p.RouteId, p.DirectionId)))
            {
                IEnumerable<RoutePattern> kept = _shapes.TryGetValue(group.Key, out ShapeEntry? old) && now - old.FetchedAt < ShapeLifetime
                    ? old.Patterns.Where(p => group.All(n => n.Id != p.Id))
                    : [];
                _shapes[group.Key] = new ShapeEntry([.. group.Concat(kept).Take(ShapesPerDirection)], now, ++_shapeSequence);
            }

            TrimShapes();
        }

        PatternsChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimShapes()
    {
        while (_shapes.Count > RetainedShapeDirections)
        {
            _shapes.Remove(_shapes.MinBy(s => s.Value.Sequence).Key);
        }
    }

    private void OnLoaded() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record ShapeEntry(IReadOnlyList<RoutePattern> Patterns, DateTimeOffset FetchedAt, long Sequence);

    private sealed class MetadataCache<TKey, T>(
        TimeProvider time, TimeSpan lifetime, int capacity,
        TransitRequestDiagnostics? diagnostics, TransitEndpoint endpoint, Action? loaded) where TKey : notnull
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<TKey, Entry> _entries = [];
        private long _accessSequence;

        public async Task<T> GetAsync(TKey key, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry;
            bool start = false;
            bool? reuse = null;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry? existing) && IsFreshOrPending(existing))
                {
                    entry = existing;
                    reuse = !entry.Completion.Task.IsCompletedSuccessfully;
                }
                else
                {
                    _entries.Remove(key);
                    MakeRoom();
                    entry = new(time.GetTimestamp(), TimeSpan.Zero);
                    _entries[key] = entry;
                    start = true;
                }

                entry.LastAccess = ++_accessSequence;
                ++entry.Waiters;
            }

            if (reuse is { } inFlight)
            {
                diagnostics?.RecordReuse(endpoint, inFlight, 1);
            }

            if (start)
            {
                _ = FetchAsync(key, entry, load);
            }

            try
            {
                return await entry.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    if (--entry.Waiters == 0)
                    {
                        if (entry.Completion.Task.IsCompleted)
                        {
                            entry.Cancellation.Dispose();
                        }
                        else
                        {
                            RemoveIfCurrent(key, entry);
                            entry.Cancellation.Cancel();
                        }
                    }
                }
            }
        }

        /// <summary>The fresh or pending key closest by <paramref name="distance"/> (null: not a candidate), else <paramref name="fallback"/>.</summary>
        public TKey Nearest(Func<TKey, double?> distance, TKey fallback)
        {
            lock (_gate)
            {
                TKey best = fallback;
                double bestDistance = double.MaxValue;
                foreach ((TKey key, Entry entry) in _entries)
                {
                    if (IsFreshOrPending(entry) && distance(key) is { } d && d < bestDistance)
                    {
                        best = key;
                        bestDistance = d;
                    }
                }

                return best;
            }
        }

        /// <summary>The loaded value for <paramref name="key"/>, while it's fresh.</summary>
        public bool TryGetFresh(TKey key, [MaybeNullWhen(false)] out T value)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry? entry) && entry.Completion.Task.IsCompletedSuccessfully && IsFreshOrPending(entry))
                {
                    value = entry.Completion.Task.Result;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public IReadOnlyList<(TKey Key, T Value, DateTimeOffset FetchedAt)> Completed()
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                return [.. _entries
                    .Where(p => p.Value.Completion.Task.IsCompletedSuccessfully && IsFreshOrPending(p.Value))
                    .Select(p => (p.Key, p.Value.Completion.Task.Result, now - Age(p.Value)))];
            }
        }

        /// <summary>Drops a completed entry so the next request loads it again. A pending load is kept, since it's already new.</summary>
        public void Forget(TKey key)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry? entry) && entry.Completion.Task.IsCompleted)
                {
                    _entries.Remove(key);
                }
            }
        }

        public void Seed(TKey key, T data, TimeSpan age)
        {
            lock (_gate)
            {
                // Do not replace a newer successful load or a load with active consumers.
                if (age < TimeSpan.Zero || age >= lifetime || (_entries.TryGetValue(key, out Entry? existing) && IsFreshOrPending(existing)))
                {
                    return;
                }

                _entries.Remove(key);
                MakeRoom();
                Entry entry = new(time.GetTimestamp(), age) { LastAccess = ++_accessSequence };
                entry.Completion.SetResult(data);
                entry.Cancellation.Dispose();
                _entries[key] = entry;
            }
        }

        private TimeSpan Age(Entry entry) => entry.InitialAge + time.GetElapsedTime(entry.StartedAt);

        private bool IsFreshOrPending(Entry entry) =>
            !entry.Completion.Task.IsCompleted
            || (entry.Completion.Task.IsCompletedSuccessfully && Age(entry) < lifetime);

        private void MakeRoom()
        {
            if (_entries.Count < capacity)
            {
                return;
            }

            KeyValuePair<TKey, Entry>[] completed = [.. _entries.Where(p => p.Value.Completion.Task.IsCompleted)
                .OrderBy(p => p.Value.LastAccess).Take(1)];
            if (completed.Length == 0)
            {
                throw new InvalidOperationException("The shared metadata load limit was reached.");
            }

            _entries.Remove(completed[0].Key);
        }

        private async Task FetchAsync(TKey key, Entry entry, Func<CancellationToken, Task<T>> load)
        {
            try
            {
                T result = await load(entry.Cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (entry.Cancellation.IsCancellationRequested)
                    {
                        RemoveIfCurrent(key, entry);
                    }

                    entry.Completion.SetResult(result);
                }

                loaded?.Invoke();
            }
            catch (OperationCanceledException ex)
            {
                lock (_gate)
                {
                    RemoveIfCurrent(key, entry);
                    entry.Completion.SetCanceled(ex.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    RemoveIfCurrent(key, entry);
                    entry.Completion.SetException(ex);
                    _ = entry.Completion.Task.Exception; // Observe even when every waiter abandoned the load.
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (entry.Waiters == 0)
                    {
                        entry.Cancellation.Dispose();
                    }
                }
            }
        }

        private void RemoveIfCurrent(TKey key, Entry entry)
        {
            if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
            }
        }

        private sealed class Entry(long startedAt, TimeSpan initialAge)
        {
            public long StartedAt { get; } = startedAt;
            public TimeSpan InitialAge { get; } = initialAge;
            public long LastAccess { get; set; }
            public int Waiters { get; set; }
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
