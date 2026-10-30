using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class AlertBookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void For_RecordedRoute_ListsCurrentAlertsMostSevereFirstThenInEffectBeforeUpcoming()
    {
        // Arrange
        ServiceAlert upcoming = Alert("Holiday schedule", AlertSeverity.Warning, new AlertPeriod(Now.AddHours(12), null));
        ServiceAlert info = Alert("Elevator out", AlertSeverity.Info);
        ServiceAlert warning = Alert("Detour", AlertSeverity.Warning);
        ServiceAlert severe = Alert("No service", AlertSeverity.Severe);
        ServiceAlert ended = Alert("Old detour", AlertSeverity.Severe, new AlertPeriod(null, Now.AddHours(-1)));
        ServiceAlert later = Alert("Track work next week", AlertSeverity.Warning, new AlertPeriod(Now.AddDays(7), null));
        AlertBook book = new();
        book.Record(Routes(("R:14", [upcoming, info, warning, severe, ended, later])));

        // Act
        IReadOnlyList<ServiceAlert> alerts = book.For("r:14", Now);

        // Assert
        CollectionAssert.AreEqual(new[] { severe, warning, upcoming, info }, alerts.ToArray());
    }

    [TestMethod]
    public void Record_RouteListedWithoutAlerts_ClearsItsAlertsAndKeepsOtherRoutes()
    {
        // Arrange
        AlertBook book = new();
        book.Record(Routes(("R:14", [Alert("Detour")]), ("R:30", [Alert("Stop moved")])));

        // Act
        book.Record(Routes(("R:14", [])));

        // Assert
        Assert.IsEmpty(book.For("R:14", Now));
        Assert.AreEqual("Stop moved", book.For("R:30", Now).Single().Title);
    }

    [TestMethod]
    public void For_UnseenRoute_IsEmpty()
    {
        // Act
        IReadOnlyList<ServiceAlert> alerts = new AlertBook().For("R:99", Now);

        // Assert
        Assert.IsEmpty(alerts);
    }

    [TestMethod]
    public void Record_PastRetainedRoutes_ForgetsTheLeastRecentlyRecorded()
    {
        // Arrange
        AlertBook book = new();
        book.Record(Routes(("R:first", [Alert("Detour")])));

        // Act
        for (int i = 0; i < AlertBook.RetainedRoutes; i++)
        {
            book.Record(Routes(($"R:{i}", [Alert("Detour")])));
        }

        // Assert
        Assert.IsEmpty(book.For("R:first", Now));
        Assert.HasCount(1, book.For("R:0", Now));
    }

    private static ServiceAlert Alert(string title, AlertSeverity severity = AlertSeverity.Warning, params AlertPeriod[] periods) =>
        new(title, $"{title}.", severity, AlertEffect.UnknownEffect, AlertCause.UnknownCause, periods);

    private static Dictionary<string, IReadOnlyList<ServiceAlert>> Routes(params (string RouteId, ServiceAlert[] Alerts)[] routes) =>
        routes.ToDictionary(r => r.RouteId, r => (IReadOnlyList<ServiceAlert>)r.Alerts);
}
