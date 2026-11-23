using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>
/// First run (SRS 4.3), at most two steps: 1. pick the output screen (hover a screen to see its
/// number on it), 2. learn the picker shortcut and where the tray icon is.
/// </summary>
internal sealed class FirstRunForm : ThemedForm
{
    private readonly ScpApp _app;
    private readonly Label _step = new() { AutoSize = true, Tag = "dim" };
    private readonly Label _heading = new() { AutoSize = true };
    private readonly Label _body = new() { AutoSize = false, Height = 48, Tag = "dim" };
    private readonly ScreenChooser _screens;
    private readonly KeysPanel _keys;
    private readonly Label _tip = new() { AutoSize = false, Height = 64, Tag = "dim" };
    private readonly PillButton _back = new("Back");
    private readonly PillButton _next = new("Next", PillKind.Primary);
    private readonly Font _headingFont = new(Theme.UiFont.FontFamily, 15f, FontStyle.Bold);
    private int _page;

    public FirstRunForm(ScpApp app)
    {
        _app = app;
        _screens = new ScreenChooser(app);
        _keys = new KeysPanel(app);
        Text = "Welcome to ScreenCast Presenter";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ClientSize = new Size(600, 500);
        KeyPreview = true;

        _heading.Font = _headingFont;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(28, 24, 28, 20) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Dock = DockStyle.Fill;
        _tip.Dock = DockStyle.Fill;
        _screens.Dock = DockStyle.Fill;
        _keys.Dock = DockStyle.Fill;

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_screens);
        content.Controls.Add(_keys);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        _next.Size = _next.Measure();
        _back.Size = _back.Measure();
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);

        layout.Controls.Add(_step, 0, 0);
        layout.Controls.Add(_heading, 0, 1);
        layout.Controls.Add(_body, 0, 2);
        layout.Controls.Add(content, 0, 3);
        layout.Controls.Add(_tip, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        _next.Click += (_, _) => Next();
        _back.Click += (_, _) => ShowPage(0);
        AcceptButton = _next;
        ApplyPalette(Theme.Current);
        ShowPage(0);
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton or ScreenChooser or KeysPanel) return;
        control.BackColor = p.Background;
        control.ForeColor = control.Tag is "dim" ? p.TextDim : p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    private void ShowPage(int page)
    {
        _page = page;
        bool first = page == 0;
        _step.Text = first ? "STEP 1 OF 2" : "STEP 2 OF 2";
        _heading.Text = first ? "Where should your presentation appear?" : "Open the picker any time";
        _body.Text = first
            ? (_app.Monitors.Count > 1 ? "Pick the TV or projector. Your own screen stays private." : "Only one screen is connected. Connect your TV now, or continue and choose it later from the tray icon.")
            : "Press these keys together, from any app:";
        _tip.Text = first
            ? "Not sure which is which? Hover over a screen and its number appears on it."
            : "Or click the ScreenCast Presenter icon next to the clock. To keep it visible: Settings › Personalisation › Taskbar › Other system tray icons.";
        _screens.Visible = first;
        _keys.Visible = !first;
        _back.Visible = !first;
        _next.Text = first ? "Next" : "Done";
        _next.Size = _next.Measure();
        if (first) _screens.Focus();
    }

    private void Next()
    {
        if (_page == 0)
        {
            if (_screens.Chosen is { } monitor) _app.SetOutputMonitor(monitor);
            ShowPage(1);
            return;
        }
        _app.CompleteFirstRun();
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_page == 0 && keyData is Keys.Left or Keys.Right)
        {
            _screens.MoveSelection(keyData == Keys.Left ? -1 : 1);
            return true;
        }
        if (keyData == Keys.Escape)
        {
            if (_page == 1) ShowPage(0);
            else Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _headingFont.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The connected screens drawn to scale; click (or arrow keys) to choose, hover to identify.</summary>
    private sealed class ScreenChooser : Control
    {
        private readonly ScpApp _app;
        private readonly List<(DisplayMonitor Monitor, Rectangle Bounds)> _cards = [];
        private int _chosen;
        private int _hover = -1;
        private IdentifyOverlay? _identify;

        public ScreenChooser(ScpApp app)
        {
            _app = app;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Screens";
            DisplayMonitor? chosen = app.ChosenOutput;
            _chosen = Math.Max(0, app.Monitors.ToList().FindIndex(m => chosen is not null && m.IsSameScreen(chosen)));
        }

        public DisplayMonitor? Chosen => _app.Monitors.Count > 0 ? _app.Monitors[Math.Clamp(_chosen, 0, _app.Monitors.Count - 1)] : null;

        public void MoveSelection(int delta)
        {
            if (_app.Monitors.Count == 0) return;
            _chosen = Math.Clamp(_chosen + delta, 0, _app.Monitors.Count - 1);
            Identify(_chosen);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, _chosen);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _cards.Clear();
            int count = Math.Max(1, _app.Monitors.Count);
            int gap = LogicalToDeviceUnits(16);
            int width = (ClientSize.Width - gap * (count - 1)) / count;
            for (int i = 0; i < _app.Monitors.Count; i++)
                _cards.Add((_app.Monitors[i], new Rectangle(i * (width + gap), LogicalToDeviceUnits(8), width, Math.Min(ClientSize.Height - LogicalToDeviceUnits(16), LogicalToDeviceUnits(180)))));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            float s = DeviceDpi / 96f;
            using var numberFont = new Font(Theme.UiFont.FontFamily, 20f, FontStyle.Bold);
            for (int i = 0; i < _cards.Count; i++)
            {
                (DisplayMonitor m, Rectangle r) = _cards[i];
                bool chosen = i == _chosen;
                Draw.Fill(g, chosen ? p.Selected : i == _hover ? p.Raised : p.Surface, r, 12 * s);
                Draw.Stroke(g, chosen ? p.Accent : p.Border, chosen ? 2 * s : 1, r, 12 * s);
                if (chosen && Focused) Draw.Stroke(g, p.Text, 1, Rectangle.Inflate(r, -4, -4), 10 * s);

                // The screen, drawn to its aspect ratio.
                float maxW = r.Width * 0.55f, maxH = r.Height * 0.42f;
                float scale = Math.Min(maxW / m.Bounds.Width, maxH / m.Bounds.Height);
                var screen = new RectangleF(r.Left + (r.Width - m.Bounds.Width * scale) / 2, r.Top + 18 * s, m.Bounds.Width * scale, m.Bounds.Height * scale);
                Draw.Stroke(g, chosen ? p.Accent : p.TextFaint, 2 * s, screen, 5 * s);
                Draw.Text(g, m.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), numberFont, Rectangle.Round(screen), chosen ? p.Accent : p.TextDim, centre: true);

                var name = new Rectangle(r.Left, (int)(screen.Bottom + 10 * s), r.Width, (int)(22 * s));
                Draw.Text(g, m.ShortName, Theme.BoldFont, name, p.Text, centre: true);
                Draw.Text(g, $"{m.Bounds.Width} × {m.Bounds.Height}", Theme.SmallFont, name with { Y = name.Bottom }, p.TextDim, centre: true);
            }
        }

        private int Hit(Point point) => _cards.FindIndex(c => c.Bounds.Contains(point));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = Hit(e.Location);
            if (hover == _hover) return;
            _hover = hover;
            if (hover >= 0) Identify(hover);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int hit = Hit(e.Location);
            if (hit < 0) return;
            _chosen = hit;
            Focus();
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        /// <summary>FR-18: show this screen's number big on the screen itself for about a second.</summary>
        private void Identify(int index)
        {
            if (index < 0 || index >= _app.Monitors.Count) return;
            if (_identify is { IsDisposed: false }) _identify.Close();
            _identify = new IdentifyOverlay(_app.Monitors[index]);
            _identify.Show();
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new ScreensAccessible(this);

        protected override void Dispose(bool disposing)
        {
            if (disposing && _identify is { IsDisposed: false }) _identify.Close();
            base.Dispose(disposing);
        }

        private sealed class ScreensAccessible(ScreenChooser owner) : ControlAccessibleObject(owner)
        {
            public override int GetChildCount() => owner._app.Monitors.Count;
            public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner._app.Monitors.Count ? new ScreenAccessible(owner, index) : null;
            public override AccessibleObject? GetFocused() => GetChild(owner._chosen);
        }

        private sealed class ScreenAccessible(ScreenChooser owner, int index) : AccessibleObject
        {
            public override string? Name => owner._app.Monitors[index].Label;
            public override AccessibleRole Role => AccessibleRole.ListItem;
            public override AccessibleObject? Parent => owner.AccessibilityObject;
            public override AccessibleStates State => index == owner._chosen ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.Selectable;
            public override void DoDefaultAction()
            {
                owner._chosen = index;
                owner.Invalidate();
            }
        }
    }

    /// <summary>Big keycaps for the picker shortcut.</summary>
    private sealed class KeysPanel : Control
    {
        private readonly ScpApp _app;

        public KeysPanel(ScpApp app)
        {
            _app = app;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            IReadOnlyList<string> parts = _app.ActiveShortcut("picker")?.Parts ?? ["Tray icon"];
            AccessibleName = string.Join(" plus ", parts);
            float s = DeviceDpi / 96f;
            using var font = new Font(Theme.UiFont.FontFamily, 14f, FontStyle.Bold);
            float x = 0, h = 60 * s, y = 20 * s;
            for (int i = 0; i < parts.Count; i++)
            {
                float w = Math.Max(60 * s, TextRenderer.MeasureText(parts[i], font).Width + 28 * s);
                var r = new RectangleF(x, y, w, h);
                bool last = i == parts.Count - 1;
                Draw.Fill(g, p.Surface, r, 10 * s);
                Draw.Stroke(g, last ? p.Accent : p.Border, 1.5f * s, r, 10 * s);
                Draw.Text(g, parts[i], font, Rectangle.Round(r), last ? p.Accent : p.Text, centre: true);
                x += w;
                if (!last)
                {
                    Draw.Text(g, "+", font, new Rectangle((int)x, (int)y, (int)(32 * s), (int)h), p.TextFaint, centre: true);
                    x += 32 * s;
                }
            }
        }
    }
}

