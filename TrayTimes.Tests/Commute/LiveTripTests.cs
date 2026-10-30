using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class LiveTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", "#ff0000");
    private static readonly TransitRoute Thirty = new("R:30", "30", "Sherman", "#0000ff");
    private static readonly TransitDirection North = new("0", "Northbound");

    [TestMethod]
    public void Compute_LiveDeparturesForEveryRide_ReplaceThePlannedOnes()
    {
        // Arrange: both buses run 2 minutes late.
        Arrival first = Live("a", Fourteen, 7, "14-1");
        Arrival second = Live("b", Thirty, 27, "30-1");

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), [first, second], Now)!;

        // Assert
        CollectionAssert.AreEqual(new[] { At(2), At(7), At(22), At(27), At(37) }, trip.Legs.Select(l => l.Starts).ToArray());
        CollectionAssert.AreEqual(new[] { At(7), At(22), At(24), At(37), At(40) }, trip.Legs.Select(l => l.Ends).ToArray());
        Assert.AreSame(first, Ride(trip, 0).Departure);
        Assert.AreSame(second, Ride(trip, 1).Departure);
        Assert.AreEqual(At(40), trip.Arrives);
    }

    [TestMethod]
    public void Compute_LateFirstBus_TakesTheSecondLinesNextDeparture()
    {
        // Arrange: the 14 leaves 5 minutes late, so its rider reaches "b" at 8:27, after the 8:25 30.
        Arrival[] batch = [Live("a", Fourteen, 10, "14-1"), Live("b", Thirty, 25, "30-1"), Live("b", Thirty, 40, "30-2")];

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), batch, Now)!;

        // Assert
        Assert.AreEqual("30-2", Ride(trip, 1).Departure?.TripId);
        Assert.AreEqual(At(40), Ride(trip, 1).Starts);
        Assert.AreEqual(At(53), trip.Arrives);
    }

    [TestMethod]
    public void Compute_CanceledDeparture_TakesTheNextOne()
    {
        // Arrange
        Arrival next = Live("a", Fourteen, 20, "14-2");
        Arrival[] batch = [Live("a", Fourteen, 5, "14-1", ArrivalStatus.Canceled), next, Live("b", Thirty, 40, "30-2")];

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), batch, Now)!;

        // Assert
        Assert.AreSame(next, Ride(trip, 0).Departure);
        Assert.AreEqual(At(15), trip.LeaveBy);
    }

    [TestMethod]
    public void Compute_CanceledDepartureTheBatchEndsWith_IsNotTakenFromThePlan()
    {
        // Arrange: the batch knows only the canceled 14-1, which the plan still lists.
        Arrival[] batch = [Live("a", Fourteen, 5, "14-1", ArrivalStatus.Canceled)];

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), batch, Now)!;

        // Assert
        Assert.AreEqual(At(20), Ride(trip, 0).Starts);
        Assert.IsNull(Ride(trip, 0).Departure);
    }

    [TestMethod]
    public void Compute_NoLiveDepartures_UsesThePlannedOnes()
    {
        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), [], Now)!;

        // Assert
        CollectionAssert.AreEqual(new[] { At(0), At(5), At(20), At(25), At(35) }, trip.Legs.Select(l => l.Starts).ToArray());
        Assert.AreEqual(At(38), trip.Arrives);
        Assert.IsNull(Ride(trip, 0).Departure);
        Assert.IsNull(Ride(trip, 1).Departure);
    }

    [TestMethod]
    public void Compute_BatchWithoutTheTransferStop_TimesOnlyThatRideFromThePlan()
    {
        // Arrange
        Arrival first = Live("a", Fourteen, 7, "14-1");

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), [first], Now)!;

        // Assert
        Assert.AreSame(first, Ride(trip, 0).Departure);
        Assert.IsNull(Ride(trip, 1).Departure);
        Assert.AreEqual(At(25), Ride(trip, 1).Starts);
        Assert.AreEqual(At(38), trip.Arrives);
    }

    [TestMethod]
    public void Compute_LateFirstBusMissesTheLastConnection_ReturnsNull()
    {
        // Arrange: off the 14 at 8:50, but the last 30 leaves "b" at 8:40.
        Arrival[] batch = [Live("a", Fourteen, 35, "14-1"), Live("b", Thirty, 40, "30-2")];

        // Act
        LiveTrip? trip = LiveTrip.Compute(TransferTrip(), batch, Now);

        // Assert
        Assert.IsNull(trip);
    }

    [TestMethod]
    public void Compute_EveryPlannedDepartureGone_ReturnsNull()
    {
        // Act
        LiveTrip? trip = LiveTrip.Compute(TransferTrip(), [], At(30));

        // Assert
        Assert.IsNull(trip);
    }

    [TestMethod]
    public void Compute_WalkBeforeTheFirstRide_LeavesByItsDepartureLessTheWalk()
    {
        // Arrange: the 14 leaves at 8:12, and the walk to it takes 5 minutes.
        Arrival[] batch = [Live("a", Fourteen, 12, "14-1"), Live("b", Thirty, 40, "30-2")];

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), batch, Now)!;

        // Assert
        Assert.AreEqual(At(7), trip.LeaveBy);
        Assert.AreEqual(At(7), trip.Legs[0].Starts);
        Assert.AreEqual(At(12), trip.Legs[0].Ends);
    }

    [TestMethod]
    public void Compute_RideWithAlternativeLines_TakesTheLineThatGetsOffSoonest()
    {
        // Arrange: the 14 from "a" leaves first but rides 20 minutes; the 30 from "c" rides 12.
        RideLine fourteen = Line("a", Fourteen, Planned(5, 25, "14-1"));
        RideLine thirty = Line("c", Thirty, Planned(7, 19, "30-1"));
        TripItinerary itinerary = new([Walk(0, 5), new RideLeg(At(5), At(25), [fourteen, thirty]), Walk(25, 30)], At(0), At(30));
        Arrival[] batch = [Live("a", Fourteen, 6, "14-1"), Live("c", Thirty, 8, "30-1")];

        // Act
        LiveTrip trip = LiveTrip.Compute(itinerary, batch, Now)!;

        // Assert
        Assert.AreSame(thirty, Ride(trip, 0).Line);
        Assert.AreEqual(At(3), trip.LeaveBy);
        Assert.AreEqual(At(25), trip.Arrives);
    }

    [TestMethod]
    public void Compute_LateTrip_RidesAsLongAsThePlanRidesThatTrip()
    {
        // Arrange: 14-1 rides 15 minutes, the next trip 25. 14-1 is running 13 minutes late, nearer the next trip's time.
        TripItinerary itinerary = OneRide(Planned(5, 20, "14-1"), Planned(20, 45, "14-2"));

        // Act
        LiveTrip trip = LiveTrip.Compute(itinerary, [Live("a", Fourteen, 18, "14-1")], Now)!;

        // Assert
        Assert.AreEqual(At(33), Ride(trip, 0).Ends);
    }

    [TestMethod]
    public void Compute_DepartureWithoutAPlannedTrip_RidesAsLongAsTheNearestPlannedTrip()
    {
        // Arrange: the rush-hour trip rides 25 minutes.
        TripItinerary itinerary = OneRide(Planned(5, 20, "14-1"), Planned(35, 60, "14-3"));

        // Act
        LiveTrip trip = LiveTrip.Compute(itinerary, [Live("a", Fourteen, 33, tripId: null)], Now)!;

        // Assert
        Assert.AreEqual(At(58), Ride(trip, 0).Ends);
    }

    [TestMethod]
    public void Compute_WalkOnlyTrip_LeavesNowAndArrivesAfterTheWalk()
    {
        // Arrange: planned an hour ago.
        TripItinerary walk = new([new WalkLeg(At(-60), At(-42), 1400, [])], At(-60), At(-42));

        // Act
        LiveTrip trip = LiveTrip.Compute(walk, [], Now)!;

        // Assert
        Assert.AreEqual(Now, trip.LeaveBy);
        Assert.AreEqual(At(18), trip.Arrives);
        Assert.IsInstanceOfType<LiveWalk>(trip.Legs.Single());
    }

    [TestMethod]
    public void Duration_LateFirstBusMissesTheTransfer_CountsTheWaitForTheNextOne()
    {
        // Arrange: leave 8:05 for the late 14, and wait at "b" from 8:27 for the 8:40 30.
        Arrival[] batch = [Live("a", Fourteen, 10, "14-1"), Live("b", Thirty, 25, "30-1"), Live("b", Thirty, 40, "30-2")];

        // Act
        LiveTrip trip = LiveTrip.Compute(TransferTrip(), batch, Now)!;

        // Assert: 8:05 to 8:53.
        Assert.AreEqual(TimeSpan.FromMinutes(48), trip.Duration);
    }

    [TestMethod]
    public void Duration_FirstBusLeavesLater_LeavesOutTheWaitBeforeLeaving()
    {
        // Arrange: the 8:05 14 is canceled, so the rider leaves at 8:15 for the 8:20.
        Arrival[] batch = [Live("a", Fourteen, 5, "14-1", ArrivalStatus.Canceled), Live("a", Fourteen, 20, "14-2")];

        // Act
        LiveTrip trip = LiveTrip.Compute(OneRide(Planned(5, 20, "14-1"), Planned(20, 35, "14-2")), batch, Now)!;

        // Assert: 8:15 to 8:35.
        Assert.AreEqual(TimeSpan.FromMinutes(20), trip.Duration);
    }

    [TestMethod]
    public void Best_SeveralOptions_TakesTheOneThatGetsThereFirstAtLiveTimes()
    {
        // Arrange: the planner's first option, the 30 from "c", arrives first as planned, but it runs 25 minutes late.
        TripItinerary direct = new([Walk(0, 5), new RideLeg(At(5), At(20), [Line("c", Thirty, Planned(5, 20, "30-x"))])], At(0), At(20));
        TripItinerary transfer = TransferTrip();

        // Act
        LiveTrip? best = LiveTrip.Best(Plan(direct, transfer), [Live("c", Thirty, 30, "30-x")], Now);

        // Assert: the transfer trip arrives at 8:38 as planned, before the late 30's 8:45.
        Assert.AreSame(transfer, best?.Itinerary);
        Assert.AreEqual(At(38), best?.Arrives);
    }

    [TestMethod]
    public void Best_SameArrival_TakesTheOneWithFewerTransfers()
    {
        // Arrange: both get there at 8:38.
        TripItinerary transfer = TransferTrip();
        TripItinerary direct = OneRide(Planned(20, 38, "14-2"));

        // Act
        LiveTrip? best = LiveTrip.Best(Plan(transfer, direct), [], Now);

        // Assert
        Assert.AreSame(direct, best?.Itinerary);
    }

    [TestMethod]
    public void Best_FasterOptionAfterTheFirstThree_IsNotConsidered()
    {
        // Arrange: only the first three trips with a ride stay live from the batch.
        TripItinerary[] slow = [.. Enumerable.Range(0, PlaceBoard.TripOptions).Select(i => OneRide(Planned(5, 50 + i, $"slow-{i}")))];
        TripItinerary fast = OneRide(Planned(5, 10, "fast"));

        // Act
        LiveTrip? best = LiveTrip.Best(Plan([.. slow, fast]), [], Now);

        // Assert
        Assert.AreSame(slow[0], best?.Itinerary);
    }

    [TestMethod]
    public void Best_NoRideLeft_FallsBackToTheWalkOnlyTrip()
    {
        // Arrange: every planned departure has gone.
        TripItinerary walk = new([new WalkLeg(At(0), At(25), 2000, [])], At(0), At(25));

        // Act
        LiveTrip? best = LiveTrip.Best(Plan(TransferTrip(), walk), [], At(30));

        // Assert
        Assert.AreSame(walk, best?.Itinerary);
        Assert.AreEqual(At(55), best?.Arrives);
    }

    [TestMethod]
    public void Best_WalkOnlyTripAndAFeasibleRide_TakesTheRide()
    {
        // Arrange: the walk is listed first.
        TripItinerary walk = new([new WalkLeg(At(0), At(25), 2000, [])], At(0), At(25));
        TripItinerary ride = TransferTrip();

        // Act
        LiveTrip? best = LiveTrip.Best(Plan(walk, ride), [], Now);

        // Assert
        Assert.AreSame(ride, best?.Itinerary);
    }

    [TestMethod]
    public void Best_NothingLeftToMake_ReturnsNull()
    {
        // Act
        LiveTrip? best = LiveTrip.Best(Plan(TransferTrip()), [], At(30));

        // Assert
        Assert.IsNull(best);
    }

    [TestMethod]
    public void FirstRideDepartures_LiveAndPlannedTrips_KeepsLiveTimesAndScheduledFallbackIds()
    {
        // Arrange
        TripItinerary itinerary = OneRide(Planned(5, 20, "14-1"), Planned(20, 35, "14-2"), Planned(35, 50, "14-3"));
        Arrival live = Live("a", Fourteen, 7, "14-1");
        Arrival canceled = Live("a", Fourteen, 20, "14-2", ArrivalStatus.Canceled);
        LiveTrip trip = LiveTrip.Compute(itinerary, [live, canceled], Now)!;

        // Act
        IReadOnlyList<Arrival> departures = trip.FirstRideDepartures([live, canceled], 6);

        // Assert
        Assert.HasCount(2, departures);
        Assert.AreSame(live, departures[0]);
        Assert.AreEqual("14-3", departures[1].TripId);
        Assert.IsFalse(departures[1].IsRealTime);
        Assert.AreEqual(At(35), departures[1].ScheduledTime);
    }

    [TestMethod]
    public void FirstRideDepartures_LiveTripMovedEarlier_DoesNotRestoreItsOldPlannedTime()
    {
        // Arrange
        TripItinerary itinerary = OneRide(Planned(10, 25, "14-1"), Planned(20, 35, "14-2"));
        Arrival[] batch = [Live("a", Fourteen, 2, "14-1")];
        LiveTrip trip = LiveTrip.Compute(itinerary, batch, Now)!;

        // Act
        IReadOnlyList<Arrival> departures = trip.FirstRideDepartures(batch, 6);

        // Assert
        Assert.AreEqual("14-2", departures.Single().TripId);
        Assert.AreEqual(At(20), departures[0].PredictedTime);
    }

    [TestMethod]
    public void FirstRideDepartures_NoBatch_UsesScheduledTimesWithThePlansTripIds()
    {
        // Arrange
        LiveTrip trip = LiveTrip.Compute(OneRide(Planned(5, 20, "14-1"), Planned(20, 35, "14-2")), [], Now)!;

        // Act
        IReadOnlyList<Arrival> departures = trip.FirstRideDepartures([], 1);

        // Assert
        Assert.AreEqual("14-1", departures.Single().TripId);
        Assert.IsFalse(departures[0].IsRealTime);
        Assert.AreEqual("a", departures[0].StopId);
    }

    [TestMethod]
    public void FirstRideDepartures_NoBatchOnATrainLine_ScheduledDeparturesAreTrains()
    {
        // Arrange
        TransitRoute brown = new("CTA:5673", "Brown", "Brown Line", "#62361b", "'L'", VehicleKind.Subway);
        TripItinerary itinerary = new(
            [Walk(0, 5), new RideLeg(At(5), At(20), [Line("a", brown, Planned(5, 20, "brn-1"))])],
            At(0),
            At(20));
        LiveTrip trip = LiveTrip.Compute(itinerary, [], Now)!;

        // Act
        IReadOnlyList<Arrival> departures = trip.FirstRideDepartures([], 1);

        // Assert
        Assert.AreEqual(VehicleKind.Subway, departures.Single().VehicleKind);
    }

    [TestMethod]
    public void FirstRideDepartures_WalkOnly_ReturnsNoChips()
    {
        // Arrange
        LiveTrip trip = LiveTrip.Compute(new TripItinerary([Walk(0, 20)], Now, At(20)), [], Now)!;

        // Act / Assert
        Assert.IsEmpty(trip.FirstRideDepartures([], 6));
    }

    /// <summary>
    /// Walk 5 minutes to "a", ride the 14 (8:05 or 8:20, 15 minutes), walk 2 minutes to "b", ride the
    /// 30 (8:25 or 8:40, 10 minutes), and walk 3 minutes: as planned, 8:00 to 8:38.
    /// </summary>
    private static TripItinerary TransferTrip() => new(
    [
        Walk(0, 5),
        new RideLeg(At(5), At(20), [Line("a", Fourteen, Planned(5, 20, "14-1"), Planned(20, 35, "14-2"))]),
        Walk(20, 22),
        new RideLeg(At(25), At(35), [Line("b", Thirty, Planned(25, 35, "30-1"), Planned(40, 50, "30-2"))]),
        Walk(35, 38),
    ], At(0), At(38));

    /// <summary>Walk 5 minutes to "a" and ride the 14.</summary>
    private static TripItinerary OneRide(params PlannedDeparture[] departures) => new(
        [Walk(0, 5), new RideLeg(departures[0].Departs, departures[0].Alights, [Line("a", Fourteen, departures)])],
        At(0),
        departures[0].Alights);

    private static TripPlan Plan(params TripItinerary[] itineraries) => new(itineraries, []);

    private static LiveRide Ride(LiveTrip trip, int index) => trip.Legs.OfType<LiveRide>().ElementAt(index);

    private static DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);

    private static WalkLeg Walk(int from, int to) => new(At(from), At(to), (to - from) * 80, []);

    private static PlannedDeparture Planned(int departs, int alights, string tripId) => new(At(departs), At(alights), IsRealTime: false, tripId);

    private static RideLine Line(string stopId, TransitRoute route, params PlannedDeparture[] departures) =>
        new(route, North, new TransitStop(stopId, $"Stop {stopId}", new GeoPoint(43, -88)), null, 4, [], departures);

    private static Arrival Live(string stopId, TransitRoute route, int minutes, string? tripId, ArrivalStatus status = ArrivalStatus.Scheduled) =>
        new(stopId, route.Id, route.DisplayId, North.Id, "Downtown", At(minutes), status, IsRealTime: true, tripId);
}
