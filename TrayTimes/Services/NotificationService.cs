using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace TrayTimes.Services;

/// <summary>
/// Windows App SDK toast notifications for bus tracking. Every alert shares one tag, so a newer
/// alert replaces the previous one in Action Center.
/// </summary>
internal static class NotificationService
{
    private const string Tag = "tracking";
    private const string ActionKey = "action";
    private const string StopTrackingAction = "stopTracking";

    private static bool _registered;

    /// <summary>Raised on a background thread when an alert is clicked (to open the flyout).</summary>
    public static event EventHandler? Opened;

    /// <summary>Raised on a background thread when an alert's "Stop tracking" button is clicked.</summary>
    public static event EventHandler? StopTrackingRequested;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Alerts are a nice-to-have; tracking still updates the tray icon without them.
            System.Diagnostics.Debug.WriteLine($"App notifications unavailable: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    public static void Show(string title, string text, bool canStopTracking)
    {
        if (!_registered)
        {
            return;
        }

        AppNotificationBuilder builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(text);
        if (canStopTracking)
        {
            builder.AddButton(new AppNotificationButton("Stop tracking").AddArgument(ActionKey, StopTrackingAction));
        }

        AppNotification notification = builder.BuildNotification();
        notification.Tag = Tag;
        notification.ExpiresOnReboot = true;
        AppNotificationManager.Default.Show(notification);
    }

    /// <summary>Removes the current alert, e.g. when the rider stops tracking.</summary>
    public static void Clear()
    {
        if (_registered)
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(Tag);
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue(ActionKey, out string? action) && action == StopTrackingAction)
        {
            StopTrackingRequested?.Invoke(null, EventArgs.Empty);
        }
        else
        {
            Opened?.Invoke(null, EventArgs.Empty);
        }
    }
}
