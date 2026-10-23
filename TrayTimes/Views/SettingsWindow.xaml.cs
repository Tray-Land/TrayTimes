using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.ApplicationModel;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using WinUIEx;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TrayTimes.Views;

/// <summary>
/// Manages favorite places, favorite stops (a stop search, or route → direction → stop, with a
/// map), and app settings. Nothing is requested until the user asks: searching, loading the
/// area's routes, and picking a route each cost one call (two for the area's routes), and are
/// cached.
/// </summary>
public sealed partial class SettingsWindow : WindowEx
{
    private static readonly TimeSpan InitialLocationTimeout = TimeSpan.FromSeconds(3);
    private static readonly GeoPoint WorldCenter = new(20, 0);
    private const double WorldZoom = 2;

    private readonly ResourceLoader _resources = new();
    private readonly ArrivalsMonitor _monitor;
    private readonly TransitBudget _budget;
    private readonly ITransitService _service;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly ObservableCollection<SavedStop> _savedStops = [];
    private readonly Dictionary<PlacePicker, PlaceKind> _fixedPickers;
    private readonly MapElementsLayer _routeLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly Dictionary<MapIcon, TransitStop> _stopPins = [];
    private readonly FontIcon _selectedGlyph;
    private FontIcon? _stopGlyph;
    private MapIcon? _selectedPin;
    private RouteDetails? _routeDetails;
    private bool _loading = true;
    private bool _savingOrder;
    private int _pickerVersion;
    private CancellationTokenSource? _pickerCancellation;

    internal SettingsWindow(ArrivalsMonitor monitor, TransitBudget budget)
    {
        InitializeComponent();
        Title = _resources.GetString("SettingsWindowTitle");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        _monitor = monitor;
        _budget = budget;
        _service = monitor.Service;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowPlacementService.CenterOnPrimary(this, 720, 800);

        SavedStopsList.ItemsSource = _savedStops;
        ReloadSavedStops();
        SavedStopsExpander.IsExpanded = _savedStops.Count > 0;
        AddStopExpander.IsExpanded = _savedStops.Count == 0;
        SettingsService.StopsChanged += OnStopsChanged;
        Closed += (_, _) => SettingsService.StopsChanged -= OnStopsChanged;

        _fixedPickers = new() { [HomePicker] = PlaceKind.Home, [WorkPicker] = PlaceKind.Work, [SchoolPicker] = PlaceKind.School };
        LoadPlaces();

        Closed += (_, _) =>
        {
            ++_pickerVersion;
            _pickerCancellation?.Cancel();
            _pickerCancellation?.Dispose();
            _pickerCancellation = null;
            _windowCancellation.Cancel();
            _windowCancellation.Dispose();
        };

        ApiUsageService.Changed += OnApiUsageChanged;
        Closed += (_, _) => ApiUsageService.Changed -= OnApiUsageChanged;
        TransitBudgetPolicy policy = _budget.Policy;
        CallsPerMinuteBox.Value = policy.CallsPerMinute;
        CallsPerMonthBox.Value = policy.CallsPerMonth;
        UpdateApiUsage();
        _selectedGlyph = new FontIcon { Glyph = MapHelpers.StopGlyph, FontSize = 20 };
        MapHelpers.ApplyStopColor(_selectedGlyph, Root);
        PickerMap.Layers.Add(_routeLayer);
        PickerMap.Layers.Add(_stopLayer);
        _stopLayer.Tapped += StopLayer_Tapped;
        Root.ActualThemeChanged += (_, _) =>
        {
            MapHelpers.ApplyBasemap(PickerMap, Root, null);
            MapHelpers.ApplyStopColor(_selectedGlyph, Root);
        };
        MapHelpers.ApplyBasemap(PickerMap, Root, null);
        _ = FrameInitialViewAsync();

        HeadOutBox.Minimum = SettingsService.MinHeadOutMinutes;
        HeadOutBox.Maximum = SettingsService.MaxHeadOutMinutes;
        HeadOutBox.Value = SettingsService.HeadOutMinutes;
        KeepAwakeToggle.IsOn = SettingsService.KeepAwakeWhileTracking;

        KeyBar.IsOpen = !_service.IsConfigured;
        StopSearchButton.IsEnabled = _service.IsConfigured;
        LoadRoutesButton.IsEnabled = _service.IsConfigured;
        RouteBox.PlaceholderText = _resources.GetString(_service.IsConfigured ? "SettingsLoadRoutesFirst" : "SettingsApiKeyNeeded");
        _ = LoadStartupStateAsync();
        _loading = false;
    }

