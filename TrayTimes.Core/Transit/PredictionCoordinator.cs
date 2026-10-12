namespace TrayTimes.Transit;

/// <summary>A stop-scoped live result, dated from the oldest contributing HTTP request's start.</summary>
public sealed record PredictionSnapshot(IReadOnlyList<Arrival> Arrivals, DateTimeOffset FetchedAt);

/// <summary>
/// App-wide raw prediction sharing. Known browsing stops can share a due batch without delaying
/// tracking; each caller owns its waiter, not the lifetime of other callers' shared HTTP work.
/// </summary>
public sealed class PredictionCoordinator
{
    public static readonly TimeSpan BrowsingInterval = TimeSpan.FromSeconds(30);
    public const int RetainedStops = 128;

    private readonly Lock _gate = new();
    private readonly ITransitService _service;
    private readonly TimeProvider _time;
    private readonly Dictionary<TransitConsumer, string[]> _browsing = [];
    private readonly Dictionary<string, Request> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StopSnapshot> _cache = new(StringComparer.Ordinal);
    private long _sequence;

    public PredictionCoordinator(ITransitService service, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(service.MaxStopsPerRequest);
        _service = service;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Register known visible stops, or an empty set on hide/invalidation. Does no HTTP work.</summary>
    public void SetBrowsingStops(TransitConsumer consumer, IReadOnlyCollection<string> stopIds)
    {
        if (consumer is not (TransitConsumer.Favorites or TransitConsumer.Commute or TransitConsumer.Nearby))
        {
            throw new ArgumentOutOfRangeException(nameof(consumer));
        }

        string[] ids = NormalizeIds(stopIds);
        if (ids.Length > RetainedStops)
        {
            throw new ArgumentOutOfRangeException(nameof(stopIds), "The browsing stop set exceeds the retained-stop limit.");
        }

        lock (_gate)
        {
            if (ids.Length == 0)
            {
                _browsing.Remove(consumer);
            }
            else
            {
                _browsing[consumer] = ids;
            }
        }
    }

    /// <summary>
    /// Share pending work and successful data strictly younger than maxAge. Force bypasses cached
    /// success, never pending work. Tracking callers leave combineBrowsing false.
    /// </summary>
    public async Task<PredictionSnapshot> GetAsync(
        IReadOnlyCollection<string> stopIds,
        TimeSpan maxAge,
        bool force = false,
        bool combineBrowsing = false,
        CancellationToken cancellationToken = default)
    {
        string[] ids = NormalizeIds(stopIds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Length == 0)
        {
            return new PredictionSnapshot([], _time.GetLocalNow());
        }

        Dictionary<string, StopSnapshot> snapshots = new(StringComparer.Ordinal);
        Dictionary<string, Request> requested = new(StringComparer.Ordinal);
        HashSet<Request> waiters = [];
        List<Request> starts = [];
        int cachedCount = 0;
        int sharedCount = 0;
        lock (_gate)
        {
            HashSet<string> missing = new(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (_pending.TryGetValue(id, out Request? pending))
                {
                    requested[id] = pending;
                    waiters.Add(pending);
                    ++sharedCount;
                }
                else if (!force && IsFresh(id, maxAge))
                {
                    snapshots[id] = _cache[id];
                    ++cachedCount;
                }
                else
                {
                    missing.Add(id);
                }
            }

            string[] required = [.. missing];
            if (required.Length > 0 && combineBrowsing && !force)
            {
                // Fill the caller's batches, but don't start an unrelated extra batch or make
                // this caller wait for/fail because of a batch containing none of its stops.
                int capacity = (_service.MaxStopsPerRequest - required.Length % _service.MaxStopsPerRequest)
                    % _service.MaxStopsPerRequest;
                foreach (string id in _browsing.Values.SelectMany(s => s))
                {
                    if (capacity == 0)
                    {
                        break;
                    }

                    if (!_pending.ContainsKey(id) && !IsFresh(id, BrowsingInterval))
                    {
                        if (missing.Add(id))
                        {
                            --capacity;
                        }
                    }
                }
            }

            foreach (string[] batch in required.Concat(missing.Except(required)).Chunk(_service.MaxStopsPerRequest))
            {
                Request request = new(batch, ++_sequence, _time.GetTimestamp(), _time.GetLocalNow());
                starts.Add(request);
                waiters.Add(request);
                foreach (string id in batch)
                {
                    _pending[id] = request;
                    if (ids.Contains(id, StringComparer.Ordinal))
                    {
                        requested[id] = request;
                    }
                }
            }

            foreach (Request request in waiters)
            {
                ++request.Waiters;
            }
        }

        if (cachedCount > 0)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Predictions, inFlight: false, cachedCount);
        }

