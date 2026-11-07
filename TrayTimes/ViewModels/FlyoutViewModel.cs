using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Commute;
using TrayTimes.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Tracking;
using TrayTimes.Transit;
using TrayTimes.Views;
using Windows.Devices.Geolocation;
using Notice = TrayTimes.Commute.PlaceNotice;

namespace TrayTimes.ViewModels;

/// <summary>Which page of the flyout is over the map: the board, the destination search, a trip's options, or the active trip's progress.</summary>
internal enum FlyoutPage
{
    Board,
    Search,
    Trip,
    Progress,
}

/// <summary>What changed on the board when it was updated, for the view to put on the map.</summary>
/// <param name="Found">The discovery now shown; null when there is none.</param>
/// <param name="Changed">Whether it is a different discovery from the one shown before.</param>
/// <param name="Reframe">Whether the map should be fitted to it.</param>
internal readonly record struct BoardUpdate(PlaceStops? Found, bool Changed, bool Reframe);

/// <summary>One choice in the jump menu.</summary>
/// <param name="Next">The stop's next departure, e.g. "14 in 4 min"; null for places.</param>
/// <summary>A stop was saved or removed: the key of its star (to pop) and the caption to show.</summary>
internal sealed record StopSaveToggledEventArgs(string StarKey, string Caption);

internal sealed record CenterOption(BoardCenter Center, string Label, string Glyph, bool IsChecked, string? Next);

/// <summary>What the trip view shows, read from the loaded plan and departures.</summary>
internal sealed record TripPresentation(
    CommuteLeg Leg,
    TripSelection Selection,
    IReadOnlyList<Arrival> Arrivals,
    TrackedBus? Tracked,
    AlertBook Alerts,
    bool IsReplanning,
    string? Error,
    TripTime Time);

/// <summary>
/// The flyout: one departures board, centered near the device (or a saved place or stop the rider jumps to), with saved
/// stops pinned first, a card above it suggesting the trip to wherever the rider is likely headed next, and, during a
/// trip, its progress. It loads and refreshes the board and the suggestion together, keeps the location permission and
/// destination choices, and decides which page is showing. The window draws the map, animates the rows, and times the
/// popup; it learns what changed from this class's events.
/// </summary>
internal sealed partial class FlyoutViewModel : ObservableObject
{
    private const int RoutesPerStop = 4;
    private const int RoutesPerNearbyStop = 3;
    private const int TimesPerRoute = 6; // the next bus, then the ones after it in the row's scrolling strip
    private const int MaxNearbyStops = 8;
    private const int MaxRoutedStops = 12; // walking routes looked up per discovery
    private const double WalkingMetersPerMinute = 80; // about 3 mph
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(10);

    private readonly ArrivalsMonitor _monitor;
    private readonly BusTrackingService _tracker;
    private readonly ActiveTripService _trips;
    private readonly Action _showSettings;
    private readonly SharedLocation _location;
    private readonly PlaceArrivalsMonitor<BoardCenter> _boardMonitor; // Nearby
    private readonly PlaceArrivalsMonitor<SuggestionContext> _suggestionMonitor; // Commute
    private readonly PlaceArrivalsMonitor<TimedTripContext> _timedTripMonitor;
    private readonly TripSelection _tripSelection = new();

    private bool _isShown;
    private bool _isClosed;
    private bool _locationPermissionRequired;
    private string? _locationPermissionError;
    private int _locationLookups; // so a lookup that a newer one replaced doesn't change the permission state

    // Board state.
    private BoardCenter _center = new NearYou(); // The center the rider picked; it can fall back (see BoardCenter.Resolve).
    private int _savedRevision; // Bumped when saved stops or places change, so the board is found again.
    private IReadOnlyList<NearbyStop> _placeStops = [];
    private DateTimeOffset? _placeFetchedAt;
    private string? _placeError;
    private Notice? _placeNotice;
    private PredictionSnapshot? _shownPlaceSnapshot;
    private bool _placeReframe;
    private DateTimeOffset? _lastFetchedAt;
    private Action? _emptyAction;

    // Suggestion state.
    private FavoritePlace? _temporaryDestination;
    private string? _suggestionError;
    private bool _isReplanning;

    // Trip state.
    private TimedTripContext? _timedTripContext;
    private string? _timedTripError;
    private CommuteLeg? _tripLeg;

