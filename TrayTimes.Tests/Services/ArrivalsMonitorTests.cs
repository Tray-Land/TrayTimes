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
public sealed class ArrivalsMonitorTests
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Loaded_FirstFeedAndReloadWithStaleLiveSnapshot_AddsScheduleWithoutHttpOrChangingFreshness(bool empty)
    {
        Fixture f = new(empty);
        await f.OpenAsync();
        DateTimeOffset? fetched = f.Monitor.Latest.FetchedAt;
        f.Clock.Advance(TimeSpan.FromSeconds(35));

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();

        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.IsNull(f.Monitor.Latest.Error);
        Assert.IsFalse(f.Monitor.IsLoading);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(empty ? 2 : 1, f.Monitor.Latest.Arrivals.Count(a => !a.IsRealTime));

        // Replay the platform disk-reload boundary with a newly parsed copy of the same feed.
        f.Schedule.Timetable = null;
        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();
        Assert.HasCount(2, f.Monitor.Latest.Arrivals); // Merge raw data, never previously merged rows.
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);

        await f.Monitor.RefreshAsync(TransitTrigger.Timer);
        Assert.AreEqual(2, f.Handler.Attempts); // Local enrichment did not extend the cache age.
        await f.Monitor.RefreshAsync(TransitTrigger.ManualRefresh);
        Assert.AreEqual(3, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Loaded_FailedLiveRefreshWithLastGoodSnapshot_PreservesErrorTimestampAndRawData(bool empty)
    {
        Fixture f = new(empty);
        await f.OpenAsync();
        DateTimeOffset? fetched = f.Monitor.Latest.FetchedAt;
        f.Clock.Advance(PredictionCoordinator.BrowsingInterval);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("unavailable", HttpStatusCode.ServiceUnavailable));
        await f.Monitor.RefreshAsync(TransitTrigger.Timer);
        string? error = f.Monitor.Latest.Error;
        Assert.IsNotNull(error);

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();

        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.AreEqual(error, f.Monitor.Latest.Error);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(empty ? 2 : 1, f.Monitor.Latest.Arrivals.Count(a => !a.IsRealTime));
    }

    [TestMethod]
    public async Task Loaded_WhileTimedRefreshIsPending_EnrichesImmediatelyWithoutResettingLoadingOrReplacingRequest()
    {
        Fixture f = new();
        await f.OpenAsync();
        DateTimeOffset? fetched = f.Monitor.Latest.FetchedAt;
        f.Clock.Advance(PredictionCoordinator.BrowsingInterval);
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task refresh = f.Monitor.RefreshAsync(TransitTrigger.Timer);
        Assert.IsTrue(f.Monitor.IsLoading);

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();

        Assert.IsTrue(f.Monitor.IsLoading);
        Assert.IsFalse(refresh.IsCompleted);
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(2, f.Handler.Attempts);
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
        await refresh;
        Assert.IsFalse(f.Monitor.IsLoading);
        Assert.AreEqual(f.Clock.GetLocalNow(), f.Monitor.Latest.FetchedAt);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
    }

    [TestMethod]
    public async Task Loaded_NoSuccessfulSnapshot_RetriesNormallyWithoutConvertingFailureToEmptySuccess()
    {
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("unavailable", HttpStatusCode.ServiceUnavailable));
        await f.OpenAsync();
        Assert.IsNull(f.Monitor.Latest.FetchedAt);
        Assert.IsNotNull(f.Monitor.Latest.Error);
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();
        f.Schedule.RaiseLoaded();

        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.IsTrue(f.Monitor.IsLoading);
        Assert.IsNotNull(f.Monitor.Latest.Error);
        Task completed = f.WaitAsync(() => pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody)));
        await completed;
        Assert.IsNotNull(f.Monitor.Latest.FetchedAt);
        Assert.IsNull(f.Monitor.Latest.Error);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
    }

    [TestMethod]
    public async Task Loaded_StopChangeDuringPendingFetch_DoesNotRemergeIncompatibleSnapshotOrRepeatRequest()
    {
        Fixture f = new();
        await f.OpenAsync();
        SettingsService.Stops = [SettingsService.Stops[0] with { StopId = "2" }];
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);
        Task changed = f.Monitor.RefreshAsync(TransitTrigger.StopsChanged);

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();

        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.IsTrue(f.Monitor.IsLoading);
        Assert.IsTrue(f.Monitor.Latest.Arrivals.All(a => a.IsRealTime)); // Old snapshot was not locally enriched.
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveBody.Replace("\"stpid\":\"1\"", "\"stpid\":\"2\"", StringComparison.Ordinal)));
        await changed;
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.IsTrue(f.Monitor.Latest.Arrivals.All(a => a.StopId == "2"));
        f.Schedule.RaiseLoaded();
        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.IsTrue(f.Monitor.Latest.Arrivals.All(a => a.StopId == "2"));
    }

    [TestMethod]
    public async Task Loaded_HiddenThenFreshReopen_EnrichesOnReopenWithoutHttp()
    {
        Fixture f = new();
        await f.OpenAsync();
        DateTimeOffset? fetched = f.Monitor.Latest.FetchedAt;
        f.Monitor.IsForeground = false;
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        int updates = 0;
        f.Monitor.Updated += (_, _) => ++updates;

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();
        Assert.AreEqual(0, updates);
        Assert.AreEqual(1, f.Handler.Attempts);
        f.Monitor.IsForeground = true;

        Assert.AreEqual(1, updates);
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task Clear_ThenTimetableLoaded_DoesNotPublishOrFetchWhileHidden()
    {
        Fixture f = new();
        await f.OpenAsync();
        f.Monitor.Clear();

        f.Schedule.Timetable = ScheduleTestSupport.Load();
        f.Schedule.RaiseLoaded();

        Assert.AreEqual(ArrivalsSnapshot.Empty, f.Monitor.Latest);
        Assert.AreEqual(1, f.Handler.Attempts);
        await f.OpenAsync(); // Normal refresh may reuse the app-lifetime coordinator's raw data.
        Assert.HasCount(2, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    private sealed class Fixture
    {
        public Fixture(bool empty = false)
        {
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(empty ? RequestTestHandler.EmptyBody : RequestTestHandler.LiveBody));
            BusTimeTransitService service = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            service.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
            Schedule = new ScheduleService(Clock);
            Monitor = new ArrivalsMonitor(service, Schedule, new DispatcherQueue(), Clock);
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public ScheduleService Schedule { get; }
        public ArrivalsMonitor Monitor { get; }

        public Task OpenAsync() => WaitAsync(() => Monitor.IsForeground = true);

        public async Task WaitAsync(Action start)
        {
            TaskCompletionSource<bool> completed = new();
            void Updated(object? sender, EventArgs e)
            {
                if (!Monitor.IsLoading)
                {
                    completed.TrySetResult(true);
                }
            }
            Monitor.Updated += Updated;
            try
            {
                start();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Monitor.Updated -= Updated;
            }
        }
    }
}
