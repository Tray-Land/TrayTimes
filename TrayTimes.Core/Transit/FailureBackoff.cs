namespace TrayTimes.Transit;

/// <summary>
/// Bounded exponential hold-off after consecutive failures. A success resets it, and a failure
/// from a request that began before the latest success, or before the latest recorded failure
/// (the same outage seen by a parallel request), is not counted again.
/// </summary>
public sealed class FailureBackoff(TimeProvider time, TimeSpan initialDelay, TimeSpan maximumDelay)
{
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;

    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _failures;
            }
        }
    }

    /// <summary>When the latest counted failure was recorded, or null after a success.</summary>
    public DateTimeOffset? LastFailureAt
    {
        get
        {
            lock (_gate)
            {
                return _failures == 0 ? null : _lastFailure;
            }
        }
    }

    /// <summary>How long requests should still wait; zero when none are on hold.</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                TimeSpan remaining = _until - time.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public void RecordFailure(DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (startedAt < _lastSuccess || startedAt <= _lastFailure)
            {
                return;
            }

            DateTimeOffset now = time.GetUtcNow();
            _failures++;
            _lastFailure = now;
            double factor = Math.Pow(2, Math.Min(_failures - 1, 30));
            TimeSpan delay = TimeSpan.FromTicks((long)Math.Min(initialDelay.Ticks * factor, maximumDelay.Ticks));
            _until = now + delay;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _lastSuccess = time.GetUtcNow();
            _failures = 0;
            _until = DateTimeOffset.MinValue;
            _lastFailure = DateTimeOffset.MinValue;
        }
    }
}
