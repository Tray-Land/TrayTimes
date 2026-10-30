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
    public async Task PlanTripAsync_RoundedTimesAndModes_ReuseOnlyTheSameTimeAndKeepLeaveNowCached()
    {
        // Arrange
        Scenario scenario = new();
        TripDestination destination = new(Work);
        TripPlan live = await scenario.Metadata.PlanTripAsync(Home, destination);
        DateTimeOffset at = RequestTestClock.Start.AddDays(1);
        TripTime leave = new(TripTimeMode.LeaveAt, at.AddMinutes(1));

        // Act
        TripPlan future = await scenario.Metadata.PlanTripAsync(Home, destination, leave);
        TripPlan rounded = await scenario.Metadata.PlanTripAsync(Home, destination, new TripTime(TripTimeMode.LeaveAt, at.AddMinutes(4)));
        await scenario.Metadata.PlanTripAsync(Home, destination, new TripTime(TripTimeMode.LeaveAt, at.AddMinutes(6)));
        await scenario.Metadata.PlanTripAsync(Home, destination, new TripTime(TripTimeMode.ArriveBy, leave.At));
        TripPlan now = await scenario.Metadata.PlanTripAsync(Home, destination);

        // Assert
        Assert.AreSame(future, rounded);
        Assert.AreSame(live, now);
        Assert.AreEqual(4, scenario.Handler.Attempts);
        Assert.IsTrue(scenario.Metadata.IsCurrentPlan(Home, destination, leave, future));
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, destination, future));
    }

    [TestMethod]
    public async Task Import_TimedPlans_RoundTripsTimeWithoutCollidingWithLeaveNow()
    {
        // Arrange
        Scenario first = new(), second = new();
        TripDestination destination = new(Work, "S:42");
        TripTime time = new(TripTimeMode.ArriveBy, RequestTestClock.Start.AddDays(1));
        await first.Metadata.PlanTripAsync(Home, destination, time);
        string json = JsonSerializer.Serialize(first.Metadata.Export(), TransitCacheJsonContext.Default.TransitCacheSnapshot);
        second.Metadata.Import(JsonSerializer.Deserialize(json, TransitCacheJsonContext.Default.TransitCacheSnapshot));

        // Act
        TripPlan reused = await second.Metadata.PlanTripAsync(Home, destination, time);
        int afterImport = second.Handler.Attempts;
        await second.Metadata.PlanTripAsync(Home, destination);

        // Assert
        Assert.AreEqual(0, afterImport);
        Assert.AreEqual(1, second.Handler.Attempts);
        Assert.AreEqual(time, second.Metadata.Export().TripPlans.Single(p => p.Time.Mode == TripTimeMode.ArriveBy).Time);
        Assert.IsTrue(second.Metadata.IsCurrentPlan(Home, destination, time, reused));
    }

    [TestMethod]
    public async Task SaveDestinationPlan_TemporaryTimedPlans_PromotesEveryTimeWithoutExtendingAge()
    {
        // Arrange
        Scenario scenario = new();
        TripDestination temporary = new(Work, "S:searched", Persist: false);
        TripTime time = new(TripTimeMode.LeaveAt, RequestTestClock.Start.AddDays(1));
        TripPlan live = await scenario.Metadata.PlanTripAsync(Home, temporary);
        TripPlan future = await scenario.Metadata.PlanTripAsync(Home, temporary, time);
        Assert.IsEmpty(scenario.Metadata.Export().TripPlans);
        scenario.Clock.Advance(TimeSpan.FromHours(2));

        // Act
        scenario.Metadata.SaveDestinationPlan(Home, temporary);
        TripPlan saved = await scenario.Metadata.PlanTripAsync(Home, temporary with { Persist = true }, time);

        // Assert
        Assert.AreSame(future, saved);
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.HasCount(2, scenario.Metadata.Export().TripPlans);
        Assert.IsTrue(scenario.Metadata.Export().TripPlans.All(p => p.FetchedAt == RequestTestClock.Start));
        scenario.Clock.Advance(TimeSpan.FromHours(1));
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, temporary with { Persist = true }, time, future));
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, temporary with { Persist = true }, live));
    }

    [TestMethod]
    public async Task Export_TemporaryDestination_ReusesInMemoryButNeverExportsItsPlan()
    {
        // Arrange
        Scenario scenario = new();
        TripDestination destination = new(Work, "S:searched", Persist: false);
        TripPlan first = await scenario.Metadata.PlanTripAsync(Home, destination);

        // Act
        TripPlan reused = await scenario.Metadata.PlanTripAsync(Home, destination);
        TransitCacheSnapshot snapshot = scenario.Metadata.Export();

        // Assert
        Assert.AreSame(first, reused);
        Assert.IsTrue(scenario.Metadata.IsCurrentPlan(Home, destination, first));
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.IsEmpty(snapshot.TripPlans);
    }

    [TestMethod]
    public async Task PlanTripAsync_NearbyStopsAndCoordinates_DoNotShareDifferentDestinationKinds()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        await scenario.Metadata.PlanTripAsync(Home, Work);
        await scenario.Metadata.PlanTripAsync(Home, new TripDestination(Work, "S:1"));
        await scenario.Metadata.PlanTripAsync(Home, new TripDestination(Work, "S:2"));
        await scenario.Metadata.PlanTripAsync(Home, new TripDestination(Work, "S:1"));

        // Assert
        Assert.AreEqual(3, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task SaveDestinationPlan_ExplicitSave_PersistsSamePlanWithoutARequestOrExtendingLifetime()
    {
        // Arrange
        Scenario scenario = new();
        TripDestination temporary = new(Work, "S:searched", Persist: false);
        TripPlan plan = await scenario.Metadata.PlanTripAsync(Home, temporary);
        scenario.Clock.Advance(TimeSpan.FromHours(2));

        // Act
        scenario.Metadata.SaveDestinationPlan(Home, temporary);
        TripPlan saved = await scenario.Metadata.PlanTripAsync(Home, temporary with { Persist = true });
        TransitCacheSnapshot snapshot = scenario.Metadata.Export();
        scenario.Clock.Advance(TimeSpan.FromHours(1));

        // Assert
        Assert.AreSame(plan, saved);
        Assert.AreEqual(1, scenario.Handler.Attempts);
        Assert.AreEqual("S:searched", snapshot.TripPlans.Single().DestinationStopId);
        Assert.AreEqual(RequestTestClock.Start, snapshot.TripPlans.Single().FetchedAt);
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, temporary with { Persist = true }, saved));
    }

    [TestMethod]
    public async Task Import_SavedStopDestination_RoundTripsIdentityWithoutReusingItForCoordinates()
    {
        // Arrange
        Scenario first = new();
        TripDestination destination = new(Work, "S:42");
        await first.Metadata.PlanTripAsync(Home, destination);
        string json = JsonSerializer.Serialize(first.Metadata.Export(), TransitCacheJsonContext.Default.TransitCacheSnapshot);
        Scenario second = new();
        second.Metadata.Import(JsonSerializer.Deserialize(json, TransitCacheJsonContext.Default.TransitCacheSnapshot));

        // Act
        await second.Metadata.PlanTripAsync(Home, destination);
        int stopRequests = second.Handler.Attempts;
        await second.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(0, stopRequests);
        Assert.AreEqual(1, second.Handler.Attempts);
    }

    [TestMethod]
    public async Task ReplanTripAsync_TemporaryStopDestination_StaysMemoryOnlyAndUsesTheGuard()
    {
        // Arrange
        Scenario scenario = new();
        TripDestination destination = new(Work, "S:42", Persist: false);
        await scenario.Metadata.PlanTripAsync(Home, destination);

        // Act
        TripPlan replanned = await scenario.Metadata.ReplanTripAsync(Home, destination);
        TripPlan held = await scenario.Metadata.ReplanTripAsync(Home, destination);

        // Assert
        Assert.AreSame(replanned, held);
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.IsEmpty(scenario.Metadata.Export().TripPlans);
    }

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
    public async Task IsCurrentPlan_FreshPlanFromAboutTheSamePlace_IsTrueWithoutARequest()
    {
        // Arrange
        Scenario scenario = new();
        TripPlan plan = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        bool current = scenario.Metadata.IsCurrentPlan(new GeoPoint(Home.Lat + 0.0005, Home.Lon), Work, plan);

        // Assert
        Assert.IsTrue(current);
        Assert.AreEqual(1, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task IsCurrentPlan_AfterThePlanLifetime_IsFalse()
    {
        // Arrange
        Scenario scenario = new();
        TripPlan plan = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        scenario.Clock.Advance(MetadataTransitService.PlanLifetime);

        // Assert
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, Work, plan));
        Assert.AreEqual(1, scenario.Handler.Attempts);
    }

    [TestMethod]
    public async Task IsCurrentPlan_AfterAReplan_IsFalseForTheOldPlanOnly()
    {
        // Arrange
        Scenario scenario = new();
        TripPlan old = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        TripPlan replanned = await scenario.Metadata.ReplanTripAsync(Home, Work);

        // Assert
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, Work, old));
        Assert.IsTrue(scenario.Metadata.IsCurrentPlan(Home, Work, replanned));
    }

    [TestMethod]
    public async Task ReplanTripAsync_FreshCachedPlan_PlansAgainAndCachesTheNewPlan()
    {
        // Arrange
        Scenario scenario = new();
        TripPlan cached = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Act
        TripPlan replanned = await scenario.Metadata.ReplanTripAsync(Home, Work);
        TripPlan later = await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.AreNotSame(cached, replanned);
        Assert.AreSame(replanned, later);
    }

    [TestMethod]
    public async Task ForceReplanTripAsync_SharedGuardHeldByAnotherReplan_PlansAgainAndHoldsTheGuard()
    {
        // Arrange: a suggestion planned about this trip again a minute ago.
        Scenario scenario = new();
        await scenario.Metadata.ReplanTripAsync(Home, Work);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act
        TripPlan forced = await scenario.Metadata.ForceReplanTripAsync(Home, new TripDestination(Work), default);
        TripPlan held = await scenario.Metadata.ReplanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(2, scenario.Handler.Attempts);
        Assert.AreSame(forced, held);
    }

    [TestMethod]
    public async Task ForceReplanTripAsync_LeaveAtTime_PlansForThatTime()
    {
        // Arrange
        Scenario scenario = new();
        TripTime time = new(TripTimeMode.LeaveAt, scenario.Clock.GetUtcNow().AddHours(1));

        // Act
        TripPlan forced = await scenario.Metadata.ForceReplanTripAsync(Home, new TripDestination(Work), time);

        // Assert
        Assert.IsTrue(scenario.Metadata.IsCurrentPlan(Home, new TripDestination(Work), time, forced));
        Assert.IsFalse(scenario.Metadata.IsCurrentPlan(Home, Work, forced));
    }

    [TestMethod]
    public async Task ReplanTripAsync_TripStaysInfeasible_PlansAgainOncePerInterval()
    {
        // Arrange: every refresh finds the trip infeasible and asks again, from a drifting location fix.
        Scenario scenario = new();
        await scenario.Metadata.PlanTripAsync(Home, Work);
        await scenario.Metadata.ReplanTripAsync(Home, Work);

        // Act
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        TripPlan held = await scenario.Metadata.ReplanTripAsync(new GeoPoint(Home.Lat + 0.0005, Home.Lon), Work);
        scenario.Clock.Advance(TimeSpan.FromMinutes(9));
        await scenario.Metadata.ReplanTripAsync(Home, Work);
        int withinInterval = scenario.Handler.Attempts;
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        TripPlan replanned = await scenario.Metadata.ReplanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual(2, withinInterval);
        Assert.AreEqual(3, scenario.Handler.Attempts);
        Assert.AreNotSame(held, replanned);
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
        Assert.HasCount(1, plan.Itineraries);
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
        Assert.HasCount(1, plan.Itineraries);
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

        // Assert: only the live departures were fetched, and without shapes. Legs say what kind they
        // are, and paths are saved as encoded polylines, not point lists.
        Assert.Contains("\"Kind\":\"walk\"", json);
        Assert.Contains("\"Kind\":\"ride\"", json);
        Assert.DoesNotContain("\"Path\":[", json);
        Assert.AreEqual(1, second.Handler.Attempts);
        Assert.AreEqual("false", RequestTestHandler.Query(second.Handler.Requests.Single(), "include_stops_and_shapes"));
        Assert.AreEqual("S:near", nearby.Lines.Single().Stop.Id);
        TripItinerary trip = plan.Itineraries.Single();
        Assert.HasCount(3, ((WalkLeg)trip.Legs[0]).Path);
        RideLine line = ((RideLeg)trip.Legs[1]).Lines.Single();
        Assert.AreEqual("S:end", line.AlightAt!.Id);
        Assert.AreEqual(1, line.Stops);
        Assert.HasCount(3, line.Path);
        Assert.AreEqual(new PlannedDeparture(RequestTestClock.Start.AddMinutes(5), RequestTestClock.Start.AddMinutes(30), true, "trip-1"), line.Departures.Single());
        Assert.AreEqual(TimeSpan.FromMinutes(30), trip.Arrives - trip.Departs);
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
        DateTimeOffset now = scenario.Clock.GetUtcNow();
        TransitCacheSnapshot wrongVersion = new(99, [new CachedAreaRoutes(Home, now, [new TransitRoute("R", "R", "R", null)])], [], [], [], [], []);
        TransitCacheSnapshot malformed = new(TransitCacheSnapshot.CurrentVersion,
            [new CachedAreaRoutes(Home, now, [new TransitRoute(" ", " ", "R", null)])],
            [], [new CachedNearbyLines(Home, 1000, now, [new NearbyLine(new TransitRoute("R", "R", "R", null), new TransitDirection("0", "N"), new TransitStop("S", "S", new GeoPoint(91, 0)), 10)])],
            [new CachedTripPlan(Home, Work, now, [new TripItinerary([new RideLeg(now, now.AddMinutes(10), [])], now, now.AddMinutes(10))])],
            [new CachedShape("x", "R", "0", "garbage", now)], []);

        // Act
        scenario.Metadata.Import(null);
        scenario.Metadata.Import(wrongVersion);
        scenario.Metadata.Import(malformed);
        await scenario.Metadata.GetRoutesNearAsync(Home);
        await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert: a ride without lines can't be shown, so its plan is fetched again.
        Assert.AreEqual(4, scenario.Handler.Attempts);
        Assert.IsEmpty(scenario.Metadata.GetCachedPatterns("R"));
    }

    [TestMethod]
    public async Task Import_SnapshotFromAnOlderBuild_KeepsItsOtherCachesAndSkipsItsPlans()
    {
        // Arrange: an older build saved each plan as first-ride options instead of whole trips.
        string at = RequestTestClock.Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        string json = $$$"""
            {"Version":1,"AreaRoutes":[],"RouteDetails":[],
             "NearbyLines":[{"Point":{"Lat":43.0,"Lon":-88.0},"RadiusMeters":1000,"FetchedAt":"{{{at}}}","Lines":[
               {"Route":{"Id":"R:1","DisplayId":"1","Name":"One","Color":null,"Mode":null},"Direction":{"Id":"0","Name":"Downtown"},
                "Stop":{"Id":"S:near","Name":"Near","Location":{"Lat":43.0005,"Lon":-88.0}},"Meters":55}]}],
             "TripPlans":[{"From":{"Lat":43.0,"Lon":-88.0},"To":{"Lat":43.1,"Lon":-88.0},"FetchedAt":"{{{at}}}","Options":[
               {"BoardAt":{"Id":"S:board","Name":"Board","Location":{"Lat":43.002,"Lon":-88.0}},"Route":{"Id":"R:1","DisplayId":"1","Name":"One","Color":null,"Mode":null},
                "Direction":{"Id":"0","Name":"Downtown"},"WalkMeters":300,"Walk":"00:05:00","Departs":"{{{at}}}","Arrives":"{{{at}}}","Transfers":0}]}],
             "Shapes":[],"ShapeCoverage":[]}
            """;
        Scenario scenario = new();

        // Act
        scenario.Metadata.Import(JsonSerializer.Deserialize(json, TransitCacheJsonContext.Default.TransitCacheSnapshot));
        NearbyLines nearby = await scenario.Metadata.GetNearbyLinesAsync(Home, 1000);
        await scenario.Metadata.PlanTripAsync(Home, Work);

        // Assert
        Assert.AreEqual("S:near", nearby.Lines.Single().Stop.Id);
        Assert.AreEqual("/v4/public/plan", scenario.Handler.Requests.Single().RequestUri!.AbsolutePath);
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

    [TestMethod]
    public async Task GetDeparturesAsync_RouteAlerts_LeavesThemInAlertsAndAResponseWithoutThemClearsThem()
    {
        // Arrange
        Scenario scenario = new();
        string withAlert = """
            {"route_departures":[{"global_route_id":"R:1","global_stop_id":"S:1","merged_itineraries":[],
              "alerts":[{"title":"Detour","description":"Buses detour via Oak St.","effect":"DETOUR","cause":"CONSTRUCTION","severity":"Warning","created_at":0,"informed_entities":[]}]}]}
            """;
        scenario.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(withAlert));

        // Act
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);
        IReadOnlyList<ServiceAlert> recorded = scenario.Metadata.Alerts.For("R:1", RequestTestClock.Start);
        scenario.Handler.RespondAsync = scenario.Respond;
        await scenario.Metadata.GetDeparturesAsync(["S:1"]);

        // Assert
        Assert.AreEqual("Detour", recorded.Single().Title);
        Assert.IsEmpty(scenario.Metadata.Alerts.For("R:1", RequestTestClock.Start));
    }

    private static string Shape(int points = 3) =>
        GeoMath.EncodePolyline(Enumerable.Range(0, points).Select(i => new GeoPoint(43 + (i * 0.01), -88)));

    private static string NearbyBody(string stopId = "S:near") => $$"""
        {"nearby_routes":[{"global_route_id":"R:1","route_short_name":"1","merged_itineraries":[{"direction_id":0,
          "closest_stop":{"global_stop_id":"{{stopId}}","stop_name":"Near","stop_lat":43.0005,"stop_lon":-88.0,"distance":55},
          "itineraries":[{"direction_id":0,"headsign":"Downtown","internal_itinerary_id":"R:1:0:Downtown","shape":"{{Shape()}}"}]}]}]}
        """;

    /// <summary>Walk 5 minutes, then ride one stop for 25 minutes; the walk and the ride both have paths.</summary>
    private static string PlanBody()
    {
        long t0 = RequestTestClock.Start.ToUnixTimeSeconds();
        return $$"""
            {"results":[{"start_time":{{t0}},"end_time":{{t0 + 1800}},"legs":[
              {"leg_mode":"walk","start_time":{{t0}},"duration":300,"distance":300,"end_time":{{t0 + 300}},"polyline":"{{Shape()}}"},
              {"leg_mode":"transit","start_time":{{t0 + 300}},"end_time":{{t0 + 1800}},
               "departures":[{"departure_time":{{t0 + 300}},"is_real_time":true,"rt_trip_id":"trip-1",
                 "plan_details":{"global_route_id":"R:1","stop_schedule_items":[{"global_stop_id":"S:board"},{"global_stop_id":"S:end"}]} }],
               "routes":[{"global_route_id":"R:1","route_short_name":"1","itineraries":[{"direction_id":0,"headsign":"Downtown","internal_itinerary_id":"R:1:0:Downtown",
                 "plan_details":{"plan_shape":"{{Shape()}}"},
                 "stops":[{"global_stop_id":"S:board","stop_name":"Board","stop_lat":43.002,"stop_lon":-88.0},
                          {"global_stop_id":"S:end","stop_name":"End","stop_lat":43.03,"stop_lon":-88.0}],"shape":"{{Shape()}}"}]}]}]}]}
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
