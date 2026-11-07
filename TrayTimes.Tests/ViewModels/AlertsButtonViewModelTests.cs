using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Transit;
using TrayTimes.ViewModels;

namespace TrayTimes.Tests.ViewModels;

[TestClass]
public sealed class AlertsButtonViewModelTests
{
    [TestMethod]
    public void Update_NoAlerts_HidesTheSign()
    {
        // Arrange
        AlertsButtonViewModel viewModel = new();

        // Act
        viewModel.Update(null, "14");

        // Assert
        Assert.IsFalse(viewModel.IsVisible);
    }

    [TestMethod]
    public void Update_SevereAlert_ShowsTheCriticalSign()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        AlertsButtonViewModel viewModel = new();

        // Act
        viewModel.Update([Alert(AlertSeverity.Severe)], "14");

        // Assert
        Assert.IsTrue(viewModel.IsVisible);
        Assert.IsTrue(viewModel.IsSevere);
        Assert.IsFalse(viewModel.ShowsCaution);
        Assert.AreEqual("Route 14 service alert: Detour", viewModel.SignName);
    }

    [TestMethod]
    public void Update_WarningAlert_ShowsTheCautionSign()
    {
        // Arrange
        AlertsButtonViewModel viewModel = new();

        // Act
        viewModel.Update([Alert(AlertSeverity.Warning)], "14");

        // Assert
        Assert.IsFalse(viewModel.IsSevere);
        Assert.IsTrue(viewModel.ShowsCaution);
    }

    [TestMethod]
    public void RefreshEntries_AlertsShown_ListsOneEntryPerAlert()
    {
        // Arrange
        using IDisposable english = ResourceLoader.UseEnglish();
        AlertsButtonViewModel viewModel = new();
        viewModel.Update([Alert(AlertSeverity.Warning), Alert(AlertSeverity.Severe)], "14");

        // Act
        viewModel.RefreshEntries(DateTimeOffset.Now);

        // Assert
        Assert.AreEqual(2, viewModel.Entries.Count);
        Assert.AreEqual("Detour", viewModel.Entries[0].Title);
        Assert.IsTrue(viewModel.Entries[0].ShowsBody);
    }

    private static ServiceAlert Alert(AlertSeverity severity) =>
        new("Detour", "Buses detour via Oak St.", severity, AlertEffect.UnknownEffect, AlertCause.UnknownCause, []);
}
