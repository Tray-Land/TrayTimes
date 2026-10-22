using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TrayTimes.Commute;
using TrayTimes.Controls;
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
/// Borderless arrivals popup shown next to the tray icon. It shows one departures board, centered
/// near the device (or a saved place or stop the rider jumps to), with saved stops pinned first,
/// and a card above it suggesting the trip to wherever the rider is likely headed next. During a
/// trip (<see cref="ActiveTripService"/>), every open shows the trip's progress instead, and the
/// card is the trip. It light-dismisses when focus leaves. Hiding keeps it warm for a quick
/// reopen; the app closes it for good (<see cref="CloseWindow"/>) a minute later.
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
    private const double WalkingMetersPerMinute = 80; // about 3 mph
    private const double ShortWalkZoom = 17; // a walk too short to frame by its bounds: about a block's width fits
    private const double VehicleZoom = 16; // a bus and the streets around it
    private static readonly TimeSpan PendingPopWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(15); // keeps minutes counting down between refreshes

    private readonly HWND _hwnd;
    private readonly ArrivalsMonitor _monitor;
    private readonly BusTrackingService _tracker;
    private readonly ActiveTripService _trips;
    private readonly TransitBudget _budget;
    private readonly VehiclePositionsMonitor _vehiclePositions;
    private readonly SharedLocation _location;
    private readonly PlaceArrivalsMonitor<BoardCenter> _boardMonitor; // Nearby
    private readonly PlaceArrivalsMonitor<SuggestionContext> _suggestionMonitor; // Commute
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _vehicleTimer;
    private readonly DispatcherQueueTimer _placeTimer;
    private readonly DispatcherQueueTimer _captionTimer;
    private readonly DispatcherQueueTimer _clockTimer;

    // Draw order: route lines, then stops, then the place markers, then buses on top
    // (each bus is a white disc with the bus emoji over it, or a larger yellow disc when selected).
    private const double SelectedRouteThickness = 8;
    private const double UnselectedRouteThickness = 4;

    private readonly List<(MapPolyline Line, string RouteId)> _routeLines = [];
    private HashSet<RouteDirection>? _drawnRoutes;

    private readonly MapElementsLayer _routeLayer = new();
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
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _mapRevealStarted;
    private bool _frameWhenMapSized;
    private bool _locationPermissionRequired;
    private bool _locationPermissionPending;
    private bool _isRequestingLocationPermission;
    private string? _locationPermissionError;
    private int _locationLookups; // so a lookup that a newer one replaced doesn't change the permission state
    private DateTime _lastDismissedAtUtc;

    private Action? _emptyAction;

    // Board state.
    private BoardCenter _center = new NearYou(); // The center the rider picked; it can fall back (see BoardCenter.Resolve).
    private int _savedRevision; // Bumped when saved stops or places change, so the board is found again.
    private GeoPoint? _placeOrigin;
    private IReadOnlyList<NearbyStop> _placeStops = [];
    private IReadOnlyList<Arrival> _placeArrivals = [];
    private DateTimeOffset? _placeFetchedAt;
    private string? _placeError;
    private Notice? _placeNotice;
    private PlaceStops? _shownPlace;
    private PredictionSnapshot? _shownPlaceSnapshot;
    private bool _placeReframe;

    // Suggestion state.
    private string? _destinationId; // The place picked from Going to, or null to guess.
    private FavoritePlace? _temporaryDestination;
    private DestinationSearchView? _destinationSearch;
    private SuggestionContext SuggestionContext => new(SettingsService.Places, _destinationId, _temporaryDestination);
    private bool IsSearchOpen => SearchHost.Visibility == Visibility.Visible;
    private string? _suggestionError;
    private SuggestionCardItem? _suggestion;
    private bool _isReplanning;
    private readonly TripSelection _tripSelection = new();
    private readonly PlaceArrivalsMonitor<TimedTripContext> _timedTripMonitor;
    private TimedTripContext? _timedTripContext;
    private TripTime _tripTime;
    private string? _timedTripError;
    private readonly TripMapRenderer _tripMap;
    private TripView? _tripView;
    private CommuteLeg? _tripLeg;
    private bool IsTripOpen => _tripLeg is not null;
    private TripProgressView? _progressView;
    private bool IsProgressOpen => ProgressHost.Visibility == Visibility.Visible;
    private bool _progressMapInitialized;
    private readonly TripStepZoom _stepZoom = new();
    private bool IsTripShown => IsTripOpen || IsProgressOpen; // A trip, not the board, owns the map.

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

    internal TimesFlyoutWindow(ArrivalsMonitor monitor, BusTrackingService tracker, ActiveTripService trips, TransitBudget budget)
    {
        InitializeComponent();
        _monitor = monitor;
        _tracker = tracker;
        _trips = trips;
        _budget = budget;
        _vehiclePositions = new VehiclePositionsMonitor(_monitor.Service, UpdateVehicles, log: DiagnosticLogService.Write);
        _location = new SharedLocation(LocatePlaceAsync);
        _boardMonitor = new PlaceArrivalsMonitor<BoardCenter>(_monitor.Predictions, TransitConsumer.Nearby, _location.LocateAsync,
            FindBoardStopsAsync,
            isOutdated: found => found.Revision != _savedRevision);
        _suggestionMonitor = new PlaceArrivalsMonitor<SuggestionContext>(_monitor.Predictions, TransitConsumer.Commute, _location.LocateAsync,
            FindSuggestionAsync,
            holds: (context, found, here) => found.Leg == context.Leg(here, DateTimeOffset.Now),
            isOutdated: found => found is { Leg: { } leg, Plan: { } plan } && !_monitor.Metadata.IsCurrentPlan(leg.Origin, SuggestionContext.Target(leg.To), plan));
        _boardMonitor.Updated += OnBoardUpdated;
        _suggestionMonitor.Updated += OnSuggestionUpdated;
        _timedTripMonitor = new PlaceArrivalsMonitor<TimedTripContext>(_monitor.Predictions, TransitConsumer.Trip,
            _ => Task.FromResult(new PlaceLocation(null)), FindTimedTripAsync, isOutdated: IsTimedTripOutdated);
        _timedTripMonitor.Updated += OnSuggestionUpdated;
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), repeating: false, HideIfFocusLeftWindow);
        _vehicleTimer = CreateTimer(VehiclePositionsMonitor.RefreshInterval, repeating: true, () => _ = RefreshVehiclesAsync(TransitTrigger.Timer));
        _placeTimer = CreateTimer(_monitor.RefreshInterval, repeating: true, OnPlaceTimerTick);
        _captionTimer = CreateTimer(TimeSpan.FromSeconds(5), repeating: false, () => HideMapCaption(animate: true));
        _clockTimer = CreateTimer(ClockInterval, repeating: true, Render);

        _stopGlyph = new FontIcon { Glyph = MapHelpers.StopGlyph, FontSize = 14 };
        _tripMap = new TripMapRenderer(_routeLayer, _stopLayer, _placeLayer, _stopGlyph, _stopPins);
        // Shared so every bus reuses one emoji raster.
        _busGlyph = MapHelpers.CreateEmoji(MapHelpers.BusEmoji, 19.5);
        _busBackdropGlyph = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, Microsoft.UI.Colors.White, 28);
        _selectedBusBackdropGlyph = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, Microsoft.UI.Colors.Yellow, 38);
        _mapHeadingCallbackToken = Map.RegisterPropertyChangedCallback(WinUIEx.Maps.MapControl.HeadingProperty, (_, _) => UpdateVehicleArrows());
        MapHelpers.ApplyStopColor(_stopGlyph, Root);
        Map.Layers.Add(_routeLayer);
        Map.Layers.Add(_stopLayer);
        Map.Layers.Add(_placeLayer);
        Map.Layers.Add(_vehicleBackdropLayer);
        Map.Layers.Add(_vehicleLayer);
        _vehicleLayer.Tapped += VehicleLayer_Tapped;
        _vehicleBackdropLayer.Tapped += VehicleLayer_Tapped; // The disc's rim is part of the bus too.
        _stopLayer.Tapped += StopLayer_Tapped;
        Map.Tapped += Map_Tapped;

        TitleText.Text = AppStrings.Get("FlyoutTitle");
        SuggestionCard.TripInvoked += SuggestionCard_TripInvoked;
        SuggestionCard.DestinationChosen += SuggestionCard_DestinationChosen;
        SuggestionCard.SearchRequested += SuggestionCard_SearchRequested;
        SuggestionCard.SaveDestinationRequested += SuggestionCard_SaveDestinationRequested;
        Root.ActualThemeChanged += (_, _) =>
        {
            MapHelpers.ApplyBasemap(Map, Root, MapCover);
            MapHelpers.ApplyStopColor(_stopGlyph, Root);
            UpdateVehicleArrows();
            if (IsTripShown)
            {
                _tripMap.Invalidate();
                RenderTrip(DateTimeOffset.Now);
                RenderProgress(DateTimeOffset.Now);
            }
        };
        MapHelpers.ApplyBasemap(Map, Root, MapCover);
        RestoreMapView();
        MapRow.Height = new GridLength(Math.Max(MinMapHeight, SettingsService.FlyoutMapHeight ?? DefaultMapHeight));

        _tracker.Changed += OnTrackingChanged;
        _monitor.Metadata.PatternsChanged += OnPatternsChanged;
        SettingsService.StopsChanged += OnStopsChanged;
        SettingsService.PlacesChanged += OnPlacesChanged;

        Render();
    }

    /// <summary>Raised when the popup is dismissed (the window stays alive until closed).</summary>
    public event EventHandler? PopupHidden;

    /// <summary>Raised when <see cref="TripSummary"/> or <see cref="SavedStopIds"/> changes.</summary>
    public event EventHandler? SummaryChanged;

    public bool IsPopupVisible => _isPopupVisible;

    /// <summary>The suggested trip in one line for the tray tooltip, e.g. "Work: leave in 6 min, 14 at 8:12"; null when there's none.</summary>
    public string? TripSummary { get; private set; }

    /// <summary>The saved stops on the board, nearest its center first.</summary>
    public IReadOnlyList<string> SavedStopIds { get; private set; } = [];

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
        Motion.Enter(PlaceChip, step: 1, rise: 10);
        StartShowing();
    }

    /// <summary>
    /// What every open shows, however the window appeared: the board near you, guessing the next trip afresh,
    /// and during a trip, its progress (or its Resume card) over the board. Back from it shows the board.
    /// </summary>
    private void StartShowing()
    {
        _monitor.IsForeground = true; // The saved stops' times, for the jump menu and the tooltip.
        _center = new NearYou(); // Each open starts near you...
        _destinationId = null; // ...and guesses the next trip afresh.
        _temporaryDestination = null;
        CloseSearch(focusDestination: false);
        CloseTrip(focusCard: false);
        if (_trips.Current is null)
        {
            CloseProgress(focusCard: false);
        }

        // Only while shown: the service outlives this window, which a subscription would keep alive.
        _trips.Changed -= OnTripsChanged;
        _trips.Changed += OnTripsChanged;
        if (_trips.Current is not null)
        {
            OpenProgress();
        }

        // Load the board beneath progress without replacing the trip's camera or map layers.
        EnterBoard(reframe: SettingsService.FlyoutMapView is null, TransitTrigger.FlyoutOpened);
        _ = RefreshVehiclesAsync(TransitTrigger.FlyoutOpened);
        _vehicleTimer.Start();
        _clockTimer.Start();
    }

    public void HidePopup()
    {
        if (!_isPopupVisible)
        {
            return;
        }

        _isPopupVisible = false;
        _trips.Changed -= OnTripsChanged;
        _destinationSearch?.Cancel();
        _locationPermissionPending = false;
        _vehiclePositions.IsForeground = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _captionTimer.Stop();
        _clockTimer.Stop();
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
        _boardMonitor.Updated -= OnBoardUpdated;
        _suggestionMonitor.Updated -= OnSuggestionUpdated;
        _timedTripMonitor.Updated -= OnSuggestionUpdated;
        SuggestionCard.TripInvoked -= SuggestionCard_TripInvoked;
        SuggestionCard.DestinationChosen -= SuggestionCard_DestinationChosen;
        SuggestionCard.SearchRequested -= SuggestionCard_SearchRequested;
        SuggestionCard.SaveDestinationRequested -= SuggestionCard_SaveDestinationRequested;
        _tracker.Changed -= OnTrackingChanged;
        _trips.Changed -= OnTripsChanged;
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
            RestoreMapView(view);
        }
    }

    private void RestoreMapView(MapView view)
    {
        _frameWhenMapSized = false;
        Map.Center = MapHelpers.ToGeopoint(new GeoPoint(view.Lat, view.Lon));
        Map.ZoomLevel = view.Zoom;
        Map.Heading = view.Heading;
        Map.Pitch = view.Pitch;
    }

    private MapView? CurrentMapView() => Map.Center is { } center
        ? new MapView(center.Position.Latitude, center.Position.Longitude, Map.ZoomLevel, Map.Heading, Map.Pitch)
        : null;

    /// <summary>
    /// Keeps the map's view for the next open, which starts near you: a view of somewhere else is
    /// forgotten, so that open frames the board near you instead.
    /// </summary>
    private void SaveMapView()
    {
        if (IsProgressOpen)
        {
            SaveProgressMapView();
        }
        else if (IsTripOpen || _shownPlace?.Center is not NearYou)
        {
            SettingsService.FlyoutMapView = null;
        }
        else
        {
            SettingsService.FlyoutMapView = CurrentMapView();
        }
    }

    private void SaveProgressMapView()
    {
        if (_trips.Current is not null && _progressMapInitialized && !_frameWhenMapSized)
        {
            _trips.MapView = CurrentMapView();
        }
    }

    private void Map_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_frameWhenMapSized && Map.ActualWidth > 0 && Map.ActualHeight > 0)
        {
            _ = FrameListedStopsAsync(MapAnimationKind.None);
        }
    }

    private void MapCoverFadeOut_Completed(object sender, object e) => MapCover.Visibility = Visibility.Collapsed;

    // ---- The board and its center ----

    /// <summary>Shows the board at its center, with its route lines and arrivals, and the trip suggestion, loading as needed.</summary>
    private void EnterBoard(bool reframe, TransitTrigger trigger = TransitTrigger.CenterChanged)
    {
        _vehiclePositions.Invalidate();
        ClearPlacePredictionDemand();
        // A fresh list: every row rises in again rather than silently swapping, scrolled back to the start.
        _listSignature = null;
        _shownRows.Clear();
        _lastMinutes.Clear();
        _stripOffsets.Clear();
        _keepListScroll = false;
        if (!IsTripShown)
        {
            ClearVehicles();
            _routeLayer.MapElements.Clear();
            ResetRoutes();
            _placeLayer.MapElements.Clear();
        }

        ConfigurePlaces();
        _shownPlace = null; // Entering cleared the map layers, not the valid discovery.
        _placeReframe = reframe;
        _boardMonitor.IsForeground = true;
        _suggestionMonitor.IsForeground = true;
        OnBoardUpdated(this, EventArgs.Empty);
        _placeTimer.Interval = _monitor.RefreshInterval;
        _placeTimer.Start();
        _ = LoadPlaceAsync(reframe, trigger);
    }

    private void ConfigurePlaces()
    {
        _boardMonitor.Configure(_center);
        _suggestionMonitor.Configure(SuggestionContext);
    }

    /// <summary>Moves the board to <paramref name="center"/>, framing it on the map.</summary>
    private void JumpTo(BoardCenter center)
    {
        CloseTrip(focusCard: false);
        _center = center;
        _locationPermissionPending = false;
        EnterBoard(reframe: true);
    }

    /// <summary>The jump menu: near you, each saved place, each saved stop with its next departure, and every saved stop.</summary>
    private void PlaceMenu_Opening(object sender, object e)
    {
        PlaceMenu.Items.Clear();
        BoardCenter shown = ShownCenter;
        PlaceMenu.Items.Add(CenterItem(new NearYou(), shown));
        foreach (FavoritePlace place in SettingsService.Places)
        {
            PlaceMenu.Items.Add(CenterItem(new NearPlace(place), shown));
        }

        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        if (saved.Count > 0)
        {
            // The saved stops' departures ride in every batch while the flyout is open, so this costs no request.
            ArrivalsSnapshot favorites = _monitor.Latest;
            DateTimeOffset now = DateTimeOffset.Now;
            PlaceMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (IGrouping<string, SavedStop> stop in saved.GroupBy(s => s.StopId, StringComparer.Ordinal))
            {
                Arrival? next = stop.SelectMany(favorites.For).Where(a => a.Status != ArrivalStatus.Canceled).MinBy(a => a.PredictedTime);
                string? when = next is null ? null
                    : next.MinutesAway(now) == 0 ? AppStrings.Format("JumpNextDueFormat", next.RouteDisplayId)
                    : AppStrings.Format("JumpNextFormat", next.RouteDisplayId, next.MinutesAway(now));
                PlaceMenu.Items.Add(CenterItem(new NearStop(stop.First()), shown, when));
            }

            PlaceMenu.Items.Add(CenterItem(new AllSavedStops(), shown));
        }

        PlaceMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem manage = new() { Text = AppStrings.Get("ManageFavorites"), Icon = new FontIcon { Glyph = "" } }; // Edit
        manage.Click += (_, _) => App.Current.ShowSettings();
        PlaceMenu.Items.Add(manage);
    }

    /// <param name="next">The stop's next departure, e.g. "14 in 4 min".</param>
    private RadioMenuFlyoutItem CenterItem(BoardCenter center, BoardCenter shown, string? next = null)
    {
        RadioMenuFlyoutItem item = new()
        {
            Text = next is null ? CenterLabel(center) : AppStrings.Format("JumpStopFormat", CenterLabel(center), next),
            GroupName = "BoardCenter",
            IsChecked = center == shown,
            Icon = new FontIcon { Glyph = CenterGlyph(center) },
        };
        item.Click += (_, _) => JumpTo(center);
        return item;
    }

    /// <summary>Where the board is centered: where its discovery resolved to, else where the rider picked.</summary>
    private BoardCenter ShownCenter => _boardMonitor.Discovery?.Center ?? _center;

    private void RenderPlaceChip()
    {
        BoardCenter center = ShownCenter;
        string label = CenterLabel(center);
        PlaceChipGlyph.Glyph = CenterGlyph(center);
        PlaceChipText.Text = label;
        AutomationProperties.SetName(PlaceChip, AppStrings.Format("PlaceChipNameFormat", label));
    }

    private static string CenterLabel(BoardCenter center) => center switch
    {
        NearPlace near => AppStrings.Format("CenterNearFormat", near.Place.Name),
        NearStop near => StopName(near.Stop.StopName),
        AllSavedStops => AppStrings.Get("CenterAllSaved"),
        _ => AppStrings.Get("CenterNearYou"),
    };

    private static string CenterGlyph(BoardCenter center) => center switch
    {
        NearPlace near => MapHelpers.PlaceGlyph(near.Place.Kind),
        NearStop => MapHelpers.SavedStopGlyph,
        AllSavedStops => "", // FavoriteList
        _ => MapHelpers.PlaceGlyph(null),
    };

    private static string StopName(string name) => name.Length > 0 ? name : AppStrings.Get("UnnamedStop");

    private async Task<PlaceLocation> LocatePlaceAsync(bool quick)
    {
        int lookup = ++_locationLookups;
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

        if (lookup == _locationLookups && _isPopupVisible && !_allowClose)
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
                    _locationPermissionError = AppStrings.Get("LocationSettingsUnavailable");
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Debug.WriteLine($"Opening Windows location settings failed: {ex}");
            _locationPermissionError = AppStrings.Get("LocationSettingsUnavailable");
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
        if (_isPopupVisible && !_allowClose)
        {
            // Cancel any discovery started before permission changed, rather than joining that old attempt.
            EnterBoard(reframe: true, TransitTrigger.Retry);
        }
    }

    /// <summary>
    /// Refreshes the board and the trip suggestion together, so they share one location lookup and
    /// one departures batch. Predictions refresh independently of any location/discovery work.
    /// </summary>
    private async Task LoadPlaceAsync(bool reframe, TransitTrigger trigger)
    {
        if (!_isPopupVisible || _allowClose)
        {
            return;
        }

        ConfigurePlaces();
        int boardVersion = _boardMonitor.Version;
        int suggestionVersion = _suggestionMonitor.Version;
        SuggestionContext context = SuggestionContext;
        bool Hidden() => !_isPopupVisible || _allowClose;
        _placeReframe |= reframe;
        if (!_monitor.Service.IsConfigured)
        {
            _placeNotice = new Notice(AppStrings.Get("ApiKeyNeededTitle"), AppStrings.Get("ApiKeyNeededText"));
            Render();
            return;
        }

        bool force = trigger is TransitTrigger.ManualRefresh or TransitTrigger.Retry;
        _suggestionError = null; // Planning again: the card says so rather than showing the last failure.
        _location.BeginRefresh();
        Task<string?> board = RefreshAsync(_boardMonitor, TransitConsumer.Nearby, trigger, force);
        Task<string?> suggestion = RefreshAsync(_suggestionMonitor, TransitConsumer.Commute, trigger, force);
        Task timedTrip = RefreshTimedTripAsync(trigger, force);
        string? boardError = await board;
        string? suggestionError = await suggestion;
        await timedTrip;
        if (Hidden())
        {
            return;
        }

        if (suggestionVersion == _suggestionMonitor.Version && context.Equals(SuggestionContext))
        {
            _suggestionError = suggestionError;
            if (suggestionError is null && _suggestionMonitor.Discovery is { Leg: { } leg, Plan: { } plan })
            {
                await ReplanInfeasibleSuggestionAsync(leg, plan, trigger);
            }
        }

        if (boardVersion == _boardMonitor.Version)
        {
            _placeError = boardError ?? _placeError;
            _placeReframe = false;
        }

        Render();
    }

    private async Task ReplanInfeasibleSuggestionAsync(CommuteLeg leg, TripPlan plan, TransitTrigger trigger)
    {
        if (!plan.Itineraries.Any(i => i.Legs.OfType<RideLeg>().Any())
            || LiveTrip.Best(plan, _suggestionMonitor.Snapshot?.Arrivals ?? [], DateTimeOffset.Now) is not null)
        {
            return;
        }

        if (_isReplanning)
        {
            return;
        }

        int version = _suggestionMonitor.Version;
        _isReplanning = true;
        Render();
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Commute, trigger);
        try
        {
            TripPlan replanned = await _monitor.Metadata.ReplanTripAsync(leg.Origin, SuggestionContext.Target(leg.To));
            if (!ReferenceEquals(replanned, plan) && _isPopupVisible && !_allowClose && version == _suggestionMonitor.Version)
            {
                await _suggestionMonitor.RefreshAsync(checkLocation: true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            Debug.WriteLine($"Infeasible trip replan failed: {ex.GetType().Name}. Keeping the last plan.");
            if (version == _suggestionMonitor.Version)
            {
                _suggestionError = ConnectivityService.IsInternetAvailable
                    ? AppStrings.Get("SuggestionErrorTitle") : AppStrings.Get("Offline");
            }
        }
        finally
        {
            _isReplanning = false;
            if (_isPopupVisible && !_allowClose)
            {
                Render();
            }
        }
    }

    /// <summary>Refreshes one monitor under its own consumer, so the request log says whose request it was.</summary>
    /// <returns>What went wrong, to show the rider; null when nothing did.</returns>
    private async Task<string?> RefreshAsync(IPlaceArrivalsMonitor monitor, TransitConsumer consumer, TransitTrigger trigger, bool force)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(consumer, trigger);
        try
        {
            await monitor.RefreshAsync(force);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
        {
            return ex is TransitApiException api ? api.Message
                : ConnectivityService.IsInternetAvailable ? AppStrings.Format("TransitUnreachableFormat", _monitor.Service.ProviderName)
                : AppStrings.Get("Offline");
        }
    }

    /// <summary>Publishes cached rows immediately, and recomputes walking/ETA presentation on metadata updates.</summary>
    private void OnBoardUpdated(object? sender, EventArgs e)
    {
        if (!_isPopupVisible || _allowClose)
        {
            return;
        }

        PlaceStops? found = _boardMonitor.Discovery;
        if (found is null)
        {
            _placeOrigin = null;
            _placeStops = [];
            _placeArrivals = [];
            _placeFetchedAt = null;
            _placeError = null;
            _placeNotice = null;
            _shownPlace = null;
            _shownPlaceSnapshot = null;
            if (!IsTripShown)
            {
                _stopLayer.MapElements.Clear();
                _stopPins.Clear();
                _placeLayer.MapElements.Clear();
                _routeLayer.MapElements.Clear();
                ResetRoutes();
            }
            Render();
            return;
        }

        PredictionSnapshot? snapshot = _boardMonitor.Snapshot;
        bool changed = !ReferenceEquals(_shownPlace, found);
        _placeOrigin = found.Origin;
        _placeStops = found.Stops;
        _placeArrivals = snapshot?.Arrivals ?? [];
        if (snapshot is not null && !ReferenceEquals(snapshot, _shownPlaceSnapshot))
        {
            _placeError = null;
        }
        _shownPlaceSnapshot = snapshot;
        _placeFetchedAt = found.Stops.Count == 0 ? null : snapshot?.FetchedAt;
        _placeNotice = found.Stops.Count == 0 ? found.EmptyNotice : null;

        if (changed)
        {
            _shownPlace = found;
            if (!IsTripShown)
            {
                RebuildStopPins();
                ShowCenterMarker(found);
            }
        }

        if (_placeReframe && changed && !IsTripShown)
        {
            _ = FrameListedStopsAsync(MapAnimationKind.None);
        }

        Render();
        DrawRoutes();
    }

    /// <summary>The trip suggestion changed: its card, and the board rows that start its trip.</summary>
    private void OnSuggestionUpdated(object? sender, EventArgs e)
    {
        if (_isPopupVisible && !_allowClose)
        {
            Render();
        }
    }

    private void ClearPlacePredictionDemand()
    {
        _boardMonitor.IsForeground = false;
        _suggestionMonitor.IsForeground = false;
        _timedTripMonitor.IsForeground = false;
    }

    /// <summary>
    /// The board at its center: the saved stops near it first, then the nearest other lines, each at its
    /// closest stop. Near you, the walking route decides which stops are quickest to reach. Without a
    /// location, near you falls back to near Home, then to every saved stop (see <see cref="BoardCenter.Resolve"/>).
    /// </summary>
    private async Task<PlaceStops> FindBoardStopsAsync(BoardCenter picked, PlaceLocation location, CancellationToken cancellationToken)
    {
        int revision = _savedRevision;
        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        BoardCenter center = picked.Resolve(location.Point, SettingsService.Places, saved);
        if (center is AllSavedStops)
        {
            PlaceBoardResult all = PlaceBoard.Saved(saved);
            Notice noneSaved = new(AppStrings.Get("NoSavedStopsTitle"), AppStrings.Get("NoSavedStopsText"), AppStrings.Get("AddAStop"), App.Current.ShowSettings);
            return new PlaceStops(null, all.Stops, noneSaved, Lines: all.Lines, EveryLineAt: all.EveryLineAt, Center: center, Revision: revision);
        }

        if (center.PointFrom(location.Point) is not { } point)
        {
            Notice unknown = new(AppStrings.Get("LocationUnknownTitle"), location.Error ?? AppStrings.Get("LocationUnknownText"),
                AppStrings.Get("TryAgain"), () => EnterBoard(reframe: true, TransitTrigger.Retry));
            return PlaceStops.Unavailable(unknown) with { Center = center, Revision = revision };
        }

        // One call finds the lines around the center and each one's closest stop (reused for nearby points).
        NearbyLines nearby = await _monitor.Metadata.GetNearbyLinesAsync(point, PlaceBoard.RadiusMeters, cancellationToken);
        bool nearYou = center is NearYou;
        PlaceBoardResult board = PlaceBoard.Around(nearby, saved, point, nearYou ? MaxRoutedStops : MaxNearbyStops);
        IReadOnlyList<NearbyStop> stops = nearYou
            ? PlaceBoard.SavedFirst(await WalkingRouteService.RankByWalkAsync(point, board.Stops, cancellationToken), saved, MaxNearbyStops)
            : board.Stops;
        string radius = AppStrings.Distance(PlaceBoard.RadiusMeters);
        Notice none = new(AppStrings.Get("NoStopsNearbyTitle"), nearYou
            ? AppStrings.Format("NoStopsNearYouFormat", radius)
            : AppStrings.Format("NoStopsNearFormat", radius, CenterName(center)));
        return new PlaceStops(point, stops, none, Lines: board.Lines, EveryLineAt: board.EveryLineAt, Center: center, Revision: revision);
    }

    /// <summary>What a board away from the rider is near: the saved place or stop.</summary>
    private static string CenterName(BoardCenter center) => center switch
    {
        NearPlace near => near.Place.Name,
        NearStop near => StopName(near.Stop.StopName),
        _ => string.Empty,
    };

    /// <summary>
    /// Works out where the rider is likely headed next, then asks the trip planner how to get there
    /// (transfers included). Every ride's boarding stop is watched, so the trip stays live.
    /// </summary>
    /// <param name="location">The device's location; without one (it's off, or the PC can't tell) the planner guesses from the time of day.</param>
    private async Task<PlaceStops> FindSuggestionAsync(SuggestionContext context, PlaceLocation location, CancellationToken cancellationToken)
    {
        if (context.Leg(location.Point, DateTimeOffset.Now) is not { } leg)
        {
            return PlaceStops.Unavailable(SetUpNotice());
        }

        if (leg.IsAlreadyThere)
        {
            return PlaceStops.Unavailable(new Notice(AppStrings.Format("SuggestionAtPlaceFormat", leg.To.Name), AppStrings.Get("SuggestionAtPlaceText")), leg);
        }

        // One plan call per trip (reused for three hours from about the same place); the stops'
        // live departures then come from the shared departures batch, like the board's.
        TripPlan plan = await _monitor.Metadata.PlanTripAsync(leg.Origin, context.Target(leg.To), cancellationToken);
        PlaceBoardResult board = PlaceBoard.Commute(plan, MaxNearbyStops);
        Notice noTrip = new(AppStrings.Format("SuggestionNoTripFormat", leg.To.Name), AppStrings.Get("SuggestionNoTripText"));
        return new PlaceStops(leg.Origin, board.Stops, noTrip, leg, board.Lines, board.Trips, plan);
    }

    /// <summary>What the card says without a home to plan from, or nowhere to go from it.</summary>
    private static Notice SetUpNotice() =>
        new(AppStrings.Get("SuggestionSetUpTitle"), string.Empty, AppStrings.Get("SuggestionSetUpButton"), App.Current.ShowSettings);

    /// <summary>Marks the board's center: the rider, near you, or the saved place it's around.</summary>
    private void ShowCenterMarker(PlaceStops found)
    {
        _placeLayer.MapElements.Clear();
        if (found.Origin is { } origin && found.Center is NearYou or NearPlace)
        {
            AddPlaceMarker((found.Center as NearPlace)?.Place.Kind, origin);
        }
    }

    private void AddPlaceMarker(PlaceKind? kind, GeoPoint point) =>
        _placeLayer.MapElements.Add(new MapIcon(MapHelpers.CreatePlaceIcon(kind, 26), MapHelpers.ToGeopoint(point))
        {
            NormalizedAnchorPoint = new Point(0.5, 0.5),
        });

    /// <summary>
    /// Draws the route and direction of every departure the board lists, pinned saved stops' included,
    /// from shapes the discovery or departures calls brought, redrawing only when that set changes.
    /// </summary>
    private void DrawRoutes()
    {
        Func<StopLine, bool> listed = BoardListing(SavedStopIdSet());
        HashSet<RouteDirection> wanted = [.. _placeArrivals
            .Where(a => a.Status != ArrivalStatus.Canceled && listed(StopLine.Of(a)))
            .Select(a => new RouteDirection(a.RouteId, a.DirectionId))];
        if (_allowClose || IsTripShown || (_drawnRoutes?.SetEquals(wanted) ?? false))
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

        _routeLayer.MapElements.Clear();
        _routeLines.Clear();
        _routeLines.AddRange(lines);
        _routeLayer.MapElements.AddRange(lines.Select(l => l.Line));
        _drawnRoutes = wanted;
        UpdateRouteThickness();
    }

    private void ResetRoutes()
    {
        _drawnRoutes = null;
        _routeLines.Clear();
    }

    /// <summary>Which lines the board lists at each stop, given its departures (see <see cref="PlaceStops.Listing"/>).</summary>
    private Func<StopLine, bool> BoardListing(IReadOnlySet<string> saved) => _shownPlace?.Listing(_placeArrivals, saved) ?? (_ => true);

    private static HashSet<string> SavedStopIdSet() => new(SettingsService.Stops.Select(s => s.StopId), StringComparer.Ordinal);

    // ---- Rendering ----

    private void Render()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        HashSet<string> savedIds = SavedStopIdSet();
        Notice? empty = null;
        bool busy = false;
        string? error = _placeError ?? (_locationPermissionRequired ? null : _boardMonitor.LocationError);
        DateTimeOffset? fetchedAt = _placeFetchedAt;
        bool loading = _boardMonitor.IsLoading || _suggestionMonitor.IsLoading;
        Func<StopLine, bool> listed = BoardListing(savedIds);
        BoardRow row = new(
            listed,
            NearYou: _shownPlace?.Center is NearYou,
            _suggestionMonitor.Discovery,
            fetchedAt is null ? string.Empty : AppStrings.Get("NoUpcomingArrivals"),
            _tracker.Current?.FindListed(_placeArrivals),
            now);

        // A stop whose every line is listed at a stop saved with All routes has nothing left to show.
        IReadOnlyList<StopGroupItem> groups = [.. _placeStops
            .Where(n => savedIds.Contains(n.Stop.Id) || n.Stop.Routes.Count == 0 || n.Stop.Routes.Any(r => listed(new StopLine(n.Stop.Id, r.RouteId, r.DirectionId))))
            .Select(n => BuildBoardGroup(n, savedIds.Contains(n.Stop.Id), row))];

        if (_placeNotice is { } notice)
        {
            empty = notice;
        }
        else if (_placeStops.Count == 0)
        {
            busy = true;
            empty = new Notice(AppStrings.Get("FindingStops"), string.Empty);
        }

        // Only near you needs the device's location; a board elsewhere, or a fallback, doesn't.
        bool showLocationHint = _center is NearYou && _locationPermissionRequired;
        if (showLocationHint && _placeStops.Count == 0)
        {
            busy = false;
            empty = new Notice(AppStrings.Get("LocationPermissionEmptyTitle"), AppStrings.Get("LocationPermissionEmptyText"));
        }

        LocationPermissionButton.Visibility = showLocationHint ? Visibility.Visible : Visibility.Collapsed;
        LocationPermissionMessage.Text = _locationPermissionError ?? AppStrings.Get("LocationPermissionMessage/Text");
        if (!showLocationHint)
        {
            LocationPermissionFlyout.Hide();
        }

        GrantLocationPermissionButton.IsEnabled = !_isRequestingLocationPermission;
        LoadingRing.IsActive = loading;
        LiveDot.Opacity = error is null ? 1 : 0.35;
        UpdatedText.Text = fetchedAt is { } at ? AppStrings.Format("UpdatedFormat", at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : loading ? AppStrings.Get("Loading")
            : string.Empty;
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
        StatusBar.Title = AppStrings.Get(fetchedAt is null ? "CantLoadArrivals" : "ShowingEarlierTimes");
        RenderPlaceChip();
        RenderSuggestion(now);
        RenderTrip(now);
        RenderProgress(now);
        ShowList(groups);
        UpdateSummary(savedIds);

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

    /// <summary>Shows the trip card: the suggested trip at live times, or what's in its way.</summary>
    private void RenderSuggestion(DateTimeOffset now)
    {
        _suggestion = SuggestionItem(now);
        SuggestionCard.Visibility = _suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        SuggestionCard.DestinationId = _destinationId;
        SuggestionCard.ShowSaveDestination(_temporaryDestination is not null && _trips.Current is null, _suggestionMonitor.IsLoading || _isReplanning);
        if (_suggestion is not null)
        {
            SuggestionCard.Show(_suggestion);
        }
    }

    /// <summary>
    /// The card's contents, read from the suggestion's discovery each time (its plan can be replaced); null hides it.
    /// During a trip, the card is the trip.
    /// </summary>
    private SuggestionCardItem? SuggestionItem(DateTimeOffset now)
    {
        if (_trips.Current is { } active)
        {
            return SuggestionCardItem.ForActiveTrip(active, _trips.Step, now);
        }

        if (!_monitor.Service.IsConfigured)
        {
            return null; // The board says what's missing.
        }

        IReadOnlyList<FavoritePlace> places = SettingsService.Places;
        if (!places.Any(p => p.Kind == PlaceKind.Home) && _temporaryDestination is null && _destinationId is null)
        {
            return SuggestionCardItem.ForNotice(SetUpNotice(), MapHelpers.PlaceGlyph(PlaceKind.Home));
        }

        if (_suggestionMonitor.Discovery is not { } found)
        {
            string glyph = MapHelpers.PlaceGlyph(null);
            return _suggestionError is { } error
                ? SuggestionCardItem.ForNotice(new Notice(AppStrings.Get("SuggestionErrorTitle"), error), glyph)
                : SuggestionCardItem.ForNotice(new Notice(AppStrings.Get("SuggestionPlanning"), string.Empty), glyph, isBusy: true);
        }

        if (_isReplanning)
        {
            return SuggestionCardItem.ForNotice(new Notice(AppStrings.Get("TripFindingRoute"), string.Empty), MapHelpers.PlaceGlyph(found.Leg?.To.Kind), isBusy: true);
        }

        return found is { Leg: { } leg, Plan: { } plan } && LiveTrip.Best(plan, _suggestionMonitor.Snapshot?.Arrivals ?? [], now) is { } trip
            ? SuggestionCardItem.ForTrip(leg, trip, now)
            : SuggestionCardItem.ForNotice(found.EmptyNotice, MapHelpers.PlaceGlyph(found.Leg?.To.Kind));
    }

    /// <summary>Tells the tray tooltip when the suggested trip, or which saved stops are on the board, changed.</summary>
    private void UpdateSummary(IReadOnlySet<string> savedIds)
    {
        string? trip = _suggestion?.TooltipText;
        string[] stops = [.. _placeStops.Select(n => n.Stop.Id).Where(savedIds.Contains)];
        if (trip == TripSummary && stops.SequenceEqual(SavedStopIds))
        {
            return;
        }

        TripSummary = trip;
        SavedStopIds = stops;
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private StopGroupItem BuildBoardGroup(NearbyStop nearby, bool isSaved, BoardRow row)
    {
        IndexedStop stop = nearby.Stop;

        // Each line is listed at one stop only (its closest, or a saved stop), so it isn't repeated.
        bool Listed(string routeId, string directionId) => row.Listed(new StopLine(stop.Id, routeId, directionId));

        int walkMinutes = Math.Max(1, nearby.WalkMinutes(WalkingMetersPerMinute));
        string[] lines = [.. stop.Routes.Where(r => Listed(r.RouteId, r.DirectionId)).Select(r => r.RouteDisplayId).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> details = [];
        if (nearby.DistanceMeters > 0) // Every saved stop has no center to be a distance from.
        {
            string distance = AppStrings.Distance(nearby.DistanceMeters);
            details.Add(row.NearYou ? AppStrings.Format("StopWalkFormat", distance, walkMinutes) : distance);
        }

        if (lines.Length > 0)
        {
            details.Add(lines.Length <= 5 ? string.Join(", ", lines) : AppStrings.Format("MoreLinesFormat", string.Join(", ", lines.Take(5)), lines.Length - 5));
        }
        else if (_shownPlace?.EveryLineAt?.Contains(stop.Id) ?? false)
        {
            details.Add(AppStrings.Get("AllRoutes"));
        }

        // Stops on opposite sides of a street share a name, so say which way the lines go.
        string[] directions = [.. stop.Routes
            .Where(r => Listed(r.RouteId, r.DirectionId))
            .Select(r => r.DirectionName.Trim())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)];
        string name = StopName(stop.Name);
        string title = directions.Length == 0 ? name : $"{name} · {string.Join("/", directions)}";

        // Walks are from the rider's location, so only the board near you flags departures they can't walk to in time.
        bool TooSoon(Arrival a) => row.NearYou && a.Status != ArrivalStatus.Canceled && a.MinutesAway(row.Now) < walkMinutes;
        IReadOnlyDictionary<StopLine, TripTiming>? trips = row.Suggestion?.Trips;
        List<ArrivalItem> arrivals = [.. RouteArrivals.ByRoute(
                _placeArrivals.Where(a => a.StopId == stop.Id && Listed(a.RouteId, a.DirectionId)),
                row.Tracked,
                a => a.Status != ArrivalStatus.Canceled && !TooSoon(a),
                isSaved ? RoutesPerStop : RoutesPerNearbyStop,
                TimesPerRoute)
            .Select(route =>
            {
                Arrival a = route.Next;
                bool tooSoon = TooSoon(a);
                return new ArrivalItem(
                    a,
                    stop.Location,
                    RouteColor(a.RouteColor ?? stop.Routes.FirstOrDefault(r => string.Equals(r.RouteId, a.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor),
                    row.Now,
                    route.Later,
                    row.Tracked,
                    tooSoonToWalk: tooSoon,
                    // A row that starts the suggested trip says when it gets the rider there.
                    destinationName: row.Suggestion?.Leg?.To.Name,
                    destinationEta: tooSoon || a.Status == ArrivalStatus.Canceled ? null : PlaceBoard.ArrivalAtDestination(a, trips),
                    transfers: trips is not null && trips.TryGetValue(StopLine.Of(a), out TripTiming? timing) ? timing.Transfers : 0);
            })];

        return new StopGroupItem(stop.Id, title, string.Join(" · ", details), stop.Location, arrivals, row.EmptyText, stop, isSaved);
    }

    private static Color RouteColor(string? color) => MapHelpers.ParseRouteColor(color);

    // ---- Events ----

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        ++_savedRevision; // The board pins saved stops: it's found again, staying shown until then.
        if (_isPopupVisible)
        {
            Render(); // Update the save stars.
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.StopsChanged);
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
        ++_savedRevision; // Near you falls back to near Home.
        if (_isPopupVisible)
        {
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.PlacesChanged);
        }
    }

    /// <summary>Route shapes arrived with some response: draw the lines that were missing them.</summary>
    private void OnPatternsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isPopupVisible && !_allowClose)
        {
            _drawnRoutes = null;
            DrawRoutes();
        }
    });

    // ---- Map ----

    private Task FrameListedStopsAsync(MapAnimationKind animation)
    {
        // A new flyout has no viewport yet; fitting its bounds then would use a near-world zoom.
        _frameWhenMapSized = Map.ActualWidth <= 0 || Map.ActualHeight <= 0;
        if (_frameWhenMapSized)
        {
            return Task.CompletedTask;
        }

        if (IsTripShown)
        {
            return MapHelpers.FrameAsync(Map, _tripMap.Points, animation);
        }

        List<GeoPoint> points = [.. _placeStops.Select(n => n.Stop.Location)];
        if (_placeOrigin is { } origin)
        {
            points.Add(origin);
        }

        return MapHelpers.FrameAsync(Map, points, animation);
    }

    private void RebuildStopPins()
    {
        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        foreach (NearbyStop stop in _placeStops)
        {
            _stopPins[new MapIcon(_stopGlyph, MapHelpers.ToGeopoint(stop.Stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) }] = stop.Stop.Name;
        }

        _stopLayer.MapElements.AddRange(_stopPins.Keys);
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
        foreach ((MapPolyline line, string routeId) in _routeLines)
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
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(IsProgressOpen ? TransitConsumer.ActiveTrip : TransitConsumer.Nearby, trigger);
        if (!_monitor.Service.IsConfigured || !_isPopupVisible || _selectedLine is not { } line
            || ((IsProgressOpen || trigger == TransitTrigger.Timer) && _budget.Level != TransitBudgetLevel.Normal))
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

    /// <summary>
    /// During a trip, shows the vehicle of the upcoming or current ride, through the tracking path
    /// (only that trip's vehicle), without moving the map. Only while the call budget is at Normal, and only
    /// for a ride with a trip ID to match; otherwise no vehicle is shown or fetched.
    /// </summary>
    private void FollowRide(ActiveTrip trip, TripStep? step, DateTimeOffset now)
    {
        if (step is not { CanFollowVehicle: true }
            || (step.Kind == TripStepKind.GetReady && step.ActionAt - now > TripTime.LiveWindow)
            || _budget.Level != TransitBudgetLevel.Normal || trip.Progress.Rides[step.Ride].TripId is not { } tripId)
        {
            if (_selectedLine is not null)
            {
                ClearVehicles();
            }

            return;
        }

        RideLine ride = trip.Progress.Trip.Legs.OfType<LiveRide>().ElementAt(step.Ride).Line;
        RouteDirection line = new(ride.Route.Id, ride.Direction.Id);
        if (_selectedLine == line && _onlyTripId == tripId)
        {
            return;
        }

        ClearVehicles();
        _selectedLine = line;
        _selectedLineLabel = LineLabel(ride.Route.Mode, ride.Route.DisplayId);
        _selectedTripId = tripId;
        _onlyTripId = tripId;
        _ = RefreshVehiclesAsync(TransitTrigger.VehicleSelected);
    }

    /// <summary>"Bus 14", or "Route 14" when the feed doesn't name the mode.</summary>
    private static string LineLabel(string? mode, string displayId) =>
        mode is { Length: > 0 } ? $"{mode} {displayId}" : $"Route {displayId}";

    private static string LineLabel(Arrival arrival) => LineLabel(arrival.Mode, arrival.RouteDisplayId);

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
            ShowMapCaption(AppStrings.Format("TrackStoppedFormat", item.ClockText, LineLabel(item.Arrival)));
            return;
        }

        TrackOrAsk(item, (FrameworkElement)sender);
    }

    /// <summary>Starts tracking <paramref name="item"/>; during a trip, first asks whether to end it, as one thing is tracked at a time (D1).</summary>
    private void TrackOrAsk(BusTimeItem item, FrameworkElement anchor)
    {
        if (_trips.Current is not { } trip)
        {
            StartTracking(item);
            return;
        }

        ConfirmFlyout.Show(anchor, TripMessages.TrackInstead(trip), AppStrings.Get("TrackInsteadConfirm"), AppStrings.Get("TrackInsteadCancel"), () =>
        {
            _trips.End();
            StartTracking(item);
        });
    }

    private void StartTracking(BusTimeItem item)
    {
        string stopName = item.StopName ?? _groups.FirstOrDefault(g => g.Arrivals.Any(a => a.Times.Contains(item)))?.Title ?? string.Empty;
        RequestPop(item.Key);
        _tracker.Start(item.Arrival, stopName);
        ShowLine(item.Arrival, item.StopLocation, onlyThisTrip: true); // Show just its bus and zoom to it.
        int lead = SettingsService.HeadOutMinutes;
        string ride = AppStrings.Format("TrackRideFormat", item.ClockText, LineLabel(item.Arrival));
        ShowMapCaption(item.Arrival.MinutesAway(DateTimeOffset.Now) <= lead
            ? AppStrings.Format("TrackHeadOutFormat", ride, lead)
            : item.Arrival.IsRealTime
                ? AppStrings.Format("TrackStartedFormat", ride, lead)
                : AppStrings.Format("TrackScheduledFormat", ride, lead));
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
                Text = minutes == 1 ? AppStrings.Get("TrackNotifyOneMinute") : AppStrings.Format("TrackNotifyMinutesFormat", minutes),
                GroupName = "HeadOutLead",
                IsChecked = minutes == current,
            };
            item.Click += (_, _) =>
            {
                SettingsService.HeadOutMinutes = minutes;
                if (arrival is { IsTracked: false } && sender is FrameworkElement chip)
                {
                    TrackOrAsk(arrival, chip);
                    return;
                }

                ShowMapCaption(AppStrings.Format("TrackLeadChangedFormat", minutes));
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

    /// <summary>Saves a listed stop (all routes), or removes it if already saved.</summary>
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

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        _ = RefreshVehiclesAsync(TransitTrigger.ManualRefresh, force: true);
        // The saved stops' times (for the jump menu and the tooltip) join the board's batch: one call.
        _ = _monitor.RefreshAsync();
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.ManualRefresh);
    }

    /// <summary>Opens the trip using only the plan and departures already shown by the card; during a trip, its progress.</summary>
    private void SuggestionCard_TripInvoked(object? sender, EventArgs e)
    {
        if (_trips.Current is not null)
        {
            OpenProgress();
            return;
        }

        if (_suggestion is not { Trip: { } trip, Leg: { } leg } || _suggestionMonitor.Discovery?.Plan is not { } plan)
        {
            return;
        }

        if (_tripView is null)
        {
            _tripView = new TripView();
            _tripView.BackRequested += TripPanel_BackRequested;
            _tripView.OptionChosen += TripPanel_OptionChosen;
            _tripView.TimeRequested += TripPanel_TimeRequested;
            _tripView.GoRequested += TripPanel_GoRequested;
            TripHost.Content = _tripView;
        }

        _tripLeg = leg;
        _tripMap.Invalidate();
        _vehiclePositions.Invalidate();
        ClearVehicles();
        HideMapCaption(animate: false);
        ResetRoutes();
        _tripSelection.Update(plan, _suggestionMonitor.Snapshot?.Arrivals ?? [], DateTimeOffset.Now, trip.Itinerary);
        ArrivalsPane.Visibility = Visibility.Collapsed;
        PlaceChip.Visibility = Visibility.Collapsed;
        TripHost.Visibility = Visibility.Visible;
        SetFitLabel(isTrip: true);
        RenderTrip(DateTimeOffset.Now);
        _tripView.FocusBack();
    }

    private void RenderTrip(DateTimeOffset now)
    {
        if (_tripLeg is not { } leg || _tripView is null)
        {
            return;
        }

        PlaceStops? found = _suggestionMonitor.Discovery;
        if (found?.Leg is { } latest && latest.To.Id != leg.To.Id)
        {
            CloseTrip(focusCard: true);
            return;
        }

        bool timed = _tripTime.Mode != TripTimeMode.Now;
        IPlaceArrivalsMonitor monitor = timed ? _timedTripMonitor : _suggestionMonitor;
        IReadOnlyList<Arrival> arrivals = monitor.Snapshot?.Arrivals ?? [];
        PlaceStops? shown = monitor.Discovery;
        if (shown is { Leg: { } current, Plan: { } plan })
        {
            _tripLeg = leg = current;
            _tripSelection.Update(plan, arrivals, now, time: _tripTime);
        }
        else if (timed)
        {
            _tripSelection.Update(new TripPlan([], []), [], now, time: _tripTime);
        }

        _tripView.Show(leg, _tripSelection, arrivals, _tracker.Current, now, (DataTemplate)Root.Resources["BusTimeTemplate"],
            timed ? _timedTripMonitor.IsLoading && shown?.Plan is null
                : _isReplanning || (_suggestionMonitor.IsLoading && shown?.Plan is null),
            timed ? _timedTripError : _suggestionError, _tripTime);
        if (_tripSelection.Selected is { } selected && _tripMap.Draw(selected, leg))
        {
            _vehiclePositions.Invalidate();
            ClearVehicles();
            _ = FrameListedStopsAsync(MapAnimationKind.Default);
        }
    }

    private void TripPanel_OptionChosen(object? sender, TripItinerary itinerary)
    {
        _tripSelection.Select(itinerary);
        HideMapCaption(animate: false);
        RenderTrip(DateTimeOffset.Now);
    }

    private void TripPanel_BackRequested(object? sender, EventArgs e) => CloseTrip(focusCard: true);

    /// <summary>
    /// Go: follows the selected option at the chosen time, from the departures it was timed with (no request),
    /// then shows its progress. A trip over 99 minutes away is scheduled.
    /// </summary>
    private void TripPanel_GoRequested(object? sender, EventArgs e)
    {
        if (_tripLeg is not { } leg || _tripSelection.Selected is not { } trip)
        {
            return;
        }

        IPlaceArrivalsMonitor monitor = _tripTime.Mode == TripTimeMode.Now ? _suggestionMonitor : _timedTripMonitor;
        string started = TripMessages.Started(trip, leg, DateTimeOffset.Now);
        MapView? view = CurrentMapView();
        _trips.Start(trip, leg, SuggestionContext.Target(leg.To), _tripTime, monitor.Snapshot);
        _trips.MapView = view;
        CloseTrip(focusCard: false, restoreBoard: false);
        OpenProgress();
        ShowMapCaption(started);
    }

    private void TripPanel_TimeRequested(object? sender, TripTime time)
    {
        if (_tripLeg is not { } leg)
        {
            return;
        }

        _timedTripMonitor.IsForeground = false;
        _tripTime = time;
        _timedTripError = null;
        _timedTripContext = time.Mode == TripTimeMode.Now ? null : new TimedTripContext(leg, SuggestionContext.Target(leg.To), time);
        if (_timedTripContext is { } context)
        {
            _timedTripMonitor.Configure(context);
            _timedTripMonitor.IsForeground = true;
            _ = RefreshTimedTripAsync(TransitTrigger.TripTimeChanged);
        }

        _tripMap.Invalidate();
        RenderTrip(DateTimeOffset.Now);
    }

    private async Task RefreshTimedTripAsync(TransitTrigger trigger, bool force = false)
    {
        if (!_timedTripMonitor.IsForeground)
        {
            return;
        }

        int version = _timedTripMonitor.Version;
        string? error = await RefreshAsync(_timedTripMonitor, TransitConsumer.Trip, trigger, force);
        if (version == _timedTripMonitor.Version && _isPopupVisible && !_allowClose)
        {
            _timedTripError = error;
            RenderTrip(DateTimeOffset.Now);
        }
    }

    private async Task<PlaceStops> FindTimedTripAsync(TimedTripContext context, PlaceLocation location, CancellationToken cancellationToken)
    {
        TripPlan plan = await _monitor.Metadata.PlanTripAsync(context.Leg.Origin, context.Destination, context.Time, cancellationToken);
        return new PlaceStops(context.Leg.Origin, [], new Notice(AppStrings.Format("SuggestionNoTripFormat", context.Leg.To.Name), string.Empty),
            Leg: context.Leg, Plan: plan, WatchedStops: PlaceBoard.BoardingStopIds(plan, i => context.Time.UsesLiveTimes(i, DateTimeOffset.Now)));
    }

    private bool IsTimedTripOutdated(PlaceStops found) =>
        _timedTripContext is { } context && found.Plan is { } plan
        && (!_monitor.Metadata.IsCurrentPlan(context.Leg.Origin, context.Destination, context.Time, plan)
            || !found.StopIds.SequenceEqual(PlaceBoard.BoardingStopIds(plan, i => context.Time.UsesLiveTimes(i, DateTimeOffset.Now))));

    /// <summary>Restores the board and its map without discovery or a departures request.</summary>
    private void CloseTrip(bool focusCard, bool restoreBoard = true)
    {
        if (!IsTripOpen)
        {
            return;
        }

        _tripLeg = null;
        _timedTripMonitor.IsForeground = false;
        _timedTripContext = null;
        _tripTime = default;
        _timedTripError = null;
        TripHost.Visibility = Visibility.Collapsed;
        if (restoreBoard)
        {
            ShowBoardAgain(focusCard);
        }
    }

    /// <summary>Puts the board and its map back from what's already loaded, after a trip view or the trip's progress.</summary>
    private void ShowBoardAgain(bool focusCard)
    {
        ArrivalsPane.Visibility = Visibility.Visible;
        PlaceChip.Visibility = Visibility.Visible;
        _vehiclePositions.Invalidate();
        ClearVehicles();
        HideMapCaption(animate: false);
        _routeLayer.MapElements.Clear();
        ResetRoutes();
        RebuildStopPins();
        _placeLayer.MapElements.Clear();
        if (_shownPlace is { } found)
        {
            ShowCenterMarker(found);
        }

        DrawRoutes();
        SetFitLabel(isTrip: false);
        _ = FrameListedStopsAsync(MapAnimationKind.Default);
        if (focusCard)
        {
            SuggestionCard.FocusTrip();
        }
    }

    /// <summary>
    /// Shows the active trip's progress (or its Resume card) over the board, drawing the trip on the map. It
    /// reads the trip from <see cref="ActiveTripService"/> and requests nothing; the board keeps loading beneath.
    /// </summary>
    private void OpenProgress()
    {
        // Even when already open: a reopen's CloseSearch has just put the board back over it.
        ArrivalsPane.Visibility = Visibility.Collapsed;
        PlaceChip.Visibility = Visibility.Collapsed;
        if (IsProgressOpen)
        {
            RenderProgress(DateTimeOffset.Now);
            _progressView?.FocusBack();
            return;
        }

        if (_progressView is null)
        {
            _progressView = new TripProgressView();
            _progressView.BackRequested += (_, _) => CloseProgress(focusCard: true);
            _progressView.EndRequested += (_, _) => _trips.End();
            _progressView.MissedRequested += (_, _) => _ = _trips.MissedAsync();
            _progressView.ResumeRequested += (_, _) => _ = _trips.ResumeAsync();
            _progressView.StepInvoked += ProgressView_StepInvoked;
            ProgressHost.Content = _progressView;
        }

        _stepZoom.Reset();
        _tripMap.Invalidate();
        _progressMapInitialized = false;
        _vehiclePositions.Invalidate();
        ClearVehicles();
        HideMapCaption(animate: false);
        ResetRoutes();
        ProgressHost.Visibility = Visibility.Visible;
        SetFitLabel(isTrip: true);
        RenderProgress(DateTimeOffset.Now);
        _progressView.FocusBack();
    }

    /// <summary>Back to the board, where the card shows the trip (the trip goes on); also when the trip ends.</summary>
    private void CloseProgress(bool focusCard)
    {
        if (!IsProgressOpen)
        {
            return;
        }

        SaveProgressMapView();
        _progressMapInitialized = false;
        ProgressHost.Visibility = Visibility.Collapsed;
        ShowBoardAgain(focusCard);
    }

    /// <summary>The trip's progress at <paramref name="now"/>, its map with the current leg bold, and the current ride's vehicle.</summary>
    private void RenderProgress(DateTimeOffset now)
    {
        if (!IsProgressOpen || _progressView is null)
        {
            return;
        }

        if (_trips.Current is not { } trip)
        {
            CloseProgress(focusCard: true);
            return;
        }

        TripStep? step = _trips.Step; // Null while the trip waits to be resumed.
        _progressView.Show(trip, step, now);
        if (_tripMap.Draw(trip.Progress.Trip, trip.Leg))
        {
            _vehiclePositions.Invalidate();
            ClearVehicles();
            if (!_progressMapInitialized)
            {
                _progressMapInitialized = true;
                if (_trips.MapView is { } view)
                {
                    RestoreMapView(view);
                }
                else
                {
                    _ = FrameListedStopsAsync(MapAnimationKind.None);
                }
            }
        }

        _tripMap.Emphasize(step?.Leg ?? -1);
        FollowRide(trip, step, now);
    }

    /// <summary>
    /// A click on one of the progress view's steps frames that leg on the map: a walk from point to point, or a ride
    /// from boarding to getting off. On the ride the rider is on, clicks alternate between the bus about where it is
    /// now and the whole ride.
    /// </summary>
    private void ProgressView_StepInvoked(object? sender, int index)
    {
        if (_trips.Current is not { } trip || trip.Progress.Trip.Legs.ElementAtOrDefault(index) is not { } leg)
        {
            return;
        }

        bool onRide = leg is LiveRide && _trips.Step is { Kind: TripStepKind.Ride } step && step.Leg == index;
        TripStepView view = _stepZoom.Tap(index, onRide);
        switch (leg)
        {
            case LiveWalk walk:
                _ = MapHelpers.FrameAsync(Map, walk.Walk.Path, MapAnimationKind.Default, ShortWalkZoom);
                break;
            case LiveRide ride when view == TripStepView.Vehicle:
                List<GeoPoint> vehicle = [RideVehicleLocation(ride, DateTimeOffset.Now)];
                _ = MapHelpers.FrameAsync(Map, vehicle, MapAnimationKind.Default, VehicleZoom);
                break;
            case LiveRide ride:
                List<GeoPoint> route = [.. ride.Line.Path, ride.Line.BoardAt.Location];
                if (ride.Line.AlightAt is { } alight)
                {
                    route.Add(alight.Location);
                }

                _ = MapHelpers.FrameAsync(Map, route, MapAnimationKind.Default);
                break;
        }
    }

    /// <summary>Where the rider's bus is: its reported position, or roughly where its times put it along the ride.</summary>
    private GeoPoint RideVehicleLocation(LiveRide ride, DateTimeOffset now)
    {
        if (_selectedTripId is { } tripId && _vehicles.Values.FirstOrDefault(v => v.TripId == tripId) is { } vehicle)
        {
            return vehicle.Location;
        }

        double length = (ride.Ends - ride.Starts).TotalSeconds;
        return GeoMath.PointAlong(ride.Line.Path, length > 0 ? (now - ride.Starts).TotalSeconds / length : 0) ?? ride.Line.BoardAt.Location;
    }

    /// <summary>The trip started, moved on, ended, or was resumed: the progress view and the board's card follow it.</summary>
    private void OnTripsChanged(object? sender, EventArgs e)
    {
        if (_isPopupVisible && !_allowClose)
        {
            Render();
        }
    }

    private void SetFitLabel(bool isTrip)
    {
        string label = AppStrings.Get(isTrip ? "TripFitLabel" : "BoardFitLabel");
        FitMenuItem.Text = label;
        AutomationProperties.SetName(FitMenuItem, label);
    }

    private sealed record TimedTripContext(CommuteLeg Leg, TripDestination Destination, TripTime Time);

    private void SuggestionCard_DestinationChosen(object? sender, string? placeId)
    {
        _temporaryDestination = null;
        _destinationId = placeId;
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.DestinationChanged);
    }

    private void SuggestionCard_SearchRequested(object? sender, EventArgs e)
    {
        CloseTrip(focusCard: false);
        if (_destinationSearch is null)
        {
            _destinationSearch = new DestinationSearchView();
            _destinationSearch.BackRequested += (_, _) => CloseSearch(focusDestination: true);
            _destinationSearch.DestinationChosen += DestinationSearch_DestinationChosen;
            SearchHost.Content = _destinationSearch;
        }

        SearchHost.Visibility = Visibility.Visible;
        ArrivalsPane.Visibility = Visibility.Collapsed;
        GeoPoint? near = _placeOrigin ?? SettingsService.Places.FirstOrDefault(p => p.Kind == PlaceKind.Home)?.Location
            ?? (Map.Center is { } center ? new GeoPoint(center.Position.Latitude, center.Position.Longitude) : null);
        _destinationSearch.Open(_monitor.Service, near);
    }

    private void CloseSearch(bool focusDestination)
    {
        _destinationSearch?.Cancel();
        SearchHost.Visibility = Visibility.Collapsed;
        ArrivalsPane.Visibility = Visibility.Visible;
        if (focusDestination)
        {
            SuggestionCard.FocusDestination();
        }
    }

    private void DestinationSearch_DestinationChosen(object? sender, DestinationResult result)
    {
        _temporaryDestination = result.AsPlace();
        _destinationId = _temporaryDestination.Id;
        CloseSearch(focusDestination: true);
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.DestinationChanged);
    }

    private void SuggestionCard_SaveDestinationRequested(object? sender, EventArgs e)
    {
        if (_temporaryDestination is not { } place)
        {
            return;
        }

        if (_suggestionMonitor.Discovery?.Leg is { } leg)
        {
            _monitor.Metadata.SaveDestinationPlan(leg.Origin, SuggestionContext.Target(place));
        }

        _temporaryDestination = null;
        SettingsService.SavePlace(place); // PlacesChanged refreshes both monitors and the destination menu.
        SuggestionCard.FocusDestination();
    }

    private void FitMenuItem_Click(object sender, RoutedEventArgs e) => FitMap();

    private void FitMap()
    {
        _stepZoom.Reset();
        DeselectVehicle();
        _ = FrameListedStopsAsync(MapAnimationKind.Default);
    }

    private void Fit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FitMap();
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
        if (IsSearchOpen)
        {
            CloseSearch(focusDestination: true);
        }
        else if (IsTripOpen)
        {
            CloseTrip(focusCard: true);
        }
        else if (IsProgressOpen)
        {
            CloseProgress(focusCard: true);
        }
        else
        {
            HidePopup();
        }
    }

    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTripOpen)
        {
            args.Handled = true;
            CloseTrip(focusCard: true);
        }
        else if (IsProgressOpen)
        {
            args.Handled = true;
            CloseProgress(focusCard: true);
        }
    }

    /// <summary>What every board row is built from in one render.</summary>
    /// <param name="Listed">Which lines the board lists at each stop.</param>
    /// <param name="NearYou">Whether the board is around the rider, so walks and too-soon departures apply.</param>
    /// <param name="Suggestion">The trip suggestion: rows that start its trip say when they get the rider there.</param>
    /// <param name="EmptyText">What a stop without departures says.</param>
    /// <param name="Tracked">The tracked departure, if the board lists it.</param>
    private readonly record struct BoardRow(
        Func<StopLine, bool> Listed,
        bool NearYou,
        PlaceStops? Suggestion,
        string EmptyText,
        Arrival? Tracked,
        DateTimeOffset Now);
}
