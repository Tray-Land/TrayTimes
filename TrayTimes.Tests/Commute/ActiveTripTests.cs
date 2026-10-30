using System.Text.Json;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class ActiveTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", "#ff0000", "Bus");
    private static readonly FavoritePlace Home = FavoritePlace.ForKind(PlaceKind.Home, new Place("Home address", 43, -88));
    private static readonly FavoritePlace Gym = new("custom-1", PlaceKind.Custom, "Gym", new Place("Gym address", 43.1, -88), "S:9");

    [TestMethod]
    public void Serialize_TripWithItsLegAndDestination_RoundTrips()
    {
        // Arrange
        ActiveTrip trip = Trip();

        // Act
        string json = JsonSerializer.Serialize(trip, ActiveTripJsonContext.Default.ActiveTrip);
        ActiveTrip copy = JsonSerializer.Deserialize(json, ActiveTripJsonContext.Default.ActiveTrip)!;

        // Assert
        Assert.IsTrue(copy.IsValid());
        Assert.AreEqual(trip.Leg, copy.Leg);
        Assert.AreEqual(trip.Destination, copy.Destination);
        Assert.AreEqual(new GeoPoint(43.1, -88), copy.Leg.To.Location);
        CollectionAssert.AreEqual(trip.Progress.Rides.ToArray(), copy.Progress.Rides.ToArray());
        CollectionAssert.AreEqual(((WalkLeg)trip.Progress.Itinerary.Legs[0]).Path.ToArray(), ((WalkLeg)copy.Progress.Itinerary.Legs[0]).Path.ToArray());
        Assert.AreEqual(trip.Progress.Trip.Arrives, copy.Progress.Trip.Arrives);
        Assert.DoesNotContain("IsAlreadyThere", json);
    }

    [TestMethod]
    public void IsValid_RideChoiceForALineThatIsNotThere_IsFalse()
    {
        // Arrange
        ActiveTrip trip = Trip();
        ActiveTrip damaged = trip with { Progress = trip.Progress with { Rides = [trip.Progress.Rides[0] with { Line = 3 }] } };

        // Act / Assert
        Assert.IsFalse(damaged.IsValid());
    }

    [TestMethod]
    public void IsValid_FewerChoicesThanRides_IsFalse()
    {
        // Arrange
        ActiveTrip trip = Trip();
        ActiveTrip damaged = trip with { Progress = trip.Progress with { Rides = [] } };

        // Act / Assert
        Assert.IsFalse(damaged.IsValid());
    }

    [TestMethod]
    public void IsValid_RideGettingOffBeforeItBoards_IsFalse()
    {
        // Arrange
        ActiveTrip trip = Trip();
        RideChoice ride = trip.Progress.Rides[0];
        ActiveTrip damaged = trip with { Progress = trip.Progress with { Rides = [ride with { Alights = ride.Departs.AddMinutes(-1) }] } };

        // Act / Assert
        Assert.IsFalse(damaged.IsValid());
    }

    [TestMethod]
    public void IsValid_KnownDepartureAtTheEndOfTime_IsFalse()
    {
        // Arrange: a time that would overflow once a ride is added to it.
        ActiveTrip trip = Trip();
        Arrival far = new("a", "R:14", "14", "0", "Downtown", DateTimeOffset.MaxValue, TripId: "14-1");
        ActiveTrip damaged = trip with { Progress = trip.Progress with { Known = [far] } };

        // Act / Assert
        Assert.IsFalse(damaged.IsValid());
    }

    [TestMethod]
    [DataRow(2, -1, -1)]
    [DataRow(0, 1, -1)]
    [DataRow(0, -1, 1)]
    public void IsValid_CounterPastTheRides_IsFalse(int getOffAlerts, int announcedRide, int strandedRide)
    {
        // Arrange
        ActiveTrip trip = Trip();
        ActiveTrip damaged = trip with
        {
            Progress = trip.Progress with { GetOffAlerts = getOffAlerts, AnnouncedRide = announcedRide, StrandedRide = strandedRide },
        };

        // Act / Assert
        Assert.IsFalse(damaged.IsValid());
    }

    [TestMethod]
    public void Deserialize_FileWithoutTheLeg_IsNotValid()
    {
        // Arrange
        string json = JsonSerializer.Serialize(Trip(), ActiveTripJsonContext.Default.ActiveTrip).Replace("\"Leg\":", "\"Other\":", StringComparison.Ordinal);

        // Act
        ActiveTrip? copy = JsonSerializer.Deserialize(json, ActiveTripJsonContext.Default.ActiveTrip);

        // Assert
        Assert.IsFalse(copy?.IsValid() ?? false);
    }

    /// <summary>Walk 5 minutes, ride the 14 for 15, walk 3: to a searched stop that isn't saved.</summary>
    private static ActiveTrip Trip()
    {
        RideLine line = new(Fourteen, new TransitDirection("0", "Northbound"), new TransitStop("a", "Stop a", new GeoPoint(43, -88)),
            new TransitStop("a-off", "Stop a-off", new GeoPoint(43.1, -88)), 4, [new GeoPoint(43, -88), new GeoPoint(43.1, -88)],
            [new PlannedDeparture(Now.AddMinutes(5), Now.AddMinutes(20), IsRealTime: true, "14-1")]);
        TripItinerary itinerary = new(
        [
            new WalkLeg(Now, Now.AddMinutes(5), 400, [new GeoPoint(43, -88), new GeoPoint(43.001, -88)]),
            new RideLeg(Now.AddMinutes(5), Now.AddMinutes(20), [line]),
            new WalkLeg(Now.AddMinutes(20), Now.AddMinutes(23), 200, []),
        ], Now, Now.AddMinutes(23));
        TripProgress progress = TripProgress.Start(LiveTrip.Compute(itinerary, [], Now)!, default, null, 5, Now);
        return new ActiveTrip(progress, new CommuteLeg(Home, Home.Location, Gym, IsGuess: false), new TripDestination(Gym.Location, Gym.StopId, Persist: false));
    }
}
