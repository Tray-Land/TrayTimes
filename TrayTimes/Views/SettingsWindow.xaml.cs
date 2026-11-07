using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
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
/// The Settings window. What it shows and does is in <see cref="SettingsViewModel"/>; this class sets up the window,
/// draws the picker map (dots for the stops to choose from, the chosen route's line), and moves focus.
/// </summary>
public sealed partial class SettingsWindow : WindowEx
{
    private static readonly TimeSpan InitialLocationTimeout = TimeSpan.FromSeconds(3);
    private static readonly GeoPoint WorldCenter = new(20, 0);
    private const double WorldZoom = 2;

    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly MapElementsLayer _routeLayer = new();
    private readonly MapElementsLayer _stopLayer = new();
    private readonly Dictionary<MapIcon, TransitStop> _stopPins = [];
    private readonly FontIcon _selectedGlyph;
    private FontIcon? _stopGlyph;
    private MapIcon? _selectedPin;

    internal SettingsWindow(ArrivalsMonitor monitor, TransitBudget budget)
    {
        ResourceLoader resources = new();
        ViewModel = new SettingsViewModel(monitor, budget, action => DispatcherQueue.TryEnqueue(() => action()), () => App.Current.ApplyKeepAwakeSetting());
        InitializeComponent();
        Title = resources.GetString("SettingsWindowTitle");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowPlacementService.CenterOnPrimary(this, 720, 800);

        ViewModel.MapCenterProvider = () => PickerMap.Center is { } center
            ? new GeoPoint(center.Position.Latitude, center.Position.Longitude)
            : WorldCenter;
        ViewModel.StopChoicesChanged += ShowStopChoices;
        ViewModel.RoutePatternsChanged += ShowRouteOnMap;
        ViewModel.AreaFound += OnAreaFound;
        ViewModel.CustomPlaceAdded += OnCustomPlaceAdded;
        ViewModel.CustomPlaceRemoved += () => AddPlaceButton.Focus(FocusState.Programmatic);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedStop))
            {
                SelectPin(ViewModel.SelectedStop);
            }
        };

        Closed += (_, _) =>
        {
            _windowCancellation.Cancel();
            _windowCancellation.Dispose();
            ViewModel.Dispose();
        };

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
        _ = ViewModel.LoadStartupStateAsync();
    }

    internal SettingsViewModel ViewModel { get; }

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
            ViewModel.PromptForArea();
            _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(WorldCenter), WorldZoom, 0, 0, MapAnimationKind.None);
            return;
        }

        _ = MapHelpers.FrameAsync(PickerMap, points, MapAnimationKind.None, singlePointZoom: 14);
    }

    private void OnAreaFound(GeoPoint point) =>
        _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(point), 13, 0, 0, MapAnimationKind.Default);

    /// <summary>Focuses the new place's name box once the list has made it.</summary>
    private void OnCustomPlaceAdded(CustomPlaceViewModel place) => DispatcherQueue.TryEnqueue(() =>
    {
        int index = ViewModel.CustomPlaces.IndexOf(place);
        if (CustomPlacesList.ContainerFromIndex(index) is DependencyObject container
            && FindDescendant<TextBox>(container) is { } nameBox)
        {
            nameBox.Focus(FocusState.Programmatic);
        }
    });

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void AreaSearchBox_KeyDown(object sender, KeyRoutedEventArgs e) => RunOnEnter(e, ViewModel.FindAreaCommand);

    private void StopSearchBox_KeyDown(object sender, KeyRoutedEventArgs e) => RunOnEnter(e, ViewModel.SearchStopsCommand);

    private static void RunOnEnter(KeyRoutedEventArgs e, System.Windows.Input.ICommand command)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            command.Execute(null);
        }
    }

    private void SavedStopsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => ViewModel.SaveStopOrderCommand.Execute(null);

    private void StopNicknameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            SaveNickname((TextBox)sender);
        }
    }

    private void StopNicknameBox_LostFocus(object sender, RoutedEventArgs e) => SaveNickname((TextBox)sender);

    private static void SaveNickname(TextBox box)
    {
        if (box.DataContext is SavedStopRow row)
        {
            row.SaveNicknameCommand.Execute(box.Text);
        }
    }

    // The picker map.

    private void StopLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _stopPins.TryGetValue(icon, out TransitStop? stop))
        {
            args.Handled = true;
            ViewModel.SelectedStop = stop;
        }
    }

    /// <summary>Swaps the chosen stop's dot for a larger accent one and moves the map to it.</summary>
    private void SelectPin(TransitStop? stop)
    {
        if (_selectedPin is not null && _stopGlyph is not null)
        {
            _selectedPin.IconElement = _stopGlyph;
            _selectedPin.ZIndex = 0;
        }

        _selectedPin = stop is null ? null : _stopPins.FirstOrDefault(p => p.Value.Id == stop.Id).Key;
        if (_selectedPin is not null)
        {
            _selectedPin.IconElement = _selectedGlyph;
            _selectedPin.ZIndex = 1;
        }

        if (stop is not null)
        {
            _ = PickerMap.TrySetViewAsync(MapHelpers.ToGeopoint(stop.Location), Math.Max(PickerMap.ZoomLevel, 15), 0, 0, MapAnimationKind.Default);
        }
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

    /// <summary>Shows stops to choose from as dots on the map (in the route's color when there is one); none clears the map.</summary>
    private void ShowStopChoices(TransitRoute? route, IReadOnlyList<TransitStop> stops)
    {
        _stopLayer.MapElements.Clear();
        _stopPins.Clear();
        _selectedPin = null;
        if (route is null)
        {
            _routeLayer.MapElements.Clear();
        }

        if (stops.Count == 0)
        {
            return;
        }

        // One shared glyph for every dot so they share a raster.
        _stopGlyph = route is null
            ? MapHelpers.CreateGlyph(MapHelpers.StopGlyph, ((SolidColorBrush)_selectedGlyph.Foreground).Color, 12)
            : MapHelpers.CreateGlyph(MapHelpers.StopGlyph, MapHelpers.ParseRouteColor(route.Color), 12);
        foreach (TransitStop stop in stops)
        {
            _stopPins[new MapIcon(_stopGlyph, MapHelpers.ToGeopoint(stop.Location)) { NormalizedAnchorPoint = new Point(0.5, 0.5) }] = stop;
        }

        _stopLayer.MapElements.AddRange(_stopPins.Keys);
        _ = MapHelpers.FrameAsync(PickerMap, stops.Select(s => s.Location).ToList(), MapAnimationKind.Default);
    }
}
