using System.IO.Compression;
using System.Text;
using TrayTimes.Models;
using TrayTimes.Transit;
using TrayTimes.Transit.Gtfs;

namespace TrayTimes.Tests.Transit.Gtfs;

[TestClass]
public sealed class GtfsTimetableTests
{
    private static readonly TimeZoneInfo CentralTime = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // Friday 25 September 2026 is the only service day. Route 14 runs north (GTFS direction 0)
    // through A, B, C and south (direction 1) through E, F; GTFS route 43S (shown as 43) runs B → D.
    private static readonly Dictionary<string, string> Feed = new()
    {
        ["agency.txt"] = """
            agency_id,agency_name,agency_url,agency_timezone
            MCTS,MCTS,https://example.com,America/Chicago
            """,
        ["routes.txt"] = """
            route_id,route_short_name,route_long_name,route_type
            14,14,Forest Home,3
            43S,43,Hales Corners,3
            """,
        ["calendar_dates.txt"] = """
            service_id,date,exception_type
            WKDY,20260925,1
            """,
        ["trips.txt"] = """
            route_id,service_id,trip_id,trip_headsign,direction_id
            14,WKDY,T1,TO DOWNTOWN,0
            14,WKDY,T2,TO SOUTHRIDGE,1
            43S,WKDY,T3,"TO HALES CORNERS, VIA 43RD",0
            14,WKDY,T4,TO DOWNTOWN,0
            """,
        ["stop_times.txt"] = """
            trip_id,arrival_time,departure_time,stop_id,stop_sequence
            T1,08:00:00,08:00:00,A,1
            T1,08:10:00,08:10:00,B,2
            T1,08:20:00,08:20:00,C,3
            T2,09:00:00,09:00:00,E,1
            T2,09:10:00,09:10:00,F,2
            T3,08:30:00,08:30:00,B,1
            T3,08:40:00,08:40:00,D,2
            T4,24:30:00,24:30:00,A,1
            T4,24:40:00,24:40:00,B,2
            """,
    };

    private static readonly IReadOnlyList<IndexedStop> Directory =
    [
        CreateStop("A", ("14", "NORTH")),
        CreateStop("B", ("14", "NORTH"), ("43", "WEST")),
        CreateStop("C", ("14", "NORTH")),
        CreateStop("D", ("43", "WEST")),
        CreateStop("E", ("14", "SOUTH")),
        CreateStop("F", ("14", "SOUTH")),
    ];

