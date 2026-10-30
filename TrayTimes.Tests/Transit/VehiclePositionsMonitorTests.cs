using System.Globalization;
using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class VehiclePositionsMonitorTests
{
    [TestMethod]
    public void Constructor_NullDependencies_RejectsInvalidMonitor()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new VehiclePositionsMonitor(null!, _ => { }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new VehiclePositionsMonitor(service, null!));
    }

    [TestMethod]
    public async Task RefreshAsync_EquivalentOverlappingAndForcedRequests_SharesOneAttemptAndPublishesLatest()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = PendingResponse();
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task first = scenario.Monitor.RefreshAsync("14", "0");
        Task second = scenario.Monitor.RefreshAsync("14", "0", force: true);
        pending.SetResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await Task.WhenAll(first, second);

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.AreEqual("t1", scenario.Published[0][0].TripId);
        Assert.AreEqual(1, scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
    }

    [TestMethod]
    [DataRow(29_999, 1)]
    [DataRow(30_000, 2)]
    public async Task RefreshAsync_SuccessAgeBoundary_ReusesOnlyDataYoungerThanReuseAge(int milliseconds, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync("14", "0");

        // Act
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        await scenario.Monitor.RefreshAsync("14", "0");

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Published);
    }

    [TestMethod]
    public async Task RefreshAsync_ForcedFreshRequest_BypassesCacheButSharesItsInflightTask()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync("14", "0");
        TaskCompletionSource<HttpResponseMessage> pending = PendingResponse();
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task forced = scenario.Monitor.RefreshAsync("14", "0", force: true);
        Task again = scenario.Monitor.RefreshAsync("14", "0");
        pending.SetResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await Task.WhenAll(forced, again);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task RefreshAsync_ChangedRouteAndOutOfOrderCompletion_PublishesOnlyTheNewerRoute()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> older = PendingResponse();
        TaskCompletionSource<HttpResponseMessage> newer = PendingResponse();
        scenario.Handler.RespondAsync = (request, token) => RequestTestHandler.Query(request, "global_route_id") == "14"
            ? older.Task.WaitAsync(token)
            : newer.Task.WaitAsync(token);

        // Act
        Task first = scenario.Monitor.RefreshAsync("14", "0");
        Task second = scenario.Monitor.RefreshAsync("30", "1");
        newer.SetResult(RequestTestHandler.Response(VehicleBody(("v30", 43.0, -88.0, "t30"))));
        await second;
        older.SetResult(RequestTestHandler.Response(VehicleBody(("v14", 43.0, -88.0, "t14"))));
        await first;

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.AreEqual("v30", scenario.Published[0][0].Id);
        Assert.AreEqual("30", scenario.Published[0][0].RouteId);
    }

    [TestMethod]
    public async Task RefreshAsync_NoRoute_ClearsMapWithoutHttp()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Monitor.RefreshAsync(null, null);

        // Assert
        Assert.AreEqual(0, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.IsEmpty(scenario.Published[0]);
    }

    [TestMethod]
    [DataRow("0", "0")]
    [DataRow(null, null)]
    public async Task RefreshAsync_Direction_IsSentOnlyWhenKnown(string? direction, string? expected)
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Monitor.RefreshAsync("14", direction);

        // Assert
        Assert.AreEqual("14", RequestTestHandler.Query(scenario.Handler.Requests[0], "global_route_id"));
        Assert.AreEqual(expected, RequestTestHandler.Query(scenario.Handler.Requests[0], "direction_id"));
    }

    [TestMethod]
    [DataRow(TransitRequestOutcome.HttpFailure)]
    [DataRow(TransitRequestOutcome.ApiFailure)]
    public async Task RefreshAsync_FailedRequest_PropagatesAndAllowsImmediateRetry(TransitRequestOutcome outcome)
    {
        // Arrange
        Scenario scenario = new();
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(outcome == TransitRequestOutcome.HttpFailure
            ? RequestTestHandler.Response("unavailable", HttpStatusCode.BadGateway)
            : RequestTestHandler.Response("not json"));

        // Act
        await Assert.ThrowsAsync<Exception>(() => scenario.Monitor.RefreshAsync("14", "0"));
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await scenario.Monitor.RefreshAsync("14", "0");

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
    }

    [TestMethod]
    public async Task IsForeground_DismissDuringFetch_DropsResultAndDoesNotStartHiddenRequests()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = PendingResponse();
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task refresh = scenario.Monitor.RefreshAsync("14", "0");

        // Act
        scenario.Monitor.IsForeground = false;
        pending.SetResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await refresh;
        await scenario.Monitor.RefreshAsync("30", "0");

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.IsEmpty(scenario.Published);
    }

    [TestMethod]
    public async Task Invalidate_PendingResult_IsNotPublishedButStaysReusable()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = PendingResponse();
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task refresh = scenario.Monitor.RefreshAsync("14", "0");

        // Act
        scenario.Monitor.Invalidate();
        pending.SetResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await refresh;
        await scenario.Monitor.RefreshAsync("14", "0");

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
    }

    [TestMethod]
    public async Task RefreshAsync_VehicleMovesFarEnough_HeadingIsItsBearing()
    {
        // Arrange: first seen, then about 110 m due east.
        Scenario scenario = new();
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1"))));
        await scenario.Monitor.RefreshAsync("14", "0");
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -87.99865, "t1"))));

        // Act
        await scenario.Monitor.RefreshAsync("14", "0", force: true);

        // Assert
        Assert.AreEqual(-1, scenario.Published[0][0].Heading);
        Assert.AreEqual(90, scenario.Published[1][0].Heading, 1);
    }

    [TestMethod]
    public async Task RefreshAsync_VehicleBarelyMoves_KeepsItsLastHeading()
    {
        // Arrange: north about 110 m, then a few meters more.
        Scenario scenario = new();
        double[] latitudes = [43.0, 43.001, 43.00103];
        foreach (double lat in latitudes)
        {
            scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody(("v1", lat, -88.0, "t1"))));

            // Act
            await scenario.Monitor.RefreshAsync("14", "0", force: true);
        }

        // Assert
        Assert.AreEqual(0, scenario.Published[1][0].Heading);
        Assert.AreEqual(0, scenario.Published[2][0].Heading);
    }

    [TestMethod]
    public async Task RefreshAsync_UnconfiguredService_DoesNotStartNetworkOrPublish()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = new(null, http: new HttpClient(handler));
        bool published = false;
        VehiclePositionsMonitor monitor = new(service, _ => published = true) { IsForeground = true };

        // Act
        await monitor.RefreshAsync("14", "0");

        // Assert
        Assert.AreEqual(0, handler.Attempts);
        Assert.IsFalse(published);
    }

    [TestMethod]
    public async Task RefreshAsync_RepeatedIdenticalResult_LogsOnceUntilItChangesOrAges()
    {
        // Arrange
        Scenario scenario = new();
        List<string> log = [];
        VehiclePositionsMonitor monitor = new(scenario.Service, _ => { }, scenario.Clock, log.Add) { IsForeground = true };

        // Act
        await monitor.RefreshAsync("14", "0", force: true);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshAsync("14", "0", force: true);
        int afterRepeat = log.Count;
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody()));
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshAsync("14", "0", force: true);
        int afterChange = log.Count;
        scenario.Clock.Advance(TimeSpan.FromMinutes(15));
        await monitor.RefreshAsync("14", "0", force: true);

        // Assert
        Assert.AreEqual(1, afterRepeat);
        Assert.AreEqual(2, afterChange);
        Assert.HasCount(3, log);
        Assert.AreEqual("vehicles route=14 direction=0 count=1 withTrip=1", log[0]);
        Assert.AreEqual("vehicles route=14 direction=0 count=0 withTrip=0", log[1]);
    }

    [TestMethod]
    public async Task RefreshAsync_Failure_LogsNothing()
    {
        // Arrange
        Scenario scenario = new();
        List<string> log = [];
        VehiclePositionsMonitor monitor = new(scenario.Service, _ => { }, scenario.Clock, log.Add) { IsForeground = true };
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("unavailable", HttpStatusCode.BadGateway));

        // Act
        await Assert.ThrowsAsync<Exception>(() => monitor.RefreshAsync("14", "0", force: true));

        // Assert
        Assert.IsEmpty(log);
    }

    private static TaskCompletionSource<HttpResponseMessage> PendingResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string VehicleBody(params (string Id, double Lat, double Lon, string TripId)[] vehicles) =>
        "{\"vehicles\":[" + string.Join(',', vehicles.Select(v => string.Create(CultureInfo.InvariantCulture,
            $$"""{"vehicle_id":"{{v.Id}}","latitude":{{v.Lat}},"longitude":{{v.Lon}},"direction_id":0,"rt_trip_id":"{{v.TripId}}","vehicle_label":"29-088","occupancy_status":2}"""))) + "]}";

    private sealed class Scenario
    {
        public Scenario()
        {
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Monitor = new VehiclePositionsMonitor(Service, Published.Add, Clock) { IsForeground = true };
        }

        public RequestTestClock Clock { get; } = new();

        public RequestTestHandler Handler { get; } = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(VehicleBody(("v1", 43.0, -88.0, "t1")))),
        };

        public TransitAppService Service { get; }

        public VehiclePositionsMonitor Monitor { get; }

        public List<IReadOnlyList<Vehicle>> Published { get; } = [];
    }
}
