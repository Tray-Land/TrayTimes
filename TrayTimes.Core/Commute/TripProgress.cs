using System.Text.Json.Serialization;
using TrayTimes.Tracking;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>Where the rider is in an active trip.</summary>
public enum TripStepKind
{
    /// <summary>Before leaving, at the origin.</summary>
    GetReady,

    /// <summary>On the way to the first ride's stop, or waiting there.</summary>
    ToStop,

    /// <summary>On a ride.</summary>
    Ride,

    /// <summary>Between rides: on the way to the next one's stop, or waiting there.</summary>
    Transfer,

    /// <summary>After the last ride, or on a walk-only trip: on the way to the destination.</summary>
    LastWalk,

    /// <summary>Arrived.</summary>
    Done,
}

/// <summary>Why the rider should hear about their trip.</summary>
public enum TripAlert
{
    /// <summary>The rider's lead time before leaving: get ready to go.</summary>
    HeadOut,

    /// <summary>Time to leave for the first ride.</summary>
    LeaveNow,

    /// <summary>The next ride's departure moved by <see cref="TrackedBus.ShiftThreshold"/> or more.</summary>
    RideChanged,

    /// <summary>A ride can't be made as told (canceled, missed, or out of reach), so the trip takes another departure.</summary>
    ConnectionChanged,

    /// <summary>Getting off is <see cref="TripProgress.ActionLead"/> away.</summary>
    GetOffSoon,
}

/// <summary>Where one of a trip's legs is relative to the rider, for the progress view's steps.</summary>
public enum TripLegState
{
    /// <summary>Still ahead.</summary>
    Upcoming,

    /// <summary>The leg the rider is on, or about to start.</summary>
    Current,

    /// <summary>Behind the rider.</summary>
    Done,
}

/// <summary>The departure an active trip takes for one ride: which of its lines, which trip, and when it boards and gets off.</summary>
/// <param name="Line">The index of the line in <see cref="RideLeg.Lines"/>.</param>
public sealed record RideChoice(int Line, string? TripId, DateTimeOffset Departs, DateTimeOffset Alights);

/// <summary>Where the rider is in a trip at one moment.</summary>
/// <param name="Ride">The ride the step leads to or is on; -1 when no ride is left.</param>
/// <param name="Leg">The current leg's index in <see cref="LiveTrip.Legs"/>; their count once arrived.</param>
/// <param name="ActionAt">When the rider next needs to act: leave, board, get off, or arrive.</param>
/// <param name="IsUrgent">True when that's now: within the lead before leaving, or <see cref="TripProgress.ActionLead"/> of boarding or getting off.</param>
public sealed record TripStep(TripStepKind Kind, int Ride, int Leg, DateTimeOffset ActionAt, bool IsUrgent)
{
    /// <summary>
    /// True while no departure can make <see cref="Ride"/> (<see cref="TripProgress.NeedsReplan"/>): the rider waits
    /// for a new plan at its stop (or, before leaving, at the origin), so there's no countdown. <see cref="ActionAt"/>
    /// is when they get there, or when they were to leave.
    /// </summary>
    public bool AwaitsReplan { get; init; }

    /// <summary>
    /// True when the rider can say they missed the ride: they're heading to it or assumed to be on it, and it
    /// isn't already waiting for a new plan (see <see cref="TripProgress.Missed"/>).
    /// </summary>
    public bool CanMiss => !AwaitsReplan && Kind is TripStepKind.ToStop or TripStepKind.Transfer or TripStepKind.Ride;

    /// <summary>Whether there is an upcoming or current ride whose own vehicle can be shown, including before leaving.</summary>
    public bool CanFollowVehicle => !AwaitsReplan && Ride >= 0
        && Kind is TripStepKind.GetReady or TripStepKind.ToStop or TripStepKind.Transfer or TripStepKind.Ride;

