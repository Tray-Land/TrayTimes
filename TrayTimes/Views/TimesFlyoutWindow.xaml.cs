using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
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
using Notice = TrayTimes.Commute.PlaceNotice;

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
    private const int RoutesPerStop = 4;
    private const int RoutesPerNearbyStop = 3;
    private const int TimesPerRoute = 6; // the next bus, then the ones after it in the row's scrolling strip
    private const double DefaultMapHeight = 230;
    private const double MinMapHeight = 100;
    private const double MinListHeight = 140;
    private const double SplitterKeyStep = 20;
    private const int MaxNearbyStops = 8;
    private const int MaxRoutedStops = 12; // walking routes looked up per discovery
    private const int NearbyRadiusMeters = 1000; // about a 12-minute walk
    private const double WalkingMetersPerMinute = 80; // about 3 mph
    private static readonly TimeSpan PendingPopWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);

    private readonly HWND _hwnd;
    private readonly ArrivalsMonitor _monitor;
    private readonly BusTrackingService _tracker;
    private readonly TransitBudget _budget;
    private readonly VehiclePositionsMonitor _vehiclePositions;
    private readonly PlaceArrivalsMonitor _placeMonitor;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _vehicleTimer;
    private readonly DispatcherQueueTimer _placeTimer;
    private readonly DispatcherQueueTimer _captionTimer;

    // Draw order: route lines, then stops, then the place markers, then buses on top
    // (each bus is a white disc with the bus emoji over it, or a larger yellow disc when selected).
    private const double SelectedRouteThickness = 8;
    private const double UnselectedRouteThickness = 4;

    private readonly List<(MapPolyline Line, string RouteId)> _placeRouteLines = [];
    private HashSet<RouteDirection>? _drawnNearbyRoutes;

    private readonly MapElementsLayer _routeLayer = new();
    private readonly List<(MapPolyline Line, string RouteId)> _routeLines = [];
    private readonly MapElementsLayer _commuteRouteLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly MapElementsLayer _placeLayer = new();
    private readonly MapElementsLayer _vehicleBackdropLayer = new();
    private readonly MapElementsLayer _vehicleLayer = new();
    private readonly Dictionary<string, (MapIcon Bus, MapIcon Backdrop, MapIcon Arrow)> _vehicleIcons = [];
    private readonly Dictionary<string, (int Heading, double MapHeading, bool Selected, bool DarkMap)> _vehicleArrowStates = [];
    private readonly Dictionary<MapIcon, string> _stopPins = [];
    private readonly Dictionary<string, Vehicle> _vehicles = [];
    private readonly FontIcon _stopGlyph;
    private readonly FontIcon _busGlyph;
    private readonly FontIcon _busBackdropGlyph;
    private readonly FontIcon _selectedBusBackdropGlyph;
    private readonly long _mapHeadingCallbackToken;
    private string? _selectedVehicleId;

    // The row picked to show vehicles for: positions are fetched for this route direction only.
    private RouteDirection? _selectedLine;
    private string _selectedLineLabel = string.Empty;
    private string? _selectedTripId;
    private string? _onlyTripId; // When set (tracking), only this trip's vehicle is drawn, not the whole line's.
    private GeoPoint? _frameWithVehicle; // The picked row's stop, to frame with its vehicle once positions arrive.

    private IReadOnlyList<StopGroupItem> _groups = [];
    private FlyoutMode _mode;
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _mapRevealStarted;
    private bool _savedStopsDirty = true;
    private bool _locationPermissionRequired;
    private bool _locationPermissionPending;
    private bool _isRequestingLocationPermission;
    private string? _locationPermissionError;
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
    private PlaceStops? _shownPlace;
    private PredictionSnapshot? _shownPlaceSnapshot;
    private bool _placeReframe;
    private CommuteLeg? _commuteLeg;
    private IReadOnlySet<StopLine>? _placeLines; // The line directions each listed stop shows; null shows all.
    private IReadOnlyDictionary<StopLine, TripTiming>? _placeTrips;
    private string? _destinationId; // The place picked from Going to, or null to guess.

    // Motion state: which rows have already animated in, so a refresh only animates what's new or changed.
    private readonly HashSet<string> _shownRows = [];
    private readonly Dictionary<string, string> _lastMinutes = [];
    private string? _listSignature;

    // Scroll state that outlives list rebuilds, so tracking a bus or a refresh doesn't jump the list.
    private readonly Dictionary<string, double> _stripOffsets = []; // each route row's time strip, by row key
    private bool _keepListScroll;
    private int _entranceStep;
    private (string Key, DateTime Until)? _pendingPop; // A bell or star just clicked, to animate once its row is rebuilt.
    private DateTimeOffset? _lastFetchedAt;
    private bool _captionShown;
    private int _captionVersion;

    internal TimesFlyoutWindow(ArrivalsMonitor monitor, BusTrackingService tracker, TransitBudget budget)
    {
        InitializeComponent();
        _monitor = monitor;
        _tracker = tracker;
        _budget = budget;
        _vehiclePositions = new VehiclePositionsMonitor(_monitor.Service, UpdateVehicles, log: DiagnosticLogService.Write);
        _placeMonitor = new PlaceArrivalsMonitor(_monitor.Predictions, LocatePlaceAsync, FindPlaceStopsAsync);
        _placeMonitor.Updated += OnPlaceUpdated;
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), repeating: false, HideIfFocusLeftWindow);
        _vehicleTimer = CreateTimer(VehiclePositionsMonitor.RefreshInterval, repeating: true, () => _ = RefreshVehiclesAsync(TransitTrigger.Timer));
        _placeTimer = CreateTimer(_monitor.RefreshInterval, repeating: true, OnPlaceTimerTick);
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
        Map.Tapped += Map_Tapped;

        TitleText.Text = new ResourceLoader().GetString("FlyoutTitle");
        Root.ActualThemeChanged += (_, _) =>
        {
            MapHelpers.ApplyBasemap(Map, Root, MapCover);
            MapHelpers.ApplyStopColor(_stopGlyph, Root);
            UpdateVehicleArrows();
        };
        MapHelpers.ApplyBasemap(Map, Root, MapCover);
        RestoreMapView();
        MapRow.Height = new GridLength(Math.Max(MinMapHeight, SettingsService.FlyoutMapHeight ?? DefaultMapHeight));

        _monitor.Updated += OnArrivalsUpdated;
        _tracker.Changed += OnTrackingChanged;
        _monitor.Metadata.PatternsChanged += OnPatternsChanged;
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
        _locationPermissionPending = false;
        _vehiclePositions.IsForeground = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _captionTimer.Stop();
        HideMapCaption(animate: false);
        _monitor.IsForeground = false;
        ClearPlacePredictionDemand();
        SaveMapView();
        AppWindow.Hide();
        PopupHidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window for real, releasing the map, its timers, and event subscriptions.</summary>
    public void CloseWindow()
    {
        _allowClose = true;
        HidePopup();
        _placeMonitor.Updated -= OnPlaceUpdated;
        _monitor.Updated -= OnArrivalsUpdated;
        _tracker.Changed -= OnTrackingChanged;
        _monitor.Metadata.PatternsChanged -= OnPatternsChanged;
        SettingsService.StopsChanged -= OnStopsChanged;
        SettingsService.PlacesChanged -= OnPlacesChanged;
        Map.UnregisterPropertyChangedCallback(WinUIEx.Maps.MapControl.HeadingProperty, _mapHeadingCallbackToken);
        Close();
    }

    private void OnPlaceTimerTick()
    {
        _placeTimer.Interval = _monitor.RefreshInterval; // The call budget may have moved on.
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.Timer);
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
            if (_locationPermissionPending && !_isRequestingLocationPermission)
            {
                RetryLocationAfterPermission();
            }
            return;
        }

        if (_isShowing || !_isPopupVisible || _locationPermissionPending)
        {
            return;
        }

        // Debounce: focus can bounce briefly while XAML popups (tooltips, menus) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        if (!_locationPermissionPending && !ShouldRemainVisible(PInvoke.GetForegroundWindow()))
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
        _locationPermissionPending = false;
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
        ClearPlacePredictionDemand();
        // A fresh list: every row rises in again rather than silently swapping, scrolled back to the start.
        _listSignature = null;
        _shownRows.Clear();
        _lastMinutes.Clear();
        _stripOffsets.Clear();
        _keepListScroll = false;
        ClearVehicles();
        _routeLayer.IsVisible = _mode == FlyoutMode.Favorites;
        _commuteRouteLayer.MapElements.Clear();
        ResetNearbyRoutes();
        _placeLayer.MapElements.Clear();

        if (_mode == FlyoutMode.Favorites)
        {
            _placeTimer.Stop();
            if (_savedStopsDirty)
            {
                _savedStopsDirty = false;
                DrawFavoriteRoutes();
            }

            RebuildStopPins();
            Render();
            if (reframe)
            {
                _ = FrameListedStopsAsync(MapAnimationKind.None);
            }

            return;
        }

        ConfigurePlace();
        _shownPlace = null; // Mode entry cleared the map layers, not the valid discovery.
        _placeReframe = reframe;
        _placeMonitor.IsForeground = true;
        OnPlaceUpdated(this, EventArgs.Empty);
        _placeTimer.Interval = _monitor.RefreshInterval;
        _placeTimer.Start();
        _ = LoadPlaceAsync(reframe, trigger);
    }

    private void ConfigurePlace() =>
        _placeMonitor.Configure(new PlaceContext(_mode, SettingsService.Places, _destinationId));

    private async Task<PlaceLocation> LocatePlaceAsync(bool quick)
    {
        int version = _placeMonitor.Version;
        string? error = null;
        bool permissionRequired = false;
        GeoPoint? point;
        Task<GeoPoint?>? refined = null;
        if (quick)
        {
            (point, refined) = await LocationService.TryGetQuickLocationAsync(
                LocationTimeout, message => error = message, () => permissionRequired = true);
        }
        else
        {
            point = await LocationService.TryGetDeviceLocationAsync(
                LocationTimeout, message => error = message, () => permissionRequired = true);
        }

        if (version == _placeMonitor.Version && _isPopupVisible && !_allowClose)
        {
            _locationPermissionRequired = permissionRequired;
            if (!permissionRequired)
            {
                _locationPermissionError = null;
            }
            Render();
        }

        return new PlaceLocation(point, refined, error);
    }

    private async void GrantLocationPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRequestingLocationPermission)
        {
            return;
        }

        _isRequestingLocationPermission = true;
        _locationPermissionPending = true;
        _locationPermissionError = null;
        _hideTimer.Stop();
        Render();
        try
        {
            GeolocationAccessStatus? access = await LocationService.RequestAccessAsync(
                message => _locationPermissionError = message);
            if (!_isPopupVisible || _allowClose)
            {
                return;
            }

            if (access is GeolocationAccessStatus.Denied or GeolocationAccessStatus.Unspecified)
            {
                // Previously denied access needs Windows Settings; keep the flyout until the user returns.
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location")))
                {
                    Debug.WriteLine("Windows location settings could not be opened.");
                    _locationPermissionError = new ResourceLoader().GetString("LocationSettingsUnavailable");
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Debug.WriteLine($"Opening Windows location settings failed: {ex}");
            _locationPermissionError = new ResourceLoader().GetString("LocationSettingsUnavailable");
        }
        finally
        {
            _isRequestingLocationPermission = false;
            if (_isPopupVisible && !_allowClose)
            {
                Render();
                if (ShouldRemainVisible(PInvoke.GetForegroundWindow()))
                {
                    RetryLocationAfterPermission();
                }
            }
        }
    }

    private void RetryLocationAfterPermission()
    {
        _locationPermissionPending = false;
        if (_isPopupVisible && _mode != FlyoutMode.Favorites && !_allowClose)
        {
            // Cancel any discovery started before permission changed, rather than joining that old attempt.
            EnterMode(reframe: true, TransitTrigger.Retry);
        }
    }

    /// <summary>Refreshes predictions independently of any location/discovery work.</summary>
    private async Task LoadPlaceAsync(bool reframe, TransitTrigger trigger)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(RequestConsumer, trigger);
        FlyoutMode mode = _mode;
        if (mode == FlyoutMode.Favorites || !_isPopupVisible || _allowClose)
        {
            return;
        }

        ConfigurePlace();
        int version = _placeMonitor.Version;
        PlaceContext context = new(mode, SettingsService.Places, _destinationId);
        bool Stale() => version != _placeMonitor.Version || mode != _mode || !_isPopupVisible || _allowClose
            || (mode == FlyoutMode.Commute && (context.DestinationId != _destinationId
                || !context.Places.SequenceEqual(SettingsService.Places)));
        _placeReframe |= reframe;

        try
        {
            if (!_monitor.Service.IsConfigured)
            {
                _placeNotice = new Notice("API key needed", "Add your Transit API key (TransitApiKey) to OAuth.resw and rebuild.");
                Render();
                return;
            }

            await _placeMonitor.RefreshAsync(force: trigger is TransitTrigger.ManualRefresh or TransitTrigger.Retry);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            if (!Stale())
            {
                _placeError = ex is TransitApiException api ? api.Message
                    : ConnectivityService.IsInternetAvailable ? $"Can't reach {_monitor.Service.ProviderName} right now. Retrying shortly."
                    : "No internet connection. Times will update when you're back online.";
            }
        }
        finally
        {
            if (!Stale())
            {
                _placeReframe = false;
                Render();
            }
        }
    }

    /// <summary>Publishes cached rows immediately, and recomputes walking/ETA presentation on metadata updates.</summary>
    private void OnPlaceUpdated(object? sender, EventArgs e)
    {
        if (!_isPopupVisible || _mode == FlyoutMode.Favorites || _allowClose)
        {
            return;
        }

        PlaceStops? found = _placeMonitor.Discovery;
        if (found is null)
        {
            _placeOrigin = null;
            _placeStops = [];
            _placeArrivals = [];
            _destinationEtas = new Dictionary<Arrival, DateTimeOffset>();
            _placeFetchedAt = null;
            _placeError = null;
            _placeNotice = null;
            _commuteLeg = null;
            _placeLines = null;
            _placeTrips = null;
            _shownPlace = null;
            _shownPlaceSnapshot = null;
            _stopLayer.MapElements.Clear();
            _stopPins.Clear();
            _placeLayer.MapElements.Clear();
            _commuteRouteLayer.MapElements.Clear();
            ResetNearbyRoutes();
            Render();
            return;
        }

        PredictionSnapshot? snapshot = _placeMonitor.Snapshot;
        IReadOnlyList<Arrival> arrivals = snapshot?.Arrivals ?? [];
        bool changed = !ReferenceEquals(_shownPlace, found);
        _placeOrigin = found.Origin;
        _placeStops = found.Stops;
        _placeArrivals = arrivals;
        _destinationEtas = EstimateDestinationArrivals(found, arrivals);
        _commuteLeg = found.Leg;
        _placeLines = found.Lines;
        _placeTrips = found.Trips;
        if (snapshot is not null && !ReferenceEquals(snapshot, _shownPlaceSnapshot))
        {
            _placeError = null;
        }
        _shownPlaceSnapshot = snapshot;
        _placeFetchedAt = found.Origin is null ? null : snapshot?.FetchedAt;
        _placeNotice = found.Stops.Count == 0 ? found.EmptyNotice : null;

        if (changed)
        {
            _shownPlace = found;
            RebuildStopPins();
            ShowPlaceMarkers(found);
            DrawCommuteRoutes();
        }

        if (_placeReframe && changed)
        {
            _ = FrameListedStopsAsync(MapAnimationKind.None);
        }

        Render();
        DrawNearbyRoutes();
    }

    private void ClearPlacePredictionDemand()
    {
        _placeMonitor.IsForeground = false;
    }

    private Task<PlaceStops> FindPlaceStopsAsync(PlaceContext context, PlaceLocation location, CancellationToken cancellationToken) =>
        context.Mode == FlyoutMode.Commute
            ? FindCommuteStopsAsync(context, location.Point, cancellationToken)
            : FindNearbyStopsAsync(location.Point, location.Error, cancellationToken);

    private async Task<PlaceStops> FindNearbyStopsAsync(GeoPoint? here, string? locationError, CancellationToken cancellationToken)
    {
        if (here is not { } point)
        {
            return PlaceStops.Unavailable(new Notice("Can't find your location", locationError ?? "Try again in a moment.", "Try again", () => EnterMode(reframe: true, TransitTrigger.Retry)));
        }

        // One call finds the lines around here and each one's closest stop (reused for nearby points);
        // the walking route then decides which of those stops is quickest to reach.
        NearbyLines nearby = await _monitor.Metadata.GetNearbyLinesAsync(point, NearbyRadiusMeters, cancellationToken);
        PlaceBoardResult board = PlaceBoard.Nearby(nearby, point, MaxRoutedStops);
        IReadOnlyList<NearbyStop> stops = [.. (await WalkingRouteService.RankByWalkAsync(point, board.Stops, cancellationToken)).Take(MaxNearbyStops)];
        return new PlaceStops(point, stops, new Notice("No stops nearby", $"Transit has no stops within {FormatDistance(NearbyRadiusMeters)} of you."),
            Lines: board.Lines);
    }

    /// <summary>
    /// Works out where the rider is likely headed next, then asks the trip planner how to get there
    /// (transfers included) and lists where to board.
    /// </summary>
    /// <param name="here">The device's location, or null (it's off, or the PC can't tell), in which case the planner guesses from the time of day.</param>
    private async Task<PlaceStops> FindCommuteStopsAsync(PlaceContext context, GeoPoint? here, CancellationToken cancellationToken)
    {
        IReadOnlyList<FavoritePlace> places = context.Places;
        Notice setUp = new(
            "Set your favorite places",
            "Add home, plus work or school, in settings. Commute then shows the rides to wherever you're headed next.",
            "Open settings",
            App.Current.ShowSettings);
        if (!places.Any(p => p.Kind == PlaceKind.Home))
        {
            return PlaceStops.Unavailable(setUp);
        }

        CommuteLeg? leg = CommutePlanner.Plan(places, here, DateTimeOffset.Now, places.FirstOrDefault(p => p.Id == context.DestinationId));
        if (leg is null)
        {
            return PlaceStops.Unavailable(setUp);
        }

        if (leg.IsAlreadyThere)
        {
            return PlaceStops.Unavailable(new Notice($"You're at {leg.To.Name}", "Use Going to, above, to pick somewhere else."), leg);
        }

        // One plan call per trip (reused for three hours from about the same place); the stops'
        // live departures then come from the shared departures batch like any other tab's.
        TripPlan plan = await _monitor.Metadata.PlanTripAsync(leg.Origin, leg.To.Location, cancellationToken);
        PlaceBoardResult board = PlaceBoard.Commute(plan, MaxNearbyStops);
        Notice noTrip = new(
            $"No trip to {leg.To.Name} right now",
            $"Transit couldn't find a way to {leg.To.Name} from here by transit.",
            "Show nearby stops",
            () => SelectMode(FlyoutMode.Nearby));
        return new PlaceStops(leg.Origin, board.Stops, noTrip, leg, board.Lines, board.Trips);
    }

    /// <summary>
    /// When the rider would reach their destination on each listed departure: its departure, plus
    /// how long the planner's trip on that line takes from there (ride, transfers, and walk).
    /// </summary>
    private static IReadOnlyDictionary<Arrival, DateTimeOffset> EstimateDestinationArrivals(PlaceStops found, IReadOnlyList<Arrival> arrivals)
    {
        Dictionary<Arrival, DateTimeOffset> etas = [];
        if (found.Trips is not { Count: > 0 } trips)
        {
            return etas;
        }

        foreach (Arrival arrival in arrivals.Where(a => a.Status == ArrivalStatus.Scheduled))
        {
            if (PlaceBoard.ArrivalAtDestination(arrival, trips) is { } eta)
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

    /// <summary>Draws the commute's lines, in their direction of travel, from shapes the trip plan brought.</summary>
    private void DrawCommuteRoutes()
    {
        if (_mode != FlyoutMode.Commute)
        {
            return;
        }

        _commuteRouteLayer.MapElements.Clear();
        _placeRouteLines.Clear();
        foreach (IGrouping<RouteDirection, StopLine> route in (_placeLines ?? new HashSet<StopLine>()).GroupBy(l => new RouteDirection(l.RouteId, l.DirectionId)))
        {
            Color color = RouteColor(_placeArrivals.FirstOrDefault(a => new RouteDirection(a.RouteId, a.DirectionId) == route.Key)?.RouteColor
                ?? _placeStops.SelectMany(s => s.Stop.Routes).FirstOrDefault(r => string.Equals(r.RouteId, route.Key.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor);
            foreach (RoutePattern pattern in _monitor.Metadata.GetCachedPatterns(route.Key.RouteId, route.Key.DirectionId))
            {
                MapPolyline line = CreateRouteLine(pattern, color);
                _commuteRouteLayer.MapElements.Add(line);
                _placeRouteLines.Add((line, route.Key.RouteId));
            }
        }

        UpdateRouteThickness();
    }

    /// <summary>
    /// Draws the route and direction of every departure listed in Nearby mode, from shapes the
    /// discovery call brought, redrawing only when that set changes.
    /// </summary>
    private void DrawNearbyRoutes()
    {
        HashSet<RouteDirection> wanted = [.. _placeArrivals
            .Where(a => a.Status != ArrivalStatus.Canceled)
            .Select(a => new RouteDirection(a.RouteId, a.DirectionId))];
        if (_mode != FlyoutMode.Nearby || _allowClose || (_drawnNearbyRoutes?.SetEquals(wanted) ?? false))
        {
            return;
        }

        List<(MapPolyline Line, string RouteId)> lines = [];
        foreach (RouteDirection route in wanted)
        {
            Color color = RouteColor(_placeArrivals.First(a => new RouteDirection(a.RouteId, a.DirectionId) == route).RouteColor);
            foreach (RoutePattern pattern in _monitor.Metadata.GetCachedPatterns(route.RouteId, route.DirectionId))
            {
                lines.Add((CreateRouteLine(pattern, color), route.RouteId));
            }
        }

        _commuteRouteLayer.MapElements.Clear();
        _placeRouteLines.Clear();
        _placeRouteLines.AddRange(lines);
        _commuteRouteLayer.MapElements.AddRange(lines.Select(l => l.Line));
        _drawnNearbyRoutes = wanted;
        UpdateRouteThickness();
    }

    private void ResetNearbyRoutes()
    {
        _drawnNearbyRoutes = null;
        _placeRouteLines.Clear();
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
                    [.. RouteArrivals.ByRoute(snapshot.For(stop), tracked, a => a.Status != ArrivalStatus.Canceled, RoutesPerStop, TimesPerRoute)
                        .Select(r => new ArrivalItem(
                            r.Next,
                            new GeoPoint(stop.Lat, stop.Lon),
                            RouteColor(r.Next.RouteColor ?? stop.RouteColor),
                            now,
                            r.Later,
                            tracked))],
                    emptyText))];
            if (saved.Count == 0)
            {
                empty = new Notice("No favorite stops", "Star the stops you ride from, or use the Commute and Nearby tabs.", "Add a stop", App.Current.ShowSettings);
            }
        }
        else
        {
            error = _placeError ?? (_locationPermissionRequired ? null : _placeMonitor.LocationError);
            fetchedAt = _placeFetchedAt;
            loading = _placeMonitor.IsLoading;
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
                empty = new Notice("Finding stops…", string.Empty);
            }

            if (_mode == FlyoutMode.Nearby && _locationPermissionRequired && _placeStops.Count == 0)
            {
                ResourceLoader resources = new();
                busy = false;
                empty = new Notice(resources.GetString("LocationPermissionEmptyTitle"), resources.GetString("LocationPermissionEmptyText"));
            }
        }

        LocationPermissionBar.IsOpen = _mode != FlyoutMode.Favorites && _locationPermissionRequired;
        LocationPermissionBar.Visibility = LocationPermissionBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        LocationPermissionBar.Message = _locationPermissionError ?? new ResourceLoader().GetString("LocationPermissionBar/Message");
        GrantLocationPermissionButton.IsEnabled = !_isRequestingLocationPermission;
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

        foreach (string gone in _stripOffsets.Keys.Where(k => !listed.Contains(k)).ToList())
        {
            _stripOffsets.Remove(gone);
        }

        // New items empty the list for a moment, which scrolls it to the top; put it back where the rider left it.
        double offset = _keepListScroll ? ListScroller.VerticalOffset : 0;
        _keepListScroll = true;
        _entranceStep = 0;
        StopsList.ItemsSource = groups;
        ListScroller.UpdateLayout();
        ListScroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>A rebuilt row's time strip picks up where the old one was scrolled to.</summary>
    private void TimeStrip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer { DataContext: ArrivalItem item } strip && _stripOffsets.TryGetValue(item.Key, out double offset))
        {
            strip.ChangeView(offset, null, null, disableAnimation: true);
        }
    }

    private void TimeStrip_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer { DataContext: ArrivalItem item } strip && !e.IsIntermediate)
        {
            _stripOffsets[item.Key] = strip.HorizontalOffset;
        }
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

        // Each line is listed at one stop only (its closest, or where the trip boards), so it isn't repeated.
        bool Listed(string routeId, string directionId) => _placeLines is null || _placeLines.Contains(new StopLine(stop.Id, routeId, directionId));

        int walkMinutes = Math.Max(1, nearby.WalkMinutes(WalkingMetersPerMinute));
        string[] lines = [.. stop.Routes.Where(r => Listed(r.RouteId, r.DirectionId)).Select(r => r.RouteDisplayId).Distinct(StringComparer.OrdinalIgnoreCase)];
        string lineText = lines.Length == 0 ? string.Empty
            : lines.Length <= 5 ? $" · {string.Join(", ", lines)}"
            : $" · {string.Join(", ", lines.Take(5))} +{lines.Length - 5}";
        string subtitle = $"{FormatDistance(nearby.DistanceMeters)} · {walkMinutes} min walk{lineText}";

        // Stops on opposite sides of a street share a name, so say which way the lines go.
        string[] directions = [.. stop.Routes
            .Where(r => Listed(r.RouteId, r.DirectionId))
            .Select(r => r.DirectionName.Trim())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)];
        string name = stop.Name.Length > 0 ? stop.Name : new ResourceLoader().GetString("UnnamedStop");
        string title = directions.Length == 0 ? name : $"{name} · {string.Join("/", directions)}";

        // Only a list from the rider's real location can flag departures they can't walk to in time.
        bool checkWalk = _mode == FlyoutMode.Nearby || (commute && _commuteLeg is { IsGuess: false });
        bool TooSoon(Arrival a) => checkWalk && a.Status != ArrivalStatus.Canceled && a.MinutesAway(now) < walkMinutes;
        List<ArrivalItem> arrivals = [.. RouteArrivals.ByRoute(
                _placeArrivals.Where(a => a.StopId == stop.Id && Listed(a.RouteId, a.DirectionId)),
                tracked,
                a => a.Status != ArrivalStatus.Canceled && !TooSoon(a),
                RoutesPerNearbyStop,
                TimesPerRoute)
            .Select(row =>
            {
                Arrival a = row.Next;
                bool tooSoon = TooSoon(a);
                return new ArrivalItem(
                    a,
                    stop.Location,
                    RouteColor(a.RouteColor ?? stop.Routes.FirstOrDefault(r => string.Equals(r.RouteId, a.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor),
                    now,
                    row.Later,
                    tracked,
                    tooSoonToWalk: tooSoon,
                    destinationName: commute ? _commuteLeg?.To.Name : null,
                    destinationEta: !tooSoon && _destinationEtas.TryGetValue(a, out DateTimeOffset eta) ? eta : null,
                    transfers: _placeTrips is not null && _placeTrips.TryGetValue(StopLine.Of(a), out TripTiming? timing) ? timing.Transfers : 0);
            })];

        return new StopGroupItem(stop.Id, title, subtitle, stop.Location, arrivals, emptyText, stop, isSaved);
    }

    /// <summary>Feet and miles where the region measures that way, else meters and kilometers.</summary>
    private static string FormatDistance(double meters)
    {
        if (RegionInfo.CurrentRegion.IsMetric)
        {
            return meters < 1000
                ? $"{Math.Max(50, Math.Round(meters / 50) * 50).ToString("N0", CultureInfo.CurrentCulture)} m"
                : $"{(meters / 1000).ToString("0.0", CultureInfo.CurrentCulture)} km";
        }

        double miles = meters / 1609.344;
        return miles < 0.1
            ? $"{Math.Max(50, Math.Round(meters * 3.28084 / 50) * 50).ToString("N0", CultureInfo.CurrentCulture)} ft"
            : $"{miles.ToString("0.0", CultureInfo.CurrentCulture)} mi";
    }

    private static Color RouteColor(string? color) => MapHelpers.ParseRouteColor(color);

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

    /// <summary>Route shapes arrived with some response: draw the lines that were missing them.</summary>
    private void OnPatternsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_allowClose)
        {
            return;
        }

        _savedStopsDirty = true;
        if (!_isPopupVisible)
        {
            return;
        }

        switch (_mode)
        {
            case FlyoutMode.Favorites:
                _savedStopsDirty = false;
                DrawFavoriteRoutes();
                break;
            case FlyoutMode.Commute:
                DrawCommuteRoutes();
                break;
            default:
                _drawnNearbyRoutes = null;
                DrawNearbyRoutes();
                break;
        }
    });

    private void OnArrivalsUpdated(object? sender, EventArgs e)
    {
        if (_mode == FlyoutMode.Favorites)
        {
            Render();
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

    /// <summary>
    /// Draws each saved stop's route (in its direction) in the route's color. Shapes come only from
    /// responses the app needed anyway (the picker's route details, discovery, or departures fetched
    /// with shapes for a stop not seen lately), so a missing line appears once its shape arrives.
    /// </summary>
    private void DrawFavoriteRoutes()
    {
        if (_allowClose)
        {
            return;
        }

        List<(MapPolyline Line, string RouteId)> lines = [];
        foreach (IGrouping<string, SavedStop> byRoute in SettingsService.Stops.Where(s => s.RouteId.Length > 0).GroupBy(s => s.RouteId, StringComparer.OrdinalIgnoreCase))
        {
            Color color = MapHelpers.ParseRouteColor(byRoute.First().RouteColor);
            foreach (RoutePattern pattern in _monitor.Metadata.GetCachedPatterns(byRoute.Key)
                .Where(p => byRoute.Any(s => s.AllRoutes || string.Equals(s.DirectionId, p.DirectionId, StringComparison.OrdinalIgnoreCase))))
            {
                lines.Add((CreateRouteLine(pattern, color), byRoute.Key));
            }
        }

        _routeLayer.MapElements.Clear();
        _routeLines.Clear();
        _routeLines.AddRange(lines);
        _routeLayer.MapElements.AddRange(lines.Select(l => l.Line));
        UpdateRouteThickness();
    }

    private static MapPolyline CreateRouteLine(RoutePattern pattern, Color color) => new()
    {
        Path = new Geopath(pattern.Points.Select(p => new BasicGeoposition { Latitude = p.Lat, Longitude = p.Lon })),
        StrokeColor = Color.FromArgb(200, color.R, color.G, color.B),
        StrokeThickness = SelectedRouteThickness,
    };

    /// <summary>Keeps the selected row's route thick and thins the rest; with no selection every route is thick.</summary>
    private void UpdateRouteThickness()
    {
        string? selectedRouteId = _selectedLine?.RouteId;
        foreach ((MapPolyline line, string routeId) in _routeLines.Concat(_placeRouteLines))
        {
            line.StrokeThickness = selectedRouteId is null || string.Equals(routeId, selectedRouteId, StringComparison.OrdinalIgnoreCase)
                ? SelectedRouteThickness
                : UnselectedRouteThickness;
        }
    }

    /// <summary>
    /// Moves the vehicle icons for the selected row's route direction. Nothing is fetched until a
    /// row is picked, and the timer keeps them moving only while the call budget is at Normal.
    /// </summary>
    private async Task RefreshVehiclesAsync(TransitTrigger trigger, bool force = false)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(RequestConsumer, trigger);
        if (!_monitor.Service.IsConfigured || !_isPopupVisible || _selectedLine is not { } line
            || (trigger == TransitTrigger.Timer && _budget.Level != TransitBudgetLevel.Normal))
        {
            return;
        }

        try
        {
            await _vehiclePositions.RefreshAsync(line.RouteId, line.DirectionId, force);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            System.Diagnostics.Debug.WriteLine($"Vehicle positions refresh failed: {ex.GetType().Name}. Keeping last-good map positions.");
        }
    }

    private void UpdateVehicles(IReadOnlyList<Vehicle> vehicles)
    {
        if (_selectedLine is null)
        {
            return; // The row was deselected while positions loaded.
        }

        if (_onlyTripId is not null)
        {
            vehicles = [.. vehicles.Where(v => v.TripId == _onlyTripId)];
        }

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

        // Highlight the picked trip's own vehicle, and the first time it shows up, frame it with its stop.
        Vehicle? trip = _selectedTripId is null ? null : vehicles.FirstOrDefault(v => v.TripId == _selectedTripId);
        SelectVehicle(_selectedVehicleId ?? trip?.Id);
        if (_frameWithVehicle is { } stop)
        {
            _frameWithVehicle = null;
            if (trip is not null)
            {
                _ = MapHelpers.FrameAsync(Map, [stop, trip.Location], MapAnimationKind.Default);
            }
        }
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

        UpdateRouteThickness();
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

    /// <summary>Removes every vehicle and forgets the selected row, so no more positions are fetched.</summary>
    private void ClearVehicles()
    {
        _vehicleLayer.MapElements.Clear();
        _vehicleBackdropLayer.MapElements.Clear();
        _vehicleIcons.Clear();
        _vehicleArrowStates.Clear();
        _vehicles.Clear();
        _selectedVehicleId = null;
        _selectedLine = null;
        _selectedTripId = null;
        _onlyTripId = null;
        _frameWithVehicle = null;
    }

    /// <summary>
    /// Shows a row's route direction's vehicles, picking out its own trip's. With <paramref name="onlyThisTrip"/>
    /// (tracking), the rest of the line's vehicles are left off the map.
    /// </summary>
    private void ShowLine(Arrival arrival, GeoPoint stop, bool onlyThisTrip = false)
    {
        RouteDirection line = new(arrival.RouteId, arrival.DirectionId);
        if (_selectedLine != line)
        {
            ClearVehicles();
            _selectedLine = line;
        }

        _selectedLineLabel = LineLabel(arrival);
        _selectedTripId = arrival.TripId;
        _onlyTripId = onlyThisTrip ? arrival.TripId : null;
        Vehicle? known = arrival.TripId is null ? null : _vehicles.Values.FirstOrDefault(v => v.TripId == arrival.TripId);
        SelectVehicle(known?.Id);
        _frameWithVehicle = known is null ? stop : null;
        _ = MapHelpers.FrameAsync(Map, known is null ? [stop] : [stop, known.Location], MapAnimationKind.Default);
        _ = RefreshVehiclesAsync(TransitTrigger.VehicleSelected);
    }

    /// <summary>"Bus 14", or "Route 14" when the feed doesn't name the mode.</summary>
    private static string LineLabel(Arrival arrival) =>
        arrival.Mode is { Length: > 0 } mode ? $"{mode} {arrival.RouteDisplayId}" : $"Route {arrival.RouteDisplayId}";

    private void VehicleLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _vehicleIcons.FirstOrDefault(p => p.Value.Bus == icon || p.Value.Backdrop == icon || p.Value.Arrow == icon).Key is { } id && _vehicles.TryGetValue(id, out Vehicle? vehicle))
        {
            args.Handled = true;
            SelectVehicle(id);
            string occupancy = vehicle.Occupancy switch
            {
                VehicleOccupancy.NotCrowded => " · Not crowded",
                VehicleOccupancy.SomeCrowding => " · Some crowding",
                VehicleOccupancy.Crowded => " · Crowded",
                _ => string.Empty,
            };
            string label = vehicle.Label is { } name ? $" · Vehicle {name}" : string.Empty;
            ShowMapCaption($"{_selectedLineLabel}{label}{occupancy}");
        }
    }

    private void StopLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _stopPins.TryGetValue(icon, out string? name))
        {
            args.Handled = true;
            DeselectVehicle();
            ShowMapCaption(name);
        }
    }

    /// <summary>A tap on empty map deselects the bus. Taps on a bus or stop are marked handled by their layer and never get here.</summary>
    private void Map_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs args)
    {
        if (!args.Handled && _selectedVehicleId is not null)
        {
            DeselectVehicle();
            HideMapCaption(animate: true);
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
        if (sender is not FrameworkElement { DataContext: BusTimeItem item } bell || !ConsumePendingPop(item.Key))
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

    // ---- Map/list splitter ----

    private void Splitter_DragDelta(object sender, DragDeltaEventArgs e) => ResizeMap(MapRow.Height.Value + e.VerticalChange);

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => SettingsService.FlyoutMapHeight = MapRow.Height.Value;

    private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResizeMap(DefaultMapHeight);
        SettingsService.FlyoutMapHeight = null;
    }

    private void Splitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        double step = e.Key switch
        {
            Windows.System.VirtualKey.Up => -SplitterKeyStep,
            Windows.System.VirtualKey.Down => SplitterKeyStep,
            _ => 0,
        };
        if (step != 0)
        {
            e.Handled = true;
            ResizeMap(MapRow.Height.Value + step);
            SettingsService.FlyoutMapHeight = MapRow.Height.Value;
        }
    }

    /// <summary>Gives the map <paramref name="height"/>, short of squeezing it or the list below their minimums.</summary>
    private void ResizeMap(double height)
    {
        double max = Math.Max(MinMapHeight, MapRow.ActualHeight + ArrivalsPane.ActualHeight - MinListHeight);
        MapRow.Height = new GridLength(Math.Clamp(height, MinMapHeight, max));
    }

    // ---- List interactions ----

    private void ArrivalButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArrivalItem item)
        {
            return;
        }

        ShowLine(item.Arrival, item.StopLocation);
    }

    private void DeselectVehicle()
    {
        _selectedTripId = null;
        SelectVehicle(null);
    }

    /// <summary>A time chip: starts tracking mode for that departure (replacing any other), or stops it.</summary>
    private void TrackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BusTimeItem item)
        {
            return;
        }

        if (item.IsTracked)
        {
            RequestPop(item.Key);
            _tracker.Stop();
            ShowMapCaption($"Stopped tracking the {item.ClockText} {LineLabel(item.Arrival)}.");
            return;
        }

        StartTracking(item);
    }

    private void StartTracking(BusTimeItem item)
    {
        string stopName = _groups.FirstOrDefault(g => g.Arrivals.Any(a => a.Times.Contains(item)))?.Title ?? string.Empty;
        RequestPop(item.Key);
        _tracker.Start(item.Arrival, stopName);
        ShowLine(item.Arrival, item.StopLocation, onlyThisTrip: true); // Show just its bus and zoom to it.
        int lead = SettingsService.HeadOutMinutes;
        string ride = $"the {item.ClockText} {LineLabel(item.Arrival)}";
        ShowMapCaption(item.Arrival.MinutesAway(DateTimeOffset.Now) <= lead
            ? $"Tracking {ride}. It's under {lead} min away, so head out now."
            : item.Arrival.IsRealTime
                ? $"Tracking {ride}. You'll be notified {lead} min before it leaves."
                : $"Tracking {ride}. It's scheduled, so we'll look for live data as it gets close and notify you {lead} min before it leaves.");
    }

    private static readonly int[] LeadChoices = [1, 2, 3, 5, 10, 15, 20, 30];

    /// <summary>Right-click on a time chip: lists how far ahead to notify, with the current choice checked.</summary>
    private void TrackButton_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        MenuFlyout menu = new();
        int current = SettingsService.HeadOutMinutes;
        BusTimeItem? arrival = (sender as FrameworkElement)?.DataContext as BusTimeItem;
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

                ShowMapCaption($"You'll be notified {minutes} min before your ride leaves.");
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
            DeselectVehicle();
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

        if (SavedStop.DistinctStopCount(saved) >= SettingsService.MaxFavoriteStops)
        {
            ShowMapCaption($"You can have up to {SettingsService.MaxFavoriteStops} favorite stops. Remove one in settings first.");
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
        DeselectVehicle();
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

}
