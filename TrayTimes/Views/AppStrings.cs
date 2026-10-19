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
}
