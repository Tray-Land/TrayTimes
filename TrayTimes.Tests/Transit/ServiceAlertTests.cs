using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class ServiceAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void IsCurrent_NoPeriods_IsActiveIndefinitely()
    {
        // Arrange
        ServiceAlert alert = Alert();

        // Act
        bool current = alert.IsCurrent(Now);

        // Assert
        Assert.IsTrue(current);
        Assert.IsTrue(alert.IsActive(Now.AddYears(5)));
        Assert.IsNull(alert.PeriodAt(Now));
    }

    [TestMethod]
    public void IsCurrent_OpenEndedPeriods_ApplyFromStartOnward()
    {
        // Arrange
        ServiceAlert alert = Alert(new AlertPeriod(Now.AddHours(-1), null));
        ServiceAlert always = Alert(new AlertPeriod(null, null));

        // Act
        bool current = alert.IsCurrent(Now);

        // Assert
        Assert.IsTrue(current);
        Assert.IsTrue(always.IsActive(Now));
        Assert.IsFalse(alert.IsActive(Now.AddHours(-2)));
    }

    [TestMethod]
    public void IsCurrent_StartsWithinLookahead_IsShownButNotYetActive()
    {
        // Arrange
        ServiceAlert alert = Alert(new AlertPeriod(Now.AddHours(20), Now.AddHours(30)));

        // Act
        bool current = alert.IsCurrent(Now);

        // Assert
        Assert.IsTrue(current);
        Assert.IsFalse(alert.IsActive(Now));
    }

    [TestMethod]
    public void IsCurrent_StartsBeyondLookahead_IsNotShown()
    {
        // Act
        bool current = Alert(new AlertPeriod(Now + ServiceAlert.Lookahead + TimeSpan.FromMinutes(1), null)).IsCurrent(Now);

        // Assert
        Assert.IsFalse(current);
    }

    [TestMethod]
    public void IsCurrent_EveryPeriodEnded_IsNotShown()
    {
        // Arrange
        ServiceAlert alert = Alert(new AlertPeriod(Now.AddHours(-5), Now.AddHours(-3)), new AlertPeriod(null, Now));

        // Act
        bool current = alert.IsCurrent(Now);

        // Assert
        Assert.IsFalse(current);
        Assert.IsNull(alert.PeriodAt(Now));
    }

    [TestMethod]
    public void PeriodAt_SeveralPeriods_ReturnsTheOneInEffectElseTheSoonestToStart()
    {
        // Arrange
        AlertPeriod tonight = new(Now.AddHours(8), Now.AddHours(12));
        AlertPeriod tomorrowNight = new(Now.AddHours(32), Now.AddHours(36));
        ServiceAlert alert = Alert(tomorrowNight, tonight);

        // Act
        AlertPeriod? before = alert.PeriodAt(Now);
        AlertPeriod? during = alert.PeriodAt(Now.AddHours(9));
        AlertPeriod? between = alert.PeriodAt(Now.AddHours(20));

        // Assert
        Assert.AreEqual(tonight, before);
        Assert.AreEqual(tonight, during);
        Assert.AreEqual(tomorrowNight, between);
    }

    private static ServiceAlert Alert(params AlertPeriod[] periods) =>
        new("Detour", "Buses detour via Oak St.", AlertSeverity.Warning, AlertEffect.Detour, AlertCause.Construction, periods);
}
