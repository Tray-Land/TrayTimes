using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class TransitBudgetTests
{
    // RequestTestClock starts on October 6: 26 days are left in the month, today included.
    private const int DaysLeft = 26;

    [TestMethod]
    public async Task AcquireAsync_FreeSlots_ReturnsAtOnce()
    {
        // Arrange
        Scenario scenario = new();

        // Act
        for (int i = 0; i < 5; i++)
        {
            await scenario.Budget.AcquireAsync(TransitPriority.Manual);
        }

        // Assert
        Assert.AreEqual(RequestTestClock.Start, scenario.Clock.GetUtcNow());
    }

    [TestMethod]
    public async Task AcquireAsync_MinuteUsedUp_WaitsUntilTheOldestSlotFrees()
    {
        // Arrange
        Scenario scenario = new();
        for (int i = 0; i < 5; i++)
        {
            await scenario.Budget.AcquireAsync(TransitPriority.Manual);
            scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Act
        Task sixth = scenario.Budget.AcquireAsync(TransitPriority.Manual);
        Assert.IsFalse(sixth.IsCompleted);
        scenario.Clock.Advance(TimeSpan.FromSeconds(54.999));
        Assert.IsFalse(sixth.IsCompleted);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await sixth.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.AreEqual(RequestTestClock.Start.AddMinutes(1), scenario.Clock.GetUtcNow());
    }

    [TestMethod]
    public async Task AcquireAsync_AutomaticRequest_LeavesReservedSlotsForTheRider()
    {
        // Arrange
        Scenario scenario = new();
        for (int i = 0; i < 3; i++)
        {
            await scenario.Budget.AcquireAsync(TransitPriority.Automatic);
        }

        // Act
        Task automatic = scenario.Budget.AcquireAsync(TransitPriority.Automatic);
        Task manual = scenario.Budget.AcquireAsync(TransitPriority.Manual);
        Task urgent = scenario.Budget.AcquireAsync(TransitPriority.Urgent);

        // Assert
        Assert.IsFalse(automatic.IsCompleted);
        Assert.IsTrue(manual.IsCompletedSuccessfully);
        Assert.IsTrue(urgent.IsCompletedSuccessfully);
        scenario.Clock.Advance(TransitBudget.Window);
        await automatic.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AcquireAsync_CanceledWhileWaiting_ThrowsAndTakesNoSlot()
    {
        // Arrange
        Scenario scenario = new();
        for (int i = 0; i < 5; i++)
        {
            await scenario.Budget.AcquireAsync(TransitPriority.Manual);
        }

        using CancellationTokenSource cancellation = new();
        Task waiting = scenario.Budget.AcquireAsync(TransitPriority.Manual, cancellation.Token);

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        scenario.Clock.Advance(TransitBudget.Window);
        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(scenario.Budget.AcquireAsync(TransitPriority.Manual).IsCompletedSuccessfully);
        }
    }

    [TestMethod]
    [DataRow(0, TransitBudgetLevel.Normal)]
    [DataRow(34, TransitBudgetLevel.Normal)]
    [DataRow(35, TransitBudgetLevel.Conserve)]
    [DataRow(56, TransitBudgetLevel.Conserve)]
    [DataRow(57, TransitBudgetLevel.Low)]
    public void Level_TodaysShare_SlowsThenPausesAutomaticRefreshes(int today, TransitBudgetLevel expected)
    {
        // Arrange: nothing used before today, so today's share is 1500 / 26 = 57.
        Scenario scenario = new() { Usage = new TransitUsage(today, today) };

        // Act
        TransitBudgetLevel level = scenario.Budget.Level;

        // Assert
        Assert.AreEqual(1500 / DaysLeft, scenario.Budget.DailyAllowance);
        Assert.AreEqual(expected, level);
    }

    [TestMethod]
    [DataRow(1469, TransitBudgetLevel.Normal)]
    [DataRow(1470, TransitBudgetLevel.Reserve)]
    [DataRow(1500, TransitBudgetLevel.Exhausted)]
    [DataRow(1700, TransitBudgetLevel.Exhausted)]
    public void Level_MonthNearlyUsed_KeepsTheReserveForTheRider(int thisMonth, TransitBudgetLevel expected)
    {
        // Arrange: nothing yet today; the reserve is 2% of 1,500, 30 calls.
        Scenario scenario = new() { Usage = new TransitUsage(0, thisMonth) };

        // Act
        TransitBudgetLevel level = scenario.Budget.Level;

        // Assert
        Assert.AreEqual(expected, level);
    }

    [TestMethod]
    [DataRow(TransitBudgetLevel.Normal, 60, 60, 1)]
    [DataRow(TransitBudgetLevel.Conserve, 180, 120, 2)]
    [DataRow(TransitBudgetLevel.Low, null, 300, 2)]
    [DataRow(TransitBudgetLevel.Reserve, null, 600, 2)]
    public void Intervals_EachLevel_FollowTheDocumentedCadence(TransitBudgetLevel level, int? refreshSeconds, int reuseSeconds, int slowdown)
    {
        // Arrange
        Scenario scenario = new() { Usage = UsageFor(level) };

        // Act
        TimeSpan? refresh = scenario.Budget.AutomaticRefreshInterval;

        // Assert
        Assert.AreEqual(level, scenario.Budget.Level);
        Assert.AreEqual(refreshSeconds is { } s ? TimeSpan.FromSeconds(s) : null, refresh);
        Assert.AreEqual(TimeSpan.FromSeconds(reuseSeconds), scenario.Budget.ReuseAge);
        Assert.AreEqual(slowdown, scenario.Budget.TrackingSlowdown);
    }

    [TestMethod]
    [DataRow(TransitBudgetLevel.Low, TransitPriority.Automatic, true)]
    [DataRow(TransitBudgetLevel.Low, TransitPriority.Manual, false)]
    [DataRow(TransitBudgetLevel.Reserve, TransitPriority.Automatic, true)]
    [DataRow(TransitBudgetLevel.Reserve, TransitPriority.Manual, false)]
    [DataRow(TransitBudgetLevel.Reserve, TransitPriority.Urgent, false)]
    [DataRow(TransitBudgetLevel.Exhausted, TransitPriority.Urgent, true)]
    [DataRow(TransitBudgetLevel.Exhausted, TransitPriority.Manual, true)]
    public async Task AcquireAsync_MonthlyLevel_RefusesOnlyWhatItMust(TransitBudgetLevel level, TransitPriority priority, bool refused)
    {
        // Arrange
        Scenario scenario = new() { Usage = UsageFor(level) };

        // Act
        Task acquire = scenario.Budget.AcquireAsync(priority);

        // Assert
        if (refused)
        {
            TransitApiException failure = await Assert.ThrowsExactlyAsync<TransitApiException>(() => acquire);
            Assert.AreEqual(TransitApiFailureKind.Budget, failure.Kind);
        }
        else
        {
            await acquire;
        }
    }

    [TestMethod]
    public void Policy_ChangedLimits_RaiseChangedAndApplyAtOnce()
    {
        // Arrange
        Scenario scenario = new() { Usage = new TransitUsage(40, 40) };
        bool changed = false;
        scenario.Budget.Changed += (_, _) => changed = true;

        // Act
        scenario.Budget.Policy = new TransitBudgetPolicy { CallsPerMinute = 60, CallsPerMonth = 100_000 };

        // Assert
        Assert.IsTrue(changed);
        Assert.AreEqual(TransitBudgetLevel.Normal, scenario.Budget.Level);
    }

    [TestMethod]
    public void Policy_InvalidLimits_AreRejected()
    {
        // Arrange
        Scenario scenario = new();

        // Act & Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scenario.Budget.Policy = new TransitBudgetPolicy { CallsPerMinute = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scenario.Budget.Policy = new TransitBudgetPolicy { CallsPerMonth = 0 });
        Assert.ThrowsExactly<ArgumentNullException>(() => new TransitBudget(null!));
    }

    [TestMethod]
    public void MonthlyReserve_SmallOrLargeMonth_IsTwoPercentButAtLeastTen()
    {
        // Act & Assert
        Assert.AreEqual(30, new TransitBudgetPolicy().MonthlyReserve);
        Assert.AreEqual(10, new TransitBudgetPolicy { CallsPerMonth = 100 }.MonthlyReserve);
        Assert.AreEqual(2000, new TransitBudgetPolicy { CallsPerMonth = 100_000 }.MonthlyReserve);
    }

    private static TransitUsage UsageFor(TransitBudgetLevel level) => level switch
    {
        TransitBudgetLevel.Normal => new TransitUsage(0, 0),
        TransitBudgetLevel.Conserve => new TransitUsage(40, 40),
        TransitBudgetLevel.Low => new TransitUsage(60, 60),
        TransitBudgetLevel.Reserve => new TransitUsage(0, 1480),
        _ => new TransitUsage(0, 1500),
    };

    private sealed class Scenario
    {
        public Scenario() => Budget = new TransitBudget(() => Usage, timeProvider: Clock);

        public RequestTestClock Clock { get; } = new();

        public TransitUsage Usage { get; set; }

        public TransitBudget Budget { get; }
    }
}
