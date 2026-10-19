using Microsoft.UI.Dispatching;
using System.Text.Json;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Services;

/// <summary>
/// Deterministic request counts for the migration and board/planner plans, through the real monitors,
/// coordinator, caches, and HTTP client, with fake HTTP and a controllable clock.
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
        PlaceArrivalsMonitor<BoardCenter> nearby = app.CreatePlaceMonitor<BoardCenter>(TransitConsumer.Nearby, ["near1", "near2"]);

        // Act
        app.Favorites.IsForeground = true;
        nearby.Configure(new NearYou());
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
        PlaceArrivalsMonitor<BoardCenter> nearby = app.CreatePlaceMonitor<BoardCenter>(TransitConsumer.Nearby, ["near"]);
        await app.OpenFavoritesAsync();
        nearby.Configure(new NearYou());
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
    public async Task BoardSuggestionAndFavorites_DueTogether_ShareOneCallWithTheTripsTransferStop()
    {
        // Arrange: both discoveries are cached, as at a known place; the trip changes at "transfer".
        App app = new();
        DateTimeOffset now = app.Clock.GetUtcNow();
        bool delayed = false;
        app.Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.Departures(
            [.. RequestTestHandler.StopIds(request).Select(id => new TestDeparture(id, "14", now.AddMinutes(id switch
            {
                "boards" => delayed ? 12 : 5,
                "transfer" => delayed ? 25 : 20,
                _ => 24,
            })))])));
        TripPlan plan = new([new TripItinerary([Ride("boards", now.AddMinutes(5)), Ride("transfer", now.AddMinutes(20))], now.AddMinutes(5), now.AddMinutes(30))], []);
        PlaceArrivalsMonitor<BoardCenter> board = app.CreatePlaceMonitor<BoardCenter>(TransitConsumer.Nearby, ["near"]);
        PlaceArrivalsMonitor<SuggestionContext> suggestion = app.CreatePlaceMonitor<SuggestionContext>(TransitConsumer.Commute, ["boards"], plan);
        board.Configure(new NearYou());
        suggestion.Configure(new SuggestionContext([], null));
        app.Favorites.IsForeground = true;
        board.IsForeground = true;
        suggestion.IsForeground = true;
        Task RefreshAllAsync(Action favorites) => AdvancingAsync(app, Task.WhenAll(
            board.RefreshAsync(checkLocation: false), suggestion.RefreshAsync(checkLocation: false), app.WaitForFavoritesAsync(favorites)));

        // Act: the flyout opens, then its timers refresh everything a minute later.
        await RefreshAllAsync(() => { });
        int opened = app.Handler.Attempts;
        LiveTrip? initial = LiveTrip.Best(plan, suggestion.Snapshot!.Arrivals, app.Clock.GetUtcNow());
        delayed = true;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await RefreshAllAsync(app.FavoritesTimer.Fire);
        LiveTrip? updated = LiveTrip.Best(plan, suggestion.Snapshot!.Arrivals, app.Clock.GetUtcNow());

        // Assert
        Assert.AreEqual(1, opened);
        Assert.AreEqual(2, app.Handler.Attempts);
        Assert.IsTrue(app.Handler.Requests.All(r => Endpoint(r) == "stop_departures"));
        CollectionAssert.AreEquivalent(new[] { "fav", "near", "boards", "transfer" }, RequestTestHandler.StopIds(app.Handler.Requests[^1]));
        CollectionAssert.AreEquivalent(new[] { "boards", "transfer" }, suggestion.Snapshot!.Arrivals.Select(a => a.StopId).ToArray());
        Assert.AreEqual(now.AddMinutes(30), initial?.Arrives);
        Assert.AreEqual(now.AddMinutes(35), updated?.Arrives);
        Assert.IsTrue(updated!.Legs.OfType<LiveRide>().All(r => r.Departure is not null));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Board_OpenWithSuggestionAndFavorites_CostsOneBatchPlusOnlyUncachedDiscovery(bool warm)
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.FlyoutOpened);
        TripDestination destination = new(new GeoPoint(43.1, -88));
        if (warm)
        {
            await app.Transit.GetNearbyLinesAsync(new GeoPoint(43, -88), PlaceBoard.RadiusMeters);
            await app.Transit.PlanTripAsync(new GeoPoint(43, -88), destination);
            app.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        int before = app.Handler.Attempts;
        PlaceArrivalsMonitor<BoardCenter> board = app.CreateDiscoveredBoardMonitor();
        PlaceArrivalsMonitor<TripDestination> suggestion = app.CreateSuggestionMonitor();
        board.Configure(new NearYou());
        suggestion.Configure(destination);
        board.IsForeground = suggestion.IsForeground = true;

        // Act
        await AdvancingAsync(app, Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync(),
            app.WaitForFavoritesAsync(() => app.Favorites.IsForeground = true)));

        // Assert
        HttpRequestMessage[] requests = [.. app.Handler.Requests.Skip(before)];
        Assert.AreEqual(warm ? 1 : 3, requests.Length);
        Assert.AreEqual(1, requests.Count(r => Endpoint(r) == "stop_departures"));
        Assert.AreEqual(warm ? 0 : 1, requests.Count(r => Endpoint(r) == "nearby_routes"));
        Assert.AreEqual(warm ? 0 : 1, requests.Count(r => Endpoint(r) == "plan"));
        CollectionAssert.AreEquivalent(new[] { "fav", "near", "boards", "transfer" },
            RequestTestHandler.StopIds(requests.Single(r => Endpoint(r) == "stop_departures")));
        Assert.AreEqual(1, suggestion.Discovery!.Plan!.Itineraries.Single().Transfers);
        Assert.AreEqual(app.Handler.Attempts, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task Board_ReopenedWithinAMinute_ReusesBothDiscoveriesAndEveryDeparture()
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.FlyoutOpened);
        PlaceArrivalsMonitor<BoardCenter> board = app.CreateDiscoveredBoardMonitor();
        PlaceArrivalsMonitor<TripDestination> suggestion = app.CreateSuggestionMonitor();
        board.Configure(new NearYou());
        suggestion.Configure(new TripDestination(new GeoPoint(43.1, -88)));
        board.IsForeground = suggestion.IsForeground = true;
        await AdvancingAsync(app, Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync(),
            app.WaitForFavoritesAsync(() => app.Favorites.IsForeground = true)));
        PlaceStops heldBoard = board.Discovery!;
        PlaceStops heldSuggestion = suggestion.Discovery!;
        board.IsForeground = suggestion.IsForeground = app.Favorites.IsForeground = false;
        app.Clock.Advance(TimeSpan.FromSeconds(45));
        int before = app.Handler.Attempts;

        // Act
        board.IsForeground = suggestion.IsForeground = app.Favorites.IsForeground = true;
        await AdvancingAsync(app, Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync()));

        // Assert
        Assert.AreEqual(3, before);
        Assert.AreEqual(before, app.Handler.Attempts);
        Assert.AreSame(heldBoard, board.Discovery);
        Assert.AreSame(heldSuggestion, suggestion.Discovery);
        Assert.HasCount(2, board.Snapshot!.Arrivals);
        Assert.HasCount(2, suggestion.Snapshot!.Arrivals);
        Assert.HasCount(1, app.Favorites.Latest.Arrivals);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Board_JumpToSavedPlaceOrStop_CostsOneBatchAndOnlyUncachedNearbyDiscovery(bool stop, bool warm)
    {
        // Arrange
        SavedStop saved = new("jump", "Jump", 43.1, -88, "14", "14", null, "0", "North", false);
        SettingsService.Stops = [.. SettingsService.Stops, saved];
        BoardCenter center = stop ? new NearStop(saved)
            : new NearPlace(FavoritePlace.ForKind(PlaceKind.Work, new Place("Work", saved.Lat, saved.Lon)));
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.CenterChanged);
        if (warm)
        {
            await app.Transit.GetNearbyLinesAsync(new GeoPoint(saved.Lat, saved.Lon), PlaceBoard.RadiusMeters);
        }

        PlaceArrivalsMonitor<BoardCenter> board = app.CreateDiscoveredBoardMonitor();
        board.Configure(new NearYou());
        board.IsForeground = true;
        await AdvancingAsync(app, board.RefreshAsync());
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        int before = app.Handler.Attempts;

        // Act
        board.Configure(center);
        await AdvancingAsync(app, board.RefreshAsync(checkLocation: false));

        // Assert
        HttpRequestMessage[] requests = [.. app.Handler.Requests.Skip(before)];
        Assert.AreEqual(warm ? 1 : 2, requests.Length);
        Assert.AreEqual(1, requests.Count(r => Endpoint(r) == "stop_departures"));
        Assert.AreEqual(warm ? 0 : 1, requests.Count(r => Endpoint(r) == "nearby_routes"));
        Assert.AreEqual("jump", board.Discovery!.Stops[0].Stop.Id);
        Assert.AreEqual(center, board.Discovery.Center);
        Assert.IsTrue(RequestTestHandler.StopIds(requests.Single(r => Endpoint(r) == "stop_departures")).Contains("jump"));
    }

    [TestMethod]
    public async Task Board_JumpToAFreshSavedStop_ReusesNearbyAndDeparturesWithoutACall()
    {
        // Arrange
        App app = new();
        PlaceArrivalsMonitor<BoardCenter> board = app.CreateDiscoveredBoardMonitor();
        board.Configure(new NearYou());
        board.IsForeground = true;
        await AdvancingAsync(app, board.RefreshAsync());
        int before = app.Handler.Attempts;

        // Act
        board.Configure(new NearStop(SettingsService.Stops.Single()));
        await AdvancingAsync(app, board.RefreshAsync(checkLocation: false));

        // Assert
        Assert.AreEqual(2, before);
        Assert.AreEqual(before, app.Handler.Attempts);
        Assert.HasCount(2, board.Snapshot!.Arrivals);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(61, 1)]
    public async Task Board_AllSavedStops_CostsNoDiscoveryAndOnlyStaleDepartures(int ageSeconds, int calls)
    {
        // Arrange
        App app = new();
        await app.OpenFavoritesAsync();
        PlaceArrivalsMonitor<BoardCenter> board = app.CreateDiscoveredBoardMonitor();
        board.Configure(new AllSavedStops());
        board.IsForeground = true;
        app.Clock.Advance(TimeSpan.FromSeconds(ageSeconds));
        int before = app.Handler.Attempts;

        // Act
        await AdvancingAsync(app, board.RefreshAsync());

        // Assert
        Assert.AreEqual(calls, app.Handler.Attempts - before);
        Assert.IsTrue(app.Handler.Requests.All(r => Endpoint(r) == "stop_departures"));
        Assert.AreEqual("fav", board.Discovery!.Stops.Single().Stop.Id);
        Assert.AreEqual("fav", board.Snapshot!.Arrivals.Single().StopId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Suggestion_NewDestination_CostsOnePlanAndABatchOnlyForUncachedBoardingStops(bool differentStops)
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, TransitTrigger.DestinationChanged);
        PlaceArrivalsMonitor<TripDestination> suggestion = app.CreateSuggestionMonitor();
        TripDestination first = new(new GeoPoint(43.1, -88));
        suggestion.Configure(first);
        suggestion.IsForeground = true;
        await AdvancingAsync(app, suggestion.RefreshAsync());
        TripPlan held = suggestion.Discovery!.Plan!;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond = app.Handler.RespondAsync;
        app.Handler.RespondAsync = (request, token) => Endpoint(request) == "plan"
            ? Task.FromResult(RequestTestHandler.Response(PlanBody(app.Clock.GetUtcNow(), differentStops ? "other" : "boards")))
            : respond(request, token);
        int before = app.Handler.Attempts;

        // Act
        suggestion.Configure(new TripDestination(new GeoPoint(43.2, -88)));
        await AdvancingAsync(app, suggestion.RefreshAsync(checkLocation: false));
        int changed = app.Handler.Attempts;
        suggestion.Configure(first);
        await AdvancingAsync(app, suggestion.RefreshAsync(checkLocation: false));

        // Assert
        HttpRequestMessage[] requests = [.. app.Handler.Requests.Skip(before)];
        Assert.AreEqual(differentStops ? 2 : 1, changed - before);
        Assert.AreEqual(changed, app.Handler.Attempts);
        Assert.AreEqual(1, requests.Count(r => Endpoint(r) == "plan"));
        Assert.AreEqual(differentStops ? 1 : 0, requests.Count(r => Endpoint(r) == "stop_departures"));
        Assert.AreSame(held, suggestion.Discovery!.Plan);
        Assert.AreEqual(app.Handler.Attempts, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task Destination_AddressSubmitThenChooseAndSave_CostsOnePlanAndNoTransitSearch()
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, TransitTrigger.DestinationChanged);
        int addressLookups = 0;
        DestinationSearch search = new((_, _) =>
        {
            ++addressLookups;
            return Task.FromResult<IReadOnlyList<GeocodeResult>>([new("123 Main Street", new GeoPoint(43.1, -88))]);
        }, app.Transit.SearchStopsAsync);
        GeoPoint origin = new(43, -88);

        // Act
        DestinationSearchResults results = await search.SearchAsync("123 Main Street", origin);
        int searched = app.Handler.Attempts;
        DestinationResult result = results.Items.Single();
        TripDestination target = new(result.Location, result.StopId, Persist: false);
        TripPlan plan = await app.Transit.PlanTripAsync(origin, target);
        app.Transit.SaveDestinationPlan(origin, target);
        TripPlan saved = await app.Transit.PlanTripAsync(origin, target with { Persist = true });

        // Assert
        Assert.AreEqual(1, addressLookups);
        Assert.AreEqual(0, searched);
        Assert.AreEqual(1, app.Handler.Attempts);
        Assert.AreEqual("plan", Endpoint(app.Handler.Requests.Single()));
        Assert.AreSame(plan, saved);
        Assert.HasCount(1, app.Transit.Export().TripPlans);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task Destination_StopSubmitThenChoose_CostsOneCachedSearchAndOneStopTargetPlan()
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, TransitTrigger.PickerSelection);
        DestinationSearch search = new(null, app.Transit.SearchStopsAsync);
        GeoPoint origin = new(43, -88);

        // Act
        await search.SearchAsync("Central Station", origin);
        DestinationSearchResults results = await search.SearchAsync("  central   station  ", origin);
        int searched = app.Handler.Attempts;
        DestinationResult result = results.Items.Single();
        await app.Transit.PlanTripAsync(origin, new TripDestination(result.Location, result.StopId, Persist: false));

        // Assert
        Assert.AreEqual(1, searched);
        Assert.AreEqual(2, app.Handler.Attempts);
        Assert.AreEqual("search_stops", Endpoint(app.Handler.Requests[0]));
        Assert.AreEqual("plan", Endpoint(app.Handler.Requests[1]));
        Assert.AreEqual("destination", RequestTestHandler.Query(app.Handler.Requests[1], "to_global_stop_ids"));
        Assert.AreEqual(2, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task Replan_InfeasibleTripCheckedEveryMinute_CostsAtMostOnePlanPerFifteenMinutes()
    {
        // Arrange
        App app = new();
        GeoPoint origin = new(43, -88);
        TripDestination destination = new(new GeoPoint(43.1, -88));
        TripPlan original = await app.Transit.PlanTripAsync(origin, destination);
        app.Clock.Advance(TimeSpan.FromHours(1));
        Assert.IsNull(LiveTrip.Best(original, [], app.Clock.GetUtcNow()));
        int before = app.Handler.Attempts;

        // Act
        TripPlan replanned = await app.Transit.ReplanTripAsync(origin, destination);
        for (int minute = 1; minute < 15; minute++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            TripPlan reused = await app.Transit.ReplanTripAsync(origin, destination);
            Assert.AreSame(replanned, reused);
        }

        int withinInterval = app.Handler.Attempts - before;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        TripPlan next = await app.Transit.ReplanTripAsync(origin, destination);

        // Assert
        Assert.AreEqual(1, withinInterval);
        Assert.AreEqual(2, app.Handler.Attempts - before);
        Assert.AreNotSame(replanned, next);
        Assert.IsNotNull(LiveTrip.Best(next, [], app.Clock.GetUtcNow()));
        Assert.IsTrue(app.Handler.Requests.All(r => Endpoint(r) == "plan"));
        Assert.AreEqual(app.Handler.Attempts, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task TimedPlans_SameRoundedTimeAndReturningToNow_CostOnePlanPerDistinctTime()
    {
        // Arrange
        App app = new();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Trip, TransitTrigger.TripTimeChanged);
        app.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"results":[]}"""));
        GeoPoint origin = new(43, -88);
        TripDestination destination = new(new GeoPoint(43.1, -88));
        DateTimeOffset tomorrow = app.Clock.GetUtcNow().AddDays(1);
        TripPlan live = await app.Transit.PlanTripAsync(origin, destination);

        // Act
        await app.Transit.PlanTripAsync(origin, destination, new TripTime(TripTimeMode.LeaveAt, tomorrow.AddMinutes(1)));
        await app.Transit.PlanTripAsync(origin, destination, new TripTime(TripTimeMode.LeaveAt, tomorrow.AddMinutes(4)));
        await app.Transit.PlanTripAsync(origin, destination, new TripTime(TripTimeMode.LeaveAt, tomorrow.AddMinutes(6)));
        await app.Transit.PlanTripAsync(origin, destination, new TripTime(TripTimeMode.ArriveBy, tomorrow.AddMinutes(5)));
        TripPlan restored = await app.Transit.PlanTripAsync(origin, destination);

        // Assert
        Assert.AreSame(live, restored);
        Assert.AreEqual(4, app.Handler.Attempts);
        Assert.IsTrue(app.Handler.Requests.All(r => r.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)));
        Assert.AreEqual(4, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    public async Task TimedTrip_FutureThenNearDeparture_JoinsTheSharedBatchOnlyWhenLiveAndClearsOnlyItsConsumer()
    {
        // Arrange
        App app = new();
        DateTimeOffset departs = app.Clock.GetUtcNow().AddHours(1);
        TripPlan plan = new([new TripItinerary([Ride("future", departs)], departs, departs.AddMinutes(10))], []);
        TripTime time = new(TripTimeMode.LeaveAt, departs);
        IReadOnlyList<string> Watched() => PlaceBoard.BoardingStopIds(plan, i => time.UsesLiveTimes(i, app.Clock.GetUtcNow()));
        PlaceArrivalsMonitor<string> timed = new(app.Predictions, TransitConsumer.Trip,
            _ => Task.FromResult(new PlaceLocation(null)),
            (_, _, _) => Task.FromResult(new PlaceStops(new GeoPoint(43, -88), [], new PlaceNotice("None", ""), Plan: plan, WatchedStops: Watched())),
            isOutdated: found => !found.StopIds.SequenceEqual(Watched()));
        PlaceArrivalsMonitor<BoardCenter> board = app.CreatePlaceMonitor<BoardCenter>(TransitConsumer.Nearby, ["near"]);
        PlaceArrivalsMonitor<SuggestionContext> suggestion = app.CreatePlaceMonitor<SuggestionContext>(TransitConsumer.Commute, ["live"]);
        board.Configure(new NearYou());
        suggestion.Configure(new SuggestionContext([], null));
        timed.Configure("future");
        board.IsForeground = suggestion.IsForeground = timed.IsForeground = true;

        // Act: tomorrow's/future boarding stops must not ask for predictions.
        await AdvancingAsync(app, Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync(), timed.RefreshAsync()));
        int opened = app.Handler.Attempts;
        string[] initialStops = RequestTestHandler.StopIds(app.Handler.Requests[^1]);
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        await AdvancingAsync(app, Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync(), timed.RefreshAsync()));
        string[] liveStops = RequestTestHandler.StopIds(app.Handler.Requests[^1]);
        int becameLive = app.Handler.Attempts;
        timed.IsForeground = false;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await AdvancingAsync(app, app.Predictions.GetAsync(["tracked"], TimeSpan.FromSeconds(10), combineBrowsing: true));

        // Assert
        Assert.AreEqual(1, opened);
        Assert.AreEqual(2, becameLive);
        CollectionAssert.AreEquivalent(new[] { "near", "live" }, initialStops);
        CollectionAssert.AreEquivalent(new[] { "near", "live", "future" }, liveStops);
        CollectionAssert.AreEquivalent(new[] { "tracked", "near", "live" }, RequestTestHandler.StopIds(app.Handler.Requests[^1]));
    }

    [TestMethod]
    public async Task TripView_OpenSwitchOptionsAndReturnToBoard_MakesNoAdditionalRequest()
    {
        // Arrange: the suggestion has already discovered and watched every boarding stop.
        App app = new();
        app.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        DateTimeOffset now = app.Clock.GetUtcNow();
        TripItinerary transfer = new([Ride("boards", now.AddMinutes(5)), Ride("transfer", now.AddMinutes(20))], now.AddMinutes(5), now.AddMinutes(30));
        TripItinerary direct = new([Ride("direct", now.AddMinutes(8))], now.AddMinutes(8), now.AddMinutes(18));
        TripItinerary walk = new([new WalkLeg(now, now.AddMinutes(25), 2000, [])], now, now.AddMinutes(25));
        TripPlan plan = new([transfer, direct, walk], []);
        PlaceArrivalsMonitor<SuggestionContext> suggestion = app.CreatePlaceMonitor<SuggestionContext>(TransitConsumer.Commute, ["boards"], plan);
        suggestion.Configure(new SuggestionContext([], null));
        suggestion.IsForeground = true;
        await AdvancingAsync(app, suggestion.RefreshAsync(checkLocation: false));
        int before = app.Handler.Attempts;
        TripSelection view = new();
        IReadOnlyList<Arrival> arrivals = suggestion.Snapshot!.Arrivals;

        // Act: opening, picking every option, and returning need only the held plan and batch.
        view.Update(suggestion.Discovery!.Plan!, arrivals, now);
        foreach (LiveTrip option in view.Options)
        {
            view.Select(option.Itinerary);
            _ = view.Selected!.FirstRideDepartures(arrivals, 6);
        }
        view.Update(plan, arrivals, now.AddSeconds(15));

        // Assert
        Assert.AreEqual(1, before);
        Assert.AreEqual(before, app.Handler.Attempts);
        Assert.HasCount(3, view.Options);
        Assert.AreSame(walk, view.Selected?.Itinerary);
        CollectionAssert.AreEquivalent(new[] { "boards", "transfer", "direct" }, RequestTestHandler.StopIds(app.Handler.Requests.Single()));
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

    /// <summary>A ten-minute ride on the 14, boarding at <paramref name="stopId"/>.</summary>
    private static RideLeg Ride(string stopId, DateTimeOffset departs) => new(departs, departs.AddMinutes(10),
        [new RideLine(new TransitRoute("14", "14", "Route 14", null), new TransitDirection("0", "North"),
            new TransitStop(stopId, stopId, new GeoPoint(43, -88)), null, 4, [], [new PlannedDeparture(departs, departs.AddMinutes(10), true, null)])]);

    private static string Endpoint(HttpRequestMessage request) => request.RequestUri!.Segments[^1];

    private static string PlanBody(DateTimeOffset now, string boardingStop = "boards") => JsonSerializer.Serialize(new
    {
        results = new[]
        {
            new
            {
                start_time = now.AddMinutes(5).ToUnixTimeSeconds(),
                end_time = now.AddMinutes(30).ToUnixTimeSeconds(),
                legs = new[] { (Stop: boardingStop, Minutes: 5), (Stop: "transfer", Minutes: 20) }.Select(leg => new
                {
                    leg_mode = "transit",
                    start_time = now.AddMinutes(leg.Minutes).ToUnixTimeSeconds(),
                    end_time = now.AddMinutes(leg.Minutes + 10).ToUnixTimeSeconds(),
                    departures = new[]
                    {
                        new
                        {
                            departure_time = now.AddMinutes(leg.Minutes).ToUnixTimeSeconds(),
                            plan_details = new
                            {
                                global_route_id = "14",
                                stop_schedule_items = new[] { new { global_stop_id = leg.Stop } },
                            },
                        },
                    },
                    routes = new[]
                    {
                        new
                        {
                            global_route_id = "14",
                            route_short_name = "14",
                            itineraries = new[]
                            {
                                new
                                {
                                    direction_id = 0,
                                    stops = new[] { new { global_stop_id = leg.Stop, stop_name = leg.Stop, stop_lat = 43, stop_lon = -88 } },
                                },
                            },
                        },
                    },
                }),
            },
        },
    });

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
            Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(Endpoint(request) switch
            {
                "nearby_routes" => """
                    {"nearby_routes":[{"global_route_id":"30","route_short_name":"30","merged_itineraries":[{"direction_id":0,
                      "closest_stop":{"global_stop_id":"near","stop_name":"Near","stop_lat":43.0005,"stop_lon":-88,"distance":55},
                      "itineraries":[{"direction_id":0,"headsign":"Downtown"}]}]}]}
                    """,
                "plan" => PlanBody(Clock.GetUtcNow()),
                "search_stops" => """
                    {"results":[{"global_stop_id":"destination","stop_name":"Central Station","stop_lat":43.1,"stop_lon":-88}]}
                    """,
                "stop_departures" => RequestTestHandler.LiveFor(RequestTestHandler.StopIds(request)),
                _ => throw new AssertFailedException("Unexpected Transit endpoint."),
            }));
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

        public PlaceArrivalsMonitor<BoardCenter> CreateDiscoveredBoardMonitor() => new(
            Predictions, TransitConsumer.Nearby,
            _ => Task.FromResult(new PlaceLocation(new GeoPoint(43, -88))),
            async (center, location, token) =>
            {
                GeoPoint? point = center.PointFrom(location.Point);
                PlaceBoardResult board = center is AllSavedStops ? PlaceBoard.Saved(SettingsService.Stops)
                    : PlaceBoard.Around(await Transit.GetNearbyLinesAsync(point!.Value, PlaceBoard.RadiusMeters, token),
                        SettingsService.Stops, point.Value, 8);
                return new PlaceStops(point, board.Stops, new PlaceNotice("None", ""),
                    Lines: board.Lines, EveryLineAt: board.EveryLineAt, Center: center);
            });

        public PlaceArrivalsMonitor<TripDestination> CreateSuggestionMonitor() => new(
            Predictions, TransitConsumer.Commute,
            _ => Task.FromResult(new PlaceLocation(new GeoPoint(43, -88))),
            async (target, location, token) =>
            {
                TripPlan plan = await Transit.PlanTripAsync(location.Point!.Value, target, token);
                PlaceBoardResult board = PlaceBoard.Commute(plan, 8);
                return new PlaceStops(location.Point, board.Stops, new PlaceNotice("None", ""),
                    Lines: board.Lines, Trips: board.Trips, Plan: plan);
            });

        /// <summary>A place monitor whose discovery lists <paramref name="stopIds"/>, plus the rides of <paramref name="plan"/>.</summary>
        public PlaceArrivalsMonitor<TContext> CreatePlaceMonitor<TContext>(TransitConsumer consumer, string[] stopIds, TripPlan? plan = null)
            where TContext : class => new(
            Predictions,
            consumer,
            _ => Task.FromResult(new PlaceLocation(new GeoPoint(43, -88))),
            (_, location, _) => Task.FromResult(new PlaceStops(location.Point, [.. stopIds.Select(id => new NearbyStop(
                new IndexedStop(id, id, 43, -88, [new StopRouteInfo("14", "14", null, "0", "North")]), 100))],
                new PlaceNotice("None", string.Empty), Plan: plan)));
    }
}
