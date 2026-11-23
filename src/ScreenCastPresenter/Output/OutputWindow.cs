using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Output;

/// <summary>
/// The black window the capture engine draws into: borderless and fullscreen on the TV or a
/// virtual "Zoom screen" (FR-10, FR-12), or a floating, resizable, dark-framed window on the
/// presenter's screen that Zoom can share by name (FR-11).
/// Life cycle (NFR-12): created cloaked → painted black → engine attaches at opacity 0 →
/// <see cref="Reveal"/> → engine fades in. When no source is attached it is the idle screen (FR-16).
/// It is deliberately NOT excluded from capture: Zoom must be able to see it (FR-14).
/// Always #0B0B0C, whatever the app theme (NFR-10).
/// </summary>
internal sealed class OutputWindow : Form, IOutputSurface
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly bool _topMost;
    private readonly bool _floating;
    private string? _message;
    private double _messageBrightness;
    private Font? _messageFont;

    public OutputWindow(OutputPlacement placement)
    {
        Monitor = placement.Monitor;
        _topMost = placement.TopMost; // a floating window may stay on top too ("Keep on top")
        _floating = placement.IsFloating;

        Text = "ScreenCast Presenter output";
        AutoScaleMode = AutoScaleMode.None; // physical pixels; never rescale on a DPI change
        StartPosition = FormStartPosition.Manual;
        if (_floating)
        {
            // A normal window: in the taskbar and Alt+Tab, so it can be found and so Zoom lists it.
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            Icon = AppIcon.Shared; // its taskbar button, and Zoom's window list
            MinimumSize = OutputPlacement.MinimumWindow;
            Bounds = placement.Window!.Value;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            Bounds = Monitor.Bounds; // window is created directly on the target monitor, at its DPI
        }
        BackColor = Theme.OutputBlack;
        // No double buffering: it would allocate a full-screen back buffer (~9 MB at 1920×1200)
        // for a window that only ever paints black and, when blanked, one line of grey text.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        Resize += (_, _) =>
        {
            _messageFont?.Dispose(); // the idle message scales with the window
            _messageFont = null;
            Resized?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The screen it is on; a floating window updates this as it is dragged.</summary>
    public DisplayMonitor Monitor { get; private set; }

    public bool IsFloating => _floating;

    /// <summary>A floating window's normal position and size (even while maximised), to reopen it there.</summary>
    public Rectangle WindowBounds => WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

    public bool IsAlive => !IsDisposed && IsHandleCreated;
    public event EventHandler? Resized;

    /// <summary>The user closed the floating window: the session should stop showing.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The floating window was minimised or restored (Zoom can't share a minimised window).</summary>
    public event EventHandler? MinimizedChanged;

    public bool IsMinimized => WindowState == FormWindowState.Minimized;

    public bool IsPrivatePreview { get; private set; }

    public void SetPrivatePreview(bool on)
    {
        if (on == IsPrivatePreview || !IsAlive) return;
        IsPrivatePreview = on;
        var hwnd = (HWND)Handle;
        if (on) DwmHelpers.ExcludeFromCapture(hwnd);
        else DwmHelpers.IncludeInCapture(hwnd);
        Text = on ? "ScreenCast Presenter preview (private)" : "ScreenCast Presenter output";
    }

    // Showing the output must never take keyboard focus away from the presenter's work.
    protected override bool ShowWithoutActivation => true;

    private FormWindowState _lastState = FormWindowState.Normal;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != _lastState)
        {
            bool minimizedChanged = WindowState == FormWindowState.Minimized || _lastState == FormWindowState.Minimized;
            _lastState = WindowState;
            if (minimizedChanged) MinimizedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (_floating && IsHandleCreated && DisplayHelpers.FromWindow(Handle) is { } now && !now.IsSameScreen(Monitor))
        {
            Monitor = now;
            Resized?.Invoke(this, EventArgs.Empty); // the session re-checks whether it now shows its own screen
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The ✕ on the floating window means "stop showing": the session fades out and closes it.
        if (_floating && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnFormClosing(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (_floating)
            {
                // An ordinary window the user moves and resizes; optionally above every other window.
                if (_topMost) cp.ExStyle |= (int)WINDOW_EX_STYLE.WS_EX_TOPMOST;
                return cp;
            }
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
        // Register before anything else so the capture guard never excludes the output (FR-14).
        CaptureGuard.AllowCapture(Handle);
        base.OnHandleCreated(e);
        var hwnd = (HWND)Handle;
        // Cloak before the window is ever shown, so no unpainted frame can reach the TV.
        DwmHelpers.SetCloaked(hwnd, true);
        DwmHelpers.ApplyFrame(hwnd, dark: true, roundCorners: _floating); // dark title bar on the floating window (NFR-11)
        DwmHelpers.MakeBareSurface(hwnd);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CaptureGuard.Forget(Handle);
        base.OnHandleDestroyed(e);
    }

    public void ShowCloaked()
    {
        Show();
        if (!_floating) Bounds = Monitor.Bounds; // re-assert in case Windows adjusted the position
        DwmHelpers.PaintNow((HWND)Handle);
    }

    /// <summary>Makes the (already painted) window visible on its monitor.</summary>
    public void Reveal()
    {
        if (IsAlive) DwmHelpers.SetCloaked((HWND)Handle, false);
    }

    /// <summary>Hides the window instantly, e.g. just before closing it.</summary>
    public void Conceal()
    {
        if (IsAlive) DwmHelpers.SetCloaked((HWND)Handle, true);
    }

    public void SetIdleMessage(string? message, double brightness = 1)
    {
        _message = message;
        _messageBrightness = Math.Clamp(brightness, 0, 1);
        if (IsAlive) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.OutputBlack);
        if (string.IsNullOrEmpty(_message) || _messageBrightness <= 0) return;

        // Soft grey text, never bright: blend from the background to #9A9AA2.
        Color target = Color.FromArgb(0x9A, 0x9A, 0xA2);
        Color colour = Color.FromArgb(
            Lerp(Theme.OutputBlack.R, target.R, _messageBrightness),
            Lerp(Theme.OutputBlack.G, target.G, _messageBrightness),
            Lerp(Theme.OutputBlack.B, target.B, _messageBrightness));

        _messageFont ??= new Font(Theme.UiFont.FontFamily, Math.Max(18f, ClientSize.Height / 22f), FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, _message, _messageFont, ClientRectangle, colour,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint (double-buffered), so there's never a half-drawn frame.
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE && !_floating) // the floating window is moved and resized like any other
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _messageFont?.Dispose();
        base.Dispose(disposing);
    }
}
