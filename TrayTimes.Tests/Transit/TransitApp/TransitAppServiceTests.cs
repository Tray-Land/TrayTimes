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
    public async Task GetDeparturesAsync_TripSearchKey_IsTheTripIdUntilARealTimeOneAndAlwaysTheScheduleTripId()
    {
        // Arrange
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
                new TestDeparture("STM:1", "STM:14", Now.AddMinutes(5), RealTime: false, SearchKey: "key-1"),
                new TestDeparture("STM:1", "STM:14", Now.AddMinutes(9), TripId: "rt-2", SearchKey: "key-2")))),
        };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["STM:1"]);

        // Assert
        CollectionAssert.AreEqual(new[] { "key-1", "rt-2" }, board.Arrivals.Select(a => a.TripId).ToArray());
        CollectionAssert.AreEqual(new[] { "key-1", "key-2" }, board.Arrivals.Select(a => a.ScheduleTripId).ToArray());
        Assert.IsTrue(board.Arrivals[1].IsTrip("key-2"));
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
    public async Task GetDeparturesAsync_TrainRoute_GivesEachDepartureTheRoutesVehicleKind()
    {
        // Arrange
        long t0 = Now.ToUnixTimeSeconds();
        string body = $$"""
            {"route_departures":[{"global_route_id":"CTA:5673","route_short_name":"Brown","route_long_name":"Brown Line","mode_name":"'L'","route_type":1,"global_stop_id":"CTA:1",
              "merged_itineraries":[{"direction_id":0,"itineraries":[{"direction_id":0,"headsign":"Kimball","internal_itinerary_id":"i0"}],
                "schedule_items":[{"departure_time":{{t0 + 300}},"is_real_time":true,"rt_trip_id":"t1","internal_itinerary_id":"i0"}]}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["CTA:1"]);

        // Assert
        Assert.AreEqual(VehicleKind.Subway, board.Arrivals.Single().VehicleKind);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_RouteAlerts_MapsEachListedRoutesAlertsByRouteId()
    {
        // Arrange: shaped like a live response, with an effect the spec doesn't list and the same route at two stops.
        long t0 = Now.ToUnixTimeSeconds();
        string detour = $$"""
            {"title":"Temporary Reroute","description":"#9 buses will operate via Ashland.","effect":"DETOUR","cause":"CONSTRUCTION","severity":"Warning",
             "created_at":{{t0}},"informed_entities":[{"global_route_id":"CTA:9"}],"active_periods":[{"start":{{t0 - 3600}},"end":null}]}
            """;
        string body = $$"""
            {"route_departures":[
              {"global_route_id":"CTA:9","route_short_name":"9","global_stop_id":"CTA:1","merged_itineraries":[],"alerts":[{{detour}},
                {"description":"The elevator at Chicago is out of service.","effect":"ACCESSIBILITY_ISSUE","cause":"TECHNICAL_PROBLEM","severity":"Info",
                 "created_at":{{t0}},"informed_entities":[],"active_periods":[{"start":null,"end":{{t0 + 7200}}}]},
                {"title":" ","description":"","effect":"SOMETHING_NEW","cause":"","severity":"Severe","created_at":{{t0}},"informed_entities":[]}]},
              {"global_route_id":"CTA:9","route_short_name":"9","global_stop_id":"CTA:2","merged_itineraries":[],"alerts":[{{detour}}]},
              {"global_route_id":"CTA:49","route_short_name":"49","global_stop_id":"CTA:1","merged_itineraries":[],"alerts":[]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["CTA:1", "CTA:2"]);

        // Assert
        Assert.IsEmpty(board.Alerts["CTA:49"]);
        IReadOnlyList<ServiceAlert> alerts = board.Alerts["CTA:9"];
        Assert.HasCount(2, alerts);
        Assert.AreEqual(new ServiceAlert("Temporary Reroute", "#9 buses will operate via Ashland.", AlertSeverity.Warning, AlertEffect.Detour,
            AlertCause.Construction, alerts[0].Periods), alerts[0]);
        Assert.AreEqual(new AlertPeriod(Now.AddHours(-1), null), alerts[0].Periods.Single());
        ServiceAlert elevator = alerts[1];
        Assert.AreEqual(string.Empty, elevator.Title);
        Assert.AreEqual(AlertEffect.AccessibilityIssue, elevator.Effect);
        Assert.AreEqual(AlertCause.TechnicalProblem, elevator.Cause);
        Assert.AreEqual(AlertSeverity.Info, elevator.Severity);
        Assert.AreEqual(new AlertPeriod(null, Now.AddHours(2)), elevator.Periods.Single());
    }

    [TestMethod]
    public async Task GetDeparturesAsync_UnknownAlertNames_ReadAsUnknown()
    {
        // Arrange
        string body = """
            {"route_departures":[{"global_route_id":"R:1","global_stop_id":"S:1","merged_itineraries":[],
              "alerts":[{"title":"Heads up","description":"Something.","effect":"SOMETHING_NEW","cause":"1","severity":"Extreme","created_at":0,"informed_entities":[]}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        DepartureBoard board = await service.GetDeparturesAsync(["S:1"]);

        // Assert
        ServiceAlert alert = board.Alerts["R:1"].Single();
        Assert.AreEqual(AlertEffect.UnknownEffect, alert.Effect);
        Assert.AreEqual(AlertCause.UnknownCause, alert.Cause);
        Assert.AreEqual(AlertSeverity.Unknown, alert.Severity);
        Assert.IsEmpty(alert.Periods);
    }

    [TestMethod]
    public async Task GetNearbyLinesAsync_Routes_ListsEachDirectionWithItsClosestStopAndShape()
    {
        // Arrange
        string shape = GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.01, -88)]);
        string body = $$"""
            {"nearby_routes":[{"global_route_id":"Metro:14","route_short_name":"14","route_long_name":"Forest Home","route_color":"FF0000","mode_name":"Bus",
              "merged_itineraries":[
                {"direction_id":0,"closest_stop":{"global_stop_id":"Metro:a","stop_name":"Oak & 1st","stop_lat":43.001,"stop_lon":-88.0,"distance":111},
                 "itineraries":[{"direction_id":0,"headsign":"Downtown","direction_headsign":"Northbound","shape":"{{shape}}","internal_itinerary_id":"i0"}]},
                {"direction_id":1,"closest_stop":{"global_stop_id":"Metro:b","stop_name":"Oak & 1st","stop_lat":43.0011,"stop_lon":-87.9999,"distance":120},
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
        Assert.AreEqual("Metro:a", north.Stop.Id);
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
    public async Task PlanTripAsync_StopDestination_UsesTheStopIdInsteadOfDestinationCoordinates()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        await service.PlanTripAsync(new GeoPoint(43, -88), new TripDestination(new GeoPoint(43.1, -88), "S:42&test", Persist: false));

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual("S%3A42%26test", request.RequestUri!.Query.Split("to_global_stop_ids=")[1].Split('&')[0]);
        Assert.IsNull(RequestTestHandler.Query(request, "to_lat"));
        Assert.IsNull(RequestTestHandler.Query(request, "to_lon"));
        Assert.AreEqual("43", RequestTestHandler.Query(request, "from_lat"));
    }

    [TestMethod]
    [DataRow(TripTimeMode.LeaveAt, "leave_time", "arrival_time")]
    [DataRow(TripTimeMode.ArriveBy, "arrival_time", "leave_time")]
    public async Task PlanTripAsync_TimedStopTrip_SendsOnlyItsRoundedConstraint(TripTimeMode mode, string expected, string absent)
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();
        TripTime time = new(mode, new DateTimeOffset(2026, 10, 20, 9, 2, 0, TimeSpan.FromHours(-4)));

        // Act
        await service.PlanTripAsync(new GeoPoint(43, -88), new TripDestination(new GeoPoint(43.1, -88), "S:42"), time);

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual(time.At.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), RequestTestHandler.Query(request, expected));
        Assert.IsNull(RequestTestHandler.Query(request, absent));
        Assert.AreEqual("S:42", RequestTestHandler.Query(request, "to_global_stop_ids"));
        Assert.IsNull(RequestTestHandler.Query(request, "to_lat"));
    }

    [TestMethod]
    public async Task PlanTripAsync_Request_AsksForAWalkOnlyOptionAndRoutesAroundOutages()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitAppService service = handler.CreateService();

        // Act
        await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        HttpRequestMessage request = handler.Requests.Single();
        Assert.AreEqual("/v4/public/plan", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("transit", RequestTestHandler.Query(request, "mode"));
        Assert.AreEqual("3", RequestTestHandler.Query(request, "num_result"));
        Assert.AreEqual("true", RequestTestHandler.Query(request, "should_update_realtime"));
        Assert.AreEqual("true", RequestTestHandler.Query(request, "walk_fallback"));
        Assert.AreEqual("true", RequestTestHandler.Query(request, "consider_downtimes"));
        Assert.IsNull(RequestTestHandler.Query(request, "leave_time"));
        Assert.IsNull(RequestTestHandler.Query(request, "arrival_time"));
    }

    [TestMethod]
    public async Task PlanTripAsync_TransferTrip_MapsEveryLegWithItsStopsTimesAndTrips()
    {
        // Arrange
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(TransferPlanBody())) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        TripItinerary trip = plan.Itineraries[0];
        CollectionAssert.AreEqual(new[] { typeof(WalkLeg), typeof(RideLeg), typeof(WalkLeg), typeof(RideLeg), typeof(WalkLeg) }, trip.Legs.Select(l => l.GetType()).ToArray());
        Assert.AreEqual(Now, trip.Departs);
        Assert.AreEqual(Now.AddSeconds(2400), trip.Arrives);
        Assert.AreEqual(1, trip.Transfers);

        WalkLeg walk = (WalkLeg)trip.Legs[0];
        Assert.AreEqual(350, walk.Meters);
        Assert.AreEqual(TimeSpan.FromMinutes(5), walk.Duration);
        Assert.HasCount(2, walk.Path);

        RideLine first = ((RideLeg)trip.Legs[1]).Lines.Single();
        Assert.AreEqual("STM:2", first.Route.Id);
        Assert.AreEqual("Métro", first.Route.Mode);
        Assert.AreEqual(VehicleKind.Subway, first.Route.VehicleKind);
        Assert.AreEqual(new TransitDirection("0", "Côte-Vertu"), first.Direction);
        Assert.AreEqual("Berri-UQAM", first.BoardAt.Name);
        Assert.AreEqual("Lionel-Groulx", first.AlightAt!.Name);
        Assert.AreEqual(2, first.Stops);
        Assert.HasCount(2, first.Path);
        CollectionAssert.AreEqual(
            new[] { new PlannedDeparture(Now.AddSeconds(300), Now.AddSeconds(1200), true, "t-2a"), new PlannedDeparture(Now.AddSeconds(600), Now.AddSeconds(1500), false, "key-2b") },
            first.Departures.ToArray());

        RideLeg second = (RideLeg)trip.Legs[3];
        Assert.AreEqual(Now.AddSeconds(1500), second.Starts);
        RideLine fiftyOne = second.Lines.Single();
        Assert.AreEqual("STM:transfer", fiftyOne.BoardAt.Id);
        Assert.AreEqual("STM:end", fiftyOne.AlightAt!.Id);
        Assert.AreEqual(3, fiftyOne.Stops);
        Assert.AreEqual("t-51", fiftyOne.Departures.Single().TripId, "The canceled departure is left out.");
        Assert.AreEqual(VehicleKind.Bus, fiftyOne.Route.VehicleKind, "A route without a route_type is a bus.");
        CollectionAssert.AreEquivalent(new[] { "STM:2", "STM:51" }, plan.Shapes.Select(s => s.RouteId).ToArray());
    }

    [TestMethod]
    public async Task PlanTripAsync_WalkOnlyResult_MapsOneWalkLeg()
    {
        // Arrange
        long t0 = Now.ToUnixTimeSeconds();
        string body = $$"""
            {"results":[{"start_time":{{t0}},"end_time":{{t0 + 1500}},"legs":[
              {"leg_mode":"walk","start_time":{{t0}},"end_time":{{t0 + 1500}},"duration":1500,"distance":1900}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        TripItinerary walk = plan.Itineraries.Single();
        Assert.AreEqual(1900, ((WalkLeg)walk.Legs.Single()).Meters);
        Assert.AreEqual(Now.AddSeconds(1500), walk.Arrives);
        Assert.AreEqual(0, walk.Transfers);
    }

    [TestMethod]
    public async Task PlanTripAsync_LegOfAnotherMode_LeavesOutThatTrip()
    {
        // Arrange
        long t0 = Now.ToUnixTimeSeconds();
        string body = $$"""
            {"results":[{"start_time":{{t0}},"end_time":{{t0 + 600}},"legs":[{"leg_mode":"personal_bike","start_time":{{t0}},"end_time":{{t0 + 600}}}]}]}
            """;
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(body)) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(43, -88), new GeoPoint(43.1, -88));

        // Assert
        Assert.IsEmpty(plan.Itineraries);
    }

    [TestMethod]
    public async Task PlanTripAsync_AbridgedStopList_FindsBoardingStopFromTheWalkThere()
    {
        // Arrange: the itinerary's stops don't include where the leg boards, and nothing says where it ends.
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
        TripItinerary trip = plan.Itineraries.Single();
        Assert.AreEqual(Now, trip.Departs);
        RideLine line = ((RideLeg)trip.Legs[1]).Lines.Single();
        Assert.AreEqual("X:9", line.BoardAt.Id);
        Assert.AreEqual(43.0007, line.BoardAt.Location.Lat, 1e-6);
        Assert.AreEqual(string.Empty, line.BoardAt.Name);
        Assert.IsNull(line.AlightAt);
        Assert.AreEqual(0, line.Stops);
    }

    [TestMethod]
    public async Task PlanTripAsync_RecordedMetroTransfer_GetsOffAtTheLastScheduledStop()
    {
        // Arrange: a recorded Transit plan from Côte-Vertu to Pie-IX (Orange line, then Green).
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RecordedPlanBody())) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(45.5143, -73.6829), new GeoPoint(45.5537, -73.5516));

        // Assert
        TripItinerary trip = plan.Itineraries[0];
        Assert.HasCount(5, trip.Legs);
        Assert.AreEqual(1, trip.Transfers);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1792380294), trip.Arrives);
        Assert.IsLessThan(100, GeoMath.DistanceMeters(new GeoPoint(45.5143, -73.6829), ((WalkLeg)trip.Legs[0]).Path[0]));

        RideLeg orangeLeg = (RideLeg)trip.Legs[1];
        RideLine orange = orangeLeg.Lines.Single();
        Assert.AreEqual("2", orange.Route.DisplayId);
        Assert.AreEqual("Montmorency", orange.Direction.Name);
        Assert.AreEqual("Côte-Vertu", orange.BoardAt.Name);
        Assert.AreEqual("STM:104418", orange.AlightAt!.Id);
        Assert.AreEqual("Lionel-Groulx", orange.AlightAt.Name);
        Assert.AreEqual(10, orange.Stops);
        Assert.IsGreaterThan(1, orange.Path.Count);
        CollectionAssert.AreEqual(new[] { "299981452", "299981480", "299981548" }, orange.Departures.Select(d => d.TripId).ToArray());
        Assert.AreEqual(orangeLeg.Ends, orange.Departures[0].Alights);

        RideLine green = ((RideLeg)trip.Legs[3]).Lines.Single();
        Assert.AreEqual("STM:104363", green.BoardAt.Id);
        Assert.AreEqual("Pie-IX", green.AlightAt!.Name);
        Assert.AreEqual(13, green.Stops);
    }

    [TestMethod]
    public async Task PlanTripAsync_RecordedRideWithAlternativeBuses_KeepsEachLineAtItsOwnStop()
    {
        // Arrange: the second recorded trip starts on the 171 or the 121, which board at different stop IDs.
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RecordedPlanBody())) };
        TransitAppService service = handler.CreateService();

        // Act
        TripPlan plan = await service.PlanTripAsync(new GeoPoint(45.5143, -73.6829), new GeoPoint(45.5537, -73.5516));

        // Assert
        TripItinerary trip = plan.Itineraries[1];
        IReadOnlyList<RideLine> buses = ((RideLeg)trip.Legs[1]).Lines;
        CollectionAssert.AreEqual(new[] { "171", "121" }, buses.Select(l => l.Route.DisplayId).ToArray());
        CollectionAssert.AreEqual(new[] { "STM:88345", "STM:99332" }, buses.Select(l => l.BoardAt.Id).ToArray());
        Assert.HasCount(2, buses[0].Departures);
        Assert.AreEqual(4, buses[0].Stops);

        RideLine rem = ((RideLeg)trip.Legs[3]).Lines.Single();
        Assert.HasCount(3, rem.Departures, "Departures on two REM itineraries from the same stop join one line.");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1792380360), rem.Departures[1].Alights, "Alighting uses the arrival time, not the departure time.");
        Assert.AreEqual(2, trip.Transfers);
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
                ? """{"networks":[{"network_id":"Metro|Milwaukee"},{"network_id":"Preview|Milwaukee","network_in_beta":true}]}"""
                : """{"routes":[{"global_route_id":"Metro:14","route_short_name":"14","route_long_name":"Forest Home"},{"global_route_id":"Metro:GRE","route_long_name":"Green Line"}]}""")),
        };
        TransitAppService service = handler.CreateService();

        // Act
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesNearAsync(new GeoPoint(43, -88));

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual("false", RequestTestHandler.Query(handler.Requests[0], "include_network_geometry"));
        Assert.AreEqual("Metro|Milwaukee",RequestTestHandler.Query(handler.Requests[1], "network_ids"));
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

    /// <summary>
    /// Walk 5 min to Berri-UQAM, ride the 2 two stops (two departures), walk 2 min, ride the 51 three
    /// stops (its earlier departure canceled), then walk to the end; plus a walk-only result.
    /// </summary>
    private static string TransferPlanBody()
    {
        long t0 = Now.ToUnixTimeSeconds();
        return $$"""
            {"results":[
             {"start_time":{{t0}},"end_time":{{t0 + 2400}},"duration":2400,"legs":[
              {"leg_mode":"walk","start_time":{{t0}},"end_time":{{t0 + 300}},"duration":300,"distance":350,"polyline":"{{GeoMath.EncodePolyline([new GeoPoint(43, -88), new GeoPoint(43.002, -88)])}}"},
              {"leg_mode":"transit","start_time":{{t0 + 300}},"end_time":{{t0 + 1200}},"duration":900,
               "departures":[
                 {"departure_time":{{t0 + 300}},"is_real_time":true,"is_cancelled":false,"rt_trip_id":"t-2a","plan_details":{"global_route_id":"STM:2","internal_itinerary_id":"i2",
                   "arrival_schedule_item":{"arrival_time":{{t0 + 1200}},"departure_time":{{t0 + 1230}} },
                   "stop_schedule_items":[{"global_stop_id":"STM:board","departure_time":{{t0 + 300}} },{"global_stop_id":"STM:mid"},{"global_stop_id":"STM:change"}]} },
                 {"departure_time":{{t0 + 600}},"is_real_time":false,"trip_search_key":"key-2b","plan_details":{"global_route_id":"STM:2","internal_itinerary_id":"i2",
                   "arrival_schedule_item":{"arrival_time":{{t0 + 1500}} },
                   "stop_schedule_items":[{"global_stop_id":"STM:board"},{"global_stop_id":"STM:mid"},{"global_stop_id":"STM:change"}]} }],
               "routes":[{"global_route_id":"STM:2","route_short_name":"2","route_long_name":"Orange","route_color":"f37d00","mode_name":"Métro","route_type":1,
                 "itineraries":[{"direction_id":0,"headsign":"Côte-Vertu",
                   "plan_details":{"internal_itinerary_id":"i2","start_stop_offset":0,"end_stop_offset":2,"plan_shape":"{{GeoMath.EncodePolyline([new GeoPoint(43.002, -88), new GeoPoint(43.05, -88)])}}"},
                   "shape":"{{GeoMath.EncodePolyline([new GeoPoint(43.002, -88), new GeoPoint(43.05, -88), new GeoPoint(43.07, -88)])}}",
                   "stops":[{"global_stop_id":"STM:board","stop_name":"Berri-UQAM","stop_lat":43.002,"stop_lon":-88.0},
                            {"global_stop_id":"STM:mid","stop_name":"Sherbrooke","stop_lat":43.03,"stop_lon":-88.0},
                            {"global_stop_id":"STM:change","stop_name":"Lionel-Groulx","stop_lat":43.05,"stop_lon":-88.0}]}]}]},
              {"leg_mode":"walk","start_time":{{t0 + 1200}},"end_time":{{t0 + 1320}},"duration":120,"distance":90,"polyline":"{{GeoMath.EncodePolyline([new GeoPoint(43.05, -88), new GeoPoint(43.0505, -88)])}}"},
              {"leg_mode":"transit","start_time":{{t0 + 1500}},"end_time":{{t0 + 2200}},"duration":700,
               "departures":[
                 {"departure_time":{{t0 + 1440}},"is_cancelled":true,"rt_trip_id":"t-51-gone","plan_details":{"global_route_id":"STM:51","internal_itinerary_id":"i51"} },
                 {"departure_time":{{t0 + 1500}},"is_real_time":true,"rt_trip_id":"t-51","plan_details":{"global_route_id":"STM:51","internal_itinerary_id":"i51",
                   "arrival_schedule_item":{"arrival_time":{{t0 + 2200}} },
                   "stop_schedule_items":[{"global_stop_id":"STM:transfer"},{"global_stop_id":"STM:a"},{"global_stop_id":"STM:b"},{"global_stop_id":"STM:end"}]} }],
               "routes":[{"global_route_id":"STM:51","route_short_name":"51","route_long_name":"Édouard-Montpetit","mode_name":"Bus",
                 "itineraries":[{"direction_id":1,"headsign":"Est","plan_details":{"internal_itinerary_id":"i51","start_stop_offset":1,"end_stop_offset":4},
                   "shape":"{{GeoMath.EncodePolyline([new GeoPoint(43.0505, -88), new GeoPoint(43.09, -88)])}}",
                   "stops":[{"global_stop_id":"STM:before","stop_name":"Before","stop_lat":43.04,"stop_lon":-88.0},
                            {"global_stop_id":"STM:transfer","stop_name":"Lionel-Groulx","stop_lat":43.0505,"stop_lon":-88.0},
                            {"global_stop_id":"STM:a","stop_name":"A","stop_lat":43.06,"stop_lon":-88.0},
                            {"global_stop_id":"STM:b","stop_name":"B","stop_lat":43.07,"stop_lon":-88.0},
                            {"global_stop_id":"STM:end","stop_name":"End","stop_lat":43.09,"stop_lon":-88.0}]}]}]},
              {"leg_mode":"walk","start_time":{{t0 + 2200}},"end_time":{{t0 + 2400}},"duration":200,"distance":150}]},
             {"start_time":{{t0}},"end_time":{{t0 + 1800}},"duration":1800,"legs":[{"leg_mode":"walk","start_time":{{t0}},"end_time":{{t0 + 1800}},"duration":1800,"distance":2100}]}]}
            """;
    }

    /// <summary>Two trips from a recorded <c>plan</c> response, trimmed to the fields the client reads.</summary>
    private static string RecordedPlanBody() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Transit", "TransitApp", "Fixtures", "plan-transfer-montreal.json"));
}
