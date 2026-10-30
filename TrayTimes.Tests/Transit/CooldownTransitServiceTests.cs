using System.Net;
using System.Net.Http.Headers;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class CooldownTransitServiceTests
{
    [TestMethod]
    public async Task GetDeparturesAsync_AutomaticRequestAfterFailure_IsSuppressedWithoutAnHttpAttempt()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Service.GetDeparturesAsync(["1"]));

        // Act
        Exception? refusal = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);

        // Assert
        Assert.IsInstanceOfType<TransitCooldownException>(refusal);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.EndpointCalls);
        TransitRequestObservation suppressed = f.Inner.Diagnostics.Snapshot.Single(o => o.Phase == TransitRequestPhase.Suppressed);
        Assert.AreEqual(TransitRequestOutcome.Suppressed, suppressed.Outcome);
        Assert.AreEqual(TransitConsumer.Favorites, suppressed.Consumer);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_ConsecutiveFailures_DoubleTheHoldOffUpToTheCap()
    {
        // Arrange
        Fixture f = new(new CooldownPolicy { FailureInitialDelay = TimeSpan.FromSeconds(15), FailureMaximumDelay = TimeSpan.FromSeconds(40) });
        f.Fail();
        await f.TryCallAsync();
        int[] expectedHoldSeconds = [15, 30, 40, 40];

        foreach (int hold in expectedHoldSeconds)
        {
            // Act & Assert
            f.Clock.Advance(TimeSpan.FromSeconds(hold - 1));
            Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(), $"still held at {hold - 1}s");
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            int before = f.Handler.Attempts;
            Assert.IsInstanceOfType<HttpRequestException>(await f.TryCallAsync(), $"retried at {hold}s");
            Assert.AreEqual(before + 1, f.Handler.Attempts);
        }
    }

    [TestMethod]
    public async Task GetDeparturesAsync_SuccessAfterFailure_ClearsTheHoldOff()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await f.TryCallAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        f.Succeed();
        Assert.IsNull(await f.TryCallAsync());

        // Act
        f.Fail();
        await f.TryCallAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        Exception? retry = await f.TryCallAsync();

        // Assert: the hold-off restarted at 15 s instead of doubling to 30 s.
        Assert.IsNotInstanceOfType<TransitCooldownException>(retry);
        Assert.AreEqual(4, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_ParallelRequestsFailingTogether_CountAsOneOutage()
    {
        // Arrange
        Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> gate = new();
        f.Handler.RespondAsync = (_, _) => gate.Task;
        Task<Exception?> first = f.TryCallAsync();
        Task<Exception?> second = f.TryCallAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        gate.SetResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));
        await Task.WhenAll(first, second);
        f.Succeed();
        f.Clock.Advance(TimeSpan.FromSeconds(15));

        // Assert: one 15 s hold-off, not 15 s then 30 s.
        Assert.IsNull(await f.TryCallAsync());
    }

    [TestMethod]
    public async Task GetDeparturesAsync_FailureStartedBeforeALaterSuccess_IsIgnored()
    {
        // Arrange
        Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> slow = new();
        f.Handler.RespondAsync = (_, _) => slow.Task;
        Task<Exception?> slowCall = f.TryCallAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Succeed();
        Assert.IsNull(await f.TryCallAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        slow.SetResult(RequestTestHandler.Response("late", HttpStatusCode.ServiceUnavailable));
        await slowCall;

        // Assert
        Assert.IsNull(await f.TryCallAsync());
    }

    [TestMethod]
    public async Task GetDeparturesAsync_ManualRetryAfterFailure_WaitsForMinimumSpacingThenBypassesHoldOff()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened);

        // Act & Assert: rapid hide/show cannot storm the service.
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.FlyoutOpened));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh));
        Assert.AreEqual(1, f.Handler.Attempts);
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Succeed();
        Assert.IsNull(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh));
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_TrackingRequestDuringOrdinaryHoldOff_IsStillSent()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);
        f.Succeed();

        // Act
        Exception? tracking = await f.TryCallAsync(TransitConsumer.Tracking, TransitTrigger.Timer);

        // Assert
        Assert.IsNull(tracking);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_TooManyRequestsWithRetryAfter_HoldsEveryoneUntilTheWindowEnds()
    {
        // Arrange
        Fixture f = new();
        f.RateLimit(TimeSpan.FromSeconds(120));
        Exception? limited = await f.TryCallAsync(TransitConsumer.Tracking, TransitTrigger.Timer);
        Assert.AreEqual(TimeSpan.FromSeconds(120), ((TransitRateLimitException)limited!).RetryAfter);
        f.Succeed();

        // Act & Assert
        f.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Tracking, TransitTrigger.Timer));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer));
        Assert.AreEqual(1, f.Handler.Attempts);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(await f.TryCallAsync(TransitConsumer.Tracking, TransitTrigger.Timer));
    }

    [TestMethod]
    public async Task GetDeparturesAsync_TooManyRequestsWithoutRetryAfter_UsesTheDefaultWait()
    {
        // Arrange
        Fixture f = new();
        f.RateLimit(null);
        await f.TryCallAsync();
        f.Succeed();

        // Act & Assert
        f.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.IsInstanceOfType<TransitCooldownException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh));
    }

    [TestMethod]
    public async Task GetDeparturesAsync_ExcessiveRetryAfter_IsBounded()
    {
        // Arrange
        Fixture f = new();
        f.RateLimit(TimeSpan.FromDays(1));
        await f.TryCallAsync();
        f.Succeed();

        // Act
        f.Clock.Advance(TimeSpan.FromHours(1));

        // Assert
        Assert.IsNull(await f.TryCallAsync());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task GetDeparturesAsync_RejectedKey_PausesAutomaticAndTrackingRequestsButAllowsOneManualRetry(HttpStatusCode status)
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("denied", status));
        TransitApiException rejected = await Assert.ThrowsExactlyAsync<TransitApiException>(() => f.Service.GetDeparturesAsync(["1"]));
        Assert.AreEqual(TransitApiFailureKind.Authentication, rejected.Kind);

        // Act
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Exception? automatic = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);
        Exception? tracking = await f.TryCallAsync(TransitConsumer.Tracking, TransitTrigger.Timer);
        Exception? manual = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh);

        // Assert
        Assert.AreEqual(rejected.Message, ((TransitApiException)automatic!).Message);
        Assert.AreEqual(rejected.Message, ((TransitApiException)tracking!).Message);
        Assert.IsInstanceOfType<TransitApiException>(manual);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_RejectedKeyThenSuccessfulManualRetry_ResumesAutomaticRequests()
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("denied", HttpStatusCode.Forbidden));
        await f.TryCallAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<TransitApiException>(await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer));
        f.Succeed();

        // Act
        Exception? manual = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh);
        Exception? automatic = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);

        // Assert
        Assert.IsNull(manual);
        Assert.IsNull(automatic);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task GetDeparturesAsync_QueryLevelError_DoesNotStartAHoldOff(HttpStatusCode status)
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"error":"Unknown stop"}""", status));
        await Assert.ThrowsExactlyAsync<TransitApiException>(() => f.Service.GetDeparturesAsync(["1"]));
        f.Succeed();

        // Act
        Exception? next = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);

        // Assert
        Assert.IsNull(next);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_CallerCancellation_IsNotAFailure()
    {
        // Arrange
        Fixture f = new();
        using CancellationTokenSource cancellation = new();
        f.Handler.RespondAsync = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return RequestTestHandler.Response(RequestTestHandler.EmptyBody);
        };
        Task<DepartureBoard> pending = f.Service.GetDeparturesAsync(["1"], cancellationToken: cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        f.Succeed();

        // Assert
        Assert.IsNull(await f.TryCallAsync());
    }

    [TestMethod]
    public async Task GetDeparturesAsync_Timeout_CountsAsAFailure()
    {
        // Arrange
        Fixture f = new();
        f.Handler.RespondAsync = (_, _) => throw new TaskCanceledException("timeout");
        await Assert.ThrowsAsync<TaskCanceledException>(() => f.Service.GetDeparturesAsync(["1"]));

        // Act
        Exception? next = await f.TryCallAsync();

        // Assert
        Assert.IsInstanceOfType<TransitCooldownException>(next);
        Assert.AreEqual(1, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task EveryEndpoint_Suppressed_NeverReturnsEmptyData()
    {
        // Arrange
        Fixture f = new();
        f.Fail();
        await f.TryCallAsync();
        GeoPoint here = new(43, -88);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.GetDeparturesAsync(["1"]));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.GetNearbyLinesAsync(here, 500));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.PlanTripAsync(here, here));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.SearchStopsAsync(here, "main"));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.GetRoutesNearAsync(here));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.GetRouteDetailsAsync("14"));
        await Assert.ThrowsExactlyAsync<TransitCooldownException>(() => f.Service.GetVehiclesAsync("14"));
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.EndpointCalls);
    }

    [TestMethod]
    public async Task GetDeparturesAsync_BudgetRefusesAutomaticRequest_SendsNothingAndStartsNoHoldOff()
    {
        // Arrange: today's share of the month is used.
        Fixture f = new(usage: new TransitUsage(60, 60));
        f.Succeed();

        // Act
        Exception? automatic = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);
        Exception? manual = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.ManualRefresh);

        // Assert
        Assert.AreEqual(TransitApiFailureKind.Budget, ((TransitApiException)automatic!).Kind);
        Assert.IsNull(manual);
        Assert.AreEqual(1, f.Handler.Attempts);
        Assert.AreEqual(1, f.Inner.Diagnostics.Snapshot.Count(o => o.Phase == TransitRequestPhase.Suppressed));
    }

    [TestMethod]
    public async Task GetRoutesNearAsync_TwoPhysicalRequests_TakeTwoBudgetSlots()
    {
        // Arrange: three of five minute slots are left after this.
        Fixture f = new(usage: new TransitUsage(0, 0));
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("""{"networks":[]}"""));
        using (TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection))
        {
            await f.Service.GetRoutesNearAsync(new GeoPoint(43, -88));
        }

        // Act: automatic requests may use only three of the five slots.
        f.Succeed();
        Exception? first = await f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);
        Task<Exception?> second = f.TryCallAsync(TransitConsumer.Favorites, TransitTrigger.Timer);

        // Assert
        Assert.IsNull(first);
        Assert.IsFalse(second.IsCompleted);
        f.Clock.Advance(TransitBudget.Window);
        Assert.IsNull(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task GetDeparturesAsync_NoKeyConfigured_TakesNoBudgetSlot()
    {
        // Arrange
        RequestTestHandler handler = new();
        TransitBudget budget = new(() => new TransitUsage(0, 1500));
        CooldownTransitService service = new(new TransitAppService(null, http: new HttpClient(handler)), budget: budget);

        // Act
        TransitApiException failure = await Assert.ThrowsExactlyAsync<TransitApiException>(() => service.GetDeparturesAsync(["1"]));

        // Assert: the missing key is reported, not an exhausted budget.
        Assert.AreEqual(TransitApiFailureKind.Authentication, failure.Kind);
    }

    [TestMethod]
    public void Properties_ForwardToTheWrappedService()
    {
        // Arrange
        Fixture f = new();

        // Assert
        Assert.AreEqual(f.Inner.ProviderName, f.Service.ProviderName);
        Assert.AreEqual(f.Inner.IsConfigured, f.Service.IsConfigured);
        Assert.AreEqual(f.Inner.MaxStopsPerRequest, f.Service.MaxStopsPerRequest);
        Assert.AreSame(f.Inner.Diagnostics, f.Service.Diagnostics);
    }

    private sealed class Fixture
    {
        public Fixture(CooldownPolicy? policy = null, TransitUsage? usage = null)
        {
            Inner = Handler.CreateService(new TransitRequestDiagnostics(Clock));
            Inner.EndpointCalled += (_, _) => ++EndpointCalls;
            TransitBudget? budget = usage is { } fixedUsage ? new TransitBudget(() => fixedUsage, timeProvider: Clock) : null;
            Service = new CooldownTransitService(Inner, Clock, policy, budget);
        }

        public RequestTestClock Clock { get; } = new();

        public RequestTestHandler Handler { get; } = new();

        public TransitAppService Inner { get; }

        public CooldownTransitService Service { get; }

        public int EndpointCalls { get; private set; }

        public void Fail() =>
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));

        public void Succeed() =>
            Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(RequestTestHandler.EmptyBody));

        public void RateLimit(TimeSpan? retryAfter) =>
            Handler.RespondAsync = (_, _) =>
            {
                HttpResponseMessage response = RequestTestHandler.Response("slow down", HttpStatusCode.TooManyRequests);
                if (retryAfter is { } wait)
                {
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
                }

                return Task.FromResult(response);
            };

        public async Task<Exception?> TryCallAsync(TransitConsumer consumer = TransitConsumer.Unspecified, TransitTrigger trigger = TransitTrigger.Unspecified)
        {
            using IDisposable scope = TransitRequestDiagnostics.BeginScope(consumer, trigger);
            try
            {
                await Service.GetDeparturesAsync(["1"]);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
