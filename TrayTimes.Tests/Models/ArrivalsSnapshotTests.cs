using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class ArrivalsSnapshotTests
{
    private static readonly ArrivalsSnapshot Snapshot = new(
        DateTimeOffset.Now,
        [
            CreateArrival("100", "14", "NORTH"),
            CreateArrival("100", "14", "SOUTH"),
            CreateArrival("100", "GRE", "NORTH"),
            CreateArrival("200", "14", "NORTH"),
        ],
        null);

    [TestMethod]
    public void For_SingleRouteStop_ReturnsOnlyMatchingRouteAndDirection()
    {
        // Arrange
        SavedStop stop = SavedStopTests.CreateStop("100", routeId: "14", directionId: "NORTH");

        // Act
        List<Arrival> arrivals = Snapshot.For(stop).ToList();

        // Assert
        Assert.HasCount(1, arrivals);
        Assert.AreEqual("14", arrivals[0].RouteId);
        Assert.AreEqual("NORTH", arrivals[0].DirectionId);
    }

    [TestMethod]
    public void For_RouteAndDirectionInDifferentCase_StillMatches()
    {
        // Arrange
        SavedStop stop = SavedStopTests.CreateStop("100", routeId: "gre", directionId: "north");

        // Act
        int count = Snapshot.For(stop).Count();

        // Assert
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void For_AllRoutesStop_ReturnsEveryArrivalAtThatStop()
    {
        // Arrange
        SavedStop stop = SavedStopTests.CreateStop("100", allRoutes: true);

        // Act
        int count = Snapshot.For(stop).Count();

        // Assert
        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public void For_EmptySnapshot_ReturnsNothing()
    {
        // Arrange
        SavedStop stop = SavedStopTests.CreateStop("100", allRoutes: true);

        // Act
        IEnumerable<Arrival> arrivals = ArrivalsSnapshot.Empty.For(stop);

        // Assert
        Assert.IsEmpty(arrivals);
    }

    private static Arrival CreateArrival(string stopId, string routeId, string directionId) =>
        new(stopId, routeId, routeId, directionId, "Downtown", DateTimeOffset.Now);
}
