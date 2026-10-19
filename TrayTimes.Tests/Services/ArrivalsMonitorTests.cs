using System.Net;
using Microsoft.UI.Dispatching;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class ArrivalsMonitorTests
{
    [TestInitialize]
    public void Initialize()
    {
        SettingsService.Reset();
        SettingsService.Stops = [new SavedStop("1", "Fixture stop", 0, 0, "14", "14", null, "0", "North", false)];
        ApiUsageService.StartOver();
    }

    [TestCleanup]
    public void Cleanup() => SettingsService.Reset();

    [TestMethod]
    public async Task IsForeground_FirstOpen_FetchesEveryFavoriteInOneRequest()
    {
        // Arrange
        SettingsService.Stops = [.. SettingsService.Stops, SettingsService.Stops[0] with { StopId = "2" }, SettingsService.Stops[0] with { StopId = "3" }];
        Fixture f = new();
        f.Handler.RespondAsync = (request, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(RequestTestHandler.StopIds(request))));

        // Act
        await f.OpenAsync();

        // Assert
        Assert.AreEqual(1, f.Handler.Attempts);
        CollectionAssert.AreEquivalent(new[] { "1", "2", "3" }, RequestTestHandler.StopIds(f.Handler.Requests.Single()));
        Assert.HasCount(3, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(1, ApiUsageService.GetSummary().Total.Today);
    }

    [TestMethod]
    [DataRow(59, 1)]
    [DataRow(60, 2)]
    public async Task IsForeground_Reopen_ReusesDataYoungerThanTheBrowsingInterval(int secondsLater, int expectedAttempts)
    {
        // Arrange
        Fixture f = new();
        await f.OpenAsync();
        f.Monitor.IsForeground = false;
        f.Clock.Advance(TimeSpan.FromSeconds(secondsLater));

        // Act
        if (expectedAttempts == 1)
        {
            f.Monitor.IsForeground = true;
        }
        else
        {
            await f.OpenAsync();
        }

        // Assert
        Assert.AreEqual(expectedAttempts, f.Handler.Attempts);
        Assert.HasCount(1, f.Monitor.Latest.Arrivals);
    }

    [TestMethod]
    public async Task RefreshAsync_FailureAfterGoodData_KeepsLastGoodArrivalsAndTimestampWithError()
    {
        // Arrange
        Fixture f = new();
        await f.OpenAsync();
        DateTimeOffset? fetched = f.Monitor.Latest.FetchedAt;
        f.Clock.Advance(PredictionCoordinator.DefaultBrowsingInterval);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("unavailable", HttpStatusCode.ServiceUnavailable));

        // Act
        await f.Monitor.RefreshAsync(TransitTrigger.Timer);

        // Assert
        Assert.AreEqual(fetched, f.Monitor.Latest.FetchedAt);
        Assert.HasCount(1, f.Monitor.Latest.Arrivals);
        Assert.AreEqual("Can't reach Transit right now. Retrying shortly.", f.Monitor.Latest.Error);
        Assert.IsFalse(f.Monitor.IsLoading);
    }

    [TestMethod]
    public async Task RefreshAsync_RejectedQuery_ShowsTheProvidersMessage()
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("{}", HttpStatusCode.Unauthorized));

        // Act
        await f.OpenAsync();

        // Assert
        Assert.IsNull(f.Monitor.Latest.FetchedAt);
        Assert.Contains("rejected the API key", f.Monitor.Latest.Error!);
    }

    [TestMethod]
    public async Task RefreshAsync_NoKey_ExplainsWithoutHttp()
    {
        // Arrange
        Fixture f = new(new TransitAppService(null, http: new HttpClient(new RequestTestHandler())));

        // Act
        await f.OpenAsync();

        // Assert
        Assert.Contains("TransitApiKey", f.Monitor.Latest.Error!);
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task RefreshAsync_StopChangeDuringPendingFetch_PublishesOnlyTheNewStops()
    {
        // Arrange
        Fixture f = new();
        await f.OpenAsync();
        SettingsService.Stops = [SettingsService.Stops[0] with { StopId = "2" }];
        TaskCompletionSource<HttpResponseMessage> pending = new();
        f.Handler.RespondAsync = (_, token) => pending.Task.WaitAsync(token);

        // Act
        Task changed = f.Monitor.RefreshAsync(TransitTrigger.StopsChanged);
        pending.SetResult(RequestTestHandler.Response(RequestTestHandler.LiveFor(["2"])));
        await changed;

        // Assert
        Assert.AreEqual(2, f.Handler.Attempts);
        Assert.IsTrue(f.Monitor.Latest.Arrivals.All(a => a.StopId == "2"));
    }

    [TestMethod]
    public async Task Clear_ThenReopen_ReusesTheAppLifetimeCoordinatorsData()
    {
        // Arrange
        Fixture f = new();
        await f.OpenAsync();

        // Act
        f.Monitor.Clear();
        Assert.AreEqual(ArrivalsSnapshot.Empty, f.Monitor.Latest);
        await f.Monitor.RefreshAsync();
        Assert.AreEqual(1, f.Handler.Attempts); // Hidden: no work.
        await f.OpenAsync();

        // Assert
        Assert.HasCount(1, f.Monitor.Latest.Arrivals);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    [DataRow(0, 60)]
    [DataRow(40, 180)]
    [DataRow(60, 300)]
    public void RefreshInterval_CallBudget_SetsTheTimer(int usedToday, int expectedSeconds)
    {
        // Arrange: today's share of 1,500 calls on October 6 is 57.
        Fixture f = new(usage: new TransitUsage(usedToday, usedToday));

        // Act
        f.Monitor.IsForeground = true;

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), f.Monitor.RefreshInterval);
        Assert.AreEqual(f.Monitor.RefreshInterval, f.Dispatcher.Timers.Single().Interval);
    }

    [TestMethod]
    public async Task RefreshAsync_TimerAfterTodaysShareIsUsed_IsRefusedWithAnExplanationButManualStillWorks()
    {
        // Arrange
        Fixture f = new(usage: new TransitUsage(60, 60));
        await f.OpenAsync();
        Assert.AreEqual(1, f.Handler.Attempts); // Opening the flyout is something the rider did.
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        await f.Monitor.RefreshAsync(TransitTrigger.Timer);
        string? timerError = f.Monitor.Latest.Error;
        await f.Monitor.RefreshAsync(TransitTrigger.ManualRefresh);

        // Assert
        Assert.Contains("paused for today", timerError!);
        Assert.IsNull(f.Monitor.Latest.Error);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    private sealed class Fixture
    {
        public Fixture(TransitAppService? service = null, TransitUsage? usage = null)
        {
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.LiveBody));
            TransitAppService client = service ?? Handler.CreateService(new TransitRequestDiagnostics(Clock));
            client.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
            TransitBudget? budget = usage is { } fixedUsage ? new TransitBudget(() => fixedUsage, timeProvider: Clock) : null;
            ITransitService transit = new MetadataTransitService(new CooldownTransitService(client, Clock, budget: budget), Clock);
            PredictionCoordinator predictions = new(transit, Clock, budget is null ? null : () => budget.ReuseAge);
            Monitor = new ArrivalsMonitor(transit, Dispatcher, Clock, predictions, budget);
        }

        public RequestTestClock Clock { get; } = new();
        public RequestTestHandler Handler { get; } = new();
        public DispatcherQueue Dispatcher { get; } = new();
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
