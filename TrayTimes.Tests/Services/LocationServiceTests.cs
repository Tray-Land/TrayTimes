using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Devices.Enumeration;
using Windows.Devices.Geolocation;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows10.0.17763")]
public sealed class LocationServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestInitialize]
    public void Initialize()
    {
        Geolocator.Reset();
        ApiInformation.HasAppCapability = true;
        AppCapability.Check = () => AppCapabilityAccessStatus.Allowed;
        DeviceAccessInformation.Status = DeviceAccessStatus.Allowed;
    }

    [TestMethod]
    [DataRow((int)AppCapabilityAccessStatus.UserPromptRequired)]
    [DataRow((int)AppCapabilityAccessStatus.DeniedByUser)]
    [DataRow((int)AppCapabilityAccessStatus.DeniedBySystem)]
    [DataRow((int)AppCapabilityAccessStatus.NotDeclaredByApp)]
    public async Task TryGetDeviceLocationAsync_WithoutAccess_ReportsPermissionWithoutPrompting(int access)
    {
        AppCapability.Check = () => (AppCapabilityAccessStatus)access;
        bool permissionRequired = false;
        string? error = null;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, message => error = message, () => permissionRequired = true);

        Assert.IsNull(point);
        Assert.IsTrue(permissionRequired);
        Assert.AreEqual("LocationAccessOff", error);
        Assert.AreEqual(0, Geolocator.Requests);
        Assert.AreEqual(0, Geolocator.Lookups);
        Assert.AreEqual("location", AppCapability.LastCapability);
    }

    [TestMethod]
    public async Task TryGetQuickLocationAsync_WithoutAccess_DoesNotReadEvenDefaultLocation()
    {
        AppCapability.Check = () => AppCapabilityAccessStatus.UserPromptRequired;
        Geolocator.DefaultGeoposition = new BasicGeoposition(43, -88);
        bool permissionRequired = false;

        (GeoPoint? point, Task<GeoPoint?>? refined) = await LocationService.TryGetQuickLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true);

        Assert.IsNull(point);
        Assert.IsNull(refined);
        Assert.IsTrue(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
        Assert.AreEqual(0, Geolocator.Lookups);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_WithAccess_ReadsPositionWithoutPrompting()
    {
        bool permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true);

        Assert.AreEqual(new GeoPoint(43, -88), point);
        Assert.IsFalse(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
        Assert.AreEqual(1, Geolocator.Lookups);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_PermissionCheck_UsesBackgroundThread()
    {
        TaskCompletionSource<GeoPoint?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThreadId = 0;
        int checkThreadId = 0;
        AppCapability.Check = () =>
        {
            checkThreadId = Environment.CurrentManagedThreadId;
            return AppCapabilityAccessStatus.Allowed;
        };
        Thread caller = new(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                completion.SetResult(LocationService.TryGetDeviceLocationAsync(Timeout).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true };

        caller.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreNotEqual(callerThreadId, checkThreadId);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_CheckFails_ReportsErrorWithoutRequestingPermission()
    {
        AppCapability.Check = () => throw new COMException("Capability check unavailable.");
        string? error = null;
        bool permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, message => error = message, () => permissionRequired = true);

        Assert.IsNull(point);
        Assert.AreEqual("LocationUnavailable", error);
        Assert.IsFalse(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
        Assert.AreEqual(0, Geolocator.Lookups);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_LookupTimesOut_ReportsUnavailable()
    {
        Geolocator.Locate = _ => throw new TimeoutException();
        string? error = null;
        bool permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, message => error = message, () => permissionRequired = true);

        Assert.IsNull(point);
        Assert.AreEqual("LocationUnavailable", error);
        Assert.IsFalse(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_AccessRevokedDuringLookup_ReportsPermissionRequired()
    {
        Geolocator.Locate = _ => throw new UnauthorizedAccessException();
        bool permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true);

        Assert.IsNull(point);
        Assert.IsTrue(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TryGetDeviceLocationAsync_OnOlderWindows_UsesNonPromptingDeviceAccessCheck(bool allowed)
    {
        ApiInformation.HasAppCapability = false;
        AppCapability.Check = () => throw new AssertFailedException("AppCapability is unavailable.");
        DeviceAccessInformation.Status = allowed ? DeviceAccessStatus.Allowed : DeviceAccessStatus.DeniedByUser;
        bool permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true);

        Assert.AreEqual(allowed, point is not null);
        Assert.AreEqual(!allowed, permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
    }

    [TestMethod]
    public async Task TryGetQuickLocationAsync_RoughFixFirst_ReturnsRefinementWithoutPrompting()
    {
        TaskCompletionSource<Geoposition> precise = new();
        Geolocator.Locate = accuracy => accuracy == PositionAccuracy.High
            ? precise.Task
            : Task.FromResult(new Geoposition(new GeoPoint(43, -88)));

        (GeoPoint? point, Task<GeoPoint?>? refined) = await LocationService.TryGetQuickLocationAsync(Timeout);

        Assert.AreEqual(new GeoPoint(43, -88), point);
        Assert.IsNotNull(refined);
        precise.SetResult(new Geoposition(new GeoPoint(43.01, -88.01)));
        Assert.AreEqual(new GeoPoint(43.01, -88.01), await refined);
        Assert.AreEqual(0, Geolocator.Requests);
        Assert.AreEqual(2, Geolocator.Lookups);
    }

    [TestMethod]
    public async Task TryGetQuickLocationAsync_RoughLookupFails_ReturnsDefaultAndPreciseFix()
    {
        TaskCompletionSource<Geoposition> precise = new();
        Geolocator.DefaultGeoposition = new BasicGeoposition(43, -88);
        Geolocator.Locate = accuracy => accuracy == PositionAccuracy.High
            ? precise.Task
            : throw new TimeoutException();

        (GeoPoint? point, Task<GeoPoint?>? refined) = await LocationService.TryGetQuickLocationAsync(Timeout);

        Assert.AreEqual(new GeoPoint(43, -88), point);
        Assert.IsNotNull(refined);
        precise.SetResult(new Geoposition(new GeoPoint(43.01, -88.01)));
        Assert.AreEqual(new GeoPoint(43.01, -88.01), await refined);
        Assert.AreEqual(0, Geolocator.Requests);
    }

    [TestMethod]
    [DataRow((int)GeolocationAccessStatus.Allowed)]
    [DataRow((int)GeolocationAccessStatus.Denied)]
    [DataRow((int)GeolocationAccessStatus.Unspecified)]
    public async Task RequestAccessAsync_UserAction_RequestsPermissionAndReportsResult(int access)
    {
        Geolocator.Request = () => Task.FromResult((GeolocationAccessStatus)access);
        string? error = null;

        GeolocationAccessStatus? result = await LocationService.RequestAccessAsync(message => error = message);

        Assert.AreEqual((GeolocationAccessStatus)access, result);
        Assert.AreEqual(1, Geolocator.Requests);
        Assert.AreEqual(0, Geolocator.Lookups);
        Assert.AreEqual(access == (int)GeolocationAccessStatus.Allowed ? null : "LocationAccessOff", error);
    }

    [TestMethod]
    public async Task RequestAccessAsync_RequestFails_ReportsUnavailable()
    {
        Geolocator.Request = () => throw new COMException("Permission request unavailable.");
        string? error = null;

        GeolocationAccessStatus? result = await LocationService.RequestAccessAsync(message => error = message);

        Assert.IsNull(result);
        Assert.AreEqual("LocationUnavailable", error);
    }

    [TestMethod]
    public async Task TryGetDeviceLocationAsync_AccessGrantedBetweenRetries_RechecksAndReadsPosition()
    {
        AppCapability.Check = () => AppCapabilityAccessStatus.DeniedByUser;
        bool permissionRequired = false;
        Assert.IsNull(await LocationService.TryGetDeviceLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true));
        Assert.IsTrue(permissionRequired);
        AppCapability.Check = () => AppCapabilityAccessStatus.Allowed;
        permissionRequired = false;

        GeoPoint? point = await LocationService.TryGetDeviceLocationAsync(
            Timeout, onPermissionRequired: () => permissionRequired = true);

        Assert.AreEqual(new GeoPoint(43, -88), point);
        Assert.IsFalse(permissionRequired);
        Assert.AreEqual(0, Geolocator.Requests);
    }
}