    public FlyoutViewModel(ArrivalsMonitor monitor, BusTrackingService tracker, ActiveTripService trips, Action showSettings)
    {
        _monitor = monitor;
        _tracker = tracker;
        _trips = trips;
        _showSettings = showSettings;
        _location = new SharedLocation(LocatePlaceAsync);
        _boardMonitor = new PlaceArrivalsMonitor<BoardCenter>(_monitor.Predictions, TransitConsumer.Nearby, _location.LocateAsync,
            FindBoardStopsAsync,
            isOutdated: found => found.Revision != _savedRevision);
        _suggestionMonitor = new PlaceArrivalsMonitor<SuggestionContext>(_monitor.Predictions, TransitConsumer.Commute, _location.LocateAsync,
            FindSuggestionAsync,
            holds: (context, found, here) => found.Leg == context.Leg(here, DateTimeOffset.Now),
            isOutdated: found => found is { Leg: { } leg, Plan: { } plan } && !_monitor.Metadata.IsCurrentPlan(leg.Origin, SuggestionContext.Target(leg.To), plan));
        _timedTripMonitor = new PlaceArrivalsMonitor<TimedTripContext>(_monitor.Predictions, TransitConsumer.Trip,
            _ => Task.FromResult(new PlaceLocation(null)), FindTimedTripAsync, isOutdated: IsTimedTripOutdated);
        _boardMonitor.Updated += OnBoardUpdated;
        _suggestionMonitor.Updated += OnSuggestionUpdated;
        _timedTripMonitor.Updated += OnSuggestionUpdated;
        _tracker.Changed += OnTrackingChanged;
        SettingsService.StopsChanged += OnStopsChanged;
        SettingsService.PlacesChanged += OnPlacesChanged;
        PlaceChipText = CenterLabel(_center);
        PlaceChipGlyph = CenterGlyph(_center);
    }

    // Events for the window.

    /// <summary>Raised after the board's data was recomputed (each time the page should be brought up to date).</summary>
    public event EventHandler? Rendered;

    /// <summary>Raised when the board was updated, for the map to show its stops, marker, and routes.</summary>
    public event EventHandler<BoardUpdate>? BoardUpdated;

    /// <summary>Raised as the board is entered, before it loads: the window clears what it showed (rows, vehicles, map).</summary>
    public event EventHandler? BoardEntering;

    /// <summary>Raised when the board has been entered and starts loading, so the window can start its refresh timer.</summary>
    public event EventHandler? BoardEntered;

    /// <summary>Raised when the board and its map should be put back from what is loaded after a trip page closed; the value is whether focus goes to the card.</summary>
    public event EventHandler<bool>? BoardRestoreRequested;

    /// <summary>Raised when the page over the map changed (see <see cref="Page"/>).</summary>
    public event EventHandler? PageChanged;

    /// <summary>Raised when the trip's progress page is about to close, for the window to keep its map view.</summary>
    public event EventHandler? ProgressClosing;

    /// <summary>Raised when fresh times arrived while the flyout is shown (a heartbeat).</summary>
    public event EventHandler? TimesRefreshed;

    /// <summary>Raised when the empty state replaced something else, for it to animate in.</summary>
    public event EventHandler? EmptyEntering;

    /// <summary>Raised when <see cref="TripSummary"/> or <see cref="SavedStopIds"/> changes.</summary>
    public event EventHandler? SummaryChanged;

    /// <summary>Raised as a refresh is requested, for the window to refresh what only it holds (the vehicles on the map).</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when a stop was saved or removed, for the window to pop the star and show the caption.</summary>
    public event EventHandler<StopSaveToggledEventArgs>? StopSaveToggled;

    // Presentation.

    public IReadOnlyList<StopGroupItem> Groups { get; private set; } = [];

    /// <summary>The suggested trip in one line for the tray tooltip, e.g. "Work: leave in 6 min, 14 at 8:12"; null when there's none.</summary>
    public string? TripSummary { get; private set; }

