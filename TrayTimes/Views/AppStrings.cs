using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace TrayTimes.Views;

/// <summary>The app's strings, from Strings/en-us/Resources.resw (and its translations, when there are any).</summary>
internal static class AppStrings
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key) => Loader.GetString(key);

    /// <summary>The format string <paramref name="key"/>, filled in for the current culture.</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Loader.GetString(key), args);

    /// <summary>Feet and miles where the region measures that way, else meters and kilometers.</summary>
    public static string Distance(double meters)
    {
        if (RegionInfo.CurrentRegion.IsMetric)
        {
            return meters < 1000
                ? Format("DistanceMetersFormat", Math.Max(50, Math.Round(meters / 50) * 50))
                : Format("DistanceKilometersFormat", meters / 1000);
        }

        double miles = meters / 1609.344;
        return miles < 0.1
            ? Format("DistanceFeetFormat", Math.Max(50, Math.Round(meters * 3.28084 / 50) * 50))
            : Format("DistanceMilesFormat", miles);
    }
}
