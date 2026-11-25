using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.UI;

internal enum BadgeDot { Accent, Hollow, Warning, Recording }

internal sealed record BadgeButton(string Label, bool Primary, Action OnClick);

/// <param name="Recording">Shown as a red "REC 12:34" segment while a recording runs (FR-54).</param>
internal sealed record BadgeContent(BadgeDot Dot, string Title, string Detail, IReadOnlyList<BadgeButton> Buttons, string? Recording = null);

/// <summary>
/// The small pill at the top of the presenter's screen (FR-15, SRS 6.1): what's showing, with
/// Blank and Stop. Hides itself after 3 seconds and comes back when the mouse touches the top
/// edge. Also carries SCP's messages, because Windows notifications could appear on the TV.
/// A long message wraps onto up to four lines instead of being cut off, stays long enough to read
/// (<see cref="MessageTiming"/>) and stays while the mouse is over it; a message can carry buttons
/// ("Show in a window", "Undo") so nobody has to remember a shortcut to act on it.
/// Never activated, never on the output screen, excluded from capture.
/// </summary>
internal sealed class PresentingBadge : LayeredWindow
{
    private static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(120);

    private readonly Func<DisplayMonitor?> _presenterScreen;
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly System.Windows.Forms.Timer _hoverPoll = new() { Interval = 150 };
    private readonly List<(RectangleF Bounds, BadgeButton Button)> _buttonRects = [];
    private readonly Queue<BadgeContent> _pendingMessages = new();

    private BadgeContent? _status;
    private BadgeContent? _message;
    private Point _location;
    private double _scale = 1;
    private int _hover = -1;
    private bool _visible;
    private bool _mouseInside;
    private DisplayMonitor? _screen;

    /// <summary>Pixels to stay below the top of the screen (the now-showing bar may sit there).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int TopClearance { get; set; }
    private int _fadeVersion;