    /// <summary>Whether the leg at <paramref name="leg"/> (an index in <see cref="LiveTrip.Legs"/>) is behind the rider, current, or ahead.</summary>
    public TripLegState StateOf(int leg) => leg < Leg ? TripLegState.Done : leg == Leg ? TripLegState.Current : TripLegState.Upcoming;

    /// <summary>
    /// Whole minutes until <see cref="ActionAt"/>, rounded up, so the countdown reads 2 just as the step turns
    /// urgent 2 minutes out; 0 once it's due.
    /// </summary>
    public int MinutesUntil(DateTimeOffset now) => Math.Max(0, (int)Math.Ceiling((ActionAt - now).TotalMinutes));
}

/// <summary>A trip's next state, and at most one alert, which came due at <see cref="DueAt"/>.</summary>
public sealed record TripUpdate(TripProgress Progress, TripAlert? Alert = null, DateTimeOffset? DueAt = null);

/// <summary>
/// A trip the rider has started (active trip mode). Immutable, like <see cref="TrackedBus"/>: each check or
/// clock tick produces the next state through <see cref="Advance"/>, with any alert to raise.
/// <para>The plan supplies the structure; the rider's choice for each ride, and the latest departures at the
/// trip's stops, supply the times. A ride not yet taken keeps its trip while the rider can still be at its
/// stop for it, and moves to another departure when it's canceled, missed, or out of reach. A ride counts as
/// taken once its last known departure passes: the app can't tell where the rider is, so it assumes they got
/// on unless they say they missed it (<see cref="Missed"/>).</para>
/// </summary>
/// <param name="Rides">One choice for each <see cref="RideLeg"/> of <paramref name="Itinerary"/>, in order.</param>
/// <param name="NotBefore">The earliest the rider leaves: when they started the trip, or the time they chose to leave at.</param>
public sealed record TripProgress(TripItinerary Itinerary, IReadOnlyList<RideChoice> Rides, DateTimeOffset NotBefore)
{
    /// <summary>How long before boarding or getting off the step turns urgent, and before getting off <see cref="TripAlert.GetOffSoon"/> fires.</summary>
    public static readonly TimeSpan ActionLead = TimeSpan.FromMinutes(2);

    /// <summary>How long after its arrival time a trip is kept.</summary>
    public static readonly TimeSpan EndGrace = TimeSpan.FromMinutes(5);

    // One check before each decision, timed so the decision's alert fires on fresh times (see NextCheck).
    private static readonly TimeSpan HeadOutCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LeaveCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BoardCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GetOffCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongWalk = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongRide = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(2);

    /// <summary>When the rider left (or was told to): leave-by stops moving.</summary>
    public DateTimeOffset? LeftAt { get; init; }

    /// <summary>The latest departures known at the trip's stops.</summary>
    public IReadOnlyList<Arrival> Known { get; init; } = [];

    /// <summary>When <see cref="Known"/> was fetched.</summary>
    public DateTimeOffset? KnownAt { get; init; }

    /// <summary>Trips the rider missed (by trip ID, else time), never chosen again.</summary>
    public IReadOnlyList<string> MissedTrips { get; init; } = [];

    /// <summary>
    /// The first ride no departure can make, or -1. The rides from it on wait for a new plan: they raise no
    /// alerts and get no checks, and before leaving, the rider isn't told to leave.
    /// </summary>
    public int StrandedRide { get; init; } = -1;

    /// <summary>True when no departure can make one of the rides (<see cref="StrandedRide"/>): the trip needs a new plan.</summary>
    [JsonIgnore]
    public bool NeedsReplan => StrandedRide >= 0;

    public bool HeadOutSent { get; init; }

    public bool LeaveNowSent { get; init; }

    /// <summary>How many rides, from the first, have had their <see cref="TripAlert.GetOffSoon"/>.</summary>
    public int GetOffAlerts { get; init; }

    /// <summary>The ride the rider was last told about; -1 before the first alert.</summary>
    public int AnnouncedRide { get; init; } = -1;

