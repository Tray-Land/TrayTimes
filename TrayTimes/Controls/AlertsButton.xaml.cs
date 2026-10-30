using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TrayTimes.Services;
using TrayTimes.Transit;
using Windows.Foundation;

namespace TrayTimes.Controls;

/// <summary>
/// A route's service alerts as a warning sign, red when one suspends service, that opens their details. It's
/// collapsed while the route has none.
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

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AlertsButton)d).Update();

    private void Update()
    {
        IReadOnlyList<ServiceAlert> alerts = Alerts ?? [];
        Visibility = alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (alerts.Count == 0)
        {
            return;
        }

        bool severe = alerts.Any(a => a.Severity == AlertSeverity.Severe);
        CautionIcon.Visibility = severe ? Visibility.Collapsed : Visibility.Visible;
        CriticalIcon.Visibility = severe ? Visibility.Visible : Visibility.Collapsed;
        string name = AlertMessages.SignName(Route, alerts);
        AutomationProperties.SetName(SignButton, name);
        ToolTipService.SetToolTip(SignButton, name);
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

    /// <summary>Fills in the details as they open, so their "until" and "starts" times read from now.</summary>
    private void DetailsFlyout_Opening(object sender, object e)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        HeadingText.Text = AlertMessages.Heading(Route);
        AlertsList.ItemsSource = (Alerts ?? []).Select(a => new AlertEntry(a, now)).ToList();
    }
}

/// <summary>One alert in the details: its headline, then what it does, why, and when, then its text.</summary>
public sealed class AlertEntry(ServiceAlert alert, DateTimeOffset now)
{
    public string Title { get; } = AlertMessages.Title(alert);

    public string Summary { get; } = AlertMessages.Summary(alert, now);

    public string Body { get; } = AlertMessages.Body(alert);

    public Visibility SummaryVisibility => Summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BodyVisibility => Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
