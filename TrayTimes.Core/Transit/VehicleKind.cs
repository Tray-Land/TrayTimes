namespace TrayTimes.Transit;

/// <summary>What runs on a line, read from its GTFS route_type, so the map can draw the right vehicle.</summary>
public enum VehicleKind
{
    /// <summary>Also coaches, trolleybuses, and any type the app doesn't draw differently.</summary>
    Bus,

    /// <summary>Tram, streetcar, or light rail.</summary>
    Tram,

    /// <summary>Subway, metro, or other rapid transit, such as Chicago's 'L'.</summary>
    Subway,

    /// <summary>Intercity or commuter rail.</summary>
    Rail,

    Ferry,

    /// <summary>Gondola, aerial tramway, or chair lift.</summary>
    AerialLift,

    Funicular,
}

public static class VehicleKinds
{
    /// <summary>Reads a GTFS route_type, basic (0-12) or extended (100-1700); a missing or unknown type is a bus.</summary>
    public static VehicleKind FromRouteType(int? routeType) => routeType switch
    {
        0 or 5 or (>= 900 and < 1000) => VehicleKind.Tram,
        1 or 12 or (>= 400 and < 700) => VehicleKind.Subway,
        2 or (>= 100 and < 200) => VehicleKind.Rail,
        4 or 1000 or 1200 => VehicleKind.Ferry,
        6 or (>= 1300 and < 1400) => VehicleKind.AerialLift,
        7 or 1400 => VehicleKind.Funicular,
        _ => VehicleKind.Bus,
    };
}