    /// <summary>The departure the rider was last told for <see cref="AnnouncedRide"/>.</summary>
    public DateTimeOffset? AnnouncedDeparture { get; init; }

    /// <summary>The trip at its current times, leg by leg. Walks before the first ride end as it leaves, until the rider has left.</summary>
    [JsonIgnore]
    public LiveTrip Trip
    {
        get
        {
            ILookup<StopLine, Arrival> known = Known.ToLookup(StopLine.Of);
            List<LiveLeg> legs = [];
            DateTimeOffset leaveBy = LeaveTime();
            DateTimeOffset at = leaveBy;
            int index = 0;
            foreach (TripLeg leg in Itinerary.Legs)
            {
                if (leg is WalkLeg walk)
                {
                    legs.Add(new LiveWalk(walk, at, at + walk.Duration));
                    at += walk.Duration;
                }
                else if (leg is RideLeg ride)
                {
                    RideChoice choice = Rides[index++];
                    RideLine line = ride.Lines[choice.Line];
                    legs.Add(new LiveRide(ride, line, choice.Departs, choice.Alights, Departure(known, line, choice)));
                    at = choice.Alights;
                }
            }

            return new LiveTrip(Itinerary, legs, leaveBy, at);
        }
    }

    private bool IsEngaged => HeadOutSent || LeaveNowSent;

    /// <summary>True when the trip can't be made before the rider has left: they wait at the origin for a new plan.</summary>
    private bool IsWaitingToLeave => NeedsReplan && LeftAt is null;

    /// <summary>How many rides, from the first, still go ahead as chosen: the ones before <see cref="StrandedRide"/>, once the rider has left.</summary>
    private int UnstrandedRides => !NeedsReplan ? Rides.Count : LeftAt is null ? 0 : StrandedRide;

    /// <summary>
    /// Starts following <paramref name="trip"/> at its current times. When it's already within the lead
    /// before leaving, the rider has just seen it, so there's no <see cref="TripAlert.HeadOut"/>.
    /// </summary>
    /// <param name="time">A leave-at time keeps the rider from being sent on an earlier departure.</param>
    /// <param name="snapshot">The departures the trip was timed from, if any.</param>
    public static TripProgress Start(LiveTrip trip, TripTime time, PredictionSnapshot? snapshot, int leadMinutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        RideChoice[] rides = [.. trip.Legs.OfType<LiveRide>().Select(Choice)];
        TripProgress progress = new TripProgress(trip.Itinerary, rides, time.Mode == TripTimeMode.LeaveAt ? time.At : now).WithKnown(snapshot);
        DateTimeOffset leaveBy = progress.LeaveTime();
        bool told = now >= leaveBy - Lead(leadMinutes);
        return progress with
        {
            LeftAt = now >= leaveBy ? leaveBy : null,
            HeadOutSent = told,
            LeaveNowSent = now >= leaveBy,
            AnnouncedRide = told && rides.Length > 0 ? 0 : -1,
            AnnouncedDeparture = told && rides.Length > 0 ? rides[0].Departs : null,
        };
    }

    /// <summary>
    /// Applies the latest departures (an older snapshot than the last one applied is ignored), re-times the
    /// trip, and raises the alert that's due. When several came due at once (the PC slept), only the latest
    /// is raised. Changes are raised only after the first alert, when the rider has been told a time. A trip
    /// that can't be made before the rider leaves doesn't send them off (<see cref="StrandedRide"/>).
    /// </summary>
    /// <param name="snapshot">Departures at <see cref="WatchedStops"/>, or null for a clock tick.</param>
    public TripUpdate Advance(PredictionSnapshot? snapshot, int leadMinutes, DateTimeOffset now)
    {
        TripProgress progress = WithKnown(snapshot);
        bool leaving = progress.LeftAt is null && now >= progress.LeaveTime();
        if (leaving)
        {
            progress = progress with { LeftAt = progress.LeaveTime() };
        }

        (RideChoice[] rides, bool switched, int stranded) = progress.Retime(now);
        progress = progress with { Rides = rides, StrandedRide = stranded, LeftAt = leaving && stranded >= 0 ? null : progress.LeftAt };
        return progress.Alert(leadMinutes, now, switched);
    }

