namespace TrayTimes.Transit;

/// <summary>App-lifetime metadata reuse; each caller cancels only its own shared-load waiter.</summary>
public sealed class MetadataTransitService : ITransitService
{
    public static readonly TimeSpan DirectoryLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan PatternLifetime = TimeSpan.FromHours(6);
    public const int RetainedRoutes = 128;
    public const int RetainedStopLists = 256;
    public const int RetainedPatterns = 64;

    private readonly ITransitService _inner;
    private readonly TimeProvider _time;
    private readonly MetadataCache<string, TransitRoute> _routes;
    private readonly MetadataCache<string, TransitDirection> _directions;
    private readonly MetadataCache<(string Route, string Direction), TransitStop> _stops;
    private readonly MetadataCache<string, RoutePattern> _patterns;

    public MetadataTransitService(ITransitService inner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _time = timeProvider ?? TimeProvider.System;
        _routes = new(_time, DirectoryLifetime, 1, Diagnostics, TransitEndpoint.Routes);
        _directions = new(_time, DirectoryLifetime, RetainedRoutes, Diagnostics, TransitEndpoint.Directions);
        _stops = new(_time, DirectoryLifetime, RetainedStopLists, Diagnostics, TransitEndpoint.Stops);
        _patterns = new(_time, PatternLifetime, RetainedPatterns, Diagnostics, TransitEndpoint.Patterns);
    }

    public string AgencyName => _inner.AgencyName;
    public bool IsConfigured => _inner.IsConfigured;
    public int MaxStopsPerRequest => _inner.MaxStopsPerRequest;
    public TransitRequestDiagnostics? Diagnostics => _inner.Diagnostics;

    public Task<IReadOnlyList<TransitRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
        _routes.GetAsync(string.Empty, _inner.GetRoutesAsync, cancellationToken);

    public Task<IReadOnlyList<TransitDirection>> GetDirectionsAsync(string routeId, CancellationToken cancellationToken = default) =>
        _directions.GetAsync(routeId, token => _inner.GetDirectionsAsync(routeId, token), cancellationToken);

    public Task<IReadOnlyList<TransitStop>> GetStopsAsync(string routeId, string directionId, CancellationToken cancellationToken = default) =>
        _stops.GetAsync((routeId, directionId), token => _inner.GetStopsAsync(routeId, directionId, token), cancellationToken);

