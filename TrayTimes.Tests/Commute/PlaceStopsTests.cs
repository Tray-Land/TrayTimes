using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class PlaceStopsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> NoneSaved = [];

    [TestMethod]
    public void Listing_LineDepartingFromAnAllRoutesStop_IsNotListedAgainAtAnotherStop()
    {
        // Arrange: "fav" was saved with All routes; the 30 departs there, though its closest stop is "near".
        PlaceStops board = Board(everyLineAt: ["fav"], new StopLine("near", "R:30", "0"), new StopLine("near", "R:14", "0"));

        // Act
        Func<StopLine, bool> lists = board.Listing([Departure("fav", "R:30")], new HashSet<string> { "fav" });

        // Assert
        Assert.IsTrue(lists(new StopLine("fav", "R:30", "0")));
        Assert.IsFalse(lists(new StopLine("near", "R:30", "0")));
        Assert.IsTrue(lists(new StopLine("near", "R:14", "0")));
    }

    [TestMethod]
    public void Listing_LineDepartingFromAnAllRoutesStop_IsStillListedAtAnotherSavedStop()
    {
        // Arrange: "mine" was saved for the 30, and "fav" with All routes.
        PlaceStops board = Board(everyLineAt: ["fav"], new StopLine("mine", "R:30", "0"));

        // Act
        Func<StopLine, bool> lists = board.Listing([Departure("fav", "R:30")], new HashSet<string> { "fav", "mine" });

        // Assert
        Assert.IsTrue(lists(new StopLine("mine", "R:30", "0")));
    }

    [TestMethod]
    public void Listing_OtherDirectionOfThatLine_IsStillListed()
    {
        // Arrange
        PlaceStops board = Board(everyLineAt: ["fav"], new StopLine("near", "R:30", "1"));

        // Act
        Func<StopLine, bool> lists = board.Listing([Departure("fav", "R:30")], NoneSaved);

        // Assert
        Assert.IsTrue(lists(new StopLine("near", "R:30", "1")));
    }

    [TestMethod]
    public void Listing_StopThatListsEveryLine_ListsLinesItWasntSavedFor()
    {
        // Arrange
        PlaceStops board = Board(everyLineAt: ["fav"]);

        // Act
        Func<StopLine, bool> lists = board.Listing([], NoneSaved);

        // Assert
        Assert.IsTrue(lists(new StopLine("fav", "R:99", "1")));
        Assert.IsFalse(lists(new StopLine("near", "R:99", "1")));
    }

    [TestMethod]
    public void Listing_NoLineFilter_ListsEveryLine()
    {
        // Arrange
        PlaceStops stops = new(null, [], new PlaceNotice("Empty", string.Empty));

        // Act
        Func<StopLine, bool> lists = stops.Listing([Departure("near", "R:30")], NoneSaved);

        // Assert
        Assert.IsTrue(lists(new StopLine("near", "R:30", "0")));
    }

    private static PlaceStops Board(string[] everyLineAt, params StopLine[] lines) =>
        new(new GeoPoint(43, -88), [], new PlaceNotice("Empty", string.Empty), Lines: lines.ToHashSet(), EveryLineAt: everyLineAt.ToHashSet(), Center: new NearYou());

    private static Arrival Departure(string stopId, string routeId) => new(stopId, routeId, routeId[2..], "0", "Downtown", Now.AddMinutes(5));
}
