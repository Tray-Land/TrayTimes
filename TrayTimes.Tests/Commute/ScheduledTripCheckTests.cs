using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

/// <summary>Step 03 review: a trip scheduled for a later day, checked early (Resume, a network retry).</summary>
[TestClass]
public sealed class ScheduledTripCheckTests
{
    private const int Lead = 5;
    private static readonly DateTimeOffset Today = new(2026, 10, 19, 23, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tomorrow = Today.AddDays(1);
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", "#ff0000");
    private static readonly TransitDirection North = new("0", "Northbound");

    [TestMethod]
    [Ignore("Step 03 defect: an Arrive by trip for a later day is pulled to today by an early check (same trip ID listed today)")]
    public void Advance_ArriveByTomorrowCheckedTodayWithTheSameTripId_KeepsTomorrowsDeparture()
    {
        // Arrange: the 14 leaves tomorrow at 23:20; GTFS trip IDs repeat daily, so today's 23:33 run has the same ID.
        TripProgress progress = Scheduled();
        Arrival todays = new("a", Fourteen.Id, "14", North.Id, "Downtown", Today.AddMinutes(28), ArrivalStatus.Scheduled, IsRealTime: false, "14-1");
        PredictionSnapshot snapshot = new([todays], Today);

        // Act
        TripUpdate update = progress.Advance(snapshot, Lead, Today);

        // Assert
        Assert.AreEqual(Tomorrow.AddMinutes(15), update.Progress.Rides[0].Departs);
        Assert.IsGreaterThan(Today.AddHours(20), update.Progress.Trip.LeaveBy);
    }

    private static TripProgress Scheduled()
    {
        DateTimeOffset leaves = Tomorrow.AddMinutes(15);
        RideLine line = new(
            Fourteen,
            North,
            new TransitStop("a", "Stop a", new GeoPoint(43, -88)),
            new TransitStop("a-off", "Stop a-off", new GeoPoint(43.1, -88)),
            4,
            [],
            [new PlannedDeparture(leaves, leaves.AddMinutes(15), IsRealTime: false, "14-1")]);
        TripItinerary itinerary = new(
        [
            new WalkLeg(leaves.AddMinutes(-5), leaves, 400, []),
            new RideLeg(leaves, leaves.AddMinutes(15), [line]),
        ], leaves.AddMinutes(-5), leaves.AddMinutes(15));
        LiveTrip trip = LiveTrip.Compute(itinerary, [], Today)!;
        return TripProgress.Start(trip, new TripTime(TripTimeMode.ArriveBy, Tomorrow.AddMinutes(30)), null, Lead, Today);
    }
}
