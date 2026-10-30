using System.Text.Json;
using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class TripProgressTests
{
    private const int Lead = 5;
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", "#ff0000");
    private static readonly TransitRoute Thirty = new("R:30", "30", "Sherman", "#0000ff");
    private static readonly TransitDirection North = new("0", "Northbound");

    [TestMethod]
    [DataRow(-8, TripStepKind.GetReady, 0, 0, 0, false)]
    [DataRow(-4, TripStepKind.GetReady, 0, 0, 0, true)]
    [DataRow(1, TripStepKind.ToStop, 0, 0, 5, false)]
    [DataRow(4, TripStepKind.ToStop, 0, 0, 5, true)]
    [DataRow(10, TripStepKind.Ride, 0, 1, 20, false)]
    [DataRow(19, TripStepKind.Ride, 0, 1, 20, true)]
    [DataRow(21, TripStepKind.Transfer, 1, 2, 25, false)]
    [DataRow(30, TripStepKind.Ride, 1, 3, 35, false)]
    [DataRow(36, TripStepKind.LastWalk, -1, 4, 38, false)]
    [DataRow(40, TripStepKind.Done, -1, 5, 38, false)]
    public void Step_TransferTrip_FollowsTheClock(int minutes, TripStepKind kind, int ride, int leg, int actionMinutes, bool urgent)
    {
        // Arrange
        TripProgress progress = Started();

        // Act
        TripStep step = progress.Step(At(minutes), Lead);

        // Assert
        Assert.AreEqual(new TripStep(kind, ride, leg, At(actionMinutes), urgent), step);
    }

    [TestMethod]
    [DataRow(-8, new[] { TripLegState.Current, TripLegState.Upcoming, TripLegState.Upcoming, TripLegState.Upcoming, TripLegState.Upcoming })]
    [DataRow(10, new[] { TripLegState.Done, TripLegState.Current, TripLegState.Upcoming, TripLegState.Upcoming, TripLegState.Upcoming })]
    [DataRow(23, new[] { TripLegState.Done, TripLegState.Done, TripLegState.Current, TripLegState.Upcoming, TripLegState.Upcoming })]
    [DataRow(40, new[] { TripLegState.Done, TripLegState.Done, TripLegState.Done, TripLegState.Done, TripLegState.Done })]
    public void StateOf_TransferTrip_MarksLegsBehindDoneTheCurrentOneAndTheRestUpcoming(int minutes, TripLegState[] expected)
    {
        // Arrange
        TripProgress progress = Started();
        TripStep step = progress.Step(At(minutes), Lead);

        // Act
        TripLegState[] states = [.. Enumerable.Range(0, progress.Trip.Legs.Count).Select(step.StateOf)];

        // Assert
        CollectionAssert.AreEqual(expected, states);
    }

    [TestMethod]
    [DataRow(TripStepKind.GetReady, false, false)]
    [DataRow(TripStepKind.ToStop, false, true)]
    [DataRow(TripStepKind.Ride, false, true)]
    [DataRow(TripStepKind.Transfer, false, true)]
    [DataRow(TripStepKind.Transfer, true, false)]
    [DataRow(TripStepKind.LastWalk, false, false)]
    [DataRow(TripStepKind.Done, false, false)]
    public void CanMiss_EachStep_OnlyWhileHeadingToOrOnARideNotWaitingForAPlan(TripStepKind kind, bool awaitsReplan, bool expected)
    {
        // Arrange
        TripStep step = new(kind, 0, 0, At(0), IsUrgent: false) { AwaitsReplan = awaitsReplan };

        // Act
        bool canMiss = step.CanMiss;

        // Assert
        Assert.AreEqual(expected, canMiss);
    }

    [TestMethod]
    [DataRow(TripStepKind.GetReady, 0, false, true)]
    [DataRow(TripStepKind.GetReady, -1, false, false)]
    [DataRow(TripStepKind.GetReady, 0, true, false)]
    [DataRow(TripStepKind.ToStop, 0, false, true)]
    [DataRow(TripStepKind.Ride, 0, false, true)]
    [DataRow(TripStepKind.Transfer, 1, false, true)]
    [DataRow(TripStepKind.Transfer, 1, true, false)]
    [DataRow(TripStepKind.LastWalk, -1, false, false)]
    [DataRow(TripStepKind.Done, -1, false, false)]
    public void CanFollowVehicle_EachStep_IncludesTheUpcomingRideBeforeLeaving(TripStepKind kind, int ride, bool awaitsReplan, bool expected)
    {
        // Arrange
        TripStep step = new(kind, ride, 0, At(0), IsUrgent: false) { AwaitsReplan = awaitsReplan };

        // Act
        bool canFollow = step.CanFollowVehicle;

        // Assert
        Assert.AreEqual(expected, canFollow);
    }

    [TestMethod]
    public void Advance_ClockOnly_RaisesEachAlertOnceWhenItIsDue()
    {
        // Act
        List<(TripAlert?, DateTimeOffset?)> alerts = Tick(Started(), from: -10, to: 40).Alerts;

        // Assert: head out at the lead, leave by 8:00, and get off 2 minutes before each stop.
        CollectionAssert.AreEqual(new (TripAlert?, DateTimeOffset?)[]
        {
            (TripAlert.HeadOut, At(-5)),
            (TripAlert.LeaveNow, At(0)),
            (TripAlert.GetOffSoon, At(18)),
            (TripAlert.GetOffSoon, At(33)),
        }, alerts);
    }

    [TestMethod]
    public void Start_WithinTheLead_SkipsHeadOut()
    {
        // Act
        List<(TripAlert?, DateTimeOffset?)> alerts = Tick(Started(at: -3), from: -3, to: 1).Alerts;

        // Assert
        CollectionAssert.AreEqual(new (TripAlert?, DateTimeOffset?)[] { (TripAlert.LeaveNow, At(0)) }, alerts);
    }

    [TestMethod]
    [DataRow(2, null)]
    [DataRow(3, TripAlert.RideChanged)]
    public void Advance_NextRideMovesAfterHeadOut_AlertsOnlyForThreeMinutesOrMore(int lateMinutes, TripAlert? expected)
    {
        // Arrange
        TripProgress progress = Started().Advance(null, Lead, At(-5)).Progress;
        PredictionSnapshot late = Snapshot(-4, Live("a", Fourteen, 5 + lateMinutes, "14-1"));

        // Act
        TripUpdate update = progress.Advance(late, Lead, At(-4));
        TripUpdate after = update.Progress.Advance(null, Lead, At(-3));

        // Assert
        Assert.AreEqual(expected, update.Alert);
        Assert.AreEqual(At(lateMinutes), update.Progress.Trip.LeaveBy);
        Assert.IsNull(after.Alert);
    }

    [TestMethod]
    public void Advance_NextRideMovesBeforeHeadOut_OnlyMovesTheCountdown()
    {
        // Act
        TripUpdate update = Started().Advance(Snapshot(-9, Live("a", Fourteen, 15, "14-1")), Lead, At(-9));

        // Assert
        Assert.IsNull(update.Alert);
        Assert.AreEqual(At(10), update.Progress.Trip.LeaveBy);
    }

    [TestMethod]
    public void Advance_LateFirstRide_MovesTheTransferWithOneConnectionChangedAlert()
    {
        // Arrange: the 14 leaves at 8:10, so its rider is off at 8:25 and reaches "b" at 8:27, after the 8:25 30.
        TripProgress progress = Started().Advance(null, Lead, At(0)).Progress;
        PredictionSnapshot late = Snapshot(2, Live("a", Fourteen, 10, "14-1"), Live("b", Thirty, 25, "30-1"), Live("b", Thirty, 40, "30-2"));

        // Act
        TripUpdate update = progress.Advance(late, Lead, At(2));
        TripUpdate after = update.Progress.Advance(null, Lead, At(3));

        // Assert
        Assert.AreEqual(TripAlert.ConnectionChanged, update.Alert);
        Assert.AreEqual("30-2", update.Progress.Rides[1].TripId);
        Assert.AreEqual(At(53), update.Progress.Trip.Arrives);
        Assert.IsNull(after.Alert);
    }

    [TestMethod]
    public void Advance_LaterTimeWhereTheRideGetsOff_MovesGettingOffAndTheTransfer()
    {
        // Arrange: on the 14 since 8:05; it now reaches "a-off" at 8:24.
        TripProgress progress = Started().Advance(null, Lead, At(6)).Progress;
        PredictionSnapshot late = Snapshot(12, Live("a-off", Fourteen, 24, "14-1"), Live("b", Thirty, 25, "30-1"), Live("b", Thirty, 40, "30-2"));

        // Act
        TripUpdate update = progress.Advance(late, Lead, At(12));
        TripUpdate getOff = update.Progress.Advance(null, Lead, At(22));

        // Assert
        Assert.AreEqual(TripAlert.ConnectionChanged, update.Alert);
        Assert.AreEqual(At(24), update.Progress.Rides[0].Alights);
        Assert.AreEqual(At(5), update.Progress.Rides[0].Departs);
        Assert.AreEqual("30-2", update.Progress.Rides[1].TripId);
        Assert.AreEqual((TripAlert?)TripAlert.GetOffSoon, getOff.Alert);
        Assert.AreEqual(At(22), getOff.DueAt);
    }

    [TestMethod]
    public void Advance_CanceledRideBeforeLeaving_TakesTheNextDepartureAndMovesLeaveBy()
    {
        // Arrange
        TripProgress progress = Started().Advance(null, Lead, At(-5)).Progress;
        PredictionSnapshot canceled = Snapshot(-2,
            Live("a", Fourteen, 5, "14-1", ArrivalStatus.Canceled), Live("a", Fourteen, 20, "14-2"), Live("b", Thirty, 40, "30-2"));

        // Act
        TripUpdate update = progress.Advance(canceled, Lead, At(-2));

        // Assert
        Assert.AreEqual(TripAlert.ConnectionChanged, update.Alert);
        Assert.AreEqual("14-2", update.Progress.Rides[0].TripId);
        Assert.AreEqual(At(15), update.Progress.Trip.LeaveBy);
        Assert.AreEqual("30-2", update.Progress.Rides[1].TripId);
    }

    [TestMethod]
    public void Advance_NoConnectionLeft_NeedsReplan()
    {
        // Arrange: off the 14 at 8:50, but the last 30 leaves "b" at 8:40.
        PredictionSnapshot late = Snapshot(-8, Live("a", Fourteen, 35, "14-1"));

        // Act
        TripUpdate update = Started().Advance(late, Lead, At(-8));

        // Assert
        Assert.IsTrue(update.Progress.NeedsReplan);
        Assert.IsNull(update.Alert);
    }

    [TestMethod]
    public void Advance_LeaveAtTrip_NeverBoardsBeforeTheChosenTime()
    {
        // Arrange: leave at 8:00, planned at 7:30. The 14 at 8:05 is canceled; another runs at 7:50.
        TripProgress progress = TripProgress.Start(LiveTrip.Scheduled(TransferTrip()), new TripTime(TripTimeMode.LeaveAt, At(0)), null, Lead, At(-30));
        PredictionSnapshot canceled = Snapshot(-30, Live("a", Fourteen, 5, "14-1", ArrivalStatus.Canceled), Live("a", Fourteen, -10, "14-0"));

        // Act
        TripUpdate update = progress.Advance(canceled, Lead, At(-30));

        // Assert
        Assert.AreEqual("14-2", update.Progress.Rides[0].TripId);
        Assert.AreEqual(At(15), update.Progress.Trip.LeaveBy);
        Assert.IsNull(update.Alert);
    }

    [TestMethod]
    public void Advance_DeparturePasses_CountsTheRideAsTakenWithoutACheck()
    {
        // Act
        TripProgress progress = Started().Advance(null, Lead, At(6)).Progress;

        // Assert: the next check is the one before getting off, not one to see that the bus left.
        Assert.AreEqual(TripStepKind.Ride, progress.Step(At(6), Lead).Kind);
        Assert.AreEqual(At(5), progress.Rides[0].Departs);
        Assert.AreEqual(At(15), progress.NextCheck(Lead, At(6), lastAttempt: At(6)));
    }

    [TestMethod]
    public void Advance_TransferLeftBeforeTheFirstRideGotThere_TakesTheNextDeparture()
    {
        // Arrange: on the 14 since 8:05; the first news after the 8:25 30 left says the 14 reaches "a-off" at 8:27.
        TripProgress progress = Started().Advance(null, Lead, At(6)).Progress;

        // Act
        TripUpdate update = progress.Advance(Snapshot(26, Live("a-off", Fourteen, 27, "14-1")), Lead, At(26));

        // Assert: the rider couldn't have caught the 8:25, so it isn't counted as taken; they're headed for the 8:40.
        Assert.AreEqual("30-2", update.Progress.Rides[1].TripId);
        Assert.AreEqual(At(53), update.Progress.Trip.Arrives);
        Assert.AreEqual(TripStepKind.Ride, update.Progress.Step(At(26), Lead).Kind);
    }

    [TestMethod]
    public void Advance_OlderSnapshot_IsIgnored()
    {
        // Arrange
        TripProgress progress = Started().Advance(Snapshot(-6, Live("a", Fourteen, 6, "14-1")), Lead, At(-6)).Progress;

        // Act
        TripProgress after = progress.Advance(Snapshot(-7, Live("a", Fourteen, 9, "14-1")), Lead, At(-5)).Progress;

        // Assert
        Assert.AreEqual(At(-6), after.KnownAt);
        Assert.AreEqual(At(6), after.Rides[0].Departs);
    }

    [TestMethod]
    public void Advance_AlertsDueWhileAsleep_RaisesOnlyTheLatest()
    {
        // Act: nothing ran from 7:50 to 8:19.
        TripUpdate update = Started().Advance(null, Lead, At(19));
        TripUpdate after = update.Progress.Advance(null, Lead, At(20));

        // Assert
        Assert.AreEqual(TripAlert.GetOffSoon, update.Alert);
        Assert.AreEqual(At(18), update.DueAt);
        Assert.IsNull(after.Alert);
    }

    [TestMethod]
    public void Missed_OnTheAssumedRide_RetimesFromTheNextDepartureWithoutNewData()
    {
        // Arrange
        TripProgress progress = Started().Advance(null, Lead, At(6)).Progress;

        // Act
        TripProgress missed = progress.Missed(At(6));

        // Assert: the next 14 at 8:20 (off at 8:35) makes the 8:40 30.
        Assert.AreEqual("14-2", missed.Rides[0].TripId);
        Assert.AreEqual("30-2", missed.Rides[1].TripId);
        Assert.AreEqual(new TripStep(TripStepKind.ToStop, 0, 0, At(20), false), missed.Step(At(6), Lead));
        Assert.IsFalse(missed.NeedsReplan);
        Assert.IsNull(missed.Advance(null, Lead, At(7)).Alert);
    }

    [TestMethod]
    public void Missed_NoLaterDeparture_NeedsReplan()
    {
        // Arrange: on the last 30.
        TripProgress progress = Started().Missed(At(6)).Advance(null, Lead, At(41)).Progress;

        // Act
        TripProgress missed = progress.Missed(At(41));

        // Assert
        Assert.IsTrue(missed.NeedsReplan);
    }

    [TestMethod]
    public void Missed_NoLaterDeparture_WaitsAtTheStopWithoutGetOffSoon()
    {
        // Arrange: on the last 30, which the rider says they missed; they're back at "b" from 8:37.
        TripProgress stranded = Started().Missed(At(6)).Advance(null, Lead, At(41)).Progress.Missed(At(41));

        // Act
        List<(TripAlert?, DateTimeOffset?)> alerts = Tick(stranded, from: 41, to: 60).Alerts;

        // Assert: no countdown, no Get off soon for the missed 30, and no checks: the next plan decides.
        Assert.AreEqual(new TripStep(TripStepKind.Transfer, 1, 2, At(37), false) { AwaitsReplan = true }, stranded.Step(At(41), Lead));
        Assert.IsEmpty(alerts);
        Assert.IsNull(stranded.NextAlertAt(Lead));
        Assert.IsNull(stranded.NextCheck(Lead, At(41)));
        Assert.AreSame(stranded, stranded.Missed(At(42)));
    }

    [TestMethod]
    public void Advance_NoConnectionBeforeLeaving_DoesNotSendTheRiderOff()
    {
        // Arrange: the 14 now leaves "a" at 8:35, too late for the last 30.
        TripProgress stranded = Started().Advance(Snapshot(-8, Live("a", Fourteen, 35, "14-1")), Lead, At(-8)).Progress;

        // Act: the clock runs past when the rider would have left, and past the 14's stop to get off (8:50).
        (TripProgress later, List<(TripAlert?, DateTimeOffset?)> alerts) = Tick(stranded, from: -7, to: 60);

        // Assert: no Head out or Leave now; the rider waits at the origin for a new plan.
        Assert.IsEmpty(alerts);
        Assert.IsNull(later.LeftAt);
        Assert.IsTrue(later.Step(At(31), Lead) is { Kind: TripStepKind.GetReady, AwaitsReplan: true });
        Assert.IsNull(later.NextCheck(Lead, At(-7)));
    }

    [TestMethod]
    public void Missed_WithoutTripIds_StaysMissedAfterItsPredictionMoves()
    {
        // Arrange: a feed without trip IDs. The 8:05 14 runs at 8:07, and the rider misses it.
        TripItinerary trip = new(
        [
            Walk(0, 5),
            new RideLeg(At(5), At(20), [Line("a", Fourteen, new PlannedDeparture(At(5), At(20), false, null), new PlannedDeparture(At(20), At(35), false, null))]),
            Walk(20, 23),
        ], At(0), At(23));
        TripProgress missed = TripProgress.Start(LiveTrip.Compute(trip, [], At(-10))!, default, null, Lead, At(-10))
            .Advance(Snapshot(4, NoTripId(7, scheduled: 5), NoTripId(20, scheduled: 20)), Lead, At(4)).Progress
            .Missed(At(6));

        // Act: the missed 14 is now due at 8:09, and the 8:20 is canceled.
        TripProgress next = missed.Advance(
            Snapshot(8, NoTripId(9, scheduled: 5), NoTripId(20, scheduled: 20, ArrivalStatus.Canceled), NoTripId(30, scheduled: 30)), Lead, At(8)).Progress;

        // Assert: it takes the 8:30, not the 14 the rider just missed.
        Assert.AreEqual(At(20), missed.Rides[0].Departs);
        Assert.AreEqual(At(30), next.Rides[0].Departs);
    }

    [TestMethod]
    public void ReplanTime_LeaveAtStillAhead_KeepsItUntilTheRiderLeaves()
    {
        // Arrange: a trip to leave at 8:30, started at 7:50.
        TripProgress progress = TripProgress.Start(LiveTrip.Compute(TransferTrip(30), [], At(-10))!,
            new TripTime(TripTimeMode.LeaveAt, At(30)), null, Lead, At(-10));

        // Act
        TripTime before = progress.ReplanTime(At(-10));
        TripTime after = progress.ReplanTime(At(31));

        // Assert
        Assert.AreEqual(new TripTime(TripTimeMode.LeaveAt, At(30)), before);
        Assert.AreEqual(default, after);
    }

    [TestMethod]
    public void Missed_BeforeLeaving_ChangesNothing()
    {
        // Arrange
        TripProgress progress = Started();

        // Act / Assert
        Assert.AreSame(progress, progress.Missed(At(-5)));
    }

    [TestMethod]
    public void NextCheck_TargetTrip_ChecksOnceBeforeEachDecision()
    {
        // Arrange: Go at 8:00 to leave at 8:10. Walk 6 minutes, ride 15, change with a 9-minute wait, ride 10, walk 5.
        TripItinerary itinerary = new(
        [
            Walk(10, 16),
            new RideLeg(At(16), At(31), [Line("a", Fourteen, Planned(16, 31, "14-1"))]),
            Walk(31, 33),
            new RideLeg(At(40), At(50), [Line("b", Thirty, Planned(40, 50, "30-1"))]),
            Walk(50, 55),
        ], At(10), At(55));
        TripProgress progress = TripProgress.Start(LiveTrip.Compute(itinerary, [], Now)!, default, Snapshot(0), Lead, Now);

        // Act
        List<DateTimeOffset> checks = Simulate(progress, Now);

        // Assert: head out, leave, board after the long walk, get off, the long wait, get off.
        CollectionAssert.AreEqual(new[] { At(4), At(8), At(13), At(26), At(37), At(45) }, checks);
    }

    [TestMethod]
    public void NextCheck_ShortWalkAndWait_SkipsTheirChecks()
    {
        // Act
        List<DateTimeOffset> checks = Simulate(Started(), At(-10));

        // Assert: the 5-minute walk and the 5-minute wait at "b" need no check of their own.
        CollectionAssert.AreEqual(new[] { At(-6), At(-2), At(15), At(30) }, checks);
    }

    [TestMethod]
    public void NextCheck_FreshData_SkipsACheckDueWithinTwoMinutes()
    {
        // Arrange
        TripProgress progress = Started(at: -7, Snapshot(-7));

        // Act / Assert: the head-out check at 7:54 is covered by data from 7:53.
        Assert.AreEqual(At(-2), progress.NextCheck(Lead, At(-7)));
    }

    [TestMethod]
    public void NextCheck_OverdueAfterSleep_IsDueNow()
    {
        // Act
        DateTimeOffset? next = Started().NextCheck(Lead, At(16));

        // Assert: the get-off check at 8:15 is late, but getting off at 8:20 is still ahead.
        Assert.AreEqual(At(16), next);
    }

    [TestMethod]
    public void NextCheck_FailedAttempt_IsNotRetriedAtOnce()
    {
        // Act: the check before leaving failed.
        DateTimeOffset? next = Started().NextCheck(Lead, At(-2), lastAttempt: At(-2));

        // Assert: it waits for the next decision's check.
        Assert.AreEqual(At(15), next);
    }

    [TestMethod]
    public void NextCheck_ScheduledTrip_WaitsForItsFirstDecision()
    {
        // Arrange: tomorrow's trip, started today.
        TripItinerary tomorrow = TransferTrip(offset: 1440);
        TripProgress progress = TripProgress.Start(LiveTrip.Scheduled(tomorrow), new TripTime(TripTimeMode.LeaveAt, At(1440)), null, Lead, Now);

        // Act
        DateTimeOffset? next = progress.NextCheck(Lead, Now);

        // Assert
        Assert.AreEqual(At(1440 - 6), next);
        Assert.IsNull(progress.Advance(null, Lead, Now).Alert);
    }

    [TestMethod]
    public void NextCheck_WalkOnlyTrip_HasNothingToCheck()
    {
        // Arrange
        TripItinerary walk = new([Walk(0, 20)], Now, At(20));

        // Act
        TripProgress progress = TripProgress.Start(LiveTrip.Compute(walk, [], Now)!, default, null, Lead, Now);

        // Assert
        Assert.IsNull(progress.NextCheck(Lead, Now));
        Assert.AreEqual(TripStepKind.LastWalk, progress.Step(Now, Lead).Kind);
    }

    [TestMethod]
    public void NextAlertAt_AsAlertsAreSent_MovesToTheNextOne()
    {
        // Arrange
        TripProgress progress = Started();

        // Act
        DateTimeOffset? first = progress.NextAlertAt(Lead);
        DateTimeOffset? second = progress.Advance(null, Lead, At(-5)).Progress.NextAlertAt(Lead);
        DateTimeOffset? third = progress.Advance(null, Lead, At(0)).Progress.NextAlertAt(Lead);
        DateTimeOffset? last = progress.Advance(null, Lead, At(34)).Progress.NextAlertAt(Lead);

        // Assert
        Assert.AreEqual(At(-5), first);
        Assert.AreEqual(At(0), second);
        Assert.AreEqual(At(18), third);
        Assert.IsNull(last);
    }

    [TestMethod]
    public void WatchedStops_AsTheTripGoesOn_DropsStopsThatCanNoLongerChangeIt()
    {
        // Arrange
        TripProgress progress = Started();

        // Act / Assert
        CollectionAssert.AreEqual(new[] { "a", "a-off", "b", "b-off" }, progress.WatchedStops(At(-10)).ToArray());
        CollectionAssert.AreEqual(new[] { "a-off", "b", "b-off" }, progress.WatchedStops(At(10)).ToArray());
        CollectionAssert.AreEqual(new[] { "b-off" }, progress.WatchedStops(At(30)).ToArray());
        Assert.IsEmpty(progress.WatchedStops(At(36)));
    }

    [TestMethod]
    public void Advance_SnapshotWithOtherStops_KeepsOnlyTheTripsStops()
    {
        // Act
        TripProgress progress = Started().Advance(Snapshot(-9, Live("a", Fourteen, 5, "14-1"), Live("elsewhere", Fourteen, 5, "14-9")), Lead, At(-9)).Progress;

        // Assert
        Assert.AreEqual("a", progress.Known.Single().StopId);
    }

    [TestMethod]
    [DataRow(42, false)]
    [DataRow(43, true)]
    public void IsOver_FiveMinutesAfterArriving_IsTrue(int minutes, bool over)
    {
        // Act / Assert
        Assert.AreEqual(over, Started().IsOver(At(minutes)));
    }

    [TestMethod]
    public void Continue_StrandedAtTheTransfer_KeepsTheRideTakenAndWhatTheRiderWasTold()
    {
        // Arrange: on the 14 since 8:05, which now reaches "a-off" at 8:45, after the last 30 leaves "b" at 8:40.
        TripProgress stranded = Started().Advance(null, Lead, At(6)).Progress
            .Advance(Snapshot(12, Live("a-off", Fourteen, 45, "14-1")), Lead, At(12)).Progress;
        TripPlan plan = new([new TripItinerary(
            [Walk(47, 49), new RideLeg(At(55), At(65), [Line("c", Thirty, Planned(55, 65, "30-9"))]), Walk(65, 70)], At(47), At(70))], []);

        // Act
        GeoPoint? from = stranded.ReplanFrom(At(12));
        TripProgress? continued = stranded.Continue(plan, At(12));

        // Assert: planned again from "b"; the rider walks on from "b" to the new stop's 8:55 30.
        Assert.IsTrue(stranded.NeedsReplan);
        Assert.AreEqual(new GeoPoint(43, -88), from);
        Assert.IsNotNull(continued);
        Assert.IsFalse(continued.NeedsReplan);
        CollectionAssert.AreEqual(new[] { "14-1", "30-9" }, continued.Rides.Select(r => r.TripId).ToArray());
        Assert.AreEqual(At(45), continued.Rides[0].Alights);
        Assert.AreEqual(At(55), continued.Rides[1].Departs);
        Assert.AreEqual(At(70), continued.Trip.Arrives);
        Assert.AreEqual((true, true, 1, (DateTimeOffset?)At(55)),
            (continued.HeadOutSent, continued.LeaveNowSent, continued.AnnouncedRide, continued.AnnouncedDeparture));
        Assert.AreEqual(TripStepKind.Ride, continued.Step(At(12), Lead).Kind);
        Assert.IsNull(continued.Advance(null, Lead, At(13)).Alert);
    }

    [TestMethod]
    public void Continue_BeforeLeaving_ReplacesTheTripFromTheOrigin()
    {
        // Arrange: the 14 now leaves "a" at 8:35, too late for the last 30.
        TripProgress stranded = Started().Advance(Snapshot(-8, Live("a", Fourteen, 35, "14-1")), Lead, At(-8)).Progress;
        TripItinerary other = new([Walk(0, 10), new RideLeg(At(10), At(30), [Line("c", Thirty, Planned(10, 30, "30-9"))]), Walk(30, 33)], At(0), At(33));

        // Act
        GeoPoint? from = stranded.ReplanFrom(At(-8));
        TripProgress? continued = stranded.Continue(new TripPlan([other], []), At(-8));

        // Assert
        Assert.IsNull(from);
        Assert.IsNotNull(continued);
        Assert.AreSame(other, continued.Itinerary);
        Assert.AreEqual("30-9", continued.Rides.Single().TripId);
        Assert.AreEqual(At(0), continued.Trip.LeaveBy);
        Assert.IsNull(continued.LeftAt);
        Assert.IsFalse(continued.HeadOutSent);
    }

    [TestMethod]
    public void Continue_MissedTheLastRide_PlansFromThatRidesStop()
    {
        // Arrange: on the last 30, which the rider says they missed.
        TripProgress stranded = Started().Missed(At(6)).Advance(null, Lead, At(41)).Progress.Missed(At(41));
        TripItinerary other = new([Walk(41, 42), new RideLeg(At(45), At(52), [Line("c", Thirty, Planned(45, 52, "30-9"))]), Walk(52, 55)], At(41), At(55));

        // Act
        TripProgress? continued = stranded.Continue(new TripPlan([other], []), At(41));

        // Assert: the 14 stays taken; the missed 30 gives way to the new one, after the new plan's 1-minute walk (leg 3).
        Assert.IsNotNull(continued);
        CollectionAssert.AreEqual(new[] { "14-2", "30-9" }, continued.Rides.Select(r => r.TripId).ToArray());
        Assert.AreEqual(new TripStep(TripStepKind.Transfer, 1, 3, At(45), false), continued.Step(At(41), Lead));
    }

    [TestMethod]
    public void Continue_PlanWithNoTripLeft_ReturnsNull()
    {
        // Arrange
        TripProgress stranded = Started().Advance(Snapshot(-8, Live("a", Fourteen, 35, "14-1")), Lead, At(-8)).Progress;

        // Act / Assert
        Assert.IsNull(stranded.Continue(new TripPlan([], []), At(-8)));
    }

    [TestMethod]
    [DataRow(0, 5)]
    [DataRow(30, 5)]
    [DataRow(60, 4)]
    [DataRow(270, 1)]
    [DataRow(300, 0)]
    [DataRow(400, 0)]
    public void MinutesUntil_CountingDown_RoundsUpToTheAction(int seconds, int minutes)
    {
        // Arrange
        TripStep step = new(TripStepKind.GetReady, 0, 0, At(5), false);

        // Act / Assert
        Assert.AreEqual(minutes, step.MinutesUntil(Now.AddSeconds(seconds)));
    }

    [TestMethod]
    public void Serialize_StartedMissedTrip_RoundTrips()
    {
        // Arrange
        TripProgress progress = Started()
            .Advance(Snapshot(2, Live("a", Fourteen, 5, "14-1"), Live("b", Thirty, 25, "30-1")), Lead, At(2)).Progress
            .Missed(At(6));

        // Act
        string json = JsonSerializer.Serialize(progress, TripProgressJsonContext.Default.TripProgress);
        TripProgress copy = JsonSerializer.Deserialize(json, TripProgressJsonContext.Default.TripProgress)!;

        // Assert
        CollectionAssert.AreEqual(progress.Rides.ToArray(), copy.Rides.ToArray());
        CollectionAssert.AreEqual(progress.MissedTrips.ToArray(), copy.MissedTrips.ToArray());
        CollectionAssert.AreEqual(progress.Known.ToArray(), copy.Known.ToArray());
        Assert.AreEqual(progress.KnownAt, copy.KnownAt);
        Assert.AreEqual(progress.LeftAt, copy.LeftAt);
        Assert.AreEqual(progress.NotBefore, copy.NotBefore);
        Assert.AreEqual((progress.HeadOutSent, progress.LeaveNowSent, progress.GetOffAlerts, progress.AnnouncedRide, progress.AnnouncedDeparture),
            (copy.HeadOutSent, copy.LeaveNowSent, copy.GetOffAlerts, copy.AnnouncedRide, copy.AnnouncedDeparture));
        Assert.AreEqual(progress.Trip.Arrives, copy.Trip.Arrives);
        Assert.AreEqual(progress.Step(At(7), Lead), copy.Step(At(7), Lead));
    }

    /// <summary>The transfer trip, timed and started at <paramref name="at"/> minutes: leave 8:00, arrive 8:38.</summary>
    private static TripProgress Started(int at = -10, PredictionSnapshot? snapshot = null) =>
        TripProgress.Start(LiveTrip.Compute(TransferTrip(), [], At(at))!, default, snapshot, Lead, At(at));

    /// <summary>Advances once a minute, collecting each alert raised.</summary>
    private static (TripProgress Progress, List<(TripAlert?, DateTimeOffset?)> Alerts) Tick(TripProgress progress, int from, int to)
    {
        List<(TripAlert?, DateTimeOffset?)> alerts = [];
        for (int minute = from; minute <= to; minute++)
        {
            TripUpdate update = progress.Advance(null, Lead, At(minute));
            progress = update.Progress;
            if (update.Alert is not null)
            {
                alerts.Add((update.Alert, update.DueAt));
            }
        }

        return (progress, alerts);
    }

    /// <summary>Checks whenever <see cref="TripProgress.NextCheck"/> says to, with unchanged times, until it says there's nothing left.</summary>
    private static List<DateTimeOffset> Simulate(TripProgress progress, DateTimeOffset now)
    {
        List<DateTimeOffset> checks = [];
        while (progress.NextCheck(Lead, now) is { } next)
        {
            checks.Add(next);
            Assert.IsLessThanOrEqualTo(15, checks.Count);
            now = next;
            progress = progress.Advance(Snapshot((int)(next - Now).TotalMinutes), Lead, now).Progress;
        }

        return checks;
    }

    /// <summary>
    /// Walk 5 minutes to "a", ride the 14 (8:05 or 8:20, 15 minutes) to "a-off", walk 2 minutes to "b",
    /// ride the 30 (8:25 or 8:40, 10 minutes) to "b-off", and walk 3 minutes: as planned, 8:00 to 8:38.
    /// </summary>
    private static TripItinerary TransferTrip(int offset = 0) => new(
    [
        Walk(offset, offset + 5),
        new RideLeg(At(offset + 5), At(offset + 20), [Line("a", Fourteen, Planned(offset + 5, offset + 20, "14-1"), Planned(offset + 20, offset + 35, "14-2"))]),
        Walk(offset + 20, offset + 22),
        new RideLeg(At(offset + 25), At(offset + 35), [Line("b", Thirty, Planned(offset + 25, offset + 35, "30-1"), Planned(offset + 40, offset + 50, "30-2"))]),
        Walk(offset + 35, offset + 38),
    ], At(offset), At(offset + 38));

    private static PredictionSnapshot Snapshot(int minutes, params Arrival[] arrivals) => new(arrivals, At(minutes));

    private static DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);

    private static WalkLeg Walk(int from, int to) => new(At(from), At(to), (to - from) * 80, []);

    private static PlannedDeparture Planned(int departs, int alights, string tripId) => new(At(departs), At(alights), IsRealTime: false, tripId);

    private static RideLine Line(string stopId, TransitRoute route, params PlannedDeparture[] departures) => new(
        route,
        North,
        new TransitStop(stopId, $"Stop {stopId}", new GeoPoint(43, -88)),
        new TransitStop($"{stopId}-off", $"Stop {stopId}-off", new GeoPoint(43.1, -88)),
        4,
        [],
        departures);

    private static Arrival Live(string stopId, TransitRoute route, int minutes, string? tripId, ArrivalStatus status = ArrivalStatus.Scheduled) =>
        new(stopId, route.Id, route.DisplayId, North.Id, "Downtown", At(minutes), status, IsRealTime: true, tripId);

    /// <summary>A real-time 14 at "a" from a feed without trip IDs, due at <paramref name="minutes"/> and timetabled at <paramref name="scheduled"/>.</summary>
    private static Arrival NoTripId(int minutes, int scheduled, ArrivalStatus status = ArrivalStatus.Scheduled) =>
        Live("a", Fourteen, minutes, null, status) with { ScheduledTime = At(scheduled) };
}
