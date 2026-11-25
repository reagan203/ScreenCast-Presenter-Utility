namespace ScreenCastPresenter.UI;

/// <summary>What the meeting app should share: the floating output window, or the Zoom screen.</summary>
internal enum ShareGuideKind { Window, ZoomScreen }

/// <summary>
/// "Show me how" (FR-12): three numbered steps for sharing in Zoom, Teams or Meet, each with a
/// simple drawing of what to look for. The drawings are generic (a meeting toolbar, a share
/// picker with the right tile outlined in SCP's yellow), not any one app's design, so they stay
/// right as those apps change. Buttons offer the next step: choose what to show, or set up a
/// Zoom screen for sharing a whole screen. On the presenter's screen, excluded from capture.
/// </summary>
internal sealed class ShareGuideForm : ThemedForm
{
    private readonly Font _headingFont = Theme.NewFont(15f, FontStyle.Bold);
    private readonly StepsPicture _picture;

    public ShareGuideForm(ScpApp app, ShareGuideKind kind, string? zoomScreenName)
    {
        Text = "Share in Zoom or Teams · ScreenCast Presenter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        TopMost = true; // above the floating output window, which stays on top while presenting
        KeyPreview = true;
        ClientSize = new Size(660, (int)(560 * Math.Max(1f, Theme.TextScale * 0.92f)));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(28, 22, 28, 18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = kind == ShareGuideKind.Window ? "Share the window in your meeting" : "Share the Zoom screen in your meeting",
            Font = _headingFont,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
        };
        _picture = new StepsPicture(kind, zoomScreenName) { Dock = DockStyle.Fill };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        PillButton done = AddButton(buttons, "Done", PillKind.Primary, Close);
        if (app.Session.State == Core.SessionState.Off)
            AddButton(buttons, "Choose what to show", PillKind.Secondary, () =>
            {
                Close();
                app.OpenPicker();
            });
        if (kind == ShareGuideKind.Window && !app.Monitors.Any(m => m.IsVirtual))
            AddButton(buttons, "Share a whole screen instead…", PillKind.Ghost, () =>
            {
                Close();
                _ = app.SetUpZoomScreenAsync();
            });

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_picture, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = done;
        ApplyPalette(Theme.Current);
    }

    private PillButton AddButton(FlowLayoutPanel panel, string text, PillKind kind, Action click)
    {
        var button = new PillButton(text, kind) { Margin = new Padding(8, 0, 0, 0) };
        button.Size = button.Measure();
        button.Click += (_, _) => click();
        panel.Controls.Add(button);
        return button;
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is PillButton or StepsPicture)
        {
            control.Invalidate();
            return;
        }
        control.BackColor = p.Background;
        control.ForeColor = p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _headingFont.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The three steps: a number, the instruction, and a small drawing of what to look for.</summary>
    private sealed class StepsPicture : Control
    {
        private readonly ShareGuideKind _kind;
        private readonly string _target;
        private readonly string[] _steps;

        public StepsPicture(ShareGuideKind kind, string? zoomScreenName)
        {
            _kind = kind;
            _target = kind == ShareGuideKind.Window ? "ScreenCast Presenter output" : zoomScreenName is { Length: > 0 } name ? $"{name} (Zoom screen)" : "Zoom screen";
            _steps =
            [
                "In Zoom, Teams or Meet, click Share (or Share Screen).",
                kind == ShareGuideKind.Window
                    ? "Choose the window “ScreenCast Presenter output”."
                    : "Choose the screen whose preview shows your presentation, not your own screen.",
                kind == ShareGuideKind.Window
                    ? "Click Share. Keep that window open: it can sit behind others, but don't minimise it."
                    : "Click Share. Your own screen stays private, whatever you open on it.",
            ];
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = string.Join(" ", _steps.Select((step, i) => $"Step {i + 1}: {step}"));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            float s = DeviceDpi / 96f;
            int rowHeight = ClientSize.Height / 3;
            int textWidth = (int)(ClientSize.Width * 0.46f);
            using Font numberFont = Theme.NewFont(11f, FontStyle.Bold);
            for (int i = 0; i < 3; i++)
            {
                int top = i * rowHeight;
                var number = new RectangleF(0, top + 6 * s, 28 * s, 28 * s);
                Draw.Fill(g, p.Accent, number, number.Width / 2);
                Draw.Text(g, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), numberFont, Rectangle.Round(number), p.OnAccent, centre: true);
                var text = new Rectangle((int)(40 * s), top + (int)(4 * s), textWidth - (int)(40 * s), rowHeight - (int)(12 * s));
                TextRenderer.DrawText(g, _steps[i], Theme.UiFont, text, p.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Top);

                var art = new RectangleF(textWidth + 16 * s, top + 4 * s, ClientSize.Width - textWidth - 16 * s, rowHeight - 16 * s);
                switch (i)
                {
                    case 0: DrawToolbar(g, art, p, s); break;
                    case 1: DrawSharePicker(g, art, p, s); break;
                    default: DrawShared(g, art, p, s); break;
                }
            }
        }

