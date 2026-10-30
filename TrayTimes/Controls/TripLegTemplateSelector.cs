using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TrayTimes.Controls;

public sealed partial class TripLegTemplateSelector : DataTemplateSelector
{
    public DataTemplate WalkTemplate { get; set; } = null!;

    public DataTemplate RideTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) => item is TripWalkItem ? WalkTemplate : RideTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
