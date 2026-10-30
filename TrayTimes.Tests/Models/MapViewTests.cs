using System.Text.Json;
using TrayTimes.Models;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class MapViewTests
{
    [TestMethod]
    public void Constructor_CenterAndZoom_DefaultsToAnUntiltedNorthUpCamera()
    {
        // Act
        MapView view = new(43, -88, 15);

        // Assert
        Assert.AreEqual(0d, view.Heading);
        Assert.AreEqual(0d, view.Pitch);
    }

    [TestMethod]
    public void Deserialize_PreviousSavedView_KeepsItsCenterAndZoom()
    {
        // Arrange
        const string json = """{"Lat":43,"Lon":-88,"Zoom":15}""";

        // Act
        MapView? view = JsonSerializer.Deserialize<MapView>(json);

        // Assert
        Assert.AreEqual(new MapView(43, -88, 15, 0, 0), view);
    }

    [TestMethod]
    public void Serialize_RotatedAndTiltedView_RoundTripsTheEntireCamera()
    {
        // Arrange
        MapView original = new(43, -88, 16, 70, 25);

        // Act
        MapView? restored = JsonSerializer.Deserialize<MapView>(JsonSerializer.Serialize(original));

        // Assert
        Assert.AreEqual(original, restored);
    }
}
