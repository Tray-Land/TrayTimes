using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class RouteNamesTests
{
    [TestMethod]
    public void IsNumber_NumberOnly_IsTrue()
    {
        // Act
        bool isNumber = RouteNames.IsNumber("15");

        // Assert
        Assert.IsTrue(isNumber);
    }

    [TestMethod]
    [DataRow("30X")]
    [DataRow("Green")]
    [DataRow("")]
    public void IsNumber_NotJustANumber_IsFalse(string displayId)
    {
        // Act
        bool isNumber = RouteNames.IsNumber(displayId);

        // Assert
        Assert.IsFalse(isNumber);
    }

    [TestMethod]
    [DataRow("Brown", "BRN")]
    [DataRow("Purple", "PRP")]
    [DataRow("Orange", "ORN")]
    [DataRow("Green", "GRN")]
    [DataRow("Silver", "SLV")]
    [DataRow("Express", "EXP")]
    [DataRow("Verte", "VRT")]
    public void Short_LongWordName_IsThreeCapitalsFromItsConsonants(string displayId, string expected)
    {
        // Act
        string shortName = RouteNames.Short(displayId);

        // Assert
        Assert.AreEqual(expected, shortName);
    }

    [TestMethod]
    [DataRow("Yellow", "YEL")]
    [DataRow("Bleue", "BLE")]
    public void Short_TooFewConsonants_IsItsFirstThreeLetters(string displayId, string expected)
    {
        // Act
        string shortName = RouteNames.Short(displayId);

        // Assert
        Assert.AreEqual(expected, shortName);
    }

    [TestMethod]
    [DataRow("PurpleLine", "PRP")]
    [DataRow("Brown Line", "BRN")]
    [DataRow("BlueLine", "Blue")]
    [DataRow("M-Line", "M")]
    public void Short_TrailingLine_IsDropped(string displayId, string expected)
    {
        // Act
        string shortName = RouteNames.Short(displayId);

        // Assert
        Assert.AreEqual(expected, shortName);
    }

    [TestMethod]
    [DataRow("Red")]
    [DataRow("Pink")]
    [DataRow("151")]
    [DataRow("J14")]
    [DataRow("UP-NW")]
    [DataRow("Line")]
    [DataRow("")]
    public void Short_FitsOrIsNotAWord_IsUnchanged(string displayId)
    {
        // Act
        string shortName = RouteNames.Short(displayId);

        // Assert
        Assert.AreEqual(displayId, shortName);
    }
}
