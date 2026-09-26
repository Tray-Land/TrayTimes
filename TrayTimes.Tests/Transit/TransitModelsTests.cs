using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TransitModelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MinutesAway_WhenDue_ReturnsZero()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(5), isDue: true);

        // Act
        int minutes = arrival.MinutesAway(Now);

        // Assert
        Assert.AreEqual(0, minutes);
    }

    [TestMethod]
    public void MinutesAway_PartialMinute_RoundsDown()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(7.9));

        // Act
        int minutes = arrival.MinutesAway(Now);

        // Assert
        Assert.AreEqual(7, minutes);
    }

    [TestMethod]
    public void MinutesAway_PredictionInThePast_ReturnsZero()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(-3));

        // Act
        int minutes = arrival.MinutesAway(Now);

        // Assert
        Assert.AreEqual(0, minutes);
    }

    [TestMethod]
    public void TransitRouteToString_DisplayIdDiffersFromName_ShowsBoth()
    {
        // Arrange
        TransitRoute route = new("GOL", "GOL", "Gold Line", "#ffcc00");

        // Act
        string text = route.ToString();

        // Assert
        Assert.AreEqual("GOL · Gold Line", text);
    }

    [TestMethod]
    public void TransitRouteToString_DisplayIdMatchesName_ShowsNameOnce()
    {
        // Arrange
        TransitRoute route = new("14", "14", "14", null);

        // Act
        string text = route.ToString();

        // Assert
        Assert.AreEqual("14", text);
    }

    private static Arrival CreateArrival(DateTimeOffset predicted, bool isDue = false) =>
        new("1234", "14", "14", "NORTH", "Downtown", "5501", predicted, isDue, false, false, ArrivalStatus.Scheduled);
}
