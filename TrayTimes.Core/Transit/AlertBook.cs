namespace TrayTimes.Transit;

/// <summary>
/// The service alerts each route had in its latest departures response. A response carries every alert of each
/// route it lists, so it replaces those routes' alerts (clearing them when it has none) and leaves the rest. Safe
/// to use from any thread.
/// </summary>
public sealed class AlertBook
{
    public const int RetainedRoutes = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Sequence, IReadOnlyList<ServiceAlert> Alerts)> _routes = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    /// <summary>Takes in one response's alerts, by route ID.</summary>
    public void Record(IReadOnlyDictionary<string, IReadOnlyList<ServiceAlert>> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        lock (_gate)
        {
            foreach ((string routeId, IReadOnlyList<ServiceAlert> routeAlerts) in alerts)
            {
                if (routeAlerts.Count == 0)
                {
                    _routes.Remove(routeId);
                }
                else
                {
                    _routes[routeId] = (++_sequence, routeAlerts);
                }
            }

            while (_routes.Count > RetainedRoutes)
            {
                _routes.Remove(_routes.MinBy(r => r.Value.Sequence).Key);
            }
        }
    }

    /// <summary>
    /// A route's alerts worth showing at <paramref name="now"/> (<see cref="ServiceAlert.IsCurrent"/>): the most
    /// severe first, and of those, the ones in effect before the ones still to come.
    /// </summary>
    public IReadOnlyList<ServiceAlert> For(string routeId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        IReadOnlyList<ServiceAlert> alerts;
        lock (_gate)
        {
            if (!_routes.TryGetValue(routeId, out (long, IReadOnlyList<ServiceAlert> Alerts) entry))
            {
                return [];
            }

            alerts = entry.Alerts;
        }

        return [.. alerts.Where(a => a.IsCurrent(now)).OrderByDescending(a => a.Severity).ThenBy(a => !a.IsActive(now))];
    }
}
