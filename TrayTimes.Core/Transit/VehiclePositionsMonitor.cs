using System.Globalization;

namespace TrayTimes.Transit;

/// <summary>
/// Coordinates one flyout's vehicle positions, which are fetched for one route direction at a time
/// (the selected row's) rather than for every listed trip. Call from its UI context; publication
/// runs on the refreshing caller's context. Only the latest visible refresh may update the map.
/// </summary>
public sealed class VehiclePositionsMonitor
{
    /// <summary>How often the selected route's vehicles refresh while it stays selected.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>Positions younger than this are reused instead of fetched again.</summary>
    public static readonly TimeSpan ReuseAge = TimeSpan.FromSeconds(30);

    /// <summary>How far a vehicle must move before its heading is worked out from its last position.</summary>
    public const double HeadingMinimumMeters = 15;

    private const int RetainedPositions = 64;

    /// <summary>How long an unchanged result goes unlogged, so a minute-by-minute refresh doesn't fill the log.</summary>
    private static readonly TimeSpan LogRepeatAge = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly ITransitService _service;
    private readonly Action<IReadOnlyList<Vehicle>> _publish;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly List<Request> _pending = [];
    private readonly Dictionary<string, (GeoPoint Location, int Heading)> _lastSeen = new(StringComparer.Ordinal);
    private Request? _cached;
    private bool _isForeground;
    private int _generation;
    private long _requestSequence;
    private (string Summary, long At)? _lastLogged;

    public VehiclePositionsMonitor(
        ITransitService service,
        Action<IReadOnlyList<Vehicle>> publish,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(publish);
        _service = service;
        _publish = publish;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
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
    /// Show one route direction's vehicles, or none with a null route. Force bypasses freshness, but
    /// never an equivalent in-flight request. Failures propagate to the caller and do not become
    /// fresh cached data.
    /// </summary>
    public async Task RefreshAsync(string? routeId, string? directionId, bool force = false)
    {
        (string Route, string Direction)? key = string.IsNullOrWhiteSpace(routeId) ? null : (routeId, directionId ?? string.Empty);
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
            if (key is not { } wanted)
            {
                task = Task.FromResult<IReadOnlyList<Vehicle>>([]);
            }
            else if (_pending.Find(r => r.Key == wanted) is { } pending)
            {
                task = pending.Completion.Task;
                inFlightReuse = true;
            }
            else if (!force && _cached is { } cached && cached.Key == wanted
                && _time.GetElapsedTime(cached.StartedAt) < ReuseAge)
            {
                task = cached.Completion.Task;
                inFlightReuse = false;
            }
            else
            {
                start = new Request(wanted, ++_requestSequence, _time.GetTimestamp());
                _pending.Add(start);
                task = start.Completion.Task;
            }
        }

        if (inFlightReuse is { } reuse)
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Vehicles, reuse, 1);
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
            IReadOnlyList<Vehicle> vehicles = await _service.GetVehiclesAsync(
                request.Key.Route, request.Key.Direction.Length == 0 ? null : request.Key.Direction);
            string? note;
            lock (_gate)
            {
                vehicles = [.. vehicles.Select(WithHeading)];
                while (_lastSeen.Count > RetainedPositions)
                {
                    _lastSeen.Remove(_lastSeen.Keys.First());
                }

                if (request.Sequence == _requestSequence)
                {
                    _cached = request;
                }

                _pending.Remove(request);
                request.Completion.SetResult(vehicles);
                note = LogNote(request, vehicles);
            }

            if (note is not null)
            {
                _log?.Invoke(note);
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

    /// <summary>
    /// What to log about a fetch, so a route whose feed publishes no live positions can be told from one that
    /// does: null when it matches the last note and is recent. Route IDs are public; no stops or places are named.
    /// </summary>
    private string? LogNote(Request request, IReadOnlyList<Vehicle> vehicles)
    {
        if (_log is null)
        {
            return null;
        }

        string summary = string.Create(CultureInfo.InvariantCulture,
            $"vehicles route={request.Key.Route} direction={(request.Key.Direction.Length == 0 ? "any" : request.Key.Direction)} count={vehicles.Count} withTrip={vehicles.Count(v => v.TripId is not null)}");
        long now = _time.GetTimestamp();
        if (_lastLogged is { } last && last.Summary == summary && _time.GetElapsedTime(last.At, now) < LogRepeatAge)
        {
            return null;
        }

        _lastLogged = (summary, now);
        return summary;
    }

    /// <summary>The provider sends no heading, so it's the bearing from where the vehicle was last seen, once it has moved.</summary>
    private Vehicle WithHeading(Vehicle vehicle)
    {
        int heading = vehicle.Heading;
        if (_lastSeen.TryGetValue(vehicle.Id, out (GeoPoint Location, int Heading) last))
        {
            if (heading < 0 && GeoMath.DistanceMeters(last.Location, vehicle.Location) >= HeadingMinimumMeters)
            {
                heading = GeoMath.BearingDegrees(last.Location, vehicle.Location);
            }
            else if (heading < 0)
            {
                heading = last.Heading;
            }

            // Keep the old anchor until the vehicle has moved far enough for a reliable bearing.
            _lastSeen[vehicle.Id] = GeoMath.DistanceMeters(last.Location, vehicle.Location) >= HeadingMinimumMeters
                ? (vehicle.Location, heading)
                : (last.Location, heading);
        }
        else
        {
            _lastSeen[vehicle.Id] = (vehicle.Location, heading);
        }

        return vehicle with { Heading = heading };
    }

    private sealed record Request((string Route, string Direction) Key, long Sequence, long StartedAt)
    {
        public TaskCompletionSource<IReadOnlyList<Vehicle>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
