using System.Text.Json.Serialization;

namespace TrayTimes.Models;

/// <summary>
/// A stop the user follows. <see cref="RouteId"/> and <see cref="DirectionId"/> are the route and
/// direction it was picked from; arrivals are filtered to them unless <see cref="AllRoutes"/> is set.
/// </summary>
public sealed record SavedStop(
    string StopId,
    string StopName,
    double Lat,
    double Lon,
    string RouteId,
    string RouteDisplayId,
    string? RouteColor,
    string DirectionId,
    string DirectionName,
    bool AllRoutes,
    string? Nickname = null)
{
    [JsonIgnore]
    public string Key => $"{StopId}|{RouteId}|{DirectionId}";

    /// <summary>The user's nickname for the stop, else its name.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? StopName : Nickname;

    /// <summary>Returns the stops with <paramref name="nickname"/> (cleared when blank) set on every entry for <paramref name="stopId"/>.</summary>
    public static IEnumerable<SavedStop> WithNickname(IEnumerable<SavedStop> stops, string stopId, string? nickname)
    {
        string? trimmed = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        return stops.Select(s => s.StopId == stopId ? s with { Nickname = trimmed } : s);
    }

    [JsonIgnore]
    public string Description => !AllRoutes ? $"Route {RouteDisplayId} · {DirectionName}"
        : RouteDisplayId.Length > 0 ? $"All routes (added from route {RouteDisplayId})"
        : "All routes";

    /// <summary>Number of distinct stop IDs, which the arrivals request is limited by.</summary>
    public static int DistinctStopCount(IEnumerable<SavedStop> stops) => stops.Select(s => s.StopId).Distinct().Count();
}
