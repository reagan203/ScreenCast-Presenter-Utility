using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>The panel's top line: what's on, with a live preview of it.</summary>
internal sealed record QuickPanelHeader(BadgeDot Dot, string Title, string Detail, string? Recording, nint Thumbnail, Rectangle? Crop);

/// <summary>
/// The quick-actions panel (left-click on the tray icon), like Windows 11's Quick Settings: on
/// acrylic above the clock, a live preview of what's showing, big tiles from
/// <see cref="QuickPanelPlan"/> (switched-on tiles in the accent colour, recording in red), and a
/// footer with Stop showing, Search and help, and Settings. Toggles keep it open; anything that
/// opens something else closes it, as does Esc or clicking elsewhere. Every pixel is custom
/// drawn through <see cref="Backdrop.Paint"/>, so text stays crisp on the acrylic. Keyboard:
/// arrows, Tab, Enter/Space. Narrator reads each tile and whether it's on. Never on the TV,
/// excluded from capture like all SCP UI.
/// </summary>
internal sealed class QuickPanel : ThemedForm
{
    private const int LogicalWidth = 360, Pad = 16, Gap = 8, TileHeight = 80, HeaderHeight = 64, FooterHeight = 56, Columns = 3;

    private readonly List<(Rectangle Bounds, QuickTile Tile, bool Footer)> _hits = [];
    private readonly ToolTip _tip = new() { InitialDelay = 600, ShowAlways = true };
    private QuickPanelState _state = new();
    private QuickPanelHeader _header = new(BadgeDot.Hollow, "", "", null, 0, null);
    private PickerThumbnail? _thumbnail;
    private nint _thumbnailSource;
    private Rectangle _preview;
    private int _hover = -1;
    private int _focus = -1;
    private bool _keyboard;
    private bool _closing;

    public QuickPanel()
    {
        Text = "ScreenCast Presenter quick actions";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        // One blit per paint (Backdrop.Paint), so no double buffer: WinForms' buffer would drop the alpha.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Quick actions";
    }

    /// <summary>A tile or footer button was chosen; the app runs its command.</summary>
    public event EventHandler<QuickTile>? Chosen;

    protected override BackdropKind WindowBackdrop => BackdropKind.Acrylic;

