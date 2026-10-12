using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class StopDirectoryBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_RoutesSharingAStop_MergesTheirRoutesOntoOneEntry()
    {
        // Arrange
        FakeDirectoryService service = new(3);

        // Act
        StopDirectoryBuilder builder = new(service);
        IReadOnlyList<IndexedStop> stops = await builder.BuildAsync();

        // Assert
        Assert.HasCount(4, stops);
        IndexedStop shared = stops.Single(s => s.Id == "shared");
        Assert.HasCount(3, shared.Routes);
        Assert.IsNotNull(builder.Metadata);
        Assert.HasCount(3, builder.Metadata.Routes);
        CollectionAssert.AreEqual(new[] { "shared", "own-r0" }, builder.Metadata.Routes[0].Directions[0].Stops.Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public async Task BuildAsync_RateLimited_StopsTheCrawlEarlyAndRethrows()
    {
        // Arrange
        FakeDirectoryService service = new(40) { Directions = _ => new TransitRateLimitException(System.Net.HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(60)) };

        // Act
        await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => new StopDirectoryBuilder(service).BuildAsync());

        // Assert
        Assert.IsLessThanOrEqualTo(4, service.DirectionCalls);
    }

    [TestMethod]
    public async Task BuildAsync_CooldownRefusal_StopsTheCrawlEarlyAndRethrows()
    {
        // Arrange
        FakeDirectoryService service = new(40) { Directions = _ => new TransitCooldownException(TimeSpan.FromSeconds(15)) };

        // Act
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => new StopDirectoryBuilder(service).BuildAsync());

        // Assert
        Assert.IsLessThanOrEqualTo(4, service.DirectionCalls);
    }

    [TestMethod]
    public async Task BuildAsync_TooManyRoutesFailing_AbortsWithoutCrawlingTheRest()
    {
        // Arrange
        FakeDirectoryService service = new(40) { Directions = _ => new HttpRequestException("down") };

        // Act
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => new StopDirectoryBuilder(service).BuildAsync());

        // Assert
        Assert.IsLessThan(40, service.DirectionCalls);
    }

    [TestMethod]
    public async Task BuildAsync_FewRoutesFailing_KeepsTheStopsThatLoaded()
    {
        // Arrange
        FakeDirectoryService service = new(8) { Directions = id => id == "r0" ? new HttpRequestException("down") : null };

        // Act
        StopDirectoryBuilder builder = new(service);
        IReadOnlyList<IndexedStop> stops = await builder.BuildAsync();

        // Assert
        Assert.IsTrue(stops.Any(s => s.Id == "shared"));
        Assert.HasCount(7, stops.Single(s => s.Id == "shared").Routes);
        Assert.IsNull(builder.Metadata); // Partial geography must not masquerade as complete picker metadata.
    }

    [TestMethod]
    public async Task BuildAsync_NoStops_Throws()
    {
        // Arrange
        FakeDirectoryService service = new(0);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => new StopDirectoryBuilder(service).BuildAsync());
    }

    [TestMethod]
    public async Task BuildAsync_CallerCancels_StopsAndThrowsCancellation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        FakeDirectoryService service = new(8) { HangDirections = true };
        Task<IReadOnlyList<IndexedStop>> build = new StopDirectoryBuilder(service).BuildAsync(cancellation.Token);
        await service.DirectionsStarted.Task;

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => build);
    }

    private sealed class FakeDirectoryService(int routeCount) : ITransitService
    {
        private int _directionCalls;

        public int DirectionCalls => _directionCalls;

        public Func<string, Exception?>? Directions { get; init; }

        public bool HangDirections { get; init; }

        public TaskCompletionSource DirectionsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string AgencyName => "Test";

        public bool IsConfigured => true;

        public int MaxStopsPerRequest => 10;

        public Task<IReadOnlyList<TransitRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransitRoute>>([.. Enumerable.Range(0, routeCount).Select(i => new TransitRoute($"r{i}", $"{i}", $"Route {i}", null))]);

        public async Task<IReadOnlyList<TransitDirection>> GetDirectionsAsync(string routeId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _directionCalls);
            if (HangDirections)
            {
                DirectionsStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Directions?.Invoke(routeId) is { } failure)
            {
                throw failure;
            }

            return [new TransitDirection("N", "North")];
        }

        public Task<IReadOnlyList<TransitStop>> GetStopsAsync(string routeId, string directionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransitStop>>([
                new TransitStop("shared", "Shared", new GeoPoint(43, -87)),
                new TransitStop($"own-{routeId}", "Own", new GeoPoint(43.1, -87.1)),
            ]);

        public Task<IReadOnlyList<Arrival>> GetArrivalsAsync(IReadOnlyCollection<string> stopIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(IReadOnlyCollection<string> vehicleIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

[TestClass]
public sealed class StopDirectoryBuildCoordinatorTests
{
    [TestMethod]
    public async Task BuildAsync_AutomaticAfterFailure_IsRefusedWithoutCrawling()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Coordinator.BuildAsync(manual: false));

        // Act
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Coordinator.BuildAsync(manual: false));

        // Assert
        Assert.AreEqual(1, f.Builds);
    }

    [TestMethod]
    public async Task BuildAsync_AutomaticAfterTheHoldOff_BuildsAgain()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Coordinator.BuildAsync(manual: false));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        f.Succeed();

        // Act
        IReadOnlyList<IndexedStop> stops = await f.Coordinator.BuildAsync(manual: false);

        // Assert
        Assert.IsEmpty(stops);
        Assert.AreEqual(2, f.Builds);
    }

    [TestMethod]
    public async Task BuildAsync_ManualAfterFailure_BypassesTheHoldOff()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Coordinator.BuildAsync(manual: false));
        f.Succeed();

        // Act
        await f.Coordinator.BuildAsync(manual: true);

        // Assert
        Assert.AreEqual(2, f.Builds);
    }

    [TestMethod]
    public async Task BuildAsync_ConcurrentCallers_ShareOneBuild()
    {
        // Arrange
        Fixture f = new();
        TaskCompletionSource<IReadOnlyList<IndexedStop>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Build = _ => gate.Task;

        // Act
        Task<IReadOnlyList<IndexedStop>> first = f.Coordinator.BuildAsync(manual: false);
        Task<IReadOnlyList<IndexedStop>> second = f.Coordinator.BuildAsync(manual: true);
        bool runningDuring = f.Coordinator.IsRunning;
        gate.SetResult([]);
        await Task.WhenAll(first, second);

        // Assert
        Assert.IsTrue(runningDuring);
        Assert.IsFalse(f.Coordinator.IsRunning);
        Assert.AreEqual(1, f.Builds);
    }

    [TestMethod]
    public async Task BuildAsync_SuccessAfterFailure_RestartsTheBackoffAtTheInitialDelay()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Coordinator.BuildAsync(manual: false));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        f.Succeed();
        await f.Coordinator.BuildAsync(manual: false);
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Coordinator.BuildAsync(manual: false));

        // Act
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        f.Succeed();

        // Assert
        await f.Coordinator.BuildAsync(manual: false);
        Assert.AreEqual(4, f.Builds);
    }

    private sealed class Fixture
    {
        public Fixture() =>
            Coordinator = new StopDirectoryBuildCoordinator(token =>
            {
                ++Builds;
                return Build(token);
            }, Clock);

        public RequestTestClock Clock { get; } = new();

        public StopDirectoryBuildCoordinator Coordinator { get; }

        public int Builds { get; private set; }

        public Func<CancellationToken, Task<IReadOnlyList<IndexedStop>>> Build { get; set; } =
            _ => Task.FromResult<IReadOnlyList<IndexedStop>>([]);

        public void Fail() => Build = _ => Task.FromException<IReadOnlyList<IndexedStop>>(new HttpRequestException("down"));

        public void Succeed() => Build = _ => Task.FromResult<IReadOnlyList<IndexedStop>>([]);
    }
}
