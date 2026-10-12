using TrayTimes.Transit;

namespace Microsoft.Windows.ApplicationModel.Resources
{
    internal sealed class ResourceLoader
    {
        public string GetString(string name) => name;
    }
}

namespace Windows.Foundation.Metadata
{
    internal static class ApiInformation
    {
        public static bool HasAppCapability { get; set; } = true;

        public static bool IsTypePresent(string name) => HasAppCapability;
    }
}

namespace Windows.Security.Authorization.AppCapabilityAccess
{
    internal enum AppCapabilityAccessStatus
    {
        DeniedBySystem,
        NotDeclaredByApp,
        DeniedByUser,
        UserPromptRequired,
        Allowed,
    }

    internal sealed class AppCapability
    {
        public static Func<AppCapabilityAccessStatus> Check { get; set; } = () => AppCapabilityAccessStatus.Allowed;

        public static string? LastCapability { get; private set; }

        public static AppCapability Create(string capability)
        {
            LastCapability = capability;
            return new AppCapability();
        }

        public AppCapabilityAccessStatus CheckAccess() => Check();
    }
}

namespace Windows.Devices.Enumeration
{
    internal enum DeviceClass
    {
        Location,
    }

    internal enum DeviceAccessStatus
    {
        Unspecified,
        Allowed,
        DeniedByUser,
        DeniedBySystem,
    }

    internal sealed class DeviceAccessInformation
    {
        public static DeviceAccessStatus Status { get; set; } = DeviceAccessStatus.Allowed;

        public DeviceAccessStatus CurrentStatus => Status;

        public static DeviceAccessInformation CreateFromDeviceClass(DeviceClass deviceClass) => new();
    }
}

namespace Windows.Devices.Geolocation
{
    internal enum GeolocationAccessStatus
    {
        Unspecified,
        Allowed,
        Denied,
    }

    internal enum PositionAccuracy
    {
        Default,
        High,
    }

    internal readonly record struct BasicGeoposition(double Latitude, double Longitude);

    internal sealed class Geopoint(GeoPoint point)
    {
        public BasicGeoposition Position { get; } = new(point.Lat, point.Lon);
    }

    internal sealed class Geocoordinate(GeoPoint point)
    {
        public Geopoint Point { get; } = new(point);
    }

    internal sealed class Geoposition(GeoPoint point)
    {
        public Geocoordinate Coordinate { get; } = new(point);
    }

    internal sealed class Geolocator
    {
        public static Func<Task<GeolocationAccessStatus>> Request { get; set; } = () => Task.FromResult(GeolocationAccessStatus.Allowed);

        public static Func<PositionAccuracy, Task<Geoposition>> Locate { get; set; } = _ => Task.FromResult(new Geoposition(new GeoPoint(43, -88)));

        public static int Requests { get; private set; }

        public static int Lookups { get; private set; }

        public static BasicGeoposition? DefaultGeoposition { get; set; }

        public PositionAccuracy DesiredAccuracy { get; set; }

        public static Task<GeolocationAccessStatus> RequestAccessAsync()
        {
            ++Requests;
            return Request();
        }

        public Task<Geoposition> GetGeopositionAsync(TimeSpan maximumAge, TimeSpan timeout)
        {
            ++Lookups;
            return Locate(DesiredAccuracy);
        }

        public static void Reset()
        {
            Requests = 0;
            Lookups = 0;
            DefaultGeoposition = null;
            Request = () => Task.FromResult(GeolocationAccessStatus.Allowed);
            Locate = _ => Task.FromResult(new Geoposition(new GeoPoint(43, -88)));
        }
    }
}
