using TrayTimes.Tracking;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Tracking;

[TestClass]
public sealed class TrackedBusTests
{
    private const int Lead = 5;
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Start_OutsideLeadWindow_HasNotAlerted()
    {
        // Act
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(12)), "Farwell & North", Lead, Now);

        // Assert
        Assert.IsNull(bus.AlertedTime);
        Assert.IsNull(bus.DueSince);
        Assert.AreEqual("Farwell & North", bus.StopName);
        Assert.AreEqual("5301", bus.TripId);
    }

    [TestMethod]
    public void Start_InsideLeadWindow_SkipsHeadOutAlert()
    {
        // Act
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(3)), "Stop", Lead, Now);

        // Assert
        Assert.AreEqual(Now.AddMinutes(3), bus.AlertedTime);
    }

    [TestMethod]
    public void Start_WhenDue_RecordsDueSince()
    {
        // Act
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Assert
        Assert.AreEqual(Now, bus.DueSince);
    }

    [TestMethod]
    public void Advance_BusReachesLeadTime_RaisesHeadOut()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(12)), "Stop", Lead, Now);
        DateTimeOffset later = Now.AddMinutes(7);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(12))], Lead, later);

        // Assert
        Assert.AreEqual(TrackingAlert.HeadOut, step.Alert);
        Assert.AreEqual(Now.AddMinutes(12), step.Bus.AlertedTime);
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_OutsideLeadTime_RaisesNothing()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(20)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(21))], Lead, Now.AddMinutes(1));

        // Assert
        Assert.IsNull(step.Alert);
        Assert.AreEqual(Now.AddMinutes(21), step.Bus.PredictedTime);
    }

    [TestMethod]
    public void Advance_FetchFailedButLeadTimeReached_StillRaisesHeadOut()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(12)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance(null, Lead, Now.AddMinutes(7));

        // Assert
        Assert.AreEqual(TrackingAlert.HeadOut, step.Alert);
    }

    [TestMethod]
    public void Advance_AfterHeadOutPredictionSlips_RaisesLater()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(4)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(8))], Lead, Now.AddMinutes(1));

        // Assert
        Assert.AreEqual(TrackingAlert.Later, step.Alert);
        Assert.AreEqual(Now.AddMinutes(8), step.Bus.AlertedTime);
    }

    [TestMethod]
    public void Advance_AfterHeadOutPredictionMovesUp_RaisesEarlier()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(5)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(2))], Lead, Now.AddSeconds(30));

        // Assert
        Assert.AreEqual(TrackingAlert.Earlier, step.Alert);
    }

    [TestMethod]
    public void Advance_SmallShiftAfterHeadOut_RaisesNothing()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(5)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(7))], Lead, Now.AddSeconds(30));

        // Assert
        Assert.IsNull(step.Alert);
    }

    [TestMethod]
    public void Advance_ShiftBeforeHeadOut_RaisesNothing()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(20)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(30))], Lead, Now.AddMinutes(1));

        // Assert
        Assert.IsNull(step.Alert);
    }

    [TestMethod]
    public void Advance_Canceled_RaisesCanceledAndEnds()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(12)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(12), status: ArrivalStatus.Canceled)], Lead, Now.AddMinutes(1));

        // Assert
        Assert.AreEqual(TrackingAlert.Canceled, step.Alert);
        Assert.IsTrue(step.Ended);
    }

    [TestMethod]
    public void Advance_BusBecomesDue_RecordsDueSinceAndKeepsTracking()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(3)), "Stop", Lead, Now);
        DateTimeOffset later = Now.AddMinutes(3);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(3))], Lead, later);

        // Assert
        Assert.AreEqual(later, step.Bus.DueSince);
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_FiveMinutesAfterDue_Ends()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now + TrackedBus.PassedGrace);

        // Assert
        Assert.IsTrue(step.Ended);
        Assert.IsNull(step.Alert);
    }

    [TestMethod]
    public void Advance_DueBusLeavesDepartures_RequiresTwoDistinctSuccessfulPollsToMarkPassed()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now.AddMinutes(1));

        // Assert
        Assert.IsFalse(step.Bus.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(60), step.Bus.RefreshInterval(Now.AddMinutes(1)));
        step = step.Bus.Advance([], Lead, Now.AddMinutes(2));
        Assert.IsTrue(step.Bus.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(90), step.Bus.RefreshInterval(Now.AddMinutes(2)));
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_RepeatedCachedMissingSnapshot_DoesNotConfirmPassageTwice()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        DateTimeOffset fetchedAt = Now.AddSeconds(10);
        bus = bus.Advance([], Lead, fetchedAt, fetchedAt).Bus;

        // Act
        bus = bus.Advance([], Lead, Now.AddSeconds(20), fetchedAt).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(30), fetchedAt).Bus;

        // Assert
        Assert.IsFalse(bus.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(60), bus.RefreshInterval(Now.AddSeconds(30)));
        Assert.IsTrue(bus.Advance([], Lead, Now.AddSeconds(40), Now.AddSeconds(40)).Bus.HasPassed);
    }

    [TestMethod]
    public void Advance_CachedMatchingPredictionBecomesDue_StillUpdatesTimeBasedLifecycle()
    {
        // Arrange
        Arrival arrival = CreateArrival(Now.AddSeconds(75));
        TrackedBus bus = TrackedBus.Start(arrival, "Stop", Lead, Now);
        DateTimeOffset fetchedAt = Now.AddSeconds(10);
        bus = bus.Advance([arrival], Lead, fetchedAt, fetchedAt).Bus;
        Assert.IsNull(bus.DueSince);

        // Act
        TrackingStep step = bus.Advance([arrival], Lead, Now.AddSeconds(20), fetchedAt);

        // Assert
        Assert.AreEqual(Now.AddSeconds(20), step.Bus.DueSince);
        Assert.IsFalse(step.Bus.HasPassed);
        Assert.IsFalse(step.Ended);
        Assert.AreEqual(0, step.Bus.MinutesAway(Now.AddSeconds(20)));
    }

    [TestMethod]
    public void Advance_FailedPollBetweenMissingSnapshots_ResetsUnconfirmedEvidence()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(10)).Bus;

        // Act
        bus = bus.Advance(null, Lead, Now.AddSeconds(20)).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(30)).Bus;

        // Assert
        Assert.IsFalse(bus.HasPassed);
        Assert.AreEqual(Now, bus.DueSince);
        Assert.IsTrue(bus.Advance([], Lead, Now.AddSeconds(40)).Bus.HasPassed);
    }

    [TestMethod]
    public void Advance_ReappearanceBetweenMissingSnapshots_RestartsConfirmation()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(10)).Bus;

        // Act
        bus = bus.Advance([CreateArrival(Now)], Lead, Now.AddSeconds(20)).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(30)).Bus;

        // Assert
        Assert.IsFalse(bus.HasPassed);
        Assert.IsTrue(bus.Advance([], Lead, Now.AddSeconds(40)).Bus.HasPassed);
    }

    [TestMethod]
    public void Advance_ConfirmedBusReappears_ClearsPassageWithoutRestartingGraceOrAlerts()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(10)).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(20)).Bus;

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(4))], Lead, Now.AddSeconds(50));

        // Assert
        Assert.IsFalse(step.Bus.HasPassed);
        Assert.AreEqual(Now, step.Bus.DueSince);
        Assert.AreEqual(0, step.Bus.MinutesAway(Now.AddSeconds(50)));
        Assert.AreEqual(TimeSpan.FromSeconds(60), step.Bus.RefreshInterval(Now.AddSeconds(50)));
        Assert.IsNull(step.Alert);
        Assert.IsFalse(step.Ended);
        Assert.IsTrue(step.Bus.Advance([CreateArrival(Now.AddMinutes(4))], Lead, Now + TrackedBus.PassedGrace).Ended);
    }

    [TestMethod]
    public void Advance_FailedPollAfterConfirmation_KeepsPassedDisplayAndGrace()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(10)).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(20)).Bus;

        // Act
        TrackingStep step = bus.Advance(null, Lead, Now.AddSeconds(50));

        // Assert
        Assert.IsTrue(step.Bus.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(90), step.Bus.RefreshInterval(Now.AddSeconds(50)));
        Assert.AreEqual(Now, step.Bus.DueSince);
        Assert.IsFalse(step.Ended);
        Assert.IsNull(step.Alert);
        Assert.IsTrue(step.Bus.Advance(null, Lead, Now + TrackedBus.PassedGrace).Ended);
    }

    [TestMethod]
    public void Advance_MatchingCancellationAfterConfirmation_EndsAndAlerts()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(10)).Bus;
        bus = bus.Advance([], Lead, Now.AddSeconds(20)).Bus;

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now, status: ArrivalStatus.Canceled)], Lead, Now.AddSeconds(50));

        // Assert
        Assert.AreEqual(TrackingAlert.Canceled, step.Alert);
        Assert.IsTrue(step.Ended);
    }

    [TestMethod]
    public void Advance_OlderSnapshotAfterFirstMiss_CannotConfirmPassage()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now), "Stop", Lead, Now);
        bus = bus.Advance([], Lead, Now.AddSeconds(20), Now.AddSeconds(20)).Bus;

        // Act
        TrackingStep step = bus.Advance([], Lead, Now.AddSeconds(30), Now.AddSeconds(10));

        // Assert
        Assert.IsFalse(step.Bus.HasPassed);
        Assert.IsTrue(step.Bus.Advance([], Lead, Now.AddSeconds(40), Now.AddSeconds(40)).Bus.HasPassed);
    }

    [TestMethod]
    public void Advance_FailedPollAfterDue_DoesNotMarkPassed()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance(null, Lead, Now.AddMinutes(1));

        // Assert
        Assert.IsFalse(step.Bus.HasPassed);
    }

    [TestMethod]
    public void Advance_JustUnderFiveMinutesAfterDue_KeepsTracking()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now + TrackedBus.PassedGrace - TimeSpan.FromSeconds(1));

        // Assert
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_NeverDueAndTenMinutesPastLastKnownTime_Ends()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(4)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now.AddMinutes(4) + TrackedBus.LostGrace);

        // Assert
        Assert.IsTrue(step.Ended);
    }

    [TestMethod]
    public void Advance_NeverDueButPredictionKeepsSlipping_KeepsTracking()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(4)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(20))], Lead, Now.AddMinutes(15));

        // Assert
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void FindIn_KnownTrip_MatchesByTripNotTime()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), tripId: "5301"), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([
            CreateArrival(Now.AddMinutes(10), tripId: "5400"),
            CreateArrival(Now.AddMinutes(25), tripId: "5301"),
        ]);

        // Assert
        Assert.AreEqual("5301", match?.TripId);
    }

    [TestMethod]
    public void FindIn_KnownTripGone_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), tripId: "5301"), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateArrival(Now.AddMinutes(10), tripId: "5400")]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindIn_KnownTripOnlyInTimetable_MatchesScheduledDeparture()
    {
        // Arrange: a feed without real-time data still lists the trip by its ID.
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(30), tripId: "t1"), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateScheduled(Now.AddMinutes(30), tripId: "t1"), CreateArrival(Now.AddMinutes(29), tripId: "t2")]);

        // Assert
        Assert.AreEqual("t1", match?.TripId);
    }

    [TestMethod]
    public void FindIn_NoTrip_MatchesNearestTimeOnSameRouteAndDirection()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), tripId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([
            CreateArrival(Now.AddMinutes(10), routeId: "GRE"),
            CreateArrival(Now.AddMinutes(10), directionId: "1"),
            CreateArrival(Now.AddMinutes(18)),
            CreateArrival(Now.AddMinutes(12)),
        ]);

        // Assert
        Assert.AreEqual(Now.AddMinutes(12), match?.PredictedTime);
    }

    [TestMethod]
    public void FindIn_NoTripAndNothingClose_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), tripId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateArrival(Now.AddMinutes(10) + TripMatch.Tolerance + TimeSpan.FromMinutes(1))]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindIn_NoTripAndOnlyScheduledTime_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), tripId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateScheduled(Now.AddMinutes(10))]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void Advance_NoTripThenOneKnown_AdoptsTrip()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(20), tripId: null), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(21), tripId: "5301")], Lead, Now.AddMinutes(1));

        // Assert
        Assert.AreEqual("5301", step.Bus.TripId);
    }

    [TestMethod]
    [DataRow(15.0, 180.0)]
    [DataRow(10.0, 90.0)]
    [DataRow(7.0, 90.0)]
    [DataRow(5.0, 60.0)]
    [DataRow(-2.0, 60.0)]
    public void RefreshInterval_ByMinutesAway_MatchesBudgetCadence(double minutesAway, double expectedSeconds)
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(minutesAway)), "Stop", Lead, Now);

        // Act
        TimeSpan interval = bus.RefreshInterval(Now);

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [TestMethod]
    public void MinutesAway_AfterDue_IsZero()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddSeconds(30)), "Stop", Lead, Now);

        // Act
        int minutes = bus.MinutesAway(Now.AddMinutes(-10));

        // Assert
        Assert.AreEqual(0, minutes);
    }

    [TestMethod]
    public void Start_ScheduledArrival_IsScheduledAndNeverDue()
    {
        // Act
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now), "Stop", Lead, Now);

        // Assert
        Assert.IsTrue(bus.IsScheduled);
        Assert.IsNull(bus.DueSince);
    }

    [TestMethod]
    public void Start_RealTimeArrival_IsNotScheduled()
    {
        // Act
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(12)), "Stop", Lead, Now);

        // Assert
        Assert.IsFalse(bus.IsScheduled);
    }

    [TestMethod]
    [DataRow(60, 5, true)]
    [DataRow(16, 5, true)]
    [DataRow(15, 5, false)]
    [DataRow(5, 5, false)]
    [DataRow(20, 30, false)]
    [DataRow(31, 30, true)]
    public void IsAwaitingRealTime_ScheduledBus_WaitsUntilLookaheadOrLeadTime(int minutesAway, int lead, bool expected)
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(minutesAway)), "Stop", lead, Now);

        // Act
        bool awaiting = bus.IsAwaitingRealTime(lead, Now);

        // Assert
        Assert.AreEqual(expected, awaiting);
    }

    [TestMethod]
    public void IsAwaitingRealTime_RealTimeBus_IsFalse()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(60)), "Stop", Lead, Now);

        // Act
        bool awaiting = bus.IsAwaitingRealTime(Lead, Now);

        // Assert
        Assert.IsFalse(awaiting);
    }

    [TestMethod]
    public void RefreshInterval_ScheduledBus_IsEveryThreeMinutes()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(8)), "Stop", Lead, Now);

        // Act
        TimeSpan interval = bus.RefreshInterval(Now);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(3), interval);
    }

    [TestMethod]
    public void Advance_ScheduledBusGetsRealTimeMatch_AdoptsPredictionAndTrip()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(23), tripId: "5301")], Lead, Now.AddMinutes(5));

        // Assert
        Assert.IsFalse(step.Bus.IsScheduled);
        Assert.AreEqual("5301", step.Bus.TripId);
        Assert.AreEqual(Now.AddMinutes(23), step.Bus.PredictedTime);
    }

    [TestMethod]
    public void Advance_ScheduledTripMatchedById_StaysScheduledAndNeverDue()
    {
        // Arrange: a feed without real-time data; the trip is still listed at its timetable time.
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20), tripId: "t1"), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateScheduled(Now.AddMinutes(20), tripId: "t1")], Lead, Now.AddMinutes(20));

        // Assert
        Assert.IsTrue(step.Bus.IsScheduled);
        Assert.IsNull(step.Bus.DueSince);
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_ScheduledTripCanceled_RaisesCanceledAndEnds()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20), tripId: "t1"), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateScheduled(Now.AddMinutes(20), tripId: "t1") with { Status = ArrivalStatus.Canceled }], Lead, Now.AddMinutes(6));

        // Assert
        Assert.AreEqual(TrackingAlert.Canceled, step.Alert);
        Assert.IsTrue(step.Ended);
    }

    [TestMethod]
    public void Advance_ScheduledBusWithNoRealTimeYet_StaysScheduledAndKeepsTime()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now.AddMinutes(10));

        // Assert
        Assert.IsTrue(step.Bus.IsScheduled);
        Assert.IsFalse(step.Bus.HasPassed);
        Assert.IsFalse(step.Ended);
        Assert.AreEqual(Now.AddMinutes(20), step.Bus.PredictedTime);
    }

    [TestMethod]
    public void Advance_ScheduledBusReachesLeadTimeWithoutRealTime_RaisesHeadOut()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20)), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance(null, Lead, Now.AddMinutes(16));

        // Assert
        Assert.AreEqual(TrackingAlert.HeadOut, step.Alert);
    }

    [TestMethod]
    public void FindListed_ScheduledBus_FindsItsTimetableRow()
    {
        // Arrange
        Arrival scheduled = CreateScheduled(Now.AddMinutes(20));
        TrackedBus bus = TrackedBus.Start(scheduled, "Stop", Lead, Now);

        // Act
        Arrival? listed = bus.FindListed([CreateScheduled(Now.AddMinutes(50)), scheduled]);

        // Assert
        Assert.AreEqual(scheduled, listed);
    }

    [TestMethod]
    public void FindListed_ScheduledBusWithRealTimeMatch_PrefersRealTimeRow()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateScheduled(Now.AddMinutes(20)), "Stop", Lead, Now);
        Arrival live = CreateArrival(Now.AddMinutes(22));

        // Act
        Arrival? listed = bus.FindListed([CreateScheduled(Now.AddMinutes(20)), live]);

        // Assert
        Assert.AreEqual(live, listed);
    }

    [TestMethod]
    public void FindListed_RealTimeBusAndOnlyScheduledRows_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10)), "Stop", Lead, Now);

        // Act
        Arrival? listed = bus.FindListed([CreateScheduled(Now.AddMinutes(10))]);

        // Assert
        Assert.IsNull(listed);
    }

    private static Arrival CreateScheduled(DateTimeOffset time, string? tripId = null) =>
        CreateArrival(time, tripId: tripId) with { IsRealTime = false };

    private static Arrival CreateArrival(
        DateTimeOffset predicted,
        string? tripId = "5301",
        string routeId = "14",
        string directionId = "0",
        ArrivalStatus status = ArrivalStatus.Scheduled) =>
        new("100", routeId, routeId, directionId, "Downtown", predicted, status, TripId: tripId);
}
