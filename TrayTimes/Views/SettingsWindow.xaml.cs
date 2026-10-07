using System.Collections.ObjectModel;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

/// <summary>Manages favorite places, favorite stops (route → direction → stop picker with a map), and app settings.</summary>
public sealed partial class SettingsWindow : WindowEx
{
    private readonly ArrivalsMonitor _monitor;
    private readonly StopIndexService _stopIndex;
    private readonly ITransitService _service;
    private readonly ObservableCollection<SavedStop> _savedStops = [];
    private readonly Dictionary<PlacePicker, PlaceKind> _fixedPickers;
    private readonly MapElementsLayer _routeLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly Dictionary<MapIcon, TransitStop> _stopPins = [];
    private readonly Dictionary<string, IReadOnlyList<RoutePattern>> _patterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly FontIcon _selectedGlyph;
    private FontIcon? _stopGlyph;
    private MapIcon? _selectedPin;
    private bool _loading = true;
    private bool _savingOrder;
    private int _pickerVersion;

    internal SettingsWindow(ArrivalsMonitor monitor, StopIndexService stopIndex)
    {
        InitializeComponent();
        _monitor = monitor;
        _stopIndex = stopIndex;
        _service = monitor.Service;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowPlacementService.CenterOnPrimary(this, 520, 820);

        SavedStopsList.ItemsSource = _savedStops;
        ReloadSavedStops();
        SettingsService.StopsChanged += OnStopsChanged;
        Closed += (_, _) => SettingsService.StopsChanged -= OnStopsChanged;

        _fixedPickers = new() { [HomePicker] = PlaceKind.Home, [WorkPicker] = PlaceKind.Work, [SchoolPicker] = PlaceKind.School };
        LoadPlaces();
        _stopIndex.Changed += OnStopIndexChanged;
        Closed += (_, _) => _stopIndex.Changed -= OnStopIndexChanged;
        UpdateStopIndexText();

        ApiUsageService.Changed += OnApiUsageChanged;
        Closed += (_, _) => ApiUsageService.Changed -= OnApiUsageChanged;
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
        FrameInitialView();

        HeadOutBox.Minimum = SettingsService.MinHeadOutMinutes;
        HeadOutBox.Maximum = SettingsService.MaxHeadOutMinutes;
        HeadOutBox.Value = SettingsService.HeadOutMinutes;
        KeepAwakeToggle.IsOn = SettingsService.KeepAwakeWhileTracking;

        KeyBar.IsOpen = !_service.IsConfigured;
        _ = LoadRoutesAsync();
        _ = LoadStartupStateAsync();
        _loading = false;
    }

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
            Header = "Place name",
            PlaceholderText = "Gym, the library, Grandma's…",
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
        StackPanel row = new() { Spacing = 4, Children = { nameRow, picker } };

        string PlaceName() => string.IsNullOrWhiteSpace(nameBox.Text) ? "Custom place" : nameBox.Text.Trim();

        void Label()
        {
            AutomationProperties.SetName(removeButton, $"Remove {PlaceName()}");
            ToolTipService.SetToolTip(removeButton, $"Remove {PlaceName()}");
            picker.AddressAutomationName = $"{PlaceName()} address";
        }

        void Save()
        {
            if (picker.Place is { } place)
            {
                SettingsService.SavePlace(new FavoritePlace(id, PlaceKind.Custom, PlaceName(), place));
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
        App.Current.Tracker.ApplyKeepAwakeSetting();
    }

    private void OnApiUsageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateApiUsage);

    private void UpdateApiUsage()
    {
        ApiCallSummary summary = ApiUsageService.GetSummary();
        ApiUsageList.ItemsSource = new[] { ToRow("All requests", summary.Total) }
            .Concat(summary.ByEndpoint.Select(e => ToRow(e.Key, e.Value)))
            .ToList();

        static ApiUsageRow ToRow(string name, ApiCallCounts counts) =>
            new(name, counts.Today.ToString("N0"), counts.Last7Days.ToString("N0"), counts.Last14Days.ToString("N0"));
    }

