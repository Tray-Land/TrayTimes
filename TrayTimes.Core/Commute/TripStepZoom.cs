namespace TrayTimes.Commute;

/// <summary>What the map shows after a tap on a step of the active trip.</summary>
public enum TripStepView
{
    /// <summary>The step's whole leg: a walk from point to point, or a ride from boarding to getting off.</summary>
    Leg,

    /// <summary>The vehicle the rider is on, about where it is now.</summary>
    Vehicle,
}

/// <summary>
/// Picks the map view for each tap on a step of the progress view. A tap shows the step's leg; repeated taps on
/// the ride the rider is on alternate between the vehicle where it is now and the whole ride.
/// </summary>
public sealed class TripStepZoom
{
    private int _leg = -1;
    private TripStepView _shown = TripStepView.Leg;

    /// <summary>The view a tap on the leg at <paramref name="leg"/> asks for.</summary>
    /// <param name="leg">The step's index in the trip's legs.</param>
    /// <param name="onRide">True when the step is the ride the rider is on.</param>
    public TripStepView Tap(int leg, bool onRide)
    {
        _shown = onRide && !(_leg == leg && _shown == TripStepView.Vehicle) ? TripStepView.Vehicle : TripStepView.Leg;
        _leg = leg;
        return _shown;
    }

    /// <summary>Forgets the last tap, so the next one on the rider's ride shows the vehicle again.</summary>
    public void Reset()
    {
        _leg = -1;
        _shown = TripStepView.Leg;
    }
}
