using System.Globalization;

namespace TrayTimes.Transit;

/// <summary>Who a request is for: a timer, the rider, or the tracked ride.</summary>
public enum TransitPriority
{
    /// <summary>Timer-driven browsing refreshes; the first to be held back.</summary>
    Automatic,

    /// <summary>Something the rider just did: opened the flyout, refreshed, picked a stop.</summary>
    Manual,

    /// <summary>The tracked ride, whose alerts must not stop.</summary>
    Urgent,
}

/// <summary>How much of today's share of the monthly quota is left, which sets how often the app refreshes on its own.</summary>
public enum TransitBudgetLevel
{
    /// <summary>Under 60% of today's share used.</summary>
    Normal,

    /// <summary>60-100% of today's share used: refresh less often.</summary>
    Conserve,

    /// <summary>Today's share used: no automatic refreshes until tomorrow.</summary>
    Low,

    /// <summary>Only the month's reserve is left: rider actions and tracking only.</summary>
    Reserve,

    /// <summary>The month's quota is used: nothing is sent until next month.</summary>
    Exhausted,
}

/// <summary>The key's limits. The defaults are Transit's free tier.</summary>
public sealed record TransitBudgetPolicy
{
    public const int DefaultCallsPerMinute = 5;
    public const int DefaultCallsPerMonth = 1500;

    public int CallsPerMinute { get; init; } = DefaultCallsPerMinute;

    public int CallsPerMonth { get; init; } = DefaultCallsPerMonth;

    /// <summary>Per-minute slots that automatic refreshes leave free for rider actions and tracking.</summary>
    public int ReservedPerMinute { get; init; } = 2;

    /// <summary>Calls at the end of the month kept for rider actions and tracking: 2% of the month, at least 10.</summary>
    public int MonthlyReserve => Math.Max(10, CallsPerMonth / 50);
}

/// <summary>Calls already made, from the saved tally (local calendar day and month).</summary>
public readonly record struct TransitUsage(int Today, int ThisMonth);

