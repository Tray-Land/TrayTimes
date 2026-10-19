using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class BoardCenterTests
{
    private static readonly GeoPoint Here = new(43.05, -88.0);
    private static readonly FavoritePlace Home = FavoritePlace.ForKind(PlaceKind.Home, new Place("Fixture home", 43, -88));
    private static readonly FavoritePlace Work = FavoritePlace.ForKind(PlaceKind.Work, new Place("Fixture work", 43.08, -88));
    private static readonly SavedStop Oak = new("S:1", "Oak & 5th", 43.01, -88, "R:14", "14", null, "0", "Northbound", false);

    [TestMethod]
    public void Resolve_NearYouWithALocation_StaysNearYou()
    {
        // Act
        BoardCenter center = new NearYou().Resolve(Here, [Home, Work], [Oak]);

        // Assert
        Assert.AreEqual(new NearYou(), center);
    }

    [TestMethod]
    public void Resolve_NearYouWithoutALocation_FallsBackToNearHome()
    {
        // Act
        BoardCenter center = new NearYou().Resolve(null, [Work, Home], [Oak]);

        // Assert
        Assert.AreEqual(new NearPlace(Home), center);
    }

    [TestMethod]
    public void Resolve_NearYouWithoutALocationOrHome_FallsBackToAllSavedStops()
    {
        // Act
        BoardCenter center = new NearYou().Resolve(null, [Work], [Oak]);

        // Assert
        Assert.AreEqual(new AllSavedStops(), center);
    }

    [TestMethod]
    public void Resolve_NearYouWithNothingToFallBackTo_StaysNearYouToAskForLocation()
    {
        // Act
        BoardCenter center = new NearYou().Resolve(null, [Work], []);

        // Assert
        Assert.AreEqual(new NearYou(), center);
    }

    [TestMethod]
    public void Resolve_PickedCenterWithoutALocation_StaysThere()
    {
        // Arrange
        BoardCenter[] picked = [new NearPlace(Work), new NearStop(Oak), new AllSavedStops()];

        // Act & Assert
        foreach (BoardCenter center in picked)
        {
            Assert.AreEqual(center, center.Resolve(null, [Home, Work], [Oak]));
        }
    }

    [TestMethod]
    public void PointFrom_EachCenter_IsWhereTheBoardLooksForLines()
    {
        // Act & Assert
        Assert.AreEqual(Here, new NearYou().PointFrom(Here));
        Assert.IsNull(new NearYou().PointFrom(null));
        Assert.AreEqual(Work.Location, new NearPlace(Work).PointFrom(Here));
        Assert.AreEqual(new GeoPoint(43.01, -88), new NearStop(Oak).PointFrom(Here));
        Assert.IsNull(new AllSavedStops().PointFrom(Here));
    }
}
