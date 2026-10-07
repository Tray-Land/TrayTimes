using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TransitRequestDiagnosticsTests
{
    [TestMethod]
    public async Task GetRequests_AllEndpoints_ObservesTheActualHttpPathAndCountsOncePerEndpoint()
    {
        // Arrange
        RequestTestHandler handler = new();
        BusTimeTransitService service = handler.CreateService();
        List<string> endpoints = [];
        service.EndpointCalled += (_, endpoint) => endpoints.Add(endpoint);

        // Act
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection))
        {
            await service.GetRoutesAsync();
            await service.GetDirectionsAsync("14");
            await service.GetStopsAsync("14", "N");
            await service.GetPatternsAsync("14");
        }

        await service.GetArrivalsAsync(["1"]);
        await service.GetVehiclesAsync(["5501"]);

        // Assert
        Assert.AreEqual(6, handler.Attempts);
        CollectionAssert.AreEqual(new[] { "getroutes", "getdirections", "getstops", "getpatterns", "getpredictions", "getvehicles" }, endpoints);
        TransitRequestObservation[] completed = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)];
        CollectionAssert.AreEqual(new[]
        {
            TransitEndpoint.Routes, TransitEndpoint.Directions, TransitEndpoint.Stops,
            TransitEndpoint.Patterns, TransitEndpoint.Predictions, TransitEndpoint.Vehicles,
        }, completed.Select(o => o.Endpoint).ToArray());
        Assert.IsTrue(completed.Take(4).All(o => o.Consumer == TransitConsumer.Settings));
    }

    [TestMethod]
    public async Task GetVehiclesAsync_SlowTimerAndResultOverlap_ObservesTwoUnoptimizedPhysicalAttempts()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => handler.Attempts == 1 ? first.Task : second.Task;
        BusTimeTransitService service = handler.CreateService();
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        Task<IReadOnlyList<Vehicle>> timer;
        Task<IReadOnlyList<Vehicle>> result;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Timer))
        {
            timer = service.GetVehiclesAsync(["5501"]);
        }

        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FavoritesResult))
        {
            result = service.GetVehiclesAsync(["5501"]);
        }

        Assert.HasCount(2, service.Diagnostics.Snapshot);
        first.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        second.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await Task.WhenAll(timer, result);

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(2, tally);
        Assert.AreEqual(2, service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.Completed));
    }

    [TestMethod]
    public async Task GetArrivalsAsync_Success_RecordsCorrelatedTimingAndOneAttempt()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        };
        BusTimeTransitService service = handler.CreateService(new TransitRequestDiagnostics(clock));
        ApiCallLog tally = new();
        service.EndpointCalled += (_, endpoint) => tally.Record(endpoint, DateOnly.FromDateTime(clock.GetUtcNow().Date));

        // Act
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Timer))
        {
            await service.GetArrivalsAsync(["1", "2"]);
        }

        // Assert
        TransitRequestObservation start = service.Diagnostics.Snapshot[0];
        TransitRequestObservation end = service.Diagnostics.Snapshot[1];
        Assert.AreEqual(start.RequestId, end.RequestId);
        Assert.AreEqual(TransitRequestPhase.Started, start.Phase);
        Assert.IsNull(start.FinishedAt);
        Assert.AreEqual(TransitRequestPhase.Completed, end.Phase);
        Assert.AreEqual(start.StartedAt.AddSeconds(2), end.FinishedAt);
        Assert.AreEqual(TimeSpan.FromSeconds(2), end.Elapsed);
        Assert.AreEqual(TransitConsumer.Favorites, end.Consumer);
        Assert.AreEqual(TransitTrigger.Timer, end.Trigger);
        Assert.AreEqual(TransitEndpoint.Predictions, end.Endpoint);
        Assert.AreEqual(TransitRequestOutcome.Success, end.Outcome);
        Assert.AreEqual(2, end.BatchSize);
        Assert.AreEqual(200, end.HttpStatus);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, tally.Summarize(DateOnly.FromDateTime(clock.GetUtcNow().Date)).Total.Today);
    }

    [TestMethod]
    [DataRow(TransitRequestOutcome.HttpFailure)]
    [DataRow(TransitRequestOutcome.ApiFailure)]
    [DataRow(TransitRequestOutcome.TransportFailure)]
    [DataRow(TransitRequestOutcome.Timeout)]
    public async Task GetRoutesAsync_Failure_RecordsSafeOutcomeAndCountsOneAttempt(TransitRequestOutcome expected)
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => expected switch
        {
            TransitRequestOutcome.HttpFailure => Task.FromResult(RequestTestHandler.Response("fixture-secret", HttpStatusCode.TooManyRequests)),
            TransitRequestOutcome.ApiFailure => Task.FromResult(RequestTestHandler.Response("""{"bustime-response":{"error":[{"msg":"fixture-secret 43.1 -88.1 private address"}]}}""")),
            TransitRequestOutcome.Timeout => throw new TaskCanceledException("fixture-secret private address"),
            _ => throw new HttpRequestException("https://bustime.example/?key=fixture-secret"),
        };
        BusTimeTransitService service = handler.CreateService();
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        await Assert.ThrowsAsync<Exception>(() => service.GetRoutesAsync());

        // Assert
        TransitRequestObservation end = service.Diagnostics.Snapshot.Last();
        Assert.AreEqual(expected, end.Outcome);
        Assert.AreEqual(expected == TransitRequestOutcome.HttpFailure ? 429 : expected == TransitRequestOutcome.ApiFailure ? 200 : (int?)null, end.HttpStatus);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, tally);
        string text = string.Join('\n', service.Diagnostics.Snapshot);
        Assert.IsFalse(text.Contains("fixture-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("bustime.example", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private address", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("43.1", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{}")]
    public async Task GetRoutesAsync_InvalidResponse_RecordsApiFailure(string body)
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        BusTimeTransitService service = handler.CreateService();

        // Act
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetRoutesAsync());

        // Assert
        Assert.AreEqual(TransitRequestOutcome.ApiFailure, service.Diagnostics.Snapshot.Last().Outcome);
    }

    [TestMethod]
    public async Task GetArrivalsAsync_CanceledInFlight_RecordsCancellationNotEmptySuccess()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new() { RespondAsync = (_, token) => pending.Task.WaitAsync(token) };
        BusTimeTransitService service = handler.CreateService();
        using CancellationTokenSource cancellation = new();
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        Task<IReadOnlyList<Arrival>> request = service.GetArrivalsAsync(["1"], cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request);

        // Assert
        Assert.AreEqual(TransitRequestOutcome.Canceled, service.Diagnostics.Snapshot.Last().Outcome);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, tally);
    }

    [TestMethod]
    public async Task GetRoutesAsync_NoKey_DoesNotObserveOrCountAnAttempt()
    {
        // Arrange
        BusTimeTransitService service = new("Test", new Uri("https://bustime.example/"), null, TimeZoneInfo.Utc);
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetRoutesAsync());

        // Assert
        Assert.IsEmpty(service.Diagnostics.Snapshot);
        Assert.AreEqual(0, tally);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(25)]
    public async Task GetRequests_IdBoundaries_ObservesCurrentPhysicalBatchSizeWithoutChangingTruncation(int count)
    {
        // Arrange
        RequestTestHandler handler = new();
        BusTimeTransitService service = handler.CreateService();
        string[] ids = Enumerable.Range(1, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // Act
        await service.GetArrivalsAsync(ids);
        await service.GetVehiclesAsync(ids);

        // Assert
        Assert.AreEqual(count == 0 ? 0 : 2, handler.Attempts);
        TransitRequestObservation[] completed = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)];
        Assert.IsTrue(completed.All(o => o.BatchSize == Math.Min(count, 10)));
        Assert.IsTrue(completed.All(o => o.Outcome == TransitRequestOutcome.Success));
    }

    [TestMethod]
    public async Task BeginScope_ConcurrentRequestsAndNestedScope_PreservesEachConsumersLabels()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => handler.Attempts == 1 ? first.Task : second.Task;
        BusTimeTransitService service = handler.CreateService();

        // Act
        Task<IReadOnlyList<Arrival>> favorites;
        Task<IReadOnlyList<Arrival>> tracking;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened))
        {
            favorites = service.GetArrivalsAsync(["1"]);
            using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Tracking, TransitTrigger.Timer))
            {
                tracking = service.GetArrivalsAsync(["1"]);
            }

            service.Diagnostics.RecordReuse(TransitEndpoint.Patterns, inFlight: false, batchSize: 1);
        }

        second.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        first.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await Task.WhenAll(favorites, tracking);
        service.Diagnostics.RecordReuse(TransitEndpoint.Routes, inFlight: true);

        // Assert
        TransitRequestObservation[] completed = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)];
        Assert.AreEqual(TransitConsumer.Favorites, completed.Single(o => o.RequestId == 1).Consumer);
        Assert.AreEqual(TransitConsumer.Tracking, completed.Single(o => o.RequestId == 2).Consumer);
        Assert.AreEqual(TransitConsumer.Favorites, service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.CacheReuse).Consumer);
        Assert.AreEqual(TransitConsumer.Unspecified, service.Diagnostics.Snapshot.Last().Consumer);
        Assert.AreEqual(2, handler.Attempts); // Observation does not yet deduplicate overlapping requests.
    }

    [TestMethod]
    public void RecordReuse_ManyObservations_RetainsBoundedSnapshotAndRaisesEveryEvent()
    {
        // Arrange
        TransitRequestDiagnostics diagnostics = new();
        int raised = 0;
        diagnostics.Observed += (_, _) => ++raised;
        IReadOnlyList<TransitRequestObservation> before = diagnostics.Snapshot;

        // Act
        for (int i = 0; i < 300; ++i)
        {
            diagnostics.RecordReuse(TransitEndpoint.Routes, inFlight: i % 2 == 0);
        }

        // Assert
        Assert.AreEqual(300, raised);
        Assert.HasCount(TransitRequestDiagnostics.RetainedObservations, diagnostics.Snapshot);
        Assert.IsEmpty(before);
        Assert.IsTrue(diagnostics.Snapshot.All(o => o.RequestId == 0 && o.Elapsed == TimeSpan.Zero));
        Assert.AreEqual(TransitRequestOutcome.Success, diagnostics.Snapshot.Last().Outcome);
    }
}
