namespace TrayTimes.Models;

/// <summary>A map camera position: center and Web Mercator zoom level.</summary>
public sealed record MapView(double Lat, double Lon, double Zoom);
