using TrayTimes.Transit;
using Windows.Devices.Geolocation;

namespace TrayTimes.Services;

internal static class LocationService
{
    private const string UnavailableMessage = "Couldn't determine your location. Try again in a moment.";
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RoughMaximumAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RoughTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Requests the device's current location. Returns null (with a user-facing reason) when
    /// access is denied, the position is unavailable, or the lookup times out.
    /// </summary>
    public static async Task<GeoPoint?> TryGetDeviceLocationAsync(TimeSpan timeout, Action<string>? onError = null) =>
        await HasAccessAsync(onError) ? await TryGetPreciseAsync(timeout, onError) : null;

    /// <summary>
    /// Like <see cref="TryGetDeviceLocationAsync"/>, but doesn't make the caller wait for a precise fix:
    /// when a rough position (see <see cref="TryGetRoughAsync"/>) arrives first it's returned along with
    /// <c>Refined</c>, which completes with the precise position (or null if that fails).
    /// </summary>
    public static async Task<(GeoPoint? Point, Task<GeoPoint?>? Refined)> TryGetQuickLocationAsync(TimeSpan timeout, Action<string>? onError = null)
    {
        if (!await HasAccessAsync(onError))
        {
            return (null, null);
        }

        Task<GeoPoint?> precise = TryGetPreciseAsync(timeout, onError);
        Task<GeoPoint?> rough = TryGetRoughAsync();
        if (await Task.WhenAny(precise, rough) == rough && await rough is { } quick)
        {
            return (quick, precise);
        }

        return (await precise, null);
    }

    private static async Task<bool> HasAccessAsync(Action<string>? onError)
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() == GeolocationAccessStatus.Allowed)
            {
                return true;
            }

            onError?.Invoke("Location access is off. Turn it on in Windows Settings > Privacy & security > Location.");
        }
        catch (Exception)
        {
            onError?.Invoke(UnavailableMessage);
        }

        return false;
    }

    private static async Task<GeoPoint?> TryGetPreciseAsync(TimeSpan timeout, Action<string>? onError)
    {
        try
        {
            return await GetPositionAsync(PositionAccuracy.High, MaximumAge, timeout);
        }
        catch (Exception)
        {
            onError?.Invoke(UnavailableMessage);
            return null;
        }
    }

    /// <summary>
    /// Windows' last fix (up to half an hour old) or a quick Wi-Fi/IP one; failing that, the default
    /// location set in Windows Settings. Good enough to pick an area while the precise fix comes in.
    /// </summary>
    private static async Task<GeoPoint?> TryGetRoughAsync()
    {
        try
        {
            return await GetPositionAsync(PositionAccuracy.Default, RoughMaximumAge, RoughTimeout);
        }
        catch (Exception)
        {
            return Geolocator.DefaultGeoposition is { } fallback ? new GeoPoint(fallback.Latitude, fallback.Longitude) : null;
        }
    }

    private static async Task<GeoPoint> GetPositionAsync(PositionAccuracy accuracy, TimeSpan maximumAge, TimeSpan timeout)
    {
        Geolocator locator = new() { DesiredAccuracy = accuracy };
        Geoposition position = await locator.GetGeopositionAsync(maximumAge, timeout);
        BasicGeoposition p = position.Coordinate.Point.Position;
        return new GeoPoint(p.Latitude, p.Longitude);
    }
}
