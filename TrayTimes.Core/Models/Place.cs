namespace TrayTimes.Models;

/// <summary>A commuter anchor such as home or work.</summary>
public sealed record Place(string Address, double Lat, double Lon);
