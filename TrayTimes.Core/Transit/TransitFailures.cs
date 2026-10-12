using System.Net;

namespace TrayTimes.Transit;

/// <summary>The provider asked the app to slow down (HTTP 429, or 503 with Retry-After).</summary>
public sealed class TransitRateLimitException : HttpRequestException
{
    public TransitRateLimitException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base($"The transit service is limiting requests (HTTP {(int)statusCode}).", null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The provider's Retry-After value, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A request was not sent because a recent failure put requests on hold. It derives from
/// <see cref="HttpRequestException"/> so callers show their normal "can't reach" status instead of data.
/// </summary>
public sealed class TransitCooldownException(TimeSpan remaining)
    : HttpRequestException("Transit requests are paused after a recent failure.")
{
    public TimeSpan Remaining { get; } = remaining;
}
