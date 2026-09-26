using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TrayTimes.Views;

/// <summary>
/// Borderless arrivals popup shown next to the tray icon. It lists either the saved stops or the
/// stops nearest home, work, or the device. It is created once and then shown/hidden so the map
/// and its tile cache stay warm, and it light-dismisses when focus leaves.
/// </summary>
public sealed partial class TimesFlyoutWindow : WindowEx
{
    private const int PopupWidth = 400;
    private const int PopupHeight = 680;
    private const int ArrivalsPerStop = 4;
    private const int ArrivalsPerNearbyStop = 3;
    private const int MaxNearbyStops = 8;
    private const double WalkingMetersPerMinute = 80; // about 3 mph
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan VehicleRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaceRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PatternCacheLifetime = TimeSpan.FromHours(6);

    private readonly HWND _hwnd;
    private readonly ArrivalsMonitor _monitor;
    private readonly StopIndexService _stopIndex;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _vehicleTimer;
    private readonly DispatcherQueueTimer _placeTimer;
    private readonly DispatcherQueueTimer _captionTimer;

    // Draw order: route lines, then stops, then the home/work/you marker, then buses on top.
    private readonly MapElementsLayer _routeLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly MapElementsLayer _placeLayer = new();
    private readonly MapElementsLayer _vehicleLayer = new();
    private readonly Dictionary<string, MapIcon> _vehicleIcons = [];
    private readonly Dictionary<MapIcon, string> _stopPins = [];
    private readonly Dictionary<string, Vehicle> _vehicles = [];
    private readonly Dictionary<Color, FontIcon> _busGlyphs = [];
    private readonly Dictionary<string, (DateTime FetchedUtc, IReadOnlyList<RoutePattern> Patterns)> _patternCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly FontIcon _stopGlyph;

    private IReadOnlyDictionary<string, TransitRoute> _routes = new Dictionary<string, TransitRoute>();
    private IReadOnlyList<StopGroupItem> _groups = [];
    private FlyoutMode _mode;
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _mapRevealStarted;
    private bool _savedStopsDirty = true;
    private DateTime _lastDismissedAtUtc;

    // Home/Work/Nearby state.
    private GeoPoint? _placeOrigin;
    private IReadOnlyList<NearbyStop> _placeStops = [];
    private IReadOnlyList<Arrival> _placeArrivals = [];
    private DateTimeOffset? _placeFetchedAt;
    private string? _placeError;
    private (string Title, string Text, string? Button)? _placeNotice;
    private bool _placeLoading;
    private int _placeVersion;

