using TrayTimes.Tests.Models;
using TrayTimes.Views;

namespace TrayTimes.Tests.Views;

[TestClass]
public sealed class SavedStopRowTests
{
    [TestMethod]
    public void Rename_NewNickname_UpdatesTheStopInPlaceAndRaisesNickname()
    {
        // Arrange
        SavedStopRow row = new(SavedStopTests.CreateStop("100"));
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        row.Rename("Work stop");
        row.Rename("Work stop");

        // Assert
        Assert.AreEqual("Work stop", row.Nickname);
        Assert.AreEqual("Work stop", row.Stop.Nickname);
        CollectionAssert.AreEqual(new[] { nameof(SavedStopRow.Nickname) }, changed);
    }

    [TestMethod]
    public void Nickname_None_IsEmpty()
    {
        // Arrange
        SavedStopRow row = new(SavedStopTests.CreateStop("100") with { Nickname = "Home stop" });

        // Act
        row.Rename(null);

        // Assert
        Assert.AreEqual(string.Empty, row.Nickname);
        Assert.IsNull(row.Stop.Nickname);
    }
}
