namespace TrayTimes.Transit;

/// <summary>
/// Lets a trip whose plan has stopped working be planned again at most once per <see cref="Interval"/>,
/// so a trip with no service left can't drain the call budget. Origins and destinations are the same
/// by the plan cache's rules (<see cref="MetadataTransitService.SamePlaceMeters"/> and
/// <see cref="MetadataTransitService.SameDestinationMeters"/>), so a drifting location fix doesn't reset it.
/// </summary>
public sealed class ReplanGuard(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<(GeoPoint From, GeoPoint To, DateTimeOffset At)> _replans = [];

    /// <summary>True, and remembered, when the trip from <paramref name="from"/> to <paramref name="to"/> may be planned again now.</summary>
    public bool TryAcquire(GeoPoint from, GeoPoint to)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            _replans.RemoveAll(r => now - r.At >= Interval);
            if (_replans.Any(r => GeoMath.DistanceMeters(r.From, from) <= MetadataTransitService.SamePlaceMeters
                && GeoMath.DistanceMeters(r.To, to) <= MetadataTransitService.SameDestinationMeters))
            {
                return false;
            }

            _replans.Add((from, to, now));
            return true;
        }
    }
}
