using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
using Windows.Foundation;

namespace TrayTimes.Controls;

/// <summary>
/// A route's service alerts as a warning sign, red when one suspends service, that opens their details. It's
/// collapsed while the route has none. What it shows is in <see cref="AlertsButtonViewModel"/>.
/// </summary>
public sealed partial class AlertsButton : UserControl
{
    public static readonly DependencyProperty AlertsProperty = DependencyProperty.Register(
        nameof(Alerts), typeof(IReadOnlyList<ServiceAlert>), typeof(AlertsButton), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RouteProperty = DependencyProperty.Register(
        nameof(Route), typeof(string), typeof(AlertsButton), new PropertyMetadata(string.Empty, OnChanged));

    public AlertsButton() => InitializeComponent();

    /// <summary>The alerts to show, most severe first; none collapses the sign.</summary>
    public IReadOnlyList<ServiceAlert>? Alerts
    {
        get => (IReadOnlyList<ServiceAlert>?)GetValue(AlertsProperty);
        set => SetValue(AlertsProperty, value);
    }

    /// <summary>The route's display ID ("14", "Blue"), which names it in the sign's tooltip and the details.</summary>
    public string Route
    {
        get => (string)GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    internal AlertsButtonViewModel ViewModel { get; } = new();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        AlertsButton button = (AlertsButton)d;
        button.ViewModel.Update(button.Alerts, button.Route);
    }

    /// <summary>
    /// Opens the details above the sign, but from the window's root: the lists holding the sign replace their rows as
    /// countdowns tick, and a flyout shown from a replaced row would close while being read.
    /// </summary>
    private void SignButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.Content is UIElement root)
        {
            Point top = SignButton.TransformToVisual(root).TransformPoint(new Point(SignButton.ActualWidth / 2, 0));
            DetailsFlyout.ShowAt(root, new FlyoutShowOptions { Position = top, Placement = FlyoutPlacementMode.Top });
        }
    }

    private void DetailsFlyout_Opening(object sender, object e) => ViewModel.RefreshEntries(DateTimeOffset.Now);
}
