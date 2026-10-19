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
    public void RouteDirection_SameIdsInDifferentCase_AreEqual()
    {
        // Arrange
        RouteDirection upper = new("GRE", "NORTH");
        RouteDirection lower = new("gre", "north");

        // Act
        bool equal = upper == lower && upper.GetHashCode() == lower.GetHashCode();

        // Assert
        Assert.IsTrue(equal);
    }

    private static FavoritePlace Fixed(PlaceKind kind, double lat) => FavoritePlace.ForKind(kind, new Place(kind.ToString(), lat, Lon));
}
