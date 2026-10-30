using TrayTimes.Commute;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class TripSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Update_FutureTrip_KeepsEveryLegTimeAndIgnoresCurrentDepartures()
    {
        // Arrange
        TripItinerary ride = Ride("future", 1445, 1465);
        TripItinerary itinerary = new([
            new WalkLeg(Now.AddDays(1), Now.AddDays(1).AddMinutes(5), 300, []),
            .. ride.Legs,
            new WalkLeg(Now.AddDays(1).AddMinutes(25), Now.AddDays(1).AddMinutes(30), 300, [])],
            Now.AddDays(1), Now.AddDays(1).AddMinutes(30));
        TripSelection selection = new();
        TripTime time = new(TripTimeMode.LeaveAt, itinerary.Departs);
        Arrival unrelated = new("future", "future", "future", "0", "Downtown", Now.AddMinutes(10), TripId: "running-now");

        // Act
        selection.Update(new TripPlan([itinerary], []), [unrelated], Now, time: time);

        // Assert
        LiveTrip selected = selection.Selected!;
        Assert.IsFalse(selected.IsLive);
        Assert.AreEqual(itinerary.Departs, selected.LeaveBy);
        Assert.AreEqual(itinerary.Arrives, selected.Arrives);
        CollectionAssert.AreEqual(itinerary.Legs.Select(l => l.Starts).ToArray(), selected.Legs.Select(l => l.Starts).ToArray());
        CollectionAssert.AreEqual(itinerary.Legs.Select(l => l.Ends).ToArray(), selected.Legs.Select(l => l.Ends).ToArray());
        Assert.IsEmpty(selected.FirstRideDepartures([unrelated], 6));
        Assert.IsEmpty(PlaceBoard.BoardingStopIds(new TripPlan([itinerary], []), i => time.UsesLiveTimes(i, Now)));
    }

    [TestMethod]
    public void Update_FutureTripGetsNear_UsesLiveDeparturesWithoutMovingBeforeItsRequestedTime()
    {
        // Arrange
        TripItinerary itinerary = Ride("future", 60, 80);
        TripPlan plan = new([itinerary], []);
        TripTime time = new(TripTimeMode.LeaveAt, itinerary.Departs);
        TripSelection selection = new();
        Arrival early = new("future", "future", "future", "0", "Downtown", Now.AddMinutes(45), TripId: "early");
        Arrival delayed = early with { PredictedTime = Now.AddMinutes(65), TripId = "future", IsRealTime = true };
        selection.Update(plan, [early, delayed], Now, time: time);
        Assert.IsFalse(selection.Selected!.IsLive);

        // Act: exactly 30 minutes before the planned departure.
        selection.Update(plan, [early, delayed], Now.AddMinutes(30), time: time);

        // Assert
        Assert.IsTrue(selection.Selected!.IsLive);
        Assert.AreEqual(delayed, selection.Selected.Legs.OfType<LiveRide>().Single().Departure);
        Assert.AreEqual(Now.AddMinutes(65), selection.Selected.LeaveBy);
        Assert.AreEqual(Now.AddMinutes(85), selection.Selected.Arrives);
        CollectionAssert.AreEqual(new[] { "future" }, PlaceBoard.BoardingStopIds(plan, i => time.UsesLiveTimes(i, Now.AddMinutes(30))).ToArray());
    }

    [TestMethod]
    public void Update_ArriveBy_SortsLatestDepartureFirstAndExcludesLateOptions()
    {
        // Arrange
        TripItinerary first = Ride("early", 60, 80), latest = Ride("latest", 70, 90), late = Ride("late", 75, 95);
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([first, late, latest], []), [], Now,
            time: new TripTime(TripTimeMode.ArriveBy, Now.AddMinutes(90)));

        // Assert
        CollectionAssert.AreEqual(new[] { latest, first }, selection.Options.Select(t => t.Itinerary).ToArray());
        Assert.AreSame(latest, selection.Selected?.Itinerary);
    }

    [TestMethod]
    public void Update_FutureWalkOnly_KeepsItsPlannedStartAndArrival()
    {
        // Arrange
        TripItinerary walk = new([new WalkLeg(Now.AddDays(1), Now.AddDays(1).AddMinutes(25), 2000, [])],
            Now.AddDays(1), Now.AddDays(1).AddMinutes(25));
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([walk], []), [], Now, time: new TripTime(TripTimeMode.ArriveBy, walk.Arrives));

        // Assert
        Assert.AreEqual(walk.Departs, selection.Selected?.LeaveBy);
        Assert.AreEqual(walk.Arrives, selection.Selected?.Arrives);
        Assert.IsFalse(selection.Selected!.IsLive);
    }

    [TestMethod]
    public void Update_LeaveAt_ExcludesTripsBeforeItsConstraint()
    {
        // Arrange
        TripItinerary early = Ride("early", 60, 80), later = Ride("later", 70, 90);
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([early, later], []), [], Now, time: new TripTime(TripTimeMode.LeaveAt, Now.AddMinutes(65)));

        // Assert
        Assert.AreSame(later, selection.Selected?.Itinerary);
        Assert.HasCount(1, selection.Options);
    }

    [TestMethod]
    public void Update_MoreThanThreeRidesAndAWalk_ShowsOnlyTheWatchedRidesAndTheWalk()
    {
        // Arrange
        TripItinerary[] rides = [.. Enumerable.Range(0, 4).Select(i => Ride($"r{i}", 5, 20 + i))];
        TripItinerary walk = Walk();
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([walk, .. rides], []), [], Now);

        // Assert
        CollectionAssert.AreEqual(new[] { rides[0], rides[1], rides[2], walk }, selection.Options.Select(t => t.Itinerary).ToArray());
        Assert.AreSame(rides[0], selection.Selected?.Itinerary);
    }

    [TestMethod]
    public void Update_PreferredCardOption_SelectsItInsteadOfTheFirstOption()
    {
        // Arrange
        TripItinerary first = Ride("a", 5, 30), preferred = Ride("b", 10, 35);
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([first, preferred], []), [], Now, preferred);

        // Assert
        Assert.AreSame(preferred, selection.Selected?.Itinerary);
    }

    [TestMethod]
    public void Select_ThenBatchRefresh_KeepsSelectionAndUpdatesItsTimes()
    {
        // Arrange
        TripItinerary first = Ride("a", 5, 20), second = Ride("b", 10, 30);
        TripPlan plan = new([first, second], []);
        TripSelection selection = new();
        selection.Update(plan, [], Now);

        // Act
        selection.Select(second);
        selection.Update(plan, [new Arrival("b", "b", "b", "0", "Downtown", Now.AddMinutes(15), TripId: "b")], Now);

        // Assert
        Assert.AreSame(second, selection.Selected?.Itinerary);
        Assert.AreEqual(Now.AddMinutes(35), selection.Selected?.Arrives);
    }

    [TestMethod]
    public void Update_SelectedOptionNoLongerFeasible_SelectsAnotherLiveOption()
    {
        // Arrange
        TripItinerary first = Ride("a", 5, 20), second = Ride("b", 15, 30);
        TripPlan plan = new([first, second], []);
        TripSelection selection = new();
        selection.Update(plan, [], Now, first);

        // Act
        selection.Update(plan, [], Now.AddMinutes(10));

        // Assert
        Assert.HasCount(1, selection.Options);
        Assert.AreSame(second, selection.Selected?.Itinerary);
    }

    [TestMethod]
    public void Update_NewPlan_ReplacesOldOptionsAndSelection()
    {
        // Arrange
        TripSelection selection = new();
        selection.Update(new TripPlan([Ride("old", 5, 20)], []), [], Now);
        TripItinerary replacement = Ride("new", 10, 25);

        // Act
        selection.Update(new TripPlan([replacement], []), [], Now);

        // Assert
        Assert.AreSame(replacement, selection.Selected?.Itinerary);
        Assert.HasCount(1, selection.Options);
    }

    [TestMethod]
    public void Update_NoFeasibleTransit_KeepsTheWalkOption()
    {
        // Arrange
        TripItinerary walk = Walk();
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([Ride("gone", -10, 0), walk], []), [], Now);

        // Assert
        Assert.AreSame(walk, selection.Selected?.Itinerary);
        Assert.AreEqual(Now.AddMinutes(25), selection.Selected?.Arrives);
    }

    [TestMethod]
    public void Update_NoFeasibleOption_HasNoSelection()
    {
        // Arrange
        TripSelection selection = new();

        // Act
        selection.Update(new TripPlan([Ride("gone", -10, 0)], []), [], Now);

        // Assert
        Assert.IsEmpty(selection.Options);
        Assert.IsNull(selection.Selected);
    }

    [TestMethod]
    public void Select_OptionNotInTheView_Throws()
    {
        // Arrange
        TripSelection selection = new();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => selection.Select(Ride("other", 5, 20)));
    }

    private static TripItinerary Ride(string id, int from, int to)
    {
        RideLine line = new(new TransitRoute(id, id, id, null), new TransitDirection("0", "Downtown"),
            new TransitStop(id, id, new GeoPoint(43, -88)), null, 2, [],
            [new PlannedDeparture(Now.AddMinutes(from), Now.AddMinutes(to), false, id)]);
        return new TripItinerary([new RideLeg(Now.AddMinutes(from), Now.AddMinutes(to), [line])], Now.AddMinutes(from), Now.AddMinutes(to));
    }

    private static TripItinerary Walk() => new([new WalkLeg(Now, Now.AddMinutes(25), 2000, [])], Now, Now.AddMinutes(25));
}
