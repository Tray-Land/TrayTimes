using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayTimes.Commute;
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
/// Borderless arrivals popup shown next to the tray icon. It lists the favorite stops, the stops
/// with a bus to wherever the rider is likely headed next (Commute), or the stops nearest the
/// device, and light-dismisses when focus leaves. Hiding keeps it
/// warm for a quick reopen; the app closes it for good (<see cref="CloseWindow"/>) a minute later.
/// </summary>
public sealed partial class TimesFlyoutWindow : WindowEx
{
    private const int PopupWidth = 400;
    private const int PopupHeight = 680;
    private const int ArrivalsPerStop = 4;
    private const int ArrivalsPerNearbyStop = 3;
    private const int MaxNearbyStops = 8;
    private const int MaxRoutedStops = 12; // walking routes looked up per refresh
    private const double WalkingMetersPerMinute = 80; // about 3 mph
    private const double MovedMeters = 25; // Closer than this, a new fix doesn't change which stops are near.
    private static readonly TimeSpan PendingPopWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PlaceRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PatternCacheLifetime = TimeSpan.FromHours(6);

    private readonly HWND _hwnd;
    private readonly ArrivalsMonitor _monitor;
    private readonly BusTrackingService _tracker;
    private readonly StopIndexService _stopIndex;
    private readonly VehiclePositionsMonitor _vehiclePositions;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _vehicleTimer;
    private readonly DispatcherQueueTimer _placeTimer;
    private readonly DispatcherQueueTimer _captionTimer;

    // Draw order: route lines, then stops, then the place markers, then buses on top
    // (each bus is a white disc with the bus emoji over it, or a larger yellow disc when selected).
    private readonly MapElementsLayer _routeLayer = new();
    private readonly MapElementsLayer _commuteRouteLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly MapElementsLayer _placeLayer = new();
    private readonly MapElementsLayer _vehicleBackdropLayer = new();
    private readonly MapElementsLayer _vehicleLayer = new();
    private readonly Dictionary<string, (MapIcon Bus, MapIcon Backdrop, MapIcon Arrow)> _vehicleIcons = [];
    private readonly Dictionary<string, (int Heading, double MapHeading, bool Selected, bool DarkMap)> _vehicleArrowStates = [];
    private readonly Dictionary<MapIcon, string> _stopPins = [];
    private readonly Dictionary<string, Vehicle> _vehicles = [];
    private readonly Dictionary<string, (DateTime FetchedUtc, IReadOnlyList<RoutePattern> Patterns)> _patternCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly FontIcon _stopGlyph;
    private readonly FontIcon _busGlyph;
    private readonly FontIcon _busBackdropGlyph;
    private readonly FontIcon _selectedBusBackdropGlyph;
    private readonly long _mapHeadingCallbackToken;
    private string? _selectedVehicleId;

    private IReadOnlyDictionary<string, TransitRoute> _routes = new Dictionary<string, TransitRoute>();
    private IReadOnlyList<StopGroupItem> _groups = [];
    private FlyoutMode _mode;
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _mapRevealStarted;
    private bool _savedStopsDirty = true;
    private DateTime _lastDismissedAtUtc;

    private Action? _emptyAction;

    // Commute/Nearby state.
    private GeoPoint? _placeOrigin;
    private IReadOnlyList<NearbyStop> _placeStops = [];
    private IReadOnlyList<Arrival> _placeArrivals = [];
    private IReadOnlyDictionary<Arrival, DateTimeOffset> _destinationEtas = new Dictionary<Arrival, DateTimeOffset>();
    private DateTimeOffset? _placeFetchedAt;
    private string? _placeError;
    private Notice? _placeNotice;
    private bool _placeLoading;
    private int _placeVersion;
    private CommuteLeg? _commuteLeg;
    private IReadOnlySet<RouteDirection> _directRoutes = new HashSet<RouteDirection>();
    private string? _destinationId; // The place picked from Going to, or null to guess.

    // Motion state: which rows have already animated in, so a refresh only animates what's new or changed.
    private readonly HashSet<string> _shownRows = [];
    private readonly Dictionary<string, string> _lastMinutes = [];
    private string? _listSignature;
    private int _entranceStep;
    private (string Key, DateTime Until)? _pendingPop; // A bell or star just clicked, to animate once its row is rebuilt.
    private DateTimeOffset? _lastFetchedAt;
    private bool _captionShown;
    private int _captionVersion;

