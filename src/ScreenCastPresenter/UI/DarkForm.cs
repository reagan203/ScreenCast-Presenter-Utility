using ScreenCastPresenter.Interop;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Base for all of SCP's own windows (picker, settings…):
/// dark from the very first frame (NFR-10/11), rounded corners, and invisible to screen capture
/// so it can never leak into a Zoom share (FR-14).
/// </summary>
internal class DarkForm : Form
{
    public DarkForm()
    {
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        Font = DarkTheme.UiFont;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var hwnd = (HWND)Handle;
        DwmHelpers.ApplyDarkFrame(hwnd, roundCorners: true);
        DwmHelpers.ExcludeFromCapture(hwnd);
        // Stay cloaked until every control has painted once (see OnShown): WinForms controls
        // briefly show the system (white) background before their first WM_PAINT.
        DwmHelpers.SetCloaked(hwnd, true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var hwnd = (HWND)Handle;
        DwmHelpers.PaintNow(hwnd);
        DwmHelpers.Flush();
        DwmHelpers.SetCloaked(hwnd, false);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);
}
