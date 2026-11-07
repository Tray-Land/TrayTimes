using TrayTimes.Transit;

namespace TrayTimes.Models;

/// <summary>
/// An address or place search match. <see cref="Address"/> is the full one-line text; for a named place,
/// <see cref="Title"/> is its name and <see cref="Subtitle"/> its address, so a list can show them on two lines.
/// </summary>
public sealed record GeocodeResult(string Address, GeoPoint Location, string? Title = null, string? Subtitle = null)
{
    public override string ToString() => Address;
}
