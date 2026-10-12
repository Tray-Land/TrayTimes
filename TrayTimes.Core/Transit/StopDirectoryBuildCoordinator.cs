using TrayTimes.Models;

namespace TrayTimes.Transit;

/// <summary>
/// Runs at most one stop-directory build at a time and holds off automatic rebuilds after a
/// failure, so a broken service is not re-crawled for every caller. A manual rebuild skips the
/// hold-off but still joins a build that is already running.
/// </summary>
public sealed class StopDirectoryBuildCoordinator(
    Func<CancellationToken, Task<IReadOnlyList<IndexedStop>>> build,
    TimeProvider? time = null,
    TimeSpan? initialDelay = null,
    TimeSpan? maximumDelay = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly FailureBackoff _backoff = new(
        time ?? TimeProvider.System,
        initialDelay ?? TimeSpan.FromMinutes(5),
        maximumDelay ?? TimeSpan.FromHours(1));

    private Task<IReadOnlyList<IndexedStop>>? _running;

    /// <summary>True while a build is in progress.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <exception cref="TransitCooldownException">An automatic build was refused because the last one failed recently.</exception>
    public Task<IReadOnlyList<IndexedStop>> BuildAsync(bool manual)
    {
        lock (_gate)
        {
            if (_running is not null)
            {
                return _running;
            }

            TimeSpan remaining = _backoff.Remaining;
            if (!manual && remaining > TimeSpan.Zero)
            {
                return Task.FromException<IReadOnlyList<IndexedStop>>(new TransitCooldownException(remaining));
            }

            _running = RunAsync();
            return _running;
        }
    }

    private async Task<IReadOnlyList<IndexedStop>> RunAsync()
    {
        // Let the caller's lock release and its assignment of _running finish before any work.
        await Task.Yield();
        DateTimeOffset startedAt = _time.GetUtcNow();
        try
        {
            IReadOnlyList<IndexedStop> stops = await build(CancellationToken.None);
            _backoff.RecordSuccess();
            return stops;
        }
        catch
        {
            _backoff.RecordFailure(startedAt);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }
}
