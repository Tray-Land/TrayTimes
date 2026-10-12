using System.Globalization;
using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class PredictionCoordinatorTests
{
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(10, 1)]
    [DataRow(11, 2)]
    [DataRow(25, 3)]
    public async Task GetAsync_StopCountBoundary_FetchesEveryStopInSafeBatches(int count, int expected)
    {
        // Arrange
        Scenario scenario = new();
        string[] ids = Ids(count);

        // Act
        PredictionSnapshot result = await scenario.Coordinator.GetAsync(ids, PredictionCoordinator.BrowsingInterval);

        // Assert
        Assert.AreEqual(expected, scenario.Handler.Attempts);
        Assert.AreEqual(expected, scenario.Tally.Summarize(DateOnly.FromDateTime(scenario.Clock.GetUtcNow().Date)).Total.Today);
        CollectionAssert.AreEquivalent(ids, scenario.Batches.SelectMany(b => b).ToArray());
        CollectionAssert.AreEquivalent(ids, result.Arrivals.Select(a => a.StopId).ToArray());
        Assert.IsTrue(scenario.Batches.All(b => b.Length <= 10));
        Assert.AreEqual(scenario.Clock.GetUtcNow(), result.FetchedAt);
    }

    [TestMethod]
    public async Task GetAsync_EquivalentReorderedForcedOverlap_SharesOneAttempt()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task<PredictionSnapshot> first = scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        Task<PredictionSnapshot> second = scenario.Coordinator.GetAsync(["2", "1", "2"], TimeSpan.FromSeconds(30), force: true);
        pending.SetResult(RequestTestHandler.Response(Body(["1", "2", "999"])));
        PredictionSnapshot[] results = await Task.WhenAll(first, second);

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.IsTrue(results.All(r => r.Arrivals.Count == 2));
        Assert.AreEqual(2, scenario.Service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.InFlightReuse).BatchSize);
    }

    [TestMethod]
    [DataRow(8, 1)]
    [DataRow(10, 1)]
    [DataRow(11, 2)]
    [DataRow(25, 3)]
    public async Task GetAsync_DisjointKnownBrowsingStops_SharesUnionAndScopesConsumers(int count, int expected)
    {
        // Arrange
        Scenario scenario = new();
        string[] favorites = Ids(count / 2);
        string[] place = Ids(count).Except(favorites).ToArray();
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Favorites, favorites);
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Nearby, place);
        List<TaskCompletionSource<HttpResponseMessage>> responses = [];
        scenario.Handler.RespondAsync = (_, token) =>
        {
            TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            responses.Add(response);
            return response.Task.WaitAsync(token);
        };

        // Act
        Task<PredictionSnapshot> first = scenario.Coordinator.GetAsync(favorites, TimeSpan.FromSeconds(30), combineBrowsing: true);
        Task<PredictionSnapshot> second = scenario.Coordinator.GetAsync(place, TimeSpan.FromSeconds(30), combineBrowsing: true);
        for (int i = 0; i < responses.Count; ++i)
        {
            responses[i].SetResult(RequestTestHandler.Response(Body(scenario.Batches[i])));
        }

        PredictionSnapshot[] results = await Task.WhenAll(first, second);

        // Assert
        Assert.AreEqual(expected, scenario.Handler.Attempts);
        CollectionAssert.AreEquivalent(Ids(count), scenario.Batches.SelectMany(b => b).ToArray());
        CollectionAssert.AreEquivalent(favorites, results[0].Arrivals.Select(a => a.StopId).ToArray());
        CollectionAssert.AreEquivalent(place, results[1].Arrivals.Select(a => a.StopId).ToArray());
    }

    [TestMethod]
    public async Task GetAsync_PartialOverlapWithoutKnownUnion_FetchesOnlyMissingStops()
    {
        // Arrange
        Scenario scenario = new();
        List<TaskCompletionSource<HttpResponseMessage>> responses = [];
        scenario.Handler.RespondAsync = (_, token) =>
        {
            TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            responses.Add(response);
            return response.Task.WaitAsync(token);
        };

        // Act
        Task<PredictionSnapshot> first = scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        Task<PredictionSnapshot> second = scenario.Coordinator.GetAsync(["2", "3"], TimeSpan.FromSeconds(30));
        responses[1].SetResult(RequestTestHandler.Response(Body(["3"])));
        responses[0].SetResult(RequestTestHandler.Response(Body(["1", "2", "999"])));
        PredictionSnapshot[] result = await Task.WhenAll(first, second);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        CollectionAssert.AreEqual(new[] { "3" }, scenario.Batches[1]);
        CollectionAssert.AreEquivalent(new[] { "1", "2" }, result[0].Arrivals.Select(a => a.StopId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "2", "3" }, result[1].Arrivals.Select(a => a.StopId).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_CoveringSuccessAndAgeBoundary_ReusesEmptyOrNonemptyDataWithoutRedating(bool empty)
    {
        // Arrange
        Scenario scenario = new();
        if (empty)
        {
            scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        }

        PredictionSnapshot initial = await scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        scenario.Clock.Advance(TimeSpan.FromSeconds(29.999));

        // Act
        PredictionSnapshot reused = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, scenario.Handler.Attempts);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(1));
        PredictionSnapshot fresh = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Assert
        Assert.AreEqual(initial.FetchedAt, reused.FetchedAt);
        Assert.AreEqual(empty ? 0 : 1, reused.Arrivals.Count);
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.AreEqual(initial.FetchedAt.AddSeconds(30), fresh.FetchedAt);
        Assert.AreEqual(1, scenario.Service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.CacheReuse));
    }

    [TestMethod]
    public async Task GetAsync_FasterTrackingAndMixedStopAges_DoesNotRefreshUnrelatedBrowsingStops()
    {
        // Arrange
        Scenario scenario = new();
        string[] browsing = Ids(11);
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Favorites, browsing);
        await scenario.Coordinator.GetAsync(browsing, TimeSpan.FromSeconds(30), combineBrowsing: true);
        scenario.Clock.Advance(TimeSpan.FromSeconds(9.999));

        // Act
        await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, scenario.Handler.Attempts);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));
        Assert.AreEqual(3, scenario.Handler.Attempts);
        scenario.Clock.Advance(TimeSpan.FromSeconds(20));
        await scenario.Coordinator.GetAsync(browsing, TimeSpan.FromSeconds(30), combineBrowsing: true);

        // Assert
        Assert.AreEqual(4, scenario.Handler.Attempts);
        CollectionAssert.AreEqual(new[] { "1" }, scenario.Batches[2]);
        CollectionAssert.AreEquivalent(browsing.Skip(1).ToArray(), scenario.Batches[3]);
    }

    [TestMethod]
    public async Task GetAsync_SharedBrowsingResponseBecomesTooOldForTracking_RefreshesOnlyTrackedStop()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.RespondAsync = (_, token) => scenario.Handler.Attempts == 1
            ? pending.Task.WaitAsync(token)
            : Task.FromResult(RequestTestHandler.Response(Body(["1"])));
        Task<PredictionSnapshot> browsing = scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        scenario.Clock.Advance(TimeSpan.FromSeconds(9));
        Task<PredictionSnapshot> tracking = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));

        // Act
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        pending.SetResult(RequestTestHandler.Response(Body(["1", "2"])));
        await browsing;
        PredictionSnapshot result = await tracking;

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.AreEqual(scenario.Clock.GetUtcNow(), result.FetchedAt);
        CollectionAssert.AreEqual(new[] { "1" }, scenario.Batches[1]);
    }

    [TestMethod]
    public async Task GetAsync_ManualForceOfFreshSuccess_SharesPendingButBypassesCache()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task<PredictionSnapshot> manual = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), force: true);
        Task<PredictionSnapshot> timer = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));
        Task<PredictionSnapshot> manualAgain = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), force: true);
        pending.SetResult(RequestTestHandler.Response(Body(["1"])));
        await Task.WhenAll(manual, timer, manualAgain);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(TransitRequestOutcome.HttpFailure)]
    [DataRow(TransitRequestOutcome.ApiFailure)]
    [DataRow(TransitRequestOutcome.Timeout)]
    public async Task GetAsync_SharedFailure_PropagatesWithoutCachingEmptySuccessAndAllowsRetry(TransitRequestOutcome outcome)
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task<PredictionSnapshot> first = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));
        Task<PredictionSnapshot> second = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Act
        if (outcome == TransitRequestOutcome.Timeout)
        {
            pending.SetException(new TaskCanceledException("Fixture timeout"));
        }
        else
        {
            pending.SetResult(outcome == TransitRequestOutcome.HttpFailure
                ? RequestTestHandler.Response("error", HttpStatusCode.TooManyRequests)
                : RequestTestHandler.Response("""{"bustime-response":{"error":[{"msg":"Invalid parameter"}]}}"""));
        }

        await Assert.ThrowsAsync<Exception>(() => first);
        await Assert.ThrowsAsync<Exception>(() => second);
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        PredictionSnapshot retry = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.IsEmpty(retry.Arrivals);
        CollectionAssert.AreEqual(new[] { outcome, TransitRequestOutcome.Success },
            scenario.Service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed).Select(o => o.Outcome).ToArray());
    }

    [TestMethod]
    public async Task GetAsync_CanceledBrowsingWaiter_DoesNotCancelSharedHiddenTracking()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken physicalToken = default;
        scenario.Handler.RespondAsync = (_, token) =>
        {
            physicalToken = token;
            return pending.Task.WaitAsync(token);
        };
        using CancellationTokenSource cancellation = new();
        Task<PredictionSnapshot> browsing = scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);
        Task<PredictionSnapshot> tracking = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));

        // Act
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => browsing);
        Assert.IsFalse(physicalToken.IsCancellationRequested);
        pending.SetResult(RequestTestHandler.Response(Body(["1", "2"])));
        PredictionSnapshot result = await tracking;

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.HasCount(1, result.Arrivals);
        Assert.AreEqual(TransitRequestOutcome.Success, scenario.Service.Diagnostics.Snapshot.Last().Outcome);
    }

    [TestMethod]
    public async Task GetAsync_AllWaitersCanceled_CancelsHttpAndAllowsImmediateReplacement()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Service.Diagnostics.Observed += (_, observation) =>
        {
            if (observation.Outcome == TransitRequestOutcome.Canceled)
            {
                canceled.TrySetResult();
            }
        };
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();
        Task<PredictionSnapshot> first = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), cancellationToken: firstCancellation.Token);
        Task<PredictionSnapshot> second = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), cancellationToken: secondCancellation.Token);

        // Act
        firstCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        secondCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(Body(["1"])));
        PredictionSnapshot replacement = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, replacement.Arrivals);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetAsync_PartialPredictionError_DistinguishesBenignEmptyFromFailedStop(bool benign)
    {
        // Arrange
        Scenario scenario = new();
        string message = benign ? "No arrival times" : "Invalid parameter";
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
            $$$"""{"bustime-response":{"prd":[{"stpid":"2","rt":"14","prdtm":"20261006 20:24"}],"error":[{"stpid":"1","msg":"{{{message}}}"}]}}"""));

        // Act
        if (benign)
        {
            PredictionSnapshot result = await scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
            Assert.HasCount(1, result.Arrivals);
            PredictionSnapshot empty = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));
            Assert.IsEmpty(empty.Arrivals);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<TransitApiException>(() => scenario.Coordinator.GetAsync(["1", "2"], TimeSpan.FromSeconds(30)));
            scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(Body(["1"])));
            PredictionSnapshot retry = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));
            Assert.HasCount(1, retry.Arrivals);
        }

        // Assert
        Assert.AreEqual(benign ? 1 : 2, scenario.Handler.Attempts);
        Assert.AreEqual(benign ? TransitRequestOutcome.Success : TransitRequestOutcome.ApiFailure,
            scenario.Service.Diagnostics.Snapshot.First(o => o.Phase == TransitRequestPhase.Completed).Outcome);
    }

    [TestMethod]
    public async Task GetAsync_CanceledOldResponseCompletesAfterReplacement_DoesNotOverwriteNewSnapshot()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> old = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource completedOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Service.Diagnostics.Observed += (_, observation) =>
        {
            if (observation.RequestId == 1 && observation.Phase == TransitRequestPhase.Completed)
            {
                completedOld.TrySetResult();
            }
        };
        scenario.Handler.RespondAsync = (_, _) => old.Task; // Deliberately ignores cancellation.
        using CancellationTokenSource cancellation = new();
        Task<PredictionSnapshot> stale = scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => stale);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(Body(["1"])));

        // Act
        PredictionSnapshot fresh = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));
        old.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await completedOld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        PredictionSnapshot reused = await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, reused.Arrivals);
        Assert.AreEqual(fresh.FetchedAt, reused.FetchedAt);
    }

    [TestMethod]
    public async Task SetBrowsingStops_HiddenOrChangedDemand_DoesNotStartWorkOrExpandTracking()
    {
        // Arrange
        Scenario scenario = new();
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Favorites, ["1", "2"]);
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Commute, ["3", "4"]);
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Commute, []);

        // Act
        Assert.AreEqual(0, scenario.Handler.Attempts);
        await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(10));
        scenario.Coordinator.SetBrowsingStops(TransitConsumer.Favorites, ["2"]);
        await scenario.Coordinator.GetAsync(["2"], TimeSpan.FromSeconds(30), combineBrowsing: true);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        CollectionAssert.AreEqual(new[] { "1", "2" }, scenario.Batches.SelectMany(b => b).ToArray());
    }

    [TestMethod]
    public async Task GetAsync_ManySuccessfulStops_KeepsOnlyBoundedSnapshots()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Coordinator.GetAsync(Ids(PredictionCoordinator.RetainedStops + 1), TimeSpan.FromSeconds(30));
        int before = scenario.Handler.Attempts;

        // Act
        await scenario.Coordinator.GetAsync(["129"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(before, scenario.Handler.Attempts);
        await scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30));

        // Assert
        Assert.AreEqual(before + 1, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetAsync_InvalidInputs_RejectsWithoutHttp()
    {
        // Arrange
        Scenario scenario = new();

        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new PredictionCoordinator(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scenario.Coordinator.SetBrowsingStops(TransitConsumer.Tracking, ["1"]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scenario.Coordinator.SetBrowsingStops(TransitConsumer.Favorites, Ids(129)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scenario.Coordinator.GetAsync([" "], TimeSpan.FromSeconds(30)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => scenario.Coordinator.GetAsync(["1"], TimeSpan.Zero));
        await Assert.ThrowsAsync<OperationCanceledException>(() => scenario.Coordinator.GetAsync(["1"], TimeSpan.FromSeconds(30), cancellationToken: new CancellationToken(true)));
        Assert.AreEqual(0, scenario.Handler.Attempts);
    }

    private static string[] Ids(int count) => Enumerable.Range(1, count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    private static string Body(IEnumerable<string> ids) =>
        """{"bustime-response":{"prd":[""" +
        string.Join(',', ids.Select(id => $$"""{"stpid":"{{id}}","rt":"14","rtdir":"N","vid":"5501","prdtm":"20261006 20:24","dyn":0}""")) +
        "]}}";

    private sealed class Scenario
    {
        public Scenario()
        {
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Service.EndpointCalled += (_, endpoint) => Tally.Record(endpoint, DateOnly.FromDateTime(Clock.GetUtcNow().Date));
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(Body(Batches.Last())));
            Coordinator = new PredictionCoordinator(Service, Clock);
            // Capture only fixture stop IDs, never retain/log the request URL or key.
            Handler.CaptureIds = ids => Batches.Add(ids);
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public List<string[]> Batches { get; } = [];
        public ApiCallLog Tally { get; } = new();
        public BusTimeTransitService Service { get; }
        public PredictionCoordinator Coordinator { get; }
    }
}
