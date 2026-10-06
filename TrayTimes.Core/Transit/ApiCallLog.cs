using System.Globalization;

namespace TrayTimes.Transit;

/// <summary>
/// Per-day, per-endpoint tally of transit API calls, kept for <see cref="RetainedDays"/> days.
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class ApiCallLog
{
    public const int RetainedDays = 14;

    private const string DayFormat = "yyyyMMdd";

    /// <summary>Calls by day (yyyyMMdd) then endpoint. Public so it can be saved and restored as-is.</summary>
    public Dictionary<string, Dictionary<string, int>> Days { get; init; } = [];

    public void Record(string endpoint, DateOnly day)
    {
        string key = DayKey(day);
        if (!Days.TryGetValue(key, out Dictionary<string, int>? counts))
        {
            counts = [];
            Days[key] = counts;
        }

        counts[endpoint] = counts.GetValueOrDefault(endpoint) + 1;

        string oldest = DayKey(day.AddDays(1 - RetainedDays));
        foreach (string stale in Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
        {
            Days.Remove(stale);
        }
    }

    public ApiCallSummary Summarize(DateOnly today)
    {
        Dictionary<string, ApiCallCounts> byEndpoint = [];
        foreach (string endpoint in Days.Values.SelectMany(d => d.Keys).Distinct())
        {
            byEndpoint[endpoint] = Count(today, endpoint);
        }

        return new ApiCallSummary(
            Count(today, null),
            [.. byEndpoint.OrderByDescending(e => e.Value.Last14Days).ThenBy(e => e.Key, StringComparer.Ordinal)]);
    }

    private static string DayKey(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private ApiCallCounts Count(DateOnly today, string? endpoint) =>
        new(CountSince(today, today, endpoint), CountSince(today.AddDays(-6), today, endpoint), CountSince(today.AddDays(-13), today, endpoint));

    private int CountSince(DateOnly first, DateOnly last, string? endpoint)
    {
        string firstKey = DayKey(first);
        string lastKey = DayKey(last);
        return Days
            .Where(d => string.CompareOrdinal(d.Key, firstKey) >= 0 && string.CompareOrdinal(d.Key, lastKey) <= 0)
            .Sum(d => endpoint is null ? d.Value.Values.Sum() : d.Value.GetValueOrDefault(endpoint));
    }
}
