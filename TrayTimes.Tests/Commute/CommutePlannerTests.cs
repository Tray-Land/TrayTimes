using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class CommutePlannerTests
{
    private const double Lon = -88.0;

    private static readonly DateTimeOffset Morning = new(2026, 9, 28, 8, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset Evening = new(2026, 9, 28, 17, 0, 0, TimeSpan.FromHours(-5));

    // Home at the south end of a north-south line, work 8 km north, school 4 km north.
    private static readonly FavoritePlace Home = Fixed(PlaceKind.Home, 43.000);
    private static readonly FavoritePlace Work = Fixed(PlaceKind.Work, 43.080);
    private static readonly FavoritePlace School = Fixed(PlaceKind.School, 43.040);
    private static readonly FavoritePlace Gym = new("gym", PlaceKind.Custom, "Gym", new Place("Gym", 43.060, Lon));

    [TestMethod]
    public void Plan_AtHome_HeadsToWork()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work, School], new GeoPoint(43.0005, Lon), Evening);

        // Assert
        Assert.IsNotNull(leg);
        Assert.AreSame(Home, leg.From);
        Assert.AreSame(Work, leg.To);
        Assert.AreEqual(Home.Location, leg.Origin);
        Assert.IsFalse(leg.IsGuess);
    }

    [TestMethod]
    public void Plan_AtHomeWithoutWork_HeadsToSchool()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Gym, School], Home.Location, Morning);

        // Assert
        Assert.AreSame(School, leg?.To);
    }

    [TestMethod]
    public void Plan_AtHomeWithOnlyCustomPlace_HeadsThere()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Gym], Home.Location, Morning);

        // Assert
        Assert.AreSame(Gym, leg?.To);
    }

    [TestMethod]
    public void Plan_AtWork_HeadsHomeFromWork()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work], new GeoPoint(43.0802, Lon), Morning);

        // Assert
        Assert.IsNotNull(leg);
        Assert.AreSame(Work, leg.From);
        Assert.AreSame(Home, leg.To);
        Assert.AreEqual(Work.Location, leg.Origin);
    }

    [TestMethod]
    public void Plan_AwayFromEveryPlace_HeadsHomeFromCurrentLocation()
    {
        // Arrange
        GeoPoint here = new(43.020, Lon);

        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work], here, Morning);

        // Assert
        Assert.IsNotNull(leg);
        Assert.IsNull(leg.From);
        Assert.AreSame(Home, leg.To);
        Assert.AreEqual(here, leg.Origin);
    }

    [TestMethod]
    public void Plan_NoLocationInMorning_GuessesHomeToWork()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work], null, Morning);

        // Assert
        Assert.IsNotNull(leg);
        Assert.AreSame(Home, leg.From);
        Assert.AreSame(Work, leg.To);
        Assert.IsTrue(leg.IsGuess);
    }

    [TestMethod]
    public void Plan_NoLocationInEvening_GuessesWorkToHome()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work], null, Evening);

        // Assert
        Assert.IsNotNull(leg);
        Assert.AreSame(Work, leg.From);
        Assert.AreSame(Home, leg.To);
        Assert.AreEqual(Work.Location, leg.Origin);
    }

    [TestMethod]
    public void Plan_NoHome_ReturnsNull()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Work, School], new GeoPoint(43.020, Lon), Morning);

        // Assert
        Assert.IsNull(leg);
    }

    [TestMethod]
    public void Plan_AtHomeWithNowhereElse_ReturnsNull()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home], Home.Location, Morning);

        // Assert
        Assert.IsNull(leg);
    }

    [TestMethod]
    public void Plan_ChosenDestination_OverridesGuess()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work, Gym], Home.Location, Morning, destination: Gym);

        // Assert
        Assert.AreSame(Gym, leg?.To);
        Assert.IsFalse(leg!.IsAlreadyThere);
    }

    [TestMethod]
    public void Plan_ChosenDestinationIsWhereRiderIs_IsAlreadyThere()
    {
        // Act
        CommuteLeg? leg = CommutePlanner.Plan([Home, Work], Home.Location, Morning, destination: Home);

        // Assert
        Assert.IsTrue(leg?.IsAlreadyThere);
    }

    [TestMethod]
    public void SharedRoutes_ReturnsRouteDirectionsServingBothEnds_IgnoringCase()
    {
        // Arrange
        IndexedStop[] origin = [Stop("o1", 43.010, ("10", "NORTHBOUND"), ("12", "EAST"))];
        IndexedStop[] destination = [Stop("d1", 43.080, ("10", "northbound"), ("12", "WEST"))];

        // Act
        IReadOnlySet<RouteDirection> shared = CommutePlanner.SharedRoutes(origin, destination);

        // Assert
        Assert.HasCount(1, shared);
        Assert.IsTrue(shared.Contains(new RouteDirection("10", "NORTHBOUND")));
    }

    [TestMethod]
    public void DirectRoutes_TwoWayRoute_KeepsOnlyDirectionTowardDestination()
    {
        // Arrange: route 10 serves both ends in both directions, from opposite sides of the street.
        IndexedStop[] origin = [Stop("o-nb", 43.010, ("10", "NB")), Stop("o-sb", 43.010, ("10", "SB"))];
        IndexedStop[] destination = [Stop("d-nb", 43.080, ("10", "NB")), Stop("d-sb", 43.080, ("10", "SB"))];
        Dictionary<string, IReadOnlyList<RoutePattern>> patterns = new() { ["10"] = [Line("10", "NB", northbound: true), Line("10", "SB", northbound: false)] };

        // Act
        IReadOnlySet<RouteDirection> direct = CommutePlanner.DirectRoutes(origin, destination, patterns);

        // Assert
        Assert.HasCount(1, direct);
        Assert.IsTrue(direct.Contains(new RouteDirection("10", "NB")));
    }

    [TestMethod]
    public void DirectRoutes_ShapeUnknown_DoesNotConfirmSharedRoute()
    {
        // Arrange
        IndexedStop[] origin = [Stop("o", 43.010, ("10", "NB"))];
        IndexedStop[] destination = [Stop("d", 43.080, ("10", "NB"))];

        // Act
        IReadOnlySet<RouteDirection> direct = CommutePlanner.DirectRoutes(origin, destination, new Dictionary<string, IReadOnlyList<RoutePattern>>());

        // Assert
        Assert.HasCount(0, direct);
    }

    [TestMethod]
    public void RunsFromTo_BoardingPointOffTheLine_ReturnsFalse()
    {
        // Arrange: about 800 m east of the line.
        GeoPoint offRoute = new(43.010, Lon + 0.01);

        // Act
        bool runs = CommutePlanner.RunsFromTo(Line("10", "NB", northbound: true), [offRoute], [new GeoPoint(43.080, Lon)]);

        // Assert
        Assert.IsFalse(runs);
    }

    [TestMethod]
    public void ArrivalAtDestination_SeveralStops_PicksEarliestIncludingTheWalk()
    {
        // Arrange: getting off at "near" is 2 minutes sooner on the bus but a 10 minute walk; "far" is a 1 minute walk.
        TripStop[] downstream = [new("near", Morning.AddMinutes(20)), new("far", Morning.AddMinutes(22))];
        Dictionary<string, double> walks = new() { ["near"] = 600, ["far"] = 60 };

        // Act
        DateTimeOffset? eta = CommutePlanner.ArrivalAtDestination(downstream, walks);

        // Assert
        Assert.AreEqual(Morning.AddMinutes(23), eta);
    }

    [TestMethod]
    public void ArrivalAtDestination_StopWithoutKnownWalk_IsIgnored()
    {
        // Arrange
        TripStop[] downstream = [new("unknown", Morning.AddMinutes(10)), new("known", Morning.AddMinutes(30))];
        Dictionary<string, double> walks = new() { ["known"] = 120 };

        // Act
        DateTimeOffset? eta = CommutePlanner.ArrivalAtDestination(downstream, walks);

        // Assert
        Assert.AreEqual(Morning.AddMinutes(32), eta);
    }

    [TestMethod]
    public void ArrivalAtDestination_BusNeverReachesANearStop_ReturnsNull()
    {
        // Act
        DateTimeOffset? eta = CommutePlanner.ArrivalAtDestination([], new Dictionary<string, double> { ["near"] = 60 });

        // Assert
        Assert.IsNull(eta);
    }

    private static FavoritePlace Fixed(PlaceKind kind, double lat) => FavoritePlace.ForKind(kind, new Place(kind.ToString(), lat, Lon));

    private static IndexedStop Stop(string id, double lat, params (string Route, string Direction)[] routes) =>
        new(id, id, lat, Lon, routes.Select(r => new StopRouteInfo(r.Route, r.Route, null, r.Direction, r.Direction)).ToList());

    /// <summary>A straight north-south shape through 43.000–43.100, a point every ~110 m.</summary>
    private static RoutePattern Line(string routeId, string directionId, bool northbound)
    {
        IEnumerable<int> steps = northbound ? Enumerable.Range(0, 101) : Enumerable.Range(0, 101).Reverse();
        return new RoutePattern($"{routeId}-{directionId}", routeId, directionId, steps.Select(i => new GeoPoint(43.000 + (i * 0.001), Lon)).ToList());
    }
}
