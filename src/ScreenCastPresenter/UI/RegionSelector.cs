using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Drag a rectangle over an area of the screen (crop, FR-04; snip, FR-50). Everything outside the
/// selection is dimmed, the selection has an accent edge and a size readout. Drag inside to move,
/// drag an edge or corner to resize, Enter (or double-click) to confirm, Esc to cancel.
/// Built from a few thin windows instead of one screen-sized bitmap, so it costs almost no memory.
/// Like all SCP UI it's excluded from capture.
/// </summary>
internal sealed class RegionSelector : IDisposable
{
    private readonly Rectangle _area;
    private readonly double _scale;
    private readonly string _hint;
    private readonly InputLayer _input;
    private readonly ShadeStrip[] _shade = new ShadeStrip[4];
    private readonly ShadeStrip[] _edge = new ShadeStrip[4];
    private readonly Readout _readout;
    private readonly TaskCompletionSource<Rectangle?> _result = new();
    private Rectangle _selection;

    private RegionSelector(Rectangle area, Rectangle? initial, string hint, double scale)
    {
        _area = area;
        _scale = scale;
        _hint = hint;
        _selection = initial is { } r ? Rectangle.Intersect(r, area) : Rectangle.Empty;
        _input = new InputLayer(this, area);
        for (int i = 0; i < 4; i++)
        {
            _shade[i] = new ShadeStrip(Color.Black, 140);
            _edge[i] = new ShadeStrip(Theme.Current.Accent, 255);
        }
        _readout = new Readout(scale);
    }

    /// <summary>The chosen rectangle in screen pixels, or null if cancelled.</summary>
    public static async Task<Rectangle?> SelectAsync(Rectangle area, Rectangle? initial, string hint, double scale)
    {
        using var selector = new RegionSelector(area, initial, hint, scale);
        selector.Show();
        return await selector._result.Task;
    }

    private void Show()
    {
        _input.Show();
        _input.Activate();
        foreach (ShadeStrip s in _shade) s.Show();
        foreach (ShadeStrip s in _edge) s.Show();
        _readout.Show();
        Layout();
    }

    private int Px(int logical) => (int)Math.Round(logical * _scale);

    /// <summary>Repositions the dim strips, the accent edge and the readout around the selection.</summary>
    private void Layout()
    {
        Rectangle a = _area, s = _selection;
        if (s.Width <= 0 || s.Height <= 0)
        {
            _shade[0].Bounds = a;
            for (int i = 1; i < 4; i++) _shade[i].Visible = false;
            foreach (ShadeStrip e in _edge) e.Visible = false;
        }
        else
        {
            SetStrip(_shade[0], Rectangle.FromLTRB(a.Left, a.Top, a.Right, s.Top));
            SetStrip(_shade[1], Rectangle.FromLTRB(a.Left, s.Bottom, a.Right, a.Bottom));
            SetStrip(_shade[2], Rectangle.FromLTRB(a.Left, s.Top, s.Left, s.Bottom));
            SetStrip(_shade[3], Rectangle.FromLTRB(s.Right, s.Top, a.Right, s.Bottom));
            int t = Math.Max(2, Px(2));
            SetStrip(_edge[0], new Rectangle(s.Left, s.Top, s.Width, t));
            SetStrip(_edge[1], new Rectangle(s.Left, s.Bottom - t, s.Width, t));
            SetStrip(_edge[2], new Rectangle(s.Left, s.Top, t, s.Height));
            SetStrip(_edge[3], new Rectangle(s.Right - t, s.Top, t, s.Height));
        }

        string size = s.Width > 0 ? $"{s.Width} × {s.Height}   ·   " : "";
        _readout.SetText($"{size}{_hint}   ·   Enter to confirm   ·   Esc to cancel");
        Size readout = _readout.Size;
        int x = Math.Clamp((s.Width > 0 ? s.Left + (s.Width - readout.Width) / 2 : a.Left + (a.Width - readout.Width) / 2), a.Left, Math.Max(a.Left, a.Right - readout.Width));
        int below = (s.Width > 0 ? s.Bottom : a.Top + a.Height / 2) + Px(10);
        int y = below + readout.Height <= a.Bottom ? below : Math.Max(a.Top, (s.Width > 0 ? s.Top : a.Top) - readout.Height - Px(10));
        _readout.Location = new Point(x, y);
    }

