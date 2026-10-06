using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class ApiCallLogTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

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
    public void Summarize_CallsOnVariousDays_CountsEachWindow()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("getpredictions", Today);
        log.Record("getpredictions", Today);
        log.Record("getpredictions", Today.AddDays(-6));
        log.Record("getpredictions", Today.AddDays(-7));
        log.Record("getpredictions", Today.AddDays(-13));

        // Act
        ApiCallCounts total = log.Summarize(Today).Total;

        // Assert
        Assert.AreEqual(new ApiCallCounts(2, 3, 5), total);
    }

    [TestMethod]
    public void Summarize_MultipleEndpoints_SplitsByEndpointBusiestFirst()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("getvehicles", Today);
        log.Record("getpredictions", Today);
        log.Record("getpredictions", Today.AddDays(-1));

        // Act
        ApiCallSummary summary = log.Summarize(Today);

        // Assert
        Assert.AreEqual(new ApiCallCounts(2, 3, 3), summary.Total);
        CollectionAssert.AreEqual(new[] { "getpredictions", "getvehicles" }, summary.ByEndpoint.Select(e => e.Key).ToArray());
        Assert.AreEqual(new ApiCallCounts(1, 2, 2), summary.ByEndpoint[0].Value);
        Assert.AreEqual(new ApiCallCounts(1, 1, 1), summary.ByEndpoint[1].Value);
    }

    [TestMethod]
    public void Record_DaysOlderThanRetention_AreDropped()
    {
        // Arrange
        ApiCallLog log = new();
        log.Record("getroutes", Today.AddDays(-ApiCallLog.RetainedDays));
        log.Record("getroutes", Today.AddDays(-(ApiCallLog.RetainedDays - 1)));

        // Act
        log.Record("getroutes", Today);

        // Assert
        Assert.HasCount(2, log.Days);
        Assert.AreEqual(2, log.Summarize(Today).Total.Last14Days);
    }
}
