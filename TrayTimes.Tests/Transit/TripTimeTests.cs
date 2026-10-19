using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TripTimeTests
{
    [TestMethod]
    [DataRow(TripTimeMode.LeaveAt, 1, 5)]
    [DataRow(TripTimeMode.ArriveBy, 4, 0)]
    [DataRow(TripTimeMode.LeaveAt, 5, 5)]
    [DataRow(TripTimeMode.ArriveBy, 5, 5)]
    public void Constructor_TimeConstraint_RoundsWithoutRelaxingIt(TripTimeMode mode, int minute, int rounded)
    {
        // Arrange
        DateTimeOffset at = new(2026, 10, 20, 9, minute, 0, TimeSpan.FromHours(-4));

        // Act
        TripTime time = new(mode, at);

        // Assert
        Assert.AreEqual(new DateTimeOffset(2026, 10, 20, 13, rounded, 0, TimeSpan.Zero), time.At);
        Assert.AreEqual(mode, time.Mode);
    }

    [TestMethod]
    public void Constructor_Now_IgnoresTimeAndEqualsDefault()
    {
        // Act
        TripTime time = new(TripTimeMode.Now, DateTimeOffset.UtcNow);

        // Assert
        Assert.AreEqual(default, time);
    }

    [TestMethod]
    public void Constructor_InvalidMode_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new TripTime((TripTimeMode)99));
    }

    [TestMethod]
    public void FromLocal_DateAndClock_UsesTheSpecifiedTimeZone()
    {
        // Arrange
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");

        // Act
        TripTime time = TripTime.FromLocal(TripTimeMode.ArriveBy, new DateTime(2026, 10, 20), TimeSpan.FromHours(9), zone);

        // Assert
        Assert.AreEqual(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero), time.At);
    }

    [TestMethod]
    [DataRow(3, 8, 2)]
    [DataRow(11, 1, 1)]
    public void FromLocal_DaylightSavingSkippedOrRepeatedTime_Throws(int month, int day, int hour)
    {
        // Arrange
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // Act / Assert
        Assert.Throws<ArgumentException>(() => TripTime.FromLocal(TripTimeMode.LeaveAt,
            new DateTime(2026, month, day), TimeSpan.FromHours(hour).Add(TimeSpan.FromMinutes(30)), zone));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(24)]
    public void FromLocal_ClockOutsideOneDay_Throws(int hour)
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => TripTime.FromLocal(TripTimeMode.LeaveAt,
            new DateTime(2026, 10, 20), TimeSpan.FromHours(hour), TimeZoneInfo.Utc));
    }
}
