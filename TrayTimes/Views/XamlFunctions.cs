using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TrayTimes.Views;

/// <summary>
/// Functions for <c>x:Bind</c> that turn a view model's plain values into the UI types they are shown as, so view models
/// stay free of brushes and the like.
/// </summary>
public static class XamlFunctions
{
    private static readonly double[] Dashed = [2, 2];

    public static Brush Solid(Color color) => new SolidColorBrush(color);

    /// <summary>An info bar's severity: a warning when something went wrong, else informational.</summary>
    public static InfoBarSeverity NoticeSeverity(bool isWarning) => isWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

    /// <summary>A line's dash pattern: solid once <paramref name="traveled"/>, dashed while it is still ahead.</summary>
    public static DoubleCollection LinkDashes(bool traveled)
    {
        DoubleCollection dashes = [];
        if (!traveled)
        {
            foreach (double dash in Dashed)
            {
                dashes.Add(dash);
            }
        }

        return dashes;
    }
}
