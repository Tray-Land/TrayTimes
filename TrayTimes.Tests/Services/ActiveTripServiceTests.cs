using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class ActiveTripServiceTests
{
    /// <summary>When the test trip leaves: 10 minutes after the clock starts, when Go is pressed.</summary>
    private static readonly DateTimeOffset Leave = RequestTestClock.Start.AddMinutes(10);
    private static readonly TransitRoute Fourteen = new("14", "14", "Forest Home", null);
    private static readonly TransitRoute Thirty = new("30", "30", "Sherman", null);
    private static readonly FavoritePlace Work = FavoritePlace.ForKind(PlaceKind.Work, new Place("Fixture address", 43.2, -88));

    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        ApiUsageService.StartOver();
        NotificationService.Reset();
        ConnectivityService.Reset();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SettingsService.Reset();
        ConnectivityService.Reset();
    }

    [TestMethod]
    public async Task Start_TransferTrip_ChecksOnceBeforeEachDecisionAsAnUrgentActiveTrip()
    {
        // Arrange
        using Fixture f = new();
        List<int> checks = [];
        List<TransitPriority> priorities = [];
        f.Handler.RespondAsync = (request, _) =>
        {
            checks.Add(Minutes(f.Clock.GetUtcNow()));
            priorities.Add(TransitRequestDiagnostics.CurrentPriority);
            return Task.FromResult(DeparturesFor(request, AsPlanned()));
        };
        f.Start();

        // Act
        await f.RunToEndAsync();

        // Assert: before heading out, before leaving, and before getting off each ride; the walk and the wait are too short for checks.
        CollectionAssert.AreEqual(new[] { -6, -2, 15, 30 }, checks);
        Assert.AreEqual(4, ApiUsageService.GetSummary().Total.Today);
        Assert.IsTrue(priorities.All(p => p == TransitPriority.Urgent));
        Assert.IsTrue(f.Service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)
            .All(o => o.Consumer == TransitConsumer.ActiveTrip && o.Outcome == TransitRequestOutcome.Success));
        Assert.IsNull(f.Trips.Current);
        Assert.AreEqual(At(43), f.Clock.GetUtcNow());
    }

    [TestMethod]
    public async Task Start_TransferTrip_SendsEachAlertOnceWithItsButtons()
    {
        // Arrange
        using Fixture f = new();
        f.Respond(AsPlanned());
        List<DateTimeOffset?> due = [];
        f.Trips.Alerted += (_, update) => due.Add(update.DueAt);
        f.Start();

        // Act
        await f.RunToEndAsync();

        // Assert: I missed it only while heading to a ride; End trip on every alert.
        CollectionAssert.AreEqual(new[] { "TripHeadOutTitleFormat", "TripLeaveNowTitleFormat", "TripGetOffSoonTitleFormat", "TripGetOffSoonTitleFormat" },
            NotificationService.Titles);
        CollectionAssert.AreEqual(new[] { "EndTrip", "MissedIt EndTrip", "EndTrip", "EndTrip" },
            NotificationService.Shown.Select(s => string.Join(' ', s.Buttons)).ToArray());
        CollectionAssert.AreEqual(new DateTimeOffset?[] { At(-5), At(0), At(18), At(33) }, due);
    }

    [TestMethod]
    public async Task MapView_TripAdvances_KeepsTheCameraWithoutPublishingAChange()
    {
        // Arrange: the flyout can be destroyed while the service follows the trip.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        int changes = 0;
        f.Trips.Changed += (_, _) => ++changes;
        MapView view = new(43.1, -88, 16, 35, 20);

        // Act
        f.Trips.MapView = view;
        int cameraChanges = changes;
        await f.RunUntilAsync(At(10));

        // Assert
        Assert.AreEqual(0, cameraChanges);
        Assert.AreEqual(TripStepKind.Ride, f.Trips.Step!.Kind);
        Assert.AreEqual(view, f.Trips.MapView);
    }

    [TestMethod]
    public void Start_ReplacesATrip_ForgetsItsMapCamera()
    {
        // Arrange
        using Fixture f = new();
        f.Start();
        f.Trips.MapView = new MapView(43.1, -88, 16);

        // Act
        f.Start(OneRideTrip());

        // Assert
        Assert.IsNull(f.Trips.MapView);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task End_TripEnds_ForgetsItsMapCamera(bool automatically)
    {
        // Arrange
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        f.Trips.MapView = new MapView(43.1, -88, 16);

        // Act
        if (automatically)
        {
            await f.RunToEndAsync();
        }
        else
        {
            f.Trips.End();
        }

        // Assert
        Assert.IsNull(f.Trips.MapView);
    }

    [TestMethod]
    public async Task Refresh_SharedDeparturesUnderAMinuteOld_SkipsTheCheck()
    {
        // Arrange: the open flyout fetched the trip's stops at 7:53:30.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        await f.RunUntilAsync(At(-7));
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.FlyoutOpened))
        {
            await f.Predictions.GetAsync(["a", "a-off", "b", "b-off"], TimeSpan.FromMinutes(1));
        }

        // Act: the head-out check at 7:54.
        await f.TickAsync();

        // Assert
        Assert.AreEqual(At(-6), f.Clock.GetUtcNow());
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task OnNetworkStatusChanged_ChecksFailedWithoutAConnection_ChecksOnceWhenItComesBack()
    {
        // Arrange: no connection through the head-out and leave checks.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        f.GoOffline();
        await f.RunUntilAsync(At(-1));
        int failed = f.Handler.Attempts;

        // Act: the network comes back, and Windows says so twice.
        await f.ActAsync(f.GoOnline);
        ConnectivityService.SetOnline(true);

        // Assert: each decision's check was tried once; one more when the network came back.
        Assert.AreEqual(2, failed);
        Assert.AreEqual(3, f.Handler.Attempts);
        Assert.HasCount(1, NotificationService.Titles, "Head out still came by the clock.");
    }

    [TestMethod]
    public async Task Refresh_WindowsReportsNoInternet_StillChecks()
    {
        // Arrange: a hotspot Windows calls constrained, which works.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        ConnectivityService.IsInternetAvailable = false;

        // Act
        await f.RunUntilAsync(At(-1));

        // Assert: the status is a hint; the head-out and leave checks went out.
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task WakeAsync_AfterSleepingThroughLeaving_RaisesOnlyTheLatestAlertAndChecksOnce()
    {
        // Arrange: asleep from 7:50 to 8:16, so the timer never fired.
        using Fixture f = new();
        f.Respond(AsPlanned());
        List<TripUpdate> alerts = [];
        f.Trips.Alerted += (_, update) => alerts.Add(update);
        f.Start();
        f.Clock.Advance(TimeSpan.FromMinutes(26));

        // Act
        await f.RunAsync(f.Trips.WakeAsync);

        // Assert: on the 14 now; the overdue check before getting off ran.
        Assert.AreEqual(TripStepKind.Ride, f.Trips.Step!.Kind);
        CollectionAssert.AreEqual(new[] { "TripLeaveNowTitleFormat" }, NotificationService.Titles);
        Assert.AreEqual(At(0), alerts.Single().DueAt);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task MissedAsync_OnTheWayToTheStop_RetimesFromTheNextDepartureWithoutACall()
    {
        // Arrange
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        await f.RunUntilAsync(At(3));
        int calls = f.Handler.Attempts;

        // Act
        await f.RunAsync(f.Trips.MissedAsync);

        // Assert
        Assert.AreEqual("14-2", f.Trips.Current!.Progress.Rides[0].TripId);
        Assert.AreEqual("30-2", f.Trips.Current.Progress.Rides[1].TripId);
        Assert.AreEqual(calls, f.Handler.Attempts);
        Assert.AreEqual("TripConnectionChangedTitleFormat", NotificationService.Titles[^1]);
        CollectionAssert.AreEqual(new[] { NotificationAction.MissedIt, NotificationAction.EndTrip }, NotificationService.Shown[^1].Buttons);
    }

    [TestMethod]
    public async Task Refresh_ConnectionLostAtTheTransfer_PlansAgainAndContinuesOnTheNewTrip()
    {
        // Arrange: from 8:14, the 14 reaches "a-off" at 8:45, after the last 30 leaves "b" at 8:40. A new plan from "b" has a 30 from "c" at 8:52.
        using Fixture f = new();
        List<int> plans = [];
        string Plan()
        {
            plans.Add(Minutes(f.Clock.GetUtcNow()));
            return OneRidePlan("c", "30", At(52), At(62), "30-9");
        }

        f.Respond(AsPlanned(), Plan);
        f.Start();
        await f.RunUntilAsync(At(14));
        f.Respond([.. AsPlanned().Where(d => d.StopId != "a-off"), new TestDeparture("a-off", "14", At(45), TripId: "14-1")], Plan);

        // Act: the check before getting off the 14, at 8:15, and the rest of the trip.
        await f.RunUntilAsync(At(15));
        ActiveTrip? continued = f.Trips.Current;
        await f.RunToEndAsync();

        // Assert
        CollectionAssert.AreEqual(new[] { 15 }, plans);
        Assert.IsNotNull(continued);
        CollectionAssert.AreEqual(new[] { "14-1", "30-9" }, continued.Progress.Rides.Select(r => r.TripId).ToArray());
        Assert.AreEqual(At(65), continued.Progress.Trip.Arrives);
        Assert.AreEqual(1, NotificationService.Titles.Count(t => t == "TripConnectionChangedTitleFormat"));
        Assert.AreEqual(At(70), f.Clock.GetUtcNow());
    }

    [TestMethod]
    public async Task MissedAsync_NoLaterDeparture_PlansAgainAtMostOncePerFifteenMinutesAndSaysSoOnce()
    {
        // Arrange: on the only 14, which the rider missed; no new plan can get them there.
        using Fixture f = new();
        List<int> plans = [];
        f.Respond([new TestDeparture("a", "14", At(5), TripId: "14-1"), new TestDeparture("a-off", "14", At(40), TripId: "14-1")],
            plan: () =>
            {
                plans.Add(Minutes(f.Clock.GetUtcNow()));
                return """{"results":[]}""";
            });
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));

        // Act
        await f.RunAsync(f.Trips.MissedAsync);
        await f.RunToEndAsync();

        // Assert: planned again at 8:06, 8:21, and 8:36, until the trip ends at 8:48.
        CollectionAssert.AreEqual(new[] { 6, 21, 36 }, plans);
        Assert.AreEqual(1, NotificationService.Titles.Count(t => t == "TripNoConnectionTitleFormat"));
        Assert.AreEqual(At(48), f.Clock.GetUtcNow());
    }

    [TestMethod]
    public void Start_WhileTrackingABus_StopsTracking()
    {
        // Arrange
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Tracker.Start(new Arrival("a", "14", "14", "0", "Downtown", At(5), TripId: "14-1"), "Fixture stop");

        // Act
        f.Start();

        // Assert: one tracking slot; the PC stays awake for the trip.
        Assert.IsFalse(f.Tracker.IsTracking);
        Assert.IsTrue(f.Trips.IsActive);
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates[^1]);
    }

    [TestMethod]
    public void End_KeepAwakeSetting_LetsThePCSleepAgain()
    {
        // Arrange
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Start();

        // Act
        f.Trips.End();

        // Assert
        CollectionAssert.AreEqual(new[] { EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, EXECUTION_STATE.ES_CONTINUOUS },
            PInvoke.ExecutionStates);
        Assert.IsNull(f.Trips.Current);
    }

    [TestMethod]
    public async Task Start_TripTomorrow_KeepsTheAppIconAndLetsThePCSleepUntilWithin99Minutes()
    {
        // Arrange
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        TripItinerary tomorrow = TransferTrip(offset: 1440);
        f.Trips.Start(LiveTrip.Scheduled(tomorrow), Leg(), new TripDestination(Work.Location), new TripTime(TripTimeMode.LeaveAt, At(1440)), null);
        f.Started();
        int? badgeAtGo = ActiveTripService.BadgeMinutes(f.Trips.Step!, f.Clock.GetUtcNow());
        TimeSpan firstWait = f.Timer.Interval;
        int statesAtGo = PInvoke.ExecutionStates.Count;

        // Act: until the countdown shows, 99 minutes before leaving.
        await f.RunUntilAsync(At(1440 - 99));

        // Assert: it re-reads the clock every 15 minutes meanwhile; no calls, and the PC may sleep, until then.
        Assert.IsNull(badgeAtGo);
        Assert.AreEqual(TimeSpan.FromMinutes(15), firstWait);
        Assert.AreEqual(0, statesAtGo);
        Assert.AreEqual(At(1440 - 99), f.Clock.GetUtcNow());
        Assert.AreEqual(99, ActiveTripService.BadgeMinutes(f.Trips.Step!, f.Clock.GetUtcNow()));
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates.Single());
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Start_MonthsCallsUsedUp_RunsOnTheClockWithoutChecks()
    {
        // Arrange
        using Fixture f = new(new TransitUsage(TransitBudgetPolicy.DefaultCallsPerMonth, TransitBudgetPolicy.DefaultCallsPerMonth));
        f.Respond(AsPlanned());
        f.Start();

        // Act
        await f.RunToEndAsync();

        // Assert
        Assert.AreEqual(0, f.Handler.Attempts);
        Assert.HasCount(4, NotificationService.Titles);
    }

    [TestMethod]
    public async Task RestoreAsync_SavedTripNotOver_WaitsWithoutChecksUntilResumeChecksOnce()
    {
        // Arrange: the app restarts a minute after Go.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        await f.TickAsync();
        using Fixture restarted = await f.RestartAsync();

        // Act
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));
        bool waiting = restarted.Trips.IsPendingResume && !restarted.Trips.IsActive && restarted.Trips.Step is null;
        TimeSpan untilForgotten = restarted.Timer.Interval;
        restarted.Clock.Advance(TimeSpan.FromMinutes(20));
        int whileWaiting = f.Handler.Attempts;
        await restarted.RunAsync(restarted.Trips.ResumeAsync);

        // Assert: it only wakes to see whether it's over, 15 minutes apart; Resume re-timed it by the clock (on the 14 at 8:11) and checked once.
        Assert.IsTrue(waiting);
        Assert.AreEqual(TimeSpan.FromMinutes(15), untilForgotten);
        Assert.AreEqual(0, whileWaiting);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.IsTrue(restarted.Trips.IsActive);
        Assert.AreEqual(TripStepKind.Ride, restarted.Trips.Step!.Kind);
        Assert.IsEmpty(NotificationService.Shown.Where(s => s.Buttons.Contains(NotificationAction.ResumeTrip)));
    }

    [TestMethod]
    public async Task RestoreAsync_StartupLaunch_AsksToResumeInANotification()
    {
        // Arrange
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();

        // Act
        await restarted.Trips.RestoreAsync(offerResume: true);

        // Assert
        Assert.AreEqual("TripResumeTitleFormat", NotificationService.Titles[^1]);
        CollectionAssert.AreEqual(new[] { NotificationAction.ResumeTrip, NotificationAction.EndTrip }, NotificationService.Shown[^1].Buttons);
    }

    [TestMethod]
    public async Task End_PendingResume_DeletesTheSavedTrip()
    {
        // Arrange
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.Trips.RestoreAsync(offerResume: false);

        // Act
        restarted.Trips.End();
        await restarted.Trips.FlushAsync();

        // Assert
        Assert.IsNull(restarted.Trips.Current);
        Assert.IsFalse(File.Exists(f.TripFile));
    }

    [TestMethod]
    public async Task RestoreAsync_ArrivalPassesUnanswered_ForgetsTheTrip()
    {
        // Arrange
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));

        // Act
        await restarted.RunToEndAsync();
        await restarted.Trips.FlushAsync();

        // Assert
        Assert.AreEqual(At(43), restarted.Clock.GetUtcNow());
        Assert.IsNull(restarted.Trips.Current);
        Assert.IsFalse(File.Exists(f.TripFile));
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task RestoreAsync_TripAlreadyOver_DeletesItAtLaunch()
    {
        // Arrange: the app restarts after the trip would have ended.
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        restarted.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        await restarted.Trips.RestoreAsync(offerResume: true);
        await restarted.Trips.FlushAsync();

        // Assert
        Assert.IsNull(restarted.Trips.Current);
        Assert.IsFalse(File.Exists(f.TripFile));
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task RestoreAsync_UnreadableFile_DeletesIt()
    {
        // Arrange
        using Fixture f = new();
        await File.WriteAllTextAsync(f.TripFile, "{\"Progress\":");

        // Act
        await f.Trips.RestoreAsync(offerResume: false);
        await f.Trips.FlushAsync();

        // Assert
        Assert.IsNull(f.Trips.Current);
        Assert.IsFalse(File.Exists(f.TripFile));
    }

    [TestMethod]
    public async Task MissedAsync_NoLaterDepartureOrPlan_SendsNoGetOffSoonForTheMissedRide()
    {
        // Arrange: on the only 14, which the rider missed; planning again finds nothing.
        using Fixture f = new();
        f.Respond([new TestDeparture("a", "14", At(5), TripId: "14-1"), new TestDeparture("a-off", "14", At(40), TripId: "14-1")]);
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        await f.RunAsync(f.Trips.MissedAsync);

        // Act
        await f.RunToEndAsync();

        // Assert: the rider isn't on that 14, so there's nothing to get off.
        CollectionAssert.DoesNotContain(NotificationService.Titles, "TripGetOffSoonTitleFormat");
    }

    [TestMethod]
    public async Task End_WhileAReplanIsInFlight_SendsNoNoConnectionNotice()
    {
        // Arrange: the rider missed the only 14, and the new plan is on its way.
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> plan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestDeparture[] departures = [new("a", "14", At(5), TripId: "14-1"), new("a-off", "14", At(40), TripId: "14-1")];
        f.Handler.RespondAsync = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
            ? plan.Task
            : Task.FromResult(DeparturesFor(request, departures));
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        Task missed = f.Trips.MissedAsync();

        // Act: the rider ends the trip before the plan comes back with nothing.
        f.Trips.End();
        plan.SetResult(RequestTestHandler.Response("""{"results":[]}"""));
        await missed.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsNull(f.Trips.Current);
        CollectionAssert.DoesNotContain(NotificationService.Titles, "TripNoConnectionTitleFormat");
    }

    [TestMethod]
    public async Task OnNetworkStatusChanged_CheckFailedThenNetworkReturns_ChecksOnce()
    {
        // Arrange: the head-out check at 7:54 fails (the hotspot dropped before Windows noticed), then Windows notices.
        using Fixture f = new();
        f.Handler.RespondAsync = (_, _) => throw new HttpRequestException("unreachable");
        f.Start();
        await f.RunUntilAsync(At(-6));
        int failed = f.Handler.Attempts;
        f.Respond(AsPlanned());
        ConnectivityService.SetOnline(false);

        // Act: the network comes back before the next decision's check (7:58).
        await ChangedWithinAsync(f.Trips, () => ConnectivityService.SetOnline(true));

        // Assert
        Assert.AreEqual(1, failed);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task RestoreAsync_SavedRidesWithoutTheirDepartureTime_DeletesTheFile()
    {
        // Arrange: a saved trip from an app whose ride choices are shaped differently (here, no departure time).
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        JsonNode saved = JsonNode.Parse(await File.ReadAllTextAsync(f.TripFile))!;
        foreach (JsonNode? ride in saved["Progress"]!["Rides"]!.AsArray())
        {
            ride!.AsObject().Remove("Departs");
        }

        await File.WriteAllTextAsync(f.TripFile, saved.ToJsonString());

        // Act
        await restarted.Trips.RestoreAsync(offerResume: true);
        await restarted.Trips.FlushAsync();

        // Assert
        Assert.IsNull(restarted.Trips.Current);
        Assert.IsFalse(File.Exists(f.TripFile));
    }

    [TestMethod]
    public async Task MissedAsync_NoLaterDepartureWhileOffline_WaitsForTheNetworkWithoutSpinning()
    {
        // Arrange: offline, the rider missed the only 14, so the trip needs a new plan it can't get yet.
        using Fixture f = new();
        f.Respond([new TestDeparture("a", "14", At(5), TripId: "14-1"), new TestDeparture("a-off", "14", At(40), TripId: "14-1")]);
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        int calls = f.Handler.Attempts;
        f.GoOffline();
        await f.RunAsync(f.Trips.MissedAsync);

        // Act
        int ticks = 0;
        while (f.Trips.Current is not null && ticks < 1000)
        {
            ++ticks;
            await f.TickAsync();
        }

        // Assert: a few wakes until 8:48, never a tight loop; it tried to plan again only at 8:06, 8:21, and 8:36.
        Assert.IsLessThan(60, ticks);
        Assert.AreEqual(At(48), f.Clock.GetUtcNow());
        Assert.AreEqual(calls + 3, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task OnNetworkStatusChanged_StrandedTripBackOnline_PlansAgainOnce()
    {
        // Arrange: offline when the rider missed the only 14; a new plan has a 30 from "c" at 8:20.
        using Fixture f = new();
        List<int> plans = [];
        f.Respond([new TestDeparture("a", "14", At(5), TripId: "14-1"), new TestDeparture("a-off", "14", At(40), TripId: "14-1")],
            plan: () =>
            {
                plans.Add(Minutes(f.Clock.GetUtcNow()));
                return OneRidePlan("c", "30", At(20), At(35), "30-9");
            });
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        f.GoOffline();
        await f.RunAsync(f.Trips.MissedAsync);
        TimeSpan untilNextTry = f.Timer.Interval;
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        // Act: back online at 8:07, and Windows says so twice.
        await f.ActAsync(f.GoOnline);
        ConnectivityService.SetOnline(true);

        // Assert: it would have waited until 8:21, but the network's return re-planned at once.
        Assert.AreEqual(TimeSpan.FromMinutes(15), untilNextTry);
        CollectionAssert.AreEqual(new[] { 7 }, plans);
        Assert.AreEqual("30-9", f.Trips.Current!.Progress.Rides[^1].TripId);
        Assert.AreEqual("TripConnectionChangedTitleFormat", NotificationService.Titles[^1]);
    }

    [TestMethod]
    public async Task End_WhileAResumeCheckIsInFlight_DropsTheStaleResult()
    {
        // Arrange: a resumed trip's check is on its way.
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => response.Task;
        int alerts = 0;
        restarted.Trips.Alerted += (_, _) => ++alerts;
        Task resumed = restarted.Trips.ResumeAsync();

        // Act
        restarted.Trips.End();
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.Departures(AsPlanned())));
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        await restarted.Trips.FlushAsync();

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.IsNull(restarted.Trips.Current);
        Assert.AreEqual(0, alerts);
        Assert.IsFalse(restarted.Timer.IsRunning);
        Assert.IsFalse(File.Exists(f.TripFile));
    }

    [TestMethod]
    public async Task WakeAsync_WhileACheckIsInFlight_MakesNoSecondCall()
    {
        // Arrange: a resumed trip's check is on its way.
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => response.Task;
        Task resumed = restarted.Trips.ResumeAsync();

        // Act: the PC wakes and the network changes while it's out.
        await restarted.Trips.WakeAsync();
        ConnectivityService.SetOnline(true);
        response.SetResult(RequestTestHandler.Response(RequestTestHandler.Departures(AsPlanned())));
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: the one check stands, and the trip goes on.
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.IsTrue(restarted.Trips.IsActive);
        Assert.IsTrue(restarted.Timer.IsRunning);
    }

    [TestMethod]
    public async Task Refresh_TimerFiresLateAfterSleep_CatchesUpLikeAWake()
    {
        // Arrange: asleep from 7:50 to 8:16; the timer due at 7:51 fires only after waking, and nothing called WakeAsync.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        f.Clock.Advance(TimeSpan.FromMinutes(26));

        // Act
        await f.ActAsync(f.Timer.Fire);

        // Assert
        Assert.AreEqual(TripStepKind.Ride, f.Trips.Step!.Kind);
        CollectionAssert.AreEqual(new[] { "TripLeaveNowTitleFormat" }, NotificationService.Titles);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.IsTrue(f.Timer.IsRunning);
    }

    [TestMethod]
    public async Task WakeAsync_PendingResume_MakesNoChecksOrAlerts()
    {
        // Arrange: a restart, then the PC sleeps through head-out.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));
        restarted.Clock.Advance(TimeSpan.FromMinutes(6));

        // Act: waking, a network change, and I missed it, all before Resume.
        await restarted.Trips.WakeAsync();
        ConnectivityService.SetOnline(false);
        ConnectivityService.SetOnline(true);
        await restarted.Trips.MissedAsync();

        // Assert
        Assert.AreEqual(0, f.Handler.Attempts);
        Assert.IsTrue(restarted.Trips.IsPendingResume);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task ResumeAsync_Offline_ChecksOnceWhenBackOnline()
    {
        // Arrange: resumed while offline; Resume's check fails.
        using Fixture f = new();
        f.Respond(AsPlanned());
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));
        restarted.GoOffline();
        await restarted.RunAsync(restarted.Trips.ResumeAsync);
        int offline = f.Handler.Attempts;

        // Act
        await restarted.ActAsync(restarted.GoOnline);

        // Assert
        Assert.AreEqual(1, offline);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Start_WhileASavedTripWaitsToResume_ReplacesIt()
    {
        // Arrange
        using Fixture f = new();
        f.Start(OneRideTrip());
        using Fixture restarted = await f.RestartAsync();
        await restarted.RunAsync(() => restarted.Trips.RestoreAsync(offerResume: false));

        // Act: Go on another trip.
        restarted.Start();
        await restarted.Trips.FlushAsync();

        // Assert: the new trip runs and is the one saved.
        Assert.IsTrue(restarted.Trips.IsActive);
        Assert.HasCount(2, restarted.Trips.Current!.Progress.Rides);
        using Fixture again = await restarted.RestartAsync();
        await again.Trips.RestoreAsync(offerResume: false);
        Assert.HasCount(2, again.Trips.Current!.Progress.Rides);
    }

    [TestMethod]
    public async Task Refresh_UnexpectedFailure_KeepsTheTripAndTriesAgainInAMinute()
    {
        // Arrange: the head-out check at 7:54 fails in a way nothing expects.
        using Fixture f = new();
        f.Handler.RespondAsync = (_, _) => throw new InvalidOperationException("Unexpected.");
        f.Start();
        await f.RunUntilAsync(At(-6));
        TimeSpan retryIn = f.Timer.Interval;
        f.Respond(AsPlanned());

        // Act
        await f.TickAsync();

        // Assert: still followed; a minute later, Head out came by the clock.
        Assert.IsTrue(f.Trips.IsActive);
        Assert.AreEqual(TimeSpan.FromMinutes(1), retryIn);
        Assert.AreEqual(At(-5), f.Clock.GetUtcNow());
        CollectionAssert.AreEqual(new[] { "TripHeadOutTitleFormat" }, NotificationService.Titles);
    }

    [TestMethod]
    public async Task MissedAsync_PlannedAgainMinutesAgo_SaysWhenItWillPlanAgainOnce()
    {
        // Arrange: the rider missed the only 14 and was sent to a new plan's 8:20 30 from "c"; at 8:10 they say they missed that too.
        using Fixture f = new();
        List<int> plans = [];
        f.Respond([new TestDeparture("a", "14", At(5), TripId: "14-1"), new TestDeparture("a-off", "14", At(40), TripId: "14-1")],
            plan: () =>
            {
                plans.Add(Minutes(f.Clock.GetUtcNow()));
                return OneRidePlan("c", "30", At(20), At(35), "30-9");
            });
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        await f.RunAsync(f.Trips.MissedAsync);
        await f.RunUntilAsync(At(10));

        // Act: twice.
        await f.RunAsync(f.Trips.MissedAsync);
        await f.RunAsync(f.Trips.MissedAsync);

        // Assert: no second plan before 8:21, which it says once, and the trip waits for it with no countdown.
        CollectionAssert.AreEqual(new[] { 6 }, plans);
        Assert.AreEqual(1, NotificationService.Titles.Count(t => t == "TripReplanWaitTitleFormat"));
        Assert.IsTrue(f.Trips.Step!.AwaitsReplan);
        Assert.IsNull(ActiveTripService.BadgeMinutes(f.Trips.Step, f.Clock.GetUtcNow()));
        Assert.AreEqual(TimeSpan.FromMinutes(11), f.Timer.Interval);
    }

    [TestMethod]
    public async Task MissedAsync_NothingLeft_SavesTheMissedRideBeforePlanningAgain()
    {
        // Arrange: the new plan is slow to come.
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> plan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestDeparture[] departures = [new("a", "14", At(5), TripId: "14-1"), new("a-off", "14", At(40), TripId: "14-1")];
        f.Handler.RespondAsync = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
            ? plan.Task
            : Task.FromResult(DeparturesFor(request, departures));
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));

        // Act
        Task missed = f.Trips.MissedAsync();
        await f.Trips.FlushAsync();
        JsonNode saved = JsonNode.Parse(await File.ReadAllTextAsync(f.TripFile))!;
        plan.SetResult(RequestTestHandler.Response("""{"results":[]}"""));
        await missed.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.AreEqual("14-1", (string?)saved["Progress"]!["MissedTrips"]![0]);
        Assert.AreEqual(0, (int?)saved["Progress"]!["StrandedRide"]);
    }

    [TestMethod]
    public async Task Start_WhileTheLastTripsReplanIsInFlight_StillSaysWhenTheNewOneHasNoWay()
    {
        // Arrange: the rider missed the only 14, ended that trip while its new plan was on its way, and went again later.
        using Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> firstPlan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestDeparture[] departures = [new("a", "14", At(5), TripId: "14-1"), new("a-off", "14", At(40), TripId: "14-1")];
        int plans = 0;
        f.Handler.RespondAsync = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
            ? ++plans == 1 ? firstPlan.Task : Task.FromResult(RequestTestHandler.Response("""{"results":[]}"""))
            : Task.FromResult(DeparturesFor(request, departures));
        f.Start(OneRideTrip());
        await f.RunUntilAsync(At(6));
        Task missed = f.Trips.MissedAsync();
        f.Trips.End();
        f.Start(OneRideTrip(offset: 20, tripId: "14-9"));
        firstPlan.SetResult(RequestTestHandler.Response("""{"results":[]}"""));
        await missed.WaitAsync(TimeSpan.FromSeconds(5));
        int toldForTheOldTrip = NotificationService.Titles.Count(t => t == "TripNoConnectionTitleFormat");
        await f.RunUntilAsync(At(26));

        // Act: the new trip's only 14 is missed too, and nothing can replace it.
        await f.RunAsync(f.Trips.MissedAsync);

        // Assert: the old trip's answer said nothing and didn't keep the new trip from saying so.
        Assert.AreEqual(2, plans);
        Assert.AreEqual(0, toldForTheOldTrip);
        Assert.AreEqual(1, NotificationService.Titles.Count(t => t == "TripNoConnectionTitleFormat"));
    }

    [TestMethod]
    public async Task RestoreAsync_UnfinishedSave_DeletesItsTempFile()
    {
        // Arrange: the app stopped while writing the trip.
        using Fixture f = new();
        f.Start();
        using Fixture restarted = await f.RestartAsync();
        await File.WriteAllTextAsync(f.TripFile + ".tmp", "{\"Progress\":");

        // Act
        await restarted.Trips.RestoreAsync(offerResume: false);
        await restarted.Trips.FlushAsync();

        // Assert: the finished save is still there to resume.
        Assert.IsTrue(restarted.Trips.IsPendingResume);
        Assert.IsFalse(File.Exists(f.TripFile + ".tmp"));
    }

    [TestMethod]
    public void TrackerStop_DuringATrip_KeepsThePCAwakeForTheTrip()
    {
        // Arrange: a bell during a trip (step 03 will ask first).
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Start();
        f.Tracker.Start(new Arrival("a", "14", "14", "0", "Downtown", At(5), TripId: "14-1"), "Fixture stop");

        // Act: tracking ends, which lets the PC sleep.
        f.Tracker.Stop();

        // Assert
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates[^1]);
    }

    [TestMethod]
    public void End_WhileTrackingABus_HandsKeepAwakeBackToTracking()
    {
        // Arrange
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Start();
        f.Tracker.Start(new Arrival("a", "14", "14", "0", "Downtown", At(5), TripId: "14-1"), "Fixture stop");

        // Act
        f.Trips.End();

        // Assert
        Assert.IsTrue(f.Tracker.IsTracking);
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates[^1]);
    }

    [TestMethod]
    public void ApplyKeepAwakeSetting_TrackingWithoutATrip_LeavesTrackingsStateAlone()
    {
        // Arrange
        using Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Tracker.Start(new Arrival("a", "14", "14", "0", "Downtown", At(5), TripId: "14-1"), "Fixture stop");

        // Act: as App.ApplyKeepAwakeSetting does, tracking first, then the trip.
        f.Tracker.ApplyKeepAwakeSetting();
        f.Trips.ApplyKeepAwakeSetting();

        // Assert
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates[^1]);
    }

    /// <summary>Runs <paramref name="act"/> and waits up to a second for the service to report a change; false when it doesn't.</summary>
    private static async Task<bool> ChangedWithinAsync(ActiveTripService trips, Action act)
    {
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args) => changed.TrySetResult();
        trips.Changed += OnChanged;
        try
        {
            act();
            return await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(1))) == changed.Task;
        }
        finally
        {
            trips.Changed -= OnChanged;
        }
    }

    private static DateTimeOffset At(int minutes) => Leave.AddMinutes(minutes);

    private static int Minutes(DateTimeOffset at) => (int)Math.Round((at - Leave).TotalMinutes);

    private static CommuteLeg Leg() => new(null, new GeoPoint(43, -88), Work, IsGuess: false);

    /// <summary>
    /// Walk 5 minutes to "a", ride the 14 (8:05 or 8:20, 15 minutes) to "a-off", walk 2 minutes to "b",
    /// ride the 30 (8:25 or 8:40, 10 minutes) to "b-off", and walk 3 minutes: 8:00 to 8:38.
    /// </summary>
    private static TripItinerary TransferTrip(int offset = 0) => new(
    [
        Walk(offset, offset + 5),
        new RideLeg(At(offset + 5), At(offset + 20), [Line("a", Fourteen, Planned(offset + 5, offset + 20, "14-1"), Planned(offset + 20, offset + 35, "14-2"))]),
        Walk(offset + 20, offset + 22),
        new RideLeg(At(offset + 25), At(offset + 35), [Line("b", Thirty, Planned(offset + 25, offset + 35, "30-1"), Planned(offset + 40, offset + 50, "30-2"))]),
        Walk(offset + 35, offset + 38),
    ], At(offset), At(offset + 38));

    /// <summary>Walk 5 minutes to "a", ride the only 14 (8:05 to 8:40) to "a-off", and walk 3 minutes.</summary>
    private static TripItinerary OneRideTrip(int offset = 0, string tripId = "14-1") => new(
    [
        Walk(offset, offset + 5),
        new RideLeg(At(offset + 5), At(offset + 40), [Line("a", Fourteen, Planned(offset + 5, offset + 40, tripId))]),
        Walk(offset + 40, offset + 43),
    ], At(offset), At(offset + 43));

    /// <summary>The transfer trip's departures, as planned, at each of its stops.</summary>
    private static TestDeparture[] AsPlanned() =>
    [
        new("a", "14", At(5), TripId: "14-1"), new("a", "14", At(20), TripId: "14-2"),
        new("a-off", "14", At(20), TripId: "14-1"), new("a-off", "14", At(35), TripId: "14-2"),
        new("b", "30", At(25), TripId: "30-1"), new("b", "30", At(40), TripId: "30-2"),
        new("b-off", "30", At(35), TripId: "30-1"), new("b-off", "30", At(50), TripId: "30-2"),
    ];

    private static HttpResponseMessage DeparturesFor(HttpRequestMessage request, TestDeparture[] departures)
    {
        string[] ids = RequestTestHandler.StopIds(request);
        return RequestTestHandler.Response(RequestTestHandler.Departures([.. departures.Where(d => ids.Contains(d.StopId))]));
    }

    /// <summary>A plan response with one trip: walk a minute to <paramref name="stop"/>, then ride <paramref name="route"/> to its "-off" stop and walk 3 minutes.</summary>
    private static string OneRidePlan(string stop, string route, DateTimeOffset departs, DateTimeOffset alights, string tripId)
    {
        long t1 = departs.ToUnixTimeSeconds();
        long t2 = alights.ToUnixTimeSeconds();
        return $$"""
            {"results":[{"start_time":{{t1 - 60}},"end_time":{{t2 + 180}},"duration":{{t2 + 240 - t1}},"legs":[
              {"leg_mode":"walk","start_time":{{t1 - 60}},"end_time":{{t1}},"duration":60,"distance":80},
              {"leg_mode":"transit","start_time":{{t1}},"end_time":{{t2}},"duration":{{t2 - t1}},
               "departures":[{"departure_time":{{t1}},"is_real_time":true,"rt_trip_id":"{{tripId}}","plan_details":{"global_route_id":"{{route}}","internal_itinerary_id":"i",
                 "arrival_schedule_item":{"arrival_time":{{t2}} },
                 "stop_schedule_items":[{"global_stop_id":"{{stop}}"},{"global_stop_id":"{{stop}}-off"}]} }],
               "routes":[{"global_route_id":"{{route}}","route_short_name":"{{route}}","route_long_name":"Route {{route}}","mode_name":"Bus",
                 "itineraries":[{"direction_id":0,"headsign":"North","plan_details":{"internal_itinerary_id":"i","start_stop_offset":0,"end_stop_offset":1},
                   "stops":[{"global_stop_id":"{{stop}}","stop_name":"Stop {{stop}}","stop_lat":43.05,"stop_lon":-88.0},
                            {"global_stop_id":"{{stop}}-off","stop_name":"Stop {{stop}}-off","stop_lat":43.15,"stop_lon":-88.0}]}]}]},
              {"leg_mode":"walk","start_time":{{t2}},"end_time":{{t2 + 180}},"duration":180,"distance":200}]}]}
            """;
    }

    private static WalkLeg Walk(int from, int to) => new(At(from), At(to), (to - from) * 80, []);

    private static PlannedDeparture Planned(int departs, int alights, string tripId) => new(At(departs), At(alights), IsRealTime: false, tripId);

    private static RideLine Line(string stopId, TransitRoute route, params PlannedDeparture[] departures) => new(
        route,
        new TransitDirection("0", "Northbound"),
        new TransitStop(stopId, $"Stop {stopId}", new GeoPoint(43, -88)),
        new TransitStop($"{stopId}-off", $"Stop {stopId}-off", new GeoPoint(43.1, -88)),
        4,
        [],
        departures);

    /// <summary>The service over the fake HTTP handler and clock, with its saved trip in a folder of its own.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly bool _ownsFolder;
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _connected;

        public Fixture(TransitUsage? usage = null)
            : this(new RequestTestClock(), new RequestTestHandler(), Directory.CreateTempSubdirectory("TrayTimesTests").FullName, usage, ownsFolder: true)
        {
        }

        private Fixture(RequestTestClock clock, RequestTestHandler handler, string folder, TransitUsage? usage, bool ownsFolder)
        {
            Clock = clock;
            Handler = handler;
            Folder = folder;
            _ownsFolder = ownsFolder;
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Service.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
            TransitBudget? budget = usage is { } fixedUsage ? new TransitBudget(() => fixedUsage, timeProvider: Clock) : null;
            MetadataTransitService metadata = new(Service, Clock);
            Predictions = new PredictionCoordinator(metadata, Clock);
            Tracker = new BusTrackingService(Service, Dispatcher, Clock, Predictions, budget);
            Trips = new ActiveTripService(metadata, Tracker, new ActiveTripStore(Folder), Dispatcher, Clock, Predictions, budget);
            Timer = Dispatcher.Timers[^1];
            ScheduledAt = Clock.GetUtcNow();
        }

        public RequestTestClock Clock { get; }
        public RequestTestHandler Handler { get; }
        public string Folder { get; }
        public string TripFile => Path.Combine(Folder, ActiveTripStore.FileName);
        public DispatcherQueue Dispatcher { get; } = new();
        public TransitAppService Service { get; }
        public PredictionCoordinator Predictions { get; }
        public BusTrackingService Tracker { get; }
        public ActiveTripService Trips { get; }
        public DispatcherQueueTimer Timer { get; }

        /// <summary>When the service last set its timer; it fires an interval later.</summary>
        public DateTimeOffset ScheduledAt { get; private set; }

        /// <summary>Answers departures from <paramref name="departures"/> at the stops asked for, and plans with <paramref name="plan"/>.</summary>
        public void Respond(TestDeparture[] departures, Func<string>? plan = null) =>
            Handler.RespondAsync = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
                ? RequestTestHandler.Response(plan?.Invoke() ?? """{"results":[]}""")
                : DeparturesFor(request, departures));

        /// <summary>The connection drops: requests fail as they do without one, and Windows (only a hint) says so.</summary>
        public void GoOffline()
        {
            _connected = Handler.RespondAsync;
            Handler.RespondAsync = (_, _) => throw new HttpRequestException("No connection.");
            ConnectivityService.IsInternetAvailable = false;
        }

        /// <summary>The connection is back, and Windows says the network changed.</summary>
        public void GoOnline()
        {
            Handler.RespondAsync = _connected!;
            ConnectivityService.SetOnline(true);
        }

        /// <summary>Go, 10 minutes before leaving.</summary>
        public void Start(TripItinerary? itinerary = null)
        {
            Trips.Start(LiveTrip.Compute(itinerary ?? TransferTrip(), [], Clock.GetLocalNow())!, Leg(), new TripDestination(Work.Location), default, null);
            Started();
        }

        public void Started() => ScheduledAt = Clock.GetUtcNow();

        /// <summary>A restart: a new service over the same clock, HTTP, and saved trip.</summary>
        public async Task<Fixture> RestartAsync()
        {
            await Trips.FlushAsync();
            return new Fixture(Clock, Handler, Folder, usage: null, ownsFolder: false);
        }

        /// <summary>Moves the clock to when the timer is due and fires it.</summary>
        public async Task TickAsync()
        {
            Clock.Advance(ScheduledAt + Timer.Interval - Clock.GetUtcNow());
            await ActAsync(Timer.Fire);
        }

        public async Task RunUntilAsync(DateTimeOffset at)
        {
            while (Trips.Current is not null && ScheduledAt + Timer.Interval <= at)
            {
                await TickAsync();
            }
        }

        public async Task RunToEndAsync()
        {
            for (int ticks = 0; Trips.Current is not null; ticks++)
            {
                Assert.IsLessThan(200, ticks);
                await TickAsync();
            }
        }

        /// <summary>Runs <paramref name="act"/> and waits for the service to report the change.</summary>
        public async Task ActAsync(Action act)
        {
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs args) => changed.TrySetResult();
            Trips.Changed += OnChanged;
            try
            {
                act();
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Trips.Changed -= OnChanged;
                Started();
            }
        }

        public async Task RunAsync(Func<Task> act)
        {
            await act().WaitAsync(TimeSpan.FromSeconds(5));
            Started();
        }

        public void Dispose()
        {
            Trips.FlushAsync().Wait(TimeSpan.FromSeconds(5));
            if (_ownsFolder)
            {
                try
                {
                    Directory.Delete(Folder, recursive: true);
                }
                catch (IOException)
                {
                    // A temp folder left behind is harmless.
                }
            }
        }
    }
}
