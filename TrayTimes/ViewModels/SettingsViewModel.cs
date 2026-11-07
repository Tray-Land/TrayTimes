using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;
using Windows.ApplicationModel;

namespace TrayTimes.ViewModels;

/// <summary>
/// Manages favorite places, favorite stops (a stop search, or route → direction → stop), and app settings.
/// Nothing is requested until the user asks: searching, loading the area's routes, and picking a route each
/// cost one call (two for the area's routes), and are cached. The picker map is the view's; it supplies the area
/// through <see cref="MapCenterProvider"/> and draws what <see cref="StopChoicesChanged"/> and
/// <see cref="RoutePatternsChanged"/> announce.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ArrivalsMonitor _monitor;
    private readonly TransitBudget _budget;
    private readonly ITransitService _service;
    private readonly Action<Action> _post;
    private readonly Action _applyKeepAwake;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _savingOrder;
    private bool _loading = true;
    private int _pickerVersion;
    private CancellationTokenSource? _pickerCancellation;
    private RouteDetails? _routeDetails;

    /// <param name="post">Runs an action on the UI thread; usage changes can arrive from any thread.</param>
    /// <param name="applyKeepAwake">Puts the saved keep-awake setting into effect.</param>
    public SettingsViewModel(ArrivalsMonitor monitor, TransitBudget budget, Action<Action> post, Action applyKeepAwake)
    {
        _applyKeepAwake = applyKeepAwake;
        _monitor = monitor;
        _budget = budget;
        _service = monitor.Service;
        _post = post;

        IReadOnlyList<FavoritePlace> places = SettingsService.Places;
        HomePlace = places.FirstOrDefault(p => p.Kind == PlaceKind.Home)?.Place;
        WorkPlace = places.FirstOrDefault(p => p.Kind == PlaceKind.Work)?.Place;
        SchoolPlace = places.FirstOrDefault(p => p.Kind == PlaceKind.School)?.Place;

        foreach (FavoritePlace place in places.Where(p => p.Kind == PlaceKind.Custom))
        {
            CustomPlaces.Add(new CustomPlaceViewModel(place, OnCustomPlaceRemoved));
        }

        ReloadSavedStops();
        IsSavedStopsExpanded = SavedStops.Count > 0;
        IsAddStopExpanded = SavedStops.Count == 0;
        SettingsService.StopsChanged += OnStopsChanged;

        TransitBudgetPolicy policy = _budget.Policy;
        CallsPerMinute = policy.CallsPerMinute;
        CallsPerMonth = policy.CallsPerMonth;
        ApiUsageService.Changed += OnApiUsageChanged;
        UpdateApiUsage();

        HeadOutMinutes = SettingsService.HeadOutMinutes;
        KeepAwakeWhileTracking = SettingsService.KeepAwakeWhileTracking;

        IsServiceConfigured = _service.IsConfigured;
        IsKeyMissing = !IsServiceConfigured;
        RoutePlaceholder = AppStrings.Get(IsServiceConfigured ? "SettingsLoadRoutesFirst" : "SettingsApiKeyNeeded");
        IsLoadRoutesEnabled = IsServiceConfigured;
        _loading = false;
    }

    /// <summary>Where the picker map is looking; searches and route lists are for this area. Set by the view.</summary>
    public Func<GeoPoint> MapCenterProvider { get; set; } = () => new GeoPoint(20, 0);

    /// <summary>The stops to show as map dots (in the route's color when there is one); none clears them.</summary>
    public event Action<TransitRoute?, IReadOnlyList<TransitStop>>? StopChoicesChanged;

    /// <summary>The patterns of the chosen route and direction to draw on the map.</summary>
    public event Action<TransitRoute, IReadOnlyList<RoutePattern>>? RoutePatternsChanged;

    /// <summary>The map should move to this point (a postal code was found).</summary>
    public event Action<GeoPoint>? AreaFound;

    /// <summary>A custom place was added; the view focuses its name box.</summary>
    public event Action<CustomPlaceViewModel>? CustomPlaceAdded;

    /// <summary>A custom place was removed; the view moves focus back to the add button.</summary>
    public event Action? CustomPlaceRemoved;

    // Places

    [ObservableProperty]
    public partial Place? HomePlace { get; set; }

    [ObservableProperty]
    public partial Place? WorkPlace { get; set; }

    [ObservableProperty]
    public partial Place? SchoolPlace { get; set; }

    partial void OnHomePlaceChanged(Place? value) => SaveSlot(PlaceKind.Home, value);

    partial void OnWorkPlaceChanged(Place? value) => SaveSlot(PlaceKind.Work, value);

    partial void OnSchoolPlaceChanged(Place? value) => SaveSlot(PlaceKind.School, value);

    private void SaveSlot(PlaceKind kind, Place? place)
    {
        if (_loading)
        {
            return;
        }

        if (place is not null)
        {
            SettingsService.SavePlace(FavoritePlace.ForKind(kind, place));
        }
        else
        {
            SettingsService.RemovePlace(kind.ToString());
        }
    }

    public ObservableCollection<CustomPlaceViewModel> CustomPlaces { get; } = [];

    [RelayCommand]
    private void AddCustomPlace()
    {
        CustomPlaceViewModel place = new(null, OnCustomPlaceRemoved);
        CustomPlaces.Add(place);
        CustomPlaceAdded?.Invoke(place);
    }

    private void OnCustomPlaceRemoved(CustomPlaceViewModel place)
    {
        CustomPlaces.Remove(place);
        CustomPlaceRemoved?.Invoke();
    }

    // Saved stops

    public ObservableCollection<SavedStopRow> SavedStops { get; } = [];

    public bool HasNoSavedStops => SavedStops.Count == 0;

    [ObservableProperty]
    public partial bool IsSavedStopsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsAddStopExpanded { get; set; }

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        if (!_savingOrder)
        {
            ReloadSavedStops();
        }
    }

    private void ReloadSavedStops()
    {
        SavedStops.Clear();
        foreach (SavedStop stop in SettingsService.Stops)
        {
            SavedStops.Add(new SavedStopRow(stop, RemoveStop, SetNickname));
        }

        OnPropertyChanged(nameof(HasNoSavedStops));
    }

    /// <summary>Saves the order the user dragged the rows into.</summary>
    [RelayCommand]
    private void SaveStopOrder()
    {
        _savingOrder = true;
        SettingsService.SetStops(SavedStops.Select(r => r.Stop));
        _savingOrder = false;
    }

    private void RemoveStop(SavedStopRow row) => SettingsService.RemoveStop(row.Stop);

    /// <summary>Saves the nickname for the row's stop. Sibling rows for the same stop show it too.</summary>
    private void SetNickname(SavedStopRow row, string? nickname)
    {
        string stopId = row.Stop.StopId;
        _savingOrder = true;
        SettingsService.SetStopNickname(stopId, nickname);
        _savingOrder = false;

        // Renamed in place, so a later reorder doesn't save the old nicknames back and no row is rebuilt.
        foreach (SavedStopRow sibling in SavedStops.Where(r => r.Stop.StopId == stopId))
        {
            sibling.Rename(nickname);
        }
    }

    // Alerts and app settings

    public int MinHeadOutMinutes => SettingsService.MinHeadOutMinutes;

    public int MaxHeadOutMinutes => SettingsService.MaxHeadOutMinutes;

    [ObservableProperty]
    public partial double HeadOutMinutes { get; set; }

    partial void OnHeadOutMinutesChanged(double value)
    {
        if (_loading)
        {
            return;
        }

        if (double.IsNaN(value))
        {
            HeadOutMinutes = SettingsService.HeadOutMinutes; // Cleared or invalid entry: put the saved value back.
            return;
        }

        SettingsService.HeadOutMinutes = (int)Math.Round(value);
    }

    [ObservableProperty]
    public partial bool KeepAwakeWhileTracking { get; set; }

    partial void OnKeepAwakeWhileTrackingChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        SettingsService.KeepAwakeWhileTracking = value;
        _applyKeepAwake();
    }

    [ObservableProperty]
    public partial bool RunAtStartup { get; set; }

    [ObservableProperty]
    public partial bool IsStartupToggleEnabled { get; set; }

    [ObservableProperty]
    public partial string StartupStatus { get; set; } = string.Empty;

    public bool HasStartupStatus => StartupStatus.Length > 0;

    partial void OnStartupStatusChanged(string value) => OnPropertyChanged(nameof(HasStartupStatus));

    /// <summary>Reads whether the app starts with Windows.</summary>
    public async Task LoadStartupStateAsync() => ApplyStartupState(await StartupService.GetStateAsync());

    partial void OnRunAtStartupChanged(bool value)
    {
        if (!_applyingStartup)
        {
            _ = SetStartupAsync(value);
        }
    }

    private bool _applyingStartup;

    private async Task SetStartupAsync(bool enabled) => ApplyStartupState(await StartupService.SetEnabledAsync(enabled));

    private void ApplyStartupState(StartupTaskState? state)
    {
        _applyingStartup = true;
        RunAtStartup = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        IsStartupToggleEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        _applyingStartup = false;

        StartupStatus = state switch
        {
            null => AppStrings.Get("SettingsStartupPackageRequired"),
            StartupTaskState.DisabledByUser => AppStrings.Get("SettingsStartupDisabledByUser"),
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => AppStrings.Get("SettingsStartupManaged"),
            _ => string.Empty,
        };
    }

    // API usage

    public ObservableCollection<ApiUsageRow> ApiUsage { get; } = [];

    [ObservableProperty]
    public partial string BudgetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EstimateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double CallsPerMinute { get; set; }

    [ObservableProperty]
    public partial double CallsPerMonth { get; set; }

    partial void OnCallsPerMinuteChanged(double value) => ApplyBudget(value, isMinute: true);

    partial void OnCallsPerMonthChanged(double value) => ApplyBudget(value, isMinute: false);

    private void ApplyBudget(double value, bool isMinute)
    {
        if (_loading)
        {
            return;
        }

        TransitBudgetPolicy saved = _budget.Policy;
        if (double.IsNaN(value) || value < 1)
        {
            // Cleared or invalid entry: put the saved value back.
            if (isMinute)
            {
                CallsPerMinute = saved.CallsPerMinute;
            }
            else
            {
                CallsPerMonth = saved.CallsPerMonth;
            }

            return;
        }

        TransitBudgetPolicy policy = saved with
        {
            CallsPerMinute = (int)Math.Round(CallsPerMinute),
            CallsPerMonth = (int)Math.Round(CallsPerMonth),
        };
        SettingsService.TransitBudget = policy;
        _budget.Policy = SettingsService.TransitBudget; // The saved, range-checked values.
        UpdateApiUsage();
    }

    private void OnApiUsageChanged(object? sender, EventArgs e) => _post(UpdateApiUsage);

    private void UpdateApiUsage()
    {
        ApiCallSummary summary = ApiUsageService.GetSummary();
        ApiUsage.Clear();
        ApiUsage.Add(ToRow(AppStrings.Get("SettingsAllRequests"), summary.Total));
        foreach (KeyValuePair<string, ApiCallCounts> endpoint in summary.ByEndpoint)
        {
            ApiUsage.Add(ToRow(endpoint.Key, endpoint.Value));
        }

        BudgetText = AppStrings.Format(
            "SettingsBudgetFormat",
            summary.Total.ThisMonth,
            _budget.Policy.CallsPerMonth,
            summary.Total.Today,
            _budget.DailyAllowance,
            AppStrings.Get($"SettingsBudgetLevel{_budget.Level}"));

        EstimateText = AppStrings.Format(
            "SettingsEstimateFormat",
            ApiCallLog.EstimateMonth(summary.Total, DateOnly.FromDateTime(DateTime.Now)),
            _budget.Policy.CallsPerMonth);

        static ApiUsageRow ToRow(string name, ApiCallCounts counts) =>
            new(name, counts.Today.ToString("N0"), counts.Last7Days.ToString("N0"), counts.ThisMonth.ToString("N0"));
    }

    // Finding an area

    public bool IsServiceConfigured { get; }

    public bool IsKeyMissing { get; }

    [ObservableProperty]
    public partial string AreaSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AreaStatus { get; set; } = string.Empty;

    public bool HasAreaStatus => AreaStatus.Length > 0;

    partial void OnAreaStatusChanged(string value) => OnPropertyChanged(nameof(HasAreaStatus));

    /// <summary>Tells the rider to enter a postal code (the map has nowhere to open).</summary>
    public void PromptForArea() => AreaStatus = AppStrings.Get("SettingsAreaPrompt");

    /// <summary>Moves the map to a postal code and remembers it as the area the map starts in when the rider's location isn't available.</summary>
    [RelayCommand]
    private async Task FindAreaAsync()
    {
        string code = AreaSearchText.Trim();
        if (code.Length == 0)
        {
            return;
        }

        try
        {
            if (await GeocodingService.FindPostalCodeAsync(code, _lifetime.Token) is not { } point)
            {
                AreaStatus = AppStrings.Get("SettingsAreaNotFound");
                return;
            }

            SettingsService.DefaultArea = new MapView(point.Lat, point.Lon, 13);
            AreaStatus = AppStrings.Get("SettingsAreaSaved");
            AreaFound?.Invoke(point);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window closed.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AreaStatus = AppStrings.Get("SettingsAreaFailed");
        }
    }

    // Choosing a stop

    [ObservableProperty]
    public partial string StopSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPickerLoading { get; set; }

    [ObservableProperty]
    public partial string AddStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<TransitRoute>? Routes { get; set; }

    [ObservableProperty]
    public partial TransitRoute? SelectedRoute { get; set; }

    [ObservableProperty]
    public partial string RoutePlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRouteEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsLoadRoutesEnabled { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TransitDirection>? Directions { get; set; }

    [ObservableProperty]
    public partial TransitDirection? SelectedDirection { get; set; }

    [ObservableProperty]
    public partial string DirectionPlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDirectionEnabled { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TransitStop>? Stops { get; set; }

    [ObservableProperty]
    public partial TransitStop? SelectedStop { get; set; }

    [ObservableProperty]
    public partial string StopPlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStopEnabled { get; set; }

    [ObservableProperty]
    public partial bool AllRoutes { get; set; }

    [ObservableProperty]
    public partial bool IsAllRoutesEnabled { get; set; }

    public bool CanAddStop => SelectedStop is not null;

    partial void OnSelectedStopChanged(TransitStop? value)
    {
        AddStatus = string.Empty;
        OnPropertyChanged(nameof(CanAddStop));
        AddStopCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRouteChanged(TransitRoute? value)
    {
        if (value is not null)
        {
            _ = LoadRouteDetailsAsync(value);
        }
    }

    /// <summary>The route's details are already loaded, so picking a direction costs nothing.</summary>
    partial void OnSelectedDirectionChanged(TransitDirection? value)
    {
        if (SelectedRoute is not { } route
            || value is null
            || _routeDetails?.Itineraries.FirstOrDefault(i => i.Direction == value) is not { } itinerary)
        {
            return;
        }

        ++_pickerVersion;
        AddStatus = string.Empty;
        ShowStopChoices(route, itinerary.Stops, AppStrings.Get(itinerary.Stops.Count == 0 ? "SettingsNoStops" : "SettingsChooseStop"));
        RoutePatternsChanged?.Invoke(route, _monitor.Metadata.GetCachedPatterns(route.Id, value.Id));
    }

    /// <summary>Cancels the previous picker selection's requests and returns the token for the new one.</summary>
    private CancellationToken BeginPickerRequest()
    {
        _pickerCancellation?.Cancel();
        _pickerCancellation?.Dispose();
        _pickerCancellation = new CancellationTokenSource();
        return _pickerCancellation.Token;
    }

    /// <summary>Lists the stops near the map whose name or code matches the search. Runs only on Enter or the button, never per keystroke.</summary>
    [RelayCommand]
    private async Task SearchStopsAsync()
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        string query = StopSearchText.Trim();
        if (!_service.IsConfigured || query.Length == 0)
        {
            return;
        }

        int version = ++_pickerVersion;
        CancellationToken cancellationToken = BeginPickerRequest();
        SelectedRoute = null;
        ResetDirectionAndStops();
        _routeDetails = null;
        StopPlaceholder = AppStrings.Get("SettingsPickerLoading");
        IsPickerLoading = true;
        try
        {
            IReadOnlyList<TransitStop> stops = await _service.SearchStopsAsync(MapCenterProvider(), query, cancellationToken);
            if (version != _pickerVersion)
            {
                return;
            }

            // A searched stop has no route picked, so it follows every route there.
            AllRoutes = true;
            IsAllRoutesEnabled = false;
            ShowStopChoices(null, stops, AppStrings.Get(stops.Count == 0 ? "SettingsNoMatchingStops" : "SettingsChooseStop"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection (or closing the window) replaced this one.
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            if (version == _pickerVersion)
            {
                StopPlaceholder = string.Empty;
                AddStatus = Describe(ex);
            }
        }
        finally
        {
            if (version == _pickerVersion)
            {
                IsPickerLoading = false;
            }
        }
    }

    /// <summary>Loads the routes of the transit networks serving the map's area (two calls, kept for a week).</summary>
    [RelayCommand]
    private async Task LoadRoutesAsync()
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        IsLoadRoutesEnabled = false;
        RoutePlaceholder = AppStrings.Get("SettingsPickerLoading");
        try
        {
            IReadOnlyList<TransitRoute> routes = await _service.GetRoutesNearAsync(MapCenterProvider(), _lifetime.Token);
            Routes = routes.OrderBy(r => r, RouteOrder.Instance).ToList();
            RoutePlaceholder = AppStrings.Get(routes.Count == 0 ? "SettingsNoRoutesHere" : "SettingsChooseRoute");
            IsRouteEnabled = routes.Count > 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            RoutePlaceholder = AppStrings.Get("SettingsRoutesUnavailable");
            AddStatus = Describe(ex);
        }

        IsLoadRoutesEnabled = true;
    }

    private async Task LoadRouteDetailsAsync(TransitRoute route)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        int version = ++_pickerVersion;
        CancellationToken cancellationToken = BeginPickerRequest();
        ResetDirectionAndStops();
        IsAllRoutesEnabled = true;
        AllRoutes = false;
        DirectionPlaceholder = AppStrings.Get("SettingsPickerLoading");
        IsPickerLoading = true;

        try
        {
            RouteDetails details = await _service.GetRouteDetailsAsync(route.Id, cancellationToken);
            if (version != _pickerVersion)
            {
                return;
            }

            _routeDetails = details;
            TransitDirection[] directions = [.. details.Itineraries.Select(i => i.Direction)];
            Directions = directions;
            DirectionPlaceholder = AppStrings.Get(directions.Length == 0 ? "SettingsNoDirections" : "SettingsChooseDirection");
            IsDirectionEnabled = directions.Length > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection (or closing the window) replaced this one.
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            if (version == _pickerVersion)
            {
                DirectionPlaceholder = string.Empty;
                AddStatus = Describe(ex);
            }
        }
        finally
        {
            if (version == _pickerVersion)
            {
                IsPickerLoading = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddStop))]
    private void AddStop()
    {
        if (SelectedStop is not { } stop)
        {
            return;
        }

        TransitRoute? route = SelectedRoute;
        TransitDirection? direction = SelectedDirection;
        IReadOnlyList<SavedStop> existing = SettingsService.Stops;
        if (existing.All(s => s.StopId != stop.Id) && SavedStop.DistinctStopCount(existing) >= SettingsService.MaxFavoriteStops)
        {
            AddStatus = AppStrings.Format("SettingsStopLimitFormat", SettingsService.MaxFavoriteStops);
            return;
        }

        SettingsService.AddStop(new SavedStop(
            stop.Id,
            stop.Name,
            stop.Location.Lat,
            stop.Location.Lon,
            route?.Id ?? string.Empty,
            route?.DisplayId ?? string.Empty,
            route?.Color,
            direction?.Id ?? string.Empty,
            direction?.Name ?? string.Empty,
            route is null || direction is null || AllRoutes));
        AddStatus = AppStrings.Format("SettingsStopAddedFormat", stop.Name);
    }

    private void ResetDirectionAndStops()
    {
        IsPickerLoading = false;
        Directions = null;
        IsDirectionEnabled = false;
        Stops = null;
        IsStopEnabled = false;
        StopPlaceholder = string.Empty;
        AddStatus = string.Empty;
        StopChoicesChanged?.Invoke(null, []);
    }

    /// <summary>Lists stops to choose from, in the box and as dots on the map.</summary>
    private void ShowStopChoices(TransitRoute? route, IReadOnlyList<TransitStop> stops, string placeholder)
    {
        Stops = stops;
        StopPlaceholder = placeholder;
        IsStopEnabled = stops.Count > 0;
        IsPickerLoading = false;
        StopChoicesChanged?.Invoke(route, stops);
    }

    private static bool IsTransitFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or TransitApiException;

    private string Describe(Exception ex) => ex is TransitApiException api
        ? api.Message
        : ConnectivityService.IsInternetAvailable
            ? AppStrings.Format("SettingsTransitUnavailableFormat", _service.ProviderName)
            : AppStrings.Get("SettingsOffline");

    /// <summary>Stops listening and cancels what is in flight; the window closed.</summary>
    public void Dispose()
    {
        ++_pickerVersion;
        _pickerCancellation?.Cancel();
        _pickerCancellation?.Dispose();
        _pickerCancellation = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        SettingsService.StopsChanged -= OnStopsChanged;
        ApiUsageService.Changed -= OnApiUsageChanged;
    }

    /// <summary>Numbered routes in numeric order ("12" before "30X"), then lettered routes alphabetically.</summary>
    internal sealed class RouteOrder : IComparer<TransitRoute>
    {
        public static RouteOrder Instance { get; } = new();

        public int Compare(TransitRoute? x, TransitRoute? y)
        {
            (bool xNum, int xVal) = LeadingNumber(x?.DisplayId);
            (bool yNum, int yVal) = LeadingNumber(y?.DisplayId);
            if (xNum != yNum)
            {
                return xNum ? -1 : 1;
            }

            int byNumber = xVal.CompareTo(yVal);
            return byNumber != 0 ? byNumber : string.Compare(x?.DisplayId, y?.DisplayId, StringComparison.OrdinalIgnoreCase);
        }

        private static (bool, int) LeadingNumber(string? s)
        {
            int length = 0;
            while (s is not null && length < s.Length && char.IsAsciiDigit(s[length]))
            {
                length++;
            }

            return length > 0 && int.TryParse(s.AsSpan(0, length), out int n) ? (true, n) : (false, 0);
        }
    }
}
