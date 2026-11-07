using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Geolocation;
using TrayTimes.Commute;
using TrayTimes.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
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
/// Borderless arrivals popup shown next to the tray icon. What it shows and does is in <see cref="FlyoutViewModel"/>:
/// one departures board, centered near the device (or a saved place or stop the rider jumps to), a card above it
/// suggesting the trip, and, during a trip (<see cref="ActiveTripService"/>), the trip's progress. This window times
/// the popup (it light-dismisses when focus leaves; hiding keeps it warm for a quick reopen, and the app closes it for
/// good, see <see cref="CloseWindow"/>, a minute later), draws the map, and animates the rows.
/// </summary>
public sealed partial class TimesFlyoutWindow : WindowEx
{
    private const int PopupWidth = 440;
    private const int PopupHeight = 680;
    private const double DefaultMapHeight = 230;
    private const double MinMapHeight = 100;
    private const double MinListHeight = 140;
    private const double SplitterKeyStep = 20;
    private const double ShortWalkZoom = 17; // a walk too short to frame by its bounds: about a block's width fits
    private const double VehicleZoom = 16; // a bus and the streets around it
    private static readonly TimeSpan PendingPopWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(15); // keeps minutes counting down between refreshes

    // Draw order: route lines, then stops, then the place markers, then vehicles on top (each is a
    // white disc with its bus or train emoji over it, or a larger yellow disc when selected).
    private const double SelectedRouteThickness = 8;
    private const double UnselectedRouteThickness = 4;

    private readonly HWND _hwnd;
    private readonly ArrivalsMonitor _monitor;
    private readonly BusTrackingService _tracker;
    private readonly ActiveTripService _trips;
    private readonly TransitBudget _budget;
    private readonly VehiclePositionsMonitor _vehiclePositions;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _vehicleTimer;
    private readonly DispatcherQueueTimer _placeTimer;
    private readonly DispatcherQueueTimer _captionTimer;
    private readonly DispatcherQueueTimer _clockTimer;

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
    private readonly Dictionary<VehicleKind, FontIcon> _vehicleGlyphs = []; // Shared so every vehicle reuses one emoji raster.
    private readonly FontIcon _busBackdropGlyph;
    private readonly FontIcon _selectedBusBackdropGlyph;
    private readonly long _mapHeadingCallbackToken;
    private string? _selectedVehicleId;

    // The row picked to show vehicles for: positions are fetched for this route direction only.
    private RouteDirection? _selectedLine;
    private string _selectedLineLabel = string.Empty;
    private VehicleKind _selectedLineVehicle;
    private string? _selectedTripId;
    private string? _onlyTripId; // When set (tracking), only this trip's vehicle is drawn, not the whole line's.
    private GeoPoint? _frameWithVehicle; // The picked row's stop, to frame with its vehicle once positions arrive.

    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _mapRevealStarted;
    private bool _frameWhenMapSized;
    private DateTime _lastDismissedAtUtc;

    // Which page the views have been set up for (see FlyoutViewModel.Page).
    private FlyoutPage _shownPage = FlyoutPage.Board;
    private DestinationSearchView? _destinationSearch;
    private readonly TripMapRenderer _tripMap;
    private TripView? _tripView;
    private TripProgressView? _progressView;
    private bool _progressMapInitialized;
    private readonly TripStepZoom _stepZoom = new();

    // Motion state: which rows have already animated in, so a refresh only animates what's new or changed.
    private readonly HashSet<string> _shownRows = [];
    private readonly Dictionary<string, string> _lastMinutes = [];
    private string? _listSignature;

    // Scroll state that outlives list rebuilds, so tracking a bus or a refresh doesn't jump the list.
    private readonly Dictionary<string, double> _stripOffsets = []; // each route row's time strip, by row key
    private bool _keepListScroll;
    private int _entranceStep;
    private int _chipStep;
    private readonly HashSet<string> _shownChips = [];
    private (string Key, DateTime Until)? _pendingPop; // A bell or star just clicked, to animate once its row is rebuilt.
    private bool _captionShown;
    private int _captionVersion;

