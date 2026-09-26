using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class GeoMathTests
{
    [TestMethod]
    public void DistanceMeters_SamePoint_ReturnsZero()
    {
        // Arrange
        GeoPoint point = new(43.0389, -87.9065);

        // Act
        double meters = GeoMath.DistanceMeters(point, point);

        // Assert
        Assert.AreEqual(0, meters, 1e-9);
    }

    [TestMethod]
    public void DistanceMeters_OneDegreeOfLatitude_ReturnsAbout111Kilometers()
    {
        // Arrange
        GeoPoint a = new(43, -88);
        GeoPoint b = new(44, -88);

        // Act
        double meters = GeoMath.DistanceMeters(a, b);

        // Assert
        Assert.AreEqual(111_195, meters, 50);
    }

    [TestMethod]
    public void DistanceMeters_SwappedPoints_ReturnsSameDistance()
    {
        // Arrange
        GeoPoint downtown = new(43.0389, -87.9065);
        GeoPoint airport = new(42.9476, -87.8966);

        // Act
        double there = GeoMath.DistanceMeters(downtown, airport);
        double back = GeoMath.DistanceMeters(airport, downtown);

        // Assert
        Assert.AreEqual(there, back, 1e-6);
    }
}
