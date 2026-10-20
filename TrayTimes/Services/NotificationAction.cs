namespace TrayTimes.Services;

/// <summary>A button on a tracking or trip notification.</summary>
internal enum NotificationAction
{
    /// <summary>Stop tracking the bus.</summary>
    StopTracking,

    /// <summary>The rider missed the ride they were heading to or assumed to be on.</summary>
    MissedIt,

    /// <summary>End the active trip, or a saved one waiting to resume.</summary>
    EndTrip,

    /// <summary>Resume a trip saved before a restart.</summary>
    ResumeTrip,
}
