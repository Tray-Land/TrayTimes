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
    public void DisplayName_WithoutNickname_IsStopName()
    {
        // Arrange
        SavedStop stop = CreateStop("100") with { Nickname = "  " };

        // Act & Assert
        Assert.AreEqual(stop.StopName, stop.DisplayName);
    }

    [TestMethod]
    public void WithNickname_SetsEveryEntryForTheStopOnly()
    {
        // Arrange
        SavedStop[] stops = [CreateStop("100", directionId: "NORTH"), CreateStop("100", directionId: "SOUTH"), CreateStop("200")];

        // Act
        SavedStop[] named = [.. SavedStop.WithNickname(stops, "100", "  Work stop ")];

        // Assert
        Assert.AreEqual("Work stop", named[0].DisplayName);
        Assert.AreEqual("Work stop", named[1].DisplayName);
        Assert.AreEqual(stops[2].StopName, named[2].DisplayName);
    }

    [TestMethod]
    public void WithNickname_BlankNickname_ClearsIt()
    {
        // Arrange
        SavedStop[] stops = [CreateStop("100") with { Nickname = "Home stop" }];

        // Act
        SavedStop[] cleared = [.. SavedStop.WithNickname(stops, "100", " ")];

        // Assert
        Assert.IsNull(cleared[0].Nickname);
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
    public void Description_AllRoutesFromStopSearch_SaysAllRoutesOnly()
    {
        // Arrange
        SavedStop stop = new("MCTS:100", "Stop 100", 43.04, -87.91, string.Empty, string.Empty, null, string.Empty, string.Empty, AllRoutes: true);

        // Act
        string description = stop.Description;

        // Assert
        Assert.AreEqual("All routes", description);
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
