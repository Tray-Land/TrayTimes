using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Transit;
using Windows.Devices.Enumeration;
using Windows.Devices.Geolocation;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace TrayTimes.Services;

[SupportedOSPlatform("windows10.0.17763")]
internal static class LocationService
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RoughMaximumAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RoughTimeout = TimeSpan.FromSeconds(2);

    [SupportedOSPlatformGuard("windows10.0.18362")]
    private static bool HasAppCapability =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
        && ApiInformation.IsTypePresent("Windows.Security.Authorization.AppCapabilityAccess.AppCapability");

    /// <summary>
    /// Reads the device's current location without prompting for permission. Returns null when
    /// access is denied, the position is unavailable, or the lookup times out.
    /// </summary>
    public static async Task<GeoPoint?> TryGetDeviceLocationAsync(
        TimeSpan timeout, Action<string>? onError = null, Action? onPermissionRequired = null) =>
        await HasAccessAsync(onError, onPermissionRequired)
            ? await TryGetPreciseAsync(timeout, onError, onPermissionRequired)
            : null;

    /// <summary>
    /// Like <see cref="TryGetDeviceLocationAsync"/>, but doesn't make the caller wait for a precise fix:
    /// when a rough position (see <see cref="TryGetRoughAsync"/>) arrives first it's returned along with
    /// <c>Refined</c>, which completes with the precise position (or null if that fails).
    /// </summary>
    public static async Task<(GeoPoint? Point, Task<GeoPoint?>? Refined)> TryGetQuickLocationAsync(
        TimeSpan timeout, Action<string>? onError = null, Action? onPermissionRequired = null)
    {
        if (!await HasAccessAsync(onError, onPermissionRequired))
        {
            return (null, null);
        }

        Task<GeoPoint?> precise = TryGetPreciseAsync(timeout, onError, onPermissionRequired);
        Task<GeoPoint?> rough = TryGetRoughAsync();
        if (await Task.WhenAny(precise, rough) == rough && await rough is { } quick)
        {
            return (quick, precise);
        }

        return (await precise, null);
    }

    /// <summary>Call from a user action on the foreground UI thread, never from a refresh.</summary>
    public static async Task<GeolocationAccessStatus?> RequestAccessAsync(Action<string>? onError = null)
    {
        try
        {
            GeolocationAccessStatus status = await Geolocator.RequestAccessAsync();
            if (status != GeolocationAccessStatus.Allowed)
            {
                ReportPermissionRequired(onError, null);
            }

            return status;
        }
        catch (Exception ex) when (IsLocationFailure(ex))
        {
            Debug.WriteLine($"Location permission request failed: {ex}");
            onError?.Invoke(GetString("LocationUnavailable"));
            return null;
        }
    }

    private static async Task<bool> HasAccessAsync(Action<string>? onError, Action? onPermissionRequired)
    {
        try
        {
            // Capability checks can make blocking RPC calls that re-enter WinUI on the UI thread.
            bool allowed = await Task.Run(() =>
                HasAppCapability
                    ? AppCapability.Create("location").CheckAccess() == AppCapabilityAccessStatus.Allowed
                    : DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.Location).CurrentStatus == DeviceAccessStatus.Allowed);
            if (!allowed)
            {
                ReportPermissionRequired(onError, onPermissionRequired);
            }

            return allowed;
        }
        catch (Exception ex) when (IsLocationFailure(ex))
        {
            Debug.WriteLine($"Location permission check failed: {ex}");
            onError?.Invoke(GetString("LocationUnavailable"));
            return false;
        }
    }

    private static async Task<GeoPoint?> TryGetPreciseAsync(
        TimeSpan timeout, Action<string>? onError, Action? onPermissionRequired)
    {
        try
        {
            return await GetPositionAsync(PositionAccuracy.High, MaximumAge, timeout);
        }
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine("Location access was revoked during the position lookup.");
            ReportPermissionRequired(onError, onPermissionRequired);
            return null;
        }
        catch (Exception ex) when (IsLocationFailure(ex))
        {
            Debug.WriteLine($"Location lookup failed: {ex}");
            onError?.Invoke(GetString("LocationUnavailable"));
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
        catch (Exception ex) when (IsLocationFailure(ex))
        {
            Debug.WriteLine($"Rough location lookup failed: {ex}");
            return Geolocator.DefaultGeoposition is { } fallback ? new GeoPoint(fallback.Latitude, fallback.Longitude) : null;
        }
    }

    private static void ReportPermissionRequired(Action<string>? onError, Action? onPermissionRequired)
    {
        onPermissionRequired?.Invoke();
        onError?.Invoke(GetString("LocationAccessOff"));
    }

    private static string GetString(string name) => new ResourceLoader().GetString(name);

    private static bool IsLocationFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException;

    private static async Task<GeoPoint> GetPositionAsync(PositionAccuracy accuracy, TimeSpan maximumAge, TimeSpan timeout)
    {
        Geolocator locator = new() { DesiredAccuracy = accuracy };
        Geoposition position = await locator.GetGeopositionAsync(maximumAge, timeout);
        BasicGeoposition p = position.Coordinate.Point.Position;
        return new GeoPoint(p.Latitude, p.Longitude);
    }
}
