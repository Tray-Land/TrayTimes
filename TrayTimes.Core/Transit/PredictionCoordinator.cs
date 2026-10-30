namespace TrayTimes.Transit;

/// <summary>A stop-scoped live result, dated from the oldest contributing HTTP request's start.</summary>
public sealed record PredictionSnapshot(IReadOnlyList<Arrival> Arrivals, DateTimeOffset FetchedAt);

/// <summary>
/// App-wide departure sharing. Known browsing stops ride along in any due batch, so Favorites,
/// Commute, Nearby, and tracking usually share one request. A new batch can wait a short
/// coalescing window for other consumers' stops before it's sent. Each caller owns its waiter,
/// not the lifetime of other callers' shared HTTP work.
/// </summary>
public sealed class PredictionCoordinator
{
    /// <summary>The browsing refresh interval when the caller doesn't supply one.</summary>
    public static readonly TimeSpan DefaultBrowsingInterval = TimeSpan.FromSeconds(60);

    public const int RetainedStops = 256;

    private readonly Lock _gate = new();
    private readonly ITransitService _service;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _browsingInterval;
    private readonly TimeSpan _coalesceWindow;
    private readonly Dictionary<TransitConsumer, string[]> _browsing = [];
    private readonly Dictionary<string, Request> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StopSnapshot> _cache = new(StringComparer.Ordinal);
    private Request? _open;
    private long _sequence;

    /// <param name="browsingInterval">How often browsing refreshes right now (it can follow the call budget).</param>
    /// <param name="coalesceWindow">How long a new batch waits for other stops to join it before it's sent; zero sends at once.</param>
    public PredictionCoordinator(ITransitService service, TimeProvider? timeProvider = null, Func<TimeSpan>? browsingInterval = null, TimeSpan coalesceWindow = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(service.MaxStopsPerRequest);
        ArgumentOutOfRangeException.ThrowIfLessThan(coalesceWindow, TimeSpan.Zero);
        _service = service;
        _time = timeProvider ?? TimeProvider.System;
        _browsingInterval = browsingInterval ?? (() => DefaultBrowsingInterval);
        _coalesceWindow = coalesceWindow;
    }

    /// <summary>How often browsing refreshes right now.</summary>
    public TimeSpan BrowsingInterval => _browsingInterval();

    /// <summary>Register known visible stops, or an empty set on hide/invalidation. Does no HTTP work.</summary>
    public void SetBrowsingStops(TransitConsumer consumer, IReadOnlyCollection<string> stopIds)
    {
        if (consumer is not (TransitConsumer.Favorites or TransitConsumer.Commute or TransitConsumer.Nearby or TransitConsumer.Trip))
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
    /// success, never pending work. With combineBrowsing, due browsing stops fill the caller's batch.
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

        int max = _service.MaxStopsPerRequest;
        (TransitConsumer, TransitTrigger) scope = TransitRequestDiagnostics.CurrentScope;
        TimeSpan fillAge = BrowsingInterval / 2;
        Dictionary<string, StopSnapshot> snapshots = new(StringComparer.Ordinal);
        Dictionary<string, Request> requested = new(StringComparer.Ordinal);
        HashSet<Request> waiters = [];
        HashSet<Request> owned = [];
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
                    if (!pending.IsSent)
                    {
                        owned.Add(pending); // It hasn't been sent, so its data will be as fresh as this caller's own.
                    }
                    else
                    {
                        ++sharedCount;
                    }
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
                int capacity = (max - (required.Length % max)) % max;
                foreach (string id in DueBrowsingStops(fillAge))
                {
                    if (capacity == 0)
                    {
                        break;
                    }

                    if (missing.Add(id))
                    {
                        --capacity;
                    }
                }
            }

            Queue<string> queue = new(required.Concat(missing.Except(required)));
            if (queue.Count > 0 && _open is { IsSent: false } open && open.Ids.Count < max)
            {
                open.CombineBrowsing |= combineBrowsing && !force;
                Claim(open, queue, max, ids, requested);
                waiters.Add(open);
                owned.Add(open);
            }

            while (queue.Count > 0)
            {
                Request request = new(++_sequence, scope) { CombineBrowsing = combineBrowsing && !force };
                Claim(request, queue, max, ids, requested);
                starts.Add(request);
                waiters.Add(request);
                owned.Add(request);
                if (_coalesceWindow > TimeSpan.Zero)
                {
                    _open = request;
                }
            }

