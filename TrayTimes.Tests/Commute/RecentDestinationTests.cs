using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class RecentDestinationTests
{
    [TestMethod]
    public void WithChosen_AlreadyListed_MovesItToTheFrontWithoutDuplicating()
    {
        // Arrange
        RecentDestination cafe = new("Cafe", 43.0, -89.0);
        RecentDestination bank = new("Bank", 43.1, -89.1);

        // Act
        var result = RecentDestination.WithChosen([cafe, bank], new RecentDestination("cafe", 43.0001, -89.0001));

        // Assert
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("cafe", result[0].Name);
        Assert.AreEqual("Bank", result[1].Name);
    }

    [TestMethod]
    public void WithChosen_PastTheLimit_DropsTheOldest()
    {
        // Arrange
        RecentDestination[] existing = [.. Enumerable.Range(0, RecentDestination.Max).Select(i => new RecentDestination($"P{i}", i, i))];

        // Act
        var result = RecentDestination.WithChosen(existing, new RecentDestination("New", 50, 50));

        // Assert
        Assert.AreEqual(RecentDestination.Max, result.Count);
        Assert.AreEqual("New", result[0].Name);
        Assert.IsFalse(result.Any(r => r.Name == $"P{RecentDestination.Max - 1}"));
    }

    [TestMethod]
    public void WithChosen_StopsMatchById()
    {
        var result = RecentDestination.WithChosen([new("Main & 1st", 1, 1, "S:1")], new("Main and First", 1, 1, "S:1"));
        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public void ToResult_RoundTripsTheChosenDestination()
    {
        DestinationResult original = new("Station", new GeoPoint(43, -88), "S:9");
        DestinationResult back = RecentDestination.From(original).ToResult();
        Assert.AreEqual(original, back);
    }
}
