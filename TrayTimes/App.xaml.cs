using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.System.Power;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Transit.TransitApp;
using TrayTimes.Views;
using Windows.Storage;
using WinUIEx;

namespace TrayTimes;

/// <summary>
/// Tray application: manual launches open a window; Windows startup launches stay in the tray.
/// The notification-area icon toggles the arrivals flyout (pattern from github.com/TheJoeFin/Traydio).
/// <para>Browsing mode: the flyout and its polling exist only while it's in use, and are torn down
/// a minute after it's dismissed. Tracking mode: <see cref="BusTrackingService"/> follows one bus
/// with no window open, showing its minutes away as the tray icon. Active trip mode:
/// <see cref="ActiveTripService"/> follows a whole trip the same way, counting down to the rider's next
/// action. One of the two is active at a time: starting a trip stops a tracked bus.</para>
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\TrayTimes_SingleInstance";
    private const string ShowEventName = "Local\\TrayTimes_ShowFlyout";
    private const string AppName = "Tray Times";
    private static readonly TimeSpan FlyoutLifetimeAfterDismiss = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleCollectDelay = TimeSpan.FromSeconds(5);

    // How long a new departures batch waits for the board's, the trip's, and the favorites' stops, so opening the flyout costs one call.
    private static readonly TimeSpan DeparturesCoalesceWindow = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ExitSaveTimeout = TimeSpan.FromSeconds(2);

    // Files the MCTS BusTime build kept in LocalState.
    private static readonly string[] LegacyFiles = ["stop-index.json", "schedule.zip", "schedule.json", "schedule.zip.download"];

    private readonly EtaTrayIcon _etaIcon = new();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _trayIcon;
    private TimesFlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private DispatcherQueue? _dispatcher;
    private ArrivalsMonitor? _monitor;
    private BusTrackingService? _tracker;
    private ActiveTripService? _trips;
    private TransitBudget? _budget;
    private TransitCacheService? _cache;
    private DispatcherQueueTimer? _flyoutCloseTimer;

    public App()
    {
        InitializeComponent();
        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    internal ArrivalsMonitor Monitor => _monitor ?? throw new InvalidOperationException("The app hasn't launched yet.");

    internal BusTrackingService Tracker => _tracker ?? throw new InvalidOperationException("The app hasn't launched yet.");

    /// <summary>The active trip, a saved one waiting to resume, or none.</summary>
    internal ActiveTripService Trips => _trips ?? throw new InvalidOperationException("The app hasn't launched yet.");

    internal TransitBudget Budget => _budget ?? throw new InvalidOperationException("The app hasn't launched yet.");

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TrayTimes-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool isStartupTask = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (!isStartupTask)
            {
                // Another instance owns the tray icon: ask it to open a window, then quit.
                try
                {
                    // Let the running instance take the foreground when it shows its window.
                    Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                    using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                    existing.Set();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Unable to show the running instance: {ex.Message}");
                }
            }

            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        MigrateSavedData();

        // Every physical request goes client <- failure/budget gate <- shared caches; only cache misses cost a call.
        TransitAppService client = new(Secrets.TransitApiKey, CultureInfo.CurrentUICulture.Name);
        client.EndpointCalled += (_, endpoint) => ApiUsageService.Record(endpoint);
        client.Diagnostics.Observed += (_, observation) => DiagnosticLogService.Observe(observation);
        _budget = new TransitBudget(ApiUsageService.GetUsage, SettingsService.TransitBudget);
        MetadataTransitService transit = new(new CooldownTransitService(client, budget: _budget));
        _cache = new TransitCacheService(transit, _dispatcher);
        _ = _cache.LoadAsync();
        PredictionCoordinator departures = new(transit, browsingInterval: () => _budget.ReuseAge, coalesceWindow: DeparturesCoalesceWindow);
        _monitor = new ArrivalsMonitor(transit, _dispatcher, predictions: departures, budget: _budget);
        _monitor.Updated += (_, _) => UpdateTooltip();
        _tracker = new BusTrackingService(transit, _dispatcher, predictions: departures, budget: _budget);
        _tracker.Changed += (_, _) => OnTrackingChanged();
        _trips = new ActiveTripService(transit, _tracker, new ActiveTripStore(ApplicationData.Current.LocalFolder.Path), _dispatcher,
            predictions: departures, budget: _budget);
        _trips.Changed += (_, _) => OnTrackingChanged();
        WatchForWake();

        _flyoutCloseTimer = _dispatcher.CreateTimer();
        _flyoutCloseTimer.Interval = FlyoutLifetimeAfterDismiss;
        _flyoutCloseTimer.IsRepeating = false;
        _flyoutCloseTimer.Tick += (_, _) => CloseFlyout();

        NotificationService.Opened += (_, _) => _dispatcher.TryEnqueue(ShowFlyout);
        NotificationService.ActionRequested += (_, action) => _dispatcher.TryEnqueue(() => OnNotificationAction(action));
        NotificationService.Register();

        InitializeTrayIcon();
        _ = RestoreTripThenShowAsync(isStartupTask);
    }

    /// <summary>
    /// A trip saved before a restart waits for the rider: a manual launch then opens the flyout, which shows its
    /// Resume card; a startup launch has no window, so it asks in a notification.
    /// </summary>
    private async Task RestoreTripThenShowAsync(bool isStartupTask)
    {
        try
        {
            await Trips.RestoreAsync(offerResume: isStartupTask);
        }
        finally
        {
            if (!isStartupTask)
            {
                // Even with nothing saved: the board near you needs no setup, and its card offers it.
                ShowFlyout();
            }
        }
    }

    /// <summary>
    /// Clears what the MCTS BusTime build saved: its stop IDs and call tally mean nothing to Transit.
    /// Favorite places are plain coordinates and carry over.
    /// </summary>
    private static void MigrateSavedData()
    {
        if (SettingsService.DataVersion >= SettingsService.CurrentDataVersion)
        {
            return;
        }

        SettingsService.SetStops([]);
        ApiUsageService.StartOver();
        foreach (string name in LegacyFiles)
        {
            try
            {
                File.Delete(Path.Combine(ApplicationData.Current.LocalFolder.Path, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Couldn't delete a legacy cache file: {ex.GetType().Name}.");
            }
        }

        SettingsService.DataVersion = SettingsService.CurrentDataVersion;
    }

    /// <summary>
    /// Re-evaluates the trip as soon as the PC wakes, so its step and alerts catch up at once. Without
    /// this, its timer still catches up when it fires, a little later.
    /// </summary>
    private void WatchForWake()
    {
        try
        {
            PowerManager.SystemSuspendStatusChanged += (_, _) =>
            {
                if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
                {
                    _dispatcher?.TryEnqueue(() => _ = Trips.WakeAsync());
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Suspend notifications unavailable: {ex.GetType().Name}.");
        }
    }

    /// <summary>A notification's button: on a tracked bus, an active trip, or a saved trip waiting to resume.</summary>
    private void OnNotificationAction(NotificationAction action)
    {
        switch (action)
        {
            case NotificationAction.StopTracking:
                Tracker.Stop();
                break;
            case NotificationAction.MissedIt:
                _ = Trips.MissedAsync();
                break;
            case NotificationAction.EndTrip:
                Trips.End();
                break;
            case NotificationAction.ResumeTrip:
                _ = Trips.ResumeAsync();
                break;
        }
    }

    /// <summary>Applies a changed "keep awake" setting to tracking and the trip; the trip goes second, so an active one wins.</summary>
    internal void ApplyKeepAwakeSetting()
    {
        Tracker.ApplyKeepAwakeSetting();
        Trips.ApplyKeepAwakeSetting();
    }

    public void ShowSettings()
    {
        _flyout?.HidePopup();
        if (_settings is null)
        {
            _settings = new SettingsWindow(Monitor, Budget);
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                if (_flyout is null)
                {
                    ReleaseIdleResources();
                }
            };
        }

        _settings.Activate();
        _settings.BringToFront();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, AppName);
        _trayIcon.Selected += (_, _) => EnsureFlyout().Toggle();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(UpdateTrayIcon);
    }

    // The white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => SystemThemeService.IsLight ? "Assets/AppIcon-dark.ico" : "Assets/AppIcon.ico";

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = "Show arrivals", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        MenuFlyoutItem settings = new() { Text = "Stops and settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        if (Trips.IsActive)
        {
            AddTripItems(menu);
        }
        else if (Tracker.Current is { } bus)
        {
            MenuFlyoutItem stopTracking = new() { Text = $"Stop tracking route {bus.RouteDisplayId}", Icon = new FontIcon { Glyph = "" } };
            stopTracking.Click += (_, _) => Tracker.Stop();
            menu.Items.Add(stopTracking);
        }

        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    /// <summary>Show trip, I missed it (while heading to or on a ride), and End trip.</summary>
    private void AddTripItems(MenuFlyout menu)
    {
        MenuFlyoutItem show = new() { Text = AppStrings.Get("TrayShowTrip"), Icon = new FontIcon { Glyph = "" } };
        show.Click += (_, _) => ShowFlyout();
        menu.Items.Add(show);
        if (Trips.Step is { CanMiss: true })
        {
            MenuFlyoutItem missed = new() { Text = AppStrings.Get("TrayMissedIt"), Icon = new FontIcon { Glyph = "" } };
            missed.Click += (_, _) => _ = Trips.MissedAsync();
            menu.Items.Add(missed);
        }

        MenuFlyoutItem end = new() { Text = AppStrings.Get("TrayEndTrip"), Icon = new FontIcon { Glyph = "" } };
        end.Click += (_, _) => Trips.End();
        menu.Items.Add(end);
    }

    private void ShowFlyout()
    {
        TimesFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowPopup();
    }

    private TimesFlyoutWindow EnsureFlyout()
    {
        _flyoutCloseTimer?.Stop();
        if (_flyout is null)
        {
            _flyout = new TimesFlyoutWindow(Monitor, Tracker, Trips, Budget);
            _flyout.PopupHidden += (_, _) =>
            {
                // Restart the countdown from this dismissal.
                _flyoutCloseTimer?.Stop();
                _flyoutCloseTimer?.Start();
            };
            _flyout.SummaryChanged += (_, _) => UpdateTooltip();
        }

        return _flyout;
    }

    /// <summary>Browsing mode's end: destroys the dismissed flyout so its map, timers, and polling go away.</summary>
    private void CloseFlyout()
    {
        if (_flyout is null || _flyout.IsPopupVisible)
        {
            return;
        }

        _flyout.CloseWindow();
        _flyout = null;
        Monitor.Clear();
        if (_settings is null)
        {
            ReleaseIdleResources();
        }
    }

    /// <summary>Called once no window is open.</summary>
    private void ReleaseIdleResources()
    {
        // An idle tray app allocates almost nothing, so the GC wouldn't run on its own to hand the
        // closed windows' memory back to Windows. Wait for WinUI to finish tearing the window down
        // first, or its objects are still referenced and just get promoted. Then run finalizers
        // (the map renderer frees its Direct3D device in one) and collect what they released. This
        // runs off the UI thread because some finalizers may need to reach it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(IdleCollectDelay);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }

    private void OnTrackingChanged()
    {
        UpdateTrayIcon();
        UpdateTooltip();
    }

    /// <summary>
    /// During a trip, the minutes to the rider's next action (red when it's time to act), or the app icon
    /// while that's over 99 minutes away; while tracking, the bus's minutes away; otherwise the app icon.
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if (_trips?.IsActive == true)
        {
            if (_trips.Step is { } step && ActiveTripService.BadgeMinutes(step, now) is { } minutes)
            {
                _etaIcon.Show(_trayIcon, minutes, step.IsUrgent, passed: false);
                return;
            }
        }
        else if (_tracker?.Current is { } bus)
        {
            _etaIcon.Show(_trayIcon, bus.MinutesAway(now), _tracker.IsTimeToGo, bus.HasPassed);
            return;
        }

        _trayIcon.SetIcon(TrayIconPath);
        _etaIcon.Release();
    }

    /// <summary>
    /// The active trip, e.g. "Trip to Work: get off at State St in 9 min (8:25)", and the tracked bus,
    /// then (while the flyout is in use) the suggested trip, e.g. "Work: leave in 6 min, 14 at 8:12", and
    /// as many of the saved stops near the board's center as fit, e.g. "14 Northbound: Due, 12 min".
    /// Times are dropped when the flyout is closed.
    /// </summary>
    private void UpdateTooltip()
    {
        if (_trayIcon is null || _monitor is null)
        {
            return;
        }

        ArrivalsSnapshot snapshot = _monitor.Latest;
        DateTimeOffset now = DateTimeOffset.Now;
        List<string> lines = [AppName];
        if (_trips?.Current is { } activeTrip)
        {
            lines.Add(TripMessages.Tooltip(activeTrip, _trips.IsPendingResume, now, SettingsService.HeadOutMinutes));
        }

        if (_tracker?.Current is { } bus)
        {
            lines.Add(AppStrings.Format("TooltipTrackingFormat", bus.RouteDisplayId, bus.StopName, Minutes([bus.MinutesAway(now)])));
        }

        if (snapshot.Error is not null && snapshot.FetchedAt is null)
        {
            lines.Add(snapshot.Error);
        }

        if (_flyout?.TripSummary is { } trip)
        {
            lines.Add(trip);
        }

        List<string> stops = [];
        if (_flyout is not null && snapshot.FetchedAt is not null)
        {
            IReadOnlyList<SavedStop> saved = SettingsService.Stops;
            foreach (SavedStop stop in _flyout.SavedStopIds.SelectMany(id => saved.Where(s => s.StopId == id)))
            {
                string label = stop.AllRoutes ? stop.StopName : AppStrings.Format("TooltipStopLineFormat", stop.RouteDisplayId, stop.DirectionName);
                int[] minutes = [.. snapshot.For(stop).Where(a => a.Status != ArrivalStatus.Canceled).Take(2).Select(a => a.MinutesAway(now))];
                string when = minutes.Length == 0 ? AppStrings.Get("TooltipNoArrivals") : Minutes(minutes);
                stops.Add(AppStrings.Format("TooltipStopFormat", label, when));
            }
        }

        _trayIcon.Tooltip = TrayTooltip.Compose(lines, stops);
    }

    /// <summary>"4, 12 min", or "Due" when every figure is; the unit follows the last figure.</summary>
    private static string Minutes(IReadOnlyList<int> minutes)
    {
        string due = AppStrings.Get("TooltipDue");
        string figures = string.Join(", ", minutes.Select(m => m == 0 ? due : m.ToString(CultureInfo.CurrentCulture)));
        return minutes[^1] == 0 ? figures : AppStrings.Format("TooltipMinutesFormat", figures);
    }

    private void ExitApp()
    {
        _flyoutCloseTimer?.Stop();
        _tracker?.Stop();
        _trips?.FlushAsync().Wait(ExitSaveTimeout); // The trip stays saved, so the next launch can offer to resume it.
        _monitor?.Clear();
        _flyout?.CloseWindow();
        _settings?.Close();
        NotificationService.Unregister();
        _cache?.SaveAsync().Wait(ExitSaveTimeout);

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _etaIcon.Release();

        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }
}
