using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class NearbyStopTests
{
    // About 111 m per 0.001 degree of latitude.
    private const double MetersPerMilliDegree = 111.2;

    private static readonly GeoPoint Origin = new(43.0, -88.0);

    [TestMethod]
    public void Find_StopsWithinRadius_ReturnsClosestFirst()
    {
        // Arrange
        IndexedStop[] stops = [StopNorthOf("far", 5), StopNorthOf("near", 1), StopNorthOf("mid", 3)];

        // Act
        IReadOnlyList<NearbyStop> nearby = NearbyStop.Find(stops, Origin, max: 10);

        // Assert
        CollectionAssert.AreEqual(new[] { "near", "mid", "far" }, nearby.Select(n => n.Stop.Id).ToArray());
        Assert.AreEqual(MetersPerMilliDegree, nearby[0].Meters, 1);
    }

    [TestMethod]
    public void Find_MoreStopsThanMax_ReturnsOnlyMax()
    {
        // Arrange
        IndexedStop[] stops = [StopNorthOf("a", 1), StopNorthOf("b", 2), StopNorthOf("c", 3)];

        // Act
        IReadOnlyList<NearbyStop> nearby = NearbyStop.Find(stops, Origin, max: 2);

        // Assert
        Assert.HasCount(2, nearby);
    }

    [TestMethod]
    public void Find_NoneWithinRadius_FallsBackToNearestFew()
    {
        // Arrange: 1.1 to 1.8 km away, outside the 800 m radius but inside the 2 km fallback.
        IndexedStop[] stops = Enumerable.Range(10, 7).Select(i => StopNorthOf($"s{i}", i + 0.5)).ToArray();

        // Act
        IReadOnlyList<NearbyStop> nearby = NearbyStop.Find(stops, Origin, max: 10);

        // Assert
        Assert.HasCount(4, nearby);
        Assert.AreEqual("s10", nearby[0].Stop.Id);
    }

    [TestMethod]
    public void Find_AllBeyondFallbackRadius_ReturnsEmpty()
    {
        // Arrange
        IndexedStop[] stops = [StopNorthOf("far", 30)];

        // Act
        IReadOnlyList<NearbyStop> nearby = NearbyStop.Find(stops, Origin, max: 10);

        // Assert
        Assert.IsEmpty(nearby);
    }

    private static IndexedStop StopNorthOf(string id, double milliDegrees) =>
        new(id, id, Origin.Lat + (milliDegrees / 1000), Origin.Lon, []);
}