    private static void SetStrip(ShadeStrip strip, Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            strip.Visible = false;
            return;
        }
        strip.Bounds = r;
        strip.Visible = true;
    }

    private void Finish(Rectangle? result)
    {
        _result.TrySetResult(result is { Width: >= 8, Height: >= 8 } r ? r : result is null ? null : _area);
    }

    public void Dispose()
    {
        _input.Dispose();
        foreach (ShadeStrip s in _shade) s.Dispose();
        foreach (ShadeStrip s in _edge) s.Dispose();
        _readout.Dispose();
    }

    private enum Grip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, New }

    /// <summary>
    /// Covers the area at alpha 1/255: invisible, but it receives the mouse (fully transparent
    /// layered pixels let clicks through to the app below).
    /// </summary>
    private sealed class InputLayer : Form
    {
        private readonly RegionSelector _owner;
        private Grip _grip;
        private Point _start;
        private Rectangle _startSelection;

        public InputLayer(RegionSelector owner, Rectangle area)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = area;
            BackColor = Color.Black;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            Text = "ScreenCast Presenter selection";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var hwnd = (HWND)Handle;
            PInvoke.SetLayeredWindowAttributes(hwnd, default, 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
            DwmHelpers.ExcludeFromCapture(hwnd);
            DwmHelpers.MakeBareSurface(hwnd);
        }

        private Point ToScreen(Point client) => new(client.X + Left, client.Y + Top);

        private Grip HitTest(Point p)
        {
            Rectangle s = _owner._selection;
            if (s.Width <= 0) return Grip.New;
            int g = Math.Max(6, (int)(8 * _owner._scale));
            bool left = Math.Abs(p.X - s.Left) <= g, right = Math.Abs(p.X - s.Right) <= g;
            bool top = Math.Abs(p.Y - s.Top) <= g, bottom = Math.Abs(p.Y - s.Bottom) <= g;
            bool insideX = p.X > s.Left - g && p.X < s.Right + g, insideY = p.Y > s.Top - g && p.Y < s.Bottom + g;
            if (top && left) return Grip.TopLeft;
            if (top && right) return Grip.TopRight;
            if (bottom && left) return Grip.BottomLeft;
            if (bottom && right) return Grip.BottomRight;
            if (left && insideY) return Grip.Left;
            if (right && insideY) return Grip.Right;
            if (top && insideX) return Grip.Top;
            if (bottom && insideX) return Grip.Bottom;
            return s.Contains(p) ? Grip.Move : Grip.New;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _start = ToScreen(e.Location);
            _grip = HitTest(_start);
            _startSelection = _owner._selection;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = ToScreen(e.Location);
            if (!Capture)
            {
                Cursor = HitTest(p) switch
                {
                    Grip.Move => Cursors.SizeAll,
                    Grip.Left or Grip.Right => Cursors.SizeWE,
                    Grip.Top or Grip.Bottom => Cursors.SizeNS,
                    Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
                    Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
                    _ => Cursors.Cross,
                };
                return;
            }

            Rectangle s = _startSelection, a = _owner._area;
            int dx = p.X - _start.X, dy = p.Y - _start.Y;
            Rectangle next = _grip switch
            {
                Grip.New => Normalised(_start, p),
                Grip.Move => new Rectangle(Math.Clamp(s.Left + dx, a.Left, a.Right - s.Width), Math.Clamp(s.Top + dy, a.Top, a.Bottom - s.Height), s.Width, s.Height),
                Grip.Left => Rectangle.FromLTRB(Math.Min(s.Left + dx, s.Right - 8), s.Top, s.Right, s.Bottom),
                Grip.Right => Rectangle.FromLTRB(s.Left, s.Top, Math.Max(s.Right + dx, s.Left + 8), s.Bottom),
                Grip.Top => Rectangle.FromLTRB(s.Left, Math.Min(s.Top + dy, s.Bottom - 8), s.Right, s.Bottom),
                Grip.Bottom => Rectangle.FromLTRB(s.Left, s.Top, s.Right, Math.Max(s.Bottom + dy, s.Top + 8)),
                Grip.TopLeft => Rectangle.FromLTRB(Math.Min(s.Left + dx, s.Right - 8), Math.Min(s.Top + dy, s.Bottom - 8), s.Right, s.Bottom),
                Grip.TopRight => Rectangle.FromLTRB(s.Left, Math.Min(s.Top + dy, s.Bottom - 8), Math.Max(s.Right + dx, s.Left + 8), s.Bottom),
                Grip.BottomLeft => Rectangle.FromLTRB(Math.Min(s.Left + dx, s.Right - 8), s.Top, s.Right, Math.Max(s.Bottom + dy, s.Top + 8)),
                Grip.BottomRight => Rectangle.FromLTRB(s.Left, s.Top, Math.Max(s.Right + dx, s.Left + 8), Math.Max(s.Bottom + dy, s.Top + 8)),
                _ => s,
            };
            _owner._selection = Rectangle.Intersect(next, a);
            _owner.Layout();
        }

        private static Rectangle Normalised(Point a, Point b) =>
            Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            _grip = Grip.None;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _owner.Finish(_owner._selection.Width > 0 ? _owner._selection : _owner._area);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Enter:
                    // Nothing dragged: the whole area.
                    _owner.Finish(_owner._selection.Width > 0 ? _owner._selection : _owner._area);
                    return true;
                case Keys.Escape:
                    _owner.Finish(null);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            BeginInvoke(() => _owner.Finish(null)); // clicking another app cancels
        }
    }

    /// <summary>A click-through rectangle of one colour at a fixed alpha.</summary>
    private sealed class ShadeStrip : OverlayForm
    {
        private readonly byte _alpha;

        public ShadeStrip(Color colour, byte alpha)
        {
            BackColor = colour;
            _alpha = alpha;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PInvoke.SetLayeredWindowAttributes((HWND)Handle, default, _alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);
    }

    /// <summary>The size readout and instructions.</summary>
    private sealed class Readout : OverlayForm
    {
        private readonly Font _font;
        private readonly double _scale;
        private string _text = "";

        public Readout(double scale)
        {
            _scale = scale;
            _font = Theme.PixelFont(13, scale);
            BackColor = Theme.Current.Surface;
        }

        public void SetText(string text)
        {
            if (text == _text) return;
            _text = text;
            Size measured = TextRenderer.MeasureText(text, _font);
            Size = new Size(measured.Width + (int)(28 * _scale), measured.Height + (int)(16 * _scale));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Current.Surface);
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, Theme.Current.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
