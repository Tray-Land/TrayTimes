using System.Net;
using System.Text.Json;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class MetadataTransitServiceTests
{
    internal const string MetadataBody = """
        {"bustime-response":{
          "routes":[{"rt":"14","rtdd":"14X","rtnm":"Fixture route","rtclr":"#112233"}],
          "directions":[{"id":"N","name":"Northbound"}],
          "stops":[{"stpid":"2","stpnm":"Second","lat":43,"lon":-87},{"stpid":"1","stpnm":"First","lat":43.1,"lon":-87.1}],
          "ptr":[{"pid":"p","rtdir":"N","pt":[{"seq":1,"lat":43,"lon":-87},{"seq":2,"lat":43.1,"lon":-87.1}]}],
          "prd":[],"vehicle":[]}}
        """;

    [TestMethod]
    [DataRow(TransitEndpoint.Routes)]
    [DataRow(TransitEndpoint.Directions)]
    [DataRow(TransitEndpoint.Stops)]
    [DataRow(TransitEndpoint.Patterns)]
    public async Task GetAsync_ConcurrentConsumersAndFreshWindowRecreation_CountsOnePhysicalAttempt(TransitEndpoint endpoint)
    {
        // Arrange
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => response.Task;

        // Act
        Task first;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.StopDirectory, TransitTrigger.DirectoryBuild))
        {
            first = f.LoadAsync(endpoint);
        }

        Task second;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection))
        {
            second = f.LoadAsync(endpoint);
        }

        response.SetResult(RequestTestHandler.Response(MetadataBody));
        await Task.WhenAll(first, second);
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Metadata))
        {
            await f.LoadAsync(endpoint); // A new window uses the same app-owned service.
        }

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.CountedAttempts);
        Assert.AreEqual(1, f.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.Started));
        Assert.AreEqual(TransitConsumer.Settings, f.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.InFlightReuse).Consumer);
        Assert.AreEqual(TransitConsumer.Favorites, f.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.CacheReuse).Consumer);
    }

    [TestMethod]
    [DataRow(TransitEndpoint.Routes)]
    [DataRow(TransitEndpoint.Directions)]
    [DataRow(TransitEndpoint.Stops)]
    [DataRow(TransitEndpoint.Patterns)]
    public async Task GetAsync_ExactLifetimeBoundary_RefetchesOnlyWhenExpired(TransitEndpoint endpoint)
    {
        // Arrange
        using Fixture f = new();
        TimeSpan lifetime = endpoint == TransitEndpoint.Patterns
            ? MetadataTransitService.PatternLifetime : MetadataTransitService.DirectoryLifetime;
        await f.LoadAsync(endpoint);

        // Act
        f.Clock.Advance(lifetime - TimeSpan.FromTicks(1));
        await f.LoadAsync(endpoint);
        int beforeExpiry = f.Handler.Attempts;
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await f.LoadAsync(endpoint);

        // Assert
        Assert.AreEqual(1, beforeExpiry);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(TransitEndpoint.Routes)]
    [DataRow(TransitEndpoint.Directions)]
    [DataRow(TransitEndpoint.Stops)]
    [DataRow(TransitEndpoint.Patterns)]
    public async Task GetAsync_FailedLoad_IsNotCachedAsSuccessAndCanRecover(TransitEndpoint endpoint)
    {
        // Arrange
        using Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.LoadAsync(endpoint));
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody));

        // Act
        await f.LoadAsync(endpoint);
        await f.LoadAsync(endpoint);

        // Assert
        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.AreEqual(2, f.CountedAttempts);
    }

    [TestMethod]
    [DataRow(TransitEndpoint.Routes)]
    [DataRow(TransitEndpoint.Directions)]
    [DataRow(TransitEndpoint.Stops)]
    [DataRow(TransitEndpoint.Patterns)]
    public async Task GetAsync_SuccessfulEmptyMetadata_RemainsFresh(TransitEndpoint endpoint)
    {
        // Arrange
        using Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"bustime-response":{}}"""));

        // Act
        await f.LoadAsync(endpoint);
        await f.LoadAsync(endpoint);

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetPatternsAsync_CanceledSettingsWaiter_PreservesSharedFlyoutLoad()
    {
        // Arrange
        using Fixture f = new();
        using CancellationTokenSource settings = new();
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken physical = default;
        f.Handler.RespondAsync = (_, token) => { physical = token; return response.Task; };
        Task<IReadOnlyList<RoutePattern>> picker = f.Service.GetPatternsAsync("14", settings.Token);
        Task<IReadOnlyList<RoutePattern>> flyout = f.Service.GetPatternsAsync("14");

        // Act
        settings.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => picker);
        bool canceledBeforeCompletion = physical.IsCancellationRequested;
        response.SetResult(RequestTestHandler.Response(MetadataBody));
        IReadOnlyList<RoutePattern> patterns = await flyout;

        // Assert
        Assert.IsFalse(canceledBeforeCompletion);
        Assert.HasCount(1, patterns);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreSame(patterns, await f.Service.GetPatternsAsync("14"));
    }

    [TestMethod]
    public async Task GetPatternsAsync_LastWaiterCanceled_CancelsPhysicalLoadAndAllowsReplacement()
    {
        // Arrange
        using Fixture f = new();
        using CancellationTokenSource waiter = new();
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return RequestTestHandler.Response(MetadataBody);
        };
        Task abandoned = f.Service.GetPatternsAsync("14", waiter.Token);

        // Act
        waiter.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody));
        IReadOnlyList<RoutePattern> replacement = await f.Service.GetPatternsAsync("14");

        // Assert
        Assert.HasCount(1, replacement);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetPatternsAsync_AlreadyCanceled_DoesNotStartHttpOrReuseData()
    {
        // Arrange
        using Fixture f = new();
        using CancellationTokenSource waiter = new();
        waiter.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.GetPatternsAsync("14", waiter.Token));
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Invalidate_OldLoadFinishesAfterReplacement_CannotReplaceNewMetadata()
    {
        // Arrange
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> old = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => old.Task;
        Task<IReadOnlyList<TransitStop>> pending = f.Service.GetStopsAsync("14", "N");

        // Act
        f.Service.Invalidate();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody.Replace("Second", "New second", StringComparison.Ordinal)));
        IReadOnlyList<TransitStop> newer = await f.Service.GetStopsAsync("14", "N");
        old.SetResult(RequestTestHandler.Response(MetadataBody));
        await pending;
        IReadOnlyList<TransitStop> cached = await f.Service.GetStopsAsync("14", "N");

        // Assert
        Assert.AreEqual("New second", cached[0].Name);
        Assert.AreSame(newer, cached);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetPatternsAsync_MoreThanBound_EvictsLeastRecentlyUsedSuccess()
    {
        // Arrange
        using Fixture f = new();
        for (int i = 0; i < MetadataTransitService.RetainedPatterns; i++)
        {
            await f.Service.GetPatternsAsync($"r{i}");
        }

        // Act
        await f.Service.GetPatternsAsync("r0"); // Keep it hot; r1 should be evicted.
        await f.Service.GetPatternsAsync("extra");
        bool firstRetained = f.Service.TryGetPatterns("r0", out _);
        bool secondRetained = f.Service.TryGetPatterns("r1", out _);
        await f.Service.GetPatternsAsync("r1");

        // Assert
        Assert.IsTrue(firstRetained);
        Assert.IsFalse(secondRetained);
        Assert.AreEqual(MetadataTransitService.RetainedPatterns + 2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetPatternsAsync_AllEntriesInFlight_RejectsOverflowWithoutSendingIt()
    {
        // Arrange
        using Fixture f = new();
        TaskCompletionSource response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = async (_, _) =>
        {
            await response.Task;
            return RequestTestHandler.Response(MetadataBody);
        };
        Task[] loads = [.. Enumerable.Range(0, MetadataTransitService.RetainedPatterns).Select(i => f.Service.GetPatternsAsync($"r{i}"))];

        // Act
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.GetPatternsAsync("overflow"));
        response.SetResult();
        await Task.WhenAll(loads);

        // Assert
        Assert.AreEqual(MetadataTransitService.RetainedPatterns, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task TryGetPatterns_Expired_DoesNotStartWork()
    {
        // Arrange
        using Fixture f = new();
        Assert.IsFalse(f.Service.TryGetPatterns("14", out _));
        await f.Service.GetPatternsAsync("14");

        // Act
        bool fresh = f.Service.TryGetPatterns("14", out _);
        f.Clock.Advance(MetadataTransitService.PatternLifetime);
        bool expired = f.Service.TryGetPatterns("14", out _);

        // Assert
        Assert.IsTrue(fresh);
        Assert.IsFalse(expired);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task SeedDirectory_CompleteCrawlRoundTrip_ReusesNamesLabelsIdsAndProviderOrderingWithoutHttp()
    {
        // Arrange
        using Fixture f = new();
        StopDirectoryBuilder builder = new(f.Service);
        await builder.BuildAsync();
        Assert.IsNotNull(builder.Metadata);
        string json = JsonSerializer.Serialize(builder.Metadata);
        StopDirectoryMetadata? restored = JsonSerializer.Deserialize<StopDirectoryMetadata>(json);
        using Fixture recreated = new();

        // Act
        bool seeded = recreated.Service.SeedDirectory(restored, recreated.Clock.GetUtcNow() - TimeSpan.FromDays(1));
        IReadOnlyList<TransitRoute> routes = await recreated.Service.GetRoutesAsync();
        IReadOnlyList<TransitDirection> directions = await recreated.Service.GetDirectionsAsync("14");
        IReadOnlyList<TransitStop> stops = await recreated.Service.GetStopsAsync("14", "N");

        // Assert
        Assert.IsTrue(seeded);
        Assert.AreEqual("14X", routes.Single().DisplayId);
        Assert.AreEqual("Fixture route", routes.Single().Name);
        Assert.AreEqual("Northbound", directions.Single().Name);
        CollectionAssert.AreEqual(new[] { "2", "1" }, stops.Select(s => s.Id).ToArray());
        Assert.AreEqual(new GeoPoint(43.1, -87.1), stops[1].Location);
        Assert.AreEqual(3, f.Handler.Attempts);
        Assert.AreEqual(0, recreated.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task SeedDirectory_LegacyExpiredFutureOrInvalidMetadata_FallsBackExplicitly(int scenario)
    {
        // Arrange
        using Fixture f = new();
        StopDirectoryMetadata? metadata = CompleteMetadata();
        DateTimeOffset builtAt = f.Clock.GetUtcNow();
        switch (scenario)
        {
            case 0: metadata = null; break;
            case 1: builtAt -= MetadataTransitService.DirectoryLifetime; break;
            case 2: builtAt += TimeSpan.FromSeconds(1); break;
            case 3: metadata = metadata with { Version = 0 }; break;
            case 4: metadata = new StopDirectoryMetadata(1, [new DirectoryRouteMetadata(new TransitRoute("14", "14", "", null), [])]); break;
        }

        // Act
        bool seeded = f.Service.SeedDirectory(metadata, builtAt);
        await f.Service.GetRoutesAsync();
        await f.Service.GetDirectionsAsync("14");
        await f.Service.GetStopsAsync("14", "N");

        // Assert
        Assert.IsFalse(seeded);
        Assert.AreEqual(3, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task SeedDirectory_AlmostExpired_PreservesOriginalBuildAge()
    {
        // Arrange
        using Fixture f = new();
        DateTimeOffset builtAt = f.Clock.GetUtcNow() - MetadataTransitService.DirectoryLifetime + TimeSpan.FromSeconds(1);
        Assert.IsTrue(f.Service.SeedDirectory(CompleteMetadata(), builtAt));
        await f.Service.GetRoutesAsync();

        // Act
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Service.GetRoutesAsync();

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetStopsAsync_DifferentDirection_DoesNotReuseWrongStopList()
    {
        // Arrange
        using Fixture f = new();
        f.Service.SeedDirectory(CompleteMetadata(), f.Clock.GetUtcNow());

        // Act
        await f.Service.GetStopsAsync("14", "N");
        await f.Service.GetStopsAsync("14", "S");

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetArrivalsAndVehiclesAsync_RepeatedCalls_AreForwardedWithoutMetadataCaching()
    {
        // Arrange
        using Fixture f = new();

        // Act
        await f.Service.GetArrivalsAsync(["1"]);
        await f.Service.GetArrivalsAsync(["1"]);
        await f.Service.GetVehiclesAsync(["5501"]);
        await f.Service.GetVehiclesAsync(["5501"]);

        // Assert
        Assert.AreEqual(4, f.Handler.Attempts);
        Assert.IsTrue(f.Service.IsConfigured);
        Assert.AreEqual(10, f.Service.MaxStopsPerRequest);
        Assert.AreEqual("Test", f.Service.AgencyName);
    }

    [TestMethod]
    public async Task GetPatternsAsync_FailureCooldown_RemainsInsideMetadataCacheAndSuccessCanBeReused()
    {
        // Arrange
        RequestTestClock clock = new();
        using RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));
        MetadataTransitService service = new(new CooldownTransitService(handler.CreateService(), clock), clock);
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Metadata);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetPatternsAsync("14"));

        // Act
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => service.GetPatternsAsync("14"));
        clock.Advance(TimeSpan.FromSeconds(15));
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody));
        await service.GetPatternsAsync("14");
        await service.GetPatternsAsync("14");

        // Assert
        Assert.AreEqual(2, handler.Attempts);
    }

    [TestMethod]
    public async Task GetPatternsAsync_SlowResponse_DoesNotExtendFreshnessBeyondRequestStart()
    {
        // Arrange
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => response.Task;
        Task pending = f.Service.GetPatternsAsync("14");

        // Act
        f.Clock.Advance(MetadataTransitService.PatternLifetime);
        response.SetResult(RequestTestHandler.Response(MetadataBody));
        await pending;
        bool fresh = f.Service.TryGetPatterns("14", out _);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody));
        await f.Service.GetPatternsAsync("14");

        // Assert
        Assert.IsFalse(fresh);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(TransitEndpoint.Directions)]
    [DataRow(TransitEndpoint.Stops)]
    public async Task GetAsync_MoreThanDirectoryCacheBound_EvictsOldSuccesses(TransitEndpoint endpoint)
    {
        // Arrange
        using Fixture f = new();
        int capacity = endpoint == TransitEndpoint.Directions
            ? MetadataTransitService.RetainedRoutes : MetadataTransitService.RetainedStopLists;
        Task Load(string route) => endpoint == TransitEndpoint.Directions
            ? f.Service.GetDirectionsAsync(route) : f.Service.GetStopsAsync(route, "N");
        for (int i = 0; i <= capacity; i++)
        {
            await Load($"r{i}");
        }

        // Act
        await Load("r0");

        // Assert
        Assert.AreEqual(capacity + 2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task SeedDirectory_NewerEndpointLoadOrSharedMiss_DoesNotReplaceLiveConsumers()
    {
        // Arrange
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => response.Task;
        Task<IReadOnlyList<TransitStop>> pending = f.Service.GetStopsAsync("14", "N");

        // Act
        Assert.IsTrue(f.Service.SeedDirectory(CompleteMetadata(), f.Clock.GetUtcNow() - TimeSpan.FromDays(1)));
        response.SetResult(RequestTestHandler.Response(MetadataBody.Replace("Second", "Endpoint second", StringComparison.Ordinal)));
        await pending;
        f.Service.SeedDirectory(CompleteMetadata(), f.Clock.GetUtcNow() - TimeSpan.FromDays(1));
        IReadOnlyList<TransitStop> current = await f.Service.GetStopsAsync("14", "N");

        // Assert
        Assert.AreEqual("Endpoint second", current[0].Name);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    internal static StopDirectoryMetadata CompleteMetadata() => new(1, [
        new DirectoryRouteMetadata(new TransitRoute("14", "14X", "Fixture route", "#112233"), [
            new DirectoryDirectionMetadata(new TransitDirection("N", "Northbound"), [
                new TransitStop("2", "Second", new GeoPoint(43, -87)),
                new TransitStop("1", "First", new GeoPoint(43.1, -87.1)),
            ]),
        ]),
    ]);

    internal sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataBody));
            TrayTimes.Transit.BusTime.BusTimeTransitService raw = Handler.CreateService(Diagnostics);
            raw.EndpointCalled += (_, _) => Interlocked.Increment(ref _countedAttempts);
            Service = new MetadataTransitService(raw, Clock);
        }

        private int _countedAttempts;
        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public TransitRequestDiagnostics Diagnostics { get; } = new();
        public MetadataTransitService Service { get; }
        public int CountedAttempts => _countedAttempts;

        public Task LoadAsync(TransitEndpoint endpoint, CancellationToken token = default) => endpoint switch
        {
            TransitEndpoint.Routes => Service.GetRoutesAsync(token),
            TransitEndpoint.Directions => Service.GetDirectionsAsync("14", token),
            TransitEndpoint.Stops => Service.GetStopsAsync("14", "N", token),
            TransitEndpoint.Patterns => Service.GetPatternsAsync("14", token),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

        public void Dispose() => Handler.Dispose();
    }
}
