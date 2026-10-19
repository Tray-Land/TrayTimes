using System.Net;
using System.Text.Json;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class MetadataTransitServiceTests
{
    private static readonly GeoPoint Home = new(43.0, -88.0);
    private static readonly GeoPoint Work = new(43.1, -88.0);

    [TestMethod]
    public async Task GetRouteDetailsAsync_ConcurrentAndLaterCallers_ShareOneRequest()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task<RouteDetails> first = scenario.Metadata.GetRouteDetailsAsync("R:1");
        Task<RouteDetails> second = scenario.Metadata.GetRouteDetailsAsync("R:1");
        pending.SetResult(RequestTestHandler.Response(RouteDetailsBody("R:1")));
        RouteDetails[] results = await Task.WhenAll(first, second);
        RouteDetails later = await scenario.Metadata.GetRouteDetailsAsync("R:1");

        // Assert
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.AreSame(results[0], later);
        Assert.AreEqual(1, scenario.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
        Assert.AreEqual(1, scenario.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.CacheReuse));
    }

    [TestMethod]
    [DataRow(0.0, 1000, 1)]
    [DataRow(0.0012, 1000, 1)]
    [DataRow(0.002, 1000, 2)]
    [DataRow(0.0, 500, 2)]
    public async Task GetNearbyLinesAsync_NearbyPointWithSameRadius_ReusesDiscovery(double latOffset, int radius, int expectedAttempts)
    {
        // Arrange: 0.0012 degrees is about 133 m, 0.002 about 222 m.
        Scenario scenario = new();
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Act
        await scenario.Metadata.GetNearbyLinesAsync(new GeoPoint(Home.Lat + latOffset, Home.Lon), radius);

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(23.9, 1)]
    [DataRow(24.0, 2)]
    public async Task GetNearbyLinesAsync_Lifetime_RefetchesOnlyAfterADay(double hours, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Act
        scenario.Clock.Advance(TimeSpan.FromHours(hours));
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(0.001, 0.0, 1)]
    [DataRow(0.0, 0.0003, 1)]
    [DataRow(0.0, 0.001, 2)]
    [DataRow(0.002, 0.0, 2)]
    public async Task PlanTripAsync_SameTripFromAboutTheSamePlace_ReusesThePlan(double originOffset, double destinationOffset, int expectedAttempts)
    {
        // Arrange: 0.001 degrees of latitude is about 111 m; 0.0003 about 33 m.
        Scenario scenario = new();
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        await scenario.Metadata.PlanTripAsync(new GeoPoint(Home.Lat + originOffset, Home.Lon), new GeoPoint(Work.Lat + destinationOffset, Work.Lon));

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(2.9, 1)]
    [DataRow(3.0, 2)]
    public async Task PlanTripAsync_Lifetime_RefetchesAfterThreeHours(double hours, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        scenario.Clock.Advance(TimeSpan.FromHours(hours));
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow("Main St", 1)]
    [DataRow("  main   st ", 1)]
    [DataRow("Main Ave", 2)]
    public async Task SearchStopsAsync_SameQueryInTheSameArea_ReusesTheResults(string query, int expectedAttempts)
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Metadata.SearchStopsAsync(Home, "Main St");

        // Act
        await scenario.Metadata.SearchStopsAsync(new GeoPoint(Home.Lat + 0.01, Home.Lon), query);

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(0.03, 2)]
    [DataRow(0.06, 4)]
    public async Task GetRoutesNearAsync_SameArea_ReusesTheRouteList(double latOffset, int expectedAttempts)
    {
        // Arrange: networks, then routes: two requests per load. 0.03 degrees is about 3.3 km.
        Scenario scenario = new();
        await scenario.Metadata.GetRoutesNearAsync(Home);

        // Act
        await scenario.Metadata.GetRoutesNearAsync(new GeoPoint(Home.Lat + latOffset, Home.Lon));

        // Assert
        Assert.AreEqual(expectedAttempts, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetNearbyLinesAsync_FailedLoad_IsNotCachedAndCanBeRetried()
    {
        // Arrange
        Scenario scenario = new();
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.Metadata.GetNearbyLinesAsync(Home, 1000));

        // Act
        scenario.Handler.RespondAsync = scenario.Respond;
        NearbyLines lines = await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, lines.Lines);
    }

    [TestMethod]
    public async Task GetPlanAsync_OneWaiterCancels_OthersStillGetTheSharedResult()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken physical = default;
        scenario.Handler.RespondAsync = (_, token) =>
        {
            physical = token;
            return pending.Task.WaitAsync(token);
        };
        using CancellationTokenSource cancellation = new();
        Task<TripPlan> abandoned = scenario.Metadata.PlanTripAsync(Home, Work, cancellation.Token);
        Task<TripPlan> kept = scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned);
        pending.SetResult(RequestTestHandler.Response(PlanBody()));
        TripPlan plan = await kept;

        // Assert
        Assert.IsFalse(physical.IsCancellationRequested);
        Assert.HasCount(1, plan.Options);
    }

    [TestMethod]
    public async Task GetPlanAsync_EveryWaiterCancels_CancelsTheRequestAndAllowsANewOne()
    {
        // Arrange
        Scenario scenario = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Diagnostics.Observed += (_, o) =>
        {
            if (o.Outcome == TransitRequestOutcome.Canceled)
            {
                canceled.TrySetResult();
            }
        };
        scenario.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        using CancellationTokenSource cancellation = new();
        Task<TripPlan> abandoned = scenario.Metadata.PlanTripAsync(Home, Work, cancellation.Token);

        // Act
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Handler.RespondAsync = scenario.Respond;
        TripPlan plan = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(1, plan.Options);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_StopNotSeenWithShapes_AsksForShapesOnceAWeek()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);
        await scenario.Metadata.GetDeparturesAsync(["S:1", "S:2"]);
        scenario.Clock.Advance(MetadataTransitService.ShapeLifetime);
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);

        // Assert
        CollectionAssert.AreEqual(new[] { "true", "false", "true", "true" },
            scenario.Handler.Requests.Select(r => RequestTestHandler.Query(r, "include_stops_and_shapes")).ToArray());
    }

    [TestMethod]
    public async Task GetDeparturesAsync_ShapesInResponse_AreCachedByDirectionAndAnnounced()
    {
        // Arrange
        Scenario scenario = new();
        int announced = 0;
        scenario.Metadata.PatternsChanged += (_, _) => ++announced;

        // Act
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);

        // Assert
        Assert.AreEqual(1, announced);
        Assert.HasCount(1, scenario.Metadata.GetCachedPatterns("R:1", "0"));
        Assert.IsEmpty(scenario.Metadata.GetCachedPatterns("R:1", "1"));
        Assert.HasCount(1, scenario.Metadata.GetCachedPatterns("r:1"));
    }

    [TestMethod]
    public async Task GetCachedPatterns_NewerShapeWithSameId_ReplacesTheOldOne()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Metadata.GetRouteDetailsAsync("R:1");

        // Act
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);

        // Assert: both responses carry itinerary "R:1:0:Downtown"; only one copy is kept.
        Assert.HasCount(1, scenario.Metadata.GetCachedPatterns("R:1", "0"));
    }

    [TestMethod]
    public async Task GetCachedPatterns_ShapesOlderThanAWeek_AreNotReturned()
    {
        // Arrange
        Scenario scenario = new();
        await scenario.Metadata.GetRouteDetailsAsync("R:1");

        // Act
        scenario.Clock.Advance(MetadataTransitService.ShapeLifetime);

        // Assert
        Assert.IsEmpty(scenario.Metadata.GetCachedPatterns("R:1"));
    }

    [TestMethod]
    public async Task GetVehiclesAsync_RepeatedCalls_AreNeverCachedHere()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Metadata.GetVehiclesAsync("R:1");
        await scenario.Metadata.GetVehiclesAsync("R:1");

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task Changed_SuccessfulLoad_IsRaisedForPersistence()
    {
        // Arrange
        Scenario scenario = new();
        int changed = 0;
        scenario.Metadata.Changed += (_, _) => ++changed;

        // Act
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.IsGreaterThanOrEqualTo(2, changed);
    }

    [TestMethod]
    public async Task Import_ExportedSnapshotAfterJsonRoundTrip_ServesDiscoveryWithoutRequests()
    {
        // Arrange
        Scenario first = new();
        await first.Metadata.GetNearbyLinesAsync(Home, 1000);
        await first.Metadata.PlanTripAsync(Home, Work);
        await first.Metadata.GetRouteDetailsAsync("R:1");
        await first.Metadata.GetRoutesNearAsync(Home);
        await first.Metadata.GetDeparturesAsync(["S:1"]);
        string json = JsonSerializer.Serialize(first.Metadata.Export(), TransitCacheJsonContext.Default.TransitCacheSnapshot);
        Scenario second = new();
        second.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        second.Metadata.Import(JsonSerializer.Deserialize(json, TransitCacheJsonContext.Default.TransitCacheSnapshot));
        NearbyLines nearby = await second.Metadata.GetNearbyLinesAsync(Home, 1000);
        TripPlan plan = await second.Metadata.PlanTripAsync(Home, Work);
        RouteDetails details = await second.Metadata.GetRouteDetailsAsync("R:1");
        IReadOnlyList<TransitRoute> routes = await second.Metadata.GetRoutesNearAsync(Home);
        await second.Metadata.GetDeparturesAsync(["S:1"]);

        // Assert: only the live departures were fetched, and without shapes.
        Assert.AreEqual(1, second.Handler.Attempts);
        Assert.AreEqual("false", RequestTestHandler.Query(second.Handler.Requests.Single(), "include_stops_and_shapes"));
        Assert.AreEqual("S:near", nearby.Lines.Single().Stop.Id);
        Assert.AreEqual(TimeSpan.FromMinutes(25), plan.Options.Single().RideToDestination);
        Assert.AreEqual("North", details.Itineraries.Single().Direction.Name);
        Assert.HasCount(1, routes);
        Assert.HasCount(1, second.Metadata.GetCachedPatterns("R:1", "0"));
        Assert.HasCount(3, second.Metadata.GetCachedPatterns("R:1", "0").Single().Points);
    }

    [TestMethod]
    public async Task Import_EntriesPastTheirLifetime_AreSkipped()
    {
        // Arrange
        Scenario first = new();
        await first.Metadata.PlanTripAsync(Home, Work);
        await first.Metadata.GetNearbyLinesAsync(Home, 1000);
        TransitCacheSnapshot snapshot = first.Metadata.Export();
        Scenario second = new();
        second.Clock.Advance(MetadataTransitService.PlanLifetime);

        // Act
        second.Metadata.Import(snapshot);
        await second.Metadata.PlanTripAsync(Home, Work);
        await second.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Assert: the plan expired after 3 hours; nearby lines last a day.
        Assert.AreEqual(1, second.Handler.Attempts);
        Assert.AreEqual("/v4/public/plan", second.Handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task Import_WrongVersionOrMalformedEntries_IsIgnoredSafely()
    {
        // Arrange
        Scenario scenario = new();
        TransitCacheSnapshot wrongVersion = new(99, [new CachedAreaRoutes(Home, scenario.Clock.GetUtcNow(), [new TransitRoute("R", "R", "R", null)])], [], [], [], [], []);
        TransitCacheSnapshot malformed = new(TransitCacheSnapshot.CurrentVersion,
            [new CachedAreaRoutes(Home, scenario.Clock.GetUtcNow(), [new TransitRoute(" ", " ", "R", null)])],
            [], [new CachedNearbyLines(Home, 1000, scenario.Clock.GetUtcNow(), [new NearbyLine(new TransitRoute("R", "R", "R", null), new TransitDirection("0", "N"), new TransitStop("S", "S", new GeoPoint(91, 0)), 10)])],
            [], [new CachedShape("x", "R", "0", "garbage", scenario.Clock.GetUtcNow())], []);

        // Act
        scenario.Metadata.Import(null);
        scenario.Metadata.Import(wrongVersion);
        scenario.Metadata.Import(malformed);
        await scenario.Metadata.GetRoutesNearAsync(Home);
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Assert
        Assert.AreEqual(3, scenario.Handler.Attempts);
        Assert.IsEmpty(scenario.Metadata.GetCachedPatterns("R"));
    }

    [TestMethod]
    public async Task Import_OlderSnapshot_DoesNotReplaceANewerLoad()
    {
        // Arrange
        Scenario first = new();
        await first.Metadata.GetNearbyLinesAsync(Home, 1000);
        TransitCacheSnapshot old = first.Metadata.Export();
        Scenario second = new();
        second.Handler.RespondAsync = (request, token) => Task.FromResult(RequestTestHandler.Response(NearbyBody("S:newer")));
        await second.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Act
        second.Metadata.Import(old);
        NearbyLines lines = await second.Metadata.GetNearbyLinesAsync(Home, 1000);

        // Assert
        Assert.AreEqual("S:newer", lines.Lines.Single().Stop.Id);
    }

    private static string Shape(int points = 3) =>
        GeoMath.EncodePolyline(Enumerable.Range(0, points).Select(i => new GeoPoint(43 + (i * 0.01), -88)));

    private static string NearbyBody(string stopId = "S:near") => $$"""
        {"nearby_routes":[{"global_route_id":"R:1","route_short_name":"1","merged_itineraries":[{"direction_id":0,
          "closest_stop":{"global_stop_id":"{{stopId}}","stop_name":"Near","stop_lat":43.0005,"stop_lon":-88.0,"distance":55},
          "itineraries":[{"direction_id":0,"headsign":"Downtown","internal_itinerary_id":"R:1:0:Downtown","shape":"{{Shape()}}"}]}]}]}
        """;

    private static string PlanBody()
    {
        long t0 = RequestTestClock.Start.ToUnixTimeSeconds();
        return $$"""
            {"results":[{"end_time":{{t0 + 1800}},"legs":[
              {"leg_mode":"walk","duration":300,"distance":300,"end_time":{{t0 + 300}}},
              {"leg_mode":"transit","end_time":{{t0 + 1800}},
               "departures":[{"departure_time":{{t0 + 300}},"plan_details":{"global_route_id":"R:1","stop_schedule_items":[{"global_stop_id":"S:board"}]} }],
               "routes":[{"global_route_id":"R:1","route_short_name":"1","itineraries":[{"direction_id":0,"headsign":"Downtown","internal_itinerary_id":"R:1:0:Downtown",
                 "stops":[{"global_stop_id":"S:board","stop_name":"Board","stop_lat":43.002,"stop_lon":-88.0}],"shape":"{{Shape()}}"}]}]}]}]}
            """;
    }

    private static string RouteDetailsBody(string routeId) => $$"""
        {"route":{"global_route_id":"{{routeId}}","route_short_name":"1"},
         "itineraries":[{"direction_id":0,"headsign":"North","canonical_itinerary":true,"internal_itinerary_id":"R:1:0:Downtown","shape":"{{Shape()}}",
           "stops":[{"global_stop_id":"S:1","stop_name":"One","stop_lat":43,"stop_lon":-88}]}]}
        """;

    private sealed class Scenario
    {
        public Scenario()
        {
            Diagnostics = new TransitRequestDiagnostics(Clock);
            Handler.RespondAsync = Respond;
            Metadata = new MetadataTransitService(Handler.CreateService(Diagnostics), Clock);
        }

        public RequestTestClock Clock { get; } = new();

        public RequestTestHandler Handler { get; } = new();

        public TransitRequestDiagnostics Diagnostics { get; }

        public MetadataTransitService Metadata { get; }

        public Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(RequestTestHandler.Response(request.RequestUri!.AbsolutePath switch
            {
                string p when p.EndsWith("nearby_routes", StringComparison.Ordinal) => NearbyBody(),
                string p when p.EndsWith("plan", StringComparison.Ordinal) => PlanBody(),
                string p when p.EndsWith("route_details", StringComparison.Ordinal) => RouteDetailsBody("R:1"),
                string p when p.EndsWith("available_networks", StringComparison.Ordinal) => """{"networks":[{"network_id":"N|Here"}]}""",
                string p when p.EndsWith("routes_for_networks", StringComparison.Ordinal) => """{"routes":[{"global_route_id":"R:1","route_short_name":"1"}]}""",
                string p when p.EndsWith("search_stops", StringComparison.Ordinal) => """{"results":[{"global_stop_id":"S:1","stop_name":"Main St","stop_lat":43,"stop_lon":-88}]}""",
                string p when p.EndsWith("vehicles", StringComparison.Ordinal) => """{"vehicles":[]}""",
                _ => RequestTestHandler.Departures(RequestTestHandler.StopIds(request)
                    .Select(id => new TestDeparture(id, "R:1", RequestTestClock.Start.AddMinutes(5), TripId: $"t-{id}", Shape: Shape()))
                    .ToArray()),
            }));
    }
}
