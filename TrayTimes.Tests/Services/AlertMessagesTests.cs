using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Services;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Services;

/// <summary>A route's service alerts as the rider sees them and Narrator reads them, from the app's English strings.</summary>
[TestClass]
public sealed class AlertMessagesTests
{
    // Thursday, October 29, 2026, in the local time zone, so clock times read as written.
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 29, 8, 0, 0));

    [TestMethod]
    public void SignName_OneAlert_NamesTheRouteAndTheAlert()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string name = AlertMessages.SignName("14", [Alert(title: "Temporary Reroute")]);

        // Assert
        Assert.AreEqual("Route 14 service alert: Temporary Reroute", name);
    }

    [TestMethod]
    public void SignName_SeveralAlerts_CountsThem()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string name = AlertMessages.SignName("Blue", [Alert(), Alert(), Alert()]);

        // Assert
        Assert.AreEqual("3 service alerts for Blue", name);
    }

    [TestMethod]
    public void Heading_NumberedRoute_SaysRouteAndNumber()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string heading = AlertMessages.Heading("14");

        // Assert
        Assert.AreEqual("Route 14 service alerts", heading);
    }

    [TestMethod]
    public void Title_Untitled_FallsBackToTheEffectThenToServiceAlert()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string detour = AlertMessages.Title(Alert(title: string.Empty, effect: AlertEffect.Detour));
        string other = AlertMessages.Title(Alert(title: string.Empty, effect: AlertEffect.OtherEffect));

        // Assert
        Assert.AreEqual("Detour", detour);
        Assert.AreEqual("Service alert", other);
    }

    [TestMethod]
    public void Summary_InEffectUntilLaterToday_SaysWhatWhyAndUntilWhen()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ServiceAlert alert = Alert(effect: AlertEffect.Detour, cause: AlertCause.Construction, periods: [new AlertPeriod(Now.AddHours(-1), Now.AddHours(10))]);

        // Act
        string summary = AlertMessages.Summary(alert, Now);

        // Assert
        Assert.AreEqual($"Detour · Construction · Until {Clock(Now.AddHours(10))}", summary);
    }

    [TestMethod]
    public void Summary_NotYetInEffect_SaysWhenItStarts()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        DateTimeOffset tomorrow = Now.AddHours(21);
        ServiceAlert alert = Alert(effect: AlertEffect.ModifiedService, cause: AlertCause.Holiday, periods: [new AlertPeriod(tomorrow, null)]);

        // Act
        string summary = AlertMessages.Summary(alert, Now);

        // Assert
        Assert.AreEqual($"Modified service · Holiday · Starts Fri {Clock(tomorrow)}", summary);
    }

    [TestMethod]
    public void Summary_UntitledOpenEndedAlert_LeavesTheEffectToTheHeadline()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        ServiceAlert alert = Alert(title: string.Empty, effect: AlertEffect.StopMoved, periods: [new AlertPeriod(Now.AddDays(-3), null)]);

        // Act
        string summary = AlertMessages.Summary(alert, Now);

        // Assert
        Assert.AreEqual(string.Empty, summary);
    }

    [TestMethod]
    public void Summary_EveryNamedEffectAndCause_HasWords()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        AlertEffect[] effects = [.. Enum.GetValues<AlertEffect>().Except([AlertEffect.UnknownEffect, AlertEffect.OtherEffect, AlertEffect.NoEffect])];
        AlertCause[] causes = [.. Enum.GetValues<AlertCause>().Except([AlertCause.UnknownCause, AlertCause.OtherCause])];

        // Act
        string[] summaries = [
            .. effects.Select(e => AlertMessages.Summary(Alert(effect: e), Now)),
            .. causes.Select(c => AlertMessages.Summary(Alert(cause: c), Now))];

        // Assert
        foreach (string summary in summaries)
        {
            Assert.IsFalse(string.IsNullOrEmpty(summary));
            Assert.DoesNotContain("Alert", summary); // A missing string reads as its key, such as "AlertCauseStrike".
        }
    }

    [TestMethod]
    public void Body_SameAsHeadline_IsLeftOut()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();

        // Act
        string repeated = AlertMessages.Body(Alert(title: string.Empty, description: "Detour", effect: AlertEffect.Detour));
        string distinct = AlertMessages.Body(Alert(title: "Detour", description: "Buses detour via Oak St."));

        // Assert
        Assert.AreEqual(string.Empty, repeated);
        Assert.AreEqual("Buses detour via Oak St.", distinct);
    }

    private static ServiceAlert Alert(
        string title = "Temporary Reroute",
        string description = "Buses detour via Oak St.",
        AlertEffect effect = AlertEffect.UnknownEffect,
        AlertCause cause = AlertCause.UnknownCause,
        AlertPeriod[]? periods = null) =>
        new(title, description, AlertSeverity.Warning, effect, cause, periods ?? []);

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.GetCultureInfo("en-US"));
}