    internal TimesFlyoutWindow(ArrivalsMonitor monitor, BusTrackingService tracker, StopIndexService stopIndex)
    {
        InitializeComponent();
        _monitor = monitor;
        _tracker = tracker;
        _stopIndex = stopIndex;
        _vehiclePositions = new VehiclePositionsMonitor(_monitor.Service, UpdateVehicles);
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), repeating: false, HideIfFocusLeftWindow);
        _vehicleTimer = CreateTimer(VehiclePositionsMonitor.RefreshInterval, repeating: true, () => _ = RefreshVehiclesAsync(TransitTrigger.Timer));
        _placeTimer = CreateTimer(PlaceRefreshInterval, repeating: true, () => _ = LoadPlaceAsync(reframe: false, TransitTrigger.Timer));
        _captionTimer = CreateTimer(TimeSpan.FromSeconds(5), repeating: false, () => HideMapCaption(animate: true));

        _stopGlyph = new FontIcon { Glyph = MapHelpers.StopGlyph, FontSize = 14 };
        // Shared so every bus reuses one emoji raster.
        _busGlyph = MapHelpers.CreateEmoji(MapHelpers.BusEmoji, 19.5);
        _busBackdropGlyph = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, Microsoft.UI.Colors.White, 28);
        _selectedBusBackdropGlyph = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, Microsoft.UI.Colors.Yellow, 38);
        _mapHeadingCallbackToken = Map.RegisterPropertyChangedCallback(WinUIEx.Maps.MapControl.HeadingProperty, (_, _) => UpdateVehicleArrows());
        MapHelpers.ApplyStopColor(_stopGlyph, Root);
        Map.Layers.Add(_routeLayer);
        Map.Layers.Add(_commuteRouteLayer);
        Map.Layers.Add(_stopLayer);
        Map.Layers.Add(_placeLayer);
        Map.Layers.Add(_vehicleBackdropLayer);
        Map.Layers.Add(_vehicleLayer);
        _vehicleLayer.Tapped += VehicleLayer_Tapped;
        _vehicleBackdropLayer.Tapped += VehicleLayer_Tapped; // The disc's rim is part of the bus too.
        _stopLayer.Tapped += StopLayer_Tapped;

        TitleText.Text = $"{_monitor.Service.AgencyName} arrivals";
        Root.ActualThemeChanged += (_, _) =>
        {
            MapHelpers.ApplyBasemap(Map, Root, MapCover);
            MapHelpers.ApplyStopColor(_stopGlyph, Root);
            UpdateVehicleArrows();
        };
        MapHelpers.ApplyBasemap(Map, Root, MapCover);
        RestoreMapView();

        _monitor.Updated += OnArrivalsUpdated;
        _tracker.Changed += OnTrackingChanged;
        _stopIndex.Changed += OnStopIndexChanged;
        _monitor.Schedule.Loaded += OnScheduleLoaded;
        SettingsService.StopsChanged += OnStopsChanged;
        SettingsService.PlacesChanged += OnPlacesChanged;

        _mode = SettingsService.FlyoutMode;
        ModeBar.SelectedItem = ModeBar.Items[(int)_mode];
        Render();
    }

    /// <summary>Raised when the popup is dismissed (the window stays alive until closed).</summary>
    public event EventHandler? PopupHidden;

    public bool IsPopupVisible => _isPopupVisible;

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
        _vehiclePositions.IsForeground = true;

        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        RevealMapOnFirstShow();
        Motion.Enter(Toolbar, step: 0, rise: 10);
        Motion.Enter(ModeBar, step: 1, rise: 10);

        _monitor.IsForeground = true;
        _destinationId = null; // Each open guesses the next trip afresh.
        // Open where the map was left; only frame the stops when there's no previous view.
        EnterMode(reframe: SettingsService.FlyoutMapView is null, TransitTrigger.FlyoutOpened);
        _vehicleTimer.Start();
    }

    public void HidePopup()
    {
        if (!_isPopupVisible)
        {
            return;
        }

        _isPopupVisible = false;
        _vehiclePositions.IsForeground = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _captionTimer.Stop();
        HideMapCaption(animate: false);
        _monitor.IsForeground = false;
        SaveMapView();
        AppWindow.Hide();
        PopupHidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window for real, releasing the map, its timers, and event subscriptions.</summary>
    public void CloseWindow()
    {
        _allowClose = true;
        HidePopup();
        ++_placeVersion; // Drop any in-flight place load.
        _monitor.Updated -= OnArrivalsUpdated;
        _tracker.Changed -= OnTrackingChanged;
        _stopIndex.Changed -= OnStopIndexChanged;
        _monitor.Schedule.Loaded -= OnScheduleLoaded;
        SettingsService.StopsChanged -= OnStopsChanged;
        SettingsService.PlacesChanged -= OnPlacesChanged;
        Map.UnregisterPropertyChangedCallback(WinUIEx.Maps.MapControl.HeadingProperty, _mapHeadingCallbackToken);
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

    private void RestoreMapView()
    {
        if (SettingsService.FlyoutMapView is { } view)
        {
            Map.Center = MapHelpers.ToGeopoint(new GeoPoint(view.Lat, view.Lon));
            Map.ZoomLevel = view.Zoom;
        }
    }

    private void SaveMapView()
    {
        if (Map.Center is { } center)
        {
            SettingsService.FlyoutMapView = new MapView(center.Position.Latitude, center.Position.Longitude, Map.ZoomLevel);
        }
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

    private void SelectMode(FlyoutMode mode) => ModeBar.SelectedItem = ModeBar.Items[(int)mode];

    private TransitConsumer RequestConsumer => _mode switch
    {
        FlyoutMode.Favorites => TransitConsumer.Favorites,
        FlyoutMode.Commute => TransitConsumer.Commute,
        _ => TransitConsumer.Nearby,
    };

    /// <summary>Shows the current mode's stops, route lines, and arrivals, loading as needed.</summary>
    private void EnterMode(bool reframe, TransitTrigger trigger = TransitTrigger.ModeChanged)
    {
        _vehiclePositions.Invalidate();
        // A fresh list: every row rises in again rather than silently swapping.
        _listSignature = null;
        _shownRows.Clear();
        _lastMinutes.Clear();
        ClearVehicles();
        _routeLayer.IsVisible = _mode == FlyoutMode.Favorites;
        _commuteRouteLayer.MapElements.Clear();
        _placeLayer.MapElements.Clear();

        if (_mode == FlyoutMode.Favorites)
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

            _ = RefreshVehiclesAsync(trigger);
            return;
        }

        _placeOrigin = null;
        _placeStops = [];
        _placeArrivals = [];
        _destinationEtas = new Dictionary<Arrival, DateTimeOffset>();
        _placeFetchedAt = null;
        _placeError = null;
        _placeNotice = null;
        _commuteLeg = null;
        _directRoutes = new HashSet<RouteDirection>();
        RebuildStopPins();
        Render();
        _placeTimer.Start();
        _ = LoadPlaceAsync(reframe, trigger);
    }

    /// <summary>Finds the mode's stops (the commute's boarding stops, or those near the device) and fetches their arrivals.</summary>
    private async Task LoadPlaceAsync(bool reframe, TransitTrigger trigger)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(RequestConsumer, trigger);
        FlyoutMode mode = _mode;
        if (mode == FlyoutMode.Favorites)
        {
            return;
        }

        int version = ++_placeVersion;
        bool Stale() => version != _placeVersion || mode != _mode;
        _placeLoading = true;
        Render();

        try
        {
            if (!_monitor.Service.IsConfigured)
            {
                await ShowPlaceAsync(PlaceStops.Unavailable(new Notice("API key needed", $"Add your {_monitor.Service.AgencyName} key to OAuth.resw and rebuild.")), reframe, Stale);
                return;
            }

            // Warm the stop directory and route list while the location comes in.
            _ = _stopIndex.GetAsync();
            if (_routes.Count == 0)
            {
                _ = _monitor.GetRouteCatalogAsync(RequestConsumer);
            }

            // On opening, a rough fix gets the list up quickly; the precise one then corrects it if it
            // lands elsewhere. Refreshes already have a precise origin, so they wait for another.
            string? locationError = null;
            (GeoPoint? here, Task<GeoPoint?>? refined) = _placeOrigin is null
                ? await LocationService.TryGetQuickLocationAsync(LocationTimeout, message => locationError = message)
                : (await LocationService.TryGetDeviceLocationAsync(LocationTimeout, message => locationError = message), null);
            await ShowPlaceAsync(await FindPlaceStopsAsync(mode, here, locationError), reframe, Stale);

            if (refined is not null && await refined is { } precise && here is { } rough
                && GeoMath.DistanceMeters(rough, precise) > MovedMeters && !Stale())
            {
                using IDisposable refinedScope = TransitRequestDiagnostics.BeginScope(RequestConsumer, TransitTrigger.RefinedLocation);
                await ShowPlaceAsync(await FindPlaceStopsAsync(mode, precise, null), reframe, Stale);
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
            }
        }
    }

    /// <summary>Fetches the found stops' arrivals and shows them, unless the load has gone stale meanwhile.</summary>
    private async Task ShowPlaceAsync(PlaceStops found, bool reframe, Func<bool> stale)
    {
        string[] stopIds = [.. found.Stops.Select(n => n.Stop.Id)];
        IReadOnlyList<Arrival> arrivals = stopIds.Length == 0 ? [] : _monitor.Schedule.AddScheduled(await _monitor.Service.GetArrivalsAsync(stopIds), stopIds);
        if (found.Stops.Count > 0 && _routes.Count == 0)
        {
            _routes = await _monitor.GetRouteCatalogAsync(RequestConsumer);
        }

        if (stale())
        {
            return;
        }

        bool moved = _placeOrigin is not { } previous || found.Origin is not { } origin
            || GeoMath.DistanceMeters(previous, origin) > MovedMeters
            || _commuteLeg?.To.Id != found.Leg?.To.Id;
        _placeOrigin = found.Origin;
        _placeStops = found.Stops;
        _placeArrivals = arrivals;
        _destinationEtas = EstimateDestinationArrivals(found, arrivals);
        _commuteLeg = found.Leg;
        _directRoutes = found.DirectRoutes ?? new HashSet<RouteDirection>();
        _placeFetchedAt = found.Origin is null ? null : DateTimeOffset.Now;
        _placeError = null;
        _placeNotice = found.Stops.Count == 0 ? found.EmptyNotice : null;

        if (moved)
        {
            RebuildStopPins();
            ShowPlaceMarkers(found);
            DrawCommuteRoutes();
        }

        if (reframe)
        {
            _ = FrameListedStopsAsync(MapAnimationKind.None);
        }

        Render();
        _ = RefreshVehiclesAsync(TransitTrigger.PlaceResult);
    }

    private Task<PlaceStops> FindPlaceStopsAsync(FlyoutMode mode, GeoPoint? here, string? locationError) =>
        mode == FlyoutMode.Commute ? FindCommuteStopsAsync(here) : FindNearbyStopsAsync(here, locationError);

    private async Task<PlaceStops> FindNearbyStopsAsync(GeoPoint? here, string? locationError)
    {
        if (here is not { } point)
        {
            return PlaceStops.Unavailable(new Notice("Can't find your location", locationError ?? "Try again in a moment.", "Try again", () => EnterMode(reframe: true, TransitTrigger.Retry)));
        }

        // Straight-line distance picks the candidates; the walking route decides which is quickest to reach.
        IReadOnlyList<NearbyStop> candidates = NearbyStop.Find(await _stopIndex.GetAsync(), point, MaxRoutedStops);
        IReadOnlyList<NearbyStop> stops = [.. (await WalkingRouteService.RankByWalkAsync(point, candidates)).Take(MaxNearbyStops)];
        return new PlaceStops(point, stops, new Notice("No stops nearby", $"There are no {_monitor.Service.AgencyName} stops within about a mile of you."));
    }

    /// <summary>
    /// Works out where the rider is likely headed next, then finds the stops near them with a bus
    /// that gets there in one ride.
    /// </summary>
    /// <param name="here">The device's location, or null (it's off, or the PC can't tell), in which case the planner guesses from the time of day.</param>
    private async Task<PlaceStops> FindCommuteStopsAsync(GeoPoint? here)
    {
        IReadOnlyList<FavoritePlace> places = SettingsService.Places;
        Notice setUp = new(
            "Set your favorite places",
            "Add home, plus work or school, in settings. Commute then shows the buses to wherever you're headed next.",
            "Open settings",
            App.Current.ShowSettings);
        if (!places.Any(p => p.Kind == PlaceKind.Home))
        {
            return PlaceStops.Unavailable(setUp);
        }

        CommuteLeg? leg = CommutePlanner.Plan(places, here, DateTimeOffset.Now, places.FirstOrDefault(p => p.Id == _destinationId));
        if (leg is null)
        {
            return PlaceStops.Unavailable(setUp);
        }

        if (leg.IsAlreadyThere)
        {
            return PlaceStops.Unavailable(new Notice($"You're at {leg.To.Name}", "Use Going to, above, to pick somewhere else."), leg);
        }

        IReadOnlyList<IndexedStop> directory = await _stopIndex.GetAsync();
        List<NearbyStop> origin = [.. NearbyStop.Find(directory, leg.Origin, int.MaxValue, CommutePlanner.WalkRadiusMeters)];
        List<IndexedStop> originStops = [.. origin.Select(n => n.Stop)];
        List<NearbyStop> destination = [.. NearbyStop.Find(directory, leg.To.Location, int.MaxValue, CommutePlanner.WalkRadiusMeters)];
        List<IndexedStop> destinationStops = [.. destination.Select(n => n.Stop)];

        // Shapes tell a route's two directions apart, so only buses heading toward the destination are listed.
        IEnumerable<string> routeIds = CommutePlanner.SharedRoutes(originStops, destinationStops).Select(r => r.RouteId).Distinct(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<RoutePattern>> patterns = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string routeId, IReadOnlyList<RoutePattern> routePatterns) in await Task.WhenAll(routeIds.Select(async id => (id, await GetPatternsAsync(id)))))
        {
            patterns[routeId] = routePatterns;
        }

        IReadOnlySet<RouteDirection> direct = CommutePlanner.DirectRoutes(originStops, destinationStops, patterns);
        // Walk times reorder the closest few; the rest stay in straight-line order after them.
        IReadOnlyList<NearbyStop> byWalk = [.. await WalkingRouteService.RankByWalkAsync(leg.Origin, origin.Take(MaxRoutedStops).ToList()), .. origin.Skip(MaxRoutedStops)];

        // Quickest first, so a stop is kept only if it's the quickest walk for some direct line.
        HashSet<RouteDirection> claimed = [];
        List<NearbyStop> stops = [];
        foreach (NearbyStop n in byWalk)
        {
            bool nearest = false;
            foreach (StopRouteInfo r in n.Stop.Routes)
            {
                RouteDirection line = new(r.RouteId, r.DirectionId);
                if (direct.Contains(line) && claimed.Add(line))
                {
                    nearest = true;
                }
            }

            if (nearest)
            {
                stops.Add(n);
            }
        }

        stops = [.. stops.Take(MaxNearbyStops)];
        Notice noDirectBus = new(
            $"No direct bus to {leg.To.Name}",
            $"None of the buses within a 10-minute walk go within a 10-minute walk of {leg.To.Name}. Tray Times doesn't plan transfers yet.",
            "Show nearby stops",
            () => SelectMode(FlyoutMode.Nearby));
        return new PlaceStops(leg.Origin, stops, noDirectBus, leg, direct, stops.Count == 0 ? null : await WalkFromStopsAsync(leg.To.Location, destination));
    }

    /// <summary>Seconds to walk between each stop near <paramref name="place"/> and it, by stop ID. Routed for the closest few; the rest are estimated.</summary>
    private static async Task<IReadOnlyDictionary<string, double>> WalkFromStopsAsync(GeoPoint place, IReadOnlyList<NearbyStop> nearby)
    {
        IReadOnlyList<NearbyStop> routed = await WalkingRouteService.RankByWalkAsync(place, [.. nearby.Take(MaxRoutedStops)]);
        return routed.Concat(nearby.Skip(MaxRoutedStops)).ToDictionary(
            n => n.Stop.Id,
            n => n.WalkSeconds ?? n.Meters / WalkingMetersPerMinute * 60);
    }

    /// <summary>
    /// When the rider would reach their destination on each listed bus: the bus's departure, its
    /// scheduled ride (shifted by how late it's running), then the walk from where it lets them off.
    /// </summary>
    private IReadOnlyDictionary<Arrival, DateTimeOffset> EstimateDestinationArrivals(PlaceStops found, IReadOnlyList<Arrival> arrivals)
    {
        Dictionary<Arrival, DateTimeOffset> etas = [];
        if (found.DestinationWalks is not { Count: > 0 } walks
            || found.DirectRoutes is not { } direct
            || _monitor.Schedule.Timetable is not { } timetable)
        {
            return etas;
        }

        string[] alightAt = [.. walks.Keys];
        IEnumerable<Arrival> shown = arrivals
            .Where(a => a.Status == ArrivalStatus.Scheduled && direct.Contains(new RouteDirection(a.RouteId, a.DirectionId)))
            .GroupBy(a => a.StopId)
            .SelectMany(g => g.Take(ArrivalsPerNearbyStop));
        foreach (Arrival arrival in shown)
        {
            if (CommutePlanner.ArrivalAtDestination(timetable.GetDownstreamArrivals(arrival, alightAt), walks) is { } eta)
            {
                etas[arrival] = eta;
            }
        }

        return etas;
    }

    private void DestinationMenu_Opening(object sender, object e)
    {
        DestinationMenu.Items.Clear();
        DestinationMenu.Items.Add(DestinationItem("Where I'm likely headed", null, null));
        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (FavoritePlace place in SettingsService.Places)
        {
            DestinationMenu.Items.Add(DestinationItem(place.Name, place.Id, PlaceGlyph(place)));
        }

        DestinationMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem edit = new() { Text = "Edit places…", Icon = new FontIcon { Glyph = "" } }; // Edit
        edit.Click += (_, _) => App.Current.ShowSettings();
        DestinationMenu.Items.Add(edit);
    }

    private RadioMenuFlyoutItem DestinationItem(string text, string? placeId, string? glyph)
    {
        RadioMenuFlyoutItem item = new()
        {
            Text = text,
            GroupName = "Destination",
            IsChecked = placeId == _destinationId,
            Icon = glyph is null ? null : new FontIcon { Glyph = glyph },
        };
        item.Click += (_, _) =>
        {
            _destinationId = placeId;
            EnterMode(reframe: true, TransitTrigger.DestinationChanged);
        };
        return item;
    }

    private static string PlaceGlyph(FavoritePlace? place) => place?.Kind switch
    {
        PlaceKind.Home => "", // HomeSolid
        PlaceKind.Work => "", // Solid briefcase
        PlaceKind.School => "", // Education
        PlaceKind.Custom => "", // Pin
        _ => "", // Location: wherever the rider is
    };

    /// <summary>Marks where the list is from and, on a commute, where it's headed.</summary>
    private void ShowPlaceMarkers(PlaceStops found)
    {
        _placeLayer.MapElements.Clear();
        if (found.Origin is not { } origin)
        {
            return;
        }

        AddPlaceMarker(PlaceGlyph(found.Leg?.From), origin);
        if (found.Leg is { } leg)
        {
            AddPlaceMarker(PlaceGlyph(leg.To), leg.To.Location);
        }
    }

    private void AddPlaceMarker(string glyph, GeoPoint point) =>
        _placeLayer.MapElements.Add(new MapIcon(MapHelpers.CreateGlyph(glyph, Microsoft.UI.Colors.DeepSkyBlue, 24), MapHelpers.ToGeopoint(point))
        {
            NormalizedAnchorPoint = new Point(0.5, 0.5),
        });

    /// <summary>Draws the commute's direct routes, in their direction of travel.</summary>
    private void DrawCommuteRoutes()
    {
        _commuteRouteLayer.MapElements.Clear();
        foreach (RouteDirection route in _directRoutes)
        {
            if (_patternCache.TryGetValue(route.RouteId, out (DateTime FetchedUtc, IReadOnlyList<RoutePattern> Patterns) cached))
            {
                Color color = RouteColor(route.RouteId, null, null);
                _commuteRouteLayer.MapElements.AddRange(cached.Patterns
                    .Where(p => string.Equals(p.DirectionId, route.DirectionId, StringComparison.OrdinalIgnoreCase))
                    .Select(p => CreateRouteLine(p, color)));
            }
        }
    }

    // ---- Rendering ----

    private void Render()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        Notice? empty = null;
        bool busy = false;
        string? error;
        DateTimeOffset? fetchedAt;
        bool loading;
        IReadOnlyList<StopGroupItem> groups;

        if (_mode == FlyoutMode.Favorites)
        {
            ArrivalsSnapshot snapshot = _monitor.Latest;
            error = snapshot.Error;
            fetchedAt = snapshot.FetchedAt;
            loading = _monitor.IsLoading;
            string emptyText = fetchedAt is null ? string.Empty : "No upcoming arrivals.";
            Arrival? tracked = _tracker.Current?.FindListed(snapshot.Arrivals);
            groups = [.. saved
                .Select(stop => new StopGroupItem(
                    stop.StopId,
                    stop.StopName,
                    stop.AllRoutes ? "All routes" : stop.Description,
                    new GeoPoint(stop.Lat, stop.Lon),
                    [.. snapshot.For(stop).Take(ArrivalsPerStop)
                        .Select(a => new ArrivalItem(
                            a,
                            new GeoPoint(stop.Lat, stop.Lon),
                            RouteColor(a.RouteId, stop.RouteId, stop.RouteColor),
                            now,
                            tracking: TrackingFor(a, tracked)))],
                    emptyText))];
            if (saved.Count == 0)
            {
                empty = new Notice("No favorite stops", "Star the stops you ride from, or use the Commute and Nearby tabs.", "Add a stop", App.Current.ShowSettings);
            }
        }
        else
        {
            error = _placeError;
            fetchedAt = _placeFetchedAt;
            loading = _placeLoading;
            HashSet<string> savedIds = [.. saved.Select(s => s.StopId)];
            string emptyText = fetchedAt is null ? string.Empty : "No upcoming arrivals.";
            Arrival? tracked = _tracker.Current?.FindListed(_placeArrivals);
            groups = [.. _placeStops.Select(n => BuildNearbyGroup(n, savedIds.Contains(n.Stop.Id), emptyText, tracked, now))];

            if (_placeNotice is { } notice)
            {
                empty = notice;
            }
            else if (_placeStops.Count == 0)
            {
                busy = true;
                empty = _stopIndex.IsBuilding
                    ? new Notice("Getting the stop list", $"Downloading every {_monitor.Service.AgencyName} stop. This happens once a week and takes a minute.")
                    : new Notice("Finding stops…", string.Empty);
            }
        }

        LoadingRing.IsActive = loading;
        LiveDot.Opacity = error is null ? 1 : 0.35;
        UpdatedText.Text = fetchedAt is { } at ? $"Updated {at.ToLocalTime():t}" : loading ? "Loading…" : string.Empty;
        if (fetchedAt != _lastFetchedAt)
        {
            _lastFetchedAt = fetchedAt;
            if (fetchedAt is not null && _isPopupVisible)
            {
                // A heartbeat: fresh times just arrived.
                Motion.Pop(LiveDot, 2.4f);
                Motion.Enter(UpdatedText, rise: 4);
            }
        }

        StatusBar.IsOpen = error is not null;
        StatusBar.Message = error ?? string.Empty;
        StatusBar.Title = fetchedAt is null ? "Can't load arrivals" : "Showing earlier times";
        RenderCommuteHeader();
        ShowList(groups);

        bool emptyEntering = empty is { } next && (EmptyState.Visibility == Visibility.Collapsed || EmptyTitle.Text != next.Title);
        EmptyState.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
        _emptyAction = empty?.Action;
        if (empty is { } e)
        {
            EmptyTitle.Text = e.Title;
            EmptyStateText.Text = e.Text;
            EmptyStateText.Visibility = e.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyRing.IsActive = busy;
            EmptyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            EmptyButton.Content = e.Button;
            EmptyButton.Visibility = e.Button is null ? Visibility.Collapsed : Visibility.Visible;
            if (emptyEntering)
            {
                Motion.Enter(EmptyState, rise: 10);
            }
        }
    }

    /// <summary>
    /// Swaps in the new rows, but only when something visible changed: rebuilding the list restarts
    /// every row, which would cut short the animations of rows that just arrived.
    /// </summary>
    private void ShowList(IReadOnlyList<StopGroupItem> groups)
    {
        string signature = string.Join('\n', groups.Select(g => g.Signature));
        if (signature == _listSignature)
        {
            return;
        }

        _listSignature = signature;
        _groups = groups;

        // A row keeps its animation history only while it stays listed; one that leaves and returns is new again.
        HashSet<string> listed = [.. groups.SelectMany(g => g.Keys)];
        _shownRows.IntersectWith(listed);
        foreach (string gone in _lastMinutes.Keys.Where(k => !listed.Contains(k)).ToList())
        {
            _lastMinutes.Remove(gone);
        }

        _entranceStep = 0;
        StopsList.ItemsSource = groups;
    }

    /// <summary>Says where the Commute tab thinks the rider is going, and from where.</summary>
    private void RenderCommuteHeader()
    {
        if (_mode != FlyoutMode.Commute || _commuteLeg is not { } leg)
        {
            CommuteHeader.Visibility = Visibility.Collapsed;
            return;
        }

        string title = $"To {leg.To.Name}";
        bool entering = CommuteHeader.Visibility == Visibility.Collapsed || CommuteTitle.Text != title;
        CommuteHeader.Visibility = Visibility.Visible;
        CommuteGlyph.Glyph = PlaceGlyph(leg.To);
        CommuteTitle.Text = title;
        if (entering)
        {
            Motion.Enter(CommuteHeader, rise: 8);
        }

        CommuteSubtitle.Text = leg.IsGuess
            ? $"From {leg.From?.Name} · location unavailable, so guessed from the time of day"
            : $"From {leg.From?.Name ?? "where you are"}";
    }

    private StopGroupItem BuildNearbyGroup(NearbyStop nearby, bool isSaved, string emptyText, Arrival? tracked, DateTimeOffset now)
    {
        IndexedStop stop = nearby.Stop;
        bool commute = _mode == FlyoutMode.Commute;
        bool Listed(string routeId, string directionId) => !commute || _directRoutes.Contains(new RouteDirection(routeId, directionId));

        int walkMinutes = Math.Max(1, nearby.WalkMinutes(WalkingMetersPerMinute));
        string[] lines = [.. stop.Routes.Where(r => Listed(r.RouteId, r.DirectionId)).Select(r => r.RouteDisplayId).Distinct(StringComparer.OrdinalIgnoreCase)];
        string lineText = lines.Length == 0 ? string.Empty
            : lines.Length <= 5 ? $" · {string.Join(", ", lines)}"
            : $" · {string.Join(", ", lines.Take(5))} +{lines.Length - 5}";
        string subtitle = $"{FormatDistance(nearby.DistanceMeters)} · {walkMinutes} min walk{lineText}";

        // Stops on opposite sides of a street share a name, so say which way the buses go.
        string[] directions = [.. stop.Routes
            .Where(r => Listed(r.RouteId, r.DirectionId))
            .Select(r => FormatDirection(r.DirectionName))
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        string title = directions.Length == 0 ? stop.Name : $"{stop.Name} · {string.Join("/", directions)}";

        // Only a list from the rider's real location can flag buses they can't walk to in time.
        bool checkWalk = _mode == FlyoutMode.Nearby || (commute && _commuteLeg is { IsGuess: false });
        List<ArrivalItem> arrivals = [.. _placeArrivals
            .Where(a => a.StopId == stop.Id && Listed(a.RouteId, a.DirectionId))
            .Take(ArrivalsPerNearbyStop)
            .Select(a =>
            {
                bool tooSoon = checkWalk && a.Status != ArrivalStatus.Canceled && a.MinutesAway(now) < walkMinutes;
                return new ArrivalItem(
                    a,
                    stop.Location,
                    RouteColor(a.RouteId, null, stop.Routes.FirstOrDefault(r => string.Equals(r.RouteId, a.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor),
                    now,
                    tooSoonToWalk: tooSoon,
                    tracking: TrackingFor(a, tracked),
                    destinationName: commute ? _commuteLeg?.To.Name : null,
                    destinationEta: !tooSoon && _destinationEtas.TryGetValue(a, out DateTimeOffset eta) ? eta : null);
            })];

        return new StopGroupItem(stop.Id, title, subtitle, stop.Location, arrivals, emptyText, stop, isSaved);
    }

    /// <summary>BusTime's "NORTH" as "North".</summary>
    private static string FormatDirection(string direction) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(direction.Trim().ToLower(CultureInfo.CurrentCulture));

    private static ArrivalTracking TrackingFor(Arrival arrival, Arrival? tracked) =>
        arrival == tracked ? ArrivalTracking.Tracked
        : arrival.Status == ArrivalStatus.Canceled ? ArrivalTracking.Unavailable
        : ArrivalTracking.Available;

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

        if (_mode == FlyoutMode.Favorites)
        {
            EnterMode(reframe: true, TransitTrigger.StopsChanged);
        }
        else
        {
            Render(); // Update the save stars.
        }
    }

    private void OnTrackingChanged(object? sender, EventArgs e)
    {
        if (_isPopupVisible)
        {
            Render(); // Update the bells.
        }
    }

    private void OnPlacesChanged(object? sender, EventArgs e)
    {
        if (_isPopupVisible && _mode == FlyoutMode.Commute)
        {
            EnterMode(reframe: true, TransitTrigger.PlacesChanged);
        }
    }

    private void OnStopIndexChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isPopupVisible && _mode != FlyoutMode.Favorites)
        {
            Render();
        }
    });

    /// <summary>Scheduled times are now available: refetch the Commute/Nearby list to include them (Favorites refreshes itself).</summary>
    private void OnScheduleLoaded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isPopupVisible && _mode != FlyoutMode.Favorites && !_allowClose)
        {
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.TimetableLoaded);
        }
    });

    private async void OnArrivalsUpdated(object? sender, EventArgs e)
    {
        TransitTrigger trigger = _monitor.IsLoading ? TransitTrigger.FavoritesLoading : TransitTrigger.FavoritesResult;
        if (_mode != FlyoutMode.Favorites)
        {
            return;
        }

        if (_routes.Count == 0 && _monitor.Service.IsConfigured && SettingsService.Stops.Any(s => s.AllRoutes))
        {
            // All-routes stops need other routes' colors; the catalog is cached after the first load.
            _routes = await _monitor.GetRouteCatalogAsync();
            if (_allowClose || _mode != FlyoutMode.Favorites)
            {
                return;
            }
        }

        Render();
        if (_isPopupVisible && trigger == TransitTrigger.FavoritesResult && !_monitor.IsLoading)
        {
            _ = RefreshVehiclesAsync(trigger);
        }
    }

    // ---- Map ----

    private Task FrameListedStopsAsync(MapAnimationKind animation)
    {
        List<GeoPoint> points = _mode == FlyoutMode.Favorites
            ? [.. SettingsService.Stops.Select(s => new GeoPoint(s.Lat, s.Lon)).Distinct()]
            : [.. _placeStops.Select(n => n.Stop.Location)];
        if (_mode != FlyoutMode.Favorites && _placeOrigin is { } origin)
        {
            points.Add(origin);
        }

        return MapHelpers.FrameAsync(Map, points, animation);
    }

    private void RebuildStopPins()
    {
        IEnumerable<(string Name, GeoPoint Location)> stops = _mode == FlyoutMode.Favorites
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
                lines.Add(CreateRouteLine(pattern, color));
            }
        }

        if (_allowClose)
        {
            return;
        }

        _routeLayer.MapElements.Clear();
        _routeLayer.MapElements.AddRange(lines);
    }

    private static MapPolyline CreateRouteLine(RoutePattern pattern, Color color) => new()
    {
        Path = new Geopath(pattern.Points.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
        StrokeColor = Color.FromArgb(200, color.R, color.G, color.B),
        StrokeThickness = 4,
    };

    private async Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(RequestConsumer, TransitTrigger.Metadata);
        if (_patternCache.TryGetValue(routeId, out (DateTime FetchedUtc, IReadOnlyList<RoutePattern> Patterns) cached) && DateTime.UtcNow - cached.FetchedUtc < PatternCacheLifetime)
        {
            _monitor.Service.Diagnostics?.RecordReuse(TransitEndpoint.Patterns, inFlight: false, batchSize: 1);
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
    private async Task RefreshVehiclesAsync(TransitTrigger trigger, bool force = false)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(RequestConsumer, trigger);
        if (!_monitor.Service.IsConfigured || !_isPopupVisible)
        {
            return;
        }

        List<ArrivalItem> upcoming = [.. _groups
            .SelectMany(g => g.Arrivals)
            .Where(a => a.Arrival.VehicleId is not null && a.Arrival.Status != ArrivalStatus.Canceled)
            .OrderBy(a => a.Arrival.PredictedTime)
            .DistinctBy(a => a.Arrival.VehicleId)
            .Take(_monitor.Service.MaxStopsPerRequest)];

        try
        {
            await _vehiclePositions.RefreshAsync([.. upcoming.Select(a => a.Arrival.VehicleId!)], force);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            System.Diagnostics.Debug.WriteLine($"Vehicle positions refresh failed: {ex.GetType().Name}. Keeping last-good map positions.");
        }
    }

    private void UpdateVehicles(IReadOnlyList<Vehicle> vehicles)
    {
        HashSet<string> seen = [];
        foreach (Vehicle vehicle in vehicles)
        {
            seen.Add(vehicle.Id);
            _vehicles[vehicle.Id] = vehicle;
            Geopoint location = MapHelpers.ToGeopoint(vehicle.Location);
            if (_vehicleIcons.TryGetValue(vehicle.Id, out (MapIcon Bus, MapIcon Backdrop, MapIcon Arrow) icons))
            {
                icons.Bus.Location = location;
                icons.Backdrop.Location = location;
                icons.Arrow.Location = location;
            }
            else
            {
                FontIcon arrowGlyph = MapHelpers.CreateGlyph("\uEDDB", Microsoft.UI.Colors.White, 17); // CaretUpSolid8
                arrowGlyph.Width = 32;
                arrowGlyph.Height = 32;
                arrowGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
                icons = (
                    // The emoji draws right of center in its box; anchor right of center to recenter it on the disc.
                    new MapIcon(_busGlyph, location) { NormalizedAnchorPoint = new Point(0.53, 0.53) },
                    new MapIcon(_busBackdropGlyph, location) { NormalizedAnchorPoint = new Point(0.5, 0.5) },
                    new MapIcon(arrowGlyph, location) { IsVisible = false });
                _vehicleIcons[vehicle.Id] = icons;
                _vehicleLayer.MapElements.Add(icons.Bus);
                _vehicleBackdropLayer.MapElements.Add(icons.Backdrop);
                _vehicleBackdropLayer.MapElements.Add(icons.Arrow);
            }
        }

        foreach (string gone in _vehicleIcons.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _vehicleLayer.MapElements.Remove(_vehicleIcons[gone].Bus);
            _vehicleBackdropLayer.MapElements.Remove(_vehicleIcons[gone].Backdrop);
            _vehicleBackdropLayer.MapElements.Remove(_vehicleIcons[gone].Arrow);
            _vehicleIcons.Remove(gone);
            _vehicleArrowStates.Remove(gone);
            _vehicles.Remove(gone);
            if (_selectedVehicleId == gone)
            {
                _selectedVehicleId = null;
            }
        }

        SelectVehicle(_selectedVehicleId);
    }

    private void SelectVehicle(string? vehicleId)
    {
        _selectedVehicleId = vehicleId;
        foreach ((string id, (MapIcon bus, MapIcon backdrop, MapIcon arrow)) in _vehicleIcons)
        {
            bool selected = id == vehicleId;
            backdrop.IconElement = selected ? _selectedBusBackdropGlyph : _busBackdropGlyph;
            backdrop.ZIndex = selected ? 1 : 0;
            bus.ZIndex = selected ? 1 : 0;
            arrow.ZIndex = selected ? 1 : 0;
        }

        UpdateVehicleArrows();
    }

    private void UpdateVehicleArrows()
    {
        foreach ((string id, var icons) in _vehicleIcons)
        {
            int heading = _vehicles[id].Heading;
            (int Heading, double MapHeading, bool Selected, bool DarkMap) appearance = (
                heading,
                heading is >= 0 and < 360 ? Map.Heading : 0,
                id == _selectedVehicleId,
                Map.MapStyle == MapStyle.Night);
            if (_vehicleArrowStates.TryGetValue(id, out var previous) && previous == appearance)
            {
                continue;
            }

            MapHelpers.UpdateVehicleArrow(icons.Arrow, appearance.Heading, appearance.MapHeading, appearance.Selected, appearance.DarkMap);
            _vehicleArrowStates[id] = appearance;
        }
    }

    private void ClearVehicles()
    {
        _vehicleLayer.MapElements.Clear();
        _vehicleBackdropLayer.MapElements.Clear();
        _vehicleIcons.Clear();
        _vehicleArrowStates.Clear();
        _vehicles.Clear();
        _selectedVehicleId = null;
    }

    private void VehicleLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _vehicleIcons.FirstOrDefault(p => p.Value.Bus == icon || p.Value.Backdrop == icon || p.Value.Arrow == icon).Key is { } id && _vehicles.TryGetValue(id, out Vehicle? vehicle))
        {
            args.Handled = true;
            SelectVehicle(id);
            ShowMapCaption($"Bus {vehicle.Id} · Route {vehicle.RouteId} to {vehicle.Destination}{(vehicle.IsDelayed ? " · Delayed" : string.Empty)}");
        }
    }

    private void StopLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _stopPins.TryGetValue(icon, out string? name))
        {
            args.Handled = true;
            SelectVehicle(null);
            ShowMapCaption(name);
        }
    }

    private void ShowMapCaption(string text)
    {
        ++_captionVersion; // Cancels a fade-out still in flight.
        MapCaptionText.Text = text;
        MapCaption.Visibility = Visibility.Visible;
        if (_captionShown)
        {
            Motion.Pop(MapCaption, 1.05f);
        }
        else
        {
            _captionShown = true;
            Motion.Enter(MapCaption, rise: 10);
        }

        _captionTimer.Stop();
        _captionTimer.Start();
    }

    private void HideMapCaption(bool animate)
    {
        _captionShown = false;
        int version = ++_captionVersion;
        if (!animate)
        {
            MapCaption.Visibility = Visibility.Collapsed;
            return;
        }

        Motion.Exit(MapCaption, () =>
        {
            if (version == _captionVersion)
            {
                MapCaption.Visibility = Visibility.Collapsed;
            }
        });
    }

    // ---- Row motion ----

    private void StopHeader_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StopGroupItem group } header && _shownRows.Add(group.GroupKey))
        {
            Motion.Enter(header, _entranceStep++);
        }
    }

    private void ArrivalRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArrivalItem item } row && _shownRows.Add(item.Key))
        {
            Motion.Enter(row, _entranceStep++);
        }
    }

    /// <summary>A minutes figure that changed since the last refresh pops, and pops harder when the bus is due.</summary>
    private void Eta_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArrivalItem item } eta)
        {
            return;
        }

        if (_lastMinutes.TryGetValue(item.Key, out string? previous) && previous != item.MinutesText)
        {
            Motion.Pop(eta, item.Arrival.MinutesAway(DateTimeOffset.Now) == 0 ? 1.45f : 1.2f);
        }

        _lastMinutes[item.Key] = item.MinutesText;
    }

    private void Bell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArrivalItem item } bell || !ConsumePendingPop(item.Key))
        {
            return;
        }

        if (item.IsTracked)
        {
            Motion.Ring(bell);
        }
        else
        {
            Motion.Pop(bell, 0.6f);
        }
    }

    private void Star_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StopGroupItem group } star && ConsumePendingPop(group.StarKey))
        {
            Motion.Pop(star, group.IsSaved ? 1.7f : 0.6f);
        }
    }

    /// <summary>Clicking a bell or star rebuilds its row, so the animation waits for the new one to load.</summary>
    private void RequestPop(string key) => _pendingPop = (key, DateTime.UtcNow + PendingPopWindow);

    private bool ConsumePendingPop(string key)
    {
        if (_pendingPop is not { } pending || pending.Key != key || DateTime.UtcNow > pending.Until)
        {
            return false;
        }

        _pendingPop = null;
        return true;
    }

    // ---- List interactions ----

    private void ArrivalButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArrivalItem item)
        {
            return;
        }

        SelectVehicle(item.Arrival.VehicleId);
        List<GeoPoint> points = [item.StopLocation];
        if (item.Arrival.VehicleId is { } vid && _vehicles.TryGetValue(vid, out Vehicle? vehicle))
        {
            points.Add(vehicle.Location);
        }

        _ = MapHelpers.FrameAsync(Map, points, MapAnimationKind.Default);
    }

    /// <summary>The bell: starts tracking mode for this bus (replacing any other), or stops it.</summary>
    private void TrackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArrivalItem item)
        {
            return;
        }

        if (item.IsTracked)
        {
            RequestPop(item.Key);
            _tracker.Stop();
            ShowMapCaption($"Stopped tracking route {item.RouteLabel}.");
            return;
        }

        StartTracking(item);
    }

    private void StartTracking(ArrivalItem item)
    {
        string stopName = _groups.FirstOrDefault(g => g.Arrivals.Contains(item))?.Title ?? string.Empty;
        RequestPop(item.Key);
        _tracker.Start(item.Arrival, stopName);
        int lead = SettingsService.HeadOutMinutes;
        ShowMapCaption(item.Arrival.MinutesAway(DateTimeOffset.Now) <= lead
            ? $"Tracking route {item.RouteLabel}. It's under {lead} min away, so head out now."
            : item.Arrival.IsRealTime
                ? $"Tracking route {item.RouteLabel}. You'll be notified {lead} min before it arrives."
                : $"Tracking route {item.RouteLabel}. It's scheduled, so we'll look for live data as it gets close and notify you {lead} min before it arrives.");
    }

    private static readonly int[] LeadChoices = [1, 2, 3, 5, 10, 15, 20, 30];

    /// <summary>Right-click on the bell: lists how far ahead to notify, with the current choice checked.</summary>
    private void TrackButton_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        MenuFlyout menu = new();
        int current = SettingsService.HeadOutMinutes;
        ArrivalItem? arrival = (sender as FrameworkElement)?.DataContext as ArrivalItem;
        foreach (int minutes in LeadChoices)
        {
            RadioMenuFlyoutItem item = new()
            {
                Text = minutes == 1 ? "Notify 1 minute before" : $"Notify {minutes} minutes before",
                GroupName = "HeadOutLead",
                IsChecked = minutes == current,
            };
            item.Click += (_, _) =>
            {
                SettingsService.HeadOutMinutes = minutes;
                if (arrival is { IsTracked: false })
                {
                    StartTracking(arrival);
                    return;
                }

                ShowMapCaption($"You'll be notified {minutes} min before the bus arrives.");
            };
            menu.Items.Add(item);
        }

        if (sender is FrameworkElement anchor)
        {
            menu.ShowAt(anchor);
        }
    }

    private void StopHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StopGroupItem group })
        {
            SelectVehicle(null);
            _ = MapHelpers.FrameAsync(Map, [group.Location], MapAnimationKind.Default, singlePointZoom: 16);
        }
    }

    /// <summary>Adds a listed stop (all routes) to the Favorites tab, or removes it if already there.</summary>
    private void SaveStopButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StopGroupItem { Saveable: { } stop } group)
        {
            return;
        }

        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        if (group.IsSaved)
        {
            RequestPop(group.StarKey);
            SettingsService.SetStops(saved.Where(s => s.StopId != stop.Id));
            ShowMapCaption($"Removed {stop.Name} from favorites.");
            return;
        }

        if (SavedStop.DistinctStopCount(saved) >= _monitor.Service.MaxStopsPerRequest)
        {
            ShowMapCaption($"You can have up to {_monitor.Service.MaxStopsPerRequest} favorite stops. Remove one in settings first.");
            return;
        }

        StopRouteInfo? route = stop.Routes.FirstOrDefault();
        RequestPop(group.StarKey);
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
        ShowMapCaption($"Added {stop.Name} to favorites.");
    }

    private void EmptyButton_Click(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        Motion.Spin(RefreshIcon);
        _ = RefreshVehiclesAsync(TransitTrigger.ManualRefresh, force: true);
        if (_mode == FlyoutMode.Favorites)
        {
            _ = _monitor.RefreshAsync();
        }
        else
        {
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.ManualRefresh);
        }
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        SelectVehicle(null);
        Motion.Pop(FitIcon, 1.4f);
        _ = FrameListedStopsAsync(MapAnimationKind.Default);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Motion.Spin(SettingsIcon);
        App.Current.ShowSettings();
    }

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

    /// <summary>What the list says when it has nothing to show, and what its button does.</summary>
    private sealed record Notice(string Title, string Text, string? Button = null, Action? Action = null);

    /// <summary>What a Commute or Nearby load found: where it looked, the stops to list, and what to say if there are none.</summary>
    private sealed record PlaceStops(
        GeoPoint? Origin,
        IReadOnlyList<NearbyStop> Stops,
        Notice EmptyNotice,
        CommuteLeg? Leg = null,
        IReadOnlySet<RouteDirection>? DirectRoutes = null,
        IReadOnlyDictionary<string, double>? DestinationWalks = null)
    {
        public static PlaceStops Unavailable(Notice notice, CommuteLeg? leg = null) => new(null, [], notice, leg);
    }
}
