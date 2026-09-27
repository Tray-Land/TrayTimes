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
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(1), isDue: true), "Stop", Lead, Now);

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
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(3), isDue: true)], Lead, later);

        // Assert
        Assert.AreEqual(later, step.Bus.DueSince);
        Assert.IsFalse(step.Ended);
    }

    [TestMethod]
    public void Advance_FiveMinutesAfterDue_Ends()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(1), isDue: true), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([], Lead, Now + TrackedBus.PassedGrace);

        // Assert
        Assert.IsTrue(step.Ended);
        Assert.IsNull(step.Alert);
    }

    [TestMethod]
    public void Advance_JustUnderFiveMinutesAfterDue_KeepsTracking()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(1), isDue: true), "Stop", Lead, Now);

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
    public void FindIn_KnownVehicle_MatchesByVehicleNotTime()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), vehicleId: "5301"), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([
            CreateArrival(Now.AddMinutes(10), vehicleId: "5400"),
            CreateArrival(Now.AddMinutes(25), vehicleId: "5301"),
        ]);

        // Assert
        Assert.AreEqual("5301", match?.VehicleId);
    }

    [TestMethod]
    public void FindIn_KnownVehicleGone_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), vehicleId: "5301"), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateArrival(Now.AddMinutes(10), vehicleId: "5400")]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindIn_NoVehicle_MatchesNearestTimeOnSameRouteAndDirection()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), vehicleId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([
            CreateArrival(Now.AddMinutes(10), routeId: "GRE"),
            CreateArrival(Now.AddMinutes(10), directionId: "SOUTH"),
            CreateArrival(Now.AddMinutes(18)),
            CreateArrival(Now.AddMinutes(12)),
        ]);

        // Assert
        Assert.AreEqual(Now.AddMinutes(12), match?.PredictedTime);
    }

    [TestMethod]
    public void FindIn_NoVehicleAndNothingClose_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), vehicleId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateArrival(Now.AddMinutes(10) + TrackedBus.MatchTolerance + TimeSpan.FromMinutes(1))]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindIn_NoVehicleAndOnlyScheduledTime_ReturnsNull()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(10), vehicleId: null), "Stop", Lead, Now);

        // Act
        Arrival? match = bus.FindIn([CreateArrival(Now.AddMinutes(10), vehicleId: null) with { IsRealTime = false }]);

        // Assert
        Assert.IsNull(match);
    }

    [TestMethod]
    public void Advance_NoVehicleThenOneAssigned_AdoptsVehicle()
    {
        // Arrange
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(20), vehicleId: null), "Stop", Lead, Now);

        // Act
        TrackingStep step = bus.Advance([CreateArrival(Now.AddMinutes(21), vehicleId: "5301")], Lead, Now.AddMinutes(1));

        // Assert
        Assert.AreEqual("5301", step.Bus.VehicleId);
    }

    [TestMethod]
    [DataRow(15.0, 60.0)]
    [DataRow(10.0, 30.0)]
    [DataRow(7.0, 30.0)]
    [DataRow(5.0, 10.0)]
    [DataRow(-2.0, 10.0)]
    public void RefreshInterval_ByMinutesAway_MatchesCadence(double minutesAway, double expectedSeconds)
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
        TrackedBus bus = TrackedBus.Start(CreateArrival(Now.AddMinutes(1), isDue: true), "Stop", Lead, Now);

        // Act
        int minutes = bus.MinutesAway(Now.AddMinutes(-10));

        // Assert
        Assert.AreEqual(0, minutes);
    }

    private static Arrival CreateArrival(
        DateTimeOffset predicted,
        bool isDue = false,
        string? vehicleId = "5301",
        string routeId = "14",
        string directionId = "NORTH",
        ArrivalStatus status = ArrivalStatus.Scheduled) =>
        new("100", routeId, routeId, directionId, "Downtown", vehicleId, predicted, isDue, false, false, status);
}
