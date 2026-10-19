using Microsoft.UI.Dispatching;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Services;

/// <summary>
/// Deterministic request counts for the scenarios in docs/transit-app-migration.md, through the real
/// monitors, coordinator, caches, and HTTP client, with fake HTTP and a controllable clock.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RequestBudgetTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);

    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        SettingsService.Stops = [new SavedStop("fav", "Favorite", 43, -88, "14", "14", null, "0", "North", false)];
        ApiUsageService.StartOver();
        NotificationService.Titles.Clear();
    }

    [TestCleanup]
    public void Cleanup() => SettingsService.Reset();

    [TestMethod]
    public async Task Favorites_OpenForFiveMinutes_CostsFiveCallsAtTheNormalLevel()
    {
        // Arrange
        App app = new();

        // Act: opening, then one timer refresh a minute until it's dismissed at 5:00.
        await app.OpenFavoritesAsync();
        for (int minute = 1; minute < 5; minute++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            await app.WaitForFavoritesAsync(app.FavoritesTimer.Fire);
        }

        // Assert
        Assert.AreEqual(5, app.Handler.Attempts);
        Assert.AreEqual(5, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task Favorites_ReopenedWithinAMinute_CostsNothing()
    {
        // Arrange
        App app = new();
        await app.OpenFavoritesAsync();
        app.Favorites.IsForeground = false;
        app.Clock.Advance(TimeSpan.FromSeconds(45));

        // Act
        app.Favorites.IsForeground = true;

        // Assert
        Assert.AreEqual(1, app.Handler.Attempts);
        Assert.HasCount(1, app.Favorites.Latest.Arrivals);
    }

    [TestMethod]
    public async Task NearbyAndFavorites_OpenedTogether_ShareOneCall()
    {
        // Arrange: Nearby's discovery is already cached, as on a desktop that hasn't moved.
        App app = new();
        PlaceArrivalsMonitor nearby = app.CreatePlaceMonitor(["near1", "near2"]);

        // Act
        app.Favorites.IsForeground = true;
        nearby.Configure(new PlaceContext(FlyoutMode.Nearby, [], null));
        nearby.IsForeground = true;
        Task place = nearby.RefreshAsync(checkLocation: false);
        Task favorites = app.WaitForFavoritesAsync(() => { });
        app.Clock.Advance(Window);
        await Task.WhenAll(place, favorites);

        // Assert
        Assert.AreEqual(1, app.Handler.Attempts);
        CollectionAssert.AreEquivalent(new[] { "fav", "near1", "near2" }, RequestTestHandler.StopIds(app.Handler.Requests.Single()));
        Assert.HasCount(2, nearby.Snapshot!.Arrivals);
        Assert.HasCount(1, app.Favorites.Latest.Arrivals);
    }

    [TestMethod]
    public async Task NearbyAndFavoritesTimers_OutOfPhase_StillShareOneCallAMinute()
    {
        // Arrange
        App app = new();
        PlaceArrivalsMonitor nearby = app.CreatePlaceMonitor(["near"]);
        await app.OpenFavoritesAsync();
        nearby.Configure(new PlaceContext(FlyoutMode.Nearby, [], null));
        nearby.IsForeground = true;
        app.Clock.Advance(TimeSpan.FromSeconds(20));
        await AdvancingAsync(app, nearby.RefreshAsync(checkLocation: false));
        int before = app.Handler.Attempts;

        // Act: over the next three minutes, Favorites ticks at :00 and Nearby at :20.
        for (int minute = 1; minute <= 3; minute++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(40));
            await AdvancingAsync(app, app.WaitForFavoritesAsync(app.FavoritesTimer.Fire));
            app.Clock.Advance(TimeSpan.FromSeconds(20));
            await AdvancingAsync(app, nearby.RefreshAsync(checkLocation: false));
        }

        // Assert
        Assert.AreEqual(3, app.Handler.Attempts - before);
    }

    [TestMethod]
    public async Task Tracking_RideFromFifteenMinutesOut_CostsTwelveCalls()
    {
        // Arrange: the ride is listed until it leaves, then gone.
        App app = new();
        DateTimeOffset leaves = app.Clock.GetUtcNow().AddMinutes(15);
        app.Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(app.Clock.GetUtcNow() < leaves
            ? RequestTestHandler.Departures(new TestDeparture("fav", "14", leaves, TripId: "ride"))
            : RequestTestHandler.EmptyBody));
        BusTrackingService tracker = new(app.Transit, app.Dispatcher, app.Clock, app.Predictions, app.Budget);
        DispatcherQueueTimer timer = app.Dispatcher.Timers[^1];
        tracker.Start(new Arrival("fav", "14", "14", "0", "Downtown", leaves, TripId: "ride"), "Favorite");

        // Act
        int ticks = 0;
        while (tracker.IsTracking)
        {
            app.Clock.Advance(timer.Interval);
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tracker.Changed += (_, _) => changed.TrySetResult();
            timer.Fire();
            await AdvancingAsync(app, changed.Task);
            Assert.IsLessThanOrEqualTo(30, ++ticks);
        }

        // Assert
        Assert.AreEqual(12, app.Handler.Attempts);
        CollectionAssert.AreEqual(new[] { "Time to head out" }, NotificationService.Titles);
    }

    /// <summary>Lets a coalescing window elapse while waiting, as real time would.</summary>
    private static async Task AdvancingAsync(App app, Task work)
    {
        for (int i = 0; i < 10 && !work.IsCompleted; i++)
        {
            await Task.WhenAny(work, Task.Delay(50));
            app.Clock.Advance(Window);
        }

        await work.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The app's request path: client, failure and budget gate, shared caches, coalescing coordinator.</summary>
    private sealed class App
    {
        public App()
        {
            Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(RequestTestHandler.StopIds(request))));
            TransitAppService client = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            client.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
            Budget = new TransitBudget(ApiUsageService.GetUsage, timeProvider: Clock);
            Transit = new MetadataTransitService(new CooldownTransitService(client, Clock, budget: Budget), Clock);
            Predictions = new PredictionCoordinator(Transit, Clock, () => Budget.ReuseAge, Window);
            Favorites = new ArrivalsMonitor(Transit, Dispatcher, Clock, Predictions, Budget);
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public DispatcherQueue Dispatcher { get; } = new();
        public TransitBudget Budget { get; }
        public MetadataTransitService Transit { get; }
        public PredictionCoordinator Predictions { get; }
        public ArrivalsMonitor Favorites { get; }
        public DispatcherQueueTimer FavoritesTimer => Dispatcher.Timers[0];

        public Task OpenFavoritesAsync() => AdvancingAsync(this, WaitForFavoritesAsync(() => Favorites.IsForeground = true));

        public async Task WaitForFavoritesAsync(Action start)
        {
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Updated(object? sender, EventArgs e)
            {
                if (!Favorites.IsLoading)
                {
                    completed.TrySetResult();
                }
            }

            Favorites.Updated += Updated;
            try
            {
                start();
                await AdvancingAsync(this, completed.Task);
            }
            finally
            {
                Favorites.Updated -= Updated;
            }
        }

        public PlaceArrivalsMonitor CreatePlaceMonitor(string[] stopIds) => new(
            Predictions,
            _ => Task.FromResult(new PlaceLocation(new GeoPoint(43, -88))),
            (_, location, _) => Task.FromResult(new PlaceStops(location.Point, [.. stopIds.Select(id => new NearbyStop(
                new IndexedStop(id, id, 43, -88, [new StopRouteInfo("14", "14", null, "0", "North")]), 100))],
                new PlaceNotice("None", string.Empty))),
            Clock);
    }
}
