using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class VehicleKindsTests
{
    [TestMethod]
    [DataRow(0, VehicleKind.Tram)]
    [DataRow(1, VehicleKind.Subway)]
    [DataRow(2, VehicleKind.Rail)]
    [DataRow(3, VehicleKind.Bus)]
    [DataRow(4, VehicleKind.Ferry)]
    [DataRow(5, VehicleKind.Tram)]
    [DataRow(6, VehicleKind.AerialLift)]
    [DataRow(7, VehicleKind.Funicular)]
    [DataRow(11, VehicleKind.Bus)]
    [DataRow(12, VehicleKind.Subway)]
    public void FromRouteType_BasicType_IsItsKind(int routeType, VehicleKind expected)
    {
        // Act
        VehicleKind kind = VehicleKinds.FromRouteType(routeType);

        // Assert
        Assert.AreEqual(expected, kind);
    }

    [TestMethod]
    [DataRow(109, VehicleKind.Rail)]
    [DataRow(401, VehicleKind.Subway)]
    [DataRow(700, VehicleKind.Bus)]
    [DataRow(900, VehicleKind.Tram)]
    [DataRow(1200, VehicleKind.Ferry)]
    [DataRow(1300, VehicleKind.AerialLift)]
    [DataRow(1400, VehicleKind.Funicular)]
    public void FromRouteType_ExtendedType_IsItsKind(int routeType, VehicleKind expected)
    {
        // Act
        VehicleKind kind = VehicleKinds.FromRouteType(routeType);

        // Assert
        Assert.AreEqual(expected, kind);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(-1)]
    [DataRow(99)]
    public void FromRouteType_MissingOrUnknown_IsBus(int? routeType)
    {
        // Act
        VehicleKind kind = VehicleKinds.FromRouteType(routeType);

        // Assert
        Assert.AreEqual(VehicleKind.Bus, kind);
    }
}