    public PresentingBadge(Func<DisplayMonitor?> presenterScreen)
    {
        _presenterScreen = presenterScreen;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_mouseInside) return;
            if (_pendingMessages.TryDequeue(out BadgeContent? next))
            {
                _message = next;
                ShowBadge(MessageTiming.For(next.Title, next.Buttons.Count));
                return;
            }
            _message = null;
            HideBadge();
        };
        _hoverPoll.Tick += (_, _) => PollTopEdge();
        Theme.Changed += (_, _) => { if (_visible) Render(); };
    }

    /// <summary>The presenting status; null hides it. Shown for 3 s, then on hover at the top edge.</summary>
    public void SetStatus(BadgeContent? status, bool reveal)
    {
        _status = status;
        _hoverPoll.Enabled = status is not null;
        if (status is null && _message is null)
        {
            HideBadge();
            return;
        }
        if (reveal) ShowBadge(AutoHide);
        else if (_visible) Render(); // e.g. the recording timer ticking: redraw without restarting the hide timer
    }

    /// <summary>A message for the presenter, e.g. "Notepad was closed.", optionally with buttons that act on it.</summary>
    public void ShowMessage(string text, BadgeDot dot = BadgeDot.Accent, IReadOnlyList<BadgeButton>? buttons = null)
    {
        var message = new BadgeContent(dot, text, "", buttons ?? []);
        if (_message is not null && _visible)
        {
            // Queue behind the one showing (several can arrive at startup); skip repeats, cap the backlog.
            if (_message.Title != text && !_pendingMessages.Any(m => m.Title == text) && _pendingMessages.Count < 5)
                _pendingMessages.Enqueue(message);
            return;
        }
        _message = message;
        ShowBadge(MessageTiming.For(text, message.Buttons.Count));
    }

    /// <summary>A message with buttons (e.g. "Screenshot saved · Open · Show in folder"); stays a little longer.</summary>
    public void ShowActions(string title, string detail, IReadOnlyList<BadgeButton> buttons)
    {
        _pendingMessages.Clear();
        _message = new BadgeContent(BadgeDot.Accent, title, detail, buttons);
        TimeSpan reading = MessageTiming.For(title + " " + detail, buttons.Count);
        ShowBadge(reading > TimeSpan.FromSeconds(8) ? reading : TimeSpan.FromSeconds(8));
    }

    private BadgeContent? Content => _message ?? _status;

    private void ShowBadge(TimeSpan hideAfter)
    {
        if (Content is null) return;
        Render();
        _hideTimer.Stop();
        _hideTimer.Interval = (int)hideAfter.TotalMilliseconds;
        _hideTimer.Start();
        if (_visible) return;

        _visible = true;
        Show();
        int version = ++_fadeVersion;
        _ = Fader.RunAsync(0, 1, Fade, a =>
        {
            if (version == _fadeVersion) Present(_location, (byte)(a * 255));
        });
    }

    private void HideBadge()
    {
        _hideTimer.Stop();
        if (!_visible) return;
        _visible = false;
        int version = ++_fadeVersion;
        _ = Fader.RunAsync(1, 0, Fade, a =>
        {
            if (version != _fadeVersion) return;
            Present(_location, (byte)(a * 255));
            if (a <= 0) Hide();
        });
    }

    /// <summary>Reveal the status when the mouse touches the top edge above the badge.</summary>
    private void PollTopEdge()
    {
        if (_visible || _status is null) return;
        DisplayMonitor? screen = _presenterScreen();
        if (screen is null) return;
        Point cursor = Cursor.Position;
        int halfSpan = (int)(260 * screen.Scale);
        int centre = screen.Bounds.Left + screen.Bounds.Width / 2;
        if (cursor.Y <= screen.Bounds.Top + 2 && cursor.Y >= screen.Bounds.Top && Math.Abs(cursor.X - centre) < halfSpan)
            ShowBadge(AutoHide);
    }

    private void Render()
    {
        if (Content is not { } content) return;
        _screen = _presenterScreen();
        if (_screen is null) return;
        _scale = _screen.Scale;
        Palette p = Theme.Current;
        float s = (float)_scale;

        using Font bold = Theme.PixelFont(13, _scale, FontStyle.Bold);
        using Font regular = Theme.PixelFont(13, _scale);
        using Font button = Theme.PixelFont(12.5f, _scale, FontStyle.Bold);

        float t = Theme.TextScale;
        float margin = 14 * s, height = 44 * s * t, pad = 16 * s, gap = 10 * s, dot = 10 * s;
        float buttonHeight = 32 * s * t, buttonPad = 12 * s;
        float maxText = 420 * s * t, maxWrapped = 480 * s * t;

        using var measureBitmap = new Bitmap(1, 1);
        using Graphics measure = Graphics.FromImage(measureBitmap);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        StringFormat format = StringFormat.GenericTypographic;

        float titleWidth = Math.Min(maxText, measure.MeasureString(content.Title, bold, int.MaxValue, format).Width + 2);

        // A message too long for one line wraps (up to four lines) in regular weight, instead of
        // being cut off with an ellipsis; the badge grows taller and gets rounded corners.
        bool wrapped = content.Detail.Length == 0 && content.Recording is null
            && measure.MeasureString(content.Title, bold, int.MaxValue, format).Width + 2 > maxText;
        Font titleFont = wrapped ? regular : bold;
        if (wrapped)
        {
            SizeF block = measure.MeasureString(content.Title, regular, (int)maxWrapped, format);
            float lineHeight = regular.GetHeight(measure);
            titleWidth = Math.Min(maxWrapped, block.Width + 2);
            height = Math.Max(height, Math.Min(block.Height, lineHeight * 4) + 22 * s);
        }
        float detailWidth = content.Detail.Length == 0 ? 0 : Math.Min(maxText, measure.MeasureString(content.Detail, regular, int.MaxValue, format).Width + 2);
        float recordingWidth = content.Recording is { Length: > 0 } rec ? measure.MeasureString(rec, bold, int.MaxValue, format).Width + 2 : 0;
        var buttonWidths = content.Buttons.Select(b => measure.MeasureString(b.Label, button, int.MaxValue, format).Width + 2 * buttonPad).ToList();

        float width = pad + dot + gap + titleWidth;
        if (detailWidth > 0) width += gap + detailWidth;
        if (recordingWidth > 0) width += gap * 1.6f + dot * 0.8f + gap * 0.6f + recordingWidth;
        if (buttonWidths.Count > 0) width += gap + 1 + gap + buttonWidths.Sum() + gap * 0.6f * (buttonWidths.Count - 1) + 6 * s;
        else width += pad;

        int bitmapWidth = (int)Math.Ceiling(width + 2 * margin);
        int bitmapHeight = (int)Math.Ceiling(height + 2 * margin);
        using var surface = new Bitmap(bitmapWidth, bitmapHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        _buttonRects.Clear();

        using (Graphics g = Graphics.FromImage(surface))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var pill = new RectangleF(margin, margin, width, height);
            GraphicsPath Shape(RectangleF r) => wrapped ? Draw.Rounded(r, 22 * s) : Pill(r);
            // Soft shadow: a few expanding, fading pills.
            for (int i = (int)margin; i > 0; i -= 2)
            {
                int alpha = (int)(p.IsDark ? 50 : 22) * (int)(margin - i + 1) / (int)margin;
                using var shadow = new SolidBrush(Color.FromArgb(Math.Clamp(alpha / 3, 0, 255), 0, 0, 0));
                using GraphicsPath sp = Shape(RectangleF.Inflate(pill, i * 0.6f, i * 0.6f) with { Y = pill.Y + i * 0.3f });
                g.FillPath(shadow, sp);
            }
            using (GraphicsPath path = Shape(pill))
            using (var fill = new SolidBrush(p.Surface))
            using (var border = new Pen(p.Border, Math.Max(1, s)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            float x = margin + pad;
            float midY = margin + height / 2;
            DrawDot(g, content.Dot, new RectangleF(x, midY - dot / 2, dot, dot), p, s);
            x += dot + gap;

            if (wrapped) DrawWrapped(g, content.Title, titleFont, p.Text, new RectangleF(x, margin + 11 * s, titleWidth, height - 22 * s));
            else DrawText(g, content.Title, titleFont, p.Text, new RectangleF(x, margin, titleWidth, height));
            x += titleWidth;
            if (detailWidth > 0)
            {
                x += gap;
                DrawText(g, content.Detail, regular, p.TextDim, new RectangleF(x, margin, detailWidth, height));
                x += detailWidth;
            }
            if (recordingWidth > 0)
            {
                x += gap * 1.6f;
                float d = dot * 0.8f;
                using (var red = new SolidBrush(p.Recording)) g.FillEllipse(red, x, midY - d / 2, d, d);
                x += d + gap * 0.6f;
                DrawText(g, content.Recording!, bold, p.Recording, new RectangleF(x, margin, recordingWidth, height));
                x += recordingWidth;
            }

            if (content.Buttons.Count > 0)
            {
                x += gap;
                using (var line = new Pen(p.Border, Math.Max(1, s)))
                    g.DrawLine(line, x, midY - 11 * s, x, midY + 11 * s);
                x += 1 + gap;
                for (int i = 0; i < content.Buttons.Count; i++)
                {
                    BadgeButton b = content.Buttons[i];
                    var r = new RectangleF(x, midY - buttonHeight / 2, buttonWidths[i], buttonHeight);
                    Color back = b.Primary ? p.Accent : (i == _hover ? p.Border : p.Raised);
                    if (b.Primary && i == _hover) back = ControlPaint.Light(p.Accent, 0.15f);
                    using (GraphicsPath bp = Pill(r))
                    using (var bb = new SolidBrush(back))
                        g.FillPath(bb, bp);
                    using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using var tb = new SolidBrush(b.Primary ? p.OnAccent : p.Text);
                    g.DrawString(b.Label, button, tb, r, centred);
                    _buttonRects.Add((r, b));
                    x += buttonWidths[i] + gap * 0.6f;
                }
            }
        }

        Rectangle area = _screen.WorkArea;
        _location = new Point(area.Left + (area.Width - bitmapWidth) / 2, area.Top + TopClearance + (int)(8 * s - margin));
        SetSurface(surface); // one GDI bitmap per redraw; fades only change alpha
        if (_visible) Present(_location, 255);
    }

    private static void DrawText(Graphics g, string text, Font font, Color colour, RectangleF bounds)
    {
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawWrapped(Graphics g, string text, Font font, Color colour, RectangleF bounds)
    {
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord,
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawDot(Graphics g, BadgeDot dot, RectangleF r, Palette p, float s)
    {
        switch (dot)
        {
            case BadgeDot.Hollow:
                using (var pen = new Pen(p.TextDim, 2 * s)) g.DrawEllipse(pen, RectangleF.Inflate(r, -s, -s));
                break;
            case BadgeDot.Warning:
                using (var brush = new SolidBrush(p.Warning)) g.FillEllipse(brush, r);
                break;
            case BadgeDot.Recording:
                using (var brush = new SolidBrush(p.Recording)) g.FillEllipse(brush, r);
                break;
            default:
                using (var halo = new SolidBrush(Color.FromArgb(64, p.Accent))) g.FillEllipse(halo, RectangleF.Inflate(r, 4 * s, 4 * s));
                using (var brush = new SolidBrush(p.Accent)) g.FillEllipse(brush, r);
                break;
        }
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        float d = r.Height;
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouseInside = true;
        int hover = _buttonRects.FindIndex(b => b.Bounds.Contains(e.Location));
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        Render();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _hover = -1;
        Render();
        _hideTimer.Stop();
        _hideTimer.Interval = 1500;
        _hideTimer.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        foreach ((RectangleF bounds, BadgeButton button) in _buttonRects)
        {
            if (!bounds.Contains(e.Location)) continue;
            _message = null;
            _pendingMessages.Clear();
            button.OnClick();
            return;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _hoverPoll.Dispose();
        }
        base.Dispose(disposing);
    }
}
