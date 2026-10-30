namespace TrayTimes.Commute;

/// <summary>
/// One location lookup shared by the place monitors that refresh together: every monitor that asks
/// during a refresh gets the same fix, and the same refinement, so Windows is asked once. The first
/// request of a refresh decides whether it's a quick fix.
/// </summary>
public sealed class SharedLocation(Func<bool, Task<PlaceLocation>> locate)
{
    private Task<PlaceLocation>? _current;

    /// <summary>Starts a refresh: the next request looks the location up again.</summary>
    public void BeginRefresh() => _current = null;

    /// <summary>This refresh's location, looked up on its first request.</summary>
    /// <param name="quick">Whether to return a rough fix first and refine it (see <see cref="PlaceLocation.Refined"/>).</param>
    public Task<PlaceLocation> LocateAsync(bool quick) => _current ??= locate(quick);
}
