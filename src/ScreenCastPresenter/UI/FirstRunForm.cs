using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>
/// First run (SRS 4.3), two steps: 1. pick the TV (hover a screen to see its number on it);
/// 2. where to find SCP: a drawing of the icon by the clock, the mouse first and the shortcut
/// second, with two switches that are on by default when they apply: keep the icon next to the
/// clock (Windows 11 hides new icons behind ^) and add SCP to the Start menu (a downloaded exe).
/// It ends with "Show something now", so the first presentation happens with the guide still in
/// mind instead of being left to remember a shortcut.
/// </summary>
internal sealed class FirstRunForm : ThemedForm
{
    private readonly ScpApp _app;
    private readonly Label _step = new() { AutoSize = true, Tag = "dim" };
    private readonly Label _heading = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label _body = new() { AutoSize = false, Height = 48, Tag = "dim", UseMnemonic = false };
    private readonly ScreenChooser _screens;
    private readonly FindItPanel _findIt;
    private readonly Label _tip = new() { AutoSize = false, Height = 40, Tag = "dim", UseMnemonic = false };
    private readonly PillButton _back = new("Back");
    private readonly PillButton _done = new("Done");
    private readonly PillButton _next = new("Next", PillKind.Primary);
    private readonly Font _headingFont = Theme.NewFont(15f, FontStyle.Bold);
    private int _page;

    public FirstRunForm(ScpApp app)
    {
        _app = app;
        _screens = new ScreenChooser(app);
        _findIt = new FindItPanel(app);
        Text = "Welcome to ScreenCast Presenter";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ClientSize = new Size(620, (int)(540 * Math.Max(1f, Theme.TextScale * 0.95f)));
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
        _findIt.Dock = DockStyle.Fill;

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_screens);
        content.Controls.Add(_findIt);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        foreach (PillButton b in new[] { _next, _done, _back })
        {
            b.Size = b.Measure();
            b.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(b);
        }

