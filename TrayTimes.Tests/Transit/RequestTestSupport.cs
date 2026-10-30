using System.Net;
using System.Text;
using System.Text.Json;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit;

/// <summary>A controllable clock whose timers (and so <c>Task.Delay</c> with this provider) fire only when advanced past.</summary>
internal sealed class RequestTestClock : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = Start;
    private long _timestamp;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing each timer that falls due on the way, in order.</summary>
    public void Advance(TimeSpan interval)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + interval;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).MinBy(t => t.Due);
                if (next is null)
                {
                    _timestamp += (target - _now).Ticks;
                    _now = target;
                    return;
                }

                _timestamp += (next.Due!.Value - _now).Ticks;
                _now = next.Due.Value;
                next.Due = next.Period is { } period && period > TimeSpan.Zero ? _now + period : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(RequestTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
                if (!clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One departure in a fake stop_departures response.</summary>
internal sealed record TestDeparture(
    string StopId,
    string RouteId,
    DateTimeOffset Time,
    string DirectionId = "0",
    string? TripId = null,
    bool RealTime = true,
    bool Canceled = false,
    string Headsign = "Downtown",
    DateTimeOffset? Scheduled = null,
    string? Shape = null,
    string? SearchKey = null);

/// <summary>Fake Transit API HTTP: counts attempts, records stop IDs, and answers with fixture JSON.</summary>
internal sealed class RequestTestHandler : HttpMessageHandler
{
    public const string EmptyBody = """{"route_departures":[]}""";

    /// <summary>One stop, one live departure 24 minutes after the clock's start, as trip 5501.</summary>
    public static readonly string LiveBody = Departures(new TestDeparture("1", "14", RequestTestClock.Start.AddMinutes(24), TripId: "5501"));

    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private int _attempts;

    public int Attempts => _attempts;

    /// <summary>Every request sent, in order (headers included).</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Action<string[]>? CaptureIds { get; set; }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(Response(EmptyBody));

    public static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The value of one query parameter of a request.</summary>
    public static string? Query(HttpRequestMessage request, string name) => Uri.UnescapeDataString(request.RequestUri!.Query)
        .TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    /// <summary>A stop_departures body: one route entry per stop and route, one merged itinerary per direction.</summary>
    public static string Departures(params TestDeparture[] departures) => JsonSerializer.Serialize(new
    {
        route_departures = departures
            .GroupBy(d => (d.StopId, d.RouteId))
            .Select(route => new
            {
                global_route_id = route.Key.RouteId,
                route_short_name = route.Key.RouteId,
                route_long_name = $"Route {route.Key.RouteId}",
                route_color = "112233",
                mode_name = "Bus",
                global_stop_id = route.Key.StopId,
                merged_itineraries = route.GroupBy(d => d.DirectionId).Select(direction => new
                {
                    direction_id = int.Parse(direction.Key, System.Globalization.CultureInfo.InvariantCulture),
                    itineraries = direction.Select(d => d.Headsign).Distinct().Select(h => new
                    {
                        direction_id = int.Parse(direction.Key, System.Globalization.CultureInfo.InvariantCulture),
                        headsign = h,
                        internal_itinerary_id = $"{route.Key.RouteId}:{direction.Key}:{h}",
                        shape = direction.FirstOrDefault(d => d.Headsign == h && d.Shape is not null)?.Shape,
                    }),
                    schedule_items = direction.Select(d => new
                    {
                        departure_time = d.Time.ToUnixTimeSeconds(),
                        scheduled_departure_time = (d.Scheduled ?? d.Time).ToUnixTimeSeconds(),
                        is_real_time = d.RealTime,
                        is_cancelled = d.Canceled,
                        rt_trip_id = d.TripId,
                        trip_search_key = d.SearchKey,
                        internal_itinerary_id = $"{route.Key.RouteId}:{direction.Key}:{d.Headsign}",
                    }),
                }),
            }),
    });

    /// <summary>A body with one live departure 24 minutes out at each requested stop.</summary>
    public static string LiveFor(IEnumerable<string> stopIds) =>
        Departures([.. stopIds.Select(id => new TestDeparture(id, "14", RequestTestClock.Start.AddMinutes(24), TripId: $"trip-{id}"))]);

    /// <summary>The stop IDs a stop_departures request asked for.</summary>
    public static string[] StopIds(HttpRequestMessage request) => Query(request, "global_stop_ids")?.Split(',') ?? [];

    public TransitAppService CreateService(TransitRequestDiagnostics? diagnostics = null) =>
        new("fixture-secret", null, new HttpClient(this), diagnostics, new Uri("https://transit.example/v4/"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        lock (_gate)
        {
            _requests.Add(request);
        }

        if (CaptureIds is not null && Query(request, "global_stop_ids") is { } ids)
        {
            CaptureIds(ids.Split(','));
        }

        return RespondAsync(request, cancellationToken);
    }
}
