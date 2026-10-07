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
        await monitor.RefreshAsync();
        monitor.IsForeground = true;
        VehiclePositionsMonitor vehicles = new(service, _ => { }, clock) { IsForeground = true };
        await vehicles.RefreshAsync(["5501"]);
        clock.Advance(VehiclePositionsMonitor.RefreshInterval);
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
    public async Task GetArrivalsAsync_PlaceBoundaryReplayWithForegroundFavorites_CountsFourPredictionsAndFourVehicles(TransitConsumer consumer)
    {
        // Arrange: real Favorites monitor; the place result/completion and timers are a replay of
        // audited UI call sites, not a measurement of location/discovery or rendered WinUI.
        RequestTestClock clock = new();
        RequestTestHandler handler = new() { RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)) };
        BusTimeTransitService service = CreateCountedService(handler, clock);
        DispatcherQueue dispatcher = new();
        ArrivalsMonitor monitor = new(service, new ScheduleService(), dispatcher, clock);
        await monitor.RefreshAsync();
        monitor.IsForeground = true;
        VehiclePositionsMonitor vehicles = new(service, _ => { }, clock) { IsForeground = true };
        await vehicles.RefreshAsync(["5501"]);
        clock.Advance(VehiclePositionsMonitor.RefreshInterval);
        int before = handler.Attempts;

        // Act
        for (int second = 0; second < 60; second += 15)
        {
            if (second % 30 == 0)
            {
                await WaitForMonitorAsync(monitor, dispatcher.Timers[0].Fire);
                using (TransitRequestDiagnostics.BeginScope(consumer, TransitTrigger.Timer))
                {
                    await service.GetArrivalsAsync(["1", "2"]);
                }

                await VehiclesAsync(vehicles, consumer, TransitTrigger.PlaceResult);
                await VehiclesAsync(vehicles, consumer, TransitTrigger.PlaceCompleted);
            }

            await VehiclesAsync(vehicles, consumer, TransitTrigger.Timer);
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        // Assert
        Assert.AreEqual(8, handler.Attempts - before);
        Assert.AreEqual(2, Completed(service, TransitEndpoint.Predictions).Count(o => o.Consumer == consumer));
        Assert.AreEqual(2, Completed(service, TransitEndpoint.Predictions).Count(o => o.Consumer == TransitConsumer.Favorites && o.Trigger == TransitTrigger.Timer));
        Assert.AreEqual(5, Completed(service, TransitEndpoint.Vehicles).Count); // Includes the initial position.
        Assert.AreEqual(0, Completed(service, TransitEndpoint.Vehicles).Count(o => o.Trigger == TransitTrigger.PlaceCompleted));
        Assert.AreEqual(10, ApiUsageService.GetSummary().Total.Today);
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
        Assert.AreEqual(2, handler.Attempts);
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
            TransitTrigger.FlyoutOpened, TransitTrigger.TimetableLoaded, TransitTrigger.FlyoutOpened,
            TransitTrigger.StopsChanged, TransitTrigger.FlyoutOpened,
        }, Completed(service, TransitEndpoint.Predictions).Select(o => o.Trigger).ToArray());
        Assert.AreEqual(5, handler.Attempts);
        Assert.AreEqual(5, ApiUsageService.GetSummary().Total.Today);
        Assert.AreEqual(TimeSpan.FromSeconds(30), dispatcher.Timers[0].Interval);
    }

    [TestMethod]
    public async Task RefreshAsync_SlowOverlappingTimetableAndReopen_ObservesBothAttemptsWithoutChangingScheduling()
    {
        // Arrange
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => handler.Attempts == 1 ? first.Task : second.Task;
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ScheduleService schedule = new();
        ArrivalsMonitor monitor = new(service, schedule, new DispatcherQueue(), clock);

        // Act
        monitor.IsForeground = true;
        Task completed = WaitForMonitorAsync(monitor, schedule.RaiseLoaded);
        clock.Advance(TimeSpan.FromSeconds(2));
        first.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        second.SetResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await completed;

        // Assert
        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(2, ApiUsageService.GetSummary().Total.Today);
        TransitRequestObservation[] started = [.. service.Diagnostics.Snapshot.Where(o => o.Phase == TransitRequestPhase.Started)];
        CollectionAssert.AreEqual(new[] { TransitTrigger.FlyoutOpened, TransitTrigger.TimetableLoaded }, started.Select(o => o.Trigger).ToArray());
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
        Assert.AreSame(await favorites, await settings);
        Assert.AreEqual(TransitConsumer.Settings, service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.InFlightReuse).Consumer);
        Assert.AreEqual(TransitConsumer.Nearby, service.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.CacheReuse).Consumer);
    }

    [TestMethod]
    public async Task RefreshAsync_EmptySuccessThenFailure_PreservesTimestampAndSurfacesFailure()
    {
        // Arrange
        RequestTestClock clock = new();
        RequestTestHandler handler = new();
        BusTimeTransitService service = CreateCountedService(handler, clock);
        ArrivalsMonitor monitor = new(service, new ScheduleService(), new DispatcherQueue(), clock);
        await monitor.RefreshAsync();
        DateTimeOffset? fetched = monitor.Latest.FetchedAt;
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable));
        clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        await monitor.RefreshAsync();

        // Assert
        Assert.IsNotNull(fetched);
        Assert.AreEqual(fetched, monitor.Latest.FetchedAt);
        Assert.IsNotNull(monitor.Latest.Error);
        Assert.IsEmpty(monitor.Latest.Arrivals);
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
