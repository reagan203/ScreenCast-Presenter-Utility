using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Output;

/// <summary>
/// The borderless black window on the TV that the capture engine draws into (FR-10).
/// Life cycle (NFR-12): created cloaked → painted black → engine attaches at opacity 0 →
/// <see cref="Reveal"/> → engine fades in. When no source is attached it is the idle screen (FR-16).
/// It is deliberately NOT excluded from capture: Zoom must be able to see it (FR-14).
/// </summary>
internal sealed class OutputWindow : Form
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly bool _topMost;

    public OutputWindow(DisplayMonitor monitor, bool topMost)
    {
        Monitor = monitor;
        _topMost = topMost;

        Text = "ScreenCast Presenter output";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None; // physical pixels; never rescale on a DPI change
        StartPosition = FormStartPosition.Manual;
        Bounds = monitor.Bounds; // window is created directly on the target monitor, at its DPI
        BackColor = DarkTheme.Background;
    }

    public DisplayMonitor Monitor { get; }

    // Showing the output must never take keyboard focus away from the presenter's work.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Tool window: not in Alt+Tab or the taskbar (FR-13). No-activate: clicks don't steal focus.
            // Topmost is set here rather than via Form.TopMost, which activates the window.
            var ex = (WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            if (_topMost) ex |= WINDOW_EX_STYLE.WS_EX_TOPMOST;
            cp.ExStyle = (int)ex;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var hwnd = (HWND)Handle;
        // Cloak before the window is ever shown, so no unpainted frame can reach the TV.
        DwmHelpers.SetCloaked(hwnd, true);
        DwmHelpers.ApplyDarkFrame(hwnd, roundCorners: false);
        DwmHelpers.MakeBareSurface(hwnd);
    }

    /// <summary>Shows the window while still cloaked and paints its black background synchronously.</summary>
    public void ShowCloaked()
    {
        Show();
        Bounds = Monitor.Bounds; // re-assert in case Windows adjusted the position
        DwmHelpers.PaintNow((HWND)Handle);
    }

    /// <summary>Makes the (already painted) window visible on its monitor.</summary>
    public void Reveal() => DwmHelpers.SetCloaked((HWND)Handle, false);

    /// <summary>Hides the window instantly, e.g. just before closing it.</summary>
    public void Conceal()
    {
        if (IsHandleCreated) DwmHelpers.SetCloaked((HWND)Handle, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(DarkTheme.Background);

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