    internal TimesFlyoutWindow(ArrivalsMonitor monitor, BusTrackingService tracker, ActiveTripService trips, TransitBudget budget)
    {
        _monitor = monitor;
        _tracker = tracker;
        _trips = trips;
        _budget = budget;
        ViewModel = new FlyoutViewModel(monitor, tracker, trips, App.Current.ShowSettings);
        InitializeComponent();
        _vehiclePositions = new VehiclePositionsMonitor(_monitor.Service, UpdateVehicles, log: DiagnosticLogService.Write);
        ViewModel.Rendered += OnRendered;
        ViewModel.BoardEntering += OnBoardEntering;
        ViewModel.BoardEntered += OnBoardEntered;
        ViewModel.BoardUpdated += OnBoardUpdated;
        ViewModel.BoardRestoreRequested += (_, focusCard) => ShowBoardAgain(focusCard);
        ViewModel.PageChanged += OnPageChanged;
        ViewModel.ProgressClosing += (_, _) =>
        {
            SaveProgressMapView();
            _progressMapInitialized = false;
        };
        ViewModel.TimesRefreshed += (_, _) =>
        {
            // A heartbeat: fresh times just arrived.
            Motion.Pop(LiveDot, 2.4f);
            Motion.Enter(UpdatedText, rise: 4);
        };
        ViewModel.EmptyEntering += (_, _) => Motion.Enter(EmptyState, rise: 10);
        ViewModel.SummaryChanged += (_, _) => SummaryChanged?.Invoke(this, EventArgs.Empty);

        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), repeating: false, HideIfFocusLeftWindow);
        _vehicleTimer = CreateTimer(VehiclePositionsMonitor.RefreshInterval, repeating: true, () => _ = RefreshVehiclesAsync(TransitTrigger.Timer));
        _placeTimer = CreateTimer(_monitor.RefreshInterval, repeating: true, OnPlaceTimerTick);
        _captionTimer = CreateTimer(TimeSpan.FromSeconds(5), repeating: false, () => HideMapCaption(animate: true));
        _clockTimer = CreateTimer(ClockInterval, repeating: true, ViewModel.Render);

        _stopGlyph = new FontIcon { Glyph = MapHelpers.StopGlyph, FontSize = 14 };
        _tripMap = new TripMapRenderer(_routeLayer, _stopLayer, _placeLayer, _stopGlyph, _stopPins);
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
            if (ViewModel.IsTripShown)
            {
                _tripMap.Invalidate();
                RenderTrip(DateTimeOffset.Now);
                RenderProgress(DateTimeOffset.Now);
            }
        };
        MapHelpers.ApplyBasemap(Map, Root, MapCover);
        RestoreMapView();
        MapRow.Height = new GridLength(Math.Max(MinMapHeight, SettingsService.FlyoutMapHeight ?? DefaultMapHeight));

        _monitor.Metadata.PatternsChanged += OnPatternsChanged;

        ViewModel.Render();
    }

    /// <summary>Raised when the popup is dismissed (the window stays alive until closed).</summary>
    public event EventHandler? PopupHidden;

    /// <summary>Raised when <see cref="TripSummary"/> or <see cref="SavedStopIds"/> changes.</summary>
    public event EventHandler? SummaryChanged;

    internal FlyoutViewModel ViewModel { get; }

    public bool IsPopupVisible => _isPopupVisible;

    /// <summary>The suggested trip in one line for the tray tooltip, e.g. "Work: leave in 6 min, 14 at 8:12"; null when there's none.</summary>
    public string? TripSummary => ViewModel.TripSummary;

    /// <summary>The saved stops on the board, nearest its center first.</summary>
    public IReadOnlyList<string> SavedStopIds => ViewModel.SavedStopIds;

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
        ViewModel.Show();
        ViewModel.StartShowing();
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
        _destinationSearch?.Cancel();
        _vehiclePositions.IsForeground = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _vehicleTimer.Stop();
        _placeTimer.Stop();
        _captionTimer.Stop();
        _clockTimer.Stop();
        HideMapCaption(animate: false);
        ViewModel.Hide();
        SaveMapView();
        AppWindow.Hide();
        PopupHidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window for real, releasing the map, its timers, and event subscriptions.</summary>
    public void CloseWindow()
    {
        _allowClose = true;
        HidePopup();
        ViewModel.Close();
        SuggestionCard.TripInvoked -= SuggestionCard_TripInvoked;
        SuggestionCard.DestinationChosen -= SuggestionCard_DestinationChosen;
        SuggestionCard.SearchRequested -= SuggestionCard_SearchRequested;
        SuggestionCard.SaveDestinationRequested -= SuggestionCard_SaveDestinationRequested;
        _monitor.Metadata.PatternsChanged -= OnPatternsChanged;
        Map.UnregisterPropertyChangedCallback(WinUIEx.Maps.MapControl.HeadingProperty, _mapHeadingCallbackToken);
        Close();
    }

    private void OnPlaceTimerTick()
    {
        _placeTimer.Interval = _monitor.RefreshInterval; // The call budget may have moved on.
        _ = ViewModel.LoadPlaceAsync(reframe: false, TransitTrigger.Timer);
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
            if (ViewModel.IsLocationPermissionPending && !ViewModel.IsRequestingLocationPermission)
            {
                ViewModel.RetryLocationAfterPermission();
            }

            return;
        }

        if (_isShowing || !_isPopupVisible || ViewModel.IsLocationPermissionPending)
        {
            return;
        }

        // Debounce: focus can bounce briefly while XAML popups (tooltips, menus) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        if (!ViewModel.IsLocationPermissionPending && !ShouldRemainVisible(PInvoke.GetForegroundWindow()))
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
        if (ViewModel.ShowsProgress)
        {
            SaveProgressMapView();
        }
        else if (ViewModel.ShowsTrip || ViewModel.ShownPlace?.Center is not NearYou)
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

    // ---- The board and its map ----

    /// <summary>The board is being entered afresh: every row rises in again rather than silently swapping, scrolled back to the start.</summary>
    private void OnBoardEntering(object? sender, EventArgs e)
    {
        _vehiclePositions.Invalidate();
        _listSignature = null;
        _shownRows.Clear();
        _shownChips.Clear();
        _lastMinutes.Clear();
        _stripOffsets.Clear();
        _keepListScroll = false;
        if (!ViewModel.IsTripShown)
        {
            ClearVehicles();
            _routeLayer.MapElements.Clear();
            ResetRoutes();
            _placeLayer.MapElements.Clear();
        }
    }

    private void OnBoardEntered(object? sender, EventArgs e)
    {
        _placeTimer.Interval = _monitor.RefreshInterval;
        _placeTimer.Start();
    }

    /// <summary>Puts the board's stops, marker, and routes on the map (unless a trip owns it).</summary>
    private void OnBoardUpdated(object? sender, BoardUpdate update)
    {
        if (update.Found is null)
        {
            if (!ViewModel.IsTripShown)
            {
                _stopLayer.MapElements.Clear();
                _stopPins.Clear();
                _placeLayer.MapElements.Clear();
                _routeLayer.MapElements.Clear();
                ResetRoutes();
            }

            return;
        }

        if (update.Changed && !ViewModel.IsTripShown)
        {
            RebuildStopPins();
            ShowCenterMarker(update.Found);
        }

        if (update.Reframe && !ViewModel.IsTripShown)
        {
            _ = FrameListedStopsAsync(MapAnimationKind.None);
        }

        DrawRoutes();
    }

    /// <summary>The jump menu: near you, each saved place, each saved stop with its next departure, and every saved stop.</summary>
    private void PlaceMenu_Opening(object sender, object e)
    {
        PlaceMenu.Items.Clear();
        bool separated = false;
        foreach (CenterOption option in ViewModel.CenterOptions())
        {
            if (!separated && option.Center is NearStop)
            {
                PlaceMenu.Items.Add(new MenuFlyoutSeparator());
                separated = true;
            }

            RadioMenuFlyoutItem item = new()
            {
                Text = option.Label,
                GroupName = "BoardCenter",
                IsChecked = option.IsChecked,
                Icon = new FontIcon { Glyph = option.Glyph },
            };
            BoardCenter center = option.Center;
            item.Click += (_, _) => ViewModel.JumpTo(center);
            PlaceMenu.Items.Add(item);
        }

        PlaceMenu.Items.Add(new MenuFlyoutSeparator());
        PlaceMenu.Items.Add(new MenuFlyoutItem
        {
            Text = AppStrings.Get("ManageFavorites"),
            Icon = new FontIcon { Glyph = "" }, // Edit
            Command = ViewModel.OpenSettingsCommand,
        });
    }

    private async void GrantLocationPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop();
        await ViewModel.GrantLocationPermissionAsync(() => ShouldRemainVisible(PInvoke.GetForegroundWindow()));
    }

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
        Func<StopLine, bool> listed = ViewModel.BoardListing();
        IReadOnlyList<Arrival> arrivals = ViewModel.PlaceArrivals;
        HashSet<RouteDirection> wanted = [.. arrivals
            .Where(a => a.Status != ArrivalStatus.Canceled && listed(StopLine.Of(a)))
            .Select(a => new RouteDirection(a.RouteId, a.DirectionId))];
        if (_allowClose || ViewModel.IsTripShown || (_drawnRoutes?.SetEquals(wanted) ?? false))
        {
            return;
        }

        List<(MapPolyline Line, string RouteId)> lines = [];
        foreach (RouteDirection route in wanted)
        {
            Color color = MapHelpers.ParseRouteColor(arrivals.First(a => new RouteDirection(a.RouteId, a.DirectionId) == route).RouteColor);
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

    // ---- Rendering ----

    /// <summary>The board's data was recomputed: bring the card, the trip pages, and the list up to date.</summary>
    private void OnRendered(object? sender, EventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (!ViewModel.ShowsLocationHint)
        {
            LocationPermissionFlyout.Hide();
        }

        SuggestionCard.DestinationId = ViewModel.DestinationId;
        SuggestionCard.ShowSaveDestination(ViewModel.CanSaveDestination, ViewModel.IsSuggestionBusy);
        if (ViewModel.Suggestion is { } suggestion)
        {
            SuggestionCard.Show(suggestion);
        }

        RenderTrip(now);
        RenderProgress(now);
        ShowList(ViewModel.Groups);
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

        // A row keeps its animation history only while it stays listed; one that leaves and returns is new again.
        HashSet<string> listed = [.. groups.SelectMany(g => g.Keys)];
        _shownRows.IntersectWith(listed);
        _shownChips.IntersectWith(groups.SelectMany(g => g.Arrivals).SelectMany(a => a.Times).Select(t => t.Key));
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

    // ---- Map ----

    /// <summary>Route shapes arrived with some response: draw the lines that were missing them.</summary>
    private void OnPatternsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isPopupVisible && !_allowClose)
        {
            _drawnRoutes = null;
            DrawRoutes();
        }
    });

    private Task FrameListedStopsAsync(MapAnimationKind animation)
    {
        // A new flyout has no viewport yet; fitting its bounds then would use a near-world zoom.
        _frameWhenMapSized = Map.ActualWidth <= 0 || Map.ActualHeight <= 0;
        if (_frameWhenMapSized)
        {
            return Task.CompletedTask;
        }

        if (ViewModel.IsTripShown)
        {
            return MapHelpers.FrameAsync(Map, _tripMap.Points, animation);
        }

        List<GeoPoint> points = [.. ViewModel.NearbyStops.Select(n => n.Stop.Location)];
        if (ViewModel.PlaceOrigin is { } origin)
        {
            points.Add(origin);
        }

        return MapHelpers.FrameAsync(Map, points, animation);
    }

    private void RebuildStopPins()
    {
        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        foreach (NearbyStop stop in ViewModel.NearbyStops)
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
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(ViewModel.ShowsProgress ? TransitConsumer.ActiveTrip : TransitConsumer.Nearby, trigger);
        if (!_monitor.Service.IsConfigured || !_isPopupVisible || _selectedLine is not { } line
            || ((ViewModel.ShowsProgress || trigger == TransitTrigger.Timer) && _budget.Level != TransitBudgetLevel.Normal))
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
            vehicles = vehicles.Where(v => v.TripId == _onlyTripId).ToList();
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
                FontIcon arrowGlyph = MapHelpers.CreateGlyph("", Microsoft.UI.Colors.White, 17); // CaretUpSolid8
                arrowGlyph.Width = 32;
                arrowGlyph.Height = 32;
                arrowGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
                icons = (
                    // The emoji draws right of center in its box; anchor right of center to recenter it on the disc.
                    new MapIcon(VehicleGlyph(_selectedLineVehicle), location) { NormalizedAnchorPoint = new Point(0.53, 0.53) },
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
                _ = MapHelpers.FrameAsync(Map, new[] { stop, trip.Location }, MapAnimationKind.Default);
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

        _selectedLineLabel = FlyoutViewModel.LineLabel(arrival);
        _selectedLineVehicle = arrival.VehicleKind;
        _selectedTripId = arrival.TripId;
        _onlyTripId = onlyThisTrip ? arrival.TripId : null;
        Vehicle? known = arrival.TripId is null ? null : _vehicles.Values.FirstOrDefault(v => v.TripId == arrival.TripId);
        SelectVehicle(known?.Id);
        _frameWithVehicle = known is null ? stop : null;
        _ = MapHelpers.FrameAsync(Map, known is null ? new[] { stop } : new[] { stop, known.Location }, MapAnimationKind.Default);
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
        _selectedLineLabel = FlyoutViewModel.LineLabel(ride.Route.Mode, ride.Route.DisplayId);
        _selectedLineVehicle = ride.Route.VehicleKind;
        _selectedTripId = tripId;
        _onlyTripId = tripId;
        _ = RefreshVehiclesAsync(TransitTrigger.VehicleSelected);
    }

    private FontIcon VehicleGlyph(VehicleKind kind)
    {
        if (!_vehicleGlyphs.TryGetValue(kind, out FontIcon? glyph))
        {
            glyph = MapHelpers.CreateEmoji(MapHelpers.VehicleEmoji(kind), 19.5);
            _vehicleGlyphs[kind] = glyph;
        }

        return glyph;
    }

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
        _chipStep = 0; // A row's chips load before the row does; the next row's start over.
        if (sender is FrameworkElement { DataContext: ArrivalItem item } row && _shownRows.Add(item.Key))
        {
            int step = _entranceStep++;
            Motion.Enter(row, step);
            Motion.Grow(row, step);
        }
    }

    /// <summary>A new bus's chip rises in just after its row, each chip a beat after the one before it.</summary>
    private void Chip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BusTimeItem item } chip && _shownChips.Add(item.Key))
        {
            Motion.Enter(chip, _entranceStep + 1 + _chipStep++, rise: 8);
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
            ShowMapCaption(ViewModel.StopTracking(item));
            return;
        }

        TrackOrAsk(item, (FrameworkElement)sender);
    }

    /// <summary>Starts tracking <paramref name="item"/>; during a trip, first asks whether to end it, as one thing is tracked at a time (D1).</summary>
    private void TrackOrAsk(BusTimeItem item, FrameworkElement anchor)
    {
        if (ViewModel.TrackInsteadQuestion is not { } question)
        {
            StartTracking(item);
            return;
        }

        ConfirmFlyout.Show(anchor, question, AppStrings.Get("TrackInsteadConfirm"), AppStrings.Get("TrackInsteadCancel"), () =>
        {
            ViewModel.EndTrip();
            StartTracking(item);
        });
    }

    private void StartTracking(BusTimeItem item)
    {
        RequestPop(item.Key);
        string caption = ViewModel.StartTracking(item);
        ShowLine(item.Arrival, item.StopLocation, onlyThisTrip: true); // Show just its bus and zoom to it.
        ShowMapCaption(caption);
    }

    private static readonly int[] LeadChoices = [1, 2, 3, 5, 10, 15, 20, 30];

    /// <summary>Right-click on a time chip: lists how far ahead to notify, with the current choice checked.</summary>
    private void TrackButton_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        MenuFlyout menu = new();
        int current = ViewModel.HeadOutMinutes;
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
                ViewModel.HeadOutMinutes = minutes;
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
            _ = MapHelpers.FrameAsync(Map, new[] { group.Location }, MapAnimationKind.Default, singlePointZoom: 16);
        }
    }

    /// <summary>Saves a listed stop (all routes), or removes it if already saved.</summary>
    private void SaveStopButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StopGroupItem { Saveable: not null } group)
        {
            return;
        }

        RequestPop(group.StarKey);
        ShowMapCaption(ViewModel.ToggleSavedStop(group));
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        _ = RefreshVehiclesAsync(TransitTrigger.ManualRefresh, force: true);
        ViewModel.Refresh();
    }

    // ---- Pages: the destination search, a trip's options, and the trip's progress ----

    /// <summary>Sets up the views for the page the flyout moved to.</summary>
    private void OnPageChanged(object? sender, EventArgs e)
    {
        FlyoutPage page = ViewModel.Page;
        if (page == _shownPage && page != FlyoutPage.Progress)
        {
            return;
        }

        FlyoutPage previous = _shownPage;
        _shownPage = page;
        if (previous == FlyoutPage.Search)
        {
            _destinationSearch?.Cancel();
        }

        switch (page)
        {
            case FlyoutPage.Search:
                ShowSearch();
                break;
            case FlyoutPage.Trip:
                ShowTripPage();
                break;
            case FlyoutPage.Progress:
                ShowProgressPage(previous);
                break;
        }
    }

    private void ShowSearch()
    {
        if (_destinationSearch is null)
        {
            _destinationSearch = new DestinationSearchView();
            _destinationSearch.BackRequested += (_, _) =>
            {
                ViewModel.CloseSearch();
                SuggestionCard.FocusDestination();
            };
            _destinationSearch.DestinationChosen += (_, result) =>
            {
                ViewModel.ChooseSearchedDestination(result);
                SuggestionCard.FocusDestination();
            };
            SearchHost.Content = _destinationSearch;
        }

        GeoPoint? mapCenter = Map.Center is { } center ? new GeoPoint(center.Position.Latitude, center.Position.Longitude) : null;
        _destinationSearch.Open(_monitor.Service, ViewModel.SearchNear(mapCenter));
    }

    private void ShowTripPage()
    {
        if (_tripView is null)
        {
            _tripView = new TripView();
            _tripView.BackRequested += (_, _) => ViewModel.CloseTrip(restoreBoard: true, focusCard: true);
            _tripView.OptionChosen += TripPanel_OptionChosen;
            _tripView.TimeRequested += TripPanel_TimeRequested;
            _tripView.GoRequested += TripPanel_GoRequested;
            TripHost.Content = _tripView;
        }

        _tripMap.Invalidate();
        _vehiclePositions.Invalidate();
        ClearVehicles();
        HideMapCaption(animate: false);
        ResetRoutes();
        SetFitLabel(isTrip: true);
        RenderTrip(DateTimeOffset.Now);
        _tripView.FocusBack();
    }

    private void RenderTrip(DateTimeOffset now)
    {
        if (_tripView is null || ViewModel.UpdateTrip(now) is not { } trip)
        {
            return;
        }

        _tripView.Show(trip.Leg, trip.Selection, trip.Arrivals, trip.Tracked, trip.Alerts, now, (DataTemplate)Root.Resources["BusTimeTemplate"],
            trip.IsReplanning, trip.Error, trip.Time);
        if (trip.Selection.Selected is { } selected && _tripMap.Draw(selected, trip.Leg))
        {
            _vehiclePositions.Invalidate();
            ClearVehicles();
            _ = FrameListedStopsAsync(MapAnimationKind.Default);
        }
    }

    private void TripPanel_OptionChosen(object? sender, TripItinerary itinerary)
    {
        ViewModel.ChooseTripOption(itinerary);
        HideMapCaption(animate: false);
        RenderTrip(DateTimeOffset.Now);
    }

    /// <summary>Go: starts the trip and shows its progress.</summary>
    private void TripPanel_GoRequested(object? sender, EventArgs e)
    {
        if (ViewModel.StartTrip(CurrentMapView()) is { } started)
        {
            ShowMapCaption(started);
        }
    }

    private void TripPanel_TimeRequested(object? sender, TripTime time)
    {
        ViewModel.SetTripTime(time);
        _tripMap.Invalidate();
        RenderTrip(DateTimeOffset.Now);
    }

    /// <summary>Puts the board and its map back from what's already loaded, after a trip view or the trip's progress.</summary>
    private void ShowBoardAgain(bool focusCard)
    {
        _shownPage = FlyoutPage.Board;
        _vehiclePositions.Invalidate();
        ClearVehicles();
        HideMapCaption(animate: false);
        _routeLayer.MapElements.Clear();
        ResetRoutes();
        RebuildStopPins();
        _placeLayer.MapElements.Clear();
        if (ViewModel.ShownPlace is { } found)
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
    private void ShowProgressPage(FlyoutPage previous)
    {
        if (previous == FlyoutPage.Progress)
        {
            RenderProgress(DateTimeOffset.Now);
            _progressView?.FocusBack();
            return;
        }

        if (_progressView is null)
        {
            _progressView = new TripProgressView();
            _progressView.BackRequested += (_, _) => ViewModel.CloseProgress(focusCard: true);
            _progressView.EndRequested += (_, _) => ViewModel.EndTrip();
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
        SetFitLabel(isTrip: true);
        RenderProgress(DateTimeOffset.Now);
        _progressView.FocusBack();
    }

    /// <summary>The trip's progress at <paramref name="now"/>, its map with the current leg bold, and the current ride's vehicle.</summary>
    private void RenderProgress(DateTimeOffset now)
    {
        if (!ViewModel.ShowsProgress || _progressView is null)
        {
            return;
        }

        if (_trips.Current is not { } trip)
        {
            ViewModel.CloseProgress(focusCard: true);
            return;
        }

        TripStep? step = _trips.Step; // Null while the trip waits to be resumed.
        _progressView.Show(trip, step, _monitor.Metadata.Alerts, now);
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

    private void SetFitLabel(bool isTrip)
    {
        string label = AppStrings.Get(isTrip ? "TripFitLabel" : "BoardFitLabel");
        FitMenuItem.Text = label;
        AutomationProperties.SetName(FitMenuItem, label);
    }

    private void SuggestionCard_TripInvoked(object? sender, EventArgs e) => ViewModel.OpenTrip();

    private void SuggestionCard_DestinationChosen(object? sender, string? placeId) => ViewModel.ChooseDestination(placeId);

    private void SuggestionCard_SearchRequested(object? sender, EventArgs e) => ViewModel.OpenSearch();

    private void SuggestionCard_SaveDestinationRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SaveTemporaryDestination())
        {
            SuggestionCard.FocusDestination();
        }
    }

    // ---- Keyboard and toolbar ----

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
        ViewModel.OpenSettingsCommand.Execute(null);
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Refresh();
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        switch (ViewModel.Page)
        {
            case FlyoutPage.Search:
                ViewModel.CloseSearch();
                SuggestionCard.FocusDestination();
                break;
            case FlyoutPage.Trip:
                ViewModel.CloseTrip(restoreBoard: true, focusCard: true);
                break;
            case FlyoutPage.Progress:
                ViewModel.CloseProgress(focusCard: true);
                break;
            default:
                HidePopup();
                break;
        }
    }

    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ShowsTrip)
        {
            args.Handled = true;
            ViewModel.CloseTrip(restoreBoard: true, focusCard: true);
        }
        else if (ViewModel.ShowsProgress)
        {
            args.Handled = true;
            ViewModel.CloseProgress(focusCard: true);
        }
    }
}
