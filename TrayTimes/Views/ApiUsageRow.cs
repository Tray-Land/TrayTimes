namespace TrayTimes.Views;

/// <summary>One line of the Settings page's API usage table.</summary>
internal sealed record ApiUsageRow(string Name, string Today, string Last7Days, string Last14Days);
