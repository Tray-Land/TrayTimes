using TrayTimes.Models;

namespace TrayTimes.Tests.Models;

[TestClass]
public sealed class TrayTooltipTests
{
    private static readonly string NewLine = Environment.NewLine;

    [TestMethod]
    public void Compose_RoomForSomeOptionalLines_AddsThemInOrderUntilOneDoesNotFit()
    {
        // Arrange: "Stop2 xx" doesn't fit, though "S3" after it would.
        string[] lines = ["Tray Time"];
        string[] optional = ["Stop1", "Stop2 xx", "S3"];
        int maxLength = $"Tray Time{NewLine}Stop1{NewLine}S3".Length;

        // Act
        string tooltip = TrayTooltip.Compose(lines, optional, maxLength);

        // Assert
        Assert.AreEqual($"Tray Time{NewLine}Stop1", tooltip);
    }

    [TestMethod]
    public void Compose_RequiredLinesTooLong_CutsThemShortWithAnEllipsis()
    {
        // Act
        string tooltip = TrayTooltip.Compose(["Tray Times", "Tracking 14 at Oak"], ["Stop1"], maxLength: 20);

        // Assert
        Assert.AreEqual(20, tooltip.Length);
        Assert.IsTrue(tooltip.EndsWith('…'));
        Assert.IsTrue(tooltip.StartsWith($"Tray Times{NewLine}Track", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Compose_EverythingFits_JoinsEveryLine()
    {
        // Act
        string tooltip = TrayTooltip.Compose(["Tray Times", "Work: leave in 6 min"], ["Oak: 4 min", "Elm: Due"]);

        // Assert
        Assert.AreEqual(string.Join(NewLine, "Tray Times", "Work: leave in 6 min", "Oak: 4 min", "Elm: Due"), tooltip);
    }
}
