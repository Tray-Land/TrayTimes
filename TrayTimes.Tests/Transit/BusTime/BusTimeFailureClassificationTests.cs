using System.Net;
using System.Net.Http.Headers;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit.BusTime;

[TestClass]
public sealed class BusTimeFailureClassificationTests
{
    [TestMethod]
    public async Task GetRoutesAsync_TooManyRequestsWithRetryAfterSeconds_ThrowsRateLimitWithTheWait()
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) =>
        {
            HttpResponseMessage response = RequestTestHandler.Response("slow down", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        };

        // Act
        TransitRateLimitException ex = await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.AreEqual(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(45), ex.RetryAfter);
    }

    [TestMethod]
    public async Task GetRoutesAsync_TooManyRequestsWithRetryAfterDate_ThrowsRateLimitWithTheRemainingWait()
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) =>
        {
            HttpResponseMessage response = RequestTestHandler.Response("slow down", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(90));
            return Task.FromResult(response);
        };

        // Act
        TransitRateLimitException ex = await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.IsNotNull(ex.RetryAfter);
        Assert.IsTrue(ex.RetryAfter > TimeSpan.FromSeconds(60) && ex.RetryAfter <= TimeSpan.FromSeconds(91), $"{ex.RetryAfter}");
    }

    [TestMethod]
    public async Task GetRoutesAsync_TooManyRequestsWithoutRetryAfter_ThrowsRateLimitWithoutAWait()
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("slow down", HttpStatusCode.TooManyRequests));

        // Act
        TransitRateLimitException ex = await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.IsNull(ex.RetryAfter);
    }

    [TestMethod]
    public async Task GetRoutesAsync_ServiceUnavailableWithRetryAfter_IsTreatedAsARateLimit()
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) =>
        {
            HttpResponseMessage response = RequestTestHandler.Response("busy", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        };

        // Act
        TransitRateLimitException ex = await Assert.ThrowsExactlyAsync<TransitRateLimitException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [TestMethod]
    public async Task GetRoutesAsync_ServiceUnavailableWithoutRetryAfter_IsAnOrdinaryHttpFailure()
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));

        // Act
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [TestMethod]
    [DataRow("Invalid API access key supplied", TransitApiFailureKind.Authentication)]
    [DataRow("Maximum number of requests exceeded", TransitApiFailureKind.RateLimit)]
    [DataRow("Unknown route", TransitApiFailureKind.Other)]
    public async Task GetRoutesAsync_BodyError_IsClassifiedByMessage(string message, TransitApiFailureKind expected)
    {
        // Arrange
        RequestTestHandler handler = new();
        handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(
            "{\"bustime-response\":{\"error\":[{\"msg\":\"" + message + "\"}]}}"));

        // Act
        TransitApiException ex = await Assert.ThrowsExactlyAsync<TransitApiException>(() => handler.CreateService().GetRoutesAsync());

        // Assert
        Assert.AreEqual(expected, ex.Kind);
    }
}
