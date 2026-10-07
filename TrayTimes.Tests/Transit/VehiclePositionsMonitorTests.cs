using System.Globalization;
using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class VehiclePositionsMonitorTests
{
    [TestMethod]
    public void Constructor_NullDependencies_RejectsInvalidMonitor()
    {
        // Arrange
        RequestTestHandler handler = new();
        BusTimeTransitService service = handler.CreateService();

        // Act / Assert
        Assert.Throws<ArgumentNullException>(() => new VehiclePositionsMonitor(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new VehiclePositionsMonitor(service, null!));
    }

    [TestMethod]
    public async Task RefreshAsync_EquivalentOverlappingAndForcedRequests_SharesOneAttemptAndPublishesLatest()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;

        // Act
        Task timer;
        Task result;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Timer))
        {
            timer = scenario.Monitor.RefreshAsync(["5501", "5502"]);
        }

        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FavoritesResult))
        {
            result = scenario.Monitor.RefreshAsync(["5502", "5501", "5501"]);
        }

        Task forced = scenario.Monitor.RefreshAsync(["5501", "5502"], force: true);
        Assert.AreEqual(1, scenario.Handler.Attempts);
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await Task.WhenAll(timer, result, forced);

        // Assert
        Assert.AreEqual(1, scenario.AttemptTally);
        Assert.HasCount(1, scenario.Published);
        Assert.AreEqual(90, scenario.Published[0].Single().Heading);
        Assert.AreEqual(new GeoPoint(0, 0), scenario.Published[0].Single().Location);
        Assert.AreEqual(2, scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
        TransitRequestObservation reuse = scenario.Service.Diagnostics.Snapshot.First(o => o.Phase == TransitRequestPhase.InFlightReuse);
        Assert.AreEqual(TransitTrigger.FavoritesResult, reuse.Trigger);
        Assert.AreEqual(TransitConsumer.Favorites, reuse.Consumer);
        Assert.AreEqual(2, reuse.BatchSize);
        Assert.AreEqual(TimeSpan.FromSeconds(2), scenario.Service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.Completed).Elapsed);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(14999, 1)]
    [DataRow(15000, 2)]
    [DataRow(15001, 2)]
    public async Task RefreshAsync_SuccessAgeBoundary_ReusesOnlyDataYoungerThanFifteenSeconds(int milliseconds, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));

        // Act
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Published);
        Assert.AreEqual(expectedAttempts == 1 ? 1 : 0,
            scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.CacheReuse));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2000)]
    [DataRow(14999)]
    public async Task RefreshAsync_SteadyStateOverlappingBursts_ProducesFourRoutineRequestsInHalfOpenMinute(int responseMilliseconds)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Clock.Advance(VehiclePositionsMonitor.RefreshInterval);
        int before = scenario.Handler.Attempts;

        // Act: [0,60), unchanged IDs, four equivalent UI calls at each due boundary.
        for (int second = 0; second < 60; second += 15)
        {
            TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
            scenario.Handler.RespondAsync = (_, _) => response.Task;
            Task[] burst =
            [
                scenario.Monitor.RefreshAsync(["5501"]),
                scenario.Monitor.RefreshAsync(["5501"]),
                scenario.Monitor.RefreshAsync(["5501", "5501"]),
                scenario.Monitor.RefreshAsync(["5501"]),
            ];
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(responseMilliseconds));
            response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
            await Task.WhenAll(burst);
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(15000 - responseMilliseconds));
        }

        // Assert
        Assert.AreEqual(4, scenario.Handler.Attempts - before);
        Assert.AreEqual(5, scenario.AttemptTally); // Includes the initial fetch outside the interval.
        Assert.HasCount(5, scenario.Published);
        Assert.AreEqual(12, scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
    }

    [TestMethod]
    public async Task RefreshAsync_SlowResponse_DoesNotExtendFreshnessFromCompletionTime()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task first = scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Clock.Advance(TimeSpan.FromSeconds(20));
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await first;
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));

        // Act
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Published);
    }

    [TestMethod]
    public async Task RefreshAsync_ForcedFreshRequest_BypassesCacheButSharesItsInflightTask()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;

        // Act
        Task forced = scenario.Monitor.RefreshAsync(["5501"], force: true);
        Task routine = scenario.Monitor.RefreshAsync(["5501"]);
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await Task.WhenAll(forced, routine);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.AreEqual(2, scenario.AttemptTally);
        Assert.HasCount(2, scenario.Published);
        Assert.AreEqual(1, scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
    }

    [TestMethod]
    public async Task RefreshAsync_ChangedSetAndOutOfOrderCompletion_PublishesAndCachesOnlyNewerState()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> old = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => scenario.Handler.Attempts == 1
            ? old.Task : Task.FromResult(RequestTestHandler.Response(VehicleBody("5502", 180)));
        Task first = scenario.Monitor.RefreshAsync(["5501"]);

        // Act
        await scenario.Monitor.RefreshAsync(["5502"]);
        Assert.AreEqual(2, scenario.Handler.Attempts); // New IDs do not wait for the old slow set.
        old.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await first;
        await scenario.Monitor.RefreshAsync(["5502"]);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Published);
        Assert.IsTrue(scenario.Published.All(v => v.Single().Id == "5502" && v.Single().Heading == 180));
    }

    [TestMethod]
    public async Task RefreshAsync_SetRoundTripWhilePending_ReusesOriginalEquivalentTask()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> firstResponse = PendingResponse();
        TaskCompletionSource<HttpResponseMessage> secondResponse = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => scenario.Handler.Attempts == 1 ? firstResponse.Task : secondResponse.Task;

        // Act
        Task first = scenario.Monitor.RefreshAsync(["5501"]);
        Task second = scenario.Monitor.RefreshAsync(["5502"]);
        Task returned = scenario.Monitor.RefreshAsync(["5501"]);
        secondResponse.SetResult(RequestTestHandler.Response(VehicleBody("5502", 180)));
        await second;
        firstResponse.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await Task.WhenAll(first, returned);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.AreEqual("5501", scenario.Published[0].Single().Id);
    }

    [TestMethod]
    public async Task RefreshAsync_EmptyNewSet_ClearsMapWithoutHttpAndDropsOlderResult()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task old = scenario.Monitor.RefreshAsync(["5501"]);

        // Act
        await scenario.Monitor.RefreshAsync([]);
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await old;

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.IsEmpty(scenario.Published[0]);
    }

    [TestMethod]
    public async Task RefreshAsync_SuccessfulEmptyResponse_IsFreshAndClearsPositions()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Clock.Advance(TimeSpan.FromSeconds(15));
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));

        // Act
        await scenario.Monitor.RefreshAsync(["5501"]);
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(3, scenario.Published);
        Assert.IsEmpty(scenario.Published[1]);
        Assert.IsEmpty(scenario.Published[2]);
    }

    [TestMethod]
    [DataRow(TransitRequestOutcome.HttpFailure)]
    [DataRow(TransitRequestOutcome.ApiFailure)]
    [DataRow(TransitRequestOutcome.TransportFailure)]
    [DataRow(TransitRequestOutcome.Timeout)]
    public async Task RefreshAsync_FailedRequest_PreservesLastGoodPositionsAndAllowsImmediateRetry(TransitRequestOutcome outcome)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Clock.Advance(TimeSpan.FromSeconds(15));
        scenario.Handler.RespondAsync = (_, _) => outcome switch
        {
            TransitRequestOutcome.HttpFailure => Task.FromResult(RequestTestHandler.Response("unavailable", HttpStatusCode.ServiceUnavailable)),
            TransitRequestOutcome.ApiFailure => Task.FromResult(RequestTestHandler.Response("""{"bustime-response":{"error":[{"msg":"fixture failure"}]}}""")),
            TransitRequestOutcome.Timeout => throw new TaskCanceledException(),
            _ => throw new HttpRequestException(),
        };

        // Act
        await Assert.ThrowsAsync<Exception>(() => scenario.Monitor.RefreshAsync(["5501"]));
        Assert.HasCount(1, scenario.Published);
        Assert.AreEqual(outcome, scenario.Service.Diagnostics.Snapshot.Last().Outcome);
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(3, scenario.Handler.Attempts);
        Assert.AreEqual(3, scenario.AttemptTally);
        Assert.HasCount(2, scenario.Published);
        Assert.IsTrue(scenario.Published.All(v => v.Single().Id == "5501"));
    }

    [TestMethod]
    public async Task RefreshAsync_SharedFailure_DoesNotLeaveAFaultedTaskBlockingRetry()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task first = scenario.Monitor.RefreshAsync(["5501"]);
        Task second = scenario.Monitor.RefreshAsync(["5501"]);
        response.SetResult(RequestTestHandler.Response("unavailable", HttpStatusCode.ServiceUnavailable));

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => first);
        await Assert.ThrowsAsync<HttpRequestException>(() => second);
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
    }

    [TestMethod]
    public async Task IsForeground_DismissDuringFetch_DropsResultAndDoesNotStartHiddenRequests()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task first = scenario.Monitor.RefreshAsync(["5501"]);

        // Act
        scenario.Monitor.IsForeground = false;
        await scenario.Monitor.RefreshAsync(["5502"], force: true);
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await first;
        Assert.IsEmpty(scenario.Published);
        scenario.Monitor.IsForeground = true;
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
        Assert.IsTrue(scenario.Monitor.IsForeground);
    }

    [TestMethod]
    public async Task IsForeground_QuickReopenDuringFetch_SharesPendingRequestWithoutPublishingOldWaiter()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task old = scenario.Monitor.RefreshAsync(["5501"]);

        // Act
        scenario.Monitor.IsForeground = false;
        scenario.Monitor.IsForeground = true;
        Task reopened = scenario.Monitor.RefreshAsync(["5501"]);
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await Task.WhenAll(old, reopened);

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
    }

    [TestMethod]
    public async Task IsForeground_StaleReopen_RefreshesPositionsInsteadOfCachingOldDataForever()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Monitor.RefreshAsync(["5501"]);
        scenario.Monitor.IsForeground = false;
        scenario.Clock.Advance(TimeSpan.FromSeconds(15));

        // Act
        scenario.Monitor.IsForeground = true;
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Published);
    }

    [TestMethod]
    public async Task Invalidate_TabRoundTripWithoutAnotherFetch_DropsOldResultAndAllowsFreshReuse()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> response = PendingResponse();
        scenario.Handler.RespondAsync = (_, _) => response.Task;
        Task old = scenario.Monitor.RefreshAsync(["5501"]);

        // Act
        scenario.Monitor.Invalidate();
        scenario.Monitor.Invalidate();
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await old;
        Assert.IsEmpty(scenario.Published);
        await scenario.Monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, scenario.Published);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(10, 1)]
    [DataRow(11, 0)]
    [DataRow(25, 0)]
    public async Task RefreshAsync_RequestSizeBoundary_NeverSilentlyTruncatesIds(int count, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        string[] ids = [.. Enumerable.Range(0, count).Select(i => (5501 + i).ToString(CultureInfo.InvariantCulture))];

        // Act
        if (count > 10)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scenario.Monitor.RefreshAsync(ids));
        }
        else
        {
            await scenario.Monitor.RefreshAsync(ids);
        }

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
        Assert.HasCount(count > 10 ? 0 : 1, scenario.Published);
        if (expectedAttempts > 0)
        {
            Assert.AreEqual(count, scenario.Service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.Started).BatchSize);
        }
    }

    [TestMethod]
    public async Task RefreshAsync_ProviderReturnsUnrequestedVehicles_PublishesOnlyRequestedPositions()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Monitor.RefreshAsync(["5502"]);

        // Assert
        Assert.HasCount(1, scenario.Published);
        Assert.IsEmpty(scenario.Published[0]);
    }

    [TestMethod]
    public async Task RefreshAsync_InvalidIds_RejectsWithoutHttpOrPublication()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => scenario.Monitor.RefreshAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => scenario.Monitor.RefreshAsync([""]));
        await Assert.ThrowsAsync<ArgumentException>(() => scenario.Monitor.RefreshAsync([" "]));

        // Assert
        Assert.AreEqual(0, scenario.Handler.Attempts);
        Assert.IsEmpty(scenario.Published);
    }

    [TestMethod]
    public async Task RefreshAsync_UnconfiguredService_DoesNotStartNetworkOrPublish()
    {
        // Arrange
        RequestTestHandler handler = new();
        BusTimeTransitService service = new("Test", new Uri("https://bustime.example/api/v3/"), null, TimeZoneInfo.Utc, new HttpClient(handler));
        bool published = false;
        VehiclePositionsMonitor monitor = new(service, _ => published = true) { IsForeground = true };

        // Act
        await monitor.RefreshAsync(["5501"]);

        // Assert
        Assert.AreEqual(0, handler.Attempts);
        Assert.IsFalse(published);
    }

    private static TaskCompletionSource<HttpResponseMessage> PendingResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string VehicleBody(string id, int heading) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""{"bustime-response":{"vehicle":[{"vid":"{{{id}}}","rt":"14","lat":0,"lon":0,"hdg":{{{heading}}}}]}}""");

    private sealed class Scenario
    {
        public Scenario()
        {
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Service.EndpointCalled += (_, _) => ++AttemptTally;
            Monitor = new VehiclePositionsMonitor(Service, Published.Add, Clock) { IsForeground = true };
        }

        public RequestTestClock Clock { get; } = new();

        public RequestTestHandler Handler { get; } = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)) };

        public BusTimeTransitService Service { get; }

        public VehiclePositionsMonitor Monitor { get; }

        public List<IReadOnlyList<Vehicle>> Published { get; } = [];

        public int AttemptTally { get; private set; }
    }
}