        if (sharedCount > 0)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Predictions, inFlight: true, sharedCount);
        }

        try
        {
            foreach (Request request in starts)
            {
                _ = FetchAsync(request);
            }

            await Task.WhenAll(waiters.Select(r => r.Completion.Task)).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach ((string id, Request request) in requested)
            {
                snapshots[id] = (await request.Completion.Task)[id];
            }

            // A slow shared response must not make tracking accept an old browsing snapshot.
            // Retry only reused stops; a caller's own slow HTTP attempt is unavoidable latency.
            string[] expired = [.. ids.Where(id => (!requested.TryGetValue(id, out Request? r) || !starts.Contains(r))
                && _time.GetElapsedTime(snapshots[id].StartedAt) >= maxAge)];
            if (expired.Length > 0)
            {
                PredictionSnapshot refreshed = await GetAsync(expired, maxAge, force: true, cancellationToken: cancellationToken);
                HashSet<string> refreshedIds = new(expired, StringComparer.Ordinal);
                IReadOnlyList<Arrival> arrivals = snapshots.Where(s => !refreshedIds.Contains(s.Key))
                    .SelectMany(s => s.Value.Arrivals).Concat(refreshed.Arrivals).OrderBy(a => a.PredictedTime).ToArray();
                DateTimeOffset at = snapshots.Where(s => !refreshedIds.Contains(s.Key))
                    .Select(s => s.Value.FetchedAt).Append(refreshed.FetchedAt).Min();
                return new PredictionSnapshot(arrivals, at);
            }

            return new PredictionSnapshot(
                snapshots.Values.SelectMany(s => s.Arrivals).OrderBy(a => a.PredictedTime).ToArray(),
                snapshots.Values.Min(s => s.FetchedAt));
        }
        finally
        {
            Release(waiters);
        }
    }

    private bool IsFresh(string id, TimeSpan maxAge) =>
        _cache.TryGetValue(id, out StopSnapshot? cached) && _time.GetElapsedTime(cached.StartedAt) < maxAge;

    private async Task FetchAsync(Request request)
    {
        try
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            IReadOnlyList<Arrival> arrivals = await _service.GetArrivalsAsync(request.Ids, request.Cancellation.Token);
            Dictionary<string, StopSnapshot> result = request.Ids.ToDictionary(id => id,
                id => new StopSnapshot(arrivals.Where(a => a.StopId == id).ToArray(),
                    request.StartedAt, request.FetchedAt, request.Sequence), StringComparer.Ordinal);
            lock (_gate)
            {
                if (!request.Cancellation.IsCancellationRequested)
                {
                    foreach ((string id, StopSnapshot snapshot) in result)
                    {
                        if (!_cache.TryGetValue(id, out StopSnapshot? old) || old.Sequence < snapshot.Sequence)
                        {
                            _cache[id] = snapshot;
                        }
                    }

                    while (_cache.Count > RetainedStops)
                    {
                        _cache.Remove(_cache.MinBy(s => s.Value.Sequence).Key);
                    }
                }

                RemovePending(request);
                request.Completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                RemovePending(request);
                request.Completion.TrySetCanceled(ex.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                RemovePending(request);
                request.Completion.TrySetException(ex);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (request.Waiters == 0)
                {
                    request.Cancellation.Dispose();
                }
            }
        }
    }

    private void Release(IEnumerable<Request> requests)
    {
        lock (_gate)
        {
            foreach (Request request in requests)
            {
                if (--request.Waiters == 0)
                {
                    if (request.Completion.Task.IsCompleted)
                    {
                        request.Cancellation.Dispose();
                    }
                    else
                    {
                        RemovePending(request);
                        request.Cancellation.Cancel();
                    }
                }
            }
        }
    }

    private void RemovePending(Request request)
    {
        foreach (string id in request.Ids)
        {
            if (_pending.TryGetValue(id, out Request? current) && ReferenceEquals(current, request))
            {
                _pending.Remove(id);
            }
        }
    }

    private static string[] NormalizeIds(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Stop IDs must not be empty.", nameof(ids));
        }

        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    private sealed record StopSnapshot(IReadOnlyList<Arrival> Arrivals, long StartedAt, DateTimeOffset FetchedAt, long Sequence);

    private sealed class Request(string[] ids, long sequence, long startedAt, DateTimeOffset fetchedAt)
    {
        public string[] Ids { get; } = ids;
        public long Sequence { get; } = sequence;
        public long StartedAt { get; } = startedAt;
        public DateTimeOffset FetchedAt { get; } = fetchedAt;
        public int Waiters { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<IReadOnlyDictionary<string, StopSnapshot>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
