using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class LineStopsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    private static readonly TransitStop First = new("S1", "First", new GeoPoint(43.000, -88.000));
    private static readonly TransitStop Second = new("S2", "Second", new GeoPoint(43.010, -88.000));
    private static readonly TransitStop Third = new("S3", "Third", new GeoPoint(43.020, -88.000));

    [TestMethod]
    public void Build_OtherRoutesAtAStop_ListedAsItsTransfersInNumberOrder()
    {
        // Arrange
        Arrival[] departures = [Departure("S2", "R22", "22"), Departure("S2", "R9", "9"), Departure("S2", "RG", "Green"), Departure("S2", "R9", "9")];

        // Act
        IReadOnlyList<LineStop> stops = LineStops.Build("R14", [First, Second, Third], departures, []);

        // Assert
        CollectionAssert.AreEqual(new[] { "9", "22", "Green" }, stops[1].Transfers.Select(t => t.DisplayId).ToArray());
        Assert.IsEmpty(stops[0].Transfers);
    }

    [TestMethod]
    public void Build_TheLinesOwnDepartures_AreNotTransfers()
    {
        // Act
        IReadOnlyList<LineStop> stops = LineStops.Build("R14", [First], [Departure("S1", "r14", "14")], []);

        // Assert
        Assert.IsEmpty(stops[0].Transfers);
    }

    [TestMethod]
    public void Build_DeparturesAtOtherStops_AreIgnored()
    {
        // Act
        IReadOnlyList<LineStop> stops = LineStops.Build("R14", [First], [Departure("S9", "R22", "22")], []);

        // Assert
        Assert.IsEmpty(stops[0].Transfers);
    }

    [TestMethod]
    public void Build_Vehicles_CountAtTheStopNearestEach()
    {
        // Arrange
        Vehicle[] vehicles =
        [
            new("B1", "R14", "0", new GeoPoint(43.0095, -88.000)),
            new("B2", "R14", "0", new GeoPoint(43.0110, -88.000)),
            new("B3", "R14", "0", new GeoPoint(43.0190, -88.000)),
        ];

        // Act
        IReadOnlyList<LineStop> stops = LineStops.Build("R14", [First, Second, Third], [], vehicles);

        // Assert
        CollectionAssert.AreEqual(new[] { 0, 2, 1 }, stops.Select(s => s.Vehicles).ToArray());
    }

    [TestMethod]
    public void NearestStops_NoStops_ReturnsNothing()
    {
        // Act
        int[] nearest = LineStops.NearestStops([], [new Vehicle("B1", "R14", "0", new GeoPoint(43, -88))]);

        // Assert
        Assert.IsEmpty(nearest);
    }

    private static Arrival Departure(string stopId, string routeId, string display) =>
        new(stopId, routeId, display, "0", "Downtown", Now.AddMinutes(5));
}
