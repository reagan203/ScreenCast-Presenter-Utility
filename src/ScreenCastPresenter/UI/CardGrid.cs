using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The picker's grid of window cards with live DWM thumbnails (FR-01). Thumbnails exist only for
/// cards currently on screen and are cropped to the grid while scrolling. Keyboard: arrows, Page
/// Up/Down, Home/End (driven by the picker). Narrator: each card is a list item.
/// </summary>
internal sealed class CardGrid : Control
{
    private const int MinCardWidth = 190, Gap = 14, Pad = 6, LabelHeight = 46;

    private readonly Dictionary<nint, PickerThumbnail> _thumbnails = [];
    private IReadOnlyList<SourceWindow> _items = [];
    private List<Rectangle> _cards = [];
    private int _selected = -1;
    private int _hover = -1;
    private int _scroll;
    private int _columns = 1;
    private int _contentHeight;
    private Font? _titleFont, _appFont, _chipFont, _letterFont;

    public CardGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false; // focus stays in the search box; arrows are forwarded by the picker
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Windows";
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>Double-click or Enter on a card.</summary>
    public event EventHandler<SourceWindow>? CardActivated;
    public event EventHandler? SelectionChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    public SourceWindow? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public int Count => _items.Count;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<SourceWindow> Items
    {
        get => _items;
        set
        {
            nint keep = Selected?.Handle ?? 0;
            _items = value;
            int index = keep == 0 ? -1 : value.ToList().FindIndex(w => w.Handle == keep);
            _selected = index >= 0 ? index : value.Count > 0 ? 0 : -1;
            _hover = -1;
            // Drop thumbnails of windows that are gone.
            foreach (nint gone in _thumbnails.Keys.Where(h => !value.Any(w => w.Handle == h)).ToList()) ReleaseThumbnail(gone);
            Relayout();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        }
    }

    public void MoveSelection(Keys key)
    {
        int rows = Math.Max(1, ClientSize.Height / Math.Max(1, CardHeight() + Scaled(Gap)));
        Select(PickerLayout.Move(_selected, _items.Count, _columns, rows, key));
    }

    public void ActivateSelected()
    {
        if (Selected is { } w) CardActivated?.Invoke(this, w);
    }

    private void Select(int index)
    {
        if (index == _selected || index < 0 || index >= _items.Count) return;
        _selected = index;
        _scroll = Math.Clamp(PickerLayout.ScrollToShow(_cards[index], _scroll, ClientSize.Height), 0, MaxScroll);
        UpdateThumbnails();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
    }

    private int Scaled(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Caption space grows with Settings › Appearance › Text size, so larger text isn't clipped.</summary>
    private int TextScaled(int logical) => LogicalToDeviceUnits((int)Math.Round(logical * Theme.TextScale));

    private int CardHeight()
    {
        int width = CardWidth();
        int thumbHeight = (width - 2 * Scaled(Pad)) * 10 / 16;
        return Scaled(Pad) * 2 + thumbHeight + TextScaled(LabelHeight);
    }

    private int CardWidth() => Math.Max(1, (ContentWidth - Scaled(Gap) * (_columns - 1)) / _columns);

    private int ContentWidth => Math.Max(1, ClientSize.Width - Scaled(10)); // room for the scrollbar

    private int MaxScroll => Math.Max(0, _contentHeight - ClientSize.Height);

    private void Relayout()
    {
        _columns = PickerLayout.Columns(ContentWidth, Scaled(MinCardWidth), Scaled(Gap));
        int height = CardHeight();
        _cards = PickerLayout.Grid(_items.Count, ContentWidth, _columns, Scaled(Gap), height);
        _contentHeight = PickerLayout.ContentHeight(_items.Count, _columns, Scaled(Gap), height);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        UpdateThumbnails();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateThumbnails();
    }

    private Rectangle ThumbArea(Rectangle card) =>
        new(card.Left + Scaled(Pad), card.Top + Scaled(Pad), card.Width - 2 * Scaled(Pad), (card.Width - 2 * Scaled(Pad)) * 10 / 16);

    /// <summary>
    /// Creates thumbnails for visible cards, positions them (cropped to the grid), and releases
    /// the ones that scrolled away. Minimised windows get no thumbnail: DWM can't draw them.
    /// </summary>
    public void UpdateThumbnails()
    {
        if (!IsHandleCreated || FindForm() is not { IsHandleCreated: true } form) return;
        Point offset = form.PointToClient(PointToScreen(Point.Empty));
        var viewport = new Rectangle(offset, ClientSize);

        for (int i = 0; i < _items.Count; i++)
        {
            SourceWindow w = _items[i];
            Rectangle area = ThumbArea(_cards[i]);
            area.Offset(offset.X, offset.Y - _scroll);
            Size source = w.IsMinimized ? Size.Empty : WindowEnumerator.ClientSize(w.Handle);
            Rectangle fit = AspectFit.Fit(source, area.Size);
            fit.Offset(area.Location);

            (Rectangle Destination, Rectangle Source)? clip = PickerLayout.Clip(fit, viewport, source);
            if (clip is null)
            {
                ReleaseThumbnail(w.Handle);
                continue;
            }
            if (!_thumbnails.TryGetValue(w.Handle, out PickerThumbnail? thumb))
            {
                thumb = PickerThumbnail.TryCreate(form.Handle, w.Handle);
                if (thumb is null) continue;
                _thumbnails[w.Handle] = thumb;
            }
            thumb.Update(clip.Value.Destination, clip.Value.Source);
        }
    }

    private void ReleaseThumbnail(nint handle)
    {
        if (_thumbnails.Remove(handle, out PickerThumbnail? thumb)) thumb.Dispose();
    }

    /// <summary>Drops every live thumbnail (the picker is hiding).</summary>
    public void ReleaseThumbnails() => ReleaseAllThumbnails();

    private void ReleaseAllThumbnails()
    {
        foreach (PickerThumbnail t in _thumbnails.Values) t.Dispose();
        _thumbnails.Clear();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(p.Background);
        Draw.HighQuality(g);
        EnsureFonts();

        if (_items.Count == 0)
        {
            var box = new Rectangle(0, 0, ContentWidth, Math.Min(ClientSize.Height, Scaled(140)));
            using var pen = new Pen(p.Border) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, Rectangle.Inflate(box, -1, -1));
            Draw.Text(g, EmptyText, Theme.UiFont, box, p.TextDim, centre: true);
            return;
        }

        float radius = Scaled(12);
        for (int i = 0; i < _items.Count; i++)
        {
            Rectangle card = _cards[i];
            card.Offset(0, -_scroll);
            if (card.Bottom < 0 || card.Top > ClientSize.Height) continue;
            SourceWindow w = _items[i];
            bool selected = i == _selected, hover = i == _hover;

            Draw.Fill(g, selected ? p.Selected : hover ? p.Raised : p.Surface, card, radius);
            if (selected) Draw.Stroke(g, p.Accent, Scaled(2), card, radius);

            Rectangle area = ThumbArea(card);
            Draw.Fill(g, p.Raised, area, Scaled(7));
            if (w.IsMinimized)
            {
                Size chip = TextRenderer.MeasureText("Minimised", _chipFont);
                var chipRect = new Rectangle(area.Left + (area.Width - chip.Width - Scaled(16)) / 2, area.Top + (area.Height - Scaled(24)) / 2, chip.Width + Scaled(16), Scaled(24));
                Draw.Fill(g, p.Surface, chipRect, chipRect.Height / 2f);
                Draw.Stroke(g, p.Border, 1, chipRect, chipRect.Height / 2f);
                Draw.Text(g, "Minimised", _chipFont!, chipRect, p.Text, centre: true);
            }

            int labelTop = area.Bottom + Scaled(8);
            var badge = new RectangleF(card.Left + Scaled(10), labelTop + TextScaled(8), Scaled(20), Scaled(20));
            Draw.LetterBadge(g, w.AppName, badge, _letterFont!);
            int textLeft = (int)badge.Right + Scaled(8);
            var titleRect = new Rectangle(textLeft, labelTop, card.Right - textLeft - Scaled(8), TextScaled(20));
            var appRect = new Rectangle(textLeft, labelTop + TextScaled(19), titleRect.Width, TextScaled(18));
            Draw.Text(g, w.Title, _titleFont!, titleRect, p.Text);
            Draw.Text(g, w.AppName, _appFont!, appRect, p.TextDim);
        }

        // Thin scrollbar.
        if (MaxScroll > 0)
        {
            int track = ClientSize.Height;
            int thumb = Math.Max(Scaled(30), track * track / Math.Max(1, _contentHeight));
            int top = (int)((long)(track - thumb) * _scroll / MaxScroll);
            Draw.Fill(g, p.Border, new Rectangle(ClientSize.Width - Scaled(5), top, Scaled(4), thumb), Scaled(2));
        }
    }

