using ScreenCastPresenter.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>What the widget shows. <see cref="ThumbnailSource"/> 0 = no preview (a whole screen, or nothing).</summary>
internal sealed record WidgetState(
    BadgeDot Dot,
    string Title,
    string Detail,
    string? Recording,
    nint ThumbnailSource,
    Rectangle? ThumbnailCrop,
    IReadOnlyList<(string Text, Action Run)> Menu);

/// <summary>
/// The "now showing" bar, like Teams' sharing bar: a small, always-on-top strip on the presenter's
/// screen while something is presented or recorded. A live mini preview, what it is, where it
/// goes, and the recording time. Click to jump to that window; ⋯ or right-click for actions;
/// drag to move. Never takes focus, never on the TV, excluded from capture like all SCP UI.
/// </summary>
internal sealed class NowShowingWidget : ThemedForm
{
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    private const int LogicalHeight = 40;

    private readonly Font _bold = new(Theme.UiFont.FontFamily, 9f, FontStyle.Bold);
    private readonly Font _small = new(Theme.UiFont.FontFamily, 8.5f);
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private WidgetState? _state;
    private PickerThumbnail? _thumbnail;
    private nint _thumbnailSource;
    private Point _pressAt;
    private Point _formAtPress;
    private bool _pressed;
    private bool _dragging;
    private bool _hover;
    private Rectangle _menuButton;
    private Rectangle _preview;

    public NowShowingWidget()
    {
        Text = "ScreenCast Presenter now showing";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        _menu.Font = Theme.UiFont;
        ThemedMenuRenderer.ApplyPopupChrome(_menu);
    }

    /// <summary>Click on the bar (not a drag): go to what's shown.</summary>
    public event EventHandler? Clicked;

    /// <summary>The user finished dragging it: the new top-left corner (to remember it).</summary>
    public event EventHandler<Point>? Moved;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Above every app (like Teams' sharing bar), out of Alt+Tab, and clicks never take focus
            // from the presenter's work.
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    /// <summary>New content; resizes to fit and refreshes the preview.</summary>
    public void SetState(WidgetState state)
    {
        _state = state;
        Size size = Measure(state);
        if (Size != size)
        {
            // Keep it centred on the same spot as it grows or shrinks.
            int centre = Left + Width / 2;
            Bounds = new Rectangle(centre - size.Width / 2, Top, size.Width, size.Height);
        }
        UpdateThumbnail();
        Invalidate();
    }

    /// <summary>The size for this content, in device pixels.</summary>
    public Size Measure(WidgetState state)
    {
        int text = Math.Max(TextRenderer.MeasureText(state.Title, _bold).Width, TextRenderer.MeasureText(state.Detail, _small).Width);
        int recording = state.Recording is { } r ? TextRenderer.MeasureText(r, _bold).Width + Px(18) : 0;
        int preview = state.ThumbnailSource != 0 ? Px(56) : 0;
        int width = Px(10) + preview + Px(16) + Math.Min(text, Px(220)) + recording + Px(12) + Px(28) + Px(6);
        return new Size(Math.Max(Px(200), width), Px(LogicalHeight));
    }

    private int Px(int logical) => LogicalToDeviceUnits(logical);

    private void UpdateThumbnail()
    {
        nint source = Visible && _state is { ThumbnailSource: not 0 } s && WindowEnumerator.IsAlive(s.ThumbnailSource) ? s.ThumbnailSource : 0;
        if (source != _thumbnailSource)
        {
            _thumbnail?.Dispose();
            _thumbnail = source != 0 && IsHandleCreated ? PickerThumbnail.TryCreate(Handle, source) : null;
            _thumbnailSource = _thumbnail is null ? 0 : source;
        }
        if (_thumbnail is null || _state is null) return;

        _preview = new Rectangle(Px(8), Px(6), Px(48), Px(28));
        Size client = WindowEnumerator.ClientSize(_thumbnailSource);
        Rectangle part = _state.ThumbnailCrop ?? new Rectangle(Point.Empty, client);
        if (part.Width <= 0 || part.Height <= 0) return;
        Rectangle fit = ScreenCastPresenter.Capture.AspectFit.Fit(part.Size, _preview.Size);
        fit.Offset(_preview.Location);
        _thumbnail.Update(fit, part);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            // Live previews only exist while visible (SRS 4.1).
            _thumbnail?.Dispose();
            _thumbnail = null;
            _thumbnailSource = 0;
        }
        else UpdateThumbnail();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(_hover ? p.Raised : p.Surface);
        Draw.HighQuality(g);
        Draw.Stroke(g, p.Border, 1, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Px(10));
        if (_state is not { } s) return;

        int x = Px(10);
        if (s.ThumbnailSource != 0)
        {
            // Dark well behind the live preview (DWM draws the picture on top of it).
            var well = new Rectangle(Px(8), Px(6), Px(48), Px(28));
            Draw.Fill(g, Theme.OutputBlack, well, Px(4));
            if (_thumbnail is null) Draw.Text(g, "—", _small, well, p.TextDim, centre: true);
            x = well.Right + Px(10);
        }

        float dot = Px(8);
        float midY = Height / 2f;
        Color dotColour = s.Dot switch
        {
            BadgeDot.Recording => p.Recording,
            BadgeDot.Warning => p.Warning,
            BadgeDot.Hollow => p.TextDim,
            _ => Color.FromArgb(0x22, 0xC5, 0x5E), // live: green, like the tray
        };
        var dotRect = new RectangleF(x, midY - dot / 2, dot, dot);
        if (s.Dot == BadgeDot.Hollow)
        {
            using var pen = new Pen(dotColour, Math.Max(1, Px(2)));
            g.DrawEllipse(pen, RectangleF.Inflate(dotRect, -1, -1));
        }
        else
        {
            using var brush = new SolidBrush(dotColour);
            g.FillEllipse(brush, dotRect);
        }
        x += (int)dot + Px(8);

        _menuButton = new Rectangle(Width - Px(32), Px(6), Px(26), Px(28));
        int recordingWidth = s.Recording is { } rec ? TextRenderer.MeasureText(rec, _bold).Width + Px(18) : 0;
        int textRight = _menuButton.Left - Px(6) - recordingWidth;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, s.Title, _bold, new Rectangle(x, Px(3), textRight - x, Px(18)), p.Text, flags | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, s.Detail, _small, new Rectangle(x, Px(20), textRight - x, Px(17)), p.TextDim, flags | TextFormatFlags.Top);

