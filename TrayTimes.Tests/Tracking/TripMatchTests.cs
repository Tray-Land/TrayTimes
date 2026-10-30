using TrayTimes.Commute;
using TrayTimes.Tracking;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Tracking;

[TestClass]
public sealed class TripMatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly StopLine Line = new("stop", "R:14", "0");

    [TestMethod]
    public void Find_TripId_MatchesThatTripAtAnyTime()
    {
        // Arrange
        Arrival trip = Departure(25, "t2");

        // Act
        Arrival? match = TripMatch.Find([Departure(5, "t1"), trip], Line, "t2", Now.AddMinutes(5));

        // Assert
        Assert.AreSame(trip, match);
    }

    [TestMethod]
    public void Find_ScheduledTripGainedARealTimeId_StillMatchesByItsTimetableId()
    {
        // Arrange: tracked while scheduled-only, by its timetable ID; now it has a real-time one too.
        Arrival trip = Departure(25, "rt-2") with { ScheduleTripId = "key-2" };

        // Act
        Arrival? match = TripMatch.Find([Departure(5, "rt-1") with { ScheduleTripId = "key-1" }, trip], Line, "key-2", Now.AddMinutes(24));

        // Assert
        Assert.AreSame(trip, match);
    }

    [TestMethod]
    public void Find_NoTripId_TakesTheNearestRealTimeDepartureWithinTheTolerance()
    {
        // Arrange
        Arrival near = Departure(7, null);

        // Act
        Arrival? match = TripMatch.Find([Departure(12, null), near, Departure(6, null, isRealTime: false)], Line, null, Now.AddMinutes(5));

        // Assert
        Assert.AreSame(near, match);
    }

    [TestMethod]
    public void Find_AnotherStopOrDirection_IsNotAMatch()
    {
        // Arrange
        Arrival elsewhere = Departure(5, "t1") with { StopId = "other" };
        Arrival southbound = Departure(5, "t1") with { DirectionId = "1" };

        // Act
        Arrival? match = TripMatch.Find([elsewhere, southbound], Line, "t1", Now.AddMinutes(5));

        // Assert
        Assert.IsNull(match);
    }

    private static Arrival Departure(int minutes, string? tripId, bool isRealTime = true) =>
        new("stop", "r:14", "14", "0", "Downtown", Now.AddMinutes(minutes), IsRealTime: isRealTime, TripId: tripId);
}
