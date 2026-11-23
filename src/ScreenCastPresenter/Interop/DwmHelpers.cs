using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Interop;

/// <summary>Small wrappers over DWM window attributes used for flash-free, private windows.</summary>
internal static class DwmHelpers
{
    // DWMWA_COLOR_NONE: tells Windows 11 not to draw its 1px window border at all.
    private const uint ColorNone = 0xFFFFFFFE;

    /// <summary>
    /// A cloaked window is fully composed by DWM but never shown on any screen. We create windows
    /// cloaked and uncloak them only after their first frame is ready, so no unpainted (white)
    /// frame can ever reach a display.
    /// </summary>
    public static unsafe void SetCloaked(HWND hwnd, bool cloaked)
    {
        BOOL value = cloaked;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
    }

    /// <summary>True when the window is cloaked by anyone: the shell (other virtual desktop, suspended UWP app) or the app itself.</summary>
    public static unsafe bool IsCloaked(HWND hwnd)
    {
        uint value = 0;
        HRESULT hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &value, sizeof(uint));
        return hr.Succeeded && value != 0;
    }

    /// <summary>Title bar colour (NFR-11) and corner style. Failures are ignored: older builds lack some attributes.</summary>
    public static unsafe void ApplyFrame(HWND hwnd, bool dark, bool roundCorners)
    {
        BOOL darkValue = dark;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkValue, (uint)sizeof(BOOL));

        // Windows 11 only; on Windows 10 this call fails harmlessly.
        DWM_WINDOW_CORNER_PREFERENCE corner = roundCorners
            ? DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND
            : DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    /// <summary>Small rounded corners for popup menus (Windows 11).</summary>
    public static unsafe void RoundSmall(HWND hwnd)
    {
        DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUNDSMALL;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    /// <summary>For the output window and overlays: no border, no open/close animation (NFR-14).</summary>
    public static unsafe void MakeBareSurface(HWND hwnd)
    {
        uint border = ColorNone;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &border, sizeof(uint));

        BOOL disable = true;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disable, (uint)sizeof(BOOL));
    }

    /// <summary>
    /// The window's visible bounds in physical pixels. Unlike GetWindowRect this excludes the
    /// invisible resize borders Windows 10/11 add around windows, so the outline hugs the window.
    /// </summary>
    public static unsafe Rectangle GetVisibleBounds(HWND hwnd)
    {
        RECT r;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Succeeded)
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        return PInvoke.GetWindowRect(hwnd, out r) ? Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom) : Rectangle.Empty;
    }

    /// <summary>
    /// Keeps SCP's own UI out of every screen capture, Zoom and recordings included (FR-14, FR-56).
    /// Never call this on the output window, or Zoom would see black.
    /// </summary>
    public static bool ExcludeFromCapture(HWND hwnd)
    {
#if DEBUG
        // Development only, never compiled into Release builds: lets the UI be screenshotted for review.
        if (Environment.GetEnvironmentVariable("SCP_DEV_ALLOW_CAPTURE") == "1") return true;
#endif
        return PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
    }

    public static bool IsExcludedFromCapture(HWND hwnd) =>
        PInvoke.GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == (uint)WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE;

    /// <summary>Paints the window and all its children synchronously, right now.</summary>
    public static unsafe void PaintNow(HWND hwnd) =>
        PInvoke.RedrawWindow(hwnd, (RECT*)null, HRGN.Null,
            REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ERASE |
            REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN | REDRAW_WINDOW_FLAGS.RDW_UPDATENOW);

    /// <summary>Blocks until DWM has composed a frame that includes all changes made so far (at most one refresh).</summary>
    public static void Flush() => _ = PInvoke.DwmFlush();
}
