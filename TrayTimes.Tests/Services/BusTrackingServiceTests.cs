using System.Net;
using Microsoft.UI.Dispatching;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class BusTrackingServiceTests
{
    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        ApiUsageService.StartOver();
        NotificationService.Titles.Clear();
    }

    [TestCleanup]
    public void Cleanup() => SettingsService.Reset();

    [TestMethod]
    public async Task Start_DueRideStaysAbsent_ChecksThreeTimesAndEndsAtGraceWithoutAFinalCall()
    {
        // Arrange
        Fixture f = new();
        DateTimeOffset due = f.Clock.GetUtcNow();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Tracker.Start(Ride(due), "Fixture stop");
        List<int> attemptSeconds = [];
        f.Service.EndpointCalled += (_, _) => attemptSeconds.Add((int)(f.Clock.GetUtcNow() - due).TotalSeconds);

        // Act
        int ticks = 0;
        while (f.Tracker.IsTracking)
        {
            await f.TickAsync();
            Assert.IsLessThanOrEqualTo(6, ++ticks);
        }

        // Assert: 60 s checks until passage is confirmed, then 90 s; the check at the end of grace is free.
        CollectionAssert.AreEqual(new[] { 60, 120, 210 }, attemptSeconds);
        Assert.AreEqual(3, ApiUsageService.GetSummary().Total.Today);
        Assert.AreEqual(4, ticks);
        Assert.AreEqual(due + TrackedBus.PassedGrace, f.Clock.GetUtcNow());
        Assert.IsEmpty(NotificationService.Titles);
        CollectionAssert.AreEqual(new[] { EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, EXECUTION_STATE.ES_CONTINUOUS },
            PInvoke.ExecutionStates);
        Assert.IsTrue(f.Service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)
            .All(o => o.Consumer == TransitConsumer.Tracking && o.Outcome == TransitRequestOutcome.Success));
    }

    [TestMethod]
    public async Task Start_RideTwelveMinutesOut_PollsEveryThreeMinutesThenEveryNinetySeconds()
    {
        // Arrange
        Fixture f = new();
        DateTimeOffset leaves = f.Clock.GetUtcNow().AddMinutes(12);
        f.Respond(leaves);

        // Act
        f.Tracker.Start(Ride(leaves), "Fixture stop");
        TimeSpan first = f.Timer.Interval;
        await f.TickAsync();

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(3), first);
        Assert.AreEqual(TimeSpan.FromSeconds(90), f.Timer.Interval);
        Assert.AreEqual(1, f.Handler.Attempts);
        f.Tracker.Stop();
    }

    [TestMethod]
    public void Start_TightCallBudget_PollsHalfAsOften()
    {
        // Arrange: 40 of today's 57-call share used.
        Fixture f = new(new TransitUsage(40, 40));

        // Act
        f.Tracker.Start(Ride(f.Clock.GetUtcNow().AddMinutes(12)), "Fixture stop");

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(6), f.Timer.Interval);
        f.Tracker.Stop();
    }

    [TestMethod]
    public async Task Start_RideApproachesLeadTime_SendsOneHeadOutAlert()
    {
        // Arrange
        Fixture f = new();
        DateTimeOffset leaves = f.Clock.GetUtcNow().AddMinutes(12);
        f.Respond(leaves);
        f.Tracker.Start(Ride(leaves), "Fixture stop");

        // Act: checks at 9, 7.5, 6 and 4.5 minutes out.
        for (int i = 0; i < 4; i++)
        {
            await f.TickAsync();
        }

        // Assert
        CollectionAssert.AreEqual(new[] { "Time to head out" }, NotificationService.Titles);
        Assert.IsTrue(f.Tracker.IsTimeToGo);
        f.Tracker.Stop();
    }

    [TestMethod]
    public async Task Start_TripCanceled_AlertsOnceAndStops()
    {
        // Arrange
        Fixture f = new();
        DateTimeOffset leaves = f.Clock.GetUtcNow().AddMinutes(12);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
            new TestDeparture("1", "14", leaves, TripId: "5501", Canceled: true))));
        f.Tracker.Start(Ride(leaves), "Fixture stop");

        // Act
        await f.TickAsync();

        // Assert
        CollectionAssert.AreEqual(new[] { "Your ride was canceled" }, NotificationService.Titles);
        Assert.IsFalse(f.Tracker.IsTracking);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.BadRequest)]
    public async Task Start_DueRideAndFailedPolls_NeverClaimsPassage(HttpStatusCode status)
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", status));
        f.Tracker.Start(Ride(f.Clock.GetUtcNow()), "Fixture stop");

        // Act
        await f.TickAsync();
        await f.TickAsync();

        // Assert
        Assert.IsNotNull(f.Tracker.Current);
        Assert.IsFalse(f.Tracker.Current.HasPassed);
        f.Tracker.Stop();
    }

    [TestMethod]
    public async Task Start_ScheduledRideOutsideLookahead_MakesNoRequestUntilLookahead()
    {
        // Arrange: a timetable-only trip 30 minutes out is looked for from 15 minutes before.
        Fixture f = new();
        DateTimeOffset leaves = f.Clock.GetUtcNow().AddMinutes(30);
        f.Respond(leaves, realTime: false);
        f.Tracker.Start(Ride(leaves) with { IsRealTime = false }, "Fixture stop");

        // Act: checks every 3 minutes, at 27, 24, 21, 18 and 15 minutes out.
        for (int i = 0; i < 5; i++)
        {
            await f.TickAsync();
        }

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.IsTrue(f.Tracker.Current!.IsScheduled);
        f.Tracker.Stop();
    }

    [TestMethod]
    public async Task Start_FlyoutOpenWithDueBrowsingStops_TrackingCallCarriesThemAlong()
    {
        // Arrange
        Fixture f = new();
        f.Predictions.SetBrowsingStops(TransitConsumer.Favorites, ["fav"]);
        f.Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(RequestTestHandler.StopIds(request))));
        f.Tracker.Start(Ride(f.Clock.GetUtcNow().AddMinutes(12)), "Fixture stop");

        // Act
        await f.TickAsync();
        PredictionSnapshot favorites = await f.Predictions.GetAsync(["fav"], PredictionCoordinator.DefaultBrowsingInterval);

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
        CollectionAssert.AreEquivalent(new[] { "1", "fav" }, RequestTestHandler.StopIds(f.Handler.Requests.Single()));
        Assert.HasCount(1, favorites.Arrivals);
        f.Tracker.Stop();
    }

    [TestMethod]
    public void Stop_WhileTracking_EndsAndReleasesKeepAwake()
    {
        // Arrange
        Fixture f = new();
        SettingsService.KeepAwakeWhileTracking = true;
        f.Tracker.Start(Ride(f.Clock.GetUtcNow().AddMinutes(12)), "Fixture stop");

        // Act
        f.Tracker.Stop();

        // Assert
        Assert.IsFalse(f.Tracker.IsTracking);
        Assert.IsFalse(f.Timer.IsRunning);
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS, PInvoke.ExecutionStates[^1]);
    }

    private static Arrival Ride(DateTimeOffset leaves) => new("1", "14", "14", "0", "Downtown", leaves, TripId: "5501");

    private sealed class Fixture
    {
        public Fixture(TransitUsage? usage = null)
        {
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Service.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
            TransitBudget? budget = usage is { } fixedUsage ? new TransitBudget(() => fixedUsage, timeProvider: Clock) : null;
            Predictions = new PredictionCoordinator(Service, Clock);
            Tracker = new BusTrackingService(Service, Dispatcher, Clock, Predictions, budget);
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public DispatcherQueue Dispatcher { get; } = new();
        public TransitAppService Service { get; }
        public PredictionCoordinator Predictions { get; }
        public BusTrackingService Tracker { get; }
        public DispatcherQueueTimer Timer => Dispatcher.Timers[0];

        public void Respond(DateTimeOffset leaves, bool realTime = true) =>
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
                new TestDeparture("1", "14", leaves, TripId: "5501", RealTime: realTime))));

        public async Task TickAsync()
        {
            Clock.Advance(Timer.Interval);
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, EventArgs args) => completed.TrySetResult();
            Tracker.Changed += Changed;
            try
            {
                Timer.Fire();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Tracker.Changed -= Changed;
            }
        }
    }
}
