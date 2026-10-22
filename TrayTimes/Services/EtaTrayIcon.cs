using System.Globalization;
using Microsoft.UI;
using TrayTimes.Tracking;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace TrayTimes.Services;

/// <summary>
/// Shows a tracked bus's minutes-away badge as the tray icon. Owns the generated icon handle:
/// the shell copies the image when the tray icon is updated, so the previous one is freed after.
/// </summary>
internal sealed class EtaTrayIcon
{
    private const uint Blue = 0xFF005FB8;
    private const uint Red = 0xFFC42B1C;
    private const uint White = 0xFFFFFFFF;
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.

    private HICON _icon;

    /// <param name="urgent">True once it's time to head out.</param>
    /// <param name="passed">True once the bus has gone by; the badge shows a slash instead of minutes.</param>
    public void Show(TrayIcon trayIcon, int minutes, bool urgent, bool passed)
    {
        int size = IconSize();
        uint background = urgent ? Red : Blue;
        string text = passed ? "/" : Math.Clamp(minutes, 0, 99).ToString(CultureInfo.InvariantCulture);
        byte[] ico = EtaFont.Render(text, size) is { } coverage
            ? EtaIcon.Create(coverage, size, background, White)
            : EtaIcon.Create(minutes, size, background, White);
        Apply(trayIcon, ico, size);
    }

    /// <summary>Shows <paramref name="glyph"/> (from <see cref="EtaFont.IconFace"/>) on the same badge; false when it doesn't draw.</summary>
    /// <param name="urgent">True once it's time to head out.</param>
    public bool ShowGlyph(TrayIcon trayIcon, string glyph, bool urgent)
    {
        int size = IconSize();
        if (EtaFont.Render(glyph, size, EtaFont.IconFace) is not { } coverage)
        {
            return false;
        }

        Apply(trayIcon, EtaIcon.Create(coverage, size, urgent ? Red : Blue, White), size);
        return true;
    }

    /// <summary>Frees the badge; call after the tray icon has been switched back to the app icon.</summary>
    public void Release()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }

    private static int IconSize() => PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());

    private void Apply(TrayIcon trayIcon, byte[] ico, int size)
    {
        HICON icon;
        nint handle;
        unsafe
        {
            fixed (byte* bits = ico)
            {
                icon = PInvoke.CreateIconFromResourceEx(
                    bits + EtaIcon.ImageOffset,
                    (uint)(ico.Length - EtaIcon.ImageOffset),
                    true,
                    IconFormatVersion,
                    size,
                    size,
                    IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }

            handle = (nint)icon.Value;
        }

        if (handle == 0)
        {
            return;
        }

        trayIcon.SetIcon(Win32Interop.GetIconIdFromIcon(handle));
        Release();
        _icon = icon;
    }
}