    /// <summary>Where the picker map is looking; searches and route lists are for this area.</summary>
    private GeoPoint MapCenter => PickerMap.Center is { } center
        ? new GeoPoint(center.Position.Latitude, center.Position.Longitude)
        : WorldCenter;

    private void LoadPlaces()
    {
        IReadOnlyList<FavoritePlace> places = SettingsService.Places;
        foreach ((PlacePicker picker, PlaceKind kind) in _fixedPickers)
        {
            picker.Place = places.FirstOrDefault(p => p.Kind == kind)?.Place;
        }

        foreach (FavoritePlace place in places.Where(p => p.Kind == PlaceKind.Custom))
        {
            AddCustomPlaceRow(place);
        }
    }

    private void FixedPlacePicker_PlaceChosen(object? sender, EventArgs e)
    {
        if (sender is not PlacePicker picker || !_fixedPickers.TryGetValue(picker, out PlaceKind kind))
        {
            return;
        }

        if (picker.Place is { } place)
        {
            SettingsService.SavePlace(FavoritePlace.ForKind(kind, place));
        }
        else
        {
            SettingsService.RemovePlace(kind.ToString());
        }
    }

    private void AddPlaceButton_Click(object sender, RoutedEventArgs e) => AddCustomPlaceRow(null).Focus(FocusState.Programmatic);

    /// <summary>
    /// Adds a name box and address picker for a custom place. The place is saved once it has an
    /// address, and kept up to date as its name or address changes. Returns the name box.
    /// </summary>
    private TextBox AddCustomPlaceRow(FavoritePlace? existing)
    {
        string id = existing?.Id ?? Guid.NewGuid().ToString("N");
        TextBox nameBox = new()
        {
            Header = _resources.GetString("SettingsCustomPlaceName"),
            PlaceholderText = _resources.GetString("SettingsCustomPlacePlaceholder"),
            Text = existing?.Name ?? string.Empty,
        };
        Button removeButton = new()
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = new FontIcon { FontSize = 14, Glyph = "" }, // Delete
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
        };
        PlacePicker picker = new() { Place = existing?.Place };

        Grid nameRow = new() { ColumnSpacing = 8 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition());
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(removeButton, 1);
        nameRow.Children.Add(nameBox);
        nameRow.Children.Add(removeButton);
        StackPanel row = new() { Spacing = 8, Children = { nameRow, picker } };

        AutomationProperties.SetName(nameBox, _resources.GetString("SettingsCustomPlaceName"));
        AutomationProperties.SetAutomationId(nameBox, $"PlaceName_{id}");
        AutomationProperties.SetAutomationId(removeButton, $"RemovePlace_{id}");

        string PlaceName() => string.IsNullOrWhiteSpace(nameBox.Text) ? _resources.GetString("SettingsCustomPlaceDefaultName") : nameBox.Text.Trim();

        void Label()
        {
            string removeLabel = string.Format(CultureInfo.CurrentCulture, _resources.GetString("SettingsRemovePlaceFormat"), PlaceName());
            AutomationProperties.SetName(removeButton, removeLabel);
            ToolTipService.SetToolTip(removeButton, removeLabel);
            picker.AddressAutomationName = string.Format(CultureInfo.CurrentCulture, _resources.GetString("SettingsPlaceAddressFormat"), PlaceName());
        }

        void Save()
        {
            if (picker.Place is { } place)
            {
                SettingsService.SavePlace(new FavoritePlace(id, PlaceKind.Custom, PlaceName(), place,
                    StopId: place == existing?.Place ? existing?.StopId : null));
            }
            else
            {
                SettingsService.RemovePlace(id);
            }
        }

        nameBox.TextChanged += (_, _) =>
        {
            Label();
            if (picker.Place is not null)
            {
                Save();
            }
        };
        picker.PlaceChosen += (_, _) => Save();
        removeButton.Click += (_, _) =>
        {
            SettingsService.RemovePlace(id);
            CustomPlacesPanel.Children.Remove(row);
            AddPlaceButton.Focus(FocusState.Programmatic);
        };