            foreach (Request request in waiters)
            {
                ++request.Waiters;
                if (!request.IsSent)
                {
                    request.Join(scope);
                }
            }
        }

        if (cachedCount > 0)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Departures, inFlight: false, cachedCount);
        }

        if (sharedCount > 0)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Departures, inFlight: true, sharedCount);
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
            string[] expired = [.. ids.Where(id => (!requested.TryGetValue(id, out Request? r) || !owned.Contains(r))
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

    /// <summary>Takes ids from the queue into an unsent request, up to the batch size.</summary>
    private void Claim(Request request, Queue<string> queue, int max, string[] callerIds, Dictionary<string, Request> requested)
    {
        while (queue.Count > 0 && request.Ids.Count < max)
        {
            string id = queue.Dequeue();
            request.Ids.Add(id);
            _pending[id] = request;
            if (callerIds.Contains(id, StringComparer.Ordinal))
            {
                requested[id] = request;
            }
        }
    }

    /// <summary>Registered browsing stops that would be due before the next refresh and aren't already on their way.</summary>
    private IEnumerable<string> DueBrowsingStops(TimeSpan fillAge) =>
        _browsing.Values.SelectMany(s => s).Distinct(StringComparer.Ordinal).Where(id => !_pending.ContainsKey(id) && !IsFresh(id, fillAge));

    private bool IsFresh(string id, TimeSpan maxAge) =>
        _cache.TryGetValue(id, out StopSnapshot? cached) && _time.GetElapsedTime(cached.StartedAt) < maxAge;

    private async Task FetchAsync(Request request)
    {
        try
        {
            if (_coalesceWindow > TimeSpan.Zero)
            {
                await Task.Delay(_coalesceWindow, _time, request.Cancellation.Token);
            }

            string[] ids;
            (TransitConsumer Consumer, TransitTrigger Trigger) scope;
            lock (_gate)
            {
                request.Cancellation.Token.ThrowIfCancellationRequested();
                if (ReferenceEquals(_open, request))
                {
                    _open = null;
                }

                // Stops registered while the batch waited (another tab's discovery) can still ride along.
                if (request.CombineBrowsing && _coalesceWindow > TimeSpan.Zero)
                {
                    foreach (string id in DueBrowsingStops(_browsingInterval() / 2).Take(_service.MaxStopsPerRequest - request.Ids.Count))
                    {
                        request.Ids.Add(id);
                        _pending[id] = request;
                    }
                }

                request.Send(_time.GetTimestamp(), _time.GetLocalNow());
                ids = [.. request.Ids];
                scope = request.Scope;
            }

            // Sent for its most urgent caller, not whichever opened it, so the budget and cooldown don't hold back tracking.
            DepartureBoard board;
            using (TransitRequestDiagnostics.BeginScope(scope.Consumer, scope.Trigger))
            {
                board = await _service.GetDeparturesAsync(ids, includeShapes: false, request.Cancellation.Token);
            }

            Dictionary<string, StopSnapshot> result = ids.ToDictionary(id => id,
                id => new StopSnapshot(board.Arrivals.Where(a => a.StopId == id).ToArray(),
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
        if (ReferenceEquals(_open, request))
        {
            _open = null;
        }

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

    /// <summary>One HTTP batch. Its stops can still be added to until it's sent.</summary>
    private sealed class Request(long sequence, (TransitConsumer Consumer, TransitTrigger Trigger) scope)
    {
        public List<string> Ids { get; } = [];
        public long Sequence { get; } = sequence;

        /// <summary>The scope it's sent under: its most urgent caller's.</summary>
        public (TransitConsumer Consumer, TransitTrigger Trigger) Scope { get; private set; } = scope;
        public bool CombineBrowsing { get; set; }
        public bool IsSent { get; private set; }
        public long StartedAt { get; private set; }
        public DateTimeOffset FetchedAt { get; private set; }
        public int Waiters { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<IReadOnlyDictionary<string, StopSnapshot>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A caller waits on this unsent batch: it's sent under that caller's scope if it's more urgent.</summary>
        public void Join((TransitConsumer Consumer, TransitTrigger Trigger) caller)
        {
            if (TransitRequestDiagnostics.PriorityOf(caller) > TransitRequestDiagnostics.PriorityOf(Scope))
            {
                Scope = caller;
            }
        }

        public void Send(long startedAt, DateTimeOffset fetchedAt)
        {
            IsSent = true;
            StartedAt = startedAt;
            FetchedAt = fetchedAt;
        }
    }
}
