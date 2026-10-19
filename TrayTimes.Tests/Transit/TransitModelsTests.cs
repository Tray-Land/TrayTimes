using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TransitModelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MinutesAway_UnderAMinute_ReturnsZero()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddSeconds(50));

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
    public void MinutesAway_DepartureInThePast_ReturnsZero()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(-3));

        // Act
        int minutes = arrival.MinutesAway(Now);

        // Assert
        Assert.AreEqual(0, minutes);
    }

    [TestMethod]
    public void IsDelayed_RealTimeTwoMinutesBehindTimetable_IsTrue()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(12)) with { ScheduledTime = Now.AddMinutes(10) };

        // Act
        bool delayed = arrival.IsDelayed;

        // Assert
        Assert.IsTrue(delayed);
        Assert.AreEqual(TimeSpan.FromMinutes(2), arrival.Delay);
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(-3, true)]
    [DataRow(5, false)]
    public void IsDelayed_SlightlyLateEarlyOrScheduledOnly_IsFalse(int minutesLate, bool realTime)
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(10 + minutesLate)) with { ScheduledTime = Now.AddMinutes(10), IsRealTime = realTime };

        // Act
        bool delayed = arrival.IsDelayed;

        // Assert
        Assert.IsFalse(delayed);
    }

    [TestMethod]
    public void Delay_NoTimetableTime_IsNull()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddMinutes(10));

        // Act
        TimeSpan? delay = arrival.Delay;

        // Assert
        Assert.IsNull(delay);
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

    [TestMethod]
    public void RideToDestination_TripOption_IsArrivalLessDeparture()
    {
        // Arrange
        TripOption option = new(new TransitStop("s", "Stop", new GeoPoint(0, 0)), new TransitRoute("r", "1", "One", null),
            new TransitDirection("0", "North"), 200, TimeSpan.FromMinutes(3), Now, Now.AddMinutes(25), Transfers: 1);

        // Act
        TimeSpan ride = option.RideToDestination;

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(25), ride);
    }

    private static Arrival CreateArrival(DateTimeOffset predicted) =>
        new("1234", "14", "14", "0", "Downtown", predicted, TripId: "5501");
}
