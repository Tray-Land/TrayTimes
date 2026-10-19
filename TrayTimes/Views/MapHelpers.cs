using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using WinUIEx.Maps;
using MapControl = WinUIEx.Maps.MapControl;
using MapIcon = WinUIEx.Maps.MapIcon;

namespace TrayTimes.Views;

/// <summary>Map, route-color, and place-glyph helpers shared by the flyout and the settings window.</summary>
internal static class MapHelpers
{
    public const string BusEmoji = "🚍"; // Oncoming bus, in color so it stands out on the basemap
    public const string SavedStopGlyph = ""; // FavoriteStarFill
    public const string StopGlyph = ""; // CircleFill

    private static readonly Color FallbackRouteColor = Color.FromArgb(255, 0, 90, 158);
    private static readonly TimeSpan MapRevealTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MapRevealFallbackDelay = TimeSpan.FromMilliseconds(2500);

    public static Geopoint ToGeopoint(GeoPoint p) => new(new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon });

    public static Geopoint ToGeopoint(SavedStop s) => ToGeopoint(new GeoPoint(s.Lat, s.Lon));

    /// <summary>Parses a "#rrggbb" route color, falling back to a neutral transit blue.</summary>
    public static Color ParseRouteColor(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#'
            && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            Color color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            // Agencies sometimes use white or black as "no color"; neither shows up on a badge or map.
            return IsNearWhite(color) || IsNearBlack(color) ? FallbackRouteColor : color;
        }

        return FallbackRouteColor;
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    public static Color ContrastingText(Color background)
    {
        // WCAG relative luminance, compared against the black/white crossover point.
        static double Channel(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double luminance = (0.2126 * Channel(background.R)) + (0.7152 * Channel(background.G)) + (0.0722 * Channel(background.B));
        return luminance > 0.179 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
    }

    /// <summary>A favorite place's glyph, by its kind; with no place, wherever the rider is.</summary>
    public static string PlaceGlyph(PlaceKind? kind) => kind switch
    {
        PlaceKind.Home => "", // HomeSolid
        PlaceKind.Work => "", // Solid briefcase
        PlaceKind.School => "", // Education
        PlaceKind.Custom => "", // Pin
        _ => "", // Location
    };

    public static FontIcon CreateGlyph(string glyph, Color color, double size) => new()
    {
        Glyph = glyph,
        FontSize = size,
        Foreground = new SolidColorBrush(color),
    };

    /// <summary>A full-color emoji map icon (FontIcon's default icon font has no emoji).</summary>
    public static FontIcon CreateEmoji(string emoji, double size) => new()
    {
        Glyph = emoji,
        FontSize = size,
        FontFamily = new FontFamily("Segoe UI Emoji"),
    };

    public static FontIcon CreatePlaceIcon(PlaceKind? kind, double size) => CreateEmoji(kind switch
    {
        PlaceKind.Home => "🏠",
        PlaceKind.Work => "💼",
        PlaceKind.School => "🎓",
        PlaceKind.Custom => "📌",
        _ => "📍",
    }, size);

    public static void UpdateVehicleArrow(MapIcon arrow, int heading, double mapHeading, bool selected, bool darkMap)
    {
        arrow.IsVisible = heading is >= 0 and < 360;
        if (!arrow.IsVisible)
        {
            return;
        }

        double angle = heading - mapHeading;
        double radians = double.DegreesToRadians(angle);
        double distance = selected ? 29 : 24;
        arrow.IconElement.RenderTransform = new RotateTransform { Angle = angle };
        arrow.IconElement.Foreground = new SolidColorBrush(darkMap ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        arrow.NormalizedAnchorPoint = new Point(
            0.5 - (Math.Sin(radians) * distance / 32),
            0.5 + (Math.Cos(radians) * distance / 32));
    }

    /// <summary>
    /// Recolors a stop glyph to the accent shade WinUI uses for filled accent surfaces in the
    /// current theme. Map icons are unparented, so theme resources won't track the theme on their own.
    /// </summary>
    public static void ApplyStopColor(FontIcon glyph, FrameworkElement themeSource)
    {
        string key = themeSource.ActualTheme == ElementTheme.Dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1";
        glyph.Foreground = new SolidColorBrush((Color)Application.Current.Resources[key]);
    }

    /// <summary>Picks a basemap that matches the app theme, or a blank one without an Azure Maps key.</summary>
    public static void ApplyBasemap(MapControl map, FrameworkElement themeSource, FrameworkElement? cover)
    {
        if (Secrets.AzureMapsKey is not { } key)
        {
            map.MapStyle = MapStyle.Blank;
            if (cover is not null)
            {
                cover.RequestedTheme = ElementTheme.Light;
            }

            return;
        }

        map.MapServiceToken = key;
        bool dark = themeSource.ActualTheme == ElementTheme.Dark;
        map.MapStyle = dark ? MapStyle.Night : MapStyle.Road;
        if (cover is not null)
        {
            // Match the cover to the basemap's tone so the reveal doesn't flash either way.
            cover.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        }
    }

    /// <summary>
    /// Waits until the map has drawn the tiles for its current view, or a timeout. WinUIEx.Maps
    /// clears to light gray until tiles arrive and has no public tile-loaded event; its
    /// CaptureRenderedFrameAsync waits for the current tile wave, so use it when present.
    /// </summary>
    public static async Task WaitForFirstFrameAsync(MapControl map)
    {
        using CancellationTokenSource timeout = new(MapRevealTimeout);
        try
        {
            MethodInfo? capture = typeof(MapControl).GetMethod("CaptureRenderedFrameAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (capture?.Invoke(map, [timeout.Token]) is Task frame)
            {
                await frame.WaitAsync(timeout.Token);
                return;
            }

            await Task.Delay(MapRevealFallbackDelay);
        }
        catch (Exception ex)
        {
            // Timed out (e.g. offline) or the API changed: reveal whatever is there.
            System.Diagnostics.Debug.WriteLine($"Map reveal wait ended early: {ex.Message}");
        }
    }

    /// <summary>Frames the given points, or centers on a single point at street level.</summary>
    public static Task FrameAsync(MapControl map, IReadOnlyCollection<GeoPoint> points, MapAnimationKind animation, double singlePointZoom = 15)
    {
        if (points.Count == 0)
        {
            return Task.CompletedTask;
        }

        double north = points.Max(p => p.Lat), south = points.Min(p => p.Lat);
        double west = points.Min(p => p.Lon), east = points.Max(p => p.Lon);

        // Close-together points would zoom in absurdly far; treat them as one.
        if (north - south < 0.002 && east - west < 0.002)
        {
            GeoPoint center = new((north + south) / 2, (east + west) / 2);
            return map.TrySetViewAsync(ToGeopoint(center), singlePointZoom, 0, 0, animation);
        }

        GeoboundingBox box = new(
            new BasicGeoposition { Latitude = north, Longitude = west },
            new BasicGeoposition { Latitude = south, Longitude = east });
        return map.TrySetViewBoundsAsync(box, new Thickness(40), animation);
    }

    private static bool IsNearWhite(Color c) => c.R > 235 && c.G > 235 && c.B > 235;

    private static bool IsNearBlack(Color c) => c.R < 20 && c.G < 20 && c.B < 20;
}
