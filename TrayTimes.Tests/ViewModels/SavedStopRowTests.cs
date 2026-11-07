using TrayTimes.Models;
using TrayTimes.Tests.Models;
using TrayTimes.ViewModels;

namespace TrayTimes.Tests.ViewModels;

[TestClass]
public sealed class SavedStopRowTests
{
    private static SavedStopRow CreateRow(SavedStop stop, Action<SavedStopRow>? remove = null, Action<SavedStopRow, string?>? setNickname = null) =>
        new(stop, remove ?? (_ => { }), setNickname ?? ((_, _) => { }));

    [TestMethod]
    public void Rename_NewNickname_UpdatesTheStopInPlaceAndRaisesNickname()
    {
        // Arrange
        SavedStopRow row = CreateRow(SavedStopTests.CreateStop("100"));
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
        SavedStopRow row = CreateRow(SavedStopTests.CreateStop("100") with { Nickname = "Home stop" });

        // Act
        row.Rename(null);

        // Assert
        Assert.AreEqual(string.Empty, row.Nickname);
        Assert.IsNull(row.Stop.Nickname);
    }

    [TestMethod]
    public void SaveNicknameCommand_ChangedText_ReportsTrimmedNickname()
    {
        // Arrange
        List<string?> saved = [];
        SavedStopRow row = CreateRow(SavedStopTests.CreateStop("100"), setNickname: (_, n) => saved.Add(n));

        // Act
        row.SaveNicknameCommand.Execute("  Work stop ");

        // Assert
        CollectionAssert.AreEqual(new string?[] { "Work stop" }, saved);
    }

    [TestMethod]
    public void SaveNicknameCommand_UnchangedOrBlankText_ReportsNothingOrClears()
    {
        // Arrange
        List<string?> saved = [];
        SavedStopRow row = CreateRow(SavedStopTests.CreateStop("100") with { Nickname = "Home" }, setNickname: (_, n) => saved.Add(n));

        // Act
        row.SaveNicknameCommand.Execute("Home");
        row.SaveNicknameCommand.Execute("   ");

        // Assert
        CollectionAssert.AreEqual(new string?[] { null }, saved);
    }

    [TestMethod]
    public void RemoveCommand_Executed_ReportsTheRow()
    {
        // Arrange
        SavedStopRow? removed = null;
        SavedStopRow row = CreateRow(SavedStopTests.CreateStop("100"), remove: r => removed = r);

        // Act
        row.RemoveCommand.Execute(null);

        // Assert
        Assert.AreSame(row, removed);
    }
}
