using System.Net;
using Microsoft.UI.Dispatching;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class RequestBaselineTests
{
    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        SettingsService.Stops = [new SavedStop("1", "Fixture stop", 0, 0, "14", "14", null, "N", "North", false)];
        ApiUsageService.Reset();
        NotificationService.Titles.Clear();
    }

    [TestCleanup]
    public void Cleanup() => SettingsService.Reset();

    [TestMethod]
    public async Task RefreshAsync_WarmFavoritesTimerAndDisplayEventBursts_CountsTwoPredictionsAndFourVehicles()
    {
        // Arrange: real monitor events, real BusTime HTTP path; display and vehicle-timer callbacks
        // replay the audited flyout boundary without WinUI, map rendering, or production traffic.
        RequestTestClock clock = new();
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)) };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        ArrivalsMonitor monitor = new(service, new ScheduleService(), dispatcher, clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        VehiclePositionsMonitor vehicles = new(service, _ => { }, clock) { IsForeground = true };
        await vehicles.RefreshAsync(["5501"]);
        clock.Advance(PredictionCoordinator.BrowsingInterval);
        List<Task> vehicleRequests = [];
        monitor.Updated += (_, _) =>
        {
            if (!monitor.IsLoading)
            {
                vehicleRequests.Add(VehiclesAsync(vehicles, TransitConsumer.Favorites, TransitTrigger.FavoritesResult,
                    [.. monitor.Latest.Arrivals.Where(a => a.VehicleId is not null).Select(a => a.VehicleId!).Distinct()]));
            }
        };
        int before = handler.Attempts;

        // Act: stable one-stop/one-vehicle, warm caches, [0,60) starting at a prediction-timer tick.
        for (int second = 0; second < 60; second += 15)
        {
            if (second % 30 == 0)
            {
                await WaitForMonitorAsync(monitor, dispatcher.Timers[0].Fire);
            }

            await VehiclesAsync(vehicles, TransitConsumer.Favorites, TransitTrigger.Timer);
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        await Task.WhenAll(vehicleRequests);

        // Assert
        Assert.AreEqual(6, handler.Attempts - before);
        Assert.AreEqual(2, Completed(service, TransitEndpoint.Predictions).Count(o => o.Trigger == TransitTrigger.Timer));
        Assert.AreEqual(5, Completed(service, TransitEndpoint.Vehicles).Count); // Includes the initial position.
        Assert.AreEqual(0, Completed(service, TransitEndpoint.Vehicles).Count(o => o.Trigger == TransitTrigger.FavoritesLoading));
        Assert.AreEqual(2, Completed(service, TransitEndpoint.Vehicles).Count(o => o.Trigger == TransitTrigger.FavoritesResult));
        Assert.AreEqual(8, ApiUsageService.GetSummary().Total.Today); // Includes setup predictions and position.
    }

    [TestMethod]
    [DataRow(TransitConsumer.Commute)]
    [DataRow(TransitConsumer.Nearby)]
    public async Task GetArrivalsAsync_PlaceBoundaryReplayWithForegroundFavorites_CountsTwoPredictionsAndFourVehicles(TransitConsumer consumer)
    {
        // Arrange: real Favorites monitor; the place result/completion and timers are a replay of
        // audited UI call sites, not a measurement of location/discovery or rendered WinUI.
        RequestTestClock clock = new();
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)) };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        ArrivalsMonitor monitor = new(service, new ScheduleService(), dispatcher, clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        monitor.Predictions.SetBrowsingStops(consumer, ["1", "2"]);
        VehiclePositionsMonitor vehicles = new(service, _ => { }, clock) { IsForeground = true };
        await vehicles.RefreshAsync(["5501"]);
        clock.Advance(PredictionCoordinator.BrowsingInterval);
        int before = handler.Attempts;

        // Act
        for (int second = 0; second < 60; second += 15)
        {
            if (second % 30 == 0)
            {
                await WaitForMonitorAsync(monitor, dispatcher.Timers[0].Fire);
                using (TransitRequestDiagnostics.BeginScope(consumer, TransitTrigger.Timer))
                {
                    await monitor.Predictions.GetAsync(["1", "2"], PredictionCoordinator.BrowsingInterval, combineBrowsing: true);
                }

                await VehiclesAsync(vehicles, consumer, TransitTrigger.PlaceResult);
                await VehiclesAsync(vehicles, consumer, TransitTrigger.PlaceCompleted);
            }

            await VehiclesAsync(vehicles, consumer, TransitTrigger.Timer);
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        // Assert
        Assert.AreEqual(6, handler.Attempts - before);
        Assert.AreEqual(0, Completed(service, TransitEndpoint.Predictions).Count(o => o.Consumer == consumer));
        Assert.AreEqual(2, Completed(service, TransitEndpoint.Predictions).Count(o => o.Consumer == TransitConsumer.Favorites && o.Trigger == TransitTrigger.Timer));
        Assert.AreEqual(5, Completed(service, TransitEndpoint.Vehicles).Count); // Includes the initial position.
        Assert.AreEqual(0, Completed(service, TransitEndpoint.Vehicles).Count(o => o.Trigger == TransitTrigger.PlaceCompleted));
        Assert.AreEqual(8, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task IsForeground_ReopenTimetableAndHiddenEvents_RecordsActualRefreshWiring()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        ScheduleService schedule = new();
        ArrivalsMonitor monitor = new(service, schedule, dispatcher, clock);

        // Act
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        await WaitForMonitorAsync(monitor, schedule.RaiseLoaded);
        monitor.IsForeground = false;
        schedule.RaiseLoaded();
        dispatcher.Timers[0].Fire();
        monitor.IsForeground = true; // Fresh reopen reuses display data: no HTTP.
        Assert.AreEqual(1, handler.Attempts);
        monitor.IsForeground = false;
        clock.Advance(TimeSpan.FromSeconds(16));
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        await WaitForMonitorAsync(monitor, SettingsService.RaiseStopsChanged);
        monitor.IsForeground = false;
        SettingsService.RaiseStopsChanged();
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);

        // Assert
        CollectionAssert.AreEqual(new[]
        {
            TransitTrigger.FlyoutOpened, TransitTrigger.FlyoutOpened,
        }, Completed(service, TransitEndpoint.Predictions).Select(o => o.Trigger).ToArray());
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(2, ApiUsageService.GetSummary().Total.Today);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
    }

    [TestMethod]
    public async Task RefreshAsync_SlowOverlappingTimetableAndReopen_SharesOneAttempt()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, token) => first.Task.WaitAsync(token);
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ScheduleService schedule = new();
        ArrivalsMonitor monitor = new(service, schedule, new DispatcherQueue(), clock);

        // Act
        monitor.IsForeground = true;
        Task completed = WaitForMonitorAsync(monitor, schedule.RaiseLoaded);
        clock.Advance(TimeSpan.FromSeconds(2));
        first.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await completed;

        // Assert
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);
        TransitRequestObservation[] started = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Started)];
        CollectionAssert.AreEqual(new[] { TransitTrigger.FlyoutOpened }, started.Select(o => o.Trigger).ToArray());
        Assert.AreEqual(0, service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.InFlightReuse));
        Assert.IsNotNull(monitor.Latest.FetchedAt);
    }

    [TestMethod]
    public async Task GetRouteCatalogAsync_ConcurrentAndCachedConsumers_CountsOneHttpAttemptAndLabelsReuse()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new() { RespondAsync = (_, _) => pending.Task };
        BusTimeTransitService service = CreateCountedService(handler, new RequestTestClock());
        ArrivalsMonitor monitor = new(service, new ScheduleService(), new DispatcherQueue());

        // Act
        Task<IReadOnlyDictionary<string, TransitRoute>> favorites = monitor.GetRouteCatalogAsync();
        Task<IReadOnlyDictionary<string, TransitRoute>> settings = monitor.GetRouteCatalogAsync(TransitConsumer.Settings);
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await Task.WhenAll(favorites, settings);
        await monitor.GetRouteCatalogAsync(TransitConsumer.Nearby);

        // Assert
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);
        CollectionAssert.AreEqual((await favorites).Values.ToArray(), (await settings).Values.ToArray());
        Assert.AreEqual(TransitConsumer.Settings, service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.InFlightReuse).Consumer);
        Assert.AreEqual(TransitConsumer.Nearby, service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.CacheReuse).Consumer);
    }

    [TestMethod]
    public async Task GetRouteCatalogAsync_FlyoutClearAndRecreatedConsumer_DoesNotDiscardAppMetadata()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        ArrivalsMonitor monitor = new(CreateCountedService(handler, clock), new ScheduleService(), new DispatcherQueue(), clock);
        await monitor.GetRouteCatalogAsync();

        // Act
        monitor.Clear();
        await monitor.GetRouteCatalogAsync(TransitConsumer.Settings);
        ArrivalsMonitor recreated = new(monitor.Metadata, new ScheduleService(), new DispatcherQueue(), clock);
        await recreated.GetRouteCatalogAsync(TransitConsumer.Favorites);
        monitor.Metadata.Invalidate();
        await monitor.GetRouteCatalogAsync(TransitConsumer.Settings);

        // Assert
        Assert.AreEqual(2, handler.Attempts); // Initial and intentional invalidation only.
        Assert.AreEqual(2, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RefreshAsync_SuccessThenFailure_PreservesLastGoodTimestampAndArrivals(bool empty)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
                empty ? RequestTestHandler.EmptyBody : RequestTestHandler.LiveBody)),
        };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ArrivalsMonitor monitor = new(service, new ScheduleService(), new DispatcherQueue(), clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        DateTimeOffset? fetched = monitor.Latest.FetchedAt;
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable));
        clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        await monitor.RefreshAsync();

        // Assert
        Assert.IsNotNull(fetched);
        Assert.AreEqual(fetched, monitor.Latest.FetchedAt);
        Assert.IsNotNull(monitor.Latest.Error);
        Assert.AreEqual(empty ? 0 : 1, monitor.Latest.Arrivals.Count);
        CollectionAssert.AreEqual(new[] { TransitRequestOutcome.Success, TransitRequestOutcome.HttpFailure },
            Completed(service, TransitEndpoint.Predictions).Select(o => o.Outcome).ToArray());
        Assert.AreEqual(2, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    [DataRow(20, 1, 60)]
    [DataRow(8, 2, 30)]
    [DataRow(4, 6, 10)]
    public async Task Start_TrackingTimerWithoutFlyout_ObservesExistingCadence(int minutesAway, int expected, int intervalSeconds)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock);
        tracker.Start(ArrivalAt(clock.GetUtcNow().AddMinutes(minutesAway)), "Fixture stop");

        // Act: first minute after Start, (0,60], no browsing consumer.
        for (int i = 0; i < expected; ++i)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(intervalSeconds), dispatcher.Timers[0].Interval);
            clock.Advance(dispatcher.Timers[0].Interval);
            await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);
        }

        // Assert
        Assert.AreEqual(expected, handler.Attempts);
        Assert.AreEqual(expected, ApiUsageService.GetSummary().Total.Today);
        Assert.IsTrue(Completed(service, TransitEndpoint.Predictions).All(o => o.Consumer == TransitConsumer.Tracking && o.Trigger == TransitTrigger.Timer && o.BatchSize == 1));
        Assert.IsTrue(tracker.IsTracking);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_ScheduledOnlyOutsideLookahead_MakesNoHttpAttemptUntilLookahead()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock);
        tracker.Start(ArrivalAt(clock.GetUtcNow().AddMinutes(20)) with { IsRealTime = false }, "Fixture stop");

        // Act
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);
        Assert.AreEqual(0, handler.Attempts);
        clock.Advance(TimeSpan.FromMinutes(4));
        await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);

        // Assert
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(TransitConsumer.Tracking, Completed(service, TransitEndpoint.Predictions).Single().Consumer);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_DueBusAndFailedTrackingFetch_DoesNotTreatFailureAsPassage()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable)),
        };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock);
        tracker.Start(ArrivalAt(clock.GetUtcNow()) with { IsDue = true }, "Fixture stop");

        // Act
        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.AreEqual(TransitRequestOutcome.HttpFailure, Completed(service, TransitEndpoint.Predictions).Single().Outcome);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);
        tracker.Stop();
    }

    [TestMethod]
    public async Task RefreshAsync_ManualForceAndHiddenCalls_PreservesIntentionalRefreshAndHiddenInactivity()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ArrivalsMonitor monitor = new(service, new ScheduleService(), new DispatcherQueue(), clock);
        await monitor.RefreshAsync();
        Assert.AreEqual(0, handler.Attempts);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        DateTimeOffset? original = monitor.Latest.FetchedAt;
        clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        await monitor.RefreshAsync(TransitTrigger.Timer);
        Assert.AreEqual(original, monitor.Latest.FetchedAt);
        await monitor.RefreshAsync();
        monitor.IsForeground = false;
        await monitor.RefreshAsync();

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(original!.Value.AddSeconds(1), monitor.Latest.FetchedAt);
        Assert.AreEqual(TransitTrigger.ManualRefresh, Completed(service, TransitEndpoint.Predictions).Last().Trigger);
    }

    [TestMethod]
    public async Task RefreshAsync_HideDuringSharedTrackingFetch_PreservesTooltipSnapshotAndHiddenTracker()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        ArrivalsMonitor monitor = new(service, new ScheduleService(), dispatcher, clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        DateTimeOffset? lastGood = monitor.Latest.FetchedAt;
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        BusTrackingService tracker = new(service, dispatcher, clock, monitor.Predictions);
        tracker.Start(ArrivalAt(clock.GetUtcNow().AddMinutes(4)), "Fixture stop");
        clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        Task browsing = monitor.RefreshAsync(TransitTrigger.Timer);
        Task tracking = WaitForTrackerAsync(tracker, dispatcher.Timers[1].Fire);
        monitor.IsForeground = false;
        await browsing;
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await tracking;

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(lastGood, monitor.Latest.FetchedAt);
        Assert.IsFalse(monitor.IsLoading);
        Assert.IsTrue(tracker.IsTracking);
        Assert.AreEqual(1, Completed(service, TransitEndpoint.Predictions).Count(o => o.Trigger == TransitTrigger.Timer));
        tracker.Stop();
    }

    [TestMethod]
    [DataRow(TransitConsumer.Commute)]
    [DataRow(TransitConsumer.Nearby)]
    public async Task Start_SharedBrowsingPredictionAndRepeatedTicks_EmitsOneAlertWithoutAnotherHttp(TransitConsumer consumer)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
                """{"bustime-response":{"prd":[{"stpid":"1","rt":"14","rtdir":"N","vid":"5501","prdtm":"20261006 20:04","dyn":0}]}}""")),
        };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        PredictionCoordinator predictions = new(service, clock);
        BusTrackingService tracker = new(service, dispatcher, clock, predictions);
        tracker.Start(ArrivalAt(clock.GetUtcNow().AddMinutes(8)), "Fixture stop");
        clock.Advance(TimeSpan.FromSeconds(30));
        using (TransitRequestDiagnostics.BeginScope(consumer, TransitTrigger.Timer))
        {
            await predictions.GetAsync(["1", "2"], TimeSpan.FromSeconds(30), combineBrowsing: true);
        }

        // Act
        await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);
        await WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire); // Same fresh shared result.

        // Assert
        Assert.AreEqual(1, handler.Attempts);
        CollectionAssert.AreEqual(new[] { "Time to head out" }, NotificationService.Titles);
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        Assert.AreEqual(2, service.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.CacheReuse && o.Consumer == TransitConsumer.Tracking));
        tracker.Stop();
    }

    [TestMethod]
    [DataRow(TransitConsumer.Commute)]
    [DataRow(TransitConsumer.Nearby)]
    public async Task GetAsync_MultiBatchPlaceReplayWithFavorites_CountsFourPredictionsAndFourVehicles(TransitConsumer consumer)
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        SettingsService.Stops = [.. Enumerable.Range(1, 10).Select(i =>
            new SavedStop(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Fixture stop", 0, 0, "14", "14", null, "N", "North", false))];
        ArrivalsMonitor monitor = new(service, new ScheduleService(), dispatcher, clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        string[] place = [.. Enumerable.Range(11, 8).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        monitor.Predictions.SetBrowsingStops(consumer, place);
        await monitor.Predictions.GetAsync(place, TimeSpan.FromSeconds(30), combineBrowsing: true);
        VehiclePositionsMonitor vehicles = new(service, _ => { }, clock) { IsForeground = true };
        await vehicles.RefreshAsync(["5501"]);
        clock.Advance(TimeSpan.FromSeconds(30));
        int before = handler.Attempts;

        // Act
        for (int second = 0; second < 60; second += 15)
        {
            if (second % 30 == 0)
            {
                await WaitForMonitorAsync(monitor, dispatcher.Timers[0].Fire);
                using (TransitRequestDiagnostics.BeginScope(consumer, TransitTrigger.Timer))
                {
                    await monitor.Predictions.GetAsync(place, TimeSpan.FromSeconds(30), combineBrowsing: true);
                }
            }

            await VehiclesAsync(vehicles, consumer, TransitTrigger.Timer);
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        // Assert
        Assert.AreEqual(8, handler.Attempts - before);
        Assert.AreEqual(4, Completed(service, TransitEndpoint.Predictions).Count(o => o.Trigger == TransitTrigger.Timer));
        CollectionAssert.AreEqual(new[] { 10, 8, 10, 8 },
            Completed(service, TransitEndpoint.Predictions).Where(o => o.Trigger == TransitTrigger.Timer).Select(o => o.BatchSize).ToArray());
    }

    [TestMethod]
    public async Task RefreshAsync_StopChangeToEmptyDuringFetch_ClearsDemandAndCancelsOldWaiter()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ArrivalsMonitor monitor = new(service, new ScheduleService(), new DispatcherQueue(), clock);
        await WaitForMonitorAsync(monitor, () => monitor.IsForeground = true);
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task refresh = monitor.RefreshAsync();

        // Act
        SettingsService.Stops = [];
        SettingsService.RaiseStopsChanged();
        await refresh;

        // Assert
        Assert.IsEmpty(monitor.Latest.Arrivals);
        Assert.IsNull(monitor.Latest.FetchedAt);
        Assert.IsFalse(monitor.IsLoading);
    }

    [TestMethod]
    public async Task Start_SharedBatchHasErrorForDueBus_DoesNotTreatOtherStopsSuccessAsPassage()
    {
        // Arrange
        RequestTestClock clock = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new() { RespondAsync = (_, token) => pending.Task.WaitAsync(token) };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        PredictionCoordinator predictions = new(service, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock, predictions);
        tracker.Start(ArrivalAt(clock.GetUtcNow()) with { IsDue = true }, "Fixture stop");
        clock.Advance(TimeSpan.FromSeconds(10));
        Task<PredictionSnapshot> browsing = predictions.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        Task tracking = WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);

        // Act
        pending.SetResult(RequestTestHandler.Response(
            """{"bustime-response":{"prd":[{"stpid":"2","rt":"14","prdtm":"20261006 20:24"}],"error":[{"stpid":"1","msg":"Invalid parameter"}]}}"""));
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => browsing);
        await tracking;

        // Assert
        Assert.IsNotNull(tracker.Current);
        Assert.IsFalse(tracker.Current.HasPassed);
        Assert.IsEmpty(NotificationService.Titles);
        Assert.AreEqual(1, handler.Attempts);
        Assert.AreEqual(TransitRequestOutcome.ApiFailure, Completed(service, TransitEndpoint.Predictions).Single().Outcome);
        tracker.Stop();
    }

    [TestMethod]
    public async Task Start_SharedFetchCrossesFiveMinuteThreshold_RechecksCurrentFreshnessForOnlyTrackedStop()
    {
        // Arrange
        RequestTestClock clock = new();
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, token) => handler.Attempts == 1
            ? pending.Task.WaitAsync(token)
            : Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        BusTimeTransitService service = CreateCountedService(handler, clock);
        PredictionCoordinator predictions = new(service, clock);
        DispatcherQueue dispatcher = new();
        BusTrackingService tracker = new(service, dispatcher, clock, predictions);
        tracker.Start(ArrivalAt(clock.GetUtcNow().AddSeconds(301)), "Fixture stop");
        Task<PredictionSnapshot> browsing = predictions.GetAsync(["1", "2"], TimeSpan.FromSeconds(30));
        Task tracking = WaitForTrackerAsync(tracker, dispatcher.Timers[0].Fire);

        // Act
        clock.Advance(TimeSpan.FromSeconds(11));
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await browsing;
        await tracking;

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        CollectionAssert.AreEqual(new[] { 2, 1 }, Completed(service, TransitEndpoint.Predictions).Select(o => o.BatchSize).ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(10), dispatcher.Timers[0].Interval);
        Assert.IsTrue(tracker.IsTracking);
        tracker.Stop();
    }

    private static BusTimeTransitService CreateCountedService(RequestTestHandler handler, RequestTestClock clock)
    {
        BusTimeTransitService service = handler.CreateService(new TransitRequestDiagnostics(clock));
        service.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
        service.Diagnostics.Observed += (_, observation) => ApiUsageService.Observe(observation);
        return service;
    }

    private static List<TransitRequestObservation> Completed(BusTimeTransitService service, TransitEndpoint endpoint) =>
        [.. service.Diagnostics.Snapshot.Where(o => o.Endpoint == endpoint && o.Phase == TransitRequestPhase.Completed)];

    private static async Task VehiclesAsync(VehiclePositionsMonitor vehicles, TransitConsumer consumer, TransitTrigger trigger, IReadOnlyCollection<string>? vehicleIds = null)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(consumer, trigger);
        await vehicles.RefreshAsync(vehicleIds ?? ["5501"]);
    }

    private static async Task WaitForMonitorAsync(ArrivalsMonitor monitor, Action trigger)
    {
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated(object? sender, EventArgs args)
        {
            if (!monitor.IsLoading)
            {
                completed.TrySetResult();
            }
        }

        monitor.Updated += Updated;
        try
        {
            trigger();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            monitor.Updated -= Updated;
        }
    }

    private static async Task WaitForTrackerAsync(BusTrackingService tracker, Action trigger)
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

    private static Arrival ArrivalAt(DateTimeOffset time) =>
        new("1", "14", "14", "N", string.Empty, "5501", time, false, false, false, ArrivalStatus.Scheduled);
}
