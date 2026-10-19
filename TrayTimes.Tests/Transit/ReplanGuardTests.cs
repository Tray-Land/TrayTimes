using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class ReplanGuardTests
{
    private static readonly GeoPoint Home = new(43.0, -88.0);
    private static readonly GeoPoint Work = new(43.1, -88.0);

    [TestMethod]
    public void TryAcquire_RepeatedForTheSameTrip_AllowsOncePerInterval()
    {
        // Arrange
        RequestTestClock clock = new();
        ReplanGuard guard = new(clock);

        // Act
        bool first = guard.TryAcquire(Home, Work);
        clock.Advance(TimeSpan.FromMinutes(5));
        bool soon = guard.TryAcquire(Home, Work);
        clock.Advance(ReplanGuard.Interval - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        bool justBefore = guard.TryAcquire(Home, Work);
        clock.Advance(TimeSpan.FromSeconds(1));
        bool after = guard.TryAcquire(Home, Work);
        bool rightAfterThat = guard.TryAcquire(Home, Work);

        // Assert
        Assert.IsTrue(first);
        Assert.IsFalse(soon);
        Assert.IsFalse(justBefore);
        Assert.IsTrue(after);
        Assert.IsFalse(rightAfterThat);
    }

    [TestMethod]
    [DataRow(0.001, 0.0, false)]
    [DataRow(0.0, 0.0003, false)]
    [DataRow(0.002, 0.0, true)]
    [DataRow(0.0, 0.001, true)]
    public void TryAcquire_AfterATrip_SharesItsGuardOnlyWithAboutTheSamePlaces(double originOffset, double destinationOffset, bool expected)
    {
        // Arrange: 0.001 degrees of latitude is about 111 m, 0.0003 about 33 m, and 0.002 about 222 m.
        ReplanGuard guard = new(new RequestTestClock());
        guard.TryAcquire(Home, Work);

        // Act
        bool allowed = guard.TryAcquire(new GeoPoint(Home.Lat + originOffset, Home.Lon), new GeoPoint(Work.Lat + destinationOffset, Work.Lon));

        // Assert
        Assert.AreEqual(expected, allowed);
    }
}
