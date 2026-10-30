using System.Net;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Commute;

[TestClass]
public sealed class PlaceArrivalsMonitorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshAsync_StableLocation_SkipsDiscoveryAndStartsPredictionsBeforeSlowLocation(bool suggestion)
    {
        Fixture f = new(suggestion);
        await f.Monitor.RefreshAsync();
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);
        TaskCompletionSource<PlaceLocation> location = new();
        f.Locate = _ => location.Task;
        TaskCompletionSource<bool> published = new();
        f.Monitor.Updated += (_, _) =>
        {
            if (f.Monitor.Snapshot?.FetchedAt == f.Clock.GetLocalNow())
            {
                published.TrySetResult(true);
            }
        };

        Task refresh = f.Monitor.RefreshAsync();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(refresh.IsCompleted);
        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(1, f.Walks);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        location.SetResult(new PlaceLocation(f.Point));
        await refresh;
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(f.Clock.GetLocalNow().AddSeconds(-10), f.Monitor.Snapshot!.FetchedAt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IsForeground_QuickReopen_PreservesImmediateRowsAndOriginalTimestampWithoutHttp(bool suggestion)
    {
        Fixture f = new(suggestion);
        await f.Monitor.RefreshAsync();
        PlaceStops? discovery = f.Monitor.Discovery;
        PredictionSnapshot? snapshot = f.Monitor.Snapshot;
        f.Monitor.IsForeground = false;
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Configure();
        f.Monitor.IsForeground = true;

        Assert.AreSame(discovery, f.Monitor.Discovery);
        Assert.AreSame(snapshot, f.Monitor.Snapshot); // Available before any awaited work.
        await f.Monitor.RefreshAsync();

        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(snapshot!.FetchedAt, f.Monitor.Snapshot!.FetchedAt);
    }

    [TestMethod]
    public async Task Configure_DestinationOrPlaceChanges_ClearsOldCommuteBeforeAwaitingDiscovery()
    {
        Fixture f = new(suggestion: true);
        await f.Monitor.RefreshAsync();
        f.Context = f.Context with { DestinationId = "Home" };

        f.Configure();

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        await f.Monitor.RefreshAsync();
        Assert.AreEqual("Home", f.Monitor.Discovery!.Leg!.To.Id);
        f.Context = f.Context with
        {
            Places = [.. f.Context.Places.Select(p => p with { Name = p.Name + " updated" })],
        };
        f.Configure();
        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
    }

    [TestMethod]
    public async Task Configure_SamePlacesInANewList_KeepsTheTrip()
    {
        // Arrange: settings hand back a new list on every read.
        Fixture f = new(suggestion: true);
        await f.Monitor.RefreshAsync();
        PlaceStops? trip = f.Monitor.Discovery;
        f.Context = f.Context with { Places = [.. f.Context.Places] };

        // Act
        f.Configure();

        // Assert
        Assert.AreSame(trip, f.Monitor.Discovery);
        Assert.IsNotNull(f.Monitor.Snapshot);
    }

    [TestMethod]
    public async Task Configure_ResetExplicitDestinationOnReopen_DoesNotDisplayPreviousTripAsCurrent()
    {
        Fixture f = new(suggestion: true);
        f.Context = f.Context with { DestinationId = "Home" };
        f.Configure();
        await f.Monitor.RefreshAsync();
        f.Monitor.IsForeground = false;
        f.Context = f.Context with { DestinationId = null };

        f.Configure();
        f.Monitor.IsForeground = true;

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        await f.Monitor.RefreshAsync();
        Assert.AreEqual("Work", f.Monitor.Discovery!.Leg!.To.Id);
    }

    [TestMethod]
    public async Task Configure_CenterChange_DoesNotPresentTheOldBoardAsTheNewOne()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        FavoritePlace work = f.Context.Places.Single(p => p.Kind == PlaceKind.Work);
        f.Center = new NearPlace(work);

        f.Configure();

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        await f.Monitor.RefreshAsync();
        Assert.AreEqual(work.Location, f.Monitor.Discovery!.Origin);
    }

    [TestMethod]
    public async Task RefreshAsync_SubthresholdThenCumulativeMovement_UsesDiscoveryOriginAsMovementBaseline()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        GeoPoint initial = f.Point;
        f.Point = new GeoPoint(initial.Lat + 0.0001, initial.Lon); // About 11 metres.
        await f.Monitor.RefreshAsync();
        Assert.AreEqual(1, f.Discoveries);
        f.Point = new GeoPoint(initial.Lat + 0.0002, initial.Lon); // About 22 metres.
        await f.Monitor.RefreshAsync();
        Assert.AreEqual(1, f.Discoveries);

        f.Point = new GeoPoint(initial.Lat + 0.0003, initial.Lon); // About 33 metres.
        await f.Monitor.RefreshAsync();

        Assert.AreEqual(2, f.Discoveries);
        Assert.AreEqual(f.Point, f.Monitor.Discovery!.Origin);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshAsync_RoughToPreciseFix_UpdatesWalksAndFetchesOnlyNewStopIds(bool changedStops)
    {
        Fixture f = new();
        TaskCompletionSource<GeoPoint?> precise = new();
        f.Locate = _ => Task.FromResult(new PlaceLocation(f.Point, precise.Task));
        TaskCompletionSource<bool> first = new();
        f.Monitor.Updated += (_, _) =>
        {
            if (f.Monitor.Snapshot is not null)
            {
                first.TrySetResult(true);
            }
        };
        Task refresh = f.Monitor.RefreshAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DateTimeOffset fetched = f.Monitor.Snapshot!.FetchedAt;
        int walkMinutes = f.Monitor.Discovery!.Stops[0].WalkMinutes(80);
        if (changedStops)
        {
            f.Ids = ["2"];
            f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(["2"])));
        }
        f.Point = new GeoPoint(f.Point.Lat + 0.001, f.Point.Lon);

        precise.SetResult(f.Point);
        await refresh;

        Assert.AreEqual(2, f.Discoveries);
        Assert.AreEqual(changedStops ? 2 : 1, f.Handler.Attempts);
        Assert.AreEqual(f.Point, f.Monitor.Discovery!.Origin);
        Assert.AreNotEqual(walkMinutes, f.Monitor.Discovery.Stops[0].WalkMinutes(80));
        Assert.AreEqual(fetched, f.Monitor.Snapshot!.FetchedAt);
        if (changedStops)
        {
            Assert.HasCount(1, f.Monitor.Snapshot.Arrivals);
            Assert.IsTrue(f.Monitor.Snapshot.Arrivals.All(a => a.StopId == "2"));
        }
    }

    [TestMethod]
    public async Task RefreshAsync_MovementWhileWalkingIsSlow_PublishesOldValidPredictionsBeforeNewDiscovery()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        PlaceStops? old = f.Monitor.Discovery;
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);
        f.Point = new GeoPoint(f.Point.Lat + 0.001, f.Point.Lon);
        TaskCompletionSource<PlaceStops> walking = new();
        f.Discover = (_, _, _) => walking.Task;
        TaskCompletionSource<bool> predictions = new();
        f.Monitor.Updated += (_, _) =>
        {
            if (f.Monitor.Snapshot?.FetchedAt == f.Clock.GetLocalNow())
            {
                predictions.TrySetResult(true);
            }
        };

        Task refresh = f.Monitor.RefreshAsync();
        await predictions.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(refresh.IsCompleted);
        Assert.AreSame(old, f.Monitor.Discovery);
        Assert.AreEqual(2, f.Handler.Attempts);
        walking.SetResult(f.Found(f.Center, new PlaceLocation(f.Point)));
        await refresh;
        Assert.AreEqual(2, f.Handler.Attempts); // Same IDs reuse the just-published response.
        Assert.AreEqual(f.Point, f.Monitor.Discovery!.Origin);
    }

    [TestMethod]
    public async Task RefreshAsync_OverlappingRoutineAndManualRequests_SharesHttpAndSlowLocation()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        TaskCompletionSource<PlaceLocation> location = new();
        TaskCompletionSource<HttpResponseMessage> http = new();
        f.Locate = _ => location.Task;
        f.Handler.RespondAsync = (_, token) => http.Task.WaitAsync(token);
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);

        Task routine = f.Monitor.RefreshAsync();
        Task forced = f.Monitor.RefreshAsync(force: true);
        Assert.AreEqual(2, f.Locations);
        Assert.AreEqual(2, f.Handler.Attempts);
        http.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        location.SetResult(new PlaceLocation(f.Point));
        await Task.WhenAll(routine, forced);

        Assert.AreEqual(1, f.Discoveries);
        Assert.IsFalse(f.Monitor.IsLoading);
        Assert.IsNotNull(f.Monitor.Snapshot);
    }

    [TestMethod]
    public async Task RefreshAsync_FreshManualRefresh_BypassesPredictionsButNotDiscovery()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();

        await f.Monitor.RefreshAsync(force: true);

        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(1, f.Walks);
    }

    [TestMethod]
    public async Task RefreshAsync_HiddenSlowLocation_DoesNotStartDownstreamDiscoveryOrHttp()
    {
        Fixture f = new();
        TaskCompletionSource<PlaceLocation> location = new();
        f.Locate = _ => location.Task;
        Task refresh = f.Monitor.RefreshAsync();

        f.Monitor.IsForeground = false;
        location.SetResult(new PlaceLocation(f.Point));
        await refresh;
        await f.Monitor.RefreshAsync(force: true);

        Assert.AreEqual(0, f.Discoveries);
        Assert.AreEqual(0, f.Handler.Attempts);
        Assert.IsNull(f.Monitor.Discovery);
    }

    [TestMethod]
    public async Task IsForeground_PermissionReturnDuringOldLookup_StartsNewLookupAndIgnoresOldDenial()
    {
        Fixture f = new();
        TaskCompletionSource<PlaceLocation> oldLocation = new();
        int locations = 0;
        f.Locate = _ => ++locations == 1
            ? oldLocation.Task
            : Task.FromResult(new PlaceLocation(f.Point));
        Task oldRefresh = f.Monitor.RefreshAsync();
        Assert.IsFalse(oldRefresh.IsCompleted);

        f.Monitor.IsForeground = false;
        f.Configure();
        f.Monitor.IsForeground = true;
        await f.Monitor.RefreshAsync(force: true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, locations);
        Assert.AreEqual(f.Point, f.Monitor.Discovery!.Origin);
        oldLocation.SetResult(new PlaceLocation(null, Error: "Location access is off."));
        await oldRefresh;
        Assert.AreEqual(f.Point, f.Monitor.Discovery.Origin);
        Assert.IsNull(f.Monitor.LocationError);
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Configure_SupersededSlowDiscovery_DropsOldStopsAndDoesNotStartPredictions()
    {
        Fixture f = new();
        TaskCompletionSource<PlaceStops> walking = new();
        f.Discover = (_, _, _) => walking.Task;
        Task refresh = f.Monitor.RefreshAsync();
        f.Center = new NearPlace(f.Context.Places[0]);

        f.Configure();
        walking.SetResult(f.Found(new NearYou(), new PlaceLocation(f.Point)));
        await refresh;

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        Assert.AreEqual(0, f.Handler.Attempts);
        Assert.IsFalse(f.Monitor.IsLoading);
    }

    [TestMethod]
    public async Task IsForeground_HiddenDuringDiscovery_CancelsTheDiscoveryRequestsAndCompletesQuietly()
    {
        Fixture f = new();
        CancellationToken observed = default;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Discover = async (_, _, token) =>
        {
            observed = token;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        };
        Task refresh = f.Monitor.RefreshAsync();
        await started.Task;

        f.Monitor.IsForeground = false;
        await refresh;

        Assert.IsTrue(observed.IsCancellationRequested);
        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsFalse(f.Monitor.IsLoading);
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Configure_ContextChangedDuringDiscovery_CancelsOnlyTheOldDiscovery()
    {
        Fixture f = new();
        List<CancellationToken> tokens = [];
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Discover = async (context, location, token) =>
        {
            tokens.Add(token);
            if (tokens.Count == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return f.Found(context, location);
        };
        Task stale = f.Monitor.RefreshAsync();
        await started.Task;

        f.Center = new NearPlace(f.Context.Places[0]);
        f.Configure();
        await stale;
        await f.Monitor.RefreshAsync();

        Assert.HasCount(2, tokens);
        Assert.IsTrue(tokens[0].IsCancellationRequested);
        Assert.IsFalse(tokens[1].IsCancellationRequested);
        Assert.IsNotNull(f.Monitor.Discovery);
    }

    [TestMethod]
    public async Task RefreshAsync_PredictionFailure_PreservesDiscoveryAndLastGoodTimestampThenRecovers()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        PredictionSnapshot? good = f.Monitor.Snapshot;
        PlaceStops? discovery = f.Monitor.Discovery;
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("error", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Monitor.RefreshAsync(force: true));

        Assert.AreSame(good, f.Monitor.Snapshot);
        Assert.AreSame(discovery, f.Monitor.Discovery);
        Assert.IsFalse(f.Monitor.IsLoading);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await f.Monitor.RefreshAsync(force: true);
        Assert.AreEqual(3, f.Handler.Attempts);
        Assert.AreEqual(1, f.Discoveries);
    }

    [TestMethod]
    public async Task RefreshAsync_LocationFailure_KeepsLastKnownDiscoveryAndSurfacesReason()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        PlaceStops? good = f.Monitor.Discovery;
        f.Locate = _ => Task.FromResult(new PlaceLocation(null, Error: "Fixture location unavailable"));

        await f.Monitor.RefreshAsync();

        Assert.AreSame(good, f.Monitor.Discovery);
        Assert.AreEqual("Fixture location unavailable", f.Monitor.LocationError);
        Assert.AreEqual(1, f.Discoveries);
        f.Locate = _ => Task.FromResult(new PlaceLocation(f.Point));
        await f.Monitor.RefreshAsync();
        Assert.IsNull(f.Monitor.LocationError);
    }

    [TestMethod]
    public async Task RefreshAsync_EmptyPredictions_AreFreshAndRetainedOnReopen()
    {
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));
        await f.Monitor.RefreshAsync();
        f.Monitor.IsForeground = false;
        f.Monitor.IsForeground = true;

        await f.Monitor.RefreshAsync();

        Assert.IsNotNull(f.Monitor.Snapshot);
        Assert.HasCount(0, f.Monitor.Snapshot.Arrivals);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.Discoveries);
    }

    [TestMethod]
    public async Task Configure_GuessedTripCrossesNoon_InvalidatesBeforeShowingDifferentCommute()
    {
        Fixture f = new(suggestion: true);
        f.Clock.Advance(TimeSpan.FromHours(-9)); // 11 AM UTC in the fixture.
        f.Locate = _ => Task.FromResult(new PlaceLocation(null));
        await f.Monitor.RefreshAsync();
        Assert.AreEqual("Work", f.Monitor.Discovery!.Leg!.To.Id);
        f.Clock.Advance(TimeSpan.FromHours(2));

        f.Configure();

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        await f.Monitor.RefreshAsync();
        Assert.AreEqual("Home", f.Monitor.Discovery!.Leg!.To.Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshAsync_WarmOpenMinuteAndReopen_CountsZeroRediscoveryAndOneDeparturesCall(bool suggestion)
    {
        Fixture f = new(suggestion);
        await f.Monitor.RefreshAsync();
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);
        int initial = f.Handler.Attempts;

        for (int second = 0; second < 60; second += 15)
        {
            if (second % 30 == 0)
            {
                await f.Monitor.RefreshAsync();
            }
            f.Monitor.IsForeground = false;
            f.Configure();
            f.Monitor.IsForeground = true;
            await f.Monitor.RefreshAsync();
            f.Clock.Advance(TimeSpan.FromSeconds(15));
        }

        Assert.AreEqual(1, f.Handler.Attempts - initial);
        Assert.AreEqual(1, f.Discoveries);
        Assert.AreEqual(1, f.Walks);
        Assert.AreEqual(0, f.Service.Diagnostics!.Snapshot.Count(o =>
            o.Phase == TransitRequestPhase.Started && o.Endpoint != TransitEndpoint.Departures));
    }

    [TestMethod]
    public async Task RefreshAsync_ContextChangedToNewStops_PublishesOnlyTheNewStops()
    {
        Fixture f = new(suggestion: true);
        await f.Monitor.RefreshAsync();
        f.Context = f.Context with { DestinationId = "Home" };
        f.Ids = ["2"];
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(["2"])));
        f.Configure();
        Assert.IsNull(f.Monitor.Snapshot);
        List<string> publishedStops = [];
        f.Monitor.Updated += (_, _) =>
        {
            if (f.Monitor.Snapshot is { } raw)
            {
                publishedStops.AddRange(raw.Arrivals.Select(a => a.StopId));
            }
        };

        await f.Monitor.RefreshAsync();

        Assert.IsGreaterThan(0, publishedStops.Count);
        Assert.IsTrue(publishedStops.All(id => id == "2"));
        Assert.AreEqual("Home", f.Monitor.Discovery!.Leg?.To.Id);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task RefreshAsync_OutdatedTrip_FindsItAgainWhileStillShowingTheOldOne()
    {
        // Arrange: the trip's plan expired, and the new one is slow to come.
        Fixture f = new(suggestion: true);
        await f.Monitor.RefreshAsync();
        PlaceStops? old = f.Monitor.Discovery;
        f.Outdated = true;
        TaskCompletionSource<PlaceStops> planning = new();
        f.Discover = (_, _, _) => planning.Task;

        // Act
        f.Configure();
        Task refresh = f.Monitor.RefreshAsync();

        // Assert: kept through Configure and while the new trip is on its way.
        Assert.AreSame(old, f.Monitor.Discovery);
        Assert.IsNotNull(f.Monitor.Snapshot);
        Assert.AreEqual(2, f.Discoveries);
        f.Outdated = false;
        planning.SetResult(f.Found(f.Context, new PlaceLocation(f.Point)));
        await refresh;
        Assert.AreNotSame(old, f.Monitor.Discovery);
        Assert.AreEqual(1, f.Handler.Attempts); // The same stops keep their departures.
    }

    [TestMethod]
    public async Task RefreshAsync_SuggestionWithATransfer_FetchesEveryRidesBoardingStop()
    {
        // Arrange: the trip boards the 14 at "1" or the 30 at "alt", then changes at "transfer".
        Fixture f = new(suggestion: true);
        f.Discover = (context, location, _) => Task.FromResult(f.Found(context, location) with { Plan = TransferPlan(f.Clock.GetUtcNow()) });

        // Act
        await f.Monitor.RefreshAsync();

        // Assert
        CollectionAssert.AreEquivalent(new[] { "1", "alt", "transfer" }, RequestTestHandler.StopIds(f.Handler.Requests.Single()));
        CollectionAssert.AreEquivalent(new[] { "1", "alt", "transfer" }, f.Monitor.Discovery!.StopIds.ToArray());
    }

    [TestMethod]
    public async Task IsForeground_BoardAndSuggestionTogether_KeepBothConsumersStopsInTheBatch()
    {
        // Arrange
        Fixture f = new();
        await f.ShowBothAsync();
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);

        // Act: another consumer's request fills its batch with every due browsing stop.
        await f.Predictions.GetAsync(["tracked"], TimeSpan.FromSeconds(10), combineBrowsing: true);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "tracked", "near", "trip" }, RequestTestHandler.StopIds(f.Handler.Requests[^1]));
    }

    [TestMethod]
    public async Task IsForeground_HidingTheBoard_KeepsTheSuggestionsStopsRegistered()
    {
        // Arrange
        Fixture f = new();
        await f.ShowBothAsync();
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);

        // Act
        f.Board.IsForeground = false;
        await f.Predictions.GetAsync(["tracked"], TimeSpan.FromSeconds(10), combineBrowsing: true);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "tracked", "trip" }, RequestTestHandler.StopIds(f.Handler.Requests[^1]));
    }

    [TestMethod]
    public async Task RefreshAsync_BoardAndSuggestionInOneRefresh_ShareOneLocationLookupAndItsRefinement()
    {
        // Arrange
        Fixture f = new();
        TaskCompletionSource<GeoPoint?> precise = new();
        int lookups = 0;
        SharedLocation location = new(_ =>
        {
            ++lookups;
            return Task.FromResult(new PlaceLocation(f.Point, precise.Task));
        });
        int boardFinds = 0;
        int tripFinds = 0;
        PredictionCoordinator predictions = new(f.Service, f.Clock);
        PlaceArrivalsMonitor<BoardCenter> board = new(predictions, TransitConsumer.Nearby, location.LocateAsync, (context, at, _) =>
        {
            ++boardFinds;
            return Task.FromResult(f.Found(context, at, ["near"]));
        });
        PlaceArrivalsMonitor<SuggestionContext> suggestion = new(predictions, TransitConsumer.Commute, location.LocateAsync, (context, at, _) =>
        {
            ++tripFinds;
            return Task.FromResult(f.Found(context, at, ["trip"]));
        });
        board.Configure(new NearYou());
        suggestion.Configure(f.Context);
        board.IsForeground = true;
        suggestion.IsForeground = true;
        GeoPoint refined = new(f.Point.Lat + 0.001, f.Point.Lon);

        // Act
        location.BeginRefresh();
        Task refreshes = Task.WhenAll(board.RefreshAsync(), suggestion.RefreshAsync());
        precise.SetResult(refined);
        await refreshes;

        // Assert: one lookup; both found stops from the rough fix, then again from the precise one.
        Assert.AreEqual(1, lookups);
        Assert.AreEqual(2, boardFinds);
        Assert.AreEqual(2, tripFinds);
        Assert.AreEqual(refined, board.Discovery!.Origin);
    }

    [TestMethod]
    public void Constructor_ConsumerThatIsNotAPlace_Throws()
    {
        // Arrange
        Fixture f = new();

        // Act & Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new PlaceArrivalsMonitor<BoardCenter>(
            f.Predictions, TransitConsumer.Favorites, f.Locate, (context, location, _) => Task.FromResult(f.Found(context, location))));
    }

    [TestMethod]
    public async Task IsForeground_HiddenPlaceWaiter_DoesNotCancelSharedTrackingOrOverwriteReopen()
    {
        Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        PredictionCoordinator predictions = new(f.Service, f.Clock);
        PlaceArrivalsMonitor<BoardCenter> place = new(predictions, TransitConsumer.Nearby,
            _ => Task.FromResult(new PlaceLocation(f.Point)),
            (context, location, _) => Task.FromResult(f.Found(context, location)));
        place.Configure(new NearYou());
        place.IsForeground = true;
        Task browsing = place.RefreshAsync();
        Task<PredictionSnapshot> tracking = predictions.GetAsync(["1"], TimeSpan.FromSeconds(10));

        place.IsForeground = false;
        place.IsForeground = true;
        Task reopened = place.RefreshAsync();
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await Task.WhenAll(browsing, tracking, reopened);

        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.HasCount(1, (await tracking).Arrivals);
        Assert.IsNotNull(place.Snapshot);
        Assert.IsFalse(place.IsLoading);
    }

    [TestMethod]
    public async Task RefreshAsync_RequiredDiscoveryFailure_DoesNotPublishUnvalidatedRoutesAndCanRetry()
    {
        Fixture f = new(suggestion: true);
        f.Discover = (_, _, _) => throw new TransitApiException("Fixture required pattern failed");

        await Assert.ThrowsExactlyAsync<TransitApiException>(() => f.Monitor.RefreshAsync());

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        Assert.AreEqual(0, f.Handler.Attempts);
        Assert.IsFalse(f.Monitor.IsLoading);
        f.Discover = (context, location, _) => Task.FromResult(f.Found(context, location));
        await f.Monitor.RefreshAsync();
        Assert.IsNotNull(f.Monitor.Snapshot);
        Assert.HasCount(1, f.Monitor.Discovery!.Lines!);
    }

    [TestMethod]
    public async Task RefreshAsync_ControlledLocationAndWalkingLatency_PublishesBeforeSerialBaselineStages()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);
        DateTimeOffset start = f.Clock.GetLocalNow();
        TaskCompletionSource<PlaceLocation> location = new();
        f.Locate = _ => location.Task;
        TaskCompletionSource<bool> published = new();
        f.Monitor.Updated += (_, _) =>
        {
            if (f.Monitor.Snapshot?.FetchedAt == start)
            {
                published.TrySetResult(true);
            }
        };
        async Task<DateTimeOffset> SerialBoundaryReplayAsync()
        {
            // The audited pre-04 boundary: location, walking, then the actual provider.
            await location.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            await f.Discover(f.Center, new PlaceLocation(f.Point), CancellationToken.None);
            await f.Service.GetDeparturesAsync(["1"]);
            return f.Clock.GetLocalNow();
        }
        Task<DateTimeOffset> baseline = SerialBoundaryReplayAsync();

        Task optimized = f.Monitor.RefreshAsync();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(optimized.IsCompleted);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        location.SetResult(new PlaceLocation(f.Point));
        await Task.WhenAll(baseline, optimized);

        Assert.AreEqual(TimeSpan.FromSeconds(15), await baseline - start);
        Assert.AreEqual(TimeSpan.Zero, f.Monitor.Snapshot!.FetchedAt - start);
        Assert.AreEqual(1, f.Discoveries); // Only initial discovery; baseline is an explicit boundary replay.
    }

    /// <summary>One trip: the 14 from "1" or the 30 from "alt", then the 30 again from "transfer".</summary>
    private static TripPlan TransferPlan(DateTimeOffset now)
    {
        TransitRoute fourteen = new("14", "14", "Route 14", null);
        TransitRoute thirty = new("30", "30", "Route 30", null);
        TransitDirection north = new("0", "North");
        RideLine Line(TransitRoute route, string stopId, DateTimeOffset departs) =>
            new(route, north, new TransitStop(stopId, stopId, new GeoPoint(43, -88)), null, 4, [], [new PlannedDeparture(departs, departs.AddMinutes(10), true, null)]);
        RideLeg first = new(now.AddMinutes(5), now.AddMinutes(15), [Line(fourteen, "1", now.AddMinutes(5)), Line(thirty, "alt", now.AddMinutes(6))]);
        RideLeg second = new(now.AddMinutes(20), now.AddMinutes(30), [Line(thirty, "transfer", now.AddMinutes(20))]);
        return new TripPlan([new TripItinerary([first, second], now.AddMinutes(5), now.AddMinutes(30))], []);
    }

    /// <summary>
    /// A board (Nearby) and a trip suggestion (Commute) monitor sharing one coordinator; <see cref="Monitor"/>
    /// is the one under test, already shown.
    /// </summary>
    private sealed class Fixture
    {
        private readonly bool _suggestion;

        public Fixture(bool suggestion = false)
        {
            _suggestion = suggestion;
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Predictions = new PredictionCoordinator(Service, Clock);
            Context = new SuggestionContext(
                [FavoritePlace.ForKind(PlaceKind.Home, new Place("Fixture home", 43, -88)),
                 FavoritePlace.ForKind(PlaceKind.Work, new Place("Fixture work", 43.08, -88))], null);
            Locate = _ => Task.FromResult(new PlaceLocation(Point));
            Discover = (context, location, _) => Task.FromResult(Found(context, location));
            Board = new PlaceArrivalsMonitor<BoardCenter>(Predictions, TransitConsumer.Nearby, CountedLocate, CountedDiscover);
            Suggestion = new PlaceArrivalsMonitor<SuggestionContext>(Predictions, TransitConsumer.Commute, CountedLocate, CountedDiscover,
                holds: (context, found, here) => found.Leg == context.Leg(here, Clock.GetLocalNow()),
                isOutdated: _ => Outdated);
            Configure();
            Monitor.IsForeground = true;
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)),
        };
        public TransitAppService Service { get; }
        public PredictionCoordinator Predictions { get; }
        public PlaceArrivalsMonitor<BoardCenter> Board { get; }
        public PlaceArrivalsMonitor<SuggestionContext> Suggestion { get; }
        public IPlaceArrivalsMonitor Monitor => _suggestion ? Suggestion : Board;
        public BoardCenter Center { get; set; } = new NearYou();
        public SuggestionContext Context { get; set; }
        public GeoPoint Point { get; set; } = new(43, -88);
        public string[] Ids { get; set; } = ["1"];
        public bool Outdated { get; set; }
        public int Locations { get; private set; }
        public int Discoveries { get; private set; }
        public int Walks { get; private set; }
        public Func<bool, Task<PlaceLocation>> Locate { get; set; }
        public Func<object, PlaceLocation, CancellationToken, Task<PlaceStops>> Discover { get; set; }

        public void Configure()
        {
            if (_suggestion)
            {
                Suggestion.Configure(Context);
            }
            else
            {
                Board.Configure(Center);
            }
        }

        /// <summary>Shows both monitors, the board at stop "near" and the trip at stop "trip", and refreshes them.</summary>
        public async Task ShowBothAsync()
        {
            Discover = (context, location, _) => Task.FromResult(Found(context, location, context is SuggestionContext ? ["trip"] : ["near"]));
            Board.Configure(Center);
            Suggestion.Configure(Context);
            Board.IsForeground = true;
            Suggestion.IsForeground = true;
            await Board.RefreshAsync();
            await Suggestion.RefreshAsync();
        }

        /// <summary>What discovery finds: for a trip suggestion, the leg from here; for a board, stops at its center.</summary>
        public PlaceStops Found(object context, PlaceLocation location, string[]? ids = null)
        {
            ids ??= Ids;
            CommuteLeg? leg = (context as SuggestionContext)?.Leg(location.Point, Clock.GetLocalNow());
            GeoPoint origin = leg?.Origin ?? (context as BoardCenter)?.PointFrom(location.Point) ?? Point;
            return new PlaceStops(origin, [.. ids.Select(id => new NearbyStop(
                new IndexedStop(id, "Fixture stop", 43, -88, [new StopRouteInfo("14", "14", null, "0", "North")]),
                100, WalkSeconds: 60 + GeoMath.DistanceMeters(new GeoPoint(43, -88), location.Point ?? origin)))],
                new PlaceNotice("Fixture empty", string.Empty), leg,
                leg is null ? null : ids.Select(id => new StopLine(id, "14", "0")).ToHashSet(),
                leg is null ? null : ids.ToDictionary(id => new StopLine(id, "14", "0"), _ => new TripTiming(TimeSpan.FromMinutes(20), 0)));
        }

        private Task<PlaceLocation> CountedLocate(bool quick)
        {
            ++Locations;
            return Locate(quick);
        }

        private Task<PlaceStops> CountedDiscover(object context, PlaceLocation location, CancellationToken cancellationToken)
        {
            ++Discoveries;
            ++Walks;
            return Discover(context, location, cancellationToken);
        }
    }
}