/// <summary>
/// FR-18: a big screen number shown briefly on that screen. It's deliberately visible on the
/// physical screen (that's its purpose) but, like all SCP UI, excluded from capture and click-through.
/// </summary>
internal sealed class IdentifyOverlay : OverlayForm
{
    private readonly int _number;
    private readonly System.Windows.Forms.Timer _close = new() { Interval = 1200 };
    private readonly Font _font;

    public IdentifyOverlay(DisplayMonitor monitor)
    {
        _number = monitor.Number;
        int size = (int)(220 * monitor.Scale);
        Bounds = new Rectangle(monitor.Bounds.Left + (monitor.Bounds.Width - size) / 2, monitor.Bounds.Top + (monitor.Bounds.Height - size) / 2, size, size);
        BackColor = Theme.Dark.Surface;
        _font = new Font(Theme.UiFont.FontFamily, (float)(96 * monitor.Scale), FontStyle.Bold, GraphicsUnit.Pixel);
        Region = new Region(Draw.Rounded(new RectangleF(0, 0, size, size), (float)(24 * monitor.Scale)));
        _close.Tick += (_, _) =>
        {
            _close.Stop();
            Close();
        };
        _close.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Dark.Surface);
        Draw.Stroke(e.Graphics, Theme.Dark.Accent, 4, new RectangleF(0, 0, Width, Height), Width * 0.11f);
        Draw.Text(e.Graphics, _number.ToString(System.Globalization.CultureInfo.InvariantCulture), _font, ClientRectangle, Theme.Dark.Accent, centre: true);
    }

    // A modeless form disposes itself when closed, which releases the timer and font below.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _close.Dispose();
            _font.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }
}