    /// <summary>The saved stops on the board, nearest its center first.</summary>
    public IReadOnlyList<string> SavedStopIds { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial double LiveDotOpacity { get; set; } = 1;

    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowsLocationHint { get; set; }

    [ObservableProperty]
    public partial string LocationPermissionMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlaceChipText { get; set; }

    [ObservableProperty]
    public partial string PlaceChipGlyph { get; set; }

    [ObservableProperty]
    public partial string PlaceChipName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    public partial string EmptyText { get; set; } = string.Empty;

    public bool HasEmptyText => EmptyText.Length > 0;

    [ObservableProperty]
    public partial bool EmptyBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyButton))]
    public partial string? EmptyButtonText { get; set; }

    public bool HasEmptyButton => EmptyButtonText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion))]
    public partial SuggestionCardItem? Suggestion { get; set; }

    public bool HasSuggestion => Suggestion is not null;

    /// <summary>How many minutes ahead of a tracked bus the rider is told to head out.</summary>
    public int HeadOutMinutes
    {
        get => SettingsService.HeadOutMinutes;
        set => SettingsService.HeadOutMinutes = value;
    }

    /// <summary>The place picked from Going to, or null to guess.</summary>
    public string? DestinationId { get; private set; }

    public bool HasTemporaryDestination => _temporaryDestination is not null;

    public bool CanSaveDestination => _temporaryDestination is not null && _trips.Current is null;

    public bool IsSuggestionBusy => _suggestionMonitor.IsLoading || _isReplanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBoard), nameof(ShowsSearch), nameof(ShowsTrip), nameof(ShowsProgress), nameof(ShowsPlaceChip), nameof(IsTripShown))]
    public partial FlyoutPage Page { get; set; }

    public bool ShowsBoard => Page == FlyoutPage.Board;

    public bool ShowsSearch => Page == FlyoutPage.Search;

    public bool ShowsTrip => Page == FlyoutPage.Trip;

    public bool ShowsProgress => Page == FlyoutPage.Progress;

    public bool ShowsPlaceChip => Page is FlyoutPage.Board or FlyoutPage.Search;

    /// <summary>A trip, not the board, owns the map.</summary>
    public bool IsTripShown => Page is FlyoutPage.Trip or FlyoutPage.Progress;

    // State the window reads.

    public ArrivalsMonitor Monitor => _monitor;

    public IReadOnlyList<NearbyStop> NearbyStops => _placeStops;

    public GeoPoint? PlaceOrigin { get; private set; }

    public IReadOnlyList<Arrival> PlaceArrivals { get; private set; } = [];

    public PlaceStops? ShownPlace { get; private set; }

    public bool IsLocationPermissionPending { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGrantLocationPermission))]
    public partial bool IsRequestingLocationPermission { get; private set; }

    public bool CanGrantLocationPermission => !IsRequestingLocationPermission;

    public TripTime TripTime { get; private set; }

    public CommuteLeg? TripLeg => _tripLeg;

    public IPlaceArrivalsMonitor SuggestionMonitor => _suggestionMonitor;

    /// <summary>Whether the flyout is up and not closing for good: nothing is requested or drawn otherwise.</summary>
    public bool IsActive => _isShown && !_isClosed;

    private SuggestionContext SuggestionContext => new(SettingsService.Places, DestinationId, _temporaryDestination);

    /// <summary>Where the board is centered: where its discovery resolved to, else where the rider picked.</summary>
    public BoardCenter ShownCenter => _boardMonitor.Discovery?.Center ?? _center;

    /// <summary>The target a trip to <paramref name="leg"/>'s destination is planned to, for the active trip to keep.</summary>
    public TripDestination TripTarget(CommuteLeg leg) => SuggestionContext.Target(leg.To);

    /// <summary>Which lines the board lists at each stop, given its departures (see <see cref="PlaceStops.Listing"/>).</summary>
    public Func<StopLine, bool> BoardListing() => ShownPlace?.Listing(PlaceArrivals, SavedStopIdSet()) ?? (_ => true);

    // Lifecycle.

    /// <summary>The popup was shown: the board and the saved stops' times are wanted.</summary>
    public void Show()
    {
        _isShown = true;
        _monitor.IsForeground = true; // The saved stops' times, for the jump menu and the tooltip.
        _center = BoardCenter.FromKey(SettingsService.BoardCenterKey, SettingsService.Places, SettingsService.Stops); // Each open starts where the rider last looked (near you, at first)...
        DestinationId = null; // ...and guesses the next trip afresh.
        _temporaryDestination = null;
        _trips.Changed -= OnTripsChanged; // Only while shown: the service outlives this view model, which a subscription would keep alive.
        _trips.Changed += OnTripsChanged;
    }

    /// <summary>The popup was hidden: nothing is wanted or listened to until it is shown again.</summary>
    public void Hide()
    {
        _isShown = false;
        _trips.Changed -= OnTripsChanged;
        IsLocationPermissionPending = false;
        _monitor.IsForeground = false;
        ClearPlacePredictionDemand();
    }

    /// <summary>The window closed for good: stops listening.</summary>
    public void Close()
    {
        _isClosed = true;
        Hide();
        _boardMonitor.Updated -= OnBoardUpdated;
        _suggestionMonitor.Updated -= OnSuggestionUpdated;
        _timedTripMonitor.Updated -= OnSuggestionUpdated;
        _tracker.Changed -= OnTrackingChanged;
        SettingsService.StopsChanged -= OnStopsChanged;
        SettingsService.PlacesChanged -= OnPlacesChanged;
    }

    /// <summary>
    /// What every open shows, however the window appeared: the board near you, guessing the next trip afresh, and during a
    /// trip, its progress (or its Resume card) over the board. Back from it shows the board.
    /// </summary>
    public void StartShowing()
    {
        CloseSearch();
        CloseTrip(restoreBoard: true);
        if (_trips.Current is null)
        {
            CloseProgress();
        }
        else
        {
            OpenProgress();
        }

        // Load the board beneath progress without replacing the trip's camera or map layers.
        EnterBoard(reframe: SettingsService.FlyoutMapView is null, TransitTrigger.FlyoutOpened);
    }

    // The board and its center.

    /// <summary>Shows the board at its center, with its route lines and arrivals, and the trip suggestion, loading as needed.</summary>
    public void EnterBoard(bool reframe, TransitTrigger trigger = TransitTrigger.CenterChanged)
    {
        ClearPlacePredictionDemand();
        BoardEntering?.Invoke(this, EventArgs.Empty);
        ConfigurePlaces();
        ShownPlace = null; // Entering cleared the map layers, not the valid discovery.
        _placeReframe = reframe;
        _boardMonitor.IsForeground = true;
        _suggestionMonitor.IsForeground = true;
        OnBoardUpdated(this, EventArgs.Empty);
        BoardEntered?.Invoke(this, EventArgs.Empty);
        _ = LoadPlaceAsync(reframe, trigger);
    }

    private void ConfigurePlaces()
    {
        _boardMonitor.Configure(_center);
        _suggestionMonitor.Configure(SuggestionContext);
    }

    /// <summary>Moves the board to <paramref name="center"/>, framing it on the map.</summary>
    public void JumpTo(BoardCenter center)
    {
        CloseTrip(restoreBoard: true);
        _center = center;
        SettingsService.BoardCenterKey = center.ToKey();
        IsLocationPermissionPending = false;
        EnterBoard(reframe: true);
    }

    /// <summary>The jump menu: near you, each saved place, each saved stop with its next departure, and every saved stop.</summary>
    public IReadOnlyList<CenterOption> CenterOptions()
    {
        BoardCenter shown = ShownCenter;
        List<CenterOption> options = [Option(new NearYou(), shown, null)];
        options.AddRange(SettingsService.Places.Select(place => Option(new NearPlace(place), shown, null)));
        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        if (saved.Count > 0)
        {
            // The saved stops' departures ride in every batch while the flyout is open, so this costs no request.
            ArrivalsSnapshot favorites = _monitor.Latest;
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (IGrouping<string, SavedStop> stop in saved.GroupBy(s => s.StopId, StringComparer.Ordinal))
            {
                Arrival? next = stop.SelectMany(favorites.For).Where(a => a.Status != ArrivalStatus.Canceled).MinBy(a => a.PredictedTime);
                string? when = next is null ? null
                    : next.MinutesAway(now) == 0 ? AppStrings.Format("JumpNextDueFormat", AppStrings.Route(next.RouteDisplayId))
                    : AppStrings.Format("JumpNextFormat", AppStrings.Route(next.RouteDisplayId), next.MinutesAway(now));
                options.Add(Option(new NearStop(stop.First()), shown, when));
            }

            options.Add(Option(new AllSavedStops(), shown, null));
        }

        return options;

        static CenterOption Option(BoardCenter center, BoardCenter shown, string? next) =>
            new(center, next is null ? CenterLabel(center) : AppStrings.Format("JumpStopFormat", CenterLabel(center), next), CenterGlyph(center), center == shown, next);
    }

    private static string CenterLabel(BoardCenter center) => center switch
    {
        NearPlace near => AppStrings.Format("CenterNearFormat", near.Place.Name),
        NearStop near => StopName(near.Stop.DisplayName),
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

    internal static string StopName(string name) => name.Length > 0 ? name : AppStrings.Get("UnnamedStop");

    /// <summary>What a board away from the rider is near: the saved place or stop.</summary>
    private static string CenterName(BoardCenter center) => center switch
    {
        NearPlace near => near.Place.Name,
        NearStop near => StopName(near.Stop.DisplayName),
        _ => string.Empty,
    };

    // Location.

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

        if (lookup == _locationLookups && IsActive)
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

    /// <summary>Asks Windows for location access, or opens its settings when access was refused; the flyout stays up meanwhile.</summary>
    /// <param name="remainsVisible">Whether the flyout is still the window in front once the request is over.</param>
    public async Task GrantLocationPermissionAsync(Func<bool>? remainsVisible)
    {
        if (IsRequestingLocationPermission)
        {
            return;
        }

        IsRequestingLocationPermission = true;
        IsLocationPermissionPending = true;
        _locationPermissionError = null;
        Render();
        try
        {
            GeolocationAccessStatus? access = await LocationService.RequestAccessAsync(
                message => _locationPermissionError = message);
            if (!IsActive)
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
            IsRequestingLocationPermission = false;
            if (IsActive)
            {
                Render();
                if (remainsVisible?.Invoke() ?? true)
                {
                    RetryLocationAfterPermission();
                }
            }
        }
    }

    /// <summary>The flyout came back to the front after the permission was asked for.</summary>
    public void RetryLocationAfterPermission()
    {
        IsLocationPermissionPending = false;
        if (IsActive)
        {
            // Cancel any discovery started before permission changed, rather than joining that old attempt.
            EnterBoard(reframe: true, TransitTrigger.Retry);
        }
    }

    // Loading.

    /// <summary>
    /// Refreshes the board and the trip suggestion together, so they share one location lookup and
    /// one departures batch. Predictions refresh independently of any location/discovery work.
    /// </summary>
    public async Task LoadPlaceAsync(bool reframe, TransitTrigger trigger)
    {
        if (!IsActive)
        {
            return;
        }

        ConfigurePlaces();
        int boardVersion = _boardMonitor.Version;
        int suggestionVersion = _suggestionMonitor.Version;
        SuggestionContext context = SuggestionContext;
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
        if (!IsActive)
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

    /// <summary>Refreshes the board and the card by the rider's request, and the saved stops' times with them (one call).</summary>
    [RelayCommand]
    public void Refresh()
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
        _ = _monitor.RefreshAsync();
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.ManualRefresh);
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
            if (!ReferenceEquals(replanned, plan) && IsActive && version == _suggestionMonitor.Version)
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
            if (IsActive)
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
        if (!IsActive)
        {
            return;
        }

        PlaceStops? found = _boardMonitor.Discovery;
        if (found is null)
        {
            PlaceOrigin = null;
            _placeStops = [];
            PlaceArrivals = [];
            _placeFetchedAt = null;
            _placeError = null;
            _placeNotice = null;
            ShownPlace = null;
            _shownPlaceSnapshot = null;
            BoardUpdated?.Invoke(this, new BoardUpdate(null, Changed: false, Reframe: false));
            Render();
            return;
        }

        PredictionSnapshot? snapshot = _boardMonitor.Snapshot;
        bool changed = !ReferenceEquals(ShownPlace, found);
        PlaceOrigin = found.Origin;
        _placeStops = found.Stops;
        PlaceArrivals = snapshot?.Arrivals ?? [];
        if (snapshot is not null && !ReferenceEquals(snapshot, _shownPlaceSnapshot))
        {
            _placeError = null;
        }

        _shownPlaceSnapshot = snapshot;
        _placeFetchedAt = found.Stops.Count == 0 ? null : snapshot?.FetchedAt;
        _placeNotice = found.Stops.Count == 0 ? found.EmptyNotice : null;
        if (changed)
        {
            ShownPlace = found;
        }

        BoardUpdated?.Invoke(this, new BoardUpdate(found, changed, _placeReframe && changed));
        Render();
    }

    /// <summary>The trip suggestion changed: its card, and the board rows that start its trip.</summary>
    private void OnSuggestionUpdated(object? sender, EventArgs e)
    {
        if (IsActive)
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
            Notice noneSaved = new(AppStrings.Get("NoSavedStopsTitle"), AppStrings.Get("NoSavedStopsText"), AppStrings.Get("AddAStop"), _showSettings);
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
    private Notice SetUpNotice() =>
        new(AppStrings.Get("SuggestionSetUpTitle"), string.Empty, AppStrings.Get("SuggestionSetUpButton"), _showSettings);

    // Presenting.

    private static HashSet<string> SavedStopIdSet() => new(SettingsService.Stops.Select(s => s.StopId), StringComparer.Ordinal);

    /// <summary>Works out everything the board, its toolbar, and the card show, then tells the window to bring itself up to date.</summary>
    public void Render()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        HashSet<string> savedIds = SavedStopIdSet();
        Notice? empty = null;
        bool busy = false;
        string? error = _placeError ?? (_locationPermissionRequired ? null : _boardMonitor.LocationError);
        DateTimeOffset? fetchedAt = _placeFetchedAt;
        bool loading = _boardMonitor.IsLoading || _suggestionMonitor.IsLoading;
        Func<StopLine, bool> listed = BoardListing();
        BoardRow row = new(
            listed,
            NearYou: ShownPlace?.Center is NearYou,
            _suggestionMonitor.Discovery,
            fetchedAt is null ? string.Empty : AppStrings.Get("NoUpcomingArrivals"),
            _tracker.Current?.FindListed(PlaceArrivals),
            now);

        // A stop whose every line is listed at a stop saved with All routes has nothing left to show.
        Groups = _placeStops
            .Where(n => savedIds.Contains(n.Stop.Id) || n.Stop.Routes.Count == 0 || n.Stop.Routes.Any(r => listed(new StopLine(n.Stop.Id, r.RouteId, r.DirectionId))))
            .Select(n => BuildBoardGroup(n, savedIds.Contains(n.Stop.Id), row))
            .ToList();

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

        ShowsLocationHint = showLocationHint;
        LocationPermissionMessage = _locationPermissionError ?? AppStrings.Get("LocationPermissionMessage/Text");
        IsLoading = loading;
        LiveDotOpacity = error is null ? 1 : 0.35;
        UpdatedText = fetchedAt is { } at ? AppStrings.Format("UpdatedFormat", at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : loading ? AppStrings.Get("Loading")
            : string.Empty;
        bool refreshed = false;
        if (fetchedAt != _lastFetchedAt)
        {
            _lastFetchedAt = fetchedAt;
            refreshed = fetchedAt is not null && IsActive;
        }

        IsStatusOpen = error is not null;
        StatusMessage = error ?? string.Empty;
        StatusTitle = AppStrings.Get(fetchedAt is null ? "CantLoadArrivals" : "ShowingEarlierTimes");
        BoardCenter center = ShownCenter;
        PlaceChipText = CenterLabel(center);
        PlaceChipGlyph = CenterGlyph(center);
        PlaceChipName = AppStrings.Format("PlaceChipNameFormat", PlaceChipText);
        Suggestion = SuggestionItem(now);
        UpdateSummary(savedIds);

        bool emptyEntering = empty is { } next && (!ShowsEmpty || EmptyTitle != next.Title);
        _emptyAction = empty?.Action;
        ShowsEmpty = empty is not null;
        if (empty is { } e)
        {
            EmptyTitle = e.Title;
            EmptyText = e.Text;
            EmptyBusy = busy;
            EmptyButtonText = e.Button;
        }

        OnPropertyChanged(nameof(CanSaveDestination));
        OnPropertyChanged(nameof(IsSuggestionBusy));
        Rendered?.Invoke(this, EventArgs.Empty);
        if (refreshed)
        {
            TimesRefreshed?.Invoke(this, EventArgs.Empty);
        }

        if (emptyEntering)
        {
            EmptyEntering?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void InvokeEmptyAction() => _emptyAction?.Invoke();

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
        if (!places.Any(p => p.Kind == PlaceKind.Home) && _temporaryDestination is null && DestinationId is null)
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
        string? trip = Suggestion?.TooltipText;
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
        else if (ShownPlace?.EveryLineAt?.Contains(stop.Id) ?? false)
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
                PlaceArrivals.Where(a => a.StopId == stop.Id && Listed(a.RouteId, a.DirectionId)),
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
                    MapHelpers.ParseRouteColor(a.RouteColor ?? stop.Routes.FirstOrDefault(r => string.Equals(r.RouteId, a.RouteId, StringComparison.OrdinalIgnoreCase))?.RouteColor),
                    row.Now,
                    route.Later,
                    row.Tracked,
                    tooSoonToWalk: tooSoon,
                    // A row that starts the suggested trip says when it gets the rider there.
                    destinationName: row.Suggestion?.Leg?.To.Name,
                    destinationEta: tooSoon || a.Status == ArrivalStatus.Canceled ? null : PlaceBoard.ArrivalAtDestination(a, trips),
                    transfers: trips is not null && trips.TryGetValue(StopLine.Of(a), out TripTiming? timing) ? timing.Transfers : 0,
                    alerts: _monitor.Metadata.Alerts.For(a.RouteId, row.Now));
            })];

        return new StopGroupItem(stop.Id, title, string.Join(" · ", details), stop.Location, arrivals, row.EmptyText, stop, isSaved);
    }

    // Changes from elsewhere.

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        ++_savedRevision; // The board pins saved stops: it's found again, staying shown until then.
        if (_isShown)
        {
            Render(); // Update the save stars.
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.StopsChanged);
        }
    }

    private void OnTrackingChanged(object? sender, EventArgs e)
    {
        if (_isShown)
        {
            Render(); // Update the bells.
        }
    }

    private void OnPlacesChanged(object? sender, EventArgs e)
    {
        ++_savedRevision; // Near you falls back to near Home.
        if (_isShown)
        {
            _ = LoadPlaceAsync(reframe: false, TransitTrigger.PlacesChanged);
        }
    }

    /// <summary>The trip started, moved on, ended, or was resumed: the progress view and the board's card follow it.</summary>
    private void OnTripsChanged(object? sender, EventArgs e)
    {
        if (IsActive)
        {
            Render();
        }
    }

    // Actions on the board.

    /// <summary>Stops tracking the departure and says so.</summary>
    /// <returns>The caption to show.</returns>
    public string StopTracking(BusTimeItem item)
    {
        _tracker.Stop();
        return AppStrings.Format("TrackStoppedFormat", item.ClockText, LineLabel(item.Arrival));
    }

    /// <summary>Whether tracking a departure first needs the rider to say yes to ending the active trip (one thing is tracked at a time).</summary>
    public string? TrackInsteadQuestion => _trips.Current is { } trip ? TripMessages.TrackInstead(trip) : null;

    public void EndTrip() => _trips.End();

    /// <summary>Starts tracking <paramref name="item"/> (replacing any other).</summary>
    /// <returns>The caption to show.</returns>
    public string StartTracking(BusTimeItem item)
    {
        string stopName = item.StopName ?? Groups.FirstOrDefault(g => g.Arrivals.Any(a => a.Times.Contains(item)))?.Title ?? string.Empty;
        _tracker.Start(item.Arrival, stopName);
        int lead = SettingsService.HeadOutMinutes;
        string ride = AppStrings.Format("TrackRideFormat", item.ClockText, LineLabel(item.Arrival));
        return item.Arrival.MinutesAway(DateTimeOffset.Now) <= lead
            ? AppStrings.Format("TrackHeadOutFormat", ride, lead)
            : item.Arrival.IsRealTime
                ? AppStrings.Format("TrackStartedFormat", ride, lead)
                : AppStrings.Format("TrackScheduledFormat", ride, lead);
    }

    /// <summary>"Bus 14", or "Route 14" when the feed doesn't name the mode.</summary>
    internal static string LineLabel(string? mode, string displayId) =>
        mode is { Length: > 0 } ? $"{mode} {displayId}" : $"Route {displayId}";

    internal static string LineLabel(Arrival arrival) => LineLabel(arrival.Mode, arrival.RouteDisplayId);

    /// <summary>The star on a stop's header: saves the stop, or removes it if already saved, and says so.</summary>
    [RelayCommand]
    private void SaveStop(StopGroupItem? group)
    {
        if (group?.Saveable is null)
        {
            return;
        }

        StopSaveToggled?.Invoke(this, new StopSaveToggledEventArgs(group.StarKey, ToggleSavedStop(group)));
    }

    /// <summary>The height the rider gave the map, or null to use the default; kept across runs.</summary>
    public double? MapHeight
    {
        get => SettingsService.FlyoutMapHeight;
        set => SettingsService.FlyoutMapHeight = value;
    }

    /// <summary>Saves a listed stop (all routes), or removes it if already saved.</summary>
    /// <returns>The caption to show; empty when the group is not a stop that can be saved.</returns>
    public string ToggleSavedStop(StopGroupItem group)
    {
        if (group.Saveable is not { } stop)
        {
            return string.Empty;
        }

        IReadOnlyList<SavedStop> saved = SettingsService.Stops;
        if (group.IsSaved)
        {
            SettingsService.SetStops(saved.Where(s => s.StopId != stop.Id));
            return $"Removed {stop.Name} from favorites.";
        }

        if (SavedStop.DistinctStopCount(saved) >= SettingsService.MaxFavoriteStops)
        {
            return $"You can have up to {SettingsService.MaxFavoriteStops} favorite stops. Remove one in settings first.";
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
        return $"Added {stop.Name} to favorites.";
    }

    // The destination.

    public void ChooseDestination(string? placeId)
    {
        _temporaryDestination = null;
        DestinationId = placeId;
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.DestinationChanged);
    }

    public void ChooseSearchedDestination(DestinationResult result)
    {
        _temporaryDestination = result.AsPlace();
        DestinationId = _temporaryDestination.Id;
        CloseSearch();
        _ = LoadPlaceAsync(reframe: false, TransitTrigger.DestinationChanged);
    }

    /// <summary>Keeps the searched destination as a favorite place. Returns whether there was one to keep.</summary>
    public bool SaveTemporaryDestination()
    {
        if (_temporaryDestination is not { } place)
        {
            return false;
        }

        if (_suggestionMonitor.Discovery?.Leg is { } leg)
        {
            _monitor.Metadata.SaveDestinationPlan(leg.Origin, SuggestionContext.Target(place));
        }

        _temporaryDestination = null;
        SettingsService.SavePlace(place); // PlacesChanged refreshes both monitors and the destination menu.
        return true;
    }

    /// <summary>Where a destination search looks near: the board's center, else Home, else <paramref name="mapCenter"/>.</summary>
    public GeoPoint? SearchNear(GeoPoint? mapCenter) =>
        PlaceOrigin ?? SettingsService.Places.FirstOrDefault(p => p.Kind == PlaceKind.Home)?.Location ?? mapCenter;

    // Pages.

    public void OpenSearch()
    {
        CloseTrip(restoreBoard: true);
        Page = FlyoutPage.Search;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseSearch()
    {
        if (Page == FlyoutPage.Search)
        {
            Page = FlyoutPage.Board;
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Opens the trip using only the plan and departures already shown by the card; during a trip, its progress.</summary>
    /// <returns>Whether a page opened.</returns>
    public bool OpenTrip()
    {
        if (_trips.Current is not null)
        {
            OpenProgress();
            return true;
        }

        if (Suggestion is not { Trip: { } trip, Leg: { } leg } || _suggestionMonitor.Discovery?.Plan is not { } plan)
        {
            return false;
        }

        _tripLeg = leg;
        _tripSelection.Update(plan, _suggestionMonitor.Snapshot?.Arrivals ?? [], DateTimeOffset.Now, trip.Itinerary);
        Page = FlyoutPage.Trip;
        PageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Closes the trip's options page.</summary>
    /// <param name="restoreBoard">Whether to put the board and its map back (not when the trip is going on to its progress).</param>
    public void CloseTrip(bool restoreBoard, bool focusCard = false)
    {
        if (_tripLeg is null)
        {
            return;
        }

        _tripLeg = null;
        _timedTripMonitor.IsForeground = false;
        _timedTripContext = null;
        TripTime = default;
        _timedTripError = null;
        if (Page == FlyoutPage.Trip)
        {
            Page = FlyoutPage.Board;
        }

        PageChanged?.Invoke(this, EventArgs.Empty);
        if (restoreBoard)
        {
            BoardRestoreRequested?.Invoke(this, focusCard);
        }
    }

    /// <summary>The trip page's data at <paramref name="now"/>; null when no trip page is open (or it closed because the destination changed).</summary>
    public TripPresentation? UpdateTrip(DateTimeOffset now)
    {
        if (_tripLeg is not { } leg)
        {
            return null;
        }

        PlaceStops? found = _suggestionMonitor.Discovery;
        if (found?.Leg is { } latest && latest.To.Id != leg.To.Id)
        {
            CloseTrip(restoreBoard: true, focusCard: true);
            return null;
        }

        bool timed = TripTime.Mode != TripTimeMode.Now;
        IPlaceArrivalsMonitor monitor = timed ? _timedTripMonitor : _suggestionMonitor;
        IReadOnlyList<Arrival> arrivals = monitor.Snapshot?.Arrivals ?? [];
        PlaceStops? shown = monitor.Discovery;
        if (shown is { Leg: { } current, Plan: { } plan })
        {
            _tripLeg = leg = current;
            _tripSelection.Update(plan, arrivals, now, time: TripTime);
        }
        else if (timed)
        {
            _tripSelection.Update(new TripPlan([], []), [], now, time: TripTime);
        }

        return new TripPresentation(leg, _tripSelection, arrivals, _tracker.Current, _monitor.Metadata.Alerts,
            timed ? _timedTripMonitor.IsLoading && shown?.Plan is null
                : _isReplanning || (_suggestionMonitor.IsLoading && shown?.Plan is null),
            timed ? _timedTripError : _suggestionError, TripTime);
    }

    public void ChooseTripOption(TripItinerary itinerary) => _tripSelection.Select(itinerary);

    /// <summary>
    /// Go: follows the selected option at the chosen time, from the departures it was timed with (no request),
    /// then shows its progress. A trip over 99 minutes away is scheduled.
    /// </summary>
    /// <param name="view">Where the map is looking, for the trip to come back to.</param>
    /// <returns>What to say about the trip starting; null when nothing was started.</returns>
    public string? StartTrip(MapView? view)
    {
        if (_tripLeg is not { } leg || _tripSelection.Selected is not { } trip)
        {
            return null;
        }

        IPlaceArrivalsMonitor monitor = TripTime.Mode == TripTimeMode.Now ? _suggestionMonitor : _timedTripMonitor;
        string started = TripMessages.Started(trip, leg, DateTimeOffset.Now);
        _trips.Start(trip, leg, SuggestionContext.Target(leg.To), TripTime, monitor.Snapshot);
        _trips.MapView = view;
        CloseTrip(restoreBoard: false);
        OpenProgress();
        return started;
    }

    /// <summary>The rider chose when to travel: now, or a time to leave at or arrive by.</summary>
    public void SetTripTime(TripTime time)
    {
        if (_tripLeg is not { } leg)
        {
            return;
        }

        _timedTripMonitor.IsForeground = false;
        TripTime = time;
        _timedTripError = null;
        _timedTripContext = time.Mode == TripTimeMode.Now ? null : new TimedTripContext(leg, SuggestionContext.Target(leg.To), time);
        if (_timedTripContext is { } context)
        {
            _timedTripMonitor.Configure(context);
            _timedTripMonitor.IsForeground = true;
            _ = RefreshTimedTripAsync(TransitTrigger.TripTimeChanged);
        }
    }

    private async Task RefreshTimedTripAsync(TransitTrigger trigger, bool force = false)
    {
        if (!_timedTripMonitor.IsForeground)
        {
            return;
        }

        int version = _timedTripMonitor.Version;
        string? error = await RefreshAsync(_timedTripMonitor, TransitConsumer.Trip, trigger, force);
        if (version == _timedTripMonitor.Version && IsActive)
        {
            _timedTripError = error;
            Render();
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

    /// <summary>Shows the active trip's progress (or its Resume card) over the board. It reads the trip from <see cref="ActiveTripService"/> and requests nothing.</summary>
    public void OpenProgress()
    {
        Page = FlyoutPage.Progress;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the board, where the card shows the trip (the trip goes on); also when the trip ends.</summary>
    public void CloseProgress(bool focusCard = false)
    {
        if (Page != FlyoutPage.Progress)
        {
            return;
        }

        ProgressClosing?.Invoke(this, EventArgs.Empty);
        Page = FlyoutPage.Board;
        PageChanged?.Invoke(this, EventArgs.Empty);
        BoardRestoreRequested?.Invoke(this, focusCard);
    }

    [RelayCommand]
    private void OpenSettings() => _showSettings();

    private sealed record TimedTripContext(CommuteLeg Leg, TripDestination Destination, TripTime Time);

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