    /// <summary>
    /// The rider missed the ride they're heading to, or were assumed to be on: re-times it from the next
    /// departure at its stop, from the departures already known. Does nothing before leaving, after the last
    /// ride, or while the rider already waits for a new plan.
    /// </summary>
    public TripProgress Missed(DateTimeOffset now)
    {
        TripStep step = Step(now, leadMinutes: 0);
        if (!step.CanMiss)
        {
            return this;
        }

        RideChoice missed = Rides[step.Ride];
        TripProgress progress = this with
        {
            MissedTrips = [.. MissedTrips, LiveTrip.TripKey(missed.TripId, missed.Departs)],
            LeftAt = LeftAt ?? LeaveTime(),
            HeadOutSent = true,
            LeaveNowSent = true,
            GetOffAlerts = Math.Min(GetOffAlerts, step.Ride),
        };
        (RideChoice[] rides, _, int stranded) = progress.Retime(now);
        progress = progress with { Rides = rides, StrandedRide = stranded };
        int next = progress.NextRide(now);
        return progress with { AnnouncedRide = next, AnnouncedDeparture = next >= 0 ? rides[next].Departs : null };
    }

    /// <summary>
    /// Where the rider is at <paramref name="now"/>. While the trip needs a new plan, the rider waits for it at
    /// the stranded ride's stop (or at the origin before leaving): that step <see cref="TripStep.AwaitsReplan"/>.
    /// </summary>
    public TripStep Step(DateTimeOffset now, int leadMinutes)
    {
        LiveTrip trip = Trip;
        if (NeedsReplan && LeftAt is null)
        {
            return new TripStep(TripStepKind.GetReady, Rides.Count > 0 ? 0 : -1, 0, trip.LeaveBy, false) { AwaitsReplan = true };
        }

        if (now < trip.LeaveBy)
        {
            return new TripStep(TripStepKind.GetReady, Rides.Count > 0 ? 0 : -1, 0, trip.LeaveBy, now >= trip.LeaveBy - Lead(leadMinutes));
        }

        int ride = 0;
        for (int i = 0; i < trip.Legs.Count; i++)
        {
            if (trip.Legs[i] is not LiveRide leg)
            {
                continue;
            }

            int toStop = i > 0 && trip.Legs[i - 1] is LiveWalk ? i - 1 : i;
            if (ride == StrandedRide)
            {
                DateTimeOffset atStop = i > 0 ? trip.Legs[i - 1].Ends : trip.LeaveBy;
                return new TripStep(ride == 0 ? TripStepKind.ToStop : TripStepKind.Transfer, ride, toStop, atStop, false) { AwaitsReplan = true };
            }

            if (now < leg.Starts)
            {
                return new TripStep(ride == 0 ? TripStepKind.ToStop : TripStepKind.Transfer, ride, toStop, leg.Starts, leg.Starts - now <= ActionLead);
            }

            if (now < leg.Ends)
            {
                return new TripStep(TripStepKind.Ride, ride, i, leg.Ends, leg.Ends - now <= ActionLead);
            }

            ++ride;
        }

        if (now < trip.Arrives)
        {
            int walk = Math.Max(0, IndexWhere(trip.Legs, l => l.Ends > now));
            return new TripStep(TripStepKind.LastWalk, -1, walk, trip.Arrives, false);
        }

        return new TripStep(TripStepKind.Done, -1, trip.Legs.Count, trip.Arrives, false);
    }

