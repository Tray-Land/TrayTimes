using TrayTimes.Transit;
using Windows.Devices.Geolocation;

namespace TrayTimes.Services;

internal static class LocationService
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Requests the device's current location. Returns null (with a user-facing reason) when
    /// access is denied, the position is unavailable, or the lookup times out.
    /// </summary>
    public static async Task<GeoPoint?> TryGetDeviceLocationAsync(TimeSpan timeout, Action<string>? onError = null)
    {
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                onError?.Invoke("Location access is off. Turn it on in Windows Settings > Privacy & security > Location.");
                return null;
            }

            Geolocator locator = new() { DesiredAccuracy = PositionAccuracy.High };
            Geoposition position = await locator.GetGeopositionAsync(MaximumAge, timeout);
            BasicGeoposition p = position.Coordinate.Point.Position;
            return new GeoPoint(p.Latitude, p.Longitude);
        }
        catch (Exception)
        {
            onError?.Invoke("Couldn't determine your location. Try again in a moment.");
            return null;
        }
    }
}
