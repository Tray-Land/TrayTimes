using System.Net;

namespace TrayTimes.Transit;

/// <summary>Tunable hold-off rules for <see cref="CooldownTransitService"/>.</summary>
public sealed record CooldownPolicy
{
    /// <summary>Hold-off after the first ordinary failure; it doubles per consecutive failure.</summary>
    public TimeSpan FailureInitialDelay { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan FailureMaximumDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The least time between a failure and a user-initiated retry, so rapid hide/show can't storm.</summary>
    public TimeSpan ManualMinimumSpacing { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wait after an HTTP 429 that carries no Retry-After.</summary>
    public TimeSpan RateLimitDefaultWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait after the service reports an exhausted request quota in its response body.</summary>
    public TimeSpan QuotaExhaustedWait { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RateLimitMaximumWait { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long automatic requests stay off after a rejected key; one manual retry is still allowed.</summary>
    public TimeSpan PermanentErrorSuppression { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Stops requests to a failing or rate-limiting service. Every physical request goes through the
/// wrapped service; while a hold is active a request is refused here with an exception, never
/// with empty data.
/// <list type="bullet">
/// <item>Rate limit (429, Retry-After, exhausted quota): nothing is sent until the provider's window ends.</item>
/// <item>Rejected key: automatic and tracking requests pause for a long time; a user-initiated retry may try again.</item>
/// <item>Ordinary failure (timeout, transport, HTTP error): automatic requests wait out a doubling hold-off;
/// user-initiated requests may retry after a short spacing; tracking is never held by it.</item>
/// <item>Success clears the ordinary hold-off and a rejected-key pause.</item>
/// </list>
/// </summary>
public sealed class CooldownTransitService : ITransitService
{
    private readonly ITransitService _inner;
    private readonly TimeProvider _time;
    private readonly CooldownPolicy _policy;
    private readonly FailureBackoff _backoff;
    private readonly Lock _gate = new();
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _rejectedUntil = DateTimeOffset.MinValue;
    private string _rejectedMessage = string.Empty;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;

    public CooldownTransitService(ITransitService inner, TimeProvider? time = null, CooldownPolicy? policy = null)
    {
        _inner = inner;
        _time = time ?? TimeProvider.System;
        _policy = policy ?? new CooldownPolicy();
        _backoff = new FailureBackoff(_time, _policy.FailureInitialDelay, _policy.FailureMaximumDelay);
    }

    private enum Priority { Automatic, Manual, Urgent }

    public string AgencyName => _inner.AgencyName;

    public bool IsConfigured => _inner.IsConfigured;

    public int MaxStopsPerRequest => _inner.MaxStopsPerRequest;

    public TransitRequestDiagnostics? Diagnostics => _inner.Diagnostics;

    public Task<IReadOnlyList<TransitRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Routes, 0, () => _inner.GetRoutesAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TransitDirection>> GetDirectionsAsync(string routeId, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Directions, 1, () => _inner.GetDirectionsAsync(routeId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TransitStop>> GetStopsAsync(string routeId, string directionId, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Stops, 1, () => _inner.GetStopsAsync(routeId, directionId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<Arrival>> GetArrivalsAsync(IReadOnlyCollection<string> stopIds, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Predictions, stopIds.Count, () => _inner.GetArrivalsAsync(stopIds, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(IReadOnlyCollection<string> vehicleIds, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Vehicles, vehicleIds.Count, () => _inner.GetVehiclesAsync(vehicleIds, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Patterns, 1, () => _inner.GetPatternsAsync(routeId, cancellationToken), cancellationToken);

    private static Priority CurrentPriority()
    {
        (TransitConsumer consumer, TransitTrigger trigger) = TransitRequestDiagnostics.CurrentScope;
        if (consumer == TransitConsumer.Tracking)
        {
            return Priority.Urgent;
        }

        return trigger is TransitTrigger.ManualRefresh or TransitTrigger.Retry or TransitTrigger.FlyoutOpened
            or TransitTrigger.ModeChanged or TransitTrigger.DestinationChanged or TransitTrigger.StopsChanged
            or TransitTrigger.PlacesChanged or TransitTrigger.PickerSelection
            ? Priority.Manual
            : Priority.Automatic;
    }

    private async Task<T> RunAsync<T>(TransitEndpoint endpoint, int batchSize, Func<Task<T>> call, CancellationToken cancellationToken)
    {
        if (CheckGate() is { } refusal)
        {
            Diagnostics?.RecordSuppressed(endpoint, batchSize);
            throw refusal;
        }

        DateTimeOffset startedAt = _time.GetUtcNow();
        try
        {
            T result = await call();
            OnSuccess(startedAt);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own cancellation says nothing about the service.
            throw;
        }
        catch (Exception ex)
        {
            OnFailure(ex, startedAt);
            throw;
        }
    }

    private Exception? CheckGate()
    {
        Priority priority = CurrentPriority();
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_rateLimitedUntil > now)
            {
                return new TransitCooldownException(_rateLimitedUntil - now);
            }

            bool rejected = _rejectedUntil > now;
            TimeSpan sinceFailure = now - _lastFailureAt;
            bool spaced = sinceFailure >= _policy.ManualMinimumSpacing;
            if (rejected && (priority != Priority.Manual || !spaced))
            {
                return new TransitApiException(_rejectedMessage, TransitApiFailureKind.Authentication);
            }

            TimeSpan ordinary = _backoff.Remaining;
            if (ordinary > TimeSpan.Zero)
            {
                if (priority == Priority.Automatic)
                {
                    return new TransitCooldownException(ordinary);
                }

                if (priority == Priority.Manual && !spaced)
                {
                    return new TransitCooldownException(_policy.ManualMinimumSpacing - sinceFailure);
                }
            }

            return null;
        }
    }

    private void OnSuccess(DateTimeOffset startedAt)
    {
        _backoff.RecordSuccess();
        lock (_gate)
        {
            if (startedAt >= _lastFailureAt)
            {
                _rejectedUntil = DateTimeOffset.MinValue;
            }
        }
    }

    private void OnFailure(Exception exception, DateTimeOffset startedAt)
    {
        DateTimeOffset now = _time.GetUtcNow();
        switch (exception)
        {
            case TransitRateLimitException limited:
                HoldForRateLimit(now, limited.RetryAfter ?? _policy.RateLimitDefaultWait);
                break;
            case TransitApiException { Kind: TransitApiFailureKind.RateLimit }:
                HoldForRateLimit(now, _policy.QuotaExhaustedWait);
                break;
            case TransitApiException { Kind: TransitApiFailureKind.Authentication } rejected:
                HoldForRejection(now, rejected.Message);
                break;
            case TransitApiException:
                // A problem with this one query; the service itself answered.
                break;
            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http:
                HoldForRejection(now, $"The transit service rejected the request (HTTP {(int)http.StatusCode!.Value}).");
                break;
            case HttpRequestException or OperationCanceledException or IOException:
                _backoff.RecordFailure(startedAt);
                lock (_gate)
                {
                    _lastFailureAt = now;
                }

                break;
        }
    }

    private void HoldForRateLimit(DateTimeOffset now, TimeSpan wait)
    {
        TimeSpan bounded = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _policy.RateLimitMaximumWait ? _policy.RateLimitMaximumWait : wait;
        lock (_gate)
        {
            DateTimeOffset until = now + bounded;
            if (until > _rateLimitedUntil)
            {
                _rateLimitedUntil = until;
            }

            _lastFailureAt = now;
        }
    }

    private void HoldForRejection(DateTimeOffset now, string message)
    {
        lock (_gate)
        {
            _rejectedUntil = now + _policy.PermanentErrorSuppression;
            _rejectedMessage = message;
            _lastFailureAt = now;
        }
    }
}
