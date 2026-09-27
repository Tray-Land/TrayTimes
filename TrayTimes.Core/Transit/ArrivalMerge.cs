namespace TrayTimes.Transit;

/// <summary>Fills in scheduled times where real-time predictions don't reach.</summary>
public static class ArrivalMerge
{
    /// <summary>
    /// How far past the last prediction a scheduled time must be to count as a different bus. A
    /// bus running a little early or late would otherwise show twice.
    /// </summary>
    public static readonly TimeSpan MatchTolerance = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The live arrivals plus, for each stop, route and direction, the scheduled ones later than
    /// anything predicted there. A route with no predictions at all shows its whole schedule.
    /// Soonest first.
    /// </summary>
    public static IReadOnlyList<Arrival> WithSchedule(IReadOnlyList<Arrival> live, IReadOnlyList<Arrival> scheduled)
    {
        if (scheduled.Count == 0)
        {
            return live;
        }

        Dictionary<(string, string, string), DateTimeOffset> lastPredicted = [];
        foreach (Arrival arrival in live)
        {
            (string, string, string) key = Key(arrival);
            if (!lastPredicted.TryGetValue(key, out DateTimeOffset latest) || arrival.PredictedTime > latest)
            {
                lastPredicted[key] = arrival.PredictedTime;
            }
        }

        List<Arrival> merged = [.. live];
        merged.AddRange(scheduled.Where(s => !lastPredicted.TryGetValue(Key(s), out DateTimeOffset latest) || s.PredictedTime > latest + MatchTolerance));
        merged.Sort((a, b) => a.PredictedTime.CompareTo(b.PredictedTime));
        return merged;
    }

    private static (string, string, string) Key(Arrival arrival) =>
        (arrival.StopId, arrival.RouteId.ToUpperInvariant(), arrival.DirectionId.ToUpperInvariant());
}
