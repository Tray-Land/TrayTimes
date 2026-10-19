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

    [TestMethod]
    public void DecodePolyline_GoogleReferenceExample_ReturnsItsPoints()
    {
        // Arrange: the example from Google's encoded polyline algorithm documentation.
        const string encoded = "_p~iF~ps|U_ulLnnqC_mqNvxq`@";

        // Act
        IReadOnlyList<GeoPoint> points = GeoMath.DecodePolyline(encoded);

        // Assert
        Assert.HasCount(3, points);
        Assert.AreEqual(new GeoPoint(38.5, -120.2), points[0]);
        Assert.AreEqual(new GeoPoint(40.7, -120.95), points[1]);
        Assert.AreEqual(new GeoPoint(43.252, -126.453), points[2]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void DecodePolyline_NothingToDecode_ReturnsNoPoints(string? encoded)
    {
        // Act
        IReadOnlyList<GeoPoint> points = GeoMath.DecodePolyline(encoded);

        // Assert
        Assert.IsEmpty(points);
    }

    [TestMethod]
    public void DecodePolyline_TruncatedInput_KeepsThePointsBeforeTheDamage()
    {
        // Arrange: the reference example with its last value cut short.
        const string encoded = "_p~iF~ps|U_ulLnnqC_mqNvxq";

        // Act
        IReadOnlyList<GeoPoint> points = GeoMath.DecodePolyline(encoded);

        // Assert
        Assert.HasCount(2, points);
    }

    [TestMethod]
    public void EncodePolyline_DecodedAgain_RoundTripsToFivePlaces()
    {
        // Arrange
        GeoPoint[] points = [new(45.526168, -73.595061), new(45.52, -73.6), new(-33.8688, 151.2093), new(0, 0)];

        // Act
        IReadOnlyList<GeoPoint> decoded = GeoMath.DecodePolyline(GeoMath.EncodePolyline(points));

        // Assert
        Assert.HasCount(points.Length, decoded);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.AreEqual(points[i].Lat, decoded[i].Lat, 1e-5);
            Assert.AreEqual(points[i].Lon, decoded[i].Lon, 1e-5);
        }
    }

    [TestMethod]
    [DataRow(44.0, -88.0, 0)]
    [DataRow(43.0, -87.0, 90)]
    [DataRow(42.0, -88.0, 180)]
    [DataRow(43.0, -89.0, 270)]
    public void BearingDegrees_DueCompassDirections_ReturnsTheirBearing(double lat, double lon, int expected)
    {
        // Arrange
        GeoPoint from = new(43, -88);

        // Act
        int bearing = GeoMath.BearingDegrees(from, new GeoPoint(lat, lon));

        // Assert
        Assert.AreEqual(expected, bearing, 1);
    }
}
