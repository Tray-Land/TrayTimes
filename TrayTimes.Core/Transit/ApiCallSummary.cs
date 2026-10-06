namespace TrayTimes.Transit;

/// <summary>How many API calls were made today and over the last 7 and 14 days (today included).</summary>
public sealed record ApiCallCounts(int Today, int Last7Days, int Last14Days);

/// <summary>Call counts overall and per endpoint, busiest endpoint first.</summary>
public sealed record ApiCallSummary(ApiCallCounts Total, IReadOnlyList<KeyValuePair<string, ApiCallCounts>> ByEndpoint);