    internal TimesFlyoutWindow(ArrivalsMonitor monitor, StopIndexService stopIndex)
    {
        InitializeComponent();
        _monitor = monitor;
        _stopIndex = stopIndex;
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), repeating: false, HideIfFocusLeftWindow);
        _vehicleTimer = CreateTimer(VehicleRefreshInterval, repeating: true, () => _ = RefreshVehiclesAsync());
        _placeTimer = CreateTimer(PlaceRefreshInterval, repeating: true, () => _ = LoadPlaceAsync(reframe: false));
        _captionTimer = CreateTimer(TimeSpan.FromSeconds(5), repeating: false, () => MapCaption.Visibility = Visibility.Collapsed);

        _stopGlyph = new FontIcon { Glyph = MapHelpers.StopGlyph, FontSize = 14 };
        MapHelpers.ApplyStopColor(_stopGlyph, Root);
        Map.Layers.Add(_routeLayer);
        Map.Layers.Add(_stopLayer);
        Map.Layers.Add(_placeLayer);
        Map.Layers.Add(_vehicleLayer);
        _vehicleLayer.Tapped += VehicleLayer_Tapped;
        _stopLayer.Tapped += StopLayer_Tapped;

        TitleText.Text = $"{_monitor.Service.AgencyName} arrivals";
        Root.ActualThemeChanged += (_, _) =>
        {
            MapHelpers.ApplyBasemap(Map, Root, MapCover);
            MapHelpers.ApplyStopColor(_stopGlyph, Root);
        };
        MapHelpers.ApplyBasemap(Map, Root, MapCover);

        _monitor.Updated += OnArrivalsUpdated;
        _stopIndex.Changed += OnStopIndexChanged;
        SettingsService.StopsChanged += OnStopsChanged;
        SettingsService.PlacesChanged += OnPlacesChanged;

        _mode = SettingsService.FlyoutMode;
        ModeBar.SelectedItem = ModeBar.Items[(int)_mode];
        Render();
    }

    /// <summary>Shows the popup, or hides it if already visible (tray-icon click behavior).</summary>
    public void Toggle()
    {
        if (_isPopupVisible)
        {
            HidePopup();
            return;
        }

        // The click that light-dismissed the popup also arrives here; don't immediately reopen.
        if (DateTime.UtcNow - _lastDismissedAtUtc < RecentDismissWindow)
        {
            return;
        }

        ShowPopup();
    }

    public void ShowPopup()
    {
        WindowPlacementService.PositionNearTray(this, PopupWidth, PopupHeight);
        _hideTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        RevealMapOnFirstShow();

        _monitor.IsForeground = true;
        EnterMode(reframe: true);
        _vehicleTimer.Start();
    }

    public void HidePopup()
    {
        if (!_isPopupVisible)
        {
            return;
        }

        _isPopupVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _captionTimer.Stop();
        MapCaption.Visibility = Visibility.Collapsed;
        _monitor.IsForeground = false;
        AppWindow.Hide();
    }

    /// <summary>Closes the window for real (app exit).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _monitor.Updated -= OnArrivalsUpdated;
        _stopIndex.Changed -= OnStopIndexChanged;
        SettingsService.StopsChanged -= OnStopsChanged;
        SettingsService.PlacesChanged -= OnPlacesChanged;
        Close();
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, bool repeating, Action tick)
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void ConfigurePresenter()
    {
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        unsafe
        {
            DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HidePopup();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isShowing = false;
            _hideTimer.Stop();
            return;
        }

        if (_isShowing || !_isPopupVisible)
        {
            return;
        }

        // Debounce: focus can bounce briefly while XAML popups (tooltips, menus) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        if (!ShouldRemainVisible(PInvoke.GetForegroundWindow()))
        {
            HidePopup();
        }
    }

    private bool ShouldRemainVisible(HWND foreground)
    {
        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
        {
            return true;
        }

        // Keep open while one of our own transient XAML popups or menus has focus.
        Span<char> className = stackalloc char[256];
        int length = PInvoke.GetClassName(foreground, className);
        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> name = className[..length];
        return name.SequenceEqual("Xaml_WindowedPopupClass") || name.SequenceEqual("#32768");
    }

    private async void RevealMapOnFirstShow()
    {
        if (_mapRevealStarted)
        {
            return;
        }

        _mapRevealStarted = true;
        await MapHelpers.WaitForFirstFrameAsync(Map);
        MapCoverFadeOut.Begin();
    }

    private void MapCoverFadeOut_Completed(object sender, object e) => MapCover.Visibility = Visibility.Collapsed;

    // ---- Modes ----

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0 || (FlyoutMode)index == _mode)
        {
            return;
        }

        _mode = (FlyoutMode)index;
        SettingsService.FlyoutMode = _mode;
        if (_isPopupVisible)
        {
            EnterMode(reframe: true);
        }
    }

    /// <summary>Shows the current mode's stops, route lines, and arrivals, loading as needed.</summary>
    private void EnterMode(bool reframe)
    {
        ClearVehicles();
        _routeLayer.IsVisible = _mode == FlyoutMode.Saved;
        _placeLayer.MapElements.Clear();

        if (_mode == FlyoutMode.Saved)
        {
            _placeTimer.Stop();
            ++_placeVersion; // Drop any in-flight place load.
            if (_savedStopsDirty)
            {
                _savedStopsDirty = false;
                _ = RebuildRoutesAsync();
            }

            RebuildStopPins();
            Render();
            if (reframe)
            {
                _ = FrameListedStopsAsync(MapAnimationKind.None);
            }

            _ = RefreshVehiclesAsync();
            return;
        }

        _placeOrigin = null;
        _placeStops = [];
        _placeArrivals = [];
        _placeFetchedAt = null;
        _placeError = null;
        _placeNotice = null;
        RebuildStopPins();
        Render();
        _placeTimer.Start();
        _ = LoadPlaceAsync(reframe);
    }

    /// <summary>Finds the stops nearest the mode's place and fetches their arrivals.</summary>
    private async Task LoadPlaceAsync(bool reframe)
    {
        FlyoutMode mode = _mode;
        if (mode == FlyoutMode.Saved)
        {
            return;
        }

        int version = ++_placeVersion;
        bool Stale() => version != _placeVersion || mode != _mode;
        _placeLoading = true;
        Render();

        try
        {
            GeoPoint? origin = await GetPlaceOriginAsync(mode);
            if (Stale())
            {
                return;
            }

            if (origin is not { } point)
            {
                return; // GetPlaceOriginAsync set _placeNotice.
            }

            IReadOnlyList<IndexedStop> directory = await _stopIndex.GetAsync();
            IReadOnlyList<NearbyStop> nearby = NearbyStop.Find(directory, point, MaxNearbyStops);
            IReadOnlyList<Arrival> arrivals = nearby.Count == 0 ? [] : await _monitor.Service.GetArrivalsAsync(nearby.Select(n => n.Stop.Id).ToList());
            if (_routes.Count == 0)
            {
                _routes = await _monitor.GetRouteCatalogAsync();
            }

            if (Stale())
            {
                return;
            }

            bool moved = _placeOrigin is not { } previous || GeoMath.DistanceMeters(previous, point) > 25;
            _placeOrigin = point;
            _placeStops = nearby;
            _placeArrivals = arrivals;
            _placeFetchedAt = DateTimeOffset.Now;
            _placeError = null;
            _placeNotice = nearby.Count == 0
                ? ("No stops nearby", $"There are no {_monitor.Service.AgencyName} stops within about a mile of {PlaceName(mode)}.", null)
                : null;

            if (moved)
            {
                RebuildStopPins();
                ShowPlaceMarker(mode, point);
            }

            if (reframe)
            {
                _ = FrameListedStopsAsync(MapAnimationKind.None);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            if (!Stale())
            {
                _placeError = ex is TransitApiException api ? api.Message
                    : ConnectivityService.IsInternetAvailable ? $"Can't reach {_monitor.Service.AgencyName} right now. Retrying shortly."
                    : "No internet connection. Times will update when you're back online.";
            }
        }
        finally
        {
            if (!Stale())
            {
                _placeLoading = false;
                Render();
                _ = RefreshVehiclesAsync();
            }
        }
    }

    private async Task<GeoPoint?> GetPlaceOriginAsync(FlyoutMode mode)
    {
        if (!_monitor.Service.IsConfigured)
        {
            _placeNotice = ("API key needed", $"Add your {_monitor.Service.AgencyName} key to OAuth.resw and rebuild.", null);
            return null;
        }

        if (mode == FlyoutMode.Nearby)
        {
            string? error = null;
            GeoPoint? here = await LocationService.TryGetDeviceLocationAsync(TimeSpan.FromSeconds(10), message => error = message);
            if (here is null)
            {
                _placeNotice = ("Can't find your location", error ?? "Try again in a moment.", "Try again");
            }

            return here;
        }

        Place? place = mode == FlyoutMode.Home ? SettingsService.Home : SettingsService.Work;
        if (place is null)
        {
            string name = mode == FlyoutMode.Home ? "home" : "work";
            _placeNotice = ($"Set your {name} address", $"Tray Times will show live arrivals at the stops closest to {name}.", "Open settings");
            return null;
        }

        return new GeoPoint(place.Lat, place.Lon);
    }

    private static string PlaceName(FlyoutMode mode) => mode switch
    {
        FlyoutMode.Home => "home",
        FlyoutMode.Work => "work",
        _ => "you",
    };

    private void ShowPlaceMarker(FlyoutMode mode, GeoPoint point)
    {
        string glyph = mode switch
        {
            FlyoutMode.Home => "", // HomeSolid
            FlyoutMode.Work => "", // solid briefcase
            _ => "",
        };
        _placeLayer.MapElements.Clear();
        _placeLayer.MapElements.Add(new MapIcon(MapHelpers.CreateGlyph(glyph, Microsoft.UI.Colors.DeepSkyBlue, 24), MapHelpers.ToGeopoint(point))
        {
            NormalizedAnchorPoint = new Point(0.5, 0.5),
        });
    }

    // ---- Rendering ----

    private void Render()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        (string Title, string Text, string? Button, bool Busy)? empty = null;
        string? error;
        DateTimeOffset? fetchedAt;
        bool loading;

        if (_mode == FlyoutMode.Saved)
        {
            ArrivalsSnapshot snapshot = _monitor.Latest;
            error = snapshot.Error;
            fetchedAt = snapshot.FetchedAt;
            loading = _monitor.IsLoading;
            string emptyText = fetchedAt is null ? string.Empty : "No upcoming arrivals.";
            _groups = saved
                .Select(stop => new StopGroupItem(
                    stop.StopId,
                    stop.StopName,
                    stop.AllRoutes ? "All routes" : stop.Description,
                    new GeoPoint(stop.Lat, stop.Lon),
                    snapshot.For(stop).Take(ArrivalsPerStop)
                        .Select(a => new ArrivalItem(a, new GeoPoint(stop.Lat, stop.Lon), RouteColor(a.RouteId, stop.RouteId, stop.RouteColor), now))
                        .ToList(),
                    emptyText))
                .ToList();
            if (saved.Count == 0)
            {
                empty = ("No saved stops", "Save the stops you ride from, or use the Home, Work, and Nearby tabs.", "Add a stop", false);
            }
        }
        else
        {
            error = _placeError;
            fetchedAt = _placeFetchedAt;
            loading = _placeLoading;
            HashSet<string> savedIds = saved.Select(s => s.StopId).ToHashSet();
            string emptyText = fetchedAt is null ? string.Empty : "No upcoming arrivals.";
            _groups = _placeStops
                .Select(n => BuildNearbyGroup(n, savedIds.Contains(n.Stop.Id), emptyText, now))
                .ToList();

            if (_placeNotice is { } notice)
            {
                empty = (notice.Title, notice.Text, notice.Button, false);
            }
            else if (_placeStops.Count == 0)
            {
                empty = _stopIndex.IsBuilding
                    ? ("Getting the stop list", $"Downloading every {_monitor.Service.AgencyName} stop. This happens once a week and takes a minute.", null, true)
                    : ("Finding stops…", string.Empty, null, true);
            }
        }

        LoadingRing.IsActive = loading;
        LiveDot.Opacity = error is null ? 1 : 0.35;
        UpdatedText.Text = fetchedAt is { } at ? $"Updated {at.ToLocalTime():t}" : loading ? "Loading…" : string.Empty;
        StatusBar.IsOpen = error is not null;
        StatusBar.Message = error ?? string.Empty;
        StatusBar.Title = fetchedAt is null ? "Can't load arrivals" : "Showing earlier times";

        StopsList.ItemsSource = _groups;
        EmptyState.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
        if (empty is { } e)
        {
            EmptyTitle.Text = e.Title;
            EmptyText.Text = e.Text;
            EmptyText.Visibility = e.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyRing.IsActive = e.Busy;
            EmptyRing.Visibility = e.Busy ? Visibility.Visible : Visibility.Collapsed;
            EmptyButton.Content = e.Button;
            EmptyButton.Visibility = e.Button is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private StopGroupItem BuildNearbyGroup(NearbyStop nearby, bool isSaved, string emptyText, DateTimeOffset now)
    {
        IndexedStop stop = nearby.Stop;
        int walkMinutes = (int)Math.Ceiling(nearby.Meters / WalkingMetersPerMinute);
        string[] lines = stop.Routes.Select(r => r.RouteDisplayId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string lineText = lines.Length == 0 ? string.Empty
            : lines.Length <= 5 ? $" · {string.Join(", ", lines)}"
            : $" · {string.Join(", ", lines.Take(5))} +{lines.Length - 5}";
        string subtitle = $"{FormatDistance(nearby.Meters)} · {walkMinutes} min walk{lineText}";

        // Only "Nearby" knows the rider is actually there, so only it flags buses they can't reach.
        bool checkWalk = _mode == FlyoutMode.Nearby;
        List<ArrivalItem> arrivals = _placeArrivals
            .Where(a => a.StopId == stop.Id)
            .Take(ArrivalsPerNearbyStop)
            .Select(a => new ArrivalItem(
                a,
                stop.Location,
                RouteColor(a.RouteId, null, stop.Routes.FirstOrDefault(r => string.Equals(r.RouteId, a.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor),
                now,
                tooSoonToWalk: checkWalk && a.Status != ArrivalStatus.Canceled && a.MinutesAway(now) < walkMinutes))
            .ToList();

        return new StopGroupItem(stop.Id, stop.Name, subtitle, stop.Location, arrivals, emptyText, stop, isSaved);
    }

    private static string FormatDistance(double meters)
    {
        double miles = meters / 1609.344;
        return miles < 0.1
            ? $"{Math.Max(50, Math.Round(meters * 3.28084 / 50) * 50).ToString("N0", CultureInfo.CurrentCulture)} ft"
            : $"{miles.ToString("0.0", CultureInfo.CurrentCulture)} mi";
    }

    /// <summary>A route's color: the known one if given, else from the route catalog.</summary>
    private Color RouteColor(string routeId, string? knownRouteId, string? knownColor) =>
        knownColor is not null && (knownRouteId is null || string.Equals(routeId, knownRouteId, StringComparison.OrdinalIgnoreCase))
            ? MapHelpers.ParseRouteColor(knownColor)
            : MapHelpers.ParseRouteColor(_routes.TryGetValue(routeId, out TransitRoute? route) ? route.Color : null);

    // ---- Events ----

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        _savedStopsDirty = true;
        if (!_isPopupVisible)
        {
            return;
        }

        if (_mode == FlyoutMode.Saved)
        {
            EnterMode(reframe: true);
        }
        else
        {
            Render(); // Update the save stars.
        }
    }

    private void OnPlacesChanged(object? sender, EventArgs e)
    {
        if (_isPopupVisible && _mode is FlyoutMode.Home or FlyoutMode.Work)
        {
            EnterMode(reframe: true);
        }
    }

    private void OnStopIndexChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isPopupVisible && _mode != FlyoutMode.Saved)
        {
            Render();
        }
    });

    private async void OnArrivalsUpdated(object? sender, EventArgs e)
    {
        if (_mode != FlyoutMode.Saved)
        {
            return;
        }

        if (_routes.Count == 0 && _monitor.Service.IsConfigured && SettingsService.Stops.Any(s => s.AllRoutes))
        {
            // All-routes stops need other routes' colors; the catalog is cached after the first load.
            _routes = await _monitor.GetRouteCatalogAsync();
        }

        Render();
        if (_isPopupVisible)
        {
            _ = RefreshVehiclesAsync();
        }
    }

    // ---- Map ----

    private Task FrameListedStopsAsync(MapAnimationKind animation)
    {
        List<GeoPoint> points = _mode == FlyoutMode.Saved
            ? SettingsService.Stops.Select(s => new GeoPoint(s.Lat, s.Lon)).Distinct().ToList()
            : _placeStops.Select(n => n.Stop.Location).ToList();
        if (_mode != FlyoutMode.Saved && _placeOrigin is { } origin)
        {
            points.Add(origin);
        }

        return MapHelpers.FrameAsync(Map, points, animation);
    }

    private void RebuildStopPins()
    {
        IEnumerable<(string Name, GeoPoint Location)> stops = _mode == FlyoutMode.Saved
            ? SettingsService.Stops.DistinctBy(s => s.StopId).Select(s => (s.StopName, new GeoPoint(s.Lat, s.Lon)))
            : _placeStops.Select(n => (n.Stop.Name, n.Stop.Location));

        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        foreach ((string name, GeoPoint location) in stops)
        {
            _stopPins[new MapIcon(_stopGlyph, MapHelpers.ToGeopoint(location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) }] = name;
        }

        _stopLayer.MapElements.AddRange(_stopPins.Keys);
    }

    /// <summary>Draws each saved stop's route (in its direction) in the route's color.</summary>
    private async Task RebuildRoutesAsync()
    {
        IReadOnlyList<SavedStop> stops = SettingsService.Stops;
        List<MapPolyline> lines = [];
        foreach (IGrouping<string, SavedStop> byRoute in stops.GroupBy(s => s.RouteId, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<RoutePattern> patterns = await GetPatternsAsync(byRoute.Key);
            Color color = MapHelpers.ParseRouteColor(byRoute.First().RouteColor);
            foreach (RoutePattern pattern in patterns.Where(p => byRoute.Any(s => s.AllRoutes || string.Equals(s.DirectionId, p.DirectionId, StringComparison.OrdinalIgnoreCase))))
            {
                lines.Add(new MapPolyline
                {
                    Path = new Geopath(pattern.Points.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
                    StrokeColor = Color.FromArgb(200, color.R, color.G, color.B),
                    StrokeThickness = 4,
                });
            }
        }

        _routeLayer.MapElements.Clear();
        _routeLayer.MapElements.AddRange(lines);
    }

    private async Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId)
    {
        if (_patternCache.TryGetValue(routeId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < PatternCacheLifetime)
        {
            return cached.Patterns;
        }

        try
        {
            IReadOnlyList<RoutePattern> patterns = await _monitor.Service.GetPatternsAsync(routeId);
            _patternCache[routeId] = (DateTime.UtcNow, patterns);
            return patterns;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            // Route lines are decoration; the next open retries.
            return [];
        }
    }

    /// <summary>Moves the bus icons for the soonest arrivals in the current list.</summary>
    private async Task RefreshVehiclesAsync()
    {
        if (!_monitor.Service.IsConfigured || !_isPopupVisible)
        {
            return;
        }

        FlyoutMode mode = _mode;
        List<ArrivalItem> upcoming = _groups
            .SelectMany(g => g.Arrivals)
            .Where(a => a.Arrival.VehicleId is not null && a.Arrival.Status != ArrivalStatus.Canceled)
            .OrderBy(a => a.Arrival.PredictedTime)
            .DistinctBy(a => a.Arrival.VehicleId)
            .Take(_monitor.Service.MaxStopsPerRequest)
            .ToList();

        IReadOnlyList<Vehicle> vehicles;
        try
        {
            vehicles = upcoming.Count == 0 ? [] : await _monitor.Service.GetVehiclesAsync(upcoming.Select(a => a.Arrival.VehicleId!).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            return;
        }

        if (mode != _mode)
        {
            return;
        }

        Dictionary<string, Color> colors = upcoming.ToDictionary(a => a.Arrival.VehicleId!, a => a.RouteColor);
        HashSet<string> seen = [];
        foreach (Vehicle vehicle in vehicles)
        {
            seen.Add(vehicle.Id);
            _vehicles[vehicle.Id] = vehicle;
            Geopoint location = MapHelpers.ToGeopoint(vehicle.Location);
            FontIcon glyph = BusGlyph(colors.GetValueOrDefault(vehicle.Id, MapHelpers.ParseRouteColor(null)));
            if (_vehicleIcons.TryGetValue(vehicle.Id, out MapIcon? icon))
            {
                icon.Location = location;
                icon.IconElement = glyph;
            }
            else
            {
                icon = new MapIcon(glyph, location) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
                _vehicleIcons[vehicle.Id] = icon;
                _vehicleLayer.MapElements.Add(icon);
            }
        }

        foreach (string gone in _vehicleIcons.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _vehicleLayer.MapElements.Remove(_vehicleIcons[gone]);
            _vehicleIcons.Remove(gone);
            _vehicles.Remove(gone);
        }
    }

    private void ClearVehicles()
    {
        _vehicleLayer.MapElements.Clear();
        _vehicleIcons.Clear();
        _vehicles.Clear();
    }

    /// <summary>One shared glyph per color so identical bus icons share a raster.</summary>
    private FontIcon BusGlyph(Color color)
    {
        if (!_busGlyphs.TryGetValue(color, out FontIcon? glyph))
        {
            glyph = MapHelpers.CreateGlyph(MapHelpers.BusGlyph, color, 22);
            _busGlyphs[color] = glyph;
        }

        return glyph;
    }

    private void VehicleLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _vehicleIcons.FirstOrDefault(p => p.Value == icon).Key is { } id && _vehicles.TryGetValue(id, out Vehicle? vehicle))
        {
            args.Handled = true;
            ShowMapCaption($"Bus {vehicle.Id} · Route {vehicle.RouteId} to {vehicle.Destination}{(vehicle.IsDelayed ? " · Delayed" : string.Empty)}");
        }
    }

    private void StopLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _stopPins.TryGetValue(icon, out string? name))
        {
            args.Handled = true;
            ShowMapCaption(name);
        }
    }

    private void ShowMapCaption(string text)
    {
        MapCaptionText.Text = text;
        MapCaption.Visibility = Visibility.Visible;
        _captionTimer.Stop();
        _captionTimer.Start();
    }

    // ---- List interactions ----

    private void ArrivalButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArrivalItem item)
        {
            return;
        }

        List<GeoPoint> points = [item.StopLocation];
        if (item.Arrival.VehicleId is { } vid && _vehicles.TryGetValue(vid, out Vehicle? vehicle))
        {
            points.Add(vehicle.Location);
        }

        _ = MapHelpers.FrameAsync(Map, points, MapAnimationKind.Default);
    }

    private void StopHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StopGroupItem group)
        {
            _ = MapHelpers.FrameAsync(Map, [group.Location], MapAnimationKind.Default, singlePointZoom: 16);
        }
    }

    /// <summary>Saves a nearby stop (all routes) to the Saved tab, or removes it if already saved.</summary>
    private void SaveStopButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StopGroupItem { Saveable: { } stop } group)
        {
            return;
        }

        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        if (group.IsSaved)
        {
            SettingsService.SetStops(saved.Where(s => s.StopId != stop.Id));
            ShowMapCaption($"Removed {stop.Name} from your stops.");
            return;
        }

        if (SavedStop.DistinctStopCount(saved) >= _monitor.Service.MaxStopsPerRequest)
        {
            ShowMapCaption($"You can save up to {_monitor.Service.MaxStopsPerRequest} stops. Remove one in settings first.");
            return;
        }

        StopRouteInfo? route = stop.Routes.FirstOrDefault();
        SettingsService.AddStop(new SavedStop(
            stop.Id,
            stop.Name,
            stop.Lat,
            stop.Lon,
            route?.RouteId ?? string.Empty,
            route?.RouteDisplayId ?? string.Empty,
            route?.RouteColor,
            route?.DirectionId ?? string.Empty,
            route?.DirectionName ?? string.Empty,
            AllRoutes: true));
        ShowMapCaption($"Saved {stop.Name}.");
    }

    private void EmptyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == FlyoutMode.Nearby)
        {
            EnterMode(reframe: true);
        }
        else
        {
            App.Current.ShowSettings();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_mode == FlyoutMode.Saved)
        {
            _ = _monitor.RefreshAsync();
        }
        else
        {
            _ = LoadPlaceAsync(reframe: false);
        }
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => _ = FrameListedStopsAsync(MapAnimationKind.Default);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Refresh();
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HidePopup();
    }
}
