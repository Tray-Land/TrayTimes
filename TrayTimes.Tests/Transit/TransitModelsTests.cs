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
    public void Transfers_TwoRidesWithWalks_IsOne()
    {
        // Arrange
        TripItinerary trip = new([Walk(0, 3), Ride(3, 15), Walk(15, 17), Ride(20, 30), Walk(30, 32)], Now, Now.AddMinutes(32));

        // Act
        int transfers = trip.Transfers;

        // Assert
        Assert.AreEqual(1, transfers);
    }

    [TestMethod]
    public void Transfers_WalkOnly_IsZero()
    {
        // Arrange
        TripItinerary trip = new([Walk(0, 20)], Now, Now.AddMinutes(20));

        // Act
        int transfers = trip.Transfers;

        // Assert
        Assert.AreEqual(0, transfers);
    }

    [TestMethod]
    public void Duration_Leg_IsEndLessStart()
    {
        // Arrange
        TripLeg leg = Ride(3, 15);

        // Act
        TimeSpan duration = leg.Duration;

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(12), duration);
    }

    private static Arrival CreateArrival(DateTimeOffset predicted) =>
        new("1234", "14", "14", "0", "Downtown", predicted, TripId: "5501");

    private static WalkLeg Walk(int fromMinute, int toMinute) => new(Now.AddMinutes(fromMinute), Now.AddMinutes(toMinute), 100, []);

    private static RideLeg Ride(int fromMinute, int toMinute) => new(Now.AddMinutes(fromMinute), Now.AddMinutes(toMinute),
    [
        new RideLine(new TransitRoute("r", "1", "One", null), new TransitDirection("0", "North"), new TransitStop("s", "Stop", new GeoPoint(0, 0)), null, 4, [],
            [new PlannedDeparture(Now.AddMinutes(fromMinute), Now.AddMinutes(toMinute), IsRealTime: true, "t")]),
    ]);
}
