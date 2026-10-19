namespace TrayTimes.Transit;

/// <summary>How many API calls were made today, over the last 7 days, and so far this calendar month (today included).</summary>
public sealed record ApiCallCounts(int Today, int Last7Days, int ThisMonth);

/// <summary>Call counts overall and per endpoint, busiest endpoint this month first.</summary>
public sealed record ApiCallSummary(ApiCallCounts Total, IReadOnlyList<KeyValuePair<string, ApiCallCounts>> ByEndpoint);
