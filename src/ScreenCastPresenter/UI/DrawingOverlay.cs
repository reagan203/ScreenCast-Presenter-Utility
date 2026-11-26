using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Drawing on the TV (FR-71). A see-through ink layer covers exactly the part of what is shown
/// that the audience sees, on the presenter's own screen; lines drawn on it go to the TV as they
/// are drawn (inside the output, so a meeting sharing it sees them too). A small toolbar offers
/// pen, highlighter, arrow, box and text (FR-78), colours, undo, clear, the laser and Done. Keys:
/// Esc or Done ends drawing and clears it, Ctrl+Z or a right-click undoes, Delete clears, P pen,
/// H highlighter, A arrow, B box, T text, L laser. Every window is hidden from capture and never on the TV.
/// </summary>
internal sealed class DrawingOverlay : IDisposable
{
    public static readonly (string Name, Color Colour)[] Colours =
    [
        ("Red", Color.FromArgb(0xE5, 0x38, 0x3B)),
        ("Yellow", Color.FromArgb(0xFF, 0xC8, 0x3D)),
        ("Green", Color.FromArgb(0x2E, 0xB8, 0x72)),
        ("Blue", Color.FromArgb(0x3A, 0x86, 0xFF)),
        ("Black", Color.FromArgb(0x11, 0x11, 0x11)),
        ("White", Color.FromArgb(0xFA, 0xFA, 0xFA)),
    ];

    /// <summary>Line widths as a fraction of the content's height: easy to see on a TV, a highlighter like a marker.</summary>
    public const float PenWidth = 0.0045f, HighlighterWidth = 0.022f;

    /// <summary>Arrows and boxes: a little bolder than the pen, so they read as shapes across a room.</summary>
    public const float ShapeWidth = 0.006f;

    /// <summary>Text: the letters' size as a fraction of the content's height (about a slide's body text).</summary>
    public const float TextHeight = 0.045f;

    private readonly InkDrawing _drawing;
    private readonly Func<IEffectsEngine?> _effects;
    private readonly Func<(Rectangle Area, Rectangle Shown)?> _geometry;
    private readonly InkCanvas _canvas;
    private readonly DrawingToolbar _toolbar;
    private Rectangle _area;
    private InkStroke? _current;
    private bool _closed;
    private TextPrompt? _prompt;

    public DrawingOverlay(InkDrawing drawing, Func<IEffectsEngine?> effects, Func<(Rectangle Area, Rectangle Shown)?> geometry, double scale)
    {
        _drawing = drawing;
        _effects = effects;
        _geometry = geometry;
        _canvas = new InkCanvas(this);
        _toolbar = new DrawingToolbar(this, scale);
    }

    public InkTool Tool { get; private set; } = InkTool.Pen;
    public Color PenColour { get; private set; } = Colours[0].Colour;
    public Color HighlighterColour { get; private set; } = Colours[1].Colour;
    public Color Colour => Tool == InkTool.Highlighter ? HighlighterColour : PenColour;

    /// <summary>How wide a new mark of <paramref name="tool"/> is (for text, how tall its letters are).</summary>
    public static float WidthFor(InkTool tool) => tool switch
    {
        InkTool.Highlighter => HighlighterWidth,
        InkTool.Arrow or InkTool.Box => ShapeWidth,
        InkTool.Text => TextHeight,
        _ => PenWidth,
    };

    /// <summary>Done, Esc, or the window closing: drawing ends (the app clears it from the TV).</summary>
    public event EventHandler? Closed;

    /// <summary>The laser was chosen: drawing ends and the laser takes over.</summary>
    public event EventHandler? LaserChosen;

    /// <summary>The ink layer's and toolbar's windows, created without showing them (tests check their styles).</summary>
    internal (nint Canvas, nint Toolbar) CreateWindows()
    {
        UpdateGeometry();
        _canvas.CreateControl();
        _toolbar.CreateControl();
        return (_canvas.Handle, _toolbar.Handle);
    }

    internal Rectangle CanvasBounds => _canvas.Bounds;

