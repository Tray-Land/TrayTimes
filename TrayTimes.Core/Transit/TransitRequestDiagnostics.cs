using System.Globalization;

namespace TrayTimes.Transit;

public enum TransitEndpoint { Departures, NearbyRoutes, Plan, SearchStops, Networks, Routes, RouteDetails, Vehicles }

public enum TransitConsumer { Unspecified, Favorites, Commute, Nearby, Tracking, Settings }

public enum TransitTrigger
{
    Unspecified, Timer, FlyoutOpened, CenterChanged, ManualRefresh, StopsChanged, PlacesChanged,
    DestinationChanged, RefinedLocation, Retry, FavoritesLoading, FavoritesResult,
    PlaceResult, PlaceCompleted, Metadata, PickerSelection, VehicleSelected,
}

public enum TransitRequestPhase { Started, Completed, CacheReuse, InFlightReuse, Suppressed }

public enum TransitRequestOutcome { Pending, Success, HttpFailure, TransportFailure, ApiFailure, Canceled, Timeout, Failure, Suppressed }

/// <summary>Credential-safe observation: no URLs, IDs, location data, or exception messages.</summary>
public sealed record TransitRequestObservation(
    long RequestId,
    TransitEndpoint Endpoint,
    TransitConsumer Consumer,
    TransitTrigger Trigger,
    TransitRequestPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    TimeSpan Elapsed,
    TransitRequestOutcome Outcome,
    int BatchSize,
    int? HttpStatus)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"Transit request {RequestId}: endpoint={Endpoint} consumer={Consumer} trigger={Trigger} phase={Phase} started={StartedAt:O} finished={FinishedAt:O} elapsedMs={Elapsed.TotalMilliseconds:F1} outcome={Outcome} batch={BatchSize} http={HttpStatus}");
}

/// <summary>
/// Bounded, in-memory transit request diagnostics. Scope labels flow across awaits without changing request
/// lifetime or scheduling. Physical attempts remain counted exclusively by EndpointCalled.
/// </summary>
public sealed class TransitRequestDiagnostics(TimeProvider? timeProvider = null)
{
    public const int RetainedObservations = 256;

    private static readonly AsyncLocal<(TransitConsumer Consumer, TransitTrigger Trigger)> Context = new();

    private readonly Lock _gate = new();
    private readonly Queue<TransitRequestObservation> _observations = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _nextRequestId;

    /// <summary>Fires for starts, completions, and reuse; may fire on any thread.</summary>
    public event EventHandler<TransitRequestObservation>? Observed;

    /// <summary>A copy of the last 256 observations, not a saved usage tally.</summary>
    public IReadOnlyList<TransitRequestObservation> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _observations.ToArray();
            }
        }
    }

    public static IDisposable BeginScope(TransitConsumer consumer, TransitTrigger trigger) => new Scope(consumer, trigger);

    /// <summary>The consumer and trigger labelling requests made from the current async flow.</summary>
    public static (TransitConsumer Consumer, TransitTrigger Trigger) CurrentScope => Context.Value;

    /// <summary>Tracking is urgent; anything the rider just did is manual; timers and the rest are automatic.</summary>
    public static TransitPriority CurrentPriority => Context.Value switch
    {
        (TransitConsumer.Tracking, _) => TransitPriority.Urgent,
        (_, TransitTrigger.ManualRefresh or TransitTrigger.Retry or TransitTrigger.FlyoutOpened
            or TransitTrigger.CenterChanged or TransitTrigger.DestinationChanged or TransitTrigger.StopsChanged
            or TransitTrigger.PlacesChanged or TransitTrigger.PickerSelection or TransitTrigger.VehicleSelected) => TransitPriority.Manual,
        _ => TransitPriority.Automatic,
    };

    /// <summary>Observe a request that was held back by a failure cooldown; this never raises EndpointCalled.</summary>
    public void RecordSuppressed(TransitEndpoint endpoint, int batchSize = 0)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Publish(new TransitRequestObservation(0, endpoint, Context.Value.Consumer, Context.Value.Trigger,
            TransitRequestPhase.Suppressed, now, now, TimeSpan.Zero, TransitRequestOutcome.Suppressed, batchSize, null));
    }

    /// <summary>Observe existing cache/task reuse; this never raises EndpointCalled.</summary>
    public void RecordReuse(TransitEndpoint endpoint, bool inFlight, int batchSize = 0)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Publish(new TransitRequestObservation(0, endpoint, Context.Value.Consumer, Context.Value.Trigger,
            inFlight ? TransitRequestPhase.InFlightReuse : TransitRequestPhase.CacheReuse,
            now, now, TimeSpan.Zero, inFlight ? TransitRequestOutcome.Pending : TransitRequestOutcome.Success, batchSize, null));
    }

    internal Attempt Start(TransitEndpoint endpoint, int batchSize)
    {
        TransitRequestObservation observation = new(Interlocked.Increment(ref _nextRequestId), endpoint,
            Context.Value.Consumer, Context.Value.Trigger, TransitRequestPhase.Started, _time.GetUtcNow(),
            null, TimeSpan.Zero, TransitRequestOutcome.Pending, batchSize, null);
        Attempt attempt = new(observation, _time.GetTimestamp());
        Publish(observation);
        return attempt;
    }

    internal void Complete(Attempt attempt, TransitRequestOutcome outcome, int? httpStatus)
    {
        Publish(attempt.Observation with
        {
            Phase = TransitRequestPhase.Completed,
            FinishedAt = _time.GetUtcNow(),
            Elapsed = _time.GetElapsedTime(attempt.Timestamp),
            Outcome = outcome,
            HttpStatus = httpStatus,
        });
    }

    private void Publish(TransitRequestObservation observation)
    {
        lock (_gate)
        {
            if (_observations.Count == RetainedObservations)
            {
                _observations.Dequeue();
            }

            _observations.Enqueue(observation);
        }

        Observed?.Invoke(this, observation);
    }

    internal sealed record Attempt(TransitRequestObservation Observation, long Timestamp);

    private sealed class Scope : IDisposable
    {
        private readonly (TransitConsumer Consumer, TransitTrigger Trigger) _previous = Context.Value;

        public Scope(TransitConsumer consumer, TransitTrigger trigger) => Context.Value = (consumer, trigger);

        public void Dispose() => Context.Value = _previous;
    }
}
