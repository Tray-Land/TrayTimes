using TrayTimes.Transit;

namespace TrayTimes.Models;

/// <summary>An address search match.</summary>
public sealed record GeocodeResult(string Address, GeoPoint Location)
{
    public override string ToString() => Address;
}
