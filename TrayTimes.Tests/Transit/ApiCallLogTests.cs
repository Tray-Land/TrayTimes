using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class ApiCallLogTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    [TestMethod]
    public void Summarize_NothingRecorded_ReturnsZeros()
    {
        // Arrange
        ApiCallLog log = new();

        // Act
        ApiCallSummary summary = log.Summarize(Today);

        // Assert
        Assert.AreEqual(new ApiCallCounts(0, 0, 0), summary.Total);
        Assert.IsEmpty(summary.ByEndpoint);
    }

    [TestMethod]
    public void Summarize_CallsOnVariousDays_CountsTodayWeekAndCalendarMonth()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("stop_departures", Today);
        log.Record("stop_departures", Today);
        log.Record("stop_departures", Today.AddDays(-6));
        log.Record("stop_departures", Today.AddDays(-7));
        log.Record("stop_departures", new DateOnly(2026, 10, 1));
        log.Record("stop_departures", new DateOnly(2026, 9, 30));

        // Act
        ApiCallCounts total = log.Summarize(Today).Total;

        // Assert
        Assert.AreEqual(new ApiCallCounts(2, 3, 5), total);
    }

    [TestMethod]
    public void Summarize_MultipleEndpoints_SplitsByEndpointBusiestThisMonthFirst()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("vehicles", Today);
        log.Record("stop_departures", Today);
        log.Record("stop_departures", Today.AddDays(-1));

        // Act
        ApiCallSummary summary = log.Summarize(Today);

        // Assert
        Assert.AreEqual(new ApiCallCounts(2, 3, 3), summary.Total);
        CollectionAssert.AreEqual(new[] { "stop_departures", "vehicles" }, summary.ByEndpoint.Select(e => e.Key).ToArray());
        Assert.AreEqual(new ApiCallCounts(1, 2, 2), summary.ByEndpoint[0].Value);
        Assert.AreEqual(new ApiCallCounts(1, 1, 1), summary.ByEndpoint[1].Value);
    }

    [TestMethod]
    public void Usage_CallsThisMonthAndLast_CountsTodayAndThisMonthOnly()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("plan", Today);
        log.Record("stop_departures", Today);
        log.Record("stop_departures", Today.AddDays(-3));
        log.Record("stop_departures", new DateOnly(2026, 9, 29));

        // Act
        TransitUsage usage = log.Usage(Today);

        // Assert
        Assert.AreEqual(new TransitUsage(2, 3), usage);
    }

    [TestMethod]
    public void Record_DaysOlderThanRetention_AreDropped()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("route_details", Today.AddDays(-ApiCallLog.RetainedDays));
        log.Record("route_details", Today.AddDays(-(ApiCallLog.RetainedDays - 1)));

        // Act
        log.Record("route_details", Today);

        // Assert
        Assert.HasCount(2, log.Days);
    }

    [TestMethod]
    public void RetainedDays_AnyCalendarMonth_IsCoveredWithTheMonthBefore()
    {
        // Arrange
        int longestTwoMonths = 31 + 31;

        // Act
        int retained = ApiCallLog.RetainedDays;

        // Assert
        Assert.IsGreaterThanOrEqualTo(longestTwoMonths, retained);
    }
}
