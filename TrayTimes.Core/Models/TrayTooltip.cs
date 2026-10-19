namespace TrayTimes.Models;

/// <summary>The tray icon's tooltip text, which Windows cuts off at <see cref="MaxLength"/> characters.</summary>
public static class TrayTooltip
{
    /// <summary>NOTIFYICONDATA.szTip's limit, less its terminating null.</summary>
    public const int MaxLength = 127;

    /// <summary>
    /// One line each: the <paramref name="lines"/> that must show (cut short with an ellipsis if they
    /// don't fit), then as many of the <paramref name="optional"/> lines as fit whole, in order.
    /// </summary>
    public static string Compose(IEnumerable<string> lines, IEnumerable<string> optional, int maxLength = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(optional);
        string text = string.Join(Environment.NewLine, lines);
        if (text.Length > maxLength)
        {
            return text[..(maxLength - 1)] + "…";
        }

        foreach (string line in optional)
        {
            string longer = text.Length == 0 ? line : text + Environment.NewLine + line;
            if (longer.Length > maxLength)
            {
                break;
            }

            text = longer;
        }

        return text;
    }
}
