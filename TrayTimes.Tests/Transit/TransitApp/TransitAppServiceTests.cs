using System.Net;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit.TransitApp;

[TestClass]
public sealed class TransitAppServiceTests
{
    private static readonly DateTimeOffset Now = RequestTestClock.Start;

    [TestMethod]
    public async Task GetDeparturesAsync_AnyRequest_SendsKeyInHeaderNeverInUrl()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        await service.GetDeparturesAsync(["STM:1"]);

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.IsTrue(request.Headers.TryGetValues("apiKey", out IEnumerable<string>? keys));
        Assert.AreEqual("fixture-secret", keys.Single());
        Assert.DoesNotContain("fixture-secret", request.RequestUri!.ToString());
        Assert.AreEqual("/v4/public/stop_departures", request.RequestUri.AbsolutePath);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_Stops_AsksForEveryStopInOneRequestWithoutShapesByDefault()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        await service.GetDeparturesAsync(["STM:1", "STM:2", "STM:1"]);

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        CollectionAssert.AreEqual(new[] { "STM:1", "STM:2" }, RequestTestHandler.StopIds(request));
        Assert.AreEqual("false", RequestTestHandler.Query(request, "include_stops_and_shapes"));
        Assert.AreEqual("true", RequestTestHandler.Query(request, "exclude_terminal_arrivals"));
        Assert.AreEqual(TransitAppService.DeparturesPerDirection.ToString(System.Globalization.CultureInfo.InvariantCulture), RequestTestHandler.Query(request, "max_num_departures"));
    }

    [TestMethod]
    public async Task GetDeparturesAsync_RealTimeScheduledAndCanceled_MapsEachDepartureSoonestFirst()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
                new TestDeparture("STM:1", "STM:14", Now.AddMinutes(12), TripId: "late", Scheduled: Now.AddMinutes(9)),
                new TestDeparture("STM:1", "STM:14", Now.AddMinutes(5), DirectionId: "1", TripId: "timetable", RealTime: false, Headsign: "Uptown"),
                new TestDeparture("STM:1", "STM:30", Now.AddMinutes(8), TripId: "gone", Canceled: true)))),
        };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["STM:1"]);

        // Assert
        CollectionAssert.AreEqual(new[] { "timetable", "gone", "late" }, board.Arrivals.Select(a => a.TripId).ToArray());
        Arrival timetable = board.Arrivals[0];
        Assert.IsFalse(timetable.IsRealTime);
        Assert.AreEqual("1", timetable.DirectionId);
        Assert.AreEqual("Uptown", timetable.Destination);
        Assert.AreEqual(ArrivalStatus.Canceled, board.Arrivals[1].Status);
        Arrival late = board.Arrivals[2];
        Assert.AreEqual(Now.AddMinutes(12), late.PredictedTime);
        Assert.AreEqual(TimeSpan.FromMinutes(3), late.Delay);
        Assert.AreEqual("#112233", late.RouteColor);
        Assert.AreEqual("Bus", late.Mode);
        Assert.AreEqual("STM:14", late.RouteDisplayId);
        Assert.IsEmpty(board.Shapes);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_UnrequestedStopInResponse_IsDropped()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(["STM:1", "STM:2"]))) };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["STM:1"]);

        // Assert
        Assert.AreEqual("STM:1", board.Arrivals.Single().StopId);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_WithShapes_DecodesItineraryShapes()
    {
        // Arrange
        string shape = GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.01, -88)]);
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
                new TestDeparture("STM:1", "STM:14", Now.AddMinutes(5), TripId: "t", Shape: shape)))),
        };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["STM:1"], includeShapes: true);

        // Assert
        Assert.AreEqual("true", RequestTestHandler.Query(handler.Requests.Single(), "include_stops_and_shapes"));
        RoutePattern pattern = board.Shapes.Single();
        Assert.AreEqual("STM:14", pattern.RouteId);
        Assert.AreEqual("0", pattern.DirectionId);
        Assert.HasCount(2, pattern.Points);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_NoStopsOrTooMany_RejectsWithoutHttp()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard none = await service.GetDeparturesAsync([]);

        // Assert
        Assert.IsEmpty(none.Arrivals);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.GetDeparturesAsync(
            [.. Enumerable.Range(0, TransitAppService.MaxStopIdsPerRequest + 1).Select(i => $"s{i}")]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetDeparturesAsync([" "]));
        Assert.AreEqual(0, handler.Attempts);
    }

    [TestMethod]
    public async Task GetNearbyLinesAsync_Routes_ListsEachDirectionWithItsClosestStopAndShape()
    {
        // Arrange
        string shape = GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.01, -88)]);
        string body = $$"""
            {"nearby_routes":[{"global_route_id":"MCTS:14","route_short_name":"14","route_long_name":"Forest Home","route_color":"FF0000","mode_name":"Bus",
              "merged_itineraries":[
                {"direction_id":0,"closest_stop":{"global_stop_id":"MCTS:a","stop_name":"Oak & 1st","stop_lat":43.001,"stop_lon":-88.0,"distance":111},
                 "itineraries":[{"direction_id":0,"headsign":"Downtown","direction_headsign":"Northbound","shape":"{{shape}}","internal_itinerary_id":"i0"}]},
                {"direction_id":1,"closest_stop":{"global_stop_id":"MCTS:b","stop_name":"Oak & 1st","stop_lat":43.0011,"stop_lon":-87.9999,"distance":120},
                 "itineraries":[{"direction_id":1,"headsign":"Airport"}]}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        NearbyLines nearby = await service.GetNearbyLinesAsync(new GeoPoint(43, -88), 1000);

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual("/v4/public/nearby_routes", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("1000", RequestTestHandler.Query(request, "max_distance"));
        Assert.AreEqual("true", RequestTestHandler.Query(request, "include_stops_and_shapes"));
        Assert.HasCount(2, nearby.Lines);
        NearbyLine north = nearby.Lines[0];
        Assert.AreEqual("MCTS:a", north.Stop.Id);
        Assert.AreEqual("Northbound", north.Direction.Name);
        Assert.AreEqual("0", north.Direction.Id);
        Assert.AreEqual(111, north.Meters);
        Assert.AreEqual("#ff0000", north.Route.Color);
        Assert.AreEqual("Airport", nearby.Lines[1].Direction.Name);
        Assert.AreEqual("i0", nearby.Shapes.Single().Id);
    }

    [TestMethod]
    public async Task GetNearbyLinesAsync_RadiusBeyondApiMaximum_IsClamped()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"nearby_routes":[]}""")) };
        TransitAppService service = handler.CreateService();

        // Act
        await service.GetNearbyLinesAsync(new GeoPoint(43, -88), 5000);

        // Assert
        Assert.AreEqual("1500", RequestTestHandler.Query(handler.Requests.Single(), "max_distance"));
    }

    [TestMethod]
    public async Task PlanTripAsync_TransferTrip_BoardsAtFirstRideWithWalkAndArrival()
    {
        // Arrange: walk 5 min, ride line 2 (two departure options), transfer, ride, walk to the end.
        long t0 = Now.ToUnixTimeSeconds();
        string body = $$"""
            {"results":[{"end_time":{{t0 + 2400}},"legs":[
              {"leg_mode":"walk","duration":300,"distance":350,"end_time":{{t0 + 300}},"polyline":"{{GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.002, -88)])}}"},
              {"leg_mode":"transit","end_time":{{t0 + 1200}},
               "departures":[
                 {"departure_time":{{t0 + 300}},"plan_details":{"global_route_id":"STM:2","internal_itinerary_id":"i2",
                   "arrival_schedule_item":{"departure_time":{{t0 + 1200}} },
                   "stop_schedule_items":[{"global_stop_id":"STM:board","departure_time":{{t0 + 300}} }]} },
                 {"departure_time":{{t0 + 600}},"plan_details":{"global_route_id":"STM:2","internal_itinerary_id":"i2",
                   "arrival_schedule_item":{"departure_time":{{t0 + 1500}} } } }],
               "routes":[{"global_route_id":"STM:2","route_short_name":"2","route_long_name":"Orange","route_color":"f37d00","mode_name":"Métro",
                 "itineraries":[{"direction_id":0,"headsign":"Côte-Vertu","internal_itinerary_id":"i2",
                   "shape":"{{GeoMath.EncodePolyline([new GeoPoint(43.002, -88), new GeoPoint(43.05, -88)])}}",
                   "stops":[{"global_stop_id":"STM:board","stop_name":"Berri-UQAM","stop_lat":43.002,"stop_lon":-88.0}]}]}]},
              {"leg_mode":"walk","duration":120,"end_time":{{t0 + 1320}}},
              {"leg_mode":"transit","end_time":{{t0 + 2200}},"routes":[{"global_route_id":"STM:51","route_short_name":"51"}]},
              {"leg_mode":"walk","duration":200,"end_time":{{t0 + 2400}}}]},
              {"end_time":{{t0 + 1800}},"legs":[{"leg_mode":"walk","duration":1800}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual("/v4/public/plan", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("transit", RequestTestHandler.Query(request, "mode"));
        TripOption option = plan.Options.Single();
        Assert.AreEqual("STM:board", option.BoardAt.Id);
        Assert.AreEqual("Berri-UQAM", option.BoardAt.Name);
        Assert.AreEqual("STM:2", option.Route.Id);
        Assert.AreEqual("Métro", option.Route.Mode);
        Assert.AreEqual("Côte-Vertu", option.Direction.Name);
        Assert.AreEqual(TimeSpan.FromMinutes(5), option.Walk);
        Assert.AreEqual(350, option.WalkMeters);
        Assert.AreEqual(Now.AddSeconds(300), option.Departs);
        Assert.AreEqual(Now.AddSeconds(2400), option.Arrives);
        Assert.AreEqual(1, option.Transfers);
        Assert.AreEqual("STM:2", plan.Shapes.Single().RouteId);
    }

    [TestMethod]
    public async Task PlanTripAsync_AbridgedStopList_FindsBoardingStopFromTheWalkThere()
    {
        // Arrange: the itinerary's stops don't include where the leg boards.
        long t0 = Now.ToUnixTimeSeconds();
        string body = $$"""
            {"results":[{"end_time":{{t0 + 900}},"legs":[
              {"leg_mode":"walk","duration":60,"distance":80,"end_time":{{t0 + 60}},"polyline":"{{GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.0007, -88)])}}"},
              {"leg_mode":"transit","end_time":{{t0 + 900}},
               "departures":[{"departure_time":{{t0 + 60}},"plan_details":{"stop_schedule_items":[{"global_stop_id":"X:9"}]} }],
               "routes":[{"global_route_id":"X:1","route_short_name":"1","itineraries":[{"direction_id":1,"headsign":"East","stops":[]}]}]}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        TripOption option = plan.Options.Single();
        Assert.AreEqual("X:9", option.BoardAt.Id);
        Assert.AreEqual(43.0007, option.BoardAt.Location.Lat, 1e-6);
        Assert.AreEqual(string.Empty, option.BoardAt.Name);
    }

    [TestMethod]
    public async Task SearchStopsAsync_Query_SendsAreaAndReturnsDistinctMatches()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""
                {"results":[{"global_stop_id":"A:1","stop_name":"Main St","stop_lat":43,"stop_lon":-88},
                            {"global_stop_id":"A:1","stop_name":"Main St","stop_lat":43,"stop_lon":-88},
                            {"global_stop_id":"A:2","stop_name":"Main & 2nd","stop_lat":43.01,"stop_lon":-88}]}
                """)),
        };
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<TransitStop> stops = await service.SearchStopsAsync(new GeoPoint(43, -88), "  main ");

        // Assert
        Assert.AreEqual("main", RequestTestHandler.Query(handler.Requests.Single(), "query"));
        CollectionAssert.AreEqual(new[] { "A:1", "A:2" }, stops.Select(s => s.Id).ToArray());
    }

    [TestMethod]
    public async Task SearchStopsAsync_BlankQuery_ReturnsNothingWithoutHttp()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<TransitStop> stops = await service.SearchStopsAsync(new GeoPoint(43, -88), "   ");

        // Assert
        Assert.IsEmpty(stops);
        Assert.AreEqual(0, handler.Attempts);
    }

    [TestMethod]
    public async Task GetRoutesNearAsync_Networks_SkipsBetaNetworksAndListsTheirRoutes()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("available_networks", StringComparison.Ordinal)
                ? """{"networks":[{"network_id":"MCTS|Milwaukee"},{"network_id":"Preview|Milwaukee","network_in_beta":true}]}"""
                : """{"routes":[{"global_route_id":"MCTS:14","route_short_name":"14","route_long_name":"Forest Home"},{"global_route_id":"MCTS:GRE","route_long_name":"Green Line"}]}""")),
        };
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesNearAsync(new GeoPoint(43, -88));

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual("false", RequestTestHandler.Query(handler.Requests[0], "include_network_geometry"));
        Assert.AreEqual("MCTS|Milwaukee", RequestTestHandler.Query(handler.Requests[1], "network_ids"));
        Assert.AreEqual("14", routes[0].DisplayId);
        Assert.AreEqual("Green Line", routes[1].DisplayId);
    }

    [TestMethod]
    public async Task GetRoutesNearAsync_NoNetworks_MakesOnlyOneRequest()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"networks":[]}""")) };
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesNearAsync(new GeoPoint(0, 0));

        // Assert
        Assert.IsEmpty(routes);
        Assert.AreEqual(1, handler.Attempts);
    }

    [TestMethod]
    public async Task GetRouteDetailsAsync_Itineraries_KeepsCanonicalActiveOnesWithTheirStopsAndShapes()
    {
        // Arrange
        string shape = GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.01, -88)]);
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response($$"""
                {"route":{"global_route_id":"R:1","route_short_name":"1"},
                 "itineraries":[
                   {"direction_id":0,"headsign":"North","canonical_itinerary":true,"shape":"{{shape}}","stops":[{"global_stop_id":"S:1","stop_name":"One","stop_lat":43,"stop_lon":-88},{"global_stop_id":"S:2","stop_name":"Two","stop_lat":43.01,"stop_lon":-88}]},
                   {"direction_id":1,"headsign":"South","canonical_itinerary":true,"stops":[{"global_stop_id":"S:2","stop_name":"Two","stop_lat":43.01,"stop_lon":-88}]},
                   {"direction_id":0,"headsign":"North short","canonical_itinerary":false,"stops":[]},
                   {"direction_id":1,"headsign":"Old","canonical_itinerary":true,"is_active":false,"stops":[]}]}
                """)),
        };
        TransitAppService service = handler.CreateService();

        // Act
        RouteDetails details = await service.GetRouteDetailsAsync("R:1");

        // Assert
        CollectionAssert.AreEqual(new[] { "North", "South" }, details.Itineraries.Select(i => i.Direction.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "S:1", "S:2" }, details.Itineraries[0].Stops.Select(s => s.Id).ToArray());
        Assert.AreEqual("0", details.Shapes.Single().DirectionId);
    }

    [TestMethod]
    public async Task GetVehiclesAsync_Vehicles_MapsTripLabelAndOccupancyAndSkipsBadPositions()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""
                {"vehicles":[{"vehicle_id":"STM:980:1|29088","latitude":45.51,"longitude":-73.68,"direction_id":0,"vehicle_label":"29-088","occupancy_status":3,"rt_trip_id":"292679910"},
                             {"vehicle_id":"nowhere","latitude":null,"longitude":-73.68}]}
                """)),
        };
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<Vehicle> vehicles = await service.GetVehiclesAsync("STM:980", "0");

        // Assert
        Assert.AreEqual("/v4/vehicles", handler.Requests.Single().RequestUri!.AbsolutePath);
        Vehicle vehicle = vehicles.Single();
        Assert.AreEqual("292679910", vehicle.TripId);
        Assert.AreEqual("29-088", vehicle.Label);
        Assert.AreEqual(VehicleOccupancy.Crowded, vehicle.Occupancy);
        Assert.AreEqual(-1, vehicle.Heading);
        Assert.AreEqual("STM:980", vehicle.RouteId);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, TransitApiFailureKind.Authentication)]
    [DataRow(HttpStatusCode.Forbidden, TransitApiFailureKind.Authentication)]
    [DataRow(HttpStatusCode.BadRequest, TransitApiFailureKind.Other)]
    [DataRow(HttpStatusCode.NotFound, TransitApiFailureKind.Other)]
    public async Task GetDeparturesAsync_RejectedRequest_ClassifiesTheFailure(HttpStatusCode status, TransitApiFailureKind kind)
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("{}", status)) };
        TransitAppService service = handler.CreateService();

        // Act
        TransitApiException failure = await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["S:1"]));

        // Assert
        Assert.AreEqual(kind, failure.Kind);
        Assert.AreEqual(TransitRequestOutcome.ApiFailure, service.Diagnostics.Snapshot.Last().Outcome);
        Assert.AreEqual((int)status, service.Diagnostics.Snapshot.Last().HttpStatus);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_TooManyRequests_ThrowsRateLimitWithRetryAfter()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) =>
            {
                HttpResponseMessage response = RequestTestHandler.Response("{}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
                return Task.FromResult(response);
            },
        };
        TransitAppService service = handler.CreateService();

        // Act
        TransitRateLimitException limited = await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => service.GetDeparturesAsync(["S:1"]));

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(42), limited.RetryAfter);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_UnreadableBody_ThrowsQueryFailure()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("<html>")) };
        TransitAppService service = handler.CreateService();

        // Act
        TransitApiException failure = await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["S:1"]));

        // Assert
        Assert.AreEqual(TransitApiFailureKind.Other, failure.Kind);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_NoKey_ThrowsAuthenticationWithoutHttp()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = new(" ", http: new HttpClient(handler));

        // Act
        TransitApiException failure = await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["S:1"]));

        // Assert
        Assert.IsFalse(service.IsConfigured);
        Assert.AreEqual(TransitApiFailureKind.Authentication, failure.Kind);
        Assert.AreEqual(0, handler.Attempts);
    }

    [TestMethod]
    public async Task EndpointCalled_EachRequest_ReportsTheEndpointName()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"vehicles":[]}""")) };
        TransitAppService service = handler.CreateService();
        List<string> endpoints = [];
        service.EndpointCalled += (_, endpoint) => endpoints.Add(endpoint);

        // Act
        await service.GetVehiclesAsync("R:1");

        // Assert
        CollectionAssert.AreEqual(new[] { "vehicles" }, endpoints);
    }
}
