using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Stops a control (and everything in it) from drawing until disposed, then draws it all once:
/// a page whose rows are replaced is never seen half built, blank or flickering. Does nothing for
/// a control that isn't on screen yet.
/// </summary>
internal readonly struct PaintPause : IDisposable
{
    private const uint WM_SETREDRAW = 0x000B;
    private readonly Control? _control;

    public PaintPause(Control control)
    {
        if (!control.IsHandleCreated || !control.Visible) return;
        _control = control;
        PInvoke.SendMessage((HWND)control.Handle, WM_SETREDRAW, 0, 0);
    }

    public unsafe void Dispose()
    {
        if (_control is not { IsDisposed: false, IsHandleCreated: true } control) return;
        var hwnd = (HWND)control.Handle;
        PInvoke.SendMessage(hwnd, WM_SETREDRAW, 1, 0);
        PInvoke.RedrawWindow(hwnd, (RECT*)null, HRGN.Null,
            REDRAW_WINDOW_FLAGS.RDW_ERASE | REDRAW_WINDOW_FLAGS.RDW_FRAME | REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN);
    }
}
