using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TransitRequestDiagnosticsTests
{
    private static readonly GeoPoint Here = new(43, -88);

    [TestMethod]
    public async Task GetRequests_AllEndpoints_ObservesTheActualHttpPathAndCountsOncePerRequest()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(request.RequestUri!.AbsolutePath switch
            {
                string p when p.EndsWith("available_networks", StringComparison.Ordinal) => """{"networks":[{"network_id":"N|Here"}]}""",
                string p when p.EndsWith("route_details", StringComparison.Ordinal) => """{"route":{"global_route_id":"14"},"itineraries":[]}""",
                _ => "{}",
            })),
        };
        TransitAppService service = handler.CreateService();
        List<string> endpoints = [];
        service.EndpointCalled += (_, endpoint) => endpoints.Add(endpoint);

        // Act
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection))
        {
            await service.SearchStopsAsync(Here, "main");
            await service.GetRoutesNearAsync(Here);
            await service.GetRouteDetailsAsync("14");
        }

        await service.GetNearbyLinesAsync(Here, 500);
        await service.PlanTripAsync(Here, Here);
        await service.GetDeparturesAsync(["1"]);
        await service.GetVehiclesAsync("14");

        // Assert
        Assert.AreEqual(8, handler.Attempts);
        CollectionAssert.AreEqual(new[]
        {
            "search_stops", "available_networks", "routes_for_networks", "route_details", "nearby_routes", "plan", "stop_departures", "vehicles",
        }, endpoints);
        TransitRequestObservation[] completed = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)];
        CollectionAssert.AreEqual(new[]
        {
            TransitEndpoint.SearchStops, TransitEndpoint.Networks, TransitEndpoint.Routes, TransitEndpoint.RouteDetails,
            TransitEndpoint.NearbyRoutes, TransitEndpoint.Plan, TransitEndpoint.Departures, TransitEndpoint.Vehicles,
        }, completed.Select(o => o.Endpoint).ToArray());
        Assert.IsTrue(completed.Take(4).All(o => o.Consumer == TransitConsumer.Settings));
        Assert.IsTrue(completed.All(o => o.Outcome == TransitRequestOutcome.Success));
    }

    [TestMethod]
    public async Task GetDeparturesAsync_Success_RecordsCorrelatedTimingAndOneAttempt()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        };
        TransitAppService service = handler.CreateService(new TransitRequestDiagnostics(clock));
        ApiCallLog tally = new();
        service.EndpointCalled += (_, endpoint) => tally.Record(endpoint, DateOnly.FromDateTime(clock.GetUtcNow().Date));

        // Act
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Timer))
        {
            await service.GetDeparturesAsync(["1", "2"]);
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
        Assert.AreEqual(TransitEndpoint.Departures, end.Endpoint);
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
    public async Task GetDeparturesAsync_Failure_RecordsSafeOutcomeAndCountsOneAttempt(TransitRequestOutcome expected)
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => expected switch
        {
            TransitRequestOutcome.HttpFailure => Task.FromResult(RequestTestHandler.Response("fixture-secret", HttpStatusCode.TooManyRequests)),
            TransitRequestOutcome.ApiFailure => Task.FromResult(RequestTestHandler.Response("""{"error":"fixture-secret 43.1 -88.1 private address"}""", HttpStatusCode.BadRequest)),
            TransitRequestOutcome.Timeout => throw new TaskCanceledException("fixture-secret private address"),
            _ => throw new HttpRequestException("https://transit.example/?key=fixture-secret"),
        };
        TransitAppService service = handler.CreateService();
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        Exception failure = await Assert.ThrowsAsync<Exception>(() => service.GetDeparturesAsync(["1"]));

        // Assert
        TransitRequestObservation end = service.Diagnostics.Snapshot.Last();
        Assert.AreEqual(expected, end.Outcome);
        Assert.AreEqual(expected == TransitRequestOutcome.HttpFailure ? 429 : expected == TransitRequestOutcome.ApiFailure ? 400 : (int?)null, end.HttpStatus);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, tally);
        string text = string.Join('\n', service.Diagnostics.Snapshot);
        Assert.IsFalse(text.Contains("fixture-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("transit.example", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private address", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("43.1", StringComparison.Ordinal));
        if (failure is TransitApiException api)
        {
            Assert.IsFalse(api.Message.Contains("fixture-secret", StringComparison.Ordinal), "server text is never shown or logged");
        }
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    public async Task GetDeparturesAsync_InvalidResponse_RecordsApiFailure(string body)
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["1"]));

        // Assert
        Assert.AreEqual(TransitRequestOutcome.ApiFailure, service.Diagnostics.Snapshot.Last().Outcome);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_CanceledInFlight_RecordsCancellationNotEmptySuccess()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new() { RespondAsync = (_, token) => pending.Task.WaitAsync(token) };
        TransitAppService service = handler.CreateService();
        using CancellationTokenSource cancellation = new();
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        Task<DepartureBoard> request = service.GetDeparturesAsync(["1"], cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request);

        // Assert
        Assert.AreEqual(TransitRequestOutcome.Canceled, service.Diagnostics.Snapshot.Last().Outcome);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, tally);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_NoKey_DoesNotObserveOrCountAnAttempt()
    {
        // Arrange
        TransitAppService service = new(null);
        int tally = 0;
        service.EndpointCalled += (_, _) => ++tally;

        // Act
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["1"]));

        // Assert
        Assert.IsEmpty(service.Diagnostics.Snapshot);
        Assert.AreEqual(0, tally);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(100)]
    [DataRow(101)]
    public async Task GetDeparturesAsync_IdBoundaries_ObservesValidBatchesAndRejectsOversizedSets(int count)
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();
        string[] ids = Enumerable.Range(1, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // Act
        if (count > service.MaxStopsPerRequest)
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.GetDeparturesAsync(ids));
        }
        else
        {
            await service.GetDeparturesAsync(ids);
        }

        // Assert
        Assert.AreEqual(count is > 0 and <= 100 ? 1 : 0, handler.Attempts);
        TransitRequestObservation[] completed = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)];
        Assert.IsTrue(completed.All(o => o.BatchSize == count && o.Outcome == TransitRequestOutcome.Success));
    }

    [TestMethod]
    public async Task BeginScope_ConcurrentRequestsAndNestedScope_PreservesEachConsumersLabels()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => handler.Attempts == 1 ? first.Task : second.Task;
        TransitAppService service = handler.CreateService();

        // Act
        Task<DepartureBoard> favorites;
        Task<DepartureBoard> tracking;
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened))
        {
            favorites = service.GetDeparturesAsync(["1"]);
            using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Tracking, TransitTrigger.Timer))
            {
                tracking = service.GetDeparturesAsync(["1"]);
            }

            service.Diagnostics.RecordReuse(TransitEndpoint.RouteDetails, inFlight: false, batchSize: 1);
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
        Assert.AreEqual(2, handler.Attempts); // The client itself never deduplicates; the coordinators do.
    }

    [TestMethod]
    [DataRow(TransitConsumer.Tracking, TransitTrigger.Timer, TransitPriority.Urgent)]
    [DataRow(TransitConsumer.ActiveTrip, TransitTrigger.Timer, TransitPriority.Urgent)]
    [DataRow(TransitConsumer.Favorites, TransitTrigger.ManualRefresh, TransitPriority.Manual)]
    [DataRow(TransitConsumer.Nearby, TransitTrigger.FlyoutOpened, TransitPriority.Manual)]
    [DataRow(TransitConsumer.Commute, TransitTrigger.VehicleSelected, TransitPriority.Manual)]
    [DataRow(TransitConsumer.Settings, TransitTrigger.PickerSelection, TransitPriority.Manual)]
    [DataRow(TransitConsumer.Favorites, TransitTrigger.Timer, TransitPriority.Automatic)]
    [DataRow(TransitConsumer.Nearby, TransitTrigger.RefinedLocation, TransitPriority.Automatic)]
    [DataRow(TransitConsumer.Unspecified, TransitTrigger.Unspecified, TransitPriority.Automatic)]
    public void CurrentPriority_Scope_ClassifiesWhoTheRequestIsFor(TransitConsumer consumer, TransitTrigger trigger, TransitPriority expected)
    {
        // Arrange
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(consumer, trigger);

        // Act
        TransitPriority priority = TransitRequestDiagnostics.CurrentPriority;

        // Assert
        Assert.AreEqual(expected, priority);
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
