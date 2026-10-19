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

    /// <summary>Initial compass bearing from <paramref name="from"/> to <paramref name="to"/>, in whole degrees clockwise from north (0-359).</summary>
    public static int BearingDegrees(GeoPoint from, GeoPoint to)
    {
        double lat1 = double.DegreesToRadians(from.Lat);
        double lat2 = double.DegreesToRadians(to.Lat);
        double dLon = double.DegreesToRadians(to.Lon - from.Lon);
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
        double degrees = double.RadiansToDegrees(Math.Atan2(y, x));
        return (int)Math.Round((degrees + 360) % 360) % 360;
    }

    /// <summary>
    /// Decodes a Google encoded polyline (precision 1e5). Returns the points read before any
    /// malformed input, so a damaged shape draws partially rather than failing.
    /// </summary>
    public static IReadOnlyList<GeoPoint> DecodePolyline(string? encoded)
    {
        List<GeoPoint> points = [];
        if (string.IsNullOrEmpty(encoded))
        {
            return points;
        }

        int index = 0;
        long lat = 0;
        long lon = 0;
        while (index < encoded.Length)
        {
            if (!TryReadValue(encoded, ref index, out long dLat) || !TryReadValue(encoded, ref index, out long dLon))
            {
                break;
            }

            lat += dLat;
            lon += dLon;
            points.Add(new GeoPoint(lat / 1e5, lon / 1e5));
        }

        return points;
    }

    /// <summary>Encodes points as a Google encoded polyline (precision 1e5), the inverse of <see cref="DecodePolyline"/>.</summary>
    public static string EncodePolyline(IEnumerable<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        System.Text.StringBuilder encoded = new();
        long lastLat = 0;
        long lastLon = 0;
        foreach (GeoPoint point in points)
        {
            long lat = (long)Math.Round(point.Lat * 1e5);
            long lon = (long)Math.Round(point.Lon * 1e5);
            WriteValue(encoded, lat - lastLat);
            WriteValue(encoded, lon - lastLon);
            lastLat = lat;
            lastLon = lon;
        }

        return encoded.ToString();
    }

    private static void WriteValue(System.Text.StringBuilder encoded, long value)
    {
        ulong shifted = value < 0 ? ~((ulong)value << 1) : (ulong)value << 1;
        while (shifted >= 0x20)
        {
            encoded.Append((char)((int)((shifted & 0x1f) | 0x20) + 63));
            shifted >>= 5;
        }

        encoded.Append((char)((int)shifted + 63));
    }

    private static bool TryReadValue(string encoded, ref int index, out long value)
    {
        long result = 0;
        int shift = 0;
        while (index < encoded.Length && shift < 64)
        {
            int b = encoded[index++] - 63;
            if (b is < 0 or > 63)
            {
                break;
            }

            result |= (long)(b & 0x1f) << shift;
            shift += 5;
            if (b < 0x20)
            {
                value = (result & 1) != 0 ? ~(result >> 1) : result >> 1;
                return true;
            }
        }

        value = 0;
        return false;
    }
}
