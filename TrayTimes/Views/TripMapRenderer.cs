using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Commute;
using TrayTimes.Models;
using TrayTimes.Transit;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TrayTimes.Views;

/// <summary>
/// Draws only the selected itinerary's ridden shapes, walks, and boarding/alighting pins. During a trip, the
/// leg the rider is on can be drawn bolder and the rest fainter (<see cref="Emphasize"/>).
/// </summary>
internal sealed class TripMapRenderer(
    MapElementsLayer routes,
    MapElementsLayer stops,
    MapElementsLayer places,
    FontIcon stopGlyph,
    Dictionary<MapIcon, string> stopPins)
{
    private const double RideThickness = 6;
    private const double WalkThickness = 3;
    private const double EmphasisExtra = 3;
    private const byte FadedAlpha = 110;

    private readonly List<(MapPolyline Line, int Leg, Color Color, double Thickness)> _paths = [];
    private TripItinerary? _itinerary;
    private RideLine[] _lines = [];
    private CommuteLeg? _leg;
    private int _emphasized = -1;

    public IReadOnlyList<GeoPoint> Points { get; private set; } = [];

    public void Invalidate() => _itinerary = null;

    /// <returns>True when the geometry changed, so a new selection can frame it once.</returns>
    public bool Draw(LiveTrip trip, CommuteLeg leg)
    {
        RideLine[] lines = [.. trip.Legs.OfType<LiveRide>().Select(r => r.Line)];
        if (ReferenceEquals(_itinerary, trip.Itinerary) && _leg == leg && _lines.SequenceEqual(lines))
        {
            return false;
        }

        _itinerary = trip.Itinerary;
        _lines = lines;
        _leg = leg;
        _emphasized = -1;
        _paths.Clear();
        routes.MapElements.Clear();
        stops.MapElements.Clear();
        places.MapElements.Clear();
        stopPins.Clear();
        List<GeoPoint> points = [leg.Origin, leg.To.Location];
        Color walkColor = ((SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]).Color;
        for (int index = 0; index < trip.Legs.Count; index++)
        {
            if (trip.Legs[index] is LiveWalk walk)
            {
                AddPath(index, walk.Walk.Path, walkColor, WalkThickness, dashed: true);
            }
            else if (trip.Legs[index] is LiveRide ride)
            {
                AddPath(index, ride.Line.Path, MapHelpers.ParseRouteColor(ride.Departure?.RouteColor ?? ride.Line.Route.Color), RideThickness, dashed: false);
                AddStop(ride.Line.BoardAt);
                if (ride.Line.AlightAt is { } alight)
                {
                    AddStop(alight);
                }
            }
        }

        AddPlace(leg.Origin, leg.From?.Kind);
        AddPlace(leg.To.Location, leg.To.Kind);
        Points = points;
        return true;

        void AddPath(int index, IReadOnlyList<GeoPoint> path, Color color, double thickness, bool dashed)
        {
            points.AddRange(path);
            if (path.Count >= 2)
            {
                MapPolyline line = new()
                {
                    Path = new Geopath(path.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
                    StrokeColor = color,
                    StrokeThickness = thickness,
                    StrokeDashed = dashed,
                };
                _paths.Add((line, index, color, thickness));
                routes.MapElements.Add(line);
            }
        }

        void AddStop(TransitStop stop)
        {
            points.Add(stop.Location);
            MapIcon pin = new(stopGlyph, MapHelpers.ToGeopoint(stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
            stopPins[pin] = stop.Name;
            stops.MapElements.Add(pin);
        }

        void AddPlace(GeoPoint point, PlaceKind? kind)
        {
            FontIcon icon = MapHelpers.CreatePlaceIcon(kind, 26);
            places.MapElements.Add(new MapIcon(icon, MapHelpers.ToGeopoint(point)) { NormalizedAnchorPoint = new Point(0.5, 0.5) });
        }
    }

    /// <summary>
    /// Draws the leg at <paramref name="leg"/> (an index in the drawn trip's legs) bolder and the others fainter;
    /// a leg with no path drawn (or -1) draws them all alike.
    /// </summary>
    public void Emphasize(int leg)
    {
        if (!_paths.Any(p => p.Leg == leg))
        {
            leg = -1;
        }

        if (leg == _emphasized)
        {
            return;
        }

        _emphasized = leg;
        foreach ((MapPolyline line, int index, Color color, double thickness) in _paths)
        {
            bool current = index == leg;
            line.StrokeThickness = current ? thickness + EmphasisExtra : thickness;
            line.StrokeColor = leg < 0 || current ? color : Color.FromArgb(Math.Min(color.A, FadedAlpha), color.R, color.G, color.B);
        }
    }
}