    [TestMethod]
    public void GetArrivals_TripRunningThatDay_ReturnsScheduledArrival()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["B"], Chicago(2026, 9, 25, 8, 0), Chicago(2026, 9, 25, 8, 20));

        // Assert
        Arrival arrival = arrivals.Single();
        Assert.AreEqual("B", arrival.StopId);
        Assert.AreEqual("14", arrival.RouteId);
        Assert.AreEqual("NORTH", arrival.DirectionId);
        Assert.AreEqual("DOWNTOWN", arrival.Destination);
        Assert.AreEqual(Chicago(2026, 9, 25, 8, 10), arrival.PredictedTime);
        Assert.IsNull(arrival.VehicleId);
        Assert.IsFalse(arrival.IsRealTime);
        Assert.AreEqual(ArrivalStatus.Scheduled, arrival.Status);
    }

    [TestMethod]
    public void GetArrivals_OtherGtfsDirection_MapsToItsRealTimeDirection()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["E"], Chicago(2026, 9, 25, 8, 0), Chicago(2026, 9, 25, 10, 0));

        // Assert
        Assert.AreEqual("SOUTH", arrivals.Single().DirectionId);
    }

    [TestMethod]
    public void GetArrivals_GtfsRouteIdWithSuffix_UsesRealTimeRouteId()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["D"], Chicago(2026, 9, 25, 8, 0), Chicago(2026, 9, 25, 10, 0));

        // Assert
        Arrival arrival = arrivals.Single();
        Assert.AreEqual("43", arrival.RouteId);
        Assert.AreEqual("WEST", arrival.DirectionId);
    }

    [TestMethod]
    public void GetArrivals_QuotedHeadsignWithComma_KeepsWholeHeadsign()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["D"], Chicago(2026, 9, 25, 8, 0), Chicago(2026, 9, 25, 10, 0));

        // Assert
        Assert.AreEqual("HALES CORNERS, VIA 43RD", arrivals.Single().Destination);
    }

    [TestMethod]
    public void GetArrivals_SeveralStops_ReturnsSoonestFirst()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["D", "B", "A"], Chicago(2026, 9, 25, 7, 0), Chicago(2026, 9, 25, 10, 0));

        // Assert
        CollectionAssert.AreEqual(new[] { "A", "B", "B", "D" }, arrivals.Select(a => a.StopId).ToArray());
    }

    [TestMethod]
    public void GetArrivals_FirstAndLastStopOfTrip_AreDepartureAndDropOffOnly()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["A", "C"], Chicago(2026, 9, 25, 7, 0), Chicago(2026, 9, 25, 9, 0));

        // Assert
        Assert.HasCount(2, arrivals);
        Assert.IsTrue(arrivals[0].IsDeparture);
        Assert.AreEqual(ArrivalStatus.Scheduled, arrivals[0].Status);
        Assert.IsFalse(arrivals[1].IsDeparture);
        Assert.AreEqual(ArrivalStatus.DropOffOnly, arrivals[1].Status);
    }

    [TestMethod]
    public void GetArrivals_TimePastMidnight_FallsEarlyNextDay()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["A"], Chicago(2026, 9, 26, 0, 0), Chicago(2026, 9, 26, 1, 0));

        // Assert
        Assert.AreEqual(Chicago(2026, 9, 26, 0, 30), arrivals.Single().PredictedTime);
    }

    [TestMethod]
    public void GetArrivals_DayWithoutService_ReturnsNothing()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["A", "B"], Chicago(2026, 9, 26, 7, 0), Chicago(2026, 9, 26, 12, 0));

        // Assert
        Assert.IsEmpty(arrivals);
    }

    [TestMethod]
    public void GetArrivals_UnknownStop_ReturnsNothing()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["Z"], Chicago(2026, 9, 25, 0, 0), Chicago(2026, 9, 26, 0, 0));

        // Assert
        Assert.IsEmpty(arrivals);
    }

    [TestMethod]
    public void GetArrivals_EmptyDirectory_KeepsGtfsIds()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, []);

        // Act
        IReadOnlyList<Arrival> arrivals = timetable.GetArrivals(["D"], Chicago(2026, 9, 25, 8, 0), Chicago(2026, 9, 25, 10, 0));

        // Assert
        Arrival arrival = arrivals.Single();
        Assert.AreEqual("43S", arrival.RouteId);
        Assert.AreEqual("43", arrival.RouteDisplayId);
        Assert.AreEqual("0", arrival.DirectionId);
    }

    [TestMethod]
    public void GetArrivals_WeeklyCalendarWithRemovedDate_SkipsThatDateOnly()
    {
        // Arrange
        Dictionary<string, string> feed = new(Feed)
        {
            ["calendar.txt"] = """
                service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
                WKDY,1,1,1,1,1,0,0,20260901,20261231
                """,
            ["calendar_dates.txt"] = """
                service_id,date,exception_type
                WKDY,20260924,2
                """,
        };
        GtfsTimetable timetable = Load(feed, Directory);

        // Act
        int thursday = timetable.GetArrivals(["A"], Chicago(2026, 9, 24, 7, 0), Chicago(2026, 9, 24, 9, 0)).Count;
        int friday = timetable.GetArrivals(["A"], Chicago(2026, 9, 25, 7, 0), Chicago(2026, 9, 25, 9, 0)).Count;
        int saturday = timetable.GetArrivals(["A"], Chicago(2026, 9, 26, 7, 0), Chicago(2026, 9, 26, 9, 0)).Count;

        // Assert
        Assert.AreEqual(0, thursday);
        Assert.AreEqual(1, friday);
        Assert.AreEqual(0, saturday);
    }

    [TestMethod]
    public void GetDownstreamArrivals_BusOnTime_ReturnsScheduledTimeAtLaterStop()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("A", "NORTH", Chicago(2026, 9, 25, 8, 0)), ["B", "C"]);

        // Assert
        Assert.HasCount(2, downstream);
        Assert.AreEqual(Chicago(2026, 9, 25, 8, 10), downstream.Single(s => s.StopId == "B").ArrivesAt);
        Assert.AreEqual(Chicago(2026, 9, 25, 8, 20), downstream.Single(s => s.StopId == "C").ArrivesAt);
    }

    [TestMethod]
    public void GetDownstreamArrivals_BusRunningLate_CarriesTheDelayForward()
    {
        // Arrange: predicted 5 minutes after the 8:00 departure.
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("A", "NORTH", Chicago(2026, 9, 25, 8, 5)), ["C"]);

        // Assert
        Assert.AreEqual(Chicago(2026, 9, 25, 8, 25), downstream.Single().ArrivesAt);
    }

    [TestMethod]
    public void GetDownstreamArrivals_StopBeforeBoardingStop_IsNotIncluded()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("B", "NORTH", Chicago(2026, 9, 25, 8, 10)), ["A", "C"]);

        // Assert
        Assert.AreEqual("C", downstream.Single().StopId);
    }

    [TestMethod]
    public void GetDownstreamArrivals_TripIdMatches_FindsTripEvenFarFromItsScheduledTime()
    {
        // Arrange: 40 minutes off, past what route-and-time matching accepts.
        GtfsTimetable timetable = Load(Feed, Directory);
        Arrival boarding = Boarding("A", "NORTH", Chicago(2026, 9, 25, 8, 40)) with { TripId = "T1" };

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(boarding, ["C"]);

        // Assert
        Assert.AreEqual(Chicago(2026, 9, 25, 9, 0), downstream.Single().ArrivesAt);
    }

    [TestMethod]
    public void GetDownstreamArrivals_UnknownTripId_FallsBackToRouteAndTime()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);
        Arrival boarding = Boarding("A", "NORTH", Chicago(2026, 9, 25, 8, 2)) with { TripId = "not-in-gtfs" };

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(boarding, ["C"]);

        // Assert
        Assert.AreEqual(Chicago(2026, 9, 25, 8, 22), downstream.Single().ArrivesAt);
    }

    [TestMethod]
    public void GetDownstreamArrivals_NoTripNearThePredictedTime_ReturnsNothing()
    {
        // Arrange: an hour from the only trips through A.
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("A", "NORTH", Chicago(2026, 9, 25, 13, 0)), ["C"]);

        // Assert
        Assert.IsEmpty(downstream);
    }

    [TestMethod]
    public void GetDownstreamArrivals_OtherDirection_ReturnsNothing()
    {
        // Arrange
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("B", "SOUTH", Chicago(2026, 9, 25, 8, 10)), ["C"]);

        // Assert
        Assert.IsEmpty(downstream);
    }

    [TestMethod]
    public void GetDownstreamArrivals_TripPastMidnight_UsesThePreviousServiceDay()
    {
        // Arrange: T4 leaves A at 24:30 on Friday's service, which is 12:30 AM Saturday.
        GtfsTimetable timetable = Load(Feed, Directory);

        // Act
        IReadOnlyList<TripStop> downstream = timetable.GetDownstreamArrivals(Boarding("A", "NORTH", Chicago(2026, 9, 26, 0, 30)), ["B"]);

        // Assert
        Assert.AreEqual(Chicago(2026, 9, 26, 0, 40), downstream.Single().ArrivesAt);
    }

    [TestMethod]
    public void Load_MissingStopTimes_ThrowsInvalidData()
    {
        // Arrange
        Dictionary<string, string> feed = new(Feed);
        feed.Remove("stop_times.txt");

        // Act & Assert
        Assert.ThrowsExactly<InvalidDataException>(() => Load(feed, Directory));
    }

    private static GtfsTimetable Load(Dictionary<string, string> files, IReadOnlyList<IndexedStop> directory)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return GtfsTimetable.Load(stream, directory);
    }

    private static Arrival Boarding(string stopId, string directionId, DateTimeOffset predicted) => new(
        stopId, "14", "14", directionId, "DOWNTOWN", "5301", predicted, IsDue: false, IsDelayed: false, IsDeparture: false, ArrivalStatus.Scheduled);

    private static DateTimeOffset Chicago(int year, int month, int day, int hour, int minute)
    {
        DateTime local = new(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, CentralTime.GetUtcOffset(local));
    }

    private static IndexedStop CreateStop(string id, params (string RouteId, string DirectionId)[] routes) =>
        new(id, $"Stop {id}", 43, -88, [.. routes.Select(r => new StopRouteInfo(r.RouteId, r.RouteId, null, r.DirectionId, r.DirectionId))]);
}
