using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class RouteArrivalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ByRoute_SameRouteTwice_FoldsIntoOneRow()
    {
        // Arrange
        Arrival first = CreateArrival(14, "15");
        Arrival second = CreateArrival(46, "15");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([first, second], null, _ => true, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.HasCount(1, rows);
        Assert.AreSame(first, rows[0].Next);
        CollectionAssert.AreEqual(new[] { second }, rows[0].Later.ToArray());
    }

    [TestMethod]
    public void ByRoute_RouteIdCaseDiffers_TreatedAsSameRoute()
    {
        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([CreateArrival(5, "gre"), CreateArrival(20, "GRE")], null, _ => true, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.HasCount(1, rows);
    }

    [TestMethod]
    public void ByRoute_OtherDirection_GetsOwnRow()
    {
        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([CreateArrival(5, "14"), CreateArrival(8, "14", "SOUTH")], null, _ => true, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.HasCount(2, rows);
    }

    [TestMethod]
    public void ByRoute_SoonestCantBeCaught_LeadsWithNextAndDropsEarlier()
    {
        // Arrange
        Arrival missed = CreateArrival(1, "14");
        Arrival next = CreateArrival(16, "14");
        Arrival after = CreateArrival(31, "14");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([missed, next, after], null, a => a != missed, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.AreSame(next, rows[0].Next);
        CollectionAssert.AreEqual(new[] { after }, rows[0].Later.ToArray());
    }

    [TestMethod]
    public void ByRoute_NoneCanBeCaught_LeadsWithSoonest()
    {
        // Arrange
        Arrival first = CreateArrival(1, "14");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([first, CreateArrival(2, "14")], null, _ => false, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.AreSame(first, rows[0].Next);
        Assert.HasCount(1, rows[0].Later);
    }

    [TestMethod]
    public void ByRoute_PinnedLaterBus_KeepsSoonestLeading()
    {
        // Arrange
        Arrival first = CreateArrival(10, "14");
        Arrival pinned = CreateArrival(30, "14");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([first, pinned, CreateArrival(50, "14")], pinned, _ => true, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.AreSame(first, rows[0].Next);
        CollectionAssert.Contains(rows[0].Later.ToArray(), pinned);
    }

    [TestMethod]
    public void ByRoute_PinnedBusCantBeCaught_StillLeads()
    {
        // Arrange
        Arrival pinned = CreateArrival(2, "14");
        Arrival next = CreateArrival(20, "14");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([pinned, next], pinned, a => a == next, maxRows: 3, maxPerRow: 3);

        // Assert
        Assert.AreSame(pinned, rows[0].Next);
    }

    [TestMethod]
    public void ByRoute_ManyBusesAndRoutes_CapsRowsAndTimes()
    {
        // Arrange
        Arrival[] arrivals =
        [
            CreateArrival(3, "A"), CreateArrival(4, "B"), CreateArrival(5, "C"),
            CreateArrival(10, "A"), CreateArrival(20, "A"), CreateArrival(30, "A"),
        ];

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute(arrivals, null, _ => true, maxRows: 2, maxPerRow: 3);

        // Assert
        CollectionAssert.AreEqual(new[] { "A", "B" }, rows.Select(r => r.Next.RouteId).ToArray());
        Assert.HasCount(2, rows[0].Later);
    }

    [TestMethod]
    public void ByRoute_LeadsChangeOrder_RowsSortedByLead()
    {
        // Arrange
        Arrival missed = CreateArrival(1, "A");

        // Act
        IReadOnlyList<RouteArrivals> rows = RouteArrivals.ByRoute([missed, CreateArrival(5, "B"), CreateArrival(20, "A")], null, a => a != missed, maxRows: 3, maxPerRow: 3);

        // Assert
        CollectionAssert.AreEqual(new[] { "B", "A" }, rows.Select(r => r.Next.RouteId).ToArray());
    }

    private static Arrival CreateArrival(int minutes, string routeId, string directionId = "NORTH") =>
        new("100", routeId, routeId, directionId, "Downtown", Now.AddMinutes(minutes), TripId: $"trip-{minutes}");
}
