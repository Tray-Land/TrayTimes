using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace TrayTimes.Views;

/// <summary>
/// Shows the up-down resize cursor over its content. Hosts the map/list splitter, since
/// <see cref="Microsoft.UI.Xaml.Controls.Primitives.Thumb"/> is sealed and the cursor can only be set from a subclass.
/// </summary>
public sealed partial class ResizeCursorGrid : Grid
{
    public ResizeCursorGrid() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
}
