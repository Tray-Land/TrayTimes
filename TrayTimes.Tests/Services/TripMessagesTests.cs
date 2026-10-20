using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Services;

/// <summary>The trip's words as the rider sees them and Narrator reads them, from the app's English strings.</summary>
[TestClass]
public sealed class TripMessagesTests
{
    private const int Lead = 5;

    // Monday, October 19, 2026, in the local time zone, so clock times read as written.
    private static readonly DateTimeOffset Today = new(new DateTime(2026, 10, 19, 8, 0, 0));
    private static readonly TransitRoute Fourteen = new("R:14", "14", "Forest Home", null);
    private static readonly TransitRoute Thirty = new("R:30", "30", "Sherman", null);
    private static readonly TransitDirection North = new("0", "Northbound");
    private static readonly FavoritePlace Home = FavoritePlace.ForKind(PlaceKind.Home, new Place("Home address", 43, -88));
    private static readonly FavoritePlace Work = FavoritePlace.ForKind(PlaceKind.Work, new Place("Work address", 43.2, -88));

    [TestMethod]
    public void Status_BeforeLeaving_SaysWhenToLeaveAndTheWalkToTheFirstRide()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        TripStatus status = Status(trip, At(-6));

        // Assert
        Assert.AreEqual("Leave in 6 min", status.Title);
        Assert.AreEqual("Walk 5 min to Oak & 5th for the 14", status.Detail);
        Assert.AreEqual("Leave in 6 minutes. Walk 5 minutes to Oak & 5th for the 14", status.Spoken);
    }

    [TestMethod]
    public void Status_OneMinuteBeforeLeaving_ReadsOneMinute()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(-1));

        // Assert
        Assert.AreEqual("Leave in 1 min", status.Title);
        Assert.AreEqual("Leave in 1 minute. Walk 5 minutes to Oak & 5th for the 14", status.Spoken);
    }

    [TestMethod]
    public void Status_WalkingToTheStop_SaysTheWalkLeft()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(3));

        // Assert
        Assert.AreEqual("The 14 leaves in 2 min", status.Title);
        Assert.AreEqual("Walk 2 min to Oak & 5th for the 14", status.Detail);
    }

    [TestMethod]
    public void Status_WaitingForTheTransfer_SaysWhereToBoardAndWhen()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(23));

        // Assert
        Assert.AreEqual("The 30 leaves in 2 min", status.Title);
        Assert.AreEqual($"Board at Lake St, leaving {Clock(At(25))}", status.Detail);
    }

    [TestMethod]
    public void Status_OnTheFirstRide_SaysWhereToGetOffAndTheWalkToTheNext()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(11));

        // Assert
        Assert.AreEqual("Get off at State St in 9 min", status.Title);
        Assert.AreEqual($"Then walk 2 min to the 30 at {Clock(At(25))}", status.Detail);
        Assert.AreEqual($"Get off at State St in 9 minutes. Then walk 2 minutes to the 30 at {Clock(At(25))}", status.Spoken);
    }

    [TestMethod]
    public void Status_OnTheLastRide_SaysTheWalkToThePlace()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(30));

        // Assert
        Assert.AreEqual("Get off at Pine Ave in 5 min", status.Title);
        Assert.AreEqual($"Then walk 3 min to Work, arriving {Clock(At(38))}", status.Detail);
    }

    [TestMethod]
    public void Status_LastWalk_SaysWhenItArrivesAndTheWalkLeft()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(36));

        // Assert
        Assert.AreEqual($"Arrive at Work at {Clock(At(38))}", status.Title);
        Assert.AreEqual("Walk 2 min to Work", status.Detail);
    }

    [TestMethod]
    public void Status_Arrived_SaysSoAndThatTheTripEndsOnItsOwn()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = Status(Trip(), At(39));

        // Assert
        Assert.AreEqual("You've arrived at Work", status.Title);
        Assert.AreEqual("The trip ends on its own in a few minutes.", status.Detail);
    }

    [TestMethod]
    public void Status_AwaitingANewPlan_SaysItIsLookingForAnotherWayWithoutACountdown()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        TripStep stranded = new(TripStepKind.Transfer, 1, 2, At(22), IsUrgent: false) { AwaitsReplan = true };

        // Act
        TripStatus status = TripMessages.Status(Trip(), stranded, At(21));

        // Assert
        Assert.AreEqual("Looking for another way…", status.Title);
        StringAssert.StartsWith(status.Detail, "No bus or train left can make this trip.");
        StringAssert.StartsWith(status.Spoken, "Looking for another way. No bus or train");
    }

    [TestMethod]
    public void Status_TripTomorrow_SaysWhenItLeavesAndThatItIsScheduled()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        DateTimeOffset tomorrow = At(1440);
        TripProgress progress = TripProgress.Start(LiveTrip.Scheduled(Itinerary(1440)), new TripTime(TripTimeMode.LeaveAt, tomorrow), null, Lead, Today);
        ActiveTrip trip = new(progress, Leg(), new TripDestination(Work.Location));

        // Act
        TripStatus status = TripMessages.Status(trip, progress.Step(Today, Lead), Today);

        // Assert
        Assert.AreEqual($"Leave Tue {Clock(tomorrow)}", status.Title);
        Assert.AreEqual("Scheduled. Tray Times checks the times shortly before you leave.", status.Detail);
    }

    [TestMethod]
    [DataRow(-6, "leave in 6 min")]
    [DataRow(3, "the 14 leaves in 2 min")]
    [DataRow(11, "get off in 9 min")]
    [DataRow(39, "arrived")]
    public void CardTitle_DuringTheTrip_SaysWhatIsNext(int minutes, string next)
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        string title = TripMessages.CardTitle(trip, trip.Progress.Step(At(minutes), Lead), At(minutes));

        // Assert
        Assert.AreEqual($"Trip to Work · {next}", title);
    }

    [TestMethod]
    public void CardTitle_WaitingToBeResumed_AsksToResume()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string title = TripMessages.CardTitle(Trip(), step: null, At(11));

        // Assert
        Assert.AreEqual("Trip to Work · resume or end it?", title);
    }

    [TestMethod]
    public void CardName_OnARide_ReadsTheTripAndItsStatus()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        string name = TripMessages.CardName(trip, trip.Progress.Step(At(11), Lead), At(11));

        // Assert
        Assert.AreEqual($"Trip to Work. Get off at State St in 9 minutes. Then walk 2 minutes to the 30 at {Clock(At(25))}", name);
    }

    [TestMethod]
    public void Resume_SavedTrip_AsksToResumeIt()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        TripStatus status = TripMessages.Resume(Trip());

        // Assert
        Assert.AreEqual("Resume your trip to Work?", status.Title);
        Assert.AreEqual("Resume your trip to Work? Tray Times closed while you were on your way.", status.Spoken);
    }

    [TestMethod]
    public void TimeLabel_EachMode_IsShortWithTheDayOnlyWhenNotToday()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        DateTimeOffset evening = Today.AddHours(9);
        DateTimeOffset tomorrowMorning = Today.AddDays(1).AddHours(1);

        // Act
        string now = TripMessages.TimeLabel(default, Today);
        string leaveAt = TripMessages.TimeLabel(new TripTime(TripTimeMode.LeaveAt, evening), Today);
        string arriveBy = TripMessages.TimeLabel(new TripTime(TripTimeMode.ArriveBy, tomorrowMorning), Today);

        // Assert
        Assert.AreEqual("Leave now", now);
        Assert.AreEqual($"Leave {Clock(evening)}", leaveAt);
        Assert.AreEqual($"Arrive by Tue {Clock(tomorrowMorning)}", arriveBy);
    }

    [TestMethod]
    public void TimeName_LeaveAt_GivesTheFullDateAndTime()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        DateTimeOffset at = Today.AddDays(1).AddHours(1);

        // Act
        string name = TripMessages.TimeName(new TripTime(TripTimeMode.LeaveAt, at));

        // Assert
        Assert.AreEqual($"Leave at {at.ToString("f", CultureInfo.GetCultureInfo("en-US"))}", name);
    }

    [TestMethod]
    [DataRow(0, null)]
    [DataRow(1, "Tue")]
    [DataRow(6, "Sun")]
    [DataRow(7, "Oct 26")]
    [DataRow(-1, "Oct 18")]
    public void Day_DaysFromToday_GivesTheWeekdayWithinAWeekElseTheDate(int days, string? expected)
    {
        // Act
        string? day = TripMessages.Day(Today.AddDays(days), Today, CultureInfo.GetCultureInfo("en-US"));

        // Assert
        Assert.AreEqual(expected, day);
    }

    [TestMethod]
    public void GoName_KnownStart_SaysWhereToAndWhenItLeaves()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        string name = TripMessages.GoName(trip.Progress.Trip, Leg(), At(-10));

        // Assert
        Assert.AreEqual($"Start trip to Work, leaving {Clock(At(0))}", name);
    }

    [TestMethod]
    public void GoName_GuessedStart_SaysWhereItStartsFrom()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        string name = TripMessages.GoName(trip.Progress.Trip, Leg(isGuess: true), At(-10));

        // Assert
        Assert.AreEqual($"Start trip from Home to Work, leaving {Clock(At(0))}", name);
    }

    [TestMethod]
    public void GoName_LeaveTimePassed_SaysLeavingNow()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ActiveTrip trip = Trip();

        // Act
        string name = TripMessages.GoName(trip.Progress.Trip, Leg(), At(1));

        // Assert
        Assert.AreEqual("Start trip to Work, leaving now", name);
    }

    [TestMethod]
    [DataRow(false, "arrive {0}")]
    [DataRow(true, "from Home · arrive {0}")]
    public void Arrival_StartGuessedOrNot_NamesTheStartOnlyWhenGuessed(bool isGuess, string expected)
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string arrival = TripMessages.Arrival(Leg(isGuess), At(38), Today);

        // Assert
        Assert.AreEqual(string.Format(CultureInfo.InvariantCulture, expected, Clock(At(38))), arrival);
    }

    [TestMethod]
    public void Started_LeavingSoon_SaysTheTripIsFollowed()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string caption = TripMessages.Started(Trip().Progress.Trip, Leg(), At(-10));

        // Assert
        Assert.AreEqual("Following your trip to Work. Tray Times will tell you when to leave.", caption);
    }

    [TestMethod]
    public void Started_TripTomorrowFromAGuessedStart_SaysItIsScheduledAndWhereFrom()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        LiveTrip tomorrow = LiveTrip.Scheduled(Itinerary(1440));

        // Act
        string caption = TripMessages.Started(tomorrow, Leg(isGuess: true), Today);

        // Assert
        Assert.AreEqual($"Your trip from Home to Work is scheduled to leave Tue {Clock(At(1440))}. Tray Times will tell you when it's time.", caption);
    }

    [TestMethod]
    public void TrackInstead_DuringATrip_AsksBeforeEndingIt()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string question = TripMessages.TrackInstead(Trip());

        // Assert
        Assert.AreEqual("End your trip to Work and track this bus instead?", question);
    }

    [TestMethod]
    public void Alert_GetOffSoonBeforeATransfer_SaysTheWalkToTheNextRide()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        TripProgress progress = Trip().Progress;
        for (int minute = -10; minute <= 18; minute++)
        {
            progress = progress.Advance(null, Lead, At(minute)).Progress;
        }

        // Act
        (string title, string text) = TripMessages.Alert(new ActiveTrip(progress, Leg(), new TripDestination(Work.Location)), TripAlert.GetOffSoon, progress, At(18));

        // Assert
        Assert.AreEqual("Get off at State St in 2 min", title);
        Assert.AreEqual($"Then walk 2 min to the 30 at {Clock(At(25))}", text);
    }

    private static TripStatus Status(ActiveTrip trip, DateTimeOffset now) => TripMessages.Status(trip, trip.Progress.Step(now, Lead), now);

    /// <summary>The transfer trip, started at 7:50: leave 8:00, walk to Oak &amp; 5th, the 14 to State St, the 30 from Lake St to Pine Ave, and walk to Work at 8:38.</summary>
    private static ActiveTrip Trip()
    {
        TripProgress progress = TripProgress.Start(LiveTrip.Compute(Itinerary(), [], At(-10))!, default, null, Lead, At(-10));
        return new ActiveTrip(progress, Leg(), new TripDestination(Work.Location));
    }

    private static CommuteLeg Leg(bool isGuess = false) => new(Home, Home.Location, Work, isGuess);

    /// <summary>Walk 5 min, the 14 (8:05–8:20), walk 2 min, wait, the 30 (8:25–8:35), walk 3 min: 8:00 to 8:38 as planned, shifted by <paramref name="offset"/> minutes.</summary>
    private static TripItinerary Itinerary(int offset = 0) => new(
    [
        Walk(offset, offset + 5),
        new RideLeg(At(offset + 5), At(offset + 20), [Line(Fourteen, "Oak & 5th", "State St", offset + 5, offset + 20, "14-1")]),
        Walk(offset + 20, offset + 22),
        new RideLeg(At(offset + 25), At(offset + 35), [Line(Thirty, "Lake St", "Pine Ave", offset + 25, offset + 35, "30-1")]),
        Walk(offset + 35, offset + 38),
    ], At(offset), At(offset + 38));

    private static WalkLeg Walk(int from, int to) => new(At(from), At(to), (to - from) * 80, []);

    private static RideLine Line(TransitRoute route, string board, string alight, int departs, int alights, string tripId) => new(
        route,
        North,
        new TransitStop(board, board, new GeoPoint(43, -88)),
        new TransitStop(alight, alight, new GeoPoint(43.1, -88)),
        4,
        [],
        [new PlannedDeparture(At(departs), At(alights), IsRealTime: false, tripId)]);

    private static DateTimeOffset At(int minutes) => Today.AddMinutes(minutes);

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.GetCultureInfo("en-US"));
}