        if (s.Recording is { } recording)
        {
            int rx = textRight + Px(8);
            float d = Px(7);
            using (var red = new SolidBrush(p.Recording)) g.FillEllipse(red, rx, midY - d / 2, d, d);
            TextRenderer.DrawText(g, recording, _bold, new Rectangle(rx + Px(10), 0, recordingWidth, Height), p.Recording, flags | TextFormatFlags.VerticalCenter);
        }

        // ⋯ menu button.
        Point cursor = PointToClient(Cursor.Position);
        if (_hover && _menuButton.Contains(cursor)) Draw.Fill(g, p.Border, _menuButton, Px(6));
        float cx = _menuButton.Left + _menuButton.Width / 2f;
        using var dots = new SolidBrush(p.TextDim);
        for (int i = -1; i <= 1; i++) g.FillEllipse(dots, cx - Px(2) + i * Px(6), midY - Px(2), Px(4), Px(4));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is drawn in OnPaint.
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _dragging = false;
        _pressAt = Cursor.Position;
        _formAtPress = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hover)
        {
            _hover = true;
            Invalidate();
        }
        else Invalidate(_menuButton);
        if (!_pressed) return;
        Point now = Cursor.Position;
        if (!_dragging && Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < Px(5)) return;
        _dragging = true;
        Cursor = Cursors.SizeAll;
        Location = new Point(_formAtPress.X + now.X - _pressAt.X, _formAtPress.Y + now.Y - _pressAt.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        Cursor = Cursors.Hand;
        if (_dragging)
        {
            _dragging = false;
            Moved?.Invoke(this, Location);
        }
        else if (_menuButton.Contains(e.Location)) ShowMenu(new Point(_menuButton.Left, _menuButton.Bottom));
        else Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    private void ShowMenu(Point at)
    {
        if (_state is null) return;
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _menu.Items.Clear();
        Palette p = Theme.Current;
        _menu.Renderer = new ThemedMenuRenderer(p);
        _menu.BackColor = p.Surface;
        _menu.ForeColor = p.Text;
        foreach ((string text, Action run) in _state.Menu)
        {
            if (text == "-") _menu.Items.Add(new ToolStripSeparator());
            else _menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => run()));
        }
        _menu.Show(this, at);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmHelpers.ApplyFrame((HWND)Handle, Theme.Current.IsDark, roundCorners: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_state is { } s) SetState(s);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE; // clicking it never steals focus
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _menu.Dispose();
            _bold.Dispose();
            _small.Dispose();
        }
        base.Dispose(disposing);
    }
}
