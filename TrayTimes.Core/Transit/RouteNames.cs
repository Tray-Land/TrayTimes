namespace TrayTimes.Transit;

public static class RouteNames
{
    /// <summary>True when the route goes by a number alone ("15"), so text names it as a route ("Route 15") rather than as the agency writes it ("30X", "Green").</summary>
    public static bool IsNumber(string displayId) =>
        displayId.Length > 0 && displayId.All(char.IsAsciiDigit);
}
