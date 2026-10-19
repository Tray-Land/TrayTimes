using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class SuggestionContextTests
{
    private static readonly GeoPoint Here = new(43, -88);

    [TestMethod]
    public void Leg_TemporaryDestinationWithoutHome_PlansFromDeviceLocationWithoutSavingThePlace()
    {
        // Arrange
        FavoritePlace destination = new DestinationResult("Station", new GeoPoint(43.1, -88), "S:1").AsPlace();
        SuggestionContext context = new([], destination.Id, destination);

        // Act
        CommuteLeg? leg = context.Leg(Here, DateTimeOffset.UtcNow);

        // Assert
        Assert.IsNotNull(leg);
        Assert.AreEqual(Here, leg.Origin);
        Assert.AreSame(destination, leg.To);
        Assert.IsFalse(context.Target(destination).Persist);
        Assert.AreEqual("S:1", context.Target(destination).StopId);
        Assert.IsEmpty(context.Places);
    }

    [TestMethod]
    public void Equals_ChangedTemporaryDestination_InvalidatesDiscovery()
    {
        // Arrange
        FavoritePlace destination = new DestinationResult("Station", Here, "S:1").AsPlace();
        SuggestionContext first = new([], destination.Id, destination);
        SuggestionContext same = new([], destination.Id, destination);

        // Act / Assert
        Assert.AreEqual(first, same);
        Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, first with { TemporaryDestination = destination with { StopId = "S:2" } });
        Assert.AreNotEqual(first, first with { TemporaryDestination = null });
    }
}
