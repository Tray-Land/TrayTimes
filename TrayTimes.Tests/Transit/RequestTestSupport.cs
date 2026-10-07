using System.Net;
using System.Text;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Transit;

internal sealed class RequestTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan interval)
    {
        _now += interval;
        _timestamp += interval.Ticks;
    }
}

internal sealed class RequestTestHandler : HttpMessageHandler
{
    public const string EmptyBody = """{"bustime-response":{"routes":[{"rt":"14","rtnm":"Test route"}],"prd":[],"vehicle":[]}}""";
    public const string LiveBody = """{"bustime-response":{"prd":[{"stpid":"1","rt":"14","rtdir":"N","vid":"5501","prdtm":"20261006 20:24","dyn":0}],"vehicle":[{"vid":"5501","rt":"14","lat":0,"lon":0,"hdg":90}]}}""";

    private int _attempts;

    public int Attempts => _attempts;

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(Response(EmptyBody));

    public static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        return RespondAsync(request, cancellationToken);
    }

    public BusTimeTransitService CreateService(TransitRequestDiagnostics? diagnostics = null) =>
        new("Test", new Uri("https://bustime.example/api/v3/"), "fixture-secret", TimeZoneInfo.Utc, new HttpClient(this), diagnostics);
}
