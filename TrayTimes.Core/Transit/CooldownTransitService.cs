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

    public TimeSpan RateLimitMaximumWait { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long automatic requests stay off after a rejected key; one manual retry is still allowed.</summary>
    public TimeSpan PermanentErrorSuppression { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// The one gate every physical request passes. It stops requests to a failing or rate-limiting
/// service, then takes a slot from the call budget. While a hold is active, or the budget refuses,
/// a request is refused here with an exception, never with empty data.
/// <list type="bullet">
/// <item>Rate limit (429, or 503 with Retry-After): nothing is sent until the provider's window ends.</item>
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
    private readonly TransitBudget? _budget;
    private readonly FailureBackoff _backoff;
    private readonly Lock _gate = new();
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _rejectedUntil = DateTimeOffset.MinValue;
    private string _rejectedMessage = string.Empty;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;

    /// <param name="budget">The call budget each sent request takes a slot from; null for none.</param>
    public CooldownTransitService(ITransitService inner, TimeProvider? time = null, CooldownPolicy? policy = null, TransitBudget? budget = null)
    {
        _inner = inner;
        _time = time ?? TimeProvider.System;
        _policy = policy ?? new CooldownPolicy();
        _budget = budget;
        _backoff = new FailureBackoff(_time, _policy.FailureInitialDelay, _policy.FailureMaximumDelay);
    }

    public string ProviderName => _inner.ProviderName;

    public bool IsConfigured => _inner.IsConfigured;

    public int MaxStopsPerRequest => _inner.MaxStopsPerRequest;

    public TransitRequestDiagnostics? Diagnostics => _inner.Diagnostics;

    public Task<DepartureBoard> GetDeparturesAsync(IReadOnlyCollection<string> stopIds, bool includeShapes = false, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Departures, stopIds.Count, () => _inner.GetDeparturesAsync(stopIds, includeShapes, cancellationToken), cancellationToken);

    public Task<NearbyLines> GetNearbyLinesAsync(GeoPoint point, int radiusMeters, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.NearbyRoutes, 1, () => _inner.GetNearbyLinesAsync(point, radiusMeters, cancellationToken), cancellationToken);

    public Task<TripPlan> PlanTripAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default) =>
        PlanTripAsync(from, new TripDestination(to), cancellationToken);

    public Task<TripPlan> PlanTripAsync(GeoPoint from, TripDestination to, CancellationToken cancellationToken = default) =>
        PlanTripAsync(from, to, default, cancellationToken);

    public Task<TripPlan> PlanTripAsync(GeoPoint from, TripDestination to, TripTime time, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Plan, 1, () => _inner.PlanTripAsync(from, to, time, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TransitStop>> SearchStopsAsync(GeoPoint near, string query, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.SearchStops, 1, () => _inner.SearchStopsAsync(near, query, cancellationToken), cancellationToken);

    /// <summary>Two requests (networks, then routes), so it takes two budget slots.</summary>
    public Task<IReadOnlyList<TransitRoute>> GetRoutesNearAsync(GeoPoint point, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Routes, 1, () => _inner.GetRoutesNearAsync(point, cancellationToken), cancellationToken, slots: 2);

    public Task<RouteDetails> GetRouteDetailsAsync(string routeId, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.RouteDetails, 1, () => _inner.GetRouteDetailsAsync(routeId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(string routeId, string? directionId = null, CancellationToken cancellationToken = default) =>
        RunAsync(TransitEndpoint.Vehicles, 1, () => _inner.GetVehiclesAsync(routeId, directionId, cancellationToken), cancellationToken);

    private async Task<T> RunAsync<T>(TransitEndpoint endpoint, int batchSize, Func<Task<T>> call, CancellationToken cancellationToken, int slots = 1)
    {
        TransitPriority priority = TransitRequestDiagnostics.CurrentPriority;
        if (CheckGate(priority) is { } refusal)
        {
            Diagnostics?.RecordSuppressed(endpoint, batchSize);
            throw refusal;
        }

        if (_budget is not null && _inner.IsConfigured)
        {
            try
            {
                for (int i = 0; i < slots; i++)
                {
                    await _budget.AcquireAsync(priority, cancellationToken);
                }
            }
            catch (TransitApiException)
            {
                Diagnostics?.RecordSuppressed(endpoint, batchSize);
                throw;
            }
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

    private Exception? CheckGate(TransitPriority priority)
    {
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
            if (rejected && (priority != TransitPriority.Manual || !spaced))
            {
                return new TransitApiException(_rejectedMessage, TransitApiFailureKind.Authentication);
            }

            TimeSpan ordinary = _backoff.Remaining;
            if (ordinary > TimeSpan.Zero)
            {
                if (priority == TransitPriority.Automatic)
                {
                    return new TransitCooldownException(ordinary);
                }

                if (priority == TransitPriority.Manual && !spaced)
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
