namespace TrayTimes.Transit;

/// <summary>
/// Coordinates one flyout's vehicle positions. Call from its UI context; publication runs on the
/// refreshing caller's context. Only the latest visible refresh may update the map.
/// </summary>
public sealed class VehiclePositionsMonitor
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly ITransitService _service;
    private readonly Action<IReadOnlyList<Vehicle>> _publish;
    private readonly TimeProvider _time;
    private readonly List<Request> _pending = [];
    private Request? _cached;
    private bool _isForeground;
    private int _generation;
    private long _requestSequence;

    public VehiclePositionsMonitor(ITransitService service, Action<IReadOnlyList<Vehicle>> publish, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(publish);
        _service = service;
        _publish = publish;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Hidden consumers neither start requests nor publish already-started results.</summary>
    public bool IsForeground
    {
        get
        {
            lock (_gate)
            {
                return _isForeground;
            }
        }
        set
        {
            lock (_gate)
            {
                if (_isForeground != value)
                {
                    _isForeground = value;
                    ++_generation;
                }
            }
        }
    }

    /// <summary>Drop pending map updates on a tab/destination change, retaining reusable positions.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            ++_generation;
        }
    }

    /// <summary>
    /// Refresh the displayed vehicle set. Force bypasses freshness, but never an equivalent
    /// in-flight request. Failures propagate to the caller and do not become fresh cached data.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyCollection<string> vehicleIds, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        if (vehicleIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Vehicle IDs must not be empty.", nameof(vehicleIds));
        }

        string[] ids = [.. vehicleIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (ids.Length > _service.MaxStopsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(vehicleIds), "The vehicle set exceeds the provider's request limit.");
        }

        int generation;
        Request? start = null;
        Task<IReadOnlyList<Vehicle>> task;
        bool? inFlightReuse = null;
        lock (_gate)
        {
            if (!_isForeground || !_service.IsConfigured)
            {
                return;
            }

            generation = ++_generation;
            if (ids.Length == 0)
            {
                task = Task.FromResult<IReadOnlyList<Vehicle>>([]);
            }
            else if (_pending.Find(r => r.Ids.SequenceEqual(ids)) is { } pending)
            {
                task = pending.Completion.Task;
                inFlightReuse = true;
            }
            else if (!force && _cached is { } cached && cached.Ids.SequenceEqual(ids)
                && _time.GetElapsedTime(cached.StartedAt) < RefreshInterval)
            {
                task = cached.Completion.Task;
                inFlightReuse = false;
            }
            else
            {
                start = new Request(ids, ++_requestSequence, _time.GetTimestamp());
                _pending.Add(start);
                task = start.Completion.Task;
            }
        }

        if (inFlightReuse is { } reuse)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Vehicles, reuse, ids.Length);
        }

        if (start is not null)
        {
            _ = FetchAsync(start);
        }

        IReadOnlyList<Vehicle> vehicles = await task;
        lock (_gate)
        {
            if (_isForeground && generation == _generation)
            {
                _publish(vehicles);
            }
        }
    }

    private async Task FetchAsync(Request request)
    {
        try
        {
            IReadOnlyList<Vehicle> vehicles = await _service.GetVehiclesAsync(request.Ids);
            vehicles = vehicles.Where(v => request.Ids.Contains(v.Id, StringComparer.Ordinal)).ToArray();
            lock (_gate)
            {
                if (request.Sequence == _requestSequence)
                {
                    _cached = request;
                }

                _pending.Remove(request);
                request.Completion.SetResult(vehicles);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                _pending.Remove(request);
                request.Completion.SetCanceled(ex.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _pending.Remove(request);
                request.Completion.SetException(ex);
            }
        }
    }

    private sealed record Request(string[] Ids, long Sequence, long StartedAt)
    {
        public TaskCompletionSource<IReadOnlyList<Vehicle>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
