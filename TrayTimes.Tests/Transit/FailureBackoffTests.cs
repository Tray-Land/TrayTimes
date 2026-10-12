using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class FailureBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Maximum = TimeSpan.FromSeconds(60);

    [TestMethod]
    public void RecordFailure_Repeated_DoublesUntilTheMaximum()
    {
        // Arrange
        RequestTestClock clock = new();
        FailureBackoff backoff = new(clock, Initial, Maximum);
        List<double> holds = [];

        // Act
        for (int i = 0; i < 5; i++)
        {
            backoff.RecordFailure(clock.GetUtcNow());
            holds.Add(backoff.Remaining.TotalSeconds);
            clock.Advance(backoff.Remaining);
        }

        // Assert
        CollectionAssert.AreEqual(new[] { 10d, 20d, 40d, 60d, 60d }, holds);
        Assert.AreEqual(5, backoff.ConsecutiveFailures);
    }

    [TestMethod]
    public void Remaining_AfterTheHoldOff_IsZero()
    {
        // Arrange
        RequestTestClock clock = new();
        FailureBackoff backoff = new(clock, Initial, Maximum);
        backoff.RecordFailure(clock.GetUtcNow());

        // Act
        clock.Advance(Initial);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, backoff.Remaining);
    }

    [TestMethod]
    public void RecordSuccess_AfterFailures_ResetsTheHoldOff()
    {
        // Arrange
        RequestTestClock clock = new();
        FailureBackoff backoff = new(clock, Initial, Maximum);
        backoff.RecordFailure(clock.GetUtcNow());
        clock.Advance(Initial);
        backoff.RecordFailure(clock.GetUtcNow());

        // Act
        backoff.RecordSuccess();

        // Assert
        Assert.AreEqual(0, backoff.ConsecutiveFailures);
        Assert.AreEqual(TimeSpan.Zero, backoff.Remaining);
        Assert.IsNull(backoff.LastFailureAt);
    }

    [TestMethod]
    public void RecordFailure_StartedBeforeTheLatestSuccess_IsIgnored()
    {
        // Arrange
        RequestTestClock clock = new();
        FailureBackoff backoff = new(clock, Initial, Maximum);
        DateTimeOffset started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        backoff.RecordSuccess();
        clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        backoff.RecordFailure(started);

        // Assert
        Assert.AreEqual(0, backoff.ConsecutiveFailures);
        Assert.AreEqual(TimeSpan.Zero, backoff.Remaining);
    }

    [TestMethod]
    public void RecordFailure_StartedBeforeTheLatestRecordedFailure_CountsOnce()
    {
        // Arrange
        RequestTestClock clock = new();
        FailureBackoff backoff = new(clock, Initial, Maximum);
        DateTimeOffset started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        backoff.RecordFailure(started);

        // Act
        backoff.RecordFailure(started);

        // Assert
        Assert.AreEqual(1, backoff.ConsecutiveFailures);
    }
}
