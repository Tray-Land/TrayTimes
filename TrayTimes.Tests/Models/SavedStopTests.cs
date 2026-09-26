using TrayTimes.Models;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class SavedStopTests
{
    [TestMethod]
    public void Key_SameStopDifferentDirection_IsDifferent()
    {
        // Arrange
        SavedStop north = CreateStop("100", directionId: "NORTH");
        SavedStop south = CreateStop("100", directionId: "SOUTH");

        // Act & Assert
        Assert.AreNotEqual(north.Key, south.Key);
    }

    [TestMethod]
    public void Description_SingleRoute_ShowsRouteAndDirection()
    {
        // Arrange
        SavedStop stop = CreateStop("100");

        // Act
        string description = stop.Description;

        // Assert
        Assert.AreEqual("Route 14 · Northbound", description);
    }

    [TestMethod]
    public void Description_AllRoutes_MentionsOriginatingRoute()
    {
        // Arrange
        SavedStop stop = CreateStop("100", allRoutes: true);

        // Act
        string description = stop.Description;

        // Assert
        Assert.AreEqual("All routes (added from route 14)", description);
    }

    [TestMethod]
    public void DistinctStopCount_RepeatedStopIds_CountsEachOnce()
    {
        // Arrange
        SavedStop[] stops =
        [
            CreateStop("100", directionId: "NORTH"),
            CreateStop("100", directionId: "SOUTH"),
            CreateStop("200"),
        ];

        // Act
        int count = SavedStop.DistinctStopCount(stops);

        // Assert
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void DistinctStopCount_NoStops_ReturnsZero()
    {
        // Act
        int count = SavedStop.DistinctStopCount([]);

        // Assert
        Assert.AreEqual(0, count);
    }

    internal static SavedStop CreateStop(string stopId, string routeId = "14", string directionId = "NORTH", bool allRoutes = false) =>
        new(stopId, $"Stop {stopId}", 43.04, -87.91, routeId, routeId, null, directionId, "Northbound", allRoutes);
}
