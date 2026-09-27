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
    public void Show(TrayIcon trayIcon, int minutes, bool urgent)
    {
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        byte[] ico = EtaIcon.Create(minutes, size, urgent ? Red : Blue, White);

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

    /// <summary>Frees the badge; call after the tray icon has been switched back to the app icon.</summary>
    public void Release()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }
}
