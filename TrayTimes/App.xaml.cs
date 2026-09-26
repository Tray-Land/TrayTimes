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
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\TrayTimes_SingleInstance";
    private const string ShowEventName = "Local\\TrayTimes_ShowFlyout";
    private const string AppName = "Tray Times";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _trayIcon;
    private TimesFlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private DispatcherQueue? _dispatcher;
    private ArrivalsMonitor? _monitor;
    private StopIndexService? _stopIndex;

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
        _monitor = new ArrivalsMonitor(mcts, _dispatcher);
        _monitor.Updated += (_, _) => UpdateTooltip();
        _stopIndex = new StopIndexService(mcts);

        InitializeTrayIcon();
        _monitor.Start();
        if (mcts.IsConfigured)
        {
            // Warm the stop directory so Home/Work/Nearby are instant; failures retry on first use.
            _ = _stopIndex.GetAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }

        // First run: nothing to show yet, so go straight to setup.
        if (SettingsService.Stops.Count == 0 && SettingsService.Home is null && SettingsService.Work is null)
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
            _settings.Closed += (_, _) => _settings = null;
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
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() => _trayIcon?.SetIcon(TrayIconPath));
    }

    // The white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => SystemThemeService.IsLight ? "Assets/AppIcon-dark.ico" : "Assets/AppIcon.ico";

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = "Show arrivals", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        MenuFlyoutItem refresh = new() { Text = "Refresh", Icon = new FontIcon { Glyph = "" } };
        refresh.Click += (_, _) => _ = Monitor.RefreshAsync();
        MenuFlyoutItem settings = new() { Text = "Stops and settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(refresh);
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

    private TimesFlyoutWindow EnsureFlyout() => _flyout ??= new TimesFlyoutWindow(Monitor, StopIndex);

    /// <summary>Summarizes the next arrivals in the tray tooltip, e.g. "14 Northbound: Due, 12 min".</summary>
    private void UpdateTooltip()
    {
        if (_trayIcon is null || _monitor is null)
        {
            return;
        }

        ArrivalsSnapshot snapshot = _monitor.Latest;
        StringBuilder text = new(AppName);
        if (snapshot.Error is not null && snapshot.FetchedAt is null)
        {
            text.AppendLine().Append(snapshot.Error);
        }
        else
        {
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (SavedStop stop in SettingsService.Stops.Take(3))
            {
                string label = stop.AllRoutes ? stop.StopName : $"{stop.RouteDisplayId} {stop.DirectionName}";
                string[] times = snapshot.For(stop)
                    .Where(a => a.Status != ArrivalStatus.Canceled)
                    .Take(2)
                    .Select(a => a.MinutesAway(now) == 0 ? "Due" : a.MinutesAway(now).ToString(System.Globalization.CultureInfo.CurrentCulture))
                    .ToArray();
                string when = times.Length == 0 ? "no arrivals" : times[^1] == "Due" ? string.Join(", ", times) : $"{string.Join(", ", times)} min";
                text.AppendLine().Append(label).Append(": ").Append(when);
            }
        }

        string tooltip = text.ToString();
        _trayIcon.Tooltip = tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..(MaxTooltipLength - 1)] + "…";
    }

    private void ExitApp()
    {
        _monitor?.Stop();
        _flyout?.CloseForExit();
        _settings?.Close();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }
}