        Label();
        CustomPlacesPanel.Children.Add(row);
        return nameBox;
    }

    private void HeadOutBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        if (double.IsNaN(args.NewValue))
        {
            sender.Value = SettingsService.HeadOutMinutes; // Cleared or invalid entry: put the saved value back.
            return;
        }

        SettingsService.HeadOutMinutes = (int)Math.Round(args.NewValue);
    }

    private void KeepAwakeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsService.KeepAwakeWhileTracking = KeepAwakeToggle.IsOn;
        App.Current.ApplyKeepAwakeSetting();
    }

    private void BudgetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        TransitBudgetPolicy saved = _budget.Policy;
        if (double.IsNaN(args.NewValue) || args.NewValue < 1)
        {
            // Cleared or invalid entry: put the saved value back.
            sender.Value = sender == CallsPerMinuteBox ? saved.CallsPerMinute : saved.CallsPerMonth;
            return;
        }

        TransitBudgetPolicy policy = saved with
        {
            CallsPerMinute = (int)Math.Round(CallsPerMinuteBox.Value),
            CallsPerMonth = (int)Math.Round(CallsPerMonthBox.Value),
        };
        SettingsService.TransitBudget = policy;
        _budget.Policy = SettingsService.TransitBudget; // The saved, range-checked values.
        UpdateApiUsage();
    }

    private void OnApiUsageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateApiUsage);

    private void UpdateApiUsage()
    {
        ApiCallSummary summary = ApiUsageService.GetSummary();
        ApiUsageList.ItemsSource = new[] { ToRow(_resources.GetString("SettingsAllRequests"), summary.Total) }
            .Concat(summary.ByEndpoint.Select(e => ToRow(e.Key, e.Value)))
            .ToList();

        TransitBudgetLevel level = _budget.Level;
        BudgetText.Text = string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("SettingsBudgetFormat"),
            summary.Total.ThisMonth,
            _budget.Policy.CallsPerMonth,
            summary.Total.Today,
            _budget.DailyAllowance,
            _resources.GetString($"SettingsBudgetLevel{level}"));

        EstimateText.Text = string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("SettingsEstimateFormat"),
            ApiCallLog.EstimateMonth(summary.Total, DateOnly.FromDateTime(DateTime.Now)),
            _budget.Policy.CallsPerMonth);

        static ApiUsageRow ToRow(string name, ApiCallCounts counts) =>
            new(name, counts.Today.ToString("N0"), counts.Last7Days.ToString("N0"), counts.ThisMonth.ToString("N0"));
    }

    /// <summary>
    /// Opens on this device's location (without asking for permission), else the area the rider chose by
    /// postal code, else the saved stops, else the rider's places, else where the flyout's map was left,
    /// else the whole world, with a prompt for a postal code.
    /// </summary>
    private async Task FrameInitialViewAsync()
    {
        List<GeoPoint> points = [];
        if (await LocationService.TryGetDeviceLocationAsync(InitialLocationTimeout) is { } here)
        {
            points = [here];
        }

        if (points.Count == 0 && SettingsService.DefaultArea is { } area)
        {
            points = [new GeoPoint(area.Lat, area.Lon)];
        }

        if (points.Count == 0)
        {
            points = [.. SettingsService.Stops.Select(s => new GeoPoint(s.Lat, s.Lon))];
        }

        if (points.Count == 0)
        {
            points = [.. SettingsService.Places.Select(p => p.Location)];
        }

        if (points.Count == 0 && SettingsService.FlyoutMapView is { } view)
        {
            points = [new GeoPoint(view.Lat, view.Lon)];
        }

        if (_windowCancellation.IsCancellationRequested)
        {
            return;
        }

        if (points.Count == 0)
        {
            ShowAreaStatus("SettingsAreaPrompt");
            _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(WorldCenter), WorldZoom, 0, 0, MapAnimationKind.None);
            return;
        }

        _ = MapHelpers.FrameAsync(PickerMap, points, MapAnimationKind.None, singlePointZoom: 14);
    }

    private void ShowAreaStatus(string resource)
    {
        AreaStatusText.Text = _resources.GetString(resource);
        AreaStatusText.Visibility = Visibility.Visible;
    }

    private void AreaSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = FindAreaAsync();
        }
    }

    private void AreaSearchButton_Click(object sender, RoutedEventArgs e) => _ = FindAreaAsync();

    /// <summary>Moves the map to a postal code and remembers it as the area the map starts in when the rider's location isn't available.</summary>
    private async Task FindAreaAsync()
    {
        string code = AreaSearchBox.Text.Trim();
        if (code.Length == 0)
        {
            return;
        }

        AreaSearchButton.IsEnabled = false;
        try
        {
            if (await GeocodingService.FindPostalCodeAsync(code, _windowCancellation.Token) is not { } point)
            {
                ShowAreaStatus("SettingsAreaNotFound");
                return;
            }

            SettingsService.DefaultArea = new MapView(point.Lat, point.Lon, 13);
            ShowAreaStatus("SettingsAreaSaved");
            _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(point), 13, 0, 0, MapAnimationKind.Default);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            // The window closed.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ShowAreaStatus("SettingsAreaFailed");
        }
        finally
        {
            AreaSearchButton.IsEnabled = true;
        }
    }

    private void OnStopsChanged(object? sender, EventArgs e)
    {
        if (!_savingOrder)
        {
            ReloadSavedStops();
        }
    }

    private void ReloadSavedStops()
    {
        _savedStops.Clear();
        foreach (SavedStop stop in SettingsService.Stops)
        {
            _savedStops.Add(stop);
        }

        NoStopsText.Visibility = _savedStops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SavedStopsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _savingOrder = true;
        SettingsService.SetStops(_savedStops);
        _savingOrder = false;
    }

    private void RemoveStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedStop stop })
        {
            SettingsService.RemoveStop(stop);
        }
    }

    /// <summary>Cancels the previous picker selection's requests and returns the token for the new one.</summary>
    private CancellationToken BeginPickerRequest()
    {
        _pickerCancellation?.Cancel();
        _pickerCancellation?.Dispose();
        _pickerCancellation = new CancellationTokenSource();
        return _pickerCancellation.Token;
    }

    private void StopSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SearchStopsAsync();
        }
    }

    private void StopSearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchStopsAsync();

    /// <summary>Lists the stops near the map whose name or code matches the search. Runs only on Enter or the button, never per keystroke.</summary>
    private async Task SearchStopsAsync()
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        string query = StopSearchBox.Text.Trim();
        if (!_service.IsConfigured || query.Length == 0)
        {
            return;
        }

        int version = ++_pickerVersion;
        CancellationToken cancellationToken = BeginPickerRequest();
        RouteBox.SelectedIndex = -1;
        ResetDirectionAndStops();
        _routeDetails = null;
        StopBox.PlaceholderText = _resources.GetString("SettingsPickerLoading");
        PickerLoadingRing.IsActive = true;
        try
        {
            IReadOnlyList<TransitStop> stops = await _service.SearchStopsAsync(MapCenter, query, cancellationToken);
            if (version != _pickerVersion)
            {
                return;
            }

            // A searched stop has no route picked, so it follows every route there.
            AllRoutesCheck.IsChecked = true;
            AllRoutesCheck.IsEnabled = false;
            ShowStopChoices(null, stops, _resources.GetString(stops.Count == 0 ? "SettingsNoMatchingStops" : "SettingsChooseStop"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection (or closing the window) replaced this one.
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            if (version == _pickerVersion)
            {
                StopBox.PlaceholderText = string.Empty;
                AddStatusText.Text = Describe(ex);
            }
        }
        finally
        {
            if (version == _pickerVersion)
            {
                PickerLoadingRing.IsActive = false;
            }
        }
    }

    /// <summary>Loads the routes of the transit networks serving the map's area (two calls, kept for a week).</summary>
    private async void LoadRoutesButton_Click(object sender, RoutedEventArgs e)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        LoadRoutesButton.IsEnabled = false;
        RouteBox.PlaceholderText = _resources.GetString("SettingsPickerLoading");
        try
        {
            IReadOnlyList<TransitRoute> routes = await _service.GetRoutesNearAsync(MapCenter, _windowCancellation.Token);
            RouteBox.ItemsSource = routes.OrderBy(r => r, RouteOrder.Instance).ToList();
            RouteBox.PlaceholderText = _resources.GetString(routes.Count == 0 ? "SettingsNoRoutesHere" : "SettingsChooseRoute");
            RouteBox.IsEnabled = routes.Count > 0;
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            RouteBox.PlaceholderText = _resources.GetString("SettingsRoutesUnavailable");
            AddStatusText.Text = Describe(ex);
        }

        LoadRoutesButton.IsEnabled = true;
    }

    private async void RouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        if (RouteBox.SelectedItem is not TransitRoute route)
        {
            return;
        }

        int version = ++_pickerVersion;
        CancellationToken cancellationToken = BeginPickerRequest();
        ResetDirectionAndStops();
        AllRoutesCheck.IsEnabled = true;
        AllRoutesCheck.IsChecked = false;
        DirectionBox.PlaceholderText = _resources.GetString("SettingsPickerLoading");
        PickerLoadingRing.IsActive = true;

        try
        {
            RouteDetails details = await _service.GetRouteDetailsAsync(route.Id, cancellationToken);
            if (version != _pickerVersion)
            {
                return;
            }

            _routeDetails = details;
            TransitDirection[] directions = [.. details.Itineraries.Select(i => i.Direction)];
            DirectionBox.ItemsSource = directions;
            DirectionBox.PlaceholderText = _resources.GetString(directions.Length == 0 ? "SettingsNoDirections" : "SettingsChooseDirection");
            DirectionBox.IsEnabled = directions.Length > 0;
            if (directions.Length > 0)
            {
                DirectionBox.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection (or closing the window) replaced this one.
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            if (version == _pickerVersion)
            {
                DirectionBox.PlaceholderText = string.Empty;
                AddStatusText.Text = Describe(ex);
            }
        }
        finally
        {
            if (version == _pickerVersion)
            {
                PickerLoadingRing.IsActive = false;
            }
        }
    }

    /// <summary>The route's details are already loaded, so picking a direction costs nothing.</summary>
    private void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteBox.SelectedItem is not TransitRoute route
            || DirectionBox.SelectedItem is not TransitDirection direction
            || _routeDetails?.Itineraries.FirstOrDefault(i => i.Direction == direction) is not { } itinerary)
        {
            return;
        }

        ++_pickerVersion;
        AddStatusText.Text = string.Empty;
        ShowStopChoices(route, itinerary.Stops, _resources.GetString(itinerary.Stops.Count == 0 ? "SettingsNoStops" : "SettingsChooseStop"));
        ShowRouteOnMap(route, _monitor.Metadata.GetCachedPatterns(route.Id, direction.Id));
    }

    private void StopBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddStatusText.Text = string.Empty;
        AddStopButton.IsEnabled = StopBox.SelectedItem is TransitStop;
        if (StopBox.SelectedItem is not TransitStop stop)
        {
            return;
        }

        // Swap the chosen stop's dot for a larger accent one.
        if (_selectedPin is not null && _stopGlyph is not null)
        {
            _selectedPin.IconElement = _stopGlyph;
            _selectedPin.ZIndex = 0;
        }

        _selectedPin = _stopPins.FirstOrDefault(p => p.Value.Id == stop.Id).Key;
        if (_selectedPin is not null)
        {
            _selectedPin.IconElement = _selectedGlyph;
            _selectedPin.ZIndex = 1;
        }

        _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(stop.Location), Math.Max(PickerMap.ZoomLevel, 15), 0, 0, MapAnimationKind.Default);
    }

    private void StopLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _stopPins.TryGetValue(icon, out TransitStop? stop))
        {
            args.Handled = true;
            StopBox.SelectedItem = stop;
        }
    }

    private void AddStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (StopBox.SelectedItem is not TransitStop stop)
        {
            return;
        }

        TransitRoute? route = RouteBox.SelectedItem as TransitRoute;
        TransitDirection? direction = DirectionBox.SelectedItem as TransitDirection;
        IReadOnlyList<SavedStop> existing = SettingsService.Stops;
        if (existing.All(s => s.StopId != stop.Id) && SavedStop.DistinctStopCount(existing) >= SettingsService.MaxFavoriteStops)
        {
            AddStatusText.Text = string.Format(CultureInfo.CurrentCulture, _resources.GetString("SettingsStopLimitFormat"), SettingsService.MaxFavoriteStops);
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
            route is null || direction is null || AllRoutesCheck.IsChecked == true));
        AddStatusText.Text = string.Format(CultureInfo.CurrentCulture, _resources.GetString("SettingsStopAddedFormat"), stop.Name);
    }

    private void ResetDirectionAndStops()
    {
        PickerLoadingRing.IsActive = false;
        DirectionBox.ItemsSource = null;
        DirectionBox.IsEnabled = false;
        StopBox.ItemsSource = null;
        StopBox.IsEnabled = false;
        StopBox.PlaceholderText = string.Empty;
        AddStopButton.IsEnabled = false;
        AddStatusText.Text = string.Empty;
        _stopLayer.MapElements.Clear();
        _routeLayer.MapElements.Clear();
        _stopPins.Clear();
        _selectedPin = null;
    }

    private void ShowRouteOnMap(TransitRoute route, IReadOnlyList<RoutePattern> patterns)
    {
        Color color = MapHelpers.ParseRouteColor(route.Color);
        _routeLayer.MapElements.Clear();
        _routeLayer.MapElements.AddRange(patterns.Select(p => new MapPolyline
        {
            Path = new Geopath(p.Points.Select(pt => new BasicGeoposition { Latitude = pt.Lat, Longitude = pt.Lon })),
            StrokeColor = color,
            StrokeThickness = 8,
        }));
    }

    /// <summary>Lists stops to choose from, in the box and as dots on the map (in the route's color when there is one).</summary>
    private void ShowStopChoices(TransitRoute? route, IReadOnlyList<TransitStop> stops, string placeholder)
    {
        StopBox.ItemsSource = stops;
        StopBox.PlaceholderText = placeholder;
        StopBox.IsEnabled = stops.Count > 0;
        AddStopButton.IsEnabled = false;
        PickerLoadingRing.IsActive = false;
        if (route is null)
        {
            _routeLayer.MapElements.Clear();
        }

        // One shared glyph for every dot so they share a raster.
        _stopGlyph = route is null
            ? MapHelpers.CreateGlyph(MapHelpers.StopGlyph, ((Microsoft.UI.Xaml.Media.SolidColorBrush)_selectedGlyph.Foreground).Color, 12)
            : MapHelpers.CreateGlyph(MapHelpers.StopGlyph, MapHelpers.ParseRouteColor(route.Color), 12);
        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        _selectedPin = null;
        foreach (TransitStop stop in stops)
        {
            _stopPins[new MapIcon(_stopGlyph, MapHelpers.ToGeopoint(stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) }] = stop;
        }

        _stopLayer.MapElements.AddRange(_stopPins.Keys);
        if (stops.Count > 0)
        {
            _ = MapHelpers.FrameAsync(PickerMap, [.. stops.Select(s => s.Location)], MapAnimationKind.Default);
        }
    }

    private static bool IsTransitFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or TransitApiException;

    private string Describe(Exception ex) => ex is TransitApiException api
        ? api.Message
        : ConnectivityService.IsInternetAvailable
            ? string.Format(CultureInfo.CurrentCulture, _resources.GetString("SettingsTransitUnavailableFormat"), _service.ProviderName)
            : _resources.GetString("SettingsOffline");

    private async Task LoadStartupStateAsync()
    {
        StartupTaskState? state = await StartupService.GetStateAsync();
        _loading = true;
        ApplyStartupState(state);
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        StartupTaskState? state = await StartupService.SetEnabledAsync(StartupToggle.IsOn);
        _loading = true;
        ApplyStartupState(state);
        _loading = false;
    }

    private void ApplyStartupState(StartupTaskState? state)
    {
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;

        string? message = state switch
        {
            null => _resources.GetString("SettingsStartupPackageRequired"),
            StartupTaskState.DisabledByUser => _resources.GetString("SettingsStartupDisabledByUser"),
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => _resources.GetString("SettingsStartupManaged"),
            _ => null,
        };
        StartupStatusText.Text = message ?? string.Empty;
        StartupStatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Numbered routes in numeric order ("12" before "30X"), then lettered routes alphabetically.</summary>
    private sealed class RouteOrder : IComparer<TransitRoute>
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