        /// <summary>A meeting toolbar: a few round buttons and a highlighted "Share".</summary>
        private static void DrawToolbar(Graphics g, RectangleF area, Palette p, float s)
        {
            var bar = new RectangleF(area.Left, area.Top + area.Height / 2 - 22 * s, Math.Min(area.Width, 280 * s), 44 * s);
            Draw.Fill(g, Color.FromArgb(0x2A, 0x2A, 0x30), bar, 10 * s);
            float x = bar.Left + 14 * s;
            for (int b = 0; b < 3; b++)
            {
                Draw.Fill(g, Color.FromArgb(0x4A, 0x4A, 0x52), new RectangleF(x, bar.Top + 10 * s, 24 * s, 24 * s), 12 * s);
                x += 34 * s;
            }
            var share = new RectangleF(x + 6 * s, bar.Top + 8 * s, 84 * s, 28 * s);
            Draw.Fill(g, p.Accent, share, 8 * s);
            Draw.Stroke(g, p.Outline, 3 * s, RectangleF.Inflate(share, 5 * s, 5 * s), 11 * s);
            Draw.Text(g, "Share", Theme.BoldFont, Rectangle.Round(share), Color.FromArgb(0xF5, 0xF5, 0xF7), centre: true);
            Draw.Fill(g, Color.FromArgb(0xD9, 0x30, 0x36), new RectangleF(share.Right + 14 * s, bar.Top + 10 * s, 40 * s, 24 * s), 12 * s);
        }

        /// <summary>A share picker: three tiles, the one to choose outlined in yellow.</summary>
        private void DrawSharePicker(Graphics g, RectangleF area, Palette p, float s)
        {
            var panel = new RectangleF(area.Left, area.Top, Math.Min(area.Width, 300 * s), area.Height);
            Draw.Fill(g, p.Surface, panel, 10 * s);
            Draw.Stroke(g, p.Border, 1, panel, 10 * s);
            float gap = 10 * s, tileWidth = (panel.Width - gap * 4) / 3, tileHeight = tileWidth * 0.62f;
            float top = panel.Top + (panel.Height - tileHeight - 22 * s) / 2;
            string[] labels = [_kind == ShareGuideKind.Window ? "Screen 1" : "Your screen", _target, "Other window"];
            using Font tiny = Theme.NewFont(7.5f);
            for (int t = 0; t < 3; t++)
            {
                var tile = new RectangleF(panel.Left + gap + t * (tileWidth + gap), top, tileWidth, tileHeight);
                bool target = t == 1;
                Draw.Fill(g, target ? Theme.OutputBlack : p.Raised, tile, 5 * s);
                if (target)
                {
                    // A slide on black: what the audience will see.
                    Draw.Fill(g, Color.FromArgb(0xE6, 0xE6, 0xEA), RectangleF.Inflate(tile, -tile.Width * 0.18f, -tile.Height * 0.2f), 2 * s);
                    Draw.Stroke(g, p.Outline, 3 * s, RectangleF.Inflate(tile, 4 * s, 4 * s), 8 * s);
                }
                if (!target)
                {
                    // The other choices are labelled inside their tiles, so the one that matters
                    // gets the whole width below and is never cut short.
                    TextRenderer.DrawText(g, labels[t], tiny, Rectangle.Round(RectangleF.Inflate(tile, -3 * s, -3 * s)), p.TextFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    continue;
                }
                var label = new Rectangle((int)panel.Left, (int)(tile.Bottom + 8 * s), (int)panel.Width, (int)(20 * s));
                Draw.Text(g, labels[t], Theme.BoldFont, label, p.Text, centre: true);
            }
        }

        /// <summary>The audience's view: only the presentation, inside a "sharing" frame.</summary>
        private static void DrawShared(Graphics g, RectangleF area, Palette p, float s)
        {
            float height = Math.Min(area.Height, 96 * s);
            var screen = new RectangleF(area.Left, area.Top + (area.Height - height) / 2, height * 16 / 9, height);
            Draw.Fill(g, Theme.OutputBlack, screen, 6 * s);
            Draw.Fill(g, Color.FromArgb(0xE6, 0xE6, 0xEA), RectangleF.Inflate(screen, -screen.Width * 0.14f, -screen.Height * 0.16f), 3 * s);
            Draw.Stroke(g, Color.FromArgb(0x22, 0xC5, 0x5E), 3 * s, RectangleF.Inflate(screen, 3 * s, 3 * s), 8 * s);
            var caption = new Rectangle((int)(screen.Right + 12 * s), (int)screen.Top, (int)Math.Max(0, area.Right - screen.Right - 12 * s), (int)screen.Height);
            TextRenderer.DrawText(g, "What everyone sees", Theme.SmallFont, caption, p.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