/// <summary>
/// Keeps requests within the key's limits. Every physical request takes a slot first:
/// <list type="bullet">
/// <item>Per minute: a sliding window of sent requests. Callers wait for a free slot; automatic
/// refreshes leave <see cref="TransitBudgetPolicy.ReservedPerMinute"/> slots for everyone else.</item>
/// <item>Per month: today's share is what's left of the month divided by the days left. As it's
/// used up, automatic refreshes slow, then stop; rider actions and tracking go on until only the
/// month's reserve is left, and tracking until the month is used up.</item>
/// </list>
/// Thread-safe.
/// </summary>
public sealed class TransitBudget
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Func<TransitUsage> _usage;
    private readonly TimeProvider _time;
    private TransitBudgetPolicy _policy;

    public TransitBudget(Func<TransitUsage> usage, TransitBudgetPolicy? policy = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _usage = usage;
        _policy = Validate(policy ?? new TransitBudgetPolicy());
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when the limits change. May fire on any thread.</summary>
    public event EventHandler? Changed;

    public TransitBudgetPolicy Policy
    {
        get
        {
            lock (_gate)
            {
                return _policy;
            }
        }

        set
        {
            lock (_gate)
            {
                _policy = Validate(value);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public TransitBudgetLevel Level => LevelFor(_usage(), Policy);

    /// <summary>Today's share of what's left of the month.</summary>
    public int DailyAllowance => AllowanceFor(_usage(), Policy);

    /// <summary>How often browsing refreshes on its own, or null while automatic refreshes are paused.</summary>
    public TimeSpan? AutomaticRefreshInterval => Level switch
    {
        TransitBudgetLevel.Normal => TimeSpan.FromSeconds(60),
        TransitBudgetLevel.Conserve => TimeSpan.FromMinutes(3),
        _ => null,
    };

    /// <summary>How old live data may be and still be shown without a new request, e.g. when the flyout reopens.</summary>
    public TimeSpan ReuseAge => Level switch
    {
        TransitBudgetLevel.Normal => TimeSpan.FromSeconds(60),
        TransitBudgetLevel.Conserve => TimeSpan.FromMinutes(2),
        TransitBudgetLevel.Low => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>How much slower than its normal schedule tracking polls.</summary>
    public int TrackingSlowdown => Level == TransitBudgetLevel.Normal ? 1 : 2;

    /// <summary>
    /// Takes a slot for one request, waiting while the minute's slots are used. Throws
    /// <see cref="TransitApiException"/> with <see cref="TransitApiFailureKind.Budget"/> when the
    /// monthly budget refuses this priority; nothing should be sent then.
    /// </summary>
    public async Task AcquireAsync(TransitPriority priority, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitBudgetPolicy policy = Policy;
            if (Refusal(priority, _usage(), policy) is { } refusal)
            {
                throw refusal;
            }

            TimeSpan wait;
            lock (_gate)
            {
                DateTimeOffset now = _time.GetUtcNow();
                while (_sent.Count > 0 && now - _sent.Peek() >= Window)
                {
                    _sent.Dequeue();
                }

                int limit = priority == TransitPriority.Automatic
                    ? Math.Max(1, policy.CallsPerMinute - policy.ReservedPerMinute)
                    : policy.CallsPerMinute;
                if (_sent.Count < limit)
                {
                    _sent.Enqueue(now);
                    return;
                }

                // The slot that frees first is the one that lets this caller under its limit.
                wait = _sent.ElementAt(_sent.Count - limit) + Window - now;
            }

            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), _time, cancellationToken);
        }
    }

    /// <summary>Today's share of what's left of the month, at least one call.</summary>
    private int AllowanceFor(TransitUsage usage, TransitBudgetPolicy policy)
    {
        DateTime today = _time.GetLocalNow().Date;
        int daysLeft = DateTime.DaysInMonth(today.Year, today.Month) - today.Day + 1;
        int leftBeforeToday = policy.CallsPerMonth - (usage.ThisMonth - usage.Today);
        return Math.Max(1, leftBeforeToday / daysLeft);
    }

    private TransitBudgetLevel LevelFor(TransitUsage usage, TransitBudgetPolicy policy)
    {
        int left = policy.CallsPerMonth - usage.ThisMonth;
        if (left <= 0)
        {
            return TransitBudgetLevel.Exhausted;
        }

        if (left <= policy.MonthlyReserve)
        {
            return TransitBudgetLevel.Reserve;
        }

        int allowance = AllowanceFor(usage, policy);
        return usage.Today >= allowance ? TransitBudgetLevel.Low
            : usage.Today * 10 >= allowance * 6 ? TransitBudgetLevel.Conserve
            : TransitBudgetLevel.Normal;
    }

    private TransitApiException? Refusal(TransitPriority priority, TransitUsage usage, TransitBudgetPolicy policy) =>
        (LevelFor(usage, policy), priority) switch
        {
            (TransitBudgetLevel.Exhausted, _) => new TransitApiException(
                string.Format(CultureInfo.CurrentCulture, "This month's {0:N0} Transit API calls are used up. Times resume next month.", policy.CallsPerMonth),
                TransitApiFailureKind.Budget),
            (TransitBudgetLevel.Reserve, TransitPriority.Automatic) => new TransitApiException(
                "Only this month's reserve of Transit API calls is left, so times update only when you refresh.", TransitApiFailureKind.Budget),
            (TransitBudgetLevel.Low, TransitPriority.Automatic) => new TransitApiException(
                "Automatic updates are paused for today to save Transit API calls. Refresh to update now.", TransitApiFailureKind.Budget),
            _ => null,
        };

    private static TransitBudgetPolicy Validate(TransitBudgetPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.CallsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.CallsPerMonth, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.ReservedPerMinute);
        return policy;
    }
}
