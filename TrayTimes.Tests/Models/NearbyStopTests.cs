using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class NearbyStopTests
{
    private static readonly GeoPoint Origin = new(43.0, -88.0);

    [TestMethod]
    public void OrderByWalk_RoutedStops_SortsByWalkTimeNotDistance()
    {
        // Arrange: "near" is closer as the crow flies but has the longer walk (say, across a freeway).
        NearbyStop near = new(StopNorthOf("near", 1), 111, WalkSeconds: 600, WalkMeters: 900);
        NearbyStop far = new(StopNorthOf("far", 3), 333, WalkSeconds: 240, WalkMeters: 340);

        // Act
        IReadOnlyList<NearbyStop> ordered = NearbyStop.OrderByWalk([near, far]);

        // Assert
        CollectionAssert.AreEqual(new[] { "far", "near" }, ordered.Select(n => n.Stop.Id).ToArray());
    }

    [TestMethod]
    public void OrderByWalk_UnroutedStops_SortAfterRoutedByDistance()
    {
        // Arrange
        NearbyStop unroutedNear = new(StopNorthOf("unroutedNear", 1), 111);
        NearbyStop unroutedFar = new(StopNorthOf("unroutedFar", 2), 222);
        NearbyStop routed = new(StopNorthOf("routed", 5), 555, WalkSeconds: 400, WalkMeters: 560);

        // Act
        IReadOnlyList<NearbyStop> ordered = NearbyStop.OrderByWalk([unroutedFar, routed, unroutedNear]);

        // Assert
        CollectionAssert.AreEqual(new[] { "routed", "unroutedNear", "unroutedFar" }, ordered.Select(n => n.Stop.Id).ToArray());
    }

    [TestMethod]
    public void WalkMinutes_Routed_RoundsUpRoutedSeconds()
    {
        // Arrange
        NearbyStop stop = new(StopNorthOf("a", 1), 111, WalkSeconds: 241, WalkMeters: 300);

        // Act / Assert
        Assert.AreEqual(5, stop.WalkMinutes(80));
        Assert.AreEqual(300, stop.DistanceMeters);
    }

    [TestMethod]
    public void WalkMinutes_Unrouted_EstimatesFromStraightLine()
    {
        // Arrange
        NearbyStop stop = new(StopNorthOf("a", 1), 161);

        // Act / Assert
        Assert.AreEqual(3, stop.WalkMinutes(80));
        Assert.AreEqual(161, stop.DistanceMeters);
    }

    private static IndexedStop StopNorthOf(string id, double milliDegrees) =>
        new(id, id, Origin.Lat + (milliDegrees / 1000), Origin.Lon, []);
}