    protected override Padding? GlassMargins => null; // the whole panel

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
            return cp;
        }
    }

    private int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    private float T => Math.Max(1f, Theme.TextScale);

    /// <summary>New content: resizes to fit, keeps the bottom edge where it was (it grows upwards, away from the taskbar).</summary>
    public void SetContent(QuickPanelState state, QuickPanelHeader header)
    {
        _state = state;
        _header = header;
        LayoutHits();
        Size size = new(Px(LogicalWidth * T), MeasureHeight());
        if (Size != size)
        {
            int bottom = Bottom;
            bool placed = Visible;
            Size = size;
            if (placed) Top = bottom - size.Height;
        }
        UpdateThumbnail();
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        Invalidate();
    }

    private int TileRows => (QuickPanelPlan.Tiles(_state).Count + Columns - 1) / Columns;

    private int MeasureHeight() =>
        Px(Pad) + Px(HeaderHeight * T) + Px(Gap * 1.5f) + TileRows * (Px(TileHeight * T) + Px(Gap)) + Px(FooterHeight * T);

    private void LayoutHits()
    {
        _hits.Clear();
        int width = Px(LogicalWidth * T);
        int tileWidth = (width - 2 * Px(Pad) - (Columns - 1) * Px(Gap)) / Columns;
        int top = Px(Pad) + Px(HeaderHeight * T) + Px(Gap * 1.5f);
        IReadOnlyList<QuickTile> tiles = QuickPanelPlan.Tiles(_state);
        for (int i = 0; i < tiles.Count; i++)
        {
            int row = i / Columns, column = i % Columns;
            var bounds = new Rectangle(Px(Pad) + column * (tileWidth + Px(Gap)), top + row * (Px(TileHeight * T) + Px(Gap)), tileWidth, Px(TileHeight * T));
            _hits.Add((bounds, tiles[i], false));
        }

        // Footer: Stop showing on the left, round icon buttons on the right.
        int footerTop = top + TileRows * (Px(TileHeight * T) + Px(Gap)) + Px(10);
        int right = width - Px(Pad);
        int button = Px(36 * T);
        foreach (QuickTile item in QuickPanelPlan.Footer(_state))
        {
            if (item.Command == "stop")
            {
                using Font font = Theme.NewFont(Theme.BodyPoints, FontStyle.Bold);
                int textWidth = TextRenderer.MeasureText(item.Label, font).Width;
                _hits.Add((new Rectangle(Px(Pad), footerTop, textWidth + Px(52), button), item, true));
                continue;
            }
        }
        foreach (QuickTile item in QuickPanelPlan.Footer(_state).Where(f => f.Command != "stop").Reverse())
        {
            right -= button;
            _hits.Add((new Rectangle(right, footerTop, button, button), item, true));
            right -= Px(6);
        }
        if (_focus >= _hits.Count) _focus = _hits.Count - 1;
        if (_hover >= _hits.Count) _hover = -1;
    }

    // ───────────────────────────── preview ─────────────────────────────

    private void UpdateThumbnail()
    {
        nint source = Visible && _header.Thumbnail != 0 && WindowEnumerator.IsAlive(_header.Thumbnail) ? _header.Thumbnail : 0;
        if (source != _thumbnailSource)
        {
            _thumbnail?.Dispose();
            _thumbnail = source != 0 && IsHandleCreated ? PickerThumbnail.TryCreate(Handle, source) : null;
            _thumbnailSource = _thumbnail is null ? 0 : source;
        }
        _preview = new Rectangle(Px(Pad), Px(Pad), Px(80 * T), Px(46 * T));
        if (_thumbnail is null) return;
        Size client = WindowEnumerator.ClientSize(_thumbnailSource);
        Rectangle part = _header.Crop ?? new Rectangle(Point.Empty, client);
        if (part.Width <= 0 || part.Height <= 0) return;
        Rectangle fit = ScreenCastPresenter.Capture.AspectFit.Fit(part.Size, Rectangle.Inflate(_preview, -Px(2), -Px(2)).Size);
        fit.Offset(_preview.Left + Px(2), _preview.Top + Px(2));
        _thumbnail.Update(fit, part);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) UpdateThumbnail();
        else
        {
            _thumbnail?.Dispose(); // live previews only exist while visible (SRS 4.1)
            _thumbnail = null;
            _thumbnailSource = 0;
        }
    }

    // ───────────────────────────── painting ─────────────────────────────

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint, in one blit.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Backdrop.Paint(e.Graphics, ClientSize, HasBackdrop ? null : p.Surface, g => DrawAll(g, p));
    }

    /// <summary>Tile fills on acrylic: translucent like Windows' own, solid without a backdrop.</summary>
    private (Color Fill, Color Border) TileColours(bool hover, Palette p) => HasBackdrop
        ? p.IsDark
            ? (Color.FromArgb(hover ? 34 : 20, 255, 255, 255), Color.FromArgb(24, 255, 255, 255))
            : (Color.FromArgb(hover ? 230 : 180, 255, 255, 255), Color.FromArgb(22, 0, 0, 0))
        : (hover ? p.Raised : p.Background, p.Border);

    private void DrawAll(Graphics g, Palette p)
    {
        float s = DeviceDpi / 96f;
        if (HasBackdrop)
        {
            // A light veil keeps text readable over busy wallpapers, as Windows' flyouts do.
            using var veil = new SolidBrush(Color.FromArgb(p.IsDark ? 70 : 90, p.Surface));
            g.FillRectangle(veil, ClientRectangle);
        }
        else
        {
            Draw.Stroke(g, p.Border, 1, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * s);
        }

        DrawHeader(g, p, s);
        using Font label = Theme.NewFont(Theme.CaptionPoints);
        using Font strong = Theme.NewFont(Theme.BodyPoints, FontStyle.Bold);
        for (int i = 0; i < _hits.Count; i++)
        {
            (Rectangle r, QuickTile tile, bool footer) = _hits[i];
            bool hover = i == _hover && tile.Enabled;
            if (footer) DrawFooterButton(g, p, s, r, tile, hover, strong);
            else DrawTile(g, p, s, r, tile, hover, label);
            if (i == _focus && _keyboard && Focused) Draw.Stroke(g, p.Text, 2 * s, RectangleF.Inflate(r, 3 * s, 3 * s), 9 * s);
        }

        // A hairline above the footer.
        int line = _hits.FirstOrDefault(h => h.Footer).Bounds.Top - Px(10);
        using var pen = new Pen(Color.FromArgb(p.IsDark ? 30 : 24, p.Text), 1);
        g.DrawLine(pen, Px(Pad), line, Width - Px(Pad), line);
    }

    private void DrawHeader(Graphics g, Palette p, float s)
    {
        Draw.Fill(g, Theme.OutputBlack, _preview, 6 * s);
        if (_thumbnail is null)
        {
            Icons.Draw(g, _header.Dot == BadgeDot.Recording ? Icons.Record : Icons.Tv, _preview, Color.FromArgb(150, 255, 255, 255), 20 * s * T);
        }

        float left = _preview.Right + 12 * s;
        float width = Width - left - Pad * s;
        using Font title = Theme.NewFont(Theme.BodyPoints, FontStyle.Bold);
        using Font detail = Theme.NewFont(Theme.CaptionPoints);
        float lineHeight = title.GetHeight(g);
        float top = _preview.Top + (_preview.Height - lineHeight * 2.1f) / 2;

        // Status dot: green while live, red while recording, hollow when idle.
        float dot = 8 * s;
        var dotRect = new RectangleF(left, top + (lineHeight - dot) / 2, dot, dot);
        Color dotColour = _header.Dot switch
        {
            BadgeDot.Recording => p.Recording,
            BadgeDot.Warning => p.Warning,
            BadgeDot.Hollow => p.TextDim,
            _ => Color.FromArgb(0x22, 0xC5, 0x5E),
        };
        if (_header.Dot == BadgeDot.Hollow)
        {
            using var ring = new Pen(dotColour, 1.6f * s);
            g.DrawEllipse(ring, RectangleF.Inflate(dotRect, -0.8f * s, -0.8f * s));
        }
        else
        {
            using var brush = new SolidBrush(dotColour);
            g.FillEllipse(brush, dotRect);
        }

        Backdrop.Text(g, _header.Title, title, new RectangleF(left + dot + 8 * s, top, width - dot - 8 * s, lineHeight), p.Text);
        string second = _header.Recording is { Length: > 0 } rec ? $"{rec}   {_header.Detail}" : _header.Detail;
        if (second.Length > 0)
        {
            var area = new RectangleF(left, top + lineHeight * 1.05f, width, lineHeight);
            if (_header.Recording is { Length: > 0 } recording)
            {
                SizeF size = g.MeasureString(recording + "   ", detail);
                Backdrop.Text(g, recording, detail, area, p.Recording);
                area = area with { X = area.X + size.Width, Width = Math.Max(0, area.Width - size.Width) };
                Backdrop.Text(g, _header.Detail, detail, area, p.TextDim);
            }
            else Backdrop.Text(g, second, detail, area, p.TextDim);
        }
    }

    private void DrawTile(Graphics g, Palette p, float s, Rectangle r, QuickTile tile, bool hover, Font label)
    {
        Color fill, fore;
        if (!tile.Enabled)
        {
            (fill, _) = TileColours(false, p);
            fore = p.TextFaint;
        }
        else if (tile.Tone == TileTone.Recording)
        {
            fill = hover ? ControlPaint.Light(p.Recording, 0.15f) : p.Recording;
            fore = Color.FromArgb(0xFA, 0xFA, 0xFB);
        }
        else if (tile.On || tile.Tone == TileTone.Accent)
        {
            fill = hover ? ControlPaint.Light(p.Accent, 0.12f) : p.Accent;
            fore = p.OnAccent;
        }
        else
        {
            (fill, Color border) = TileColours(hover, p);
            Draw.Fill(g, fill, r, 8 * s);
            Draw.Stroke(g, border, 1, r, 8 * s);
            fill = Color.Empty;
            fore = p.Text;
        }
        if (fill != Color.Empty) Draw.Fill(g, fill, r, 8 * s);

        var icon = new RectangleF(r.Left, r.Top + 12 * s * T, r.Width, 24 * s * T);
        Icons.Draw(g, tile.Icon, icon, fore, 20 * s * T);
        var text = new RectangleF(r.Left + 6 * s, icon.Bottom + 4 * s, r.Width - 12 * s, r.Bottom - icon.Bottom - 8 * s);
        using var brush = new SolidBrush(fore);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
        g.DrawString(tile.Label, label, brush, text, format);
    }

    private void DrawFooterButton(Graphics g, Palette p, float s, Rectangle r, QuickTile item, bool hover, Font strong)
    {
        if (item.Command == "stop")
        {
            // A pill with the stop icon, so the way out is always obvious.
            (Color fill, Color border) = TileColours(hover, p);
            Draw.Fill(g, fill, r, r.Height / 2f);
            Draw.Stroke(g, border, 1, r, r.Height / 2f);
            var icon = new RectangleF(r.Left + 12 * s, r.Top, 18 * s * T, r.Height);
            Icons.Draw(g, Icons.Stop, icon, p.Recording, 14 * s * T);
            Backdrop.Text(g, item.Label, strong, new RectangleF(icon.Right + 6 * s, r.Top, r.Right - icon.Right - 12 * s, r.Height), p.Text);
            return;
        }
        if (hover) Draw.Fill(g, TileColours(true, p).Fill, r, r.Height / 2f);
        Icons.Draw(g, item.Icon, r, p.Text, 16 * s * T);
    }

    // ───────────────────────────── input ─────────────────────────────

    private int Hit(Point point) => _hits.FindIndex(h => h.Bounds.Contains(point));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = Hit(e.Location);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover >= 0 && _hits[hover].Tile.Enabled ? Cursors.Hand : Cursors.Default;
        _tip.SetToolTip(this, hover >= 0 ? TipFor(_hits[hover].Tile) : null);
        Invalidate();
    }

    private string TipFor(QuickTile tile)
    {
        string text = tile.Description ?? tile.Label;
        return tile.Shortcut is { } id && ShortcutHint?.Invoke(id) is { } keys ? $"{text} ({keys})" : text;
    }

    /// <summary>The keys for an action, shown in tooltips (set by the app).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? ShortcutHint { get; set; }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int hit = Hit(e.Location);
        if (hit >= 0) Choose(_hits[hit].Tile);
    }

    private void Choose(QuickTile tile)
    {
        if (!tile.Enabled || _closing) return;
        if (!tile.KeepOpen) CloseQuietly();
        Chosen?.Invoke(this, tile);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int count = _hits.Count;
        if (count == 0) return base.ProcessCmdKey(ref msg, keyData);
        int tiles = _hits.Count(h => !h.Footer);
        int next = _focus;
        switch (keyData)
        {
            case Keys.Escape:
                CloseQuietly();
                return true;
            case Keys.Enter or Keys.Space:
                if (_focus >= 0) Choose(_hits[_focus].Tile);
                return true;
            case Keys.Tab: next = (_focus + 1) % count; break;
            case Keys.Shift | Keys.Tab: next = (_focus - 1 + count) % count; break;
            case Keys.Right: next = Math.Min(count - 1, _focus + 1); break;
            case Keys.Left: next = Math.Max(0, _focus - 1); break;
            case Keys.Down: next = _focus < 0 ? 0 : _focus + Columns < tiles ? _focus + Columns : tiles; break;
            case Keys.Up: next = _focus >= tiles ? Math.Max(0, tiles - 1) : Math.Max(0, _focus - Columns); break;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
        _keyboard = true;
        _focus = Math.Clamp(next, 0, count - 1);
        AccessibilityNotifyClients(AccessibleEvents.Focus, _focus);
        Invalidate();
        return true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateThumbnail();
        Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_closing) BeginInvoke(CloseQuietly); // clicked elsewhere, like Windows' own flyouts
    }

    /// <summary>When it last closed: a click on the tray icon right after closing it shouldn't reopen it.</summary>
    public DateTime ClosedAt { get; private set; }

    private void CloseQuietly()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        ClosedAt = DateTime.UtcNow;
        Close();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PanelAccessible(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class PanelAccessible(QuickPanel panel) : ControlAccessibleObject(panel)
    {
        public override int GetChildCount() => panel._hits.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < panel._hits.Count ? new TileAccessible(panel, index) : null;
        public override AccessibleObject? GetFocused() => panel._focus >= 0 ? GetChild(panel._focus) : null;
    }

    private sealed class TileAccessible(QuickPanel panel, int index) : AccessibleObject
    {
        private QuickTile? Tile => index < panel._hits.Count ? panel._hits[index].Tile : null;
        public override string? Name => Tile is { } t ? t.Label + (t.KeepOpen ? t.On ? ", on" : ", off" : "") : null;
        public override string? Description => Tile?.Description;
        public override AccessibleRole Role => Tile is { KeepOpen: true } ? AccessibleRole.CheckButton : AccessibleRole.PushButton;
        public override AccessibleObject? Parent => panel.AccessibilityObject;
        public override Rectangle Bounds => index < panel._hits.Count ? panel.RectangleToScreen(panel._hits[index].Bounds) : Rectangle.Empty;
        public override AccessibleStates State =>
            AccessibleStates.Focusable
            | (index == panel._focus ? AccessibleStates.Focused : 0)
            | (Tile is { On: true, KeepOpen: true } ? AccessibleStates.Checked : 0)
            | (Tile is { Enabled: false } ? AccessibleStates.Unavailable : 0);
        public override string? DefaultAction => "Press";
        public override void DoDefaultAction()
        {
            if (Tile is { } t) panel.Choose(t);
        }
    }
}