    private void EnsureFonts()
    {
        if (_titleFont is not null) return;
        _titleFont = Theme.NewFont(9.75f, FontStyle.Bold);
        _appFont = Theme.NewFont(9f);
        _chipFont = Theme.NewFont(9f);
        _letterFont = Theme.NewFont(8.25f, FontStyle.Bold);
    }

    private int HitTest(Point p)
    {
        var content = new Point(p.X, p.Y + _scroll);
        return _cards.FindIndex(c => c.Contains(content));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = HitTest(e.Location);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int hit = HitTest(e.Location);
        if (hit >= 0) Select(hit);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitTest(e.Location) >= 0) ActivateSelected();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int next = Math.Clamp(_scroll - e.Delta * Scaled(90) / 120, 0, MaxScroll);
        if (next == _scroll) return;
        _scroll = next;
        UpdateThumbnails();
        Invalidate();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Invalidate();

    protected override AccessibleObject CreateAccessibilityInstance() => new GridAccessible(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            ReleaseAllThumbnails();
            _titleFont?.Dispose();
            _appFont?.Dispose();
            _chipFont?.Dispose();
            _letterFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Narrator: the grid is a list; each card a selectable item named "title, app".</summary>
    private sealed class GridAccessible(CardGrid grid) : ControlAccessibleObject(grid)
    {
        public override int GetChildCount() => grid._items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < grid._items.Count ? new CardAccessible(grid, index) : null;
        public override AccessibleObject? GetFocused() => grid._selected >= 0 ? GetChild(grid._selected) : null;
        public override AccessibleObject? GetSelected() => GetFocused();
    }

    private sealed class CardAccessible(CardGrid grid, int index) : AccessibleObject
    {
        private SourceWindow? Window => index < grid._items.Count ? grid._items[index] : null;
        public override string? Name => Window is { } w ? $"{w.Title}, {w.AppName}{(w.IsMinimized ? ", minimised" : "")}" : null;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject? Parent => grid.AccessibilityObject;
        public override string? DefaultAction => "Show";

        public override AccessibleStates State =>
            AccessibleStates.Selectable | AccessibleStates.Focusable |
            (index == grid._selected ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.None);

        public override Rectangle Bounds
        {
            get
            {
                if (index >= grid._cards.Count) return Rectangle.Empty;
                Rectangle r = grid._cards[index];
                r.Offset(0, -grid._scroll);
                return grid.RectangleToScreen(r);
            }
        }

        public override void DoDefaultAction()
        {
            grid.Select(index);
            grid.ActivateSelected();
        }

        public override void Select(AccessibleSelection flags) => grid.Select(index);
    }
}
