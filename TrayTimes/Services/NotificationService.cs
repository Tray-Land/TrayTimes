using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TrayTimes.Views;

namespace TrayTimes.Services;

/// <summary>
/// Windows App SDK toast notifications for bus tracking and active trips. Every alert shares one tag, so a
/// newer alert replaces the previous one in Action Center.
/// </summary>
internal static class NotificationService
{
    private const string Tag = "tracking";
    private const string ActionKey = "action";

    private static bool _registered;

    /// <summary>Raised on a background thread when an alert is clicked (to open the flyout).</summary>
    public static event EventHandler? Opened;

    /// <summary>Raised on a background thread when one of an alert's buttons is clicked.</summary>
    public static event EventHandler<NotificationAction>? ActionRequested;

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

    /// <param name="buttons">The buttons to offer, in order.</param>
    public static void Show(string title, string text, IReadOnlyList<NotificationAction> buttons)
    {
        if (!_registered)
        {
            return;
        }

        AppNotificationBuilder builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(text);
        foreach (NotificationAction action in buttons)
        {
            builder.AddButton(new AppNotificationButton(Label(action)).AddArgument(ActionKey, action.ToString()));
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

    private static string Label(NotificationAction action) => AppStrings.Get(action switch
    {
        NotificationAction.StopTracking => "NotificationStopTracking",
        NotificationAction.MissedIt => "NotificationMissedIt",
        NotificationAction.EndTrip => "NotificationEndTrip",
        _ => "NotificationResumeTrip",
    });

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Case doesn't matter, so a toast from an older version ("stopTracking") still works.
        if (args.Arguments.TryGetValue(ActionKey, out string? value)
            && Enum.TryParse(value, ignoreCase: true, out NotificationAction action) && Enum.IsDefined(action))
        {
            ActionRequested?.Invoke(null, action);
        }
        else
        {
            Opened?.Invoke(null, EventArgs.Empty);
        }
    }
}
