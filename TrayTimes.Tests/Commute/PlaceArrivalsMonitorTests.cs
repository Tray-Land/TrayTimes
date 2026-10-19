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
    [DataRow(FlyoutMode.Nearby)]
    [DataRow(FlyoutMode.Commute)]
    public async Task RefreshAsync_StableLocation_SkipsDiscoveryAndStartsPredictionsBeforeSlowLocation(FlyoutMode mode)
    {
        Fixture f = new(mode);
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
    [DataRow(FlyoutMode.Nearby)]
    [DataRow(FlyoutMode.Commute)]
    public async Task IsForeground_QuickReopen_PreservesImmediateRowsAndOriginalTimestampWithoutHttp(FlyoutMode mode)
    {
        Fixture f = new(mode);
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
        Fixture f = new(FlyoutMode.Commute);
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
    public async Task Configure_ResetExplicitDestinationOnReopen_DoesNotDisplayPreviousTripAsCurrent()
    {
        Fixture f = new(FlyoutMode.Commute);
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
    public async Task Configure_ModeChange_DoesNotPresentNearbyAsValidatedCommute()
    {
        Fixture f = new();
        await f.Monitor.RefreshAsync();
        f.Context = f.Context with { Mode = FlyoutMode.Commute };

        f.Configure();

        Assert.IsNull(f.Monitor.Discovery);
        Assert.IsNull(f.Monitor.Snapshot);
        await f.Monitor.RefreshAsync();
        Assert.IsNotNull(f.Monitor.Discovery!.Leg);
        Assert.HasCount(1, f.Monitor.Discovery.Lines!);
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
        walking.SetResult(f.Found(f.Context, new PlaceLocation(f.Point)));
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
        f.Context = f.Context with { Mode = FlyoutMode.Commute };

        f.Configure();
        walking.SetResult(f.Found(new PlaceContext(FlyoutMode.Nearby, [], null), new PlaceLocation(f.Point)));
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

        f.Context = f.Context with { Mode = FlyoutMode.Commute };
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
        Fixture f = new(FlyoutMode.Commute);
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
    [DataRow(FlyoutMode.Nearby)]
    [DataRow(FlyoutMode.Commute)]
    public async Task RefreshAsync_WarmOpenMinuteAndReopen_CountsZeroRediscoveryAndOneDeparturesCall(FlyoutMode mode)
    {
        Fixture f = new(mode);
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
        Fixture f = new(FlyoutMode.Commute);
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
    public async Task IsForeground_HiddenPlaceWaiter_DoesNotCancelSharedTrackingOrOverwriteReopen()
    {
        Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        PredictionCoordinator predictions = new(f.Service, f.Clock);
        PlaceArrivalsMonitor place = new(predictions,
            _ => Task.FromResult(new PlaceLocation(f.Point)),
            (context, location, _) => Task.FromResult(f.Found(context, location)), f.Clock);
        place.Configure(f.Context);
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
        Fixture f = new(FlyoutMode.Commute);
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
            await f.Discover(f.Context, new PlaceLocation(f.Point), CancellationToken.None);
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

    private sealed class Fixture
    {
        public Fixture(FlyoutMode mode = FlyoutMode.Nearby)
        {
            Service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Context = new PlaceContext(mode,
                [FavoritePlace.ForKind(PlaceKind.Home, new Place("Fixture home", 43, -88)),
                 FavoritePlace.ForKind(PlaceKind.Work, new Place("Fixture work", 43.08, -88))], null);
            Locate = _ => Task.FromResult(new PlaceLocation(Point));
            Discover = (context, location, _) => Task.FromResult(Found(context, location));
            Monitor = new PlaceArrivalsMonitor(new PredictionCoordinator(Service, Clock), quick =>
            {
                ++Locations;
                return Locate(quick);
            }, (context, location, token) =>
            {
                ++Discoveries;
                ++Walks;
                return Discover(context, location, token);
            }, Clock);
            Configure();
            Monitor.IsForeground = true;
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new()
        {
            RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)),
        };
        public TransitAppService Service { get; }
        public PlaceArrivalsMonitor Monitor { get; }
        public PlaceContext Context { get; set; }
        public GeoPoint Point { get; set; } = new(43, -88);
        public string[] Ids { get; set; } = ["1"];
        public int Locations { get; private set; }
        public int Discoveries { get; private set; }
        public int Walks { get; private set; }
        public Func<bool, Task<PlaceLocation>> Locate { get; set; }
        public Func<PlaceContext, PlaceLocation, CancellationToken, Task<PlaceStops>> Discover { get; set; }

        public void Configure() => Monitor.Configure(Context);

        public PlaceStops Found(PlaceContext context, PlaceLocation location)
        {
            CommuteLeg? leg = context.Mode == FlyoutMode.Commute
                ? CommutePlanner.Plan(context.Places, location.Point, Clock.GetLocalNow(),
                    context.Places.FirstOrDefault(p => p.Id == context.DestinationId))
                : null;
            GeoPoint origin = leg?.Origin ?? location.Point ?? Point;
            return new PlaceStops(origin, [.. Ids.Select(id => new NearbyStop(
                new IndexedStop(id, "Fixture stop", 43, -88, [new StopRouteInfo("14", "14", null, "0", "North")]),
                100, WalkSeconds: 60 + GeoMath.DistanceMeters(new GeoPoint(43, -88), location.Point ?? origin)))],
                new PlaceNotice("Fixture empty", string.Empty), leg,
                leg is null ? null : Ids.Select(id => new StopLine(id, "14", "0")).ToHashSet(),
                leg is null ? null : Ids.ToDictionary(id => new StopLine(id, "14", "0"), _ => new TripTiming(TimeSpan.FromMinutes(20), 0)));
        }
    }
}