    /// <summary>The stops whose departures can still change the trip: each ride not yet taken boards at any of its lines' stops, and every ride not yet over gets off at its line's stop.</summary>
    public IReadOnlyList<string> WatchedStops(DateTimeOffset now)
    {
        List<string> stops = [];
        int index = 0;
        foreach (RideLeg ride in Itinerary.Legs.OfType<RideLeg>())
        {
            RideChoice choice = Rides[index++];
            if (choice.Alights <= now)
            {
                continue;
            }

            if (choice.Departs > now)
            {
                stops.AddRange(ride.Lines.Select(l => l.BoardAt.Id));
            }

            if (ride.Lines[choice.Line].AlightAt is { } alight)
            {
                stops.Add(alight.Id);
            }
        }

        return [.. stops.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// When to check the trip's departures next. There's no steady cadence: one check shortly before each
    /// decision still ahead (head out, leave now, boarding after a long walk, getting off a long ride,
    /// boarding after a long wait), skipping any within <see cref="MergeWindow"/> after the last data or
    /// attempt. A check that's overdue (after sleep) is due now.
    /// </summary>
    /// <param name="lastAttempt">The last check, even a failed one, so a failure isn't retried at once.</param>
    /// <returns>Null when no decision is left to check for.</returns>
    public DateTimeOffset? NextCheck(int leadMinutes, DateTimeOffset now, DateTimeOffset? lastAttempt = null)
    {
        DateTimeOffset? last = KnownAt > lastAttempt || lastAttempt is null ? KnownAt : lastAttempt;
        DateTimeOffset? next = Checks(leadMinutes)
            .Where(c => c.Decision > now && (last is null || c.At > last + MergeWindow))
            .Select(c => (DateTimeOffset?)c.At)
            .Min();
        return next < now ? now : next;
    }

    /// <summary>When the next alert falls due by the clock alone; null when none is left (or the rest wait for a new plan).</summary>
    public DateTimeOffset? NextAlertAt(int leadMinutes)
    {
        DateTimeOffset leaveBy = LeaveTime();
        List<DateTimeOffset> due = [];
        if (!HeadOutSent && !IsWaitingToLeave)
        {
            due.Add(leaveBy - Lead(leadMinutes));
        }

        if (!LeaveNowSent && !IsWaitingToLeave)
        {
            due.Add(leaveBy);
        }

        if (GetOffAlerts < UnstrandedRides)
        {
            due.Add(GetOffAt(Rides[GetOffAlerts]));
        }

        return due.Count == 0 ? null : due.Min();
    }

    /// <summary>True once <see cref="EndGrace"/> has passed since the trip's arrival.</summary>
    public bool IsOver(DateTimeOffset now) => now >= Trip.Arrives + EndGrace;

    /// <summary>
    /// Where to plan the rest of the trip from when <see cref="NeedsReplan"/>: once the rider has left, the stop of
    /// the first ride they haven't taken (a missed ride counts as not taken); before that, null, for the origin.
    /// </summary>
    public GeoPoint? ReplanFrom(DateTimeOffset now)
    {
        int ride = LeftAt is null ? -1 : FirstUntaken(now);
        return ride < 0 ? null : Itinerary.Legs.OfType<RideLeg>().ElementAt(ride).Lines[Rides[ride].Line].BoardAt.Location;
    }

    /// <summary>
    /// Continues on the best trip of <paramref name="plan"/>, planned again from <see cref="ReplanFrom"/>, as soon
    /// as the rider can be there. Keeps the rides taken, the walks to where the new plan starts, and what the
    /// rider was told, so leaving isn't announced again; a rider who'd been told a time is now told the new
    /// one (<see cref="AnnouncedRide"/>). Before leaving, the new trip replaces this one.
    /// </summary>
    /// <returns>Null when the plan has no trip left to make.</returns>
    public TripProgress? Continue(TripPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int from = LeftAt is null ? 0 : FirstUntaken(now);
        if (from < 0)
        {
            return null;
        }

        DateTimeOffset ready = LeftAt is null ? Later(NotBefore, now) : Later(ReachesStop(from), now);
        if (LiveTrip.Best(plan, Known, ready) is not { } next)
        {
            return null;
        }

        TripLeg[] kept = LeftAt is null ? [] : [.. LegsBefore(from)];
        TripProgress progress = this with
        {
            Itinerary = kept.Length == 0 ? next.Itinerary : new TripItinerary([.. kept, .. next.Itinerary.Legs], Itinerary.Departs, next.Itinerary.Arrives),
            Rides = [.. Rides.Take(from), .. next.Legs.OfType<LiveRide>().Select(Choice)],
            StrandedRide = -1,
            GetOffAlerts = Math.Min(GetOffAlerts, from),
        };
        int announced = progress.NextRide(now);
        return IsEngaged
            ? progress with { AnnouncedRide = announced, AnnouncedDeparture = announced >= 0 ? progress.Rides[announced].Departs : null }
            : progress;
    }

    private static TimeSpan Lead(int minutes) => TimeSpan.FromMinutes(minutes);

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset GetOffAt(RideChoice ride) =>
        ride.Alights - ActionLead > ride.Departs ? ride.Alights - ActionLead : ride.Departs;

    private static RideChoice Choice(LiveRide ride) => new(
        IndexWhere(ride.Ride.Lines, l => ReferenceEquals(l, ride.Line)),
        ride.Departure?.TripId ?? ride.Line.Departures.FirstOrDefault(d => d.Departs == ride.Starts)?.TripId,
        ride.Starts,
        ride.Ends);

    private static int IndexWhere<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (predicate(items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The chosen trip among the known departures at the line's boarding stop.</summary>
    private static Arrival? Departure(ILookup<StopLine, Arrival> known, RideLine line, RideChoice choice)
    {
        StopLine boarding = new(line.BoardAt.Id, line.Route.Id, line.Direction.Id);
        return TripMatch.Find(known[boarding], boarding, choice.TripId, choice.Departs);
    }

    /// <summary>When the trip reaches the line's stop to get off at, if that stop's departures list it.</summary>
    private static DateTimeOffset? Alighting(ILookup<StopLine, Arrival> known, RideLine line, string? tripId, DateTimeOffset departs)
    {
        if (tripId is null || line.AlightAt is not { } stop)
        {
            return null;
        }

        return known[new StopLine(stop.Id, line.Route.Id, line.Direction.Id)]
            .FirstOrDefault(a => a.TripId == tripId && a.Status != ArrivalStatus.Canceled && a.PredictedTime > departs)?.PredictedTime;
    }

    /// <summary>When the rider leaves: when they left, else the first ride's departure less the walk to it (a walk-only trip's start).</summary>
    private DateTimeOffset LeaveTime()
    {
        if (LeftAt is { } left)
        {
            return left;
        }

        if (Rides.Count == 0)
        {
            return NotBefore;
        }

        TimeSpan walk = Itinerary.Legs.TakeWhile(l => l is not RideLeg).OfType<WalkLeg>().Aggregate(TimeSpan.Zero, (total, w) => total + w.Duration);
        return Rides[0].Departs - walk;
    }

    private TripProgress WithKnown(PredictionSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.FetchedAt < KnownAt)
        {
            return this;
        }

        HashSet<string> stops = new(Itinerary.Legs.OfType<RideLeg>()
            .SelectMany(r => r.Lines)
            .SelectMany(l => new[] { l.BoardAt.Id, l.AlightAt?.Id })
            .OfType<string>(), StringComparer.Ordinal);
        return this with { Known = [.. snapshot.Arrivals.Where(a => stops.Contains(a.StopId))], KnownAt = snapshot.FetchedAt };
    }

    /// <summary>
    /// Re-times every ride not yet taken. Each keeps its trip, at its latest known time, while the rider
    /// can be at its stop for it; otherwise it takes the departure of any of its lines that gets off
    /// soonest (<see cref="LiveTrip.Board"/>). A taken ride only updates when it gets off. A ride counts as taken
    /// only after the rides before it, and only if the rider could have been at its stop by then.
    /// </summary>
    /// <returns>The rides; whether any took another departure; and the first with none left, or -1 (from it on, they're left as they were).</returns>
    private (RideChoice[] Rides, bool Switched, int Stranded) Retime(DateTimeOffset now)
    {
        ILookup<StopLine, Arrival> known = Known.ToLookup(StopLine.Of);
        HashSet<string> missed = new(MissedTrips, StringComparer.Ordinal);
        RideChoice[] rides = [.. Rides];
        bool switched = false;
        bool takenSoFar = true;
        DateTimeOffset ready = LeftAt ?? (NotBefore > now ? NotBefore : now);
        int index = 0;
        foreach (TripLeg leg in Itinerary.Legs)
        {
            if (leg is WalkLeg walk)
            {
                ready += walk.Duration;
                continue;
            }

            if (leg is not RideLeg ride)
            {
                continue;
            }

            RideChoice choice = rides[index];
            RideLine line = ride.Lines[choice.Line];
            bool isMissed = missed.Contains(LiveTrip.TripKey(choice.TripId, choice.Departs));
            if (takenSoFar && !isMissed && choice.Departs <= now && choice.Departs >= ready)
            {
                rides[index] = choice with { Alights = Alighting(known, line, choice.TripId, choice.Departs) ?? choice.Alights };
            }
            else
            {
                takenSoFar = false;
                DateTimeOffset earliest = ready > now ? ready : now;
                Arrival? departure = isMissed ? null : Departure(known, line, choice);
                DateTimeOffset departs = departure?.PredictedTime ?? choice.Departs;
                if (!isMissed && departure?.Status != ArrivalStatus.Canceled && departs >= earliest)
                {
                    DateTimeOffset alights = departure is null ? choice.Alights : departs + LiveTrip.RideTime(ride, line, departure);
                    rides[index] = choice with { Departs = departs, Alights = Alighting(known, line, choice.TripId, departs) ?? alights };
                }
                else if (LiveTrip.Board(ride, known, earliest, missed) is { } next)
                {
                    RideChoice taken = Choice(next);
                    rides[index] = taken with { Alights = Alighting(known, next.Line, taken.TripId, taken.Departs) ?? taken.Alights };
                    switched = true;
                }
                else
                {
                    return (rides, switched, index);
                }
            }

            ready = rides[index++].Alights;
        }

        return (rides, switched, -1);
    }

    /// <summary>
    /// Raises the latest clock alert that's due, else a change the rider was told about, and records what they
    /// were told. Rides waiting for a new plan raise nothing, and a trip that can't be made doesn't send the rider off.
    /// </summary>
    private TripUpdate Alert(int leadMinutes, DateTimeOffset now, bool switched)
    {
        DateTimeOffset leaveBy = LeaveTime();
        bool headOutDue = !IsWaitingToLeave && now >= leaveBy - Lead(leadMinutes);
        List<(TripAlert Alert, DateTimeOffset DueAt)> due = [];
        if (!HeadOutSent && headOutDue)
        {
            due.Add((TripAlert.HeadOut, leaveBy - Lead(leadMinutes)));
        }

        if (!LeaveNowSent && LeftAt is { } left)
        {
            due.Add((TripAlert.LeaveNow, left));
        }

        int getOff = GetOffAlerts;
        while (getOff < UnstrandedRides && now >= GetOffAt(Rides[getOff]))
        {
            due.Add((TripAlert.GetOffSoon, GetOffAt(Rides[getOff++])));
        }

        TripProgress progress = this with
        {
            HeadOutSent = HeadOutSent || headOutDue,
            LeaveNowSent = LeaveNowSent || LeftAt is not null,
            GetOffAlerts = getOff,
        };

        int next = NextRide(now);
        (TripAlert Alert, DateTimeOffset DueAt)? raised = null;
        if (due.Count > 0)
        {
            raised = due.MaxBy(d => d.DueAt);
        }
        else if (progress.IsEngaged && switched)
        {
            raised = (TripAlert.ConnectionChanged, now);
        }
        else if (progress.IsEngaged && next >= 0 && next == AnnouncedRide && AnnouncedDeparture is { } told
            && (Rides[next].Departs - told).Duration() >= TrackedBus.ShiftThreshold)
        {
            raised = (TripAlert.RideChanged, now);
        }

        if (progress.IsEngaged && (raised is not null || next != AnnouncedRide))
        {
            progress = progress with { AnnouncedRide = next, AnnouncedDeparture = next >= 0 ? Rides[next].Departs : null };
        }

        return new TripUpdate(progress, raised?.Alert, raised?.DueAt);
    }

    /// <summary>The first ride not yet taken; -1 when every ride is.</summary>
    private int NextRide(DateTimeOffset now) => IndexWhere(Rides, r => r.Departs > now);

    /// <summary>Like <see cref="NextRide"/>, but a missed ride counts as not taken even after it left.</summary>
    private int FirstUntaken(DateTimeOffset now)
    {
        HashSet<string> missed = new(MissedTrips, StringComparer.Ordinal);
        return IndexWhere(Rides, r => r.Departs > now || missed.Contains(LiveTrip.TripKey(r.TripId, r.Departs)));
    }

    /// <summary>When the rider, having left, can be at <paramref name="ride"/>'s stop: after the rides before it and the walks between.</summary>
    private DateTimeOffset ReachesStop(int ride)
    {
        DateTimeOffset at = LeaveTime();
        int index = 0;
        foreach (TripLeg leg in Itinerary.Legs)
        {
            if (leg is WalkLeg walk)
            {
                at += walk.Duration;
            }
            else if (leg is RideLeg)
            {
                if (index == ride)
                {
                    break;
                }

                at = Rides[index++].Alights;
            }
        }

        return at;
    }

    /// <summary>The itinerary's legs before <paramref name="ride"/>'s ride leg.</summary>
    private IEnumerable<TripLeg> LegsBefore(int ride)
    {
        int index = 0;
        foreach (TripLeg leg in Itinerary.Legs)
        {
            if (leg is RideLeg && index++ == ride)
            {
                yield break;
            }

            yield return leg;
        }
    }

    /// <summary>
    /// Each decision still to come with the check that comes before it (see <see cref="NextCheck"/>). Rides
    /// waiting for a new plan have none: planning again decides them.
    /// </summary>
    private IEnumerable<(DateTimeOffset At, DateTimeOffset Decision)> Checks(int leadMinutes)
    {
        int count = UnstrandedRides;
        if (count == 0 || IsWaitingToLeave)
        {
            yield break;
        }

        LiveTrip trip = Trip;
        LiveRide[] rides = [.. trip.Legs.OfType<LiveRide>()];
        DateTimeOffset headOut = trip.LeaveBy - Lead(leadMinutes);
        yield return (headOut - HeadOutCheck, headOut);
        yield return (trip.LeaveBy - LeaveCheck, trip.LeaveBy);
        TimeSpan walk = trip.Legs.TakeWhile(l => l is not LiveRide).Aggregate(TimeSpan.Zero, (total, l) => total + (l.Ends - l.Starts));
        if (walk > LongWalk)
        {
            yield return (rides[0].Starts - BoardCheck, rides[0].Starts);
        }

        for (int i = 0; i < count; i++)
        {
            if (i > 0 && rides[i].Starts - rides[i - 1].Ends > LongWait)
            {
                yield return (rides[i].Starts - BoardCheck, rides[i].Starts);
            }

            if (rides[i].Ends - rides[i].Starts > LongRide)
            {
                yield return (rides[i].Ends - GetOffCheck, rides[i].Ends - ActionLead);
            }
        }
    }
}

[JsonSerializable(typeof(TripProgress))]
public sealed partial class TripProgressJsonContext : JsonSerializerContext;
