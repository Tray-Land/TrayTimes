using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TrayTimes.ViewModels;

namespace TrayTimes.Controls;

/// <summary>A view that shows trip legs and can open a ride's line.</summary>
internal interface ILineLauncher
{
    void RequestLine(LineRequest line);
}

/// <summary>The trip legs' templates, shared by <see cref="TripView"/> and <see cref="TripProgressView"/>; a class so they can use x:Bind.</summary>
public sealed partial class TripLegTemplates : ResourceDictionary
{
    public TripLegTemplates() => InitializeComponent();

    /// <summary>A ride's route badge was pressed: the view showing the leg opens that line's detail.</summary>
    private void RouteBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TripRideItem ride } badge)
        {
            return;
        }

        for (DependencyObject? parent = VisualTreeHelper.GetParent(badge); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ILineLauncher launcher)
            {
                launcher.RequestLine(ride.Line);
                return;
            }
        }
    }
}
