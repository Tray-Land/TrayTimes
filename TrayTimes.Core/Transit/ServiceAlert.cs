namespace TrayTimes.Transit;

/// <summary>How much an alert matters, as Transit grades it. <see cref="Severe"/> means service is suspended.</summary>
public enum AlertSeverity
{
    Unknown,
    Info,
    Warning,
    Severe,
}

/// <summary>What an alert does to service, named as in GTFS-realtime.</summary>
public enum AlertEffect
{
    UnknownEffect,
    NoService,
    ReducedService,
    SignificantDelays,
    Detour,
    AdditionalService,
    ModifiedService,
    StopMoved,
    AccessibilityIssue,
    OtherEffect,
    NoEffect,
}

/// <summary>Why an alert was issued, named as in GTFS-realtime.</summary>
public enum AlertCause
{
    UnknownCause,
    OtherCause,
    TechnicalProblem,
    Strike,
    Demonstration,
    Accident,
    Holiday,
    Weather,
    Maintenance,
    Construction,
    PoliceActivity,
    MedicalEmergency,
}

/// <summary>When an alert applies: from <see cref="Starts"/> (null: it always has) until <see cref="Ends"/> (null: indefinitely).</summary>
public readonly record struct AlertPeriod(DateTimeOffset? Starts, DateTimeOffset? Ends)
{
    public bool Contains(DateTimeOffset at) => (Starts is null || Starts <= at) && (Ends is null || at < Ends);
}

/// <summary>
/// A transit agency's notice about a route, such as a detour, a holiday schedule, or a closed station. With no
/// <see cref="Periods"/> it applies indefinitely.
/// </summary>
/// <param name="Title">A short headline; empty when the agency gave none.</param>
public sealed record ServiceAlert(
    string Title,
    string Description,
    AlertSeverity Severity,
    AlertEffect Effect,
    AlertCause Cause,
    IReadOnlyList<AlertPeriod> Periods)
{
    /// <summary>How soon an alert must start to be shown before it does, so tomorrow's holiday schedule shows today.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromDays(1);

    /// <summary>The period in effect at <paramref name="now"/>, else the next one to start; null when none is (or there are none).</summary>
    public AlertPeriod? PeriodAt(DateTimeOffset now)
    {
        AlertPeriod? next = null;
        foreach (AlertPeriod period in Periods)
        {
            if (period.Contains(now))
            {
                return period;
            }

            if (period.Starts > now && (next is null || period.Starts < next.Value.Starts))
            {
                next = period;
            }
        }

        return next;
    }

    /// <summary>Whether the alert applies at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => Periods.Count == 0 || PeriodAt(now)?.Contains(now) == true;

    /// <summary>Whether to show the alert at <paramref name="now"/>: it applies, or it starts within <see cref="Lookahead"/>.</summary>
    public bool IsCurrent(DateTimeOffset now) => IsActive(now) || PeriodAt(now)?.Starts - now <= Lookahead;
}
