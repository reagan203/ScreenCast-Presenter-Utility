using ScreenCastPresenter.Interop;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Base for SCP's own windows (picker, palette, settings, first run):
/// themed from the very first frame (NFR-10/11), rounded corners, follows light/dark changes live,
/// and invisible to screen capture so it can never leak into a Zoom share or recording (FR-14).
/// </summary>
internal class ThemedForm : Form
{
    public ThemedForm()
    {
        Font = Theme.UiFont;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ApplyPalette(Theme.Current);
        Theme.Changed += OnThemeChanged;
    }

    protected Palette Palette => Theme.Current;

    /// <summary>Override to recolour child controls; call base.</summary>
    protected virtual void ApplyPalette(Palette palette)
    {
        BackColor = palette.Background;
        ForeColor = palette.Text;
        if (IsHandleCreated) DwmHelpers.ApplyFrame((HWND)Handle, palette.IsDark, roundCorners: true);
        Invalidate(true);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => ApplyPalette(Theme.Current));
        else ApplyPalette(Theme.Current);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var hwnd = (HWND)Handle;
        DwmHelpers.ExcludeFromCapture(hwnd);
        DwmHelpers.ApplyFrame(hwnd, Theme.Current.IsDark, roundCorners: true);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
