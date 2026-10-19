using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class PlaceBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Origin = new(43.0, -88.0);
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", "#ff0000");
    private static readonly TransitRoute Thirty = new("R:30", "30", "Sherman", "#0000ff");
    private static readonly TransitDirection North = new("0", "Northbound");
    private static readonly TransitDirection South = new("1", "Southbound");

    [TestMethod]
    public void Nearby_LinesAtSeveralStops_GroupsByStopClosestFirst()
    {
        // Arrange
        TransitStop near = Stop("near", 0.001);
        TransitStop far = Stop("far", 0.004);
        NearbyLines lines = new([
            new NearbyLine(Fourteen, North, far, 444),
            new NearbyLine(Fourteen, South, near, 111),
            new NearbyLine(Thirty, North, near, 111),
        ], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Nearby(lines, Origin, maxStops: 8);

        // Assert
        CollectionAssert.AreEqual(new[] { "near", "far" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.AreEqual(111, board.Stops[0].Meters);
        CollectionAssert.AreEquivalent(new[] { "R:14", "R:30" }, board.Stops[0].Stop.Routes.Select(r => r.RouteId).ToArray());
        Assert.AreEqual("Southbound", board.Stops[0].Stop.Routes.Single(r => r.RouteId == "R:14").DirectionName);
        Assert.AreEqual("#ff0000", board.Stops[0].Stop.Routes.Single(r => r.RouteId == "R:14").RouteColor);
    }

    [TestMethod]
    public void Nearby_EachLineDirection_IsListedOnlyAtItsClosestStop()
    {
        // Arrange
        TransitStop near = Stop("near", 0.001);
        TransitStop far = Stop("far", 0.004);
        NearbyLines lines = new([new NearbyLine(Fourteen, North, far, 444), new NearbyLine(Fourteen, South, near, 111)], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Nearby(lines, Origin, maxStops: 8);

        // Assert
        Assert.Contains(new StopLine("far", "R:14", "0"), board.Lines);
        Assert.Contains(new StopLine("near", "R:14", "1"), board.Lines);
        Assert.DoesNotContain(new StopLine("near", "R:14", "0"), board.Lines);
    }

    [TestMethod]
    public void Nearby_MoreStopsThanMax_KeepsTheClosestAndOnlyTheirLines()
    {
        // Arrange
        NearbyLines lines = new([.. Enumerable.Range(1, 5).Select(i => new NearbyLine(Fourteen, North, Stop($"s{i}", i * 0.001), i * 111))], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Nearby(lines, Origin, maxStops: 2);

        // Assert
        CollectionAssert.AreEqual(new[] { "s1", "s2" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.HasCount(2, board.Lines);
    }

    [TestMethod]
    public void Nearby_NoLines_ReturnsNoStops()
    {
        // Act
        PlaceBoardResult board = PlaceBoard.Nearby(new NearbyLines([], []), Origin, maxStops: 8);

        // Assert
        Assert.IsEmpty(board.Stops);
        Assert.IsEmpty(board.Lines);
    }

    [TestMethod]
    public void Commute_SeveralOptions_ListsBoardingStopsBestTripFirstWithTheirWalk()
    {
        // Arrange: the 30 from "b" gets there sooner than the 14 from "a".
        TripPlan plan = new([
            Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 40, walkMinutes: 3),
            Trip("b", Thirty, North, departsIn: 8, arrivesIn: 30, walkMinutes: 6, transfers: 1),
        ], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(plan, maxStops: 8);

        // Assert
        CollectionAssert.AreEqual(new[] { "b", "a" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.AreEqual(360, board.Stops[0].WalkSeconds);
        Assert.AreEqual(6 * 80, board.Stops[0].WalkMeters);
        Assert.AreEqual(new TripTiming(TimeSpan.FromMinutes(22), 1), board.Trips![new StopLine("b", "R:30", "0")]);
        Assert.AreEqual(new TripTiming(TimeSpan.FromMinutes(35), 0), board.Trips[new StopLine("a", "R:14", "0")]);
    }

    [TestMethod]
    public void Commute_SameLineFromTheSameStopTwice_KeepsTheQuickerTrip()
    {
        // Arrange
        TripPlan plan = new([
            Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 45, walkMinutes: 3, transfers: 1),
            Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3),
        ], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(plan, maxStops: 8);

        // Assert
        Assert.HasCount(1, board.Stops);
        Assert.AreEqual(new TripTiming(TimeSpan.FromMinutes(30), 0), board.Trips!.Single().Value);
    }

    [TestMethod]
    public void Commute_TwoLinesAtOneStop_ListsTheStopOnceWithBoth()
    {
        // Arrange
        TripPlan plan = new([
            Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3),
            Trip("a", Thirty, South, departsIn: 7, arrivesIn: 37, walkMinutes: 3),
        ], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(plan, maxStops: 8);

        // Assert
        Assert.HasCount(1, board.Stops);
        Assert.HasCount(2, board.Stops[0].Stop.Routes);
        Assert.HasCount(2, board.Lines);
    }

    [TestMethod]
    public void Commute_RideWithAlternativeLines_ListsEachLineAtItsOwnStopShiftedByItsDeparture()
    {
        // Arrange: one trip whose first ride can be the 14 from "a" (planned: 8:05 to 8:20, then 10 more
        // minutes to the destination) or the 30 from "b", which leaves at 8:09 and gets off at 8:26.
        RideLeg ride = new(Now.AddMinutes(5), Now.AddMinutes(20),
        [
            Line("a", Fourteen, North, Now.AddMinutes(5), Now.AddMinutes(20)),
            Line("b", Thirty, North, Now.AddMinutes(9), Now.AddMinutes(26)),
        ]);
        TripItinerary trip = new([new WalkLeg(Now, Now.AddMinutes(5), 400, []), ride, new WalkLeg(Now.AddMinutes(20), Now.AddMinutes(30), 800, [])], Now, Now.AddMinutes(30));

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([trip], []), maxStops: 8);

        // Assert
        CollectionAssert.AreEqual(new[] { "a", "b" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.AreEqual(new TripTiming(TimeSpan.FromMinutes(25), 0), board.Trips![new StopLine("a", "R:14", "0")]);
        Assert.AreEqual(new TripTiming(TimeSpan.FromMinutes(27), 0), board.Trips[new StopLine("b", "R:30", "0")]);
        Assert.AreEqual(300, board.Stops[1].WalkSeconds);
    }

    [TestMethod]
    public void Commute_WalkOnlyTrip_IsLeftOut()
    {
        // Arrange
        TripItinerary walk = new([new WalkLeg(Now, Now.AddMinutes(18), 1400, [])], Now, Now.AddMinutes(18));

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([walk], []), maxStops: 8);

        // Assert
        Assert.IsEmpty(board.Stops);
        Assert.IsEmpty(board.Lines);
        Assert.IsEmpty(board.Trips!);
    }

    [TestMethod]
    public void Around_SavedStopInsideTheRadius_IsPinnedFirstAndSuppressesItsLineAtACloserStop()
    {
        // Arrange: the 14 north is saved at "fav", about 445 m away; its closest stop is "near", about 110 m away.
        TransitStop near = Stop("near", 0.001);
        NearbyLines lines = new([new NearbyLine(Fourteen, North, near, 111), new NearbyLine(Thirty, North, near, 111)], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Around(lines, [Saved("fav", 0.004, Fourteen, North)], Origin, maxStops: 8);

        // Assert
        CollectionAssert.AreEqual(new[] { "fav", "near" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.AreEqual(445, board.Stops[0].Meters, 1);
        Assert.AreEqual("Saved fav", board.Stops[0].Stop.Name);
        Assert.AreEqual("Northbound", board.Stops[0].Stop.Routes.Single().DirectionName);
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:14", "0")));
        Assert.IsFalse(board.Lists(new StopLine("near", "R:14", "0")));
        Assert.IsTrue(board.Lists(new StopLine("near", "R:30", "0")));
    }

    [TestMethod]
    public void Around_SavedStopOutsideTheRadius_IsNotShown()
    {
        // Arrange: about 1.1 km away.
        NearbyLines lines = new([new NearbyLine(Fourteen, North, Stop("near", 0.001), 111)], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Around(lines, [Saved("fav", 0.01, Fourteen, North)], Origin, maxStops: 8);

        // Assert
        CollectionAssert.AreEqual(new[] { "near" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.IsTrue(board.Lists(new StopLine("near", "R:14", "0")));
    }

    [TestMethod]
    public void Around_SavedStopForOneLine_ListsJustThatLine()
    {
        // Act
        PlaceBoardResult board = PlaceBoard.Around(new NearbyLines([], []), [Saved("fav", 0.002, Fourteen, North)], Origin, maxStops: 8);

        // Assert
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:14", "0")));
        Assert.IsFalse(board.Lists(new StopLine("fav", "R:14", "1")));
        Assert.IsFalse(board.Lists(new StopLine("fav", "R:30", "0")));
    }

    [TestMethod]
    public void Around_AllRoutesSavedStop_ListsEveryLine()
    {
        // Act
        PlaceBoardResult board = PlaceBoard.Around(new NearbyLines([], []), [Saved("fav", 0.002, Fourteen, North, allRoutes: true)], Origin, maxStops: 8);

        // Assert
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:14", "0")));
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:99", "1")));
        Assert.IsFalse(board.Lists(new StopLine("other", "R:99", "1")));
        Assert.AreEqual("R:14", board.Stops.Single().Stop.Routes.Single().RouteId);
    }

    [TestMethod]
    public void Around_NearbyLineWhoseClosestStopIsPinned_IsListedThere()
    {
        // Arrange: "fav" is saved for the 14, and it's also where the 30 is closest.
        TransitStop fav = Stop("fav", 0.002);
        NearbyLines lines = new([new NearbyLine(Thirty, North, fav, 222)], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Around(lines, [Saved("fav", 0.002, Fourteen, North)], Origin, maxStops: 8);

        // Assert
        Assert.HasCount(1, board.Stops);
        CollectionAssert.AreEquivalent(new[] { "R:14", "R:30" }, board.Stops[0].Stop.Routes.Select(r => r.RouteId).ToArray());
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:14", "0")));
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:30", "0")));
    }

    [TestMethod]
    public void Around_TwoLinesSavedAtOneStop_PinsTheStopOnceWithBoth()
    {
        // Act
        PlaceBoardResult board = PlaceBoard.Around(new NearbyLines([], []),
            [Saved("fav", 0.002, Fourteen, North), Saved("fav", 0.002, Thirty, South)], Origin, maxStops: 8);

        // Assert
        Assert.HasCount(1, board.Stops);
        Assert.HasCount(2, board.Stops[0].Stop.Routes);
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:14", "0")));
        Assert.IsTrue(board.Lists(new StopLine("fav", "R:30", "1")));
    }

    [TestMethod]
    public void Around_MoreStopsThanMax_KeepsThePinnedStopsFirstWithinTheTotal()
    {
        // Arrange: two saved stops, and five other lines at five stops.
        NearbyLines lines = new([.. Enumerable.Range(1, 5).Select(i => new NearbyLine(
            new TransitRoute($"R:{i}", $"{i}", $"Line {i}", null), North, Stop($"n{i}", i * 0.001), i * 111))], []);
        SavedStop[] saved = [Saved("far", 0.003, Fourteen, North), Saved("close", 0.002, Thirty, North)];

        // Act
        PlaceBoardResult board = PlaceBoard.Around(lines, saved, Origin, maxStops: 4);

        // Assert
        CollectionAssert.AreEqual(new[] { "close", "far", "n1", "n2" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.IsFalse(board.Lists(new StopLine("n3", "R:3", "0")));
    }

    [TestMethod]
    public void Saved_SeveralSavedStops_ListsEachOnceInSavedOrderWithItsLines()
    {
        // Arrange
        SavedStop[] saved = [Saved("b", 0.02, Fourteen, North), Saved("a", 0.03, Thirty, North, allRoutes: true), Saved("b", 0.02, Thirty, South)];

        // Act
        PlaceBoardResult board = PlaceBoard.Saved(saved);

        // Assert
        CollectionAssert.AreEqual(new[] { "b", "a" }, board.Stops.Select(s => s.Stop.Id).ToArray());
        Assert.IsTrue(board.Stops.All(s => s.Meters == 0));
        Assert.IsTrue(board.Lists(new StopLine("b", "R:14", "0")));
        Assert.IsTrue(board.Lists(new StopLine("b", "R:30", "1")));
        Assert.IsFalse(board.Lists(new StopLine("b", "R:30", "0")));
        Assert.IsTrue(board.Lists(new StopLine("a", "R:99", "1")));
    }

    [TestMethod]
    public void SavedFirst_WalkRankingMovedANearbyStopAhead_PutsTheSavedStopsBackFirstInTheirNewOrder()
    {
        // Arrange: walking ranked "n1" first, then the saved "far", "n2", and the saved "close".
        NearbyStop[] ranked = [Nearby("n1"), Nearby("far"), Nearby("n2"), Nearby("close")];
        SavedStop[] saved = [Saved("close", 0.002, Thirty, North), Saved("far", 0.003, Fourteen, North)];

        // Act
        IReadOnlyList<NearbyStop> stops = PlaceBoard.SavedFirst(ranked, saved, maxStops: 3);

        // Assert
        CollectionAssert.AreEqual(new[] { "far", "close", "n1" }, stops.Select(s => s.Stop.Id).ToArray());
    }

    [TestMethod]
    public void BoardingStopIds_TransferTripWithAlternativeLines_ListsEveryRidesBoardingStop()
    {
        // Arrange: walk, the 14 from "a" or the 30 from "b", walk, then the 30 south from "t".
        RideLeg first = new(Now.AddMinutes(5), Now.AddMinutes(20),
            [Line("a", Fourteen, North, Now.AddMinutes(5), Now.AddMinutes(20)), Line("b", Thirty, North, Now.AddMinutes(9), Now.AddMinutes(26))]);
        RideLeg second = new(Now.AddMinutes(30), Now.AddMinutes(40), [Line("t", Thirty, South, Now.AddMinutes(30), Now.AddMinutes(40))]);
        TripItinerary trip = new([new WalkLeg(Now, Now.AddMinutes(5), 400, []), first, new WalkLeg(Now.AddMinutes(20), Now.AddMinutes(25), 300, []), second],
            Now, Now.AddMinutes(40));

        // Act
        IReadOnlyList<string> stops = PlaceBoard.BoardingStopIds(new TripPlan([trip], []));

        // Assert
        CollectionAssert.AreEqual(new[] { "a", "b", "t" }, stops.ToArray());
    }

    [TestMethod]
    public void BoardingStopIds_MoreTripsThanTheOptions_WatchesOnlyTheTopTransitTrips()
    {
        // Arrange: a walk-only trip first, then four transit trips.
        TripItinerary walk = new([new WalkLeg(Now, Now.AddMinutes(18), 1400, [])], Now, Now.AddMinutes(18));
        TripPlan plan = new([walk, .. new[] { "a", "b", "c", "d" }.Select(id => Trip(id, Fourteen, North, departsIn: 5, arrivesIn: 30, walkMinutes: 3))], []);

        // Act
        IReadOnlyList<string> stops = PlaceBoard.BoardingStopIds(plan);

        // Assert
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, stops.ToArray());
    }

    [TestMethod]
    public void ArrivalAtDestination_DepartureOnAPlannedLine_AddsThatLinesRestOfTrip()
    {
        // Arrange: the planner's trip on the 14 takes 30 minutes from boarding.
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3)], []), maxStops: 8);
        Arrival later = new("a", "R:14", "14", "0", "Downtown", Now.AddMinutes(20));

        // Act
        DateTimeOffset? eta = PlaceBoard.ArrivalAtDestination(later, board.Trips);

        // Assert
        Assert.AreEqual(Now.AddMinutes(50), eta);
    }

    [TestMethod]
    public void ArrivalAtDestination_DepartureNotOnAPlannedLine_IsUnknown()
    {
        // Arrange
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([Trip("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3)], []), maxStops: 8);

        // Act & Assert
        Assert.IsNull(PlaceBoard.ArrivalAtDestination(new Arrival("a", "R:14", "14", "1", "Uptown", Now), board.Trips));
        Assert.IsNull(PlaceBoard.ArrivalAtDestination(new Arrival("b", "R:14", "14", "0", "Downtown", Now), board.Trips));
        Assert.IsNull(PlaceBoard.ArrivalAtDestination(new Arrival("a", "R:14", "14", "0", "Downtown", Now), null));
    }

    [TestMethod]
    public void StopLine_RouteAndDirectionInDifferentCase_AreEqualButStopIdsAreExact()
    {
        // Act & Assert
        Assert.AreEqual(new StopLine("S:1", "gre", "north"), new StopLine("S:1", "GRE", "NORTH"));
        Assert.AreEqual(new StopLine("S:1", "gre", "north").GetHashCode(), new StopLine("S:1", "GRE", "NORTH").GetHashCode());
        Assert.AreNotEqual(new StopLine("s:1", "GRE", "NORTH"), new StopLine("S:1", "GRE", "NORTH"));
    }

    private static TransitStop Stop(string id, double latOffset) => new(id, $"Stop {id}", new GeoPoint(Origin.Lat + latOffset, Origin.Lon));

    private static NearbyStop Nearby(string id) => new(new IndexedStop(id, $"Stop {id}", Origin.Lat, Origin.Lon, []), 100);

    private static SavedStop Saved(string id, double latOffset, TransitRoute route, TransitDirection direction, bool allRoutes = false) =>
        new(id, $"Saved {id}", Origin.Lat + latOffset, Origin.Lon, route.Id, route.DisplayId, route.Color, direction.Id, direction.Name, allRoutes);

    /// <summary>Walk to <paramref name="stopId"/>, ride, then (for each transfer) a one-minute ride, arriving at <paramref name="arrivesIn"/>.</summary>
    private static TripItinerary Trip(string stopId, TransitRoute route, TransitDirection direction, int departsIn, int arrivesIn, int walkMinutes, int transfers = 0)
    {
        DateTimeOffset departs = Now.AddMinutes(departsIn);
        DateTimeOffset firstRideEnds = Now.AddMinutes(arrivesIn - transfers);
        List<TripLeg> legs =
        [
            new WalkLeg(departs.AddMinutes(-walkMinutes), departs, walkMinutes * 80, []),
            new RideLeg(departs, firstRideEnds, [Line(stopId, route, direction, departs, firstRideEnds)]),
        ];
        for (int i = 0; i < transfers; i++)
        {
            DateTimeOffset starts = firstRideEnds.AddMinutes(i);
            legs.Add(new RideLeg(starts, starts.AddMinutes(1), [Line($"transfer{i}", Thirty, South, starts, starts.AddMinutes(1))]));
        }

        return new TripItinerary(legs, departs.AddMinutes(-walkMinutes), Now.AddMinutes(arrivesIn));
    }

    private static RideLine Line(string stopId, TransitRoute route, TransitDirection direction, DateTimeOffset departs, DateTimeOffset alights) =>
        new(route, direction, Stop(stopId, 0.001), null, 5, [], [new PlannedDeparture(departs, alights, IsRealTime: true, $"trip-{stopId}")]);
}