        layout.Controls.Add(_step, 0, 0);
        layout.Controls.Add(_heading, 0, 1);
        layout.Controls.Add(_body, 0, 2);
        layout.Controls.Add(content, 0, 3);
        layout.Controls.Add(_tip, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        _next.Click += (_, _) => Next();
        _done.Click += (_, _) => Finish(showNow: false);
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
        if (control is PillButton or ScreenChooser or TrayPicture or ToggleSwitch) return;
        control.BackColor = p.Background;
        control.ForeColor = control.Tag is "dim" ? p.TextDim : p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    private void ShowPage(int page)
    {
        _page = page;
        bool first = page == 0;
        _step.Text = first ? "STEP 1 OF 2" : "STEP 2 OF 2";
        _heading.Text = first ? "Where should your presentation appear?" : "Find ScreenCast Presenter by the clock";
        _body.Text = first
            ? (_app.Monitors.Count > 1 ? "Pick the TV or projector. Your own screen stays private." : "Only one screen is connected. Connect your TV now, or continue and choose it later from the icon by the clock.")
            : "Click this icon to show something, blank the TV, record and more. Right-click it for the full menu.";
        _tip.Text = first
            ? "Not sure which is which? Point at a screen here and its number appears on it."
            : _app.ActiveShortcut("picker") is { } key ? $"From any app you can also press {key}." : "";
        _screens.Visible = first;
        _findIt.Visible = !first;
        _back.Visible = !first;
        _done.Visible = !first;
        _next.Text = first ? "Next" : "Show something now";
        _next.Size = _next.Measure();
        if (first) _screens.Focus();
        else _next.Focus();
    }

    private void Next()
    {
        if (_page == 0)
        {
            if (_screens.Chosen is { } monitor) _app.SetOutputMonitor(monitor);
            ShowPage(1);
            return;
        }
        Finish(showNow: true);
    }

    private void Finish(bool showNow)
    {
        _app.CompleteFirstRun(showNow, install: _findIt.Install, pinIcon: _findIt.PinIcon);
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
            using var numberFont = Theme.NewFont(20f, FontStyle.Bold);
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

    /// <summary>Step 2: the drawing of the taskbar corner, and the two switches that apply on this PC.</summary>
    private sealed class FindItPanel : Panel
    {
        private readonly TrayPicture _picture = new() { Dock = DockStyle.Top };
        private readonly ToggleSwitch? _pin;
        private readonly ToggleSwitch? _install;

        public FindItPanel(ScpApp app)
        {
            Controls.Add(_picture);
            int y = 0;
            var rows = new Panel { Dock = DockStyle.Fill };
            // New icons start behind the ^ arrow on Windows 10 and 11. Only Windows 11 can be told
            // to show it (CanPinTrayIcon); on Windows 10 the drawing shows where to look instead.
            bool hiddenByWindows = app.TrayIconPinned != true;
            if (app.CanPinTrayIcon)
                _pin = AddRow(rows, ref y, "Keep the icon next to the clock", "Windows hides new icons behind the ^ arrow.");
            if (app.CanInstall)
                _install = AddRow(rows, ref y, "Add to the Start menu and desktop", "So you can always find it again. You can remove it in Settings › Apps.");
            Controls.Add(rows);
            rows.BringToFront();
            // The drawing shows where the icon will be once Done is pressed.
            _picture.HiddenBehindArrow = hiddenByWindows && !(_pin?.Checked ?? false);
            if (_pin is not null) _pin.CheckedChanged += (_, _) => _picture.HiddenBehindArrow = !_pin.Checked;
        }

        public bool PinIcon => _pin?.Checked ?? false;
        public bool Install => _install?.Checked ?? false;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            _picture.Height = LogicalToDeviceUnits(170);
            base.OnLayout(levent);
        }

        private ToggleSwitch AddRow(Panel rows, ref int y, string title, string detail)
        {
            var toggle = new ToggleSwitch { Checked = true, AccessibleName = title, AccessibleDescription = detail, Location = new Point(0, y + LogicalToDeviceUnits(4)) };
            var titleLabel = new Label { Text = title, AutoSize = true, UseMnemonic = false, Font = Theme.BoldFont, Location = new Point(LogicalToDeviceUnits(58), y) };
            var detailLabel = new Label { Text = detail, AutoSize = true, UseMnemonic = false, Tag = "dim", Location = new Point(LogicalToDeviceUnits(58), y + LogicalToDeviceUnits(20) + (int)(4 * Theme.TextScale)) };
            titleLabel.Click += (_, _) => toggle.Checked = !toggle.Checked;
            rows.Controls.Add(toggle);
            rows.Controls.Add(titleLabel);
            rows.Controls.Add(detailLabel);
            y += LogicalToDeviceUnits((int)(52 * Theme.TextScale));
            return toggle;
        }
    }

    /// <summary>
    /// The right-hand end of the taskbar, drawn: the ^ arrow, two status icons and the clock, with
    /// SCP's icon pointed out, either on the taskbar or in the ^ flyout where Windows hides it.
    /// </summary>
    private sealed class TrayPicture : Control
    {
        private bool _hidden;

        public TrayPicture()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.Graphic;
            UpdateAccessibleName();
        }

        /// <summary>The icon is in the ^ flyout rather than on the taskbar.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool HiddenBehindArrow
        {
            get => _hidden;
            set
            {
                _hidden = value;
                UpdateAccessibleName();
                Invalidate();
            }
        }

        private void UpdateAccessibleName() => AccessibleName = _hidden
            ? "The ScreenCast Presenter icon is behind the up arrow next to the clock, at the bottom right of the screen."
            : "The ScreenCast Presenter icon is on the taskbar, next to the clock, at the bottom right of the screen.";

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            float s = DeviceDpi / 96f;

            // The taskbar, as a strip along the bottom, fading in from the left.
            float barHeight = 48 * s;
            var bar = new RectangleF(0, Height - barHeight - 6 * s, Width, barHeight);
            Color barColour = p.IsDark ? Color.FromArgb(0x20, 0x20, 0x24) : Color.FromArgb(0xE9, 0xEA, 0xEE);
            Draw.Fill(g, barColour, bar, 10 * s);
            Draw.Stroke(g, p.Border, 1, bar, 10 * s);

            Color glyph = p.IsDark ? Color.FromArgb(0xD8, 0xD8, 0xDE) : Color.FromArgb(0x30, 0x30, 0x38);
            using var clockFont = Theme.NewFont(9f);
            float right = bar.Right - 16 * s;
            var clock = new RectangleF(right - 62 * s, bar.Top, 62 * s, barHeight);
            Draw.Text(g, "10:24", clockFont, Rectangle.Round(clock), glyph, centre: true);
            right = clock.Left - 10 * s;
            // Wi-Fi and sound, as simple shapes.
            for (int i = 0; i < 2; i++)
            {
                var icon = new RectangleF(right - 18 * s, bar.Top + (barHeight - 14 * s) / 2, 14 * s, 14 * s);
                using var pen = new Pen(glyph, 1.6f * s);
                if (i == 0) g.DrawArc(pen, icon, 200, 140);
                else g.DrawPolygon(pen, [new PointF(icon.Left, icon.Top + 4 * s), new PointF(icon.Left + 5 * s, icon.Top + 4 * s), new PointF(icon.Right - 2 * s, icon.Top), new PointF(icon.Right - 2 * s, icon.Bottom), new PointF(icon.Left + 5 * s, icon.Bottom - 4 * s), new PointF(icon.Left, icon.Bottom - 4 * s)]);
                right = icon.Left - 10 * s;
            }

            RectangleF ours;
            if (_hidden)
            {
                // The ^ arrow, with its flyout open above it and SCP's icon inside.
                var chevron = new RectangleF(right - 22 * s, bar.Top + (barHeight - 22 * s) / 2, 22 * s, 22 * s);
                Draw.Fill(g, p.Raised, chevron, 5 * s);
                using (var pen = new Pen(glyph, 1.8f * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    float cx = chevron.Left + chevron.Width / 2, cy = chevron.Top + chevron.Height / 2 + 2 * s;
                    g.DrawLines(pen, [new PointF(cx - 5 * s, cy), new PointF(cx, cy - 5 * s), new PointF(cx + 5 * s, cy)]);
                }
                var flyout = new RectangleF(chevron.Left + chevron.Width / 2 - 66 * s, bar.Top - 66 * s, 132 * s, 58 * s);
                Draw.Fill(g, barColour, flyout, 8 * s);
                Draw.Stroke(g, p.Border, 1, flyout, 8 * s);
                for (int i = 0; i < 4; i++)
                {
                    var slot = new RectangleF(flyout.Left + 12 * s + i * 29 * s, flyout.Top + 17 * s, 22 * s, 22 * s);
                    if (i == 1) continue;
                    Draw.Fill(g, Draw.LetterTint("abcd"[i].ToString()), RectangleF.Inflate(slot, -3 * s, -3 * s), 4 * s);
                }
                ours = new RectangleF(flyout.Left + 12 * s + 29 * s, flyout.Top + 17 * s, 22 * s, 22 * s);
            }
            else
            {
                ours = new RectangleF(right - 24 * s, bar.Top + (barHeight - 24 * s) / 2, 24 * s, 24 * s);
            }

            g.DrawIcon(AppIcon.Shared, Rectangle.Round(ours));
            Draw.Stroke(g, p.Outline, 3 * s, RectangleF.Inflate(ours, 6 * s, 6 * s), 9 * s);

            // A callout pointing at it.
            string label = _hidden ? "Click ^ then this icon" : "Click this icon";
            using var labelFont = Theme.NewFont(10f, FontStyle.Bold);
            Size size = TextRenderer.MeasureText(label, labelFont);
            var callout = new RectangleF(Math.Max(0, ours.Left + ours.Width / 2 - size.Width / 2 - 14 * s - 70 * s), Math.Max(0, ours.Top - 54 * s), size.Width + 28 * s, 32 * s);
            Draw.Fill(g, p.Accent, callout, 8 * s);
            Draw.Text(g, label, labelFont, Rectangle.Round(callout), p.OnAccent, centre: true);
            using var arrow = new Pen(p.Accent, 2.5f * s) { EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor };
            g.DrawLine(arrow, callout.Left + callout.Width * 0.75f, callout.Bottom, ours.Left + ours.Width / 2, ours.Top - 8 * s);
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
