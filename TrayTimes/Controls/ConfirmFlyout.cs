using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TrayTimes.Controls;

/// <summary>A question beside the control that raised it, with a button to go ahead (accent) and one to leave things as they are.</summary>
internal static class ConfirmFlyout
{
    private const double TextWidth = 260;

    /// <param name="confirmed">Runs when the rider goes ahead; nothing happens otherwise.</param>
    public static void Show(FrameworkElement anchor, string question, string confirm, string cancel, Action confirmed)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(confirmed);
        Button yes = new() { Content = confirm, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Button no = new() { Content = cancel };
        AutomationProperties.SetAutomationId(yes, "ConfirmButton");
        AutomationProperties.SetAutomationId(no, "CancelButton");
        AutomationProperties.SetHelpText(yes, question); // Focus lands on a button; Narrator reads the question with it.
        AutomationProperties.SetHelpText(no, question);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, MaxWidth = TextWidth });
        content.Children.Add(buttons);
        Flyout flyout = new() { Content = content };
        yes.Click += (_, _) =>
        {
            flyout.Hide();
            confirmed();
        };
        no.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(anchor);
    }
}
