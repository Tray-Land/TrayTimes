using System.Globalization;
using TrayTimes.Commute;
using TrayTimes.Transit;
using TrayTimes.Views;

namespace TrayTimes.Services;

/// <summary>
/// The words for a trip: the trip view's time and Go labels, and the active trip's tray tooltip line,
/// notifications, progress status, and board card. Minutes round up, as the tray countdown does
/// (<see cref="TripStep.MinutesUntil"/>). Texts Narrator reads spell the units out ("6 minutes").
/// </summary>
internal static class TripMessages
{
    /// <summary>How many days ahead a time is named by its weekday ("Tue") rather than its date.</summary>
    private const int WeekdayDays = 7;

    /// <summary>The time dropdown's short label: "Leave now", "Leave 8:00 AM", or "Arrive by Tue 9:00 AM".</summary>
    public static string TimeLabel(TripTime time, DateTimeOffset now) => time.Mode switch
    {
        TripTimeMode.LeaveAt => AppStrings.Format("TripLeaveShortFormat", When(time.At, now)),
        TripTimeMode.ArriveBy => AppStrings.Format("TripArriveByFormat", When(time.At, now)),
        _ => AppStrings.Get("TripLeaveNow/Text"),
    };

    /// <summary>The time with its full date, for the dropdown's tooltip and name: "Leave at Tuesday, October 20, 2026 8:00 AM".</summary>
    public static string TimeName(TripTime time) => time.Mode == TripTimeMode.Now
        ? AppStrings.Get("TripLeaveNow/Text")
        : AppStrings.Format(time.Mode == TripTimeMode.LeaveAt ? "TripLeaveAtFormat" : "TripArriveByFormat",
            time.At.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));

    /// <summary>A time of day, after its day when that isn't today: "8:00 AM", "Tue 8:00 AM", or "Oct 28 8:00 AM".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now) =>
        Day(at, now, CultureInfo.CurrentCulture) is { } day ? AppStrings.Format("TripDayTimeFormat", day, Clock(at)) : Clock(at);

    /// <summary>
    /// The day of <paramref name="at"/> when it isn't today's: its short weekday within the next week ("Tue"),
    /// else its short month and day ("Oct 28"); null for today.
    /// </summary>
    public static string? Day(DateTimeOffset at, DateTimeOffset now, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        DateTime day = at.ToLocalTime().Date;
        double ahead = (day - now.ToLocalTime().Date).TotalDays;
        return ahead == 0 ? null
            : ahead is > 0 and < WeekdayDays ? day.ToString("ddd", culture)
            : day.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), culture);
    }

    /// <summary>When a trip arrives, and where it starts when that was guessed: "arrive 6:00 PM", or "from Home · arrive 6:00 PM".</summary>
    public static string Arrival(CommuteLeg leg, DateTimeOffset arrives, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(leg);
        string arrive = AppStrings.Format("SuggestionArriveFormat", When(arrives, now));
        return leg.IsGuess && leg.From is { } from ? AppStrings.Format("TripFromArrivalFormat", from.Name, arrive) : arrive;
    }

    /// <summary>What Go starts, for its name and tooltip: "Start trip to Work, leaving 5:29 PM" (from where, when that was guessed).</summary>
    public static string GoName(LiveTrip trip, CommuteLeg leg, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(leg);
        string leaving = trip.LeaveBy <= now ? AppStrings.Get("TripNow") : When(trip.LeaveBy, now);
        return leg.IsGuess && leg.From is { } from
            ? AppStrings.Format("GoNameFromFormat", from.Name, leg.To.Name, leaving)
            : AppStrings.Format("GoNameFormat", leg.To.Name, leaving);
    }

    /// <summary>
    /// The caption after Go: "Following your trip to Work…", or, for a trip that leaves over
    /// <see cref="ActiveTripService.MaxBadgeMinutes"/> from now, that it's scheduled and when it leaves.
    /// </summary>
    public static string Started(LiveTrip trip, CommuteLeg leg, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(leg);
        string? from = leg.IsGuess ? leg.From?.Name : null;
        if (trip.LeaveBy - now > TimeSpan.FromMinutes(ActiveTripService.MaxBadgeMinutes))
        {
            string leave = When(trip.LeaveBy, now);
            return from is null
                ? AppStrings.Format("TripScheduledFormat", leg.To.Name, leave)
                : AppStrings.Format("TripScheduledFromFormat", from, leg.To.Name, leave);
        }

        return from is null ? AppStrings.Format("TripStartedFormat", leg.To.Name) : AppStrings.Format("TripStartedFromFormat", from, leg.To.Name);
    }

    /// <summary>What a bell asks during a trip, as one thing is tracked at a time: "End your trip to Work and track this bus instead?"</summary>
    public static string TrackInstead(ActiveTrip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        return AppStrings.Format("TrackInsteadFormat", trip.Leg.To.Name);
    }

    /// <summary>
    /// The progress view's status card for <paramref name="step"/>: "Leave in 6 min" over "Walk 4 min to Oak &amp; 5th for
    /// the 14", read as "Leave in 6 minutes. Walk 4 minutes to Oak &amp; 5th for the 14".
    /// </summary>
    public static TripStatus Status(ActiveTrip trip, TripStep step, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(step);
        LiveTrip live = trip.Progress.Trip;
        LiveRide[] rides = [.. live.Legs.OfType<LiveRide>()];
        string place = trip.Leg.To.Name;
        int minutes = step.MinutesUntil(now);
        if (step.AwaitsReplan)
        {
            return Compose(spoken => AppStrings.Get(spoken ? "TripStatusReplanSpoken" : "TripStatusReplanTitle"), _ => AppStrings.Get("TripStatusReplanText"));
        }

        return step.Kind switch
        {
            TripStepKind.GetReady when ActiveTripService.BadgeMinutes(step, now) is null => Compose(
                _ => AppStrings.Format("TripStatusLeaveAtFormat", When(step.ActionAt, now)), _ => AppStrings.Get("TripStatusScheduledText")),
            TripStepKind.GetReady => Compose(
                spoken => AppStrings.Format("TripStatusLeaveFormat", In(minutes, spoken)), spoken => WayTo(live, step.Ride, place, now, spoken)),
            TripStepKind.ToStop or TripStepKind.Transfer => Compose(
                spoken => AppStrings.Format("TripStatusRideLeavesFormat", Route(rides[step.Ride]), In(minutes, spoken)),
                spoken => WayTo(live, step.Ride, place, now, spoken)),
            TripStepKind.Ride => Compose(
                spoken => AppStrings.Format("TripStatusGetOffFormat", StopName(rides[step.Ride].Line.AlightAt), In(minutes, spoken)),
                spoken => AfterRide(live, step.Ride, place, spoken)),
            TripStepKind.LastWalk => Compose(
                _ => AppStrings.Format("TripStatusArriveFormat", place, Clock(live.Arrives)), spoken => WayTo(live, rides.Length, place, now, spoken)),
            _ => Compose(_ => AppStrings.Format("TripStatusArrivedFormat", place), _ => AppStrings.Get("TripStatusDoneText")),
        };
    }

    /// <summary>The Resume card for a trip saved before a restart: "Resume your trip to Work?"</summary>
    public static TripStatus Resume(ActiveTrip trip)
    {
        (string title, string text) = ResumePrompt(trip);
        return new TripStatus(title, text, AppStrings.Format("TripResumeSpokenFormat", title, text));
    }

    /// <summary>
    /// The board's card for the trip: "Trip to Work · get off in 9 min". <paramref name="step"/> is null for a trip
    /// waiting to be resumed.
    /// </summary>
    public static string CardTitle(ActiveTrip trip, TripStep? step, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        LiveTrip live = trip.Progress.Trip;
        string phrase = step switch
        {
            null => AppStrings.Get("TripCardResume"),
            { AwaitsReplan: true } => AppStrings.Get("TripCardReplan"),
            { Kind: TripStepKind.GetReady } when ActiveTripService.BadgeMinutes(step, now) is null =>
                AppStrings.Format("TripCardLeaveAtFormat", When(step.ActionAt, now)),
            { Kind: TripStepKind.GetReady } => AppStrings.Format("TripCardLeaveFormat", In(step.MinutesUntil(now), spoken: false)),
            { Kind: TripStepKind.ToStop or TripStepKind.Transfer } => AppStrings.Format("TripCardRideLeavesFormat",
                Route(live.Legs.OfType<LiveRide>().ElementAt(step.Ride)), In(step.MinutesUntil(now), spoken: false)),
            { Kind: TripStepKind.Ride } => AppStrings.Format("TripCardGetOffFormat", In(step.MinutesUntil(now), spoken: false)),
            { Kind: TripStepKind.LastWalk } => AppStrings.Format("SuggestionArriveFormat", Clock(live.Arrives)),
            _ => AppStrings.Get("TripCardArrived"),
        };
        return AppStrings.Format("TripCardFormat", trip.Leg.To.Name, phrase);
    }

    /// <summary>What Narrator reads for the board's card: the trip and its status, or the Resume question.</summary>
    public static string CardName(ActiveTrip trip, TripStep? step, DateTimeOffset now) => step is null
        ? Resume(trip).Title
        : AppStrings.Format("TripCardNameFormat", trip.Leg.To.Name, Status(trip, step, now).Spoken);
    /// <summary>The tooltip's trip line, e.g. "Trip to Work: get off at State St in 9 min (8:25)".</summary>
    public static string Tooltip(ActiveTrip trip, bool isPendingResume, DateTimeOffset now, int leadMinutes)
    {
        string place = trip.Leg.To.Name;
        if (isPendingResume)
        {
            return AppStrings.Format("TripTooltipResumeFormat", place);
        }

        TripStep step = trip.Progress.Step(now, leadMinutes);
        if (step.AwaitsReplan)
        {
            return AppStrings.Format("TripTooltipReplanFormat", place);
        }

        LiveRide[] rides = [.. trip.Progress.Trip.Legs.OfType<LiveRide>()];
        string when = In(step.MinutesUntil(now));
        string clock = Clock(step.ActionAt);
        return step.Kind switch
        {
            TripStepKind.GetReady => AppStrings.Format("TripTooltipLeaveFormat", place, when, clock),
            TripStepKind.ToStop or TripStepKind.Transfer =>
                AppStrings.Format("TripTooltipBoardFormat", place, Route(rides[step.Ride]), StopName(rides[step.Ride].Line.BoardAt), when, clock),
            TripStepKind.Ride => AppStrings.Format("TripTooltipGetOffFormat", place, StopName(rides[step.Ride].Line.AlightAt), when, clock),
            TripStepKind.LastWalk => AppStrings.Format("TripTooltipArriveFormat", place, clock),
            _ => AppStrings.Format("TripTooltipArrivedFormat", place),
        };
    }

    /// <summary>
    /// The notification for <paramref name="alert"/>, just raised on <paramref name="trip"/>, e.g. "Leave now for
    /// Work" and "Walk 4 min to Oak &amp; 5th for the 14 at 8:12."
    /// </summary>
    /// <param name="before">The progress the alert was raised from, to tell which ride took another departure.</param>
    public static (string Title, string Text) Alert(ActiveTrip trip, TripAlert alert, TripProgress before, DateTimeOffset now)
    {
        TripProgress progress = trip.Progress;
        LiveTrip live = progress.Trip;
        LiveRide[] rides = [.. live.Legs.OfType<LiveRide>()];
        string place = trip.Leg.To.Name;
        if (alert == TripAlert.HeadOut)
        {
            return (AppStrings.Format("TripHeadOutTitleFormat", In(Minutes(live.LeaveBy, now)), place), Boarding(live, 0, place));
        }

        if (alert == TripAlert.LeaveNow)
        {
            return (AppStrings.Format("TripLeaveNowTitleFormat", place), Boarding(live, 0, place));
        }

        if (alert == TripAlert.GetOffSoon)
        {
            LiveRide off = rides[progress.GetOffAlerts - 1];
            return (AppStrings.Format("TripGetOffSoonTitleFormat", StopName(off.Line.AlightAt), In(Minutes(off.Ends, now))),
                AfterRide(live, progress.GetOffAlerts - 1, place, spoken: false));
        }

        int changed = alert == TripAlert.ConnectionChanged ? ChangedRide(before, progress) : progress.AnnouncedRide;
        if (changed < 0 || changed >= rides.Length)
        {
            return (AppStrings.Get("TripWalkRestTitle"), Boarding(live, rides.Length, place));
        }

        LiveRide ride = rides[changed];
        string title = AppStrings.Format(alert == TripAlert.RideChanged ? "TripRideChangedTitleFormat" : "TripConnectionChangedTitleFormat", Route(ride), Clock(ride.Starts));

        // Still on the ride before it, "it leaves in 6 min" isn't something to act on; where to get off, and the wait after, is.
        if (changed > 0 && progress.Step(now, leadMinutes: 0) is { Kind: TripStepKind.Ride, AwaitsReplan: false } step && step.Ride == changed - 1)
        {
            LiveRide off = rides[changed - 1];
            string getOff = AppStrings.Format("TripStatusGetOffFormat", StopName(off.Line.AlightAt), In(Minutes(off.Ends, now)));
            return (title, AppStrings.Format("TripTransferChangedFormat", getOff, AfterRide(live, changed - 1, place, spoken: false), place, Clock(live.Arrives)));
        }

        return (title, AppStrings.Format("TripRideLeavesFormat", StopName(ride.Line.BoardAt), In(Minutes(ride.Starts, now)), place, Clock(live.Arrives)));
    }

    /// <summary>Planning the trip again found no way to finish it.</summary>
    public static (string Title, string Text) NoConnection(ActiveTrip trip) =>
        (AppStrings.Format("TripNoConnectionTitleFormat", trip.Leg.To.Name), AppStrings.Get("TripNoConnectionText"));

    /// <summary>The trip needs a new plan, but it was planned again under 15 minutes ago: when it will be.</summary>
    public static (string Title, string Text) ReplanWait(ActiveTrip trip, DateTimeOffset at) =>
        (AppStrings.Format("TripReplanWaitTitleFormat", trip.Leg.To.Name), AppStrings.Format("TripReplanWaitTextFormat", Clock(at)));

    /// <summary>Asks whether to resume a trip saved before a restart.</summary>
    public static (string Title, string Text) ResumePrompt(ActiveTrip trip) =>
        (AppStrings.Format("TripResumeTitleFormat", trip.Leg.To.Name), AppStrings.Get("TripResumeText"));

    /// <summary>The way to <paramref name="ride"/>: "Walk 4 min to Oak &amp; 5th for the 14 at 8:12."; past the last ride, the walk to the place.</summary>
    private static string Boarding(LiveTrip live, int ride, string place)
    {
        (TimeSpan walk, LiveRide? next) = WalkTo(live, ride);
        if (next is null)
        {
            return AppStrings.Format("TripWalkToPlaceFormat", WalkMinutes(walk), place, Clock(live.Arrives));
        }

        return walk > TimeSpan.Zero
            ? AppStrings.Format("TripWalkToRideFormat", WalkMinutes(walk), StopName(next.Line.BoardAt), Route(next), Clock(next.Starts))
            : AppStrings.Format("TripCatchRideFormat", Route(next), StopName(next.Line.BoardAt), Clock(next.Starts));
    }

    /// <summary>
    /// What comes after getting off <paramref name="ride"/>: "Then walk 1 min to the 30 at 8:30", or, when the next
    /// ride leaves a minute or more after the rider can be at its stop, the wait too: "Then wait 4 min for the 30 at 8:30".
    /// </summary>
    private static string AfterRide(LiveTrip live, int ride, string place, bool spoken)
    {
        (TimeSpan walk, LiveRide? next) = WalkTo(live, ride + 1);
        if (next is not null)
        {
            int wait = (int)(next.Starts - (live.Legs.OfType<LiveRide>().ElementAt(ride).Ends + walk)).TotalMinutes;
            if (wait >= 1)
            {
                string waiting = Duration(TimeSpan.FromMinutes(wait), spoken);
                return walk > TimeSpan.Zero
                    ? AppStrings.Format("TripThenWalkWaitForRideFormat", Duration(walk, spoken), waiting, Route(next), Clock(next.Starts))
                    : AppStrings.Format("TripThenWaitForRideFormat", waiting, Route(next), Clock(next.Starts));
            }

            return walk > TimeSpan.Zero
                ? AppStrings.Format("TripThenWalkToRideFormat", Duration(walk, spoken), Route(next), Clock(next.Starts))
                : AppStrings.Format("TripThenCatchRideFormat", Route(next), Clock(next.Starts));
        }

        return walk > TimeSpan.Zero
            ? AppStrings.Format("TripThenWalkToPlaceFormat", Duration(walk, spoken), place, Clock(live.Arrives))
            : AppStrings.Format("TripArriveAtFormat", place, Clock(live.Arrives));
    }

    /// <summary>
    /// The way to <paramref name="ride"/> at <paramref name="now"/>: "Walk 4 min to Oak &amp; 5th for the 14" while there's
    /// walking left, else "Board at Oak &amp; 5th, leaving 8:12"; past the last ride, "Walk 3 min to Work".
    /// </summary>
    private static string WayTo(LiveTrip live, int ride, string place, DateTimeOffset now, bool spoken)
    {
        (TimeSpan left, LiveRide? next) = WalkTo(live, ride, now);
        if (next is null)
        {
            return AppStrings.Format("TripStatusWalkToPlaceFormat", Duration(left, spoken), place);
        }

        return left > TimeSpan.Zero
            ? AppStrings.Format("TripStatusWalkToRideFormat", Duration(left, spoken), StopName(next.Line.BoardAt), Route(next))
            : AppStrings.Format("TripStatusBoardAtFormat", StopName(next.Line.BoardAt), Clock(next.Starts));
    }

    /// <summary>
    /// The walking from the previous ride (or the start) to <paramref name="ride"/>, and that ride; past the last ride, the
    /// walking to the end. With <paramref name="now"/>, only the walking still ahead then.
    /// </summary>
    private static (TimeSpan Walk, LiveRide? Ride) WalkTo(LiveTrip live, int ride, DateTimeOffset? now = null)
    {
        TimeSpan walk = TimeSpan.Zero;
        int index = 0;
        foreach (LiveLeg leg in live.Legs)
        {
            if (leg is not LiveRide next)
            {
                DateTimeOffset from = now > leg.Starts ? now.Value : leg.Starts;
                walk += leg.Ends > from ? leg.Ends - from : TimeSpan.Zero;
            }
            else if (index++ == ride)
            {
                return (walk, next);
            }
            else
            {
                walk = TimeSpan.Zero;
            }
        }

        return (walk, null);
    }

    /// <summary>A status from its title and detail, each written for the screen and for Narrator (spoken: true).</summary>
    private static TripStatus Compose(Func<bool, string> title, Func<bool, string> detail) =>
        new(title(false), detail(false), AppStrings.Format("TripStatusSpokenFormat", title(true), detail(true)));

    /// <summary>The first ride that took another departure; else the ride the rider was last told about.</summary>
    private static int ChangedRide(TripProgress before, TripProgress after)
    {
        for (int i = 0; i < after.Rides.Count; i++)
        {
            if (i >= before.Rides.Count || IsSwitched(before.Rides[i], after.Rides[i]))
            {
                return i;
            }
        }

        return after.AnnouncedRide;
    }

    private static bool IsSwitched(RideChoice was, RideChoice now) =>
        was.Line != now.Line || was.TripId != now.TripId || (was.TripId is null && was.Departs != now.Departs);

    private static string Route(LiveRide ride) => ride.Line.Route.DisplayId;

    private static string StopName(TransitStop? stop) => stop is { Name: { Length: > 0 } name } ? name : AppStrings.Get("TripStopUnnamed");

    private static string In(int minutes) => In(minutes, spoken: false);

    /// <summary>"in 6 min" (spoken: "in 6 minutes", "in 1 minute"), or "now".</summary>
    private static string In(int minutes, bool spoken) => minutes == 0 ? AppStrings.Get("TripNow")
        : !spoken ? AppStrings.Format("TripInMinutesFormat", minutes)
        : minutes == 1 ? AppStrings.Get("TripInOneMinuteSpoken")
        : AppStrings.Format("TripInMinutesSpokenFormat", minutes);

    /// <summary>"4 min" (spoken: "4 minutes", "1 minute"), at least a minute.</summary>
    private static string Duration(TimeSpan duration, bool spoken)
    {
        int minutes = WalkMinutes(duration);
        return !spoken ? AppStrings.Format("TripDurationFormat", minutes)
            : minutes == 1 ? AppStrings.Get("TripDurationOneMinuteSpoken")
            : AppStrings.Format("TripDurationSpokenFormat", minutes);
    }

    private static int Minutes(DateTimeOffset at, DateTimeOffset now) => Math.Max(0, (int)Math.Ceiling((at - now).TotalMinutes));

    private static int WalkMinutes(TimeSpan walk) => Math.Max(1, (int)Math.Ceiling(walk.TotalMinutes));

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}

/// <summary>What the progress view's status card says about the current step, and how Narrator reads it.</summary>
/// <param name="Title">The step, large: "Leave in 6 min".</param>
/// <param name="Detail">What it involves: "Walk 4 min to Oak &amp; 5th for the 14".</param>
/// <param name="Spoken">Both, with the units spelled out: "Leave in 6 minutes. Walk 4 minutes to Oak &amp; 5th for the 14".</param>
internal sealed record TripStatus(string Title, string Detail, string Spoken);
