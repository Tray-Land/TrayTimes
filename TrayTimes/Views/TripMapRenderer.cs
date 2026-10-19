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

/// <summary>Draws only the selected itinerary's ridden shapes, walks, and boarding/alighting pins.</summary>
internal sealed class TripMapRenderer(
    MapElementsLayer routes,
    MapElementsLayer stops,
    MapElementsLayer places,
    FontIcon stopGlyph,
    Dictionary<MapIcon, string> stopPins)
{
    private TripItinerary? _itinerary;
    private RideLine[] _lines = [];
    private CommuteLeg? _leg;

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
        routes.MapElements.Clear();
        stops.MapElements.Clear();
        places.MapElements.Clear();
        stopPins.Clear();
        List<GeoPoint> points = [leg.Origin, leg.To.Location];
        Color walkColor = ((SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]).Color;
        foreach (LiveLeg part in trip.Legs)
        {
            if (part is LiveWalk walk)
            {
                AddPath(walk.Walk.Path, walkColor, 3, dashed: true);
            }
            else if (part is LiveRide ride)
            {
                AddPath(ride.Line.Path, MapHelpers.ParseRouteColor(ride.Departure?.RouteColor ?? ride.Line.Route.Color), 6, dashed: false);
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

        void AddPath(IReadOnlyList<GeoPoint> path, Color color, double thickness, bool dashed)
        {
            points.AddRange(path);
            if (path.Count >= 2)
            {
                routes.MapElements.Add(new MapPolyline
                {
                    Path = new Geopath(path.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
                    StrokeColor = color,
                    StrokeThickness = thickness,
                    StrokeDashed = dashed,
                });
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
}