    public void Show()
    {
        if (!UpdateGeometry()) return;
        _canvas.Show();
        _canvas.Activate();
        _toolbar.Show();
        PlaceToolbar();
    }

    /// <summary>What's shown moved, was resized or zoomed: the layer follows, and the lines stay on the content. False if it's gone from view.</summary>
    public bool UpdateGeometry()
    {
        if (_closed) return false;
        if (_geometry() is not { } g || g.Shown.Width < 8 || g.Shown.Height < 8)
        {
            Close();
            return false;
        }
        _area = g.Area;
        if (_canvas.Bounds != g.Shown) _canvas.Place(g.Shown);
        _canvas.Redraw(_drawing.Strokes, _current);
        if (_toolbar.Visible) PlaceToolbar();
        return true;
    }

    /// <summary>Above the drawing area when there's room on that screen, otherwise just inside its top edge.</summary>
    private void PlaceToolbar()
    {
        Rectangle shown = _canvas.Bounds;
        Rectangle screen = Screen.FromRectangle(shown).WorkingArea;
        Size size = _toolbar.Size;
        int gap = _toolbar.LogicalToDeviceUnits(8);
        int x = Math.Clamp(shown.Left + (shown.Width - size.Width) / 2, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        int y = shown.Top - size.Height - gap >= screen.Top ? shown.Top - size.Height - gap : Math.Max(screen.Top, shown.Top + gap);
        _toolbar.Location = new Point(x, y);
    }

    public void SetTool(InkTool tool)
    {
        Tool = tool;
        _toolbar.Refresh(this);
    }

    public void SetColour(Color colour)
    {
        if (Tool == InkTool.Highlighter) HighlighterColour = colour;
        else PenColour = colour;
        _toolbar.Refresh(this);
    }

    /// <summary>A point on screen as a fraction of the content's whole area (what the TV maps from).</summary>
    private PointF ToContent(Point screen) => PointerInput.Fraction(screen, _area);

    internal void BeginStroke(Point screen)
    {
        if (Tool == InkTool.Text)
        {
            AskForText(screen);
            return;
        }
        _current = _drawing.Begin(Tool, Colour, WidthFor(Tool));
        _effects()?.InkStart(_current);
        AddPoint(screen);
    }

    internal void AddPoint(Point screen)
    {
        if (_current is null) return;
        PointF p = ToContent(screen);
        if (_current.IsShape && _current.Points.Count > 0)
        {
            // An arrow or a box: its start stays, its end follows the pointer.
            if (_current.Points.Count > 1 && _current.Points[^1] == p) return;
            _current.SetEnd(p);
            _effects()?.InkSet(_current.Id, _current.Points);
        }
        else
        {
            if (!_current.Add(p)) return;
            _effects()?.InkAdd(_current.Id, [p]);
        }
        _canvas.Redraw(_drawing.Strokes, _current, quick: true);
    }

    internal void EndStroke()
    {
        if (_current is null) return;
        InkStroke done = _current;
        _current = null;
        // A click with the arrow or box tool, without dragging, draws nothing: it isn't kept.
        if (done.IsShape && (done.Points.Count < 2 || Distance(done.Points[0], done.Points[^1]) < 0.004f) && _drawing.Remove(done.Id))
            _effects()?.InkRemove(done.Id);
        _canvas.Redraw(_drawing.Strokes, null);
    }

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The text tool: a small box to type in appears where you clicked; Enter puts the words on the TV there.</summary>
    private void AskForText(Point screen)
    {
        _prompt?.Close();
        PointF at = ToContent(screen);
        var prompt = new TextPrompt(Colour);
        _prompt = prompt;
        prompt.Entered += (_, text) =>
        {
            if (_closed || text.Trim().Length == 0) return;
            InkStroke stroke = _drawing.Begin(InkTool.Text, Colour, TextHeight, text);
            stroke.Add(at);
            if (_effects() is { } effects)
            {
                effects.InkStart(stroke);
                effects.InkAdd(stroke.Id, stroke.Points);
            }
            _canvas.Redraw(_drawing.Strokes, null);
        };
        prompt.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_prompt, prompt)) _prompt = null;
            if (!_closed && _canvas.Visible) _canvas.Activate(); // keys go back to the drawing
            prompt.Dispose();
        };
        Rectangle screenArea = Screen.FromPoint(screen).WorkingArea;
        prompt.CreateControl();
        Size size = prompt.Size;
        prompt.Location = new Point(Math.Clamp(screen.X, screenArea.Left, Math.Max(screenArea.Left, screenArea.Right - size.Width)),
            Math.Clamp(screen.Y - size.Height / 2, screenArea.Top, Math.Max(screenArea.Top, screenArea.Bottom - size.Height)));
        prompt.Show();
        prompt.Activate();
    }

    public void Undo()
    {
        if (_current is not null) EndStroke();
        if (_drawing.Undo() is { } id) _effects()?.InkRemove(id);
        _canvas.Redraw(_drawing.Strokes, null);
    }

    public void Clear()
    {
        _current = null;
        _drawing.Clear();
        _effects()?.InkClear();
        _canvas.Redraw(_drawing.Strokes, null);
    }

    public void ChooseLaser()
    {
        Close();
        LaserChosen?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _prompt?.Close();
        _canvas.Hide();
        _toolbar.Hide();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A key on the ink layer or the toolbar.</summary>
    internal bool HandleKey(Keys keys)
    {
        switch (keys)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Delete or Keys.Back: Clear(); return true;
            case Keys.P: SetTool(InkTool.Pen); return true;
            case Keys.H: SetTool(InkTool.Highlighter); return true;
            case Keys.A: SetTool(InkTool.Arrow); return true;
            case Keys.B: SetTool(InkTool.Box); return true;
            case Keys.T: SetTool(InkTool.Text); return true;
            case Keys.L: ChooseLaser(); return true;
        }
        return false;
    }

    private bool IsOurs(HWND hwnd) =>
        _canvas.IsHandleCreated && (nint)hwnd == _canvas.Handle
        || _toolbar.IsHandleCreated && ((nint)hwnd == _toolbar.Handle || PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == (HWND)_toolbar.Handle)
        || _prompt is { IsDisposed: false, IsHandleCreated: true } p && ((nint)hwnd == p.Handle || PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == (HWND)p.Handle);

    public void Dispose()
    {
        _prompt?.Dispose();
        _canvas.Dispose();
        _toolbar.Dispose();
    }

    /// <summary>A one-line box to type a text mark in (FR-78). Enter puts it on the TV; Esc or clicking away cancels.</summary>
    private sealed class TextPrompt : ThemedForm
    {
        private readonly TextBox _box = new() { BorderStyle = BorderStyle.None, MaxLength = InkStroke.MaxTextLength };
        private bool _done;

        public TextPrompt(Color colour)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = "ScreenCast Presenter: text on the TV";
            Padding = new Padding(LogicalToDeviceUnits(10));
            _box.Font = new Font(Theme.UiFont.FontFamily, 13f);
            _box.Width = LogicalToDeviceUnits(320);
            _box.AccessibleName = "Text to put on the TV";
            _box.PlaceholderText = "Type, then press Enter";
            _box.Location = new Point(Padding.Left, Padding.Top);
            Controls.Add(_box);
            ClientSize = new Size(_box.Width + Padding.Horizontal, _box.Height + Padding.Vertical);
            Accent = colour;
        }

        private Color Accent { get; }

        /// <summary>The words typed, when Enter is pressed.</summary>
        public event EventHandler<string>? Entered;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _box.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Enter:
                    _done = true;
                    string text = _box.Text;
                    Close();
                    Entered?.Invoke(this, text);
                    return true;
                case Keys.Escape:
                    Close();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_done && !IsDisposed) BeginInvoke(Close); // clicked away: nothing is added
        }

        protected override void ApplyPalette(Palette palette)
        {
            base.ApplyPalette(palette);
            BackColor = palette.Surface;
            _box.BackColor = palette.Surface;
            _box.ForeColor = palette.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Accent, LogicalToDeviceUnits(2));
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _box.Font.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The ink layer: a per-pixel-alpha window over the shown area. Every pixel is at least 1/255
    /// opaque so it receives the mouse (the app underneath never sees the drawing clicks). Lines
    /// are drawn into a bitmap that is the window's own surface (a DIB section): finished lines
    /// are kept in a second bitmap, so a highlighter line in progress is redrawn whole each time
    /// and never darkens where it overlaps itself.
    /// </summary>
    private sealed class InkCanvas : Form
    {
        private const int WM_MOUSEACTIVATE = 0x0021, MA_ACTIVATE = 1;

        private readonly DrawingOverlay _owner;
        private HDC _memory;
        private nint _dib;
        private HGDIOBJ _previous;
        private nint _bits;
        private Size _size;
        private Bitmap? _surface;  // wraps the DIB's pixels
        private Bitmap? _finished; // finished lines only

        public InkCanvas(DrawingOverlay owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            Text = "ScreenCast Presenter: drawing";
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
            DwmHelpers.ExcludeFromCapture((HWND)Handle);
            DwmHelpers.MakeBareSurface((HWND)Handle);
        }

        public void Place(Rectangle bounds)
        {
            Bounds = bounds;
            if (bounds.Size == _size) return;
            ReleaseSurface();
            _size = bounds.Size;
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = _size.Width,
                Height = -_size.Height, // top-down, like a GDI+ bitmap
                Planes = 1,
                BitCount = 32,
            };
            _memory = PInvoke.CreateCompatibleDC(HDC.Null);
            _dib = CreateDIBSection(0, ref header, 0, out _bits, 0, 0);
            if (_dib == 0 || _bits == 0)
            {
                ReleaseSurface();
                return;
            }
            _previous = PInvoke.SelectObject(_memory, new HGDIOBJ(_dib));
            _surface = new Bitmap(_size.Width, _size.Height, _size.Width * 4, PixelFormat.Format32bppPArgb, _bits);
            _finished = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppPArgb);
        }

        /// <summary>Draws the lines (finished ones again only when <paramref name="quick"/> is false) and shows the result.</summary>
        public void Redraw(IReadOnlyList<InkStroke> strokes, InkStroke? current, bool quick = false)
        {
            if (_surface is null || _finished is null) return;
            if (!quick)
            {
                using Graphics f = Graphics.FromImage(_finished);
                f.Clear(Color.FromArgb(1, 0, 0, 0)); // invisible, but it catches the mouse
                Smooth(f);
                foreach (InkStroke s in strokes)
                {
                    if (!ReferenceEquals(s, current)) DrawStroke(f, s);
                }
            }
            using (Graphics g = Graphics.FromImage(_surface))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(_finished, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                Smooth(g);
                if (current is not null) DrawStroke(g, current);
            }
            Present();
        }

        private static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        private void DrawStroke(Graphics g, InkStroke stroke)
        {
            if (stroke.Points.Count == 0) return;
            Rectangle area = _owner._area;
            PointF Local(PointF p) => new(area.X + p.X * area.Width - Left, area.Y + p.Y * area.Height - Top);
            float width = Math.Max(1.5f, stroke.Width * area.Height);
            Color colour = Color.FromArgb(stroke.Tool == InkTool.Highlighter ? 102 : 255, Color.FromArgb(stroke.Argb));
            switch (stroke.Tool)
            {
                case InkTool.Text:
                    if (stroke.Text is not { Length: > 0 } text) return;
                    using (var font = new Font("Segoe UI Semibold", Math.Max(8f, width), GraphicsUnit.Pixel))
                    using (var shadow = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                    using (var brush = new SolidBrush(colour))
                    {
                        PointF at = Local(stroke.Points[0]);
                        g.DrawString(text, font, shadow, at.X + width * 0.06f, at.Y + width * 0.06f);
                        g.DrawString(text, font, brush, at);
                    }
                    return;
                case InkTool.Arrow or InkTool.Box:
                    if (stroke.Points.Count < 2) return;
                    PointF a = Local(stroke.Points[0]), b = Local(stroke.Points[^1]);
                    using (var shapePen = new Pen(colour, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        if (stroke.Tool == InkTool.Box)
                        {
                            g.DrawRectangle(shapePen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                            return;
                        }
                        g.DrawLine(shapePen, a, b);
                        if (Layouts.ArrowHead(new(a.X, a.Y), new(b.X, b.Y), Math.Max(width * 4, 10)) is { } head)
                        {
                            g.DrawLine(shapePen, head.Tip.X, head.Tip.Y, head.Left.X, head.Left.Y);
                            g.DrawLine(shapePen, head.Tip.X, head.Tip.Y, head.Right.X, head.Right.Y);
                        }
                    }
                    return;
            }
            if (stroke.Points.Count == 1)
            {
                PointF dot = Local(stroke.Points[0]);
                using var brush = new SolidBrush(colour);
                g.FillEllipse(brush, dot.X - width / 2, dot.Y - width / 2, width, width);
                return;
            }
            using var pen = new Pen(colour, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [.. stroke.Points.Select(Local)]);
        }

        private void Present()
        {
            if (_dib == 0 || !IsHandleCreated) return;
            var blend = new BLENDFUNCTION { BlendOp = (byte)PInvoke.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA };
            PInvoke.UpdateLayeredWindow((HWND)Handle, HDC.Null, Location, new SIZE { cx = _size.Width, cy = _size.Height }, _memory, new Point(0, 0), default, blend,
                UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Present();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Capture = true;
                _owner.BeginStroke(PointToScreen(e.Location));
            }
            else if (e.Button == MouseButtons.Right) _owner.Undo();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left) _owner.AddPoint(PointToScreen(e.Location));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
            _owner.EndStroke();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => _owner.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_ACTIVATE; // clicks draw, and keys (Esc, Ctrl+Z) come here
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Switching to another app ends drawing; clicking the toolbar doesn't.
            BeginInvoke(() =>
            {
                if (!IsDisposed && !_owner.IsOurs(PInvoke.GetForegroundWindow())) _owner.Close();
            });
        }

        private void ReleaseSurface()
        {
            _surface?.Dispose();
            _surface = null;
            _finished?.Dispose();
            _finished = null;
            if (!_memory.IsNull)
            {
                if (!_previous.IsNull) PInvoke.SelectObject(_memory, _previous);
                PInvoke.DeleteDC(_memory);
            }
            if (_dib != 0) PInvoke.DeleteObject(new HGDIOBJ(_dib));
            _memory = HDC.Null;
            _dib = 0;
            _previous = default;
            _bits = 0;
            _size = Size.Empty;
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseSurface(); // GDI objects: released even when finalised
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    }

    /// <summary>Pen, highlighter, colours, undo, clear, laser, done. On the presenter's screen, hidden from capture.</summary>
    private sealed class DrawingToolbar : ThemedForm
    {
        private readonly FlowLayoutPanel _row = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(6) };
        private readonly PillButton _pen = new("Pen");
        private readonly PillButton _highlighter = new("Highlighter");
        private readonly PillButton _arrow = new("Arrow");
        private readonly PillButton _box = new("Box");
        private readonly PillButton _text = new("Text");
        private readonly List<SwatchButton> _swatches = [];
        private readonly DrawingOverlay _owner;

        public DrawingToolbar(DrawingOverlay owner, double scale)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            KeyPreview = true;
            Text = "ScreenCast Presenter: drawing tools";
            Padding = new Padding(1);

            Add(_pen, () => owner.SetTool(InkTool.Pen), "Pen (P)");
            Add(_highlighter, () => owner.SetTool(InkTool.Highlighter), "Highlighter (H)");
            Add(_arrow, () => owner.SetTool(InkTool.Arrow), "Arrow: drag from the tail to the tip (A)");
            Add(_box, () => owner.SetTool(InkTool.Box), "Box: drag from corner to corner (B)");
            Add(_text, () => owner.SetTool(InkTool.Text), "Text: click where it goes, then type (T)");
            foreach ((string name, Color colour) in Colours)
            {
                var swatch = new SwatchButton(colour) { AccessibleName = name, Margin = new Padding(0, 0, LogicalToDeviceUnits(4), 0) };
                swatch.Size = new Size(LogicalToDeviceUnits(30), _pen.Measure().Height);
                swatch.Click += (_, _) => owner.SetColour(colour);
                _swatches.Add(swatch);
                _row.Controls.Add(swatch);
            }
            Add(new PillButton("Undo"), owner.Undo, "Undo the last line (Ctrl+Z or right-click)");
            Add(new PillButton("Clear"), owner.Clear, "Clear the drawing (Delete)");
            Add(new PillButton("Laser"), owner.ChooseLaser, "Stop drawing and use the laser pointer (L)");
            Add(new PillButton("Done", PillKind.Primary), owner.Close, "Stop drawing and clear it (Esc)");
            Controls.Add(_row);
            Refresh(owner);
        }

        private void Add(PillButton button, Action click, string name)
        {
            button.AccessibleName ??= name;
            button.Radius = 10;
            button.Margin = new Padding(0, 0, LogicalToDeviceUnits(4), 0);
            button.Size = button.Measure();
            button.Click += (_, _) => click();
            _row.Controls.Add(button);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                return cp;
            }
        }

        /// <summary>The chosen tool and colour stand out.</summary>
        public void Refresh(DrawingOverlay owner)
        {
            _pen.Kind = owner.Tool == InkTool.Pen ? PillKind.Selected : PillKind.Secondary;
            _highlighter.Kind = owner.Tool == InkTool.Highlighter ? PillKind.Selected : PillKind.Secondary;
            _arrow.Kind = owner.Tool == InkTool.Arrow ? PillKind.Selected : PillKind.Secondary;
            _box.Kind = owner.Tool == InkTool.Box ? PillKind.Selected : PillKind.Secondary;
            _text.Kind = owner.Tool == InkTool.Text ? PillKind.Selected : PillKind.Secondary;
            foreach (SwatchButton swatch in _swatches)
            {
                swatch.Chosen = swatch.Colour.ToArgb() == owner.Colour.ToArgb();
                swatch.AccessibleDescription = swatch.Chosen ? "Chosen" : null;
                swatch.Invalidate();
            }
            _pen.Invalidate();
            _highlighter.Invalidate();
            _arrow.Invalidate();
            _box.Invalidate();
            _text.Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => _owner.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        /// <summary>A colour to draw with: a dot of the colour, ringed in the accent colour when chosen.</summary>
        private sealed class SwatchButton : Button
        {
            private bool _hover;

            public SwatchButton(Color colour)
            {
                Colour = colour;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Cursor = Cursors.Hand;
            }

            public Color Colour { get; }

            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Chosen { get; set; }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Palette p = Theme.Current;
                Graphics g = e.Graphics;
                g.Clear(Parent?.BackColor ?? p.Surface);
                Draw.HighQuality(g);
                float size = Math.Min(Width, Height) - LogicalToDeviceUnits(8);
                var dot = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
                if (_hover) Draw.Fill(g, p.Raised, RectangleF.Inflate(dot, 3, 3), (size + 6) / 2);
                Draw.Fill(g, Colour, dot, size / 2);
                Draw.Stroke(g, p.Border, 1, dot, size / 2); // white and black dots stay visible on either theme
                if (Chosen) Draw.Stroke(g, p.Accent, LogicalToDeviceUnits(2), RectangleF.Inflate(dot, 3, 3), (size + 6) / 2);
                if (Focused && ShowFocusCues) Draw.Stroke(g, p.Text, 1, RectangleF.Inflate(dot, 5, 5), (size + 10) / 2);
            }
        }

        protected override void ApplyPalette(Palette palette)
        {
            base.ApplyPalette(palette);
            _row.BackColor = palette.Surface;
            BackColor = palette.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
