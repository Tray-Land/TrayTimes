using Microsoft.UI.Xaml.Controls;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TrayTimes.Views;

/// <summary>Draws one direction of a bus line: its route in the line's color and a pin at each of its stops.</summary>
internal sealed class LineMapRenderer(
    MapElementsLayer routes,
    MapElementsLayer stops,
    Dictionary<MapIcon, string> stopPins)
{
    private const double LineThickness = 7;
    private const byte LineAlpha = 200;
    private const double StopDotSize = 10;

    /// <summary>Everything drawn, to frame it by.</summary>
    public IReadOnlyList<GeoPoint> Points { get; private set; } = [];

    public void Clear()
    {
        routes.MapElements.Clear();
        stops.MapElements.Clear();
        stopPins.Clear();
        Points = [];
    }

    public void Draw(LineMap map, Color color)
    {
        Clear();
        RoutePattern[] patterns = [.. map.Details.Shapes.Where(s => string.Equals(s.DirectionId, map.DirectionId, StringComparison.OrdinalIgnoreCase))];
        if (patterns.Length == 0)
        {
            patterns = [.. map.Details.Shapes]; // The feed's shapes don't say which way they go.
        }

        List<GeoPoint> points = [];
        foreach (RoutePattern pattern in patterns)
        {
            points.AddRange(pattern.Points);
            routes.MapElements.Add(new MapPolyline
            {
                Path = new Geopath(pattern.Points.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
                StrokeColor = Color.FromArgb(LineAlpha, color.R, color.G, color.B),
                StrokeThickness = LineThickness,
            });
        }

        // A line has dozens of stops: small dots in its own color keep them from hiding the route at a distance.
        FontIcon dot = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, color, StopDotSize);
        foreach (TransitStop stop in map.Stops)
        {
            points.Add(stop.Location);
            MapIcon pin = new(dot, MapHelpers.ToGeopoint(stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
            stopPins[pin] = stop.Name;
            stops.MapElements.Add(pin);
        }

        Points = points;
    }
}