    public Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId, CancellationToken cancellationToken = default) =>
        _patterns.GetAsync(routeId, token => _inner.GetPatternsAsync(routeId, token), cancellationToken);

    public Task<IReadOnlyList<Arrival>> GetArrivalsAsync(IReadOnlyCollection<string> stopIds, CancellationToken cancellationToken = default) =>
        _inner.GetArrivalsAsync(stopIds, cancellationToken);

    public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(IReadOnlyCollection<string> vehicleIds, CancellationToken cancellationToken = default) =>
        _inner.GetVehiclesAsync(vehicleIds, cancellationToken);

    /// <summary>Reads a fresh shape without starting work (for an already validated commute).</summary>
    public bool TryGetPatterns(string routeId, out IReadOnlyList<RoutePattern> patterns) =>
        _patterns.TryGet(routeId, out patterns);

    /// <summary>Future callers use a new generation; existing consumers retain ownership of their loads.</summary>
    public void Invalidate()
    {
        _routes.Invalidate();
        _directions.Invalidate();
        _stops.Invalidate();
        _patterns.Invalidate();
    }

    /// <summary>
    /// Reuses only a complete, fresh directory's original ordered metadata. Older geographic-only
    /// files and incomplete/invalid metadata must fall back to endpoint loads, not inferred order.
    /// </summary>
    public bool SeedDirectory(StopDirectoryMetadata? metadata, DateTimeOffset builtAt)
    {
        TimeSpan age = _time.GetUtcNow() - builtAt;
        if (age < TimeSpan.Zero || age >= DirectoryLifetime || !IsValid(metadata))
        {
            System.Diagnostics.Debug.WriteLine("Stop directory metadata unavailable, incomplete, or expired. Using shared endpoint loads.");
            return false;
        }

        _routes.Seed(string.Empty, metadata!.Routes.Select(r => r.Route).ToArray(), age);
        foreach (DirectoryRouteMetadata route in metadata.Routes)
        {
            _directions.Seed(route.Route.Id, route.Directions.Select(d => d.Direction).ToArray(), age);
            foreach (DirectoryDirectionMetadata direction in route.Directions)
            {
                _stops.Seed((route.Route.Id, direction.Direction.Id), direction.Stops, age);
            }
        }

        return true;
    }

    private static bool IsValid(StopDirectoryMetadata? metadata) =>
        metadata is { Version: 1, Routes.Count: > 0 and <= RetainedRoutes }
        && metadata.Routes.All(r => r is not null && r.Route is not null
            && !string.IsNullOrWhiteSpace(r.Route.Id) && !string.IsNullOrWhiteSpace(r.Route.Name)
            && !string.IsNullOrWhiteSpace(r.Route.DisplayId) && r.Directions is not null
            && r.Directions.All(d => d is not null && d.Direction is not null
                && !string.IsNullOrWhiteSpace(d.Direction.Id) && !string.IsNullOrWhiteSpace(d.Direction.Name)
                && d.Stops is not null && d.Stops.All(s => s is not null
                    && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name)
                    && double.IsFinite(s.Location.Lat) && s.Location.Lat is >= -90 and <= 90
                    && double.IsFinite(s.Location.Lon) && s.Location.Lon is >= -180 and <= 180))
            && r.Directions.Select(d => d.Direction.Id).Distinct(StringComparer.Ordinal).Count() == r.Directions.Count)
        && metadata.Routes.Select(r => r.Route.Id).Distinct(StringComparer.Ordinal).Count() == metadata.Routes.Count
        && metadata.Routes.Sum(r => r.Directions.Count) <= RetainedStopLists;

    private sealed class MetadataCache<TKey, T>(
        TimeProvider time, TimeSpan lifetime, int capacity,
        TransitRequestDiagnostics? diagnostics, TransitEndpoint endpoint) where TKey : notnull
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<TKey, Entry> _entries = [];
        private long _generation;
        private long _accessSequence;

        public async Task<IReadOnlyList<T>> GetAsync(
            TKey key, Func<CancellationToken, Task<IReadOnlyList<T>>> load, CancellationToken cancellationToken)
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
                    entry = new(time.GetTimestamp(), TimeSpan.Zero, _generation);
                    _entries[key] = entry;
                    start = true;
                }

                entry.LastAccess = ++_accessSequence;
                ++entry.Waiters;
            }

            if (reuse is { } inFlight)
            {
                diagnostics?.RecordReuse(endpoint, inFlight, endpoint == TransitEndpoint.Routes ? 0 : 1);
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

        public bool TryGet(TKey key, out IReadOnlyList<T> result)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry? entry)
                    && entry.Completion.Task.IsCompletedSuccessfully && IsFreshOrPending(entry))
                {
                    entry.LastAccess = ++_accessSequence;
                    result = entry.Completion.Task.Result;
                    return true;
                }
            }

            result = [];
            return false;
        }

        public void Seed(TKey key, IReadOnlyList<T> data, TimeSpan age)
        {
            lock (_gate)
            {
                // Do not replace a newer successful endpoint load or a load with active consumers.
                if (_entries.TryGetValue(key, out Entry? existing) && IsFreshOrPending(existing))
                {
                    return;
                }

                _entries.Remove(key);
                MakeRoom();
                Entry entry = new(time.GetTimestamp(), age, _generation) { LastAccess = ++_accessSequence };
                entry.Completion.SetResult(data);
                entry.Cancellation.Dispose();
                _entries[key] = entry;
            }
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                ++_generation;
                _entries.Clear();
            }
        }

        private bool IsFreshOrPending(Entry entry) =>
            !entry.Completion.Task.IsCompleted
            || entry.Completion.Task.IsCompletedSuccessfully
                && entry.InitialAge + time.GetElapsedTime(entry.StartedAt) < lifetime;

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

        private async Task FetchAsync(TKey key, Entry entry, Func<CancellationToken, Task<IReadOnlyList<T>>> load)
        {
            try
            {
                IReadOnlyList<T> result = await load(entry.Cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (entry.Generation != _generation || entry.Cancellation.IsCancellationRequested)
                    {
                        RemoveIfCurrent(key, entry);
                    }

                    entry.Completion.SetResult(result);
                }
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

        private sealed class Entry(long startedAt, TimeSpan initialAge, long generation)
        {
            public long StartedAt { get; } = startedAt;
            public TimeSpan InitialAge { get; } = initialAge;
            public long Generation { get; } = generation;
            public long LastAccess { get; set; }
            public int Waiters { get; set; }
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource<IReadOnlyList<T>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
