namespace TrayTimes.Models;

/// <summary>A map camera position: center, Web Mercator zoom level, heading, and pitch.</summary>
public sealed record MapView(double Lat, double Lon, double Zoom, double Heading = 0, double Pitch = 0);
