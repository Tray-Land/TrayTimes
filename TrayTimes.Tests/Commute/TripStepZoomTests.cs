using TrayTimes.Commute;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class TripStepZoomTests
{
    [TestMethod]
    public void Tap_StepNotOnTheRidersRide_ShowsItsLegEveryTime()
    {
        // Arrange
        TripStepZoom zoom = new();

        // Act
        TripStepView first = zoom.Tap(0, onRide: false);
        TripStepView again = zoom.Tap(0, onRide: false);

        // Assert
        Assert.AreEqual(TripStepView.Leg, first);
        Assert.AreEqual(TripStepView.Leg, again);
    }

    [TestMethod]
    public void Tap_RidersRide_AlternatesTheVehicleAndTheWholeRide()
    {
        // Arrange
        TripStepZoom zoom = new();

        // Act
        TripStepView[] taps = [.. Enumerable.Range(0, 5).Select(_ => zoom.Tap(1, onRide: true))];

        // Assert
        CollectionAssert.AreEqual(
            new[] { TripStepView.Vehicle, TripStepView.Leg, TripStepView.Vehicle, TripStepView.Leg, TripStepView.Vehicle },
            taps);
    }

    [TestMethod]
    public void Tap_AnotherStepBetween_StartsTheRidersRideOverOnTheVehicle()
    {
        // Arrange: the vehicle, then the whole ride, then a walk.
        TripStepZoom zoom = new();
        zoom.Tap(1, onRide: true);
        zoom.Tap(1, onRide: true);
        zoom.Tap(2, onRide: false);

        // Act
        TripStepView view = zoom.Tap(1, onRide: true);

        // Assert
        Assert.AreEqual(TripStepView.Vehicle, view);
    }

    [TestMethod]
    public void Tap_AnotherStepWhileOnTheVehicle_ShowsThatStepsLegThenTheVehicleAgain()
    {
        // Arrange
        TripStepZoom zoom = new();
        zoom.Tap(1, onRide: true);

        // Act
        TripStepView other = zoom.Tap(0, onRide: false);
        TripStepView back = zoom.Tap(1, onRide: true);

        // Assert
        Assert.AreEqual(TripStepView.Leg, other);
        Assert.AreEqual(TripStepView.Vehicle, back);
    }

    [TestMethod]
    public void Tap_AfterReset_ShowsTheVehicleInsteadOfZoomingOut()
    {
        // Arrange: the vehicle is showing, so the next tap would zoom out.
        TripStepZoom zoom = new();
        zoom.Tap(1, onRide: true);
        zoom.Reset();

        // Act
        TripStepView view = zoom.Tap(1, onRide: true);

        // Assert
        Assert.AreEqual(TripStepView.Vehicle, view);
    }

    [TestMethod]
    public void Tap_RideStopsBeingTheRidersRide_ShowsItsWholeLeg()
    {
        // Arrange: the rider got off while the vehicle was showing.
        TripStepZoom zoom = new();
        zoom.Tap(1, onRide: true);

        // Act
        TripStepView view = zoom.Tap(1, onRide: false);

        // Assert
        Assert.AreEqual(TripStepView.Leg, view);
    }
}
