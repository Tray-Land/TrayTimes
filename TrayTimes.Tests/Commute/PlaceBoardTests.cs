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
            Option("a", Fourteen, North, departsIn: 5, arrivesIn: 40, walkMinutes: 3),
            Option("b", Thirty, North, departsIn: 8, arrivesIn: 30, walkMinutes: 6, transfers: 1),
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
            Option("a", Fourteen, North, departsIn: 5, arrivesIn: 45, walkMinutes: 3, transfers: 1),
            Option("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3),
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
            Option("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3),
            Option("a", Thirty, South, departsIn: 7, arrivesIn: 37, walkMinutes: 3),
        ], []);

        // Act
        PlaceBoardResult board = PlaceBoard.Commute(plan, maxStops: 8);

        // Assert
        Assert.HasCount(1, board.Stops);
        Assert.HasCount(2, board.Stops[0].Stop.Routes);
        Assert.HasCount(2, board.Lines);
    }

    [TestMethod]
    public void ArrivalAtDestination_DepartureOnAPlannedLine_AddsThatLinesRestOfTrip()
    {
        // Arrange: the planner's trip on the 14 takes 30 minutes from boarding.
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([Option("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3)], []), maxStops: 8);
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
        PlaceBoardResult board = PlaceBoard.Commute(new TripPlan([Option("a", Fourteen, North, departsIn: 5, arrivesIn: 35, walkMinutes: 3)], []), maxStops: 8);

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

    private static TripOption Option(string stopId, TransitRoute route, TransitDirection direction, int departsIn, int arrivesIn, int walkMinutes, int transfers = 0) =>
        new(Stop(stopId, 0.001), route, direction, walkMinutes * 80, TimeSpan.FromMinutes(walkMinutes), Now.AddMinutes(departsIn), Now.AddMinutes(arrivesIn), transfers);
}
