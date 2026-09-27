using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Transit.BusTime;
using TrayTimes.Views;
using WinUIEx;

namespace TrayTimes;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// arrivals flyout (pattern from github.com/TheJoeFin/Traydio).
/// <para>Browsing mode: the flyout and its polling exist only while it's in use, and are torn down
/// a minute after it's dismissed. Tracking mode: <see cref="BusTrackingService"/> follows one bus
/// with no window open, showing its minutes away as the tray icon.</para>
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\TrayTimes_SingleInstance";
    private const string ShowEventName = "Local\\TrayTimes_ShowFlyout";
    private const string AppName = "Tray Times";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit
    private static readonly TimeSpan FlyoutLifetimeAfterDismiss = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleCollectDelay = TimeSpan.FromSeconds(5);

    // MCTS's published GTFS timetable, for scheduled times where BusTime has no prediction.
    private static readonly Uri MctsScheduleUri = new("https://kamino.mcts.org/gtfs/google_transit.zip");

    private readonly EtaTrayIcon _etaIcon = new();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _trayIcon;
    private TimesFlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private DispatcherQueue? _dispatcher;
    private ArrivalsMonitor? _monitor;
    private BusTrackingService? _tracker;
    private StopIndexService? _stopIndex;
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

    internal StopIndexService StopIndex => _stopIndex ?? throw new InvalidOperationException("The app hasn't launched yet.");

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
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        BusTimeTransitService mcts = BusTimeTransitService.CreateMcts(Secrets.MctsApiKey);
        _stopIndex = new StopIndexService(mcts);
        _monitor = new ArrivalsMonitor(mcts, new ScheduleService(MctsScheduleUri, _stopIndex), _dispatcher);
        _monitor.Updated += (_, _) => UpdateTooltip();
        _tracker = new BusTrackingService(mcts, _dispatcher);
        _tracker.Changed += (_, _) => OnTrackingChanged();

        _flyoutCloseTimer = _dispatcher.CreateTimer();
        _flyoutCloseTimer.Interval = FlyoutLifetimeAfterDismiss;
        _flyoutCloseTimer.IsRepeating = false;
        _flyoutCloseTimer.Tick += (_, _) => CloseFlyout();

        NotificationService.Opened += (_, _) => _dispatcher.TryEnqueue(ShowFlyout);
        NotificationService.StopTrackingRequested += (_, _) => _dispatcher.TryEnqueue(() => Tracker.Stop());
        NotificationService.Register();

        InitializeTrayIcon();
        if (mcts.IsConfigured)
        {
            // Make sure the stop directory is on disk so Home/Work/Nearby are quick; failures retry
            // on first use. Only the file is kept: the in-memory copy is dropped until needed.
            _ = _stopIndex.GetAsync().ContinueWith(
                t =>
                {
                    _ = t.Exception;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (_flyout is null && _settings is null)
                        {
                            _stopIndex.Unload();
                        }
                    });
                },
                TaskScheduler.Default);
        }

        // First run: nothing to show yet, so go straight to setup.
        if (SettingsService.Stops.Count == 0 && SettingsService.Places.Count == 0)
        {
            ShowSettings();
        }
    }

    public void ShowSettings()
    {
        _flyout?.HidePopup();
        if (_settings is null)
        {
            _settings = new SettingsWindow(Monitor, StopIndex);
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
        if (Tracker.Current is { } bus)
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
            _flyout = new TimesFlyoutWindow(Monitor, Tracker, StopIndex);
            _flyout.PopupHidden += (_, _) =>
            {
                // Restart the countdown from this dismissal.
                _flyoutCloseTimer?.Stop();
                _flyoutCloseTimer?.Start();
            };
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
        StopIndex.Unload();
        Monitor.Schedule.Unload();

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

    /// <summary>The tracked bus's minutes away while tracking; otherwise the app icon.</summary>
    private void UpdateTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_tracker?.Current is { } bus)
        {
            _etaIcon.Show(_trayIcon, bus.MinutesAway(DateTimeOffset.Now), _tracker.IsTimeToGo);
            return;
        }

        _trayIcon.SetIcon(TrayIconPath);
        _etaIcon.Release();
    }

    /// <summary>
    /// The tracked bus, then (while the flyout is in use) the next arrivals, e.g.
    /// "14 Northbound: Due, 12 min". Times are dropped when the flyout is closed.
    /// </summary>
    private void UpdateTooltip()
    {
        if (_trayIcon is null || _monitor is null)
        {
            return;
        }

        ArrivalsSnapshot snapshot = _monitor.Latest;
        StringBuilder text = new(AppName);
        DateTimeOffset now = DateTimeOffset.Now;
        if (_tracker?.Current is { } bus)
        {
            int minutes = bus.MinutesAway(now);
            text.AppendLine().Append($"Tracking {bus.RouteDisplayId} at {bus.StopName}: ")
                .Append(minutes == 0 ? "Due" : $"{minutes.ToString(CultureInfo.CurrentCulture)} min");
        }

        if (snapshot.Error is not null && snapshot.FetchedAt is null)
        {
            text.AppendLine().Append(snapshot.Error);
        }
        else if (snapshot.FetchedAt is not null)
        {
            foreach (SavedStop stop in SettingsService.Stops.Take(3))
            {
                string label = stop.AllRoutes ? stop.StopName : $"{stop.RouteDisplayId} {stop.DirectionName}";
                string[] times = [.. snapshot.For(stop)
                    .Where(a => a.Status != ArrivalStatus.Canceled)
                    .Take(2)
                    .Select(a => a.MinutesAway(now) == 0 ? "Due" : a.MinutesAway(now).ToString(CultureInfo.CurrentCulture))];
                string when = times.Length == 0 ? "no arrivals" : times[^1] == "Due" ? string.Join(", ", times) : $"{string.Join(", ", times)} min";
                text.AppendLine().Append(label).Append(": ").Append(when);
            }
        }

        string tooltip = text.ToString();
        _trayIcon.Tooltip = tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..(MaxTooltipLength - 1)] + "…";
    }

    private void ExitApp()
    {
        _flyoutCloseTimer?.Stop();
        _tracker?.Stop();
        _monitor?.Clear();
        _flyout?.CloseWindow();
        _settings?.Close();
        NotificationService.Unregister();

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
