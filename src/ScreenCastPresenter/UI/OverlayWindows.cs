using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Base for SCP's on-screen overlays (outline strips and tab). Topmost, never activated, never in
/// Alt+Tab, excluded from capture, and click-through so it never gets in the presenter's way.
/// </summary>
internal class OverlayForm : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Layered + transparent = mouse clicks pass straight through to the window underneath.
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                               WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var hwnd = (HWND)Handle;
        PInvoke.SetLayeredWindowAttributes(hwnd, default, 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        DwmHelpers.ExcludeFromCapture(hwnd);
        DwmHelpers.MakeBareSurface(hwnd);
        DwmHelpers.ApplyFrame(hwnd, dark: true, roundCorners: false);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_NCHITTEST:
                m.Result = HTTRANSPARENT;
                return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// A per-pixel-alpha window (UpdateLayeredWindow): smooth rounded shapes and soft shadows that
/// WinForms can't draw. Used by the presenting badge. Receives mouse input where it's not
/// transparent, is never activated, and is excluded from capture.
/// </summary>
internal class LayeredWindow : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    public LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                               WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_LAYERED);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmHelpers.ExcludeFromCapture((HWND)Handle);
        DwmHelpers.MakeBareSurface((HWND)Handle);
    }

    private HDC _memory;
    private nint _hbitmap;
    private HGDIOBJ _previous;
    private Size _surfaceSize;

    protected bool HasSurface => _hbitmap != 0;

    /// <summary>
    /// Converts <paramref name="bitmap"/> (32-bit ARGB) into the GDI bitmap the window shows. Done
    /// once per redraw; fades then only change the alpha (<see cref="Present"/>), so a fade
    /// allocates nothing. The caller may dispose <paramref name="bitmap"/> afterwards.
    /// </summary>
    protected void SetSurface(Bitmap bitmap)
    {
        ReleaseSurface();
        _memory = PInvoke.CreateCompatibleDC(HDC.Null);
        // GetHbitmap with a transparent background yields the premultiplied alpha ULW expects.
        _hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        _previous = PInvoke.SelectObject(_memory, new HGDIOBJ(_hbitmap));
        _surfaceSize = bitmap.Size;
    }

    /// <summary>
    /// Shows the surface at <paramref name="location"/>, faded by <paramref name="alpha"/>. A
    /// layered window shows nothing until this is called, so it can never show an unpainted frame.
    /// </summary>
    protected void Present(Point location, byte alpha)
    {
        if (_hbitmap == 0) return;
        if (!IsHandleCreated) CreateHandle();
        var size = new SIZE { cx = _surfaceSize.Width, cy = _surfaceSize.Height };
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER,
            SourceConstantAlpha = alpha,
            AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };
        PInvoke.UpdateLayeredWindow((HWND)Handle, HDC.Null, location, size, _memory, new Point(0, 0), default, blend,
            UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
    }

    private void ReleaseSurface()
    {
        if (_hbitmap == 0) return;
        PInvoke.SelectObject(_memory, _previous);
        PInvoke.DeleteObject(new HGDIOBJ(_hbitmap));
        PInvoke.DeleteDC(_memory);
        _hbitmap = 0;
        _memory = HDC.Null;
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseSurface(); // GDI objects: release even when finalised
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }
}
