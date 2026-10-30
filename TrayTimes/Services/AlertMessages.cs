using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Services;

/// <summary>The words for a route's service alerts: the warning sign's name and tooltip, and the details it opens.</summary>
internal static class AlertMessages
{
    /// <summary>The sign's name and tooltip: "Route 14 service alert: Temporary Reroute", or "3 service alerts for Route 14".</summary>
    public static string SignName(string routeDisplayId, IReadOnlyList<ServiceAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        string route = AppStrings.Route(routeDisplayId);
        return alerts.Count == 1
            ? AppStrings.Format("AlertSignOneFormat", route, Title(alerts[0]))
            : AppStrings.Format("AlertSignManyFormat", alerts.Count, route);
    }

    /// <summary>What heads the details: "Route 14 service alerts".</summary>
    public static string Heading(string routeDisplayId) => AppStrings.Format("AlertsHeadingFormat", AppStrings.Route(routeDisplayId));

    /// <summary>An alert's headline: its title, else what it does ("Detour"), else "Service alert".</summary>
    public static string Title(ServiceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alert.Title.Length > 0 ? alert.Title : Effect(alert.Effect) ?? AppStrings.Get("AlertUntitled");
    }

    /// <summary>
    /// What the alert does, why, and until (or from) when, as far as each is known and not already its headline:
    /// "Detour · Construction · Until Fri 6:00 PM", or "Starts 5:00 AM". Empty when none is.
    /// </summary>
    public static string Summary(ServiceAlert alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        List<string> parts = [];
        if (alert.Title.Length > 0 && Effect(alert.Effect) is { } effect)
        {
            parts.Add(effect);
        }

        if (Cause(alert.Cause) is { } cause)
        {
            parts.Add(cause);
        }

        AlertPeriod? period = alert.PeriodAt(now);
        if (period is { Starts: { } starts } && starts > now)
        {
            parts.Add(AppStrings.Format("AlertStartsFormat", TripMessages.When(starts, now)));
        }
        else if (period is { Ends: { } ends })
        {
            parts.Add(AppStrings.Format("AlertUntilFormat", TripMessages.When(ends, now)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The alert's text, unless its headline already says it all.</summary>
    public static string Body(ServiceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alert.Description == Title(alert) ? string.Empty : alert.Description;
    }

    /// <summary>"Detour", and so on; null when the effect is unknown or says nothing ("other effect", "no effect").</summary>
    private static string? Effect(AlertEffect effect) =>
        effect is AlertEffect.UnknownEffect or AlertEffect.OtherEffect or AlertEffect.NoEffect ? null : AppStrings.Get($"AlertEffect{effect}");

    /// <summary>"Holiday", and so on; null when the cause is unknown or unnamed ("other cause").</summary>
    private static string? Cause(AlertCause cause) =>
        cause is AlertCause.UnknownCause or AlertCause.OtherCause ? null : AppStrings.Get($"AlertCause{cause}");
}
