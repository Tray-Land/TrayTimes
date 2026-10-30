using Microsoft.UI.Xaml;

namespace TrayTimes.Controls;

/// <summary>The trip legs' templates, shared by <see cref="TripView"/> and <see cref="TripProgressView"/>; a class so they can use x:Bind.</summary>
public sealed partial class TripLegTemplates : ResourceDictionary
{
    public TripLegTemplates() => InitializeComponent();
}
