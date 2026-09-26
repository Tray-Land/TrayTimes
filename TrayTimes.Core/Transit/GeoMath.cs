namespace TrayTimes.Transit;

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle (haversine) distance in meters.</summary>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        double dLat = double.DegreesToRadians(b.Lat - a.Lat);
        double dLon = double.DegreesToRadians(b.Lon - a.Lon);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(double.DegreesToRadians(a.Lat)) * Math.Cos(double.DegreesToRadians(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