    private void ResetApiUsageButton_Click(object sender, RoutedEventArgs e) => ApiUsageService.Reset();

    private void OnStopIndexChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateStopIndexText);

    private void UpdateStopIndexText()
    {
        StopIndexText.Text = _stopIndex.IsBuilding
            ? "Downloading the stop directory…"
            : _stopIndex.BuiltAt is { } at
                ? $"Stop directory: {_stopIndex.Count:N0} stops, updated {at.ToLocalTime():d}."
                : "Stop directory not downloaded yet.";
        RebuildIndexButton.IsEnabled = !_stopIndex.IsBuilding && _service.IsConfigured;
    }

    private async void RebuildIndexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _stopIndex.RebuildAsync();
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            StopIndexText.Text = Describe(ex);
        }
    }

    private void FrameInitialView()
    {
        List<GeoPoint> saved = [.. SettingsService.Stops.Select(s => new GeoPoint(s.Lat, s.Lon))];
        // Downtown Milwaukee until the user has stops of their own.
        _ = MapHelpers.FrameAsync(PickerMap, saved.Count > 0 ? saved : [new GeoPoint(43.0389, -87.9065)], MapAnimationKind.None, singlePointZoom: 12);
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

    private async Task LoadRoutesAsync()
    {
        if (!_service.IsConfigured)
        {
            RouteBox.PlaceholderText = "API key needed";
            return;
        }

        IReadOnlyDictionary<string, TransitRoute> catalog = await _monitor.GetRouteCatalogAsync(TransitConsumer.Settings);
        if (catalog.Count == 0)
        {
            RouteBox.PlaceholderText = "Couldn't load routes";
            AddStatusText.Text = $"Couldn't reach {_service.AgencyName}. Close and reopen this window to try again.";
            return;
        }

        RouteBox.ItemsSource = catalog.Values.OrderBy(r => r, RouteOrder.Instance).ToList();
        RouteBox.PlaceholderText = "Choose a route";
        RouteBox.IsEnabled = true;
    }

    private async void RouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        if (RouteBox.SelectedItem is not TransitRoute route)
        {
            return;
        }

        int version = ++_pickerVersion;
        ResetDirectionAndStops();
        DirectionBox.PlaceholderText = "Loading…";

        try
        {
            IReadOnlyList<TransitDirection> directions = await _service.GetDirectionsAsync(route.Id);
            if (version != _pickerVersion)
            {
                return;
            }

            DirectionBox.ItemsSource = directions;
            DirectionBox.PlaceholderText = directions.Count == 0 ? "No directions" : "Choose a direction";
            DirectionBox.IsEnabled = directions.Count > 0;
            if (directions.Count > 0)
            {
                DirectionBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            if (version == _pickerVersion)
            {
                DirectionBox.PlaceholderText = string.Empty;
                AddStatusText.Text = Describe(ex);
            }
        }
    }

    private async void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Settings, TransitTrigger.PickerSelection);
        if (RouteBox.SelectedItem is not TransitRoute route || DirectionBox.SelectedItem is not TransitDirection direction)
        {
            return;
        }

        int version = ++_pickerVersion;
        StopBox.ItemsSource = null;
        StopBox.IsEnabled = false;
        StopBox.PlaceholderText = "Loading…";
        AddStopButton.IsEnabled = false;
        AddStatusText.Text = string.Empty;
        PickerLoadingRing.IsActive = true;

        try
        {
            Task<IReadOnlyList<TransitStop>> stopsTask = _service.GetStopsAsync(route.Id, direction.Id);
            Task<IReadOnlyList<RoutePattern>> patternsTask = GetPatternsAsync(route.Id);
            IReadOnlyList<TransitStop> stops = await stopsTask;
            IReadOnlyList<RoutePattern> patterns = await patternsTask;
            if (version != _pickerVersion)
            {
                return;
            }

            StopBox.ItemsSource = stops;
            StopBox.PlaceholderText = stops.Count == 0 ? "No stops" : "Choose a stop";
            StopBox.IsEnabled = stops.Count > 0;
            ShowRouteOnMap(route, direction, stops, patterns);
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
        if (RouteBox.SelectedItem is not TransitRoute route
            || DirectionBox.SelectedItem is not TransitDirection direction
            || StopBox.SelectedItem is not TransitStop stop)
        {
            return;
        }

        IReadOnlyList<SavedStop> existing = SettingsService.Stops;
        if (existing.All(s => s.StopId != stop.Id) && SavedStop.DistinctStopCount(existing) >= _service.MaxStopsPerRequest)
        {
            AddStatusText.Text = $"You can follow up to {_service.MaxStopsPerRequest} stops. Remove one first.";
            return;
        }

        SettingsService.AddStop(new SavedStop(
            stop.Id,
            stop.Name,
            stop.Location.Lat,
            stop.Location.Lon,
            route.Id,
            route.DisplayId,
            route.Color,
            direction.Id,
            direction.Name,
            AllRoutesCheck.IsChecked == true));
        AddStatusText.Text = $"Added {stop.Name}.";
    }

    private void ResetDirectionAndStops()
    {
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

    private void ShowRouteOnMap(TransitRoute route, TransitDirection direction, IReadOnlyList<TransitStop> stops, IReadOnlyList<RoutePattern> patterns)
    {
        Color color = MapHelpers.ParseRouteColor(route.Color);
        _routeLayer.MapElements.Clear();
        _routeLayer.MapElements.AddRange(patterns
            .Where(p => string.Equals(p.DirectionId, direction.Id, StringComparison.OrdinalIgnoreCase))
            .Select(p => new MapPolyline
            {
                Path = new Geopath(p.Points.Select(pt => new BasicGeoposition { Latitude = pt.Lat, Longitude = pt.Lon })),
                StrokeColor = color,
                StrokeThickness = 4,
            }));

        // One shared glyph for every dot so they share a raster.
        _stopGlyph = MapHelpers.CreateGlyph(MapHelpers.StopGlyph, color, 12);
        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        _selectedPin = null;
        foreach (TransitStop stop in stops)
        {
            _stopPins[new MapIcon(_stopGlyph, MapHelpers.ToGeopoint(stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) }] = stop;
        }

        _stopLayer.MapElements.AddRange(_stopPins.Keys);
        _ = MapHelpers.FrameAsync(PickerMap, [.. stops.Select(s => s.Location)], MapAnimationKind.Default);
    }

    private async Task<IReadOnlyList<RoutePattern>> GetPatternsAsync(string routeId)
    {
        if (_patterns.TryGetValue(routeId, out IReadOnlyList<RoutePattern>? cached))
        {
            _service.Diagnostics?.RecordReuse(TransitEndpoint.Patterns, inFlight: false, batchSize: 1);
            return cached;
        }

        try
        {
            IReadOnlyList<RoutePattern> patterns = await _service.GetPatternsAsync(routeId);
            _patterns[routeId] = patterns;
            return patterns;
        }
        catch (Exception ex) when (IsTransitFailure(ex))
        {
            // The route line is decoration; the stop dots still work without it.
            return [];
        }
    }

    private static bool IsTransitFailure(Exception ex) => ex is HttpRequestException or TaskCanceledException or TransitApiException;

    private string Describe(Exception ex) => ex is TransitApiException api
        ? api.Message
        : ConnectivityService.IsInternetAvailable ? $"Couldn't reach {_service.AgencyName}. Try again." : "No internet connection.";

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
            null => "Start with Windows is only available when Tray Times is installed as a package.",
            StartupTaskState.DisabledByUser => "Startup was turned off in Task Manager or Windows Settings > Apps > Startup. Turn it back on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "This setting is managed by your organization.",
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
