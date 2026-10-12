using System.Net;
using Microsoft.UI.Dispatching;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class BusTrackingServiceTests
{
    private const string MatchingBody =
        """{"bustime-response":{"prd":[{"stpid":"1","rt":"14","rtdir":"N","vid":"5501","prdtm":"20261006 20:04","dyn":0}]}}""";
    private const string CanceledBody =
        """{"bustime-response":{"prd":[{"stpid":"1","rt":"14","rtdir":"N","vid":"5501","prdtm":"20261006 20:04","dyn":1}]}}""";

    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        ApiUsageService.Reset();
        NotificationService.Titles.Clear();
    }

    [TestCleanup]
    public void Cleanup() => SettingsService.Reset();

    [TestMethod]
    public async Task Start_DueBusRemainsAbsent_CountsElevenAttemptsWithoutExtendingFiveMinuteGrace()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        SettingsService.KeepAwakeWhileTracking = true;
        tracker.Start(DueArrival(due), "Fixture stop");

        List<int> attemptSeconds = [];
        service.EndpointCalled += (_, _) => attemptSeconds.Add((int)(clock.GetUtcNow() - due).TotalSeconds);

        // Act: (due, due + 300 s]; the final local check can reuse the fresh t=290 snapshot.
        int ticks = 0;
        while (tracker.IsTracking)
        {
            await TickAsync(tracker, dispatcher, clock);
            ++ticks;
            Assert.IsLessThanOrEqualTo(12, ticks);
            if (tracker.Current is { } bus)
            {
                Assert.AreEqual(ticks >= 2, bus.HasPassed);
                Assert.AreEqual(due, bus.DueSince);
                Assert.AreEqual(0, bus.MinutesAway(clock.GetUtcNow()));
                Assert.AreEqual(1, PInvoke.ExecutionStates.Count);
                Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, PInvoke.ExecutionStates[0]);
            }
        }

        // Assert
        Assert.AreEqual(11, handler.Attempts);
        Assert.AreEqual(11, ApiUsageService.GetSummary().Total.Today);
        Assert.AreEqual(12, ticks);
        CollectionAssert.AreEqual(new[] { 10, 20, 50, 80, 110, 140, 170, 200, 230, 260, 290 }, attemptSeconds);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsFalse(dispatcher.Timers[0].IsRunning);
        Assert.IsEmpty(NotificationService.Titles);
        CollectionAssert.AreEqual(new[] { EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED, EXECUTION_STATE.ES_CONTINUOUS },
            PInvoke.ExecutionStates);
        Assert.IsTrue(service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed)
            .All(o => o.Consumer == TransitConsumer.Tracking && o.BatchSize == 1 && o.Outcome == TransitRequestOutcome.Success));
    }

    [TestMethod]
    public async Task Start_DueBusDisappearsThenReappears_PreservesOriginalGraceAndObservesUpdatedPrediction()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MatchingBody));

        // Act
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(due.AddMinutes(4), tracker.Current.PredictedTime);
        Assert.AreEqual(due, tracker.Current.DueSince);
        Assert.AreEqual(0, tracker.Current.MinutesAway(clock.GetUtcNow()));
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        Assert.IsEmpty(NotificationService.Titles);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_DueBusDisappearsThenCanceled_AlertsOnceAndStopsImmediately()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(CanceledBody));

        // Act
        await TickAsync(tracker, dispatcher, clock);
        dispatcher.Timers[0].Fire();

        // Assert
        CollectionAssert.AreEqual(new[] { "Your bus was canceled" }, NotificationService.Titles);
        Assert.AreEqual(2, handler.Attempts);
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsFalse(dispatcher.Timers[0].IsRunning);
    }

    [TestMethod]
    [DataRow("http", TransitRequestOutcome.HttpFailure)]
    [DataRow("api", TransitRequestOutcome.ApiFailure)]
    [DataRow("timeout", TransitRequestOutcome.Timeout)]
    public async Task Start_DueBusFailedOrCanceledPoll_DoesNotTreatFailureAsPassage(string failure, TransitRequestOutcome outcome)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => failure switch
            {
                "http" => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable)),
                "api" => Task.FromResult(RequestTestHandler.Response("""{"bustime-response":{"error":[{"msg":"Invalid parameter"}]}}""")),
                _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
            },
        };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(new CooldownTransitService(service, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");

        // Act
        await TickAsync(tracker, dispatcher, clock);
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(due, tracker.Current.DueSince);
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        Assert.AreEqual(2, handler.Attempts);
        Assert.IsEmpty(NotificationService.Titles);
        Assert.IsTrue(service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Completed).All(o => o.Outcome == outcome));
        tracker.Stop();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_SuccessfulEmptyOrUnrelatedTripAfterDue_MarksPassedWithoutEndingGrace(bool unrelated)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(unrelated
                ? MatchingBody.Replace("5501", "6602", StringComparison.Ordinal)
                : RequestTestHandler.EmptyBody)),
        };
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");

        // Act
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(due, tracker.Current.DueSince);
        Assert.IsTrue(dispatcher.Timers[0].IsRunning);
        Assert.AreEqual(1, handler.Attempts);
        await TickAsync(tracker, dispatcher, clock);
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
        Assert.AreEqual(2, handler.Attempts);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_DueBusGraceExpiresDuringFailedPolls_EndsWithoutClaimingPassage()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable)),
        };
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(new CooldownTransitService(CreateCountedService(handler, clock), clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");

        // Act
        for (int tick = 1; tick <= 30; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
            if (tick < 30)
            {
                Assert.IsNotNull(tracker.Current);
                Assert.IsFalse(tracker.Current.HasPassed);
            }
        }

        // Assert
        Assert.AreEqual(30, handler.Attempts);
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task Start_DueBusWithBrowsingAtSameStop_AddsNoAttemptsAndRetainsElevenGraceRequests()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        PredictionCoordinator predictions = new(service, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock, predictions);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");

        // Act: browsing keeps its original 30-second cadence, independently of tracking.
        DateTimeOffset nextTracking = clock.GetUtcNow() + dispatcher.Timers[0].Interval;
        for (int second = 10; second <= 300; second += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            if (second % 30 == 0)
            {
                using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Favorites, TransitTrigger.Timer);
                await predictions.GetAsync(["1"], PredictionCoordinator.BrowsingInterval);
            }

            if (clock.GetUtcNow() == nextTracking)
            {
                await WaitForChangeAsync(tracker, dispatcher.Timers[0].Fire);
                nextTracking = clock.GetUtcNow() + dispatcher.Timers[0].Interval;
            }
        }

        // Assert
        Assert.AreEqual(11, handler.Attempts);
        Assert.AreEqual(11, service.Diagnostics.Snapshot.Count(o =>
            o.Phase == TransitRequestPhase.Started && o.Consumer == TransitConsumer.Tracking));
        Assert.AreEqual(10, service.Diagnostics.Snapshot.Count(o =>
            o.Phase == TransitRequestPhase.CacheReuse && o.Consumer == TransitConsumer.Favorites));
        Assert.AreEqual(11, ApiUsageService.GetSummary().Total.Today);
        Assert.IsFalse(tracker.IsTracking);
    }

    [TestMethod]
    public async Task Start_ReusedMissingSnapshotAndThenNewFetch_ConfirmsOnlyDistinctResults()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);

        // Act: a premature event must not count the same cached result as another missing bus.
        await WaitForChangeAsync(tracker, dispatcher.Timers[0].Fire);
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(1, handler.Attempts);
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
        Assert.AreEqual(2, handler.Attempts);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_ConfirmedBusReturns_RestoresCadenceAndRequiresNewConfirmation()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MatchingBody));

        // Act
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.AreEqual(due.AddSeconds(50), clock.GetUtcNow());
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(due, tracker.Current.DueSince);
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        Assert.IsEmpty(NotificationService.Titles);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await TickAsync(tracker, dispatcher, clock);
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        await TickAsync(tracker, dispatcher, clock);
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
        Assert.AreEqual(5, handler.Attempts);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_ConfirmedBusCanceled_AlertsAtRecoveryCheckAndEndsOnce()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(CanceledBody));

        // Act
        await TickAsync(tracker, dispatcher, clock);
        dispatcher.Timers[0].Fire();

        // Assert
        Assert.AreEqual(due.AddSeconds(50), clock.GetUtcNow());
        Assert.AreEqual(3, handler.Attempts);
        Assert.IsFalse(tracker.IsTracking);
        CollectionAssert.AreEqual(new[] { "Your bus was canceled" }, NotificationService.Titles);
        Assert.AreEqual(EXECUTION_STATE.ES_CONTINUOUS, PInvoke.ExecutionStates[^1]);
    }

    [TestMethod]
    public async Task Start_ConfirmedBusReturnsAndStaysListed_SavesOnlyTwoRequestsAndKeepsOriginalExpiry()
    {
        // Arrange
        RequestTestClock clock = new();
        DateTimeOffset due = clock.GetUtcNow();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
                clock.GetUtcNow() >= due.AddSeconds(50) ? MatchingBody : RequestTestHandler.EmptyBody)),
        };
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(due), "Fixture stop");

        // Act
        for (int tick = 0; tick < 28; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
            if (clock.GetUtcNow() >= due.AddSeconds(50) && tracker.Current is { } bus)
            {
                Assert.IsFalse(bus.HasPassed);
                Assert.AreEqual(due, bus.DueSince);
            }
        }

        // Assert
        Assert.AreEqual(28, handler.Attempts);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_BriefReturnOrCancellationBetweenRecoveryChecks_CanBeMissedAsApproved(bool canceled)
    {
        // Arrange
        RequestTestClock clock = new();
        DateTimeOffset due = clock.GetUtcNow();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
                clock.GetUtcNow() >= due.AddSeconds(30) && clock.GetUtcNow() < due.AddSeconds(40)
                    ? canceled ? CanceledBody : MatchingBody
                    : RequestTestHandler.EmptyBody)),
        };
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(due), "Fixture stop");

        // Act
        for (int tick = 0; tick < 12; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
            if (tracker.Current is { } bus && clock.GetUtcNow() >= due.AddSeconds(20))
            {
                Assert.IsTrue(bus.HasPassed);
            }
        }

        // Assert: this explicitly bounds the tradeoff; no full recovery guarantee is claimed.
        Assert.AreEqual(11, handler.Attempts);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task Start_RateLimitedBetweenMisses_SuppressedChecksCannotConfirmPassage()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(new CooldownTransitService(service, clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) =>
        {
            HttpResponseMessage response = RequestTestHandler.Response("error", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        };
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));

        // Act: suppressed t=30..70, first new success at t=80, second at t=90.
        for (int tick = 0; tick < 6; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
            Assert.IsNotNull(tracker.Current);
            Assert.IsFalse(tracker.Current.HasPassed);
        }

        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);
        Assert.AreEqual(4, handler.Attempts);
        Assert.AreEqual(4, ApiUsageService.GetSummary().Total.Today);
        Assert.AreEqual(5, service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.Suppressed));
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_FailureBetweenMissingResults_RequiresTwoNewSuccessfulSnapshots()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(new CooldownTransitService(CreateCountedService(handler, clock), clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable));

        // Act
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await TickAsync(tracker, dispatcher, clock);
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);
        Assert.AreEqual(4, handler.Attempts);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_ConfirmedThenFailedUntilGraceEnds_PreservesPassedStateAndCompletesLifecycle()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(new CooldownTransitService(CreateCountedService(handler, clock), clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        await TickAsync(tracker, dispatcher, clock);
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable));

        // Act
        for (int tick = 0; tick < 10; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
            if (tracker.Current is { } bus)
            {
                Assert.IsTrue(bus.HasPassed);
                Assert.AreEqual(due, bus.DueSince);
            }
        }

        // Assert
        Assert.AreEqual(12, handler.Attempts);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task Start_LaterDisappearance_SavesFewerRequestsAndKeepsOriginalGrace()
    {
        // Arrange
        RequestTestClock clock = new();
        DateTimeOffset due = clock.GetUtcNow();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
                clock.GetUtcNow() < due.AddSeconds(120) ? MatchingBody : RequestTestHandler.EmptyBody)),
        };
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(due), "Fixture stop");

        // Act
        for (int tick = 0; tick < 19; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
        }

        // Assert
        Assert.AreEqual(18, handler.Attempts);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
        Assert.IsEmpty(NotificationService.Titles);
    }

    [TestMethod]
    public async Task Start_SlowConfirmationResponse_DoesNotExtendOriginalGrace()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        DateTimeOffset due = clock.GetUtcNow();
        tracker.Start(DueArrival(due), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task confirmation = TickAsync(tracker, dispatcher, clock);

        // Act
        clock.Advance(TimeSpan.FromSeconds(35));
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await confirmation;
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        for (int tick = 0; tick < 9; ++tick)
        {
            await TickAsync(tracker, dispatcher, clock);
        }

        // Assert
        Assert.AreEqual(10, handler.Attempts);
        Assert.AreEqual(due.AddMinutes(5), clock.GetUtcNow());
        Assert.IsFalse(tracker.IsTracking);
    }

    [TestMethod]
    public async Task Start_ReplacementAfterConfirmation_DoesNotCarryEvidenceToNewTrip()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(CreateCountedService(handler, clock), dispatcher, clock);
        tracker.Start(DueArrival(clock.GetUtcNow()), "Fixture stop");
        await TickAsync(tracker, dispatcher, clock);
        await TickAsync(tracker, dispatcher, clock);
        Assert.IsNotNull(tracker.Current);
        Assert.IsTrue(tracker.Current.HasPassed);

        // Act
        tracker.Start(DueArrival(clock.GetUtcNow()) with { StopId = "2", VehicleId = "6602" }, "Replacement stop");
        await TickAsync(tracker, dispatcher, clock);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.AreEqual("2", tracker.Current.StopId);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        tracker.Stop();
    }

    private static BusTimeTransitService CreateCountedService(RequestTestHandler handler, RequestTestClock clock)
    {
        BusTimeTransitService service = handler.CreateService(new TransitRequestDiagnostics(clock));
        service.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
        return service;
    }

    private static Arrival DueArrival(DateTimeOffset now) =>
        new("1", "14", "14", "N", string.Empty, "5501", now, true, false, false, ArrivalStatus.Scheduled);

    private static Task TickAsync(BusTrackingService tracker, DispatcherQueue dispatcher, RequestTestClock clock)
    {
        clock.Advance(dispatcher.Timers[0].Interval);
        return WaitForChangeAsync(tracker, dispatcher.Timers[0].Fire);
    }

    private static async Task WaitForChangeAsync(BusTrackingService tracker, Action trigger)
    {
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) => completed.TrySetResult();
        tracker.Changed += Changed;
        try
        {
            trigger();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            tracker.Changed -= Changed;
        }
    }
}
