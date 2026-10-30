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
}
