using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TrayTimes.Controls;

/// <summary>
/// A trip view's header: Back, the heading, and the actions (the time and Go, or End trip), in that order, on
/// one row. The actions keep their natural width and the heading gets the rest. When that's under
/// <see cref="MinHeadingWidth"/> (a long time label, or large text), the actions move under the heading, so
/// neither the heading nor a button is clipped.
/// </summary>
public sealed partial class TripHeaderPanel : Panel
{
    private const double Spacing = 8;
    private const double RowSpacing = 4;
    private const double MinHeadingWidth = 64; // About "To Work" in one line; less, and a long name has no room for two words a line.

    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
        {
            return default;
        }

        (UIElement back, UIElement heading, UIElement actions) = (Children[0], Children[1], Children[2]);
        Size unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
        back.Measure(unbounded);
        actions.Measure(unbounded);
        double beside = availableSize.Width - back.DesiredSize.Width - Spacing;
        double room = beside - actions.DesiredSize.Width - Spacing;
        _stacked = room < MinHeadingWidth;
        heading.Measure(new Size(Math.Max(0, _stacked ? beside : room), double.PositiveInfinity));
        double top = Math.Max(back.DesiredSize.Height, heading.DesiredSize.Height);
        if (_stacked)
        {
            return new Size(availableSize.Width, top + RowSpacing + actions.DesiredSize.Height);
        }

        double width = double.IsInfinity(availableSize.Width)
            ? back.DesiredSize.Width + Spacing + heading.DesiredSize.Width + Spacing + actions.DesiredSize.Width
            : availableSize.Width;
        return new Size(width, Math.Max(top, actions.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 3)
        {
            return finalSize;
        }

        (UIElement back, UIElement heading, UIElement actions) = (Children[0], Children[1], Children[2]);
        double backWidth = back.DesiredSize.Width;
        double headingLeft = backWidth + Spacing;
        double actionsWidth = actions.DesiredSize.Width;
        double actionsLeft = Math.Max(0, finalSize.Width - actionsWidth);
        if (_stacked)
        {
            double top = Math.Max(back.DesiredSize.Height, heading.DesiredSize.Height);
            back.Arrange(new Rect(0, 0, backWidth, top));
            heading.Arrange(new Rect(headingLeft, 0, Math.Max(0, finalSize.Width - headingLeft), top));
            actions.Arrange(new Rect(actionsLeft, top + RowSpacing, actionsWidth, actions.DesiredSize.Height));
        }
        else
        {
            back.Arrange(new Rect(0, 0, backWidth, finalSize.Height));
            heading.Arrange(new Rect(headingLeft, 0, Math.Max(0, actionsLeft - Spacing - headingLeft), finalSize.Height));
            actions.Arrange(new Rect(actionsLeft, 0, actionsWidth, finalSize.Height));
        }

        return finalSize;
    }
}
