using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Settings › Keyboard shortcuts and the Ctrl+Alt+K cheat sheet (FR-26): every action grouped,
/// searchable, and editable. Click a row (or select it and press Enter), then press the new keys;
/// Backspace switches it off, Esc cancels. Problems (used by another app, types a character) are
/// shown on the row.
/// </summary>
internal sealed class ShortcutsForm : ThemedForm
{
    private readonly ScpApp _app;
    private readonly SearchBox _search = new("Search shortcuts, e.g. blank or favourite") { Hint = "Type to filter" };
    private readonly CheckBox _enabled = new() { Text = "Shortcuts on", AutoSize = true };
    private readonly PillButton _reset = new("Reset to defaults");
    private readonly Label _title = new() { Text = "Keyboard shortcuts", AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true };
    private readonly ShortcutList _list;
    private readonly Font _titleFont = Theme.NewFont(15f, FontStyle.Bold);

    public ShortcutsForm(ScpApp app)
    {
        _app = app;
        _list = new ShortcutList(app);
        Text = "Keyboard shortcuts · ScreenCast Presenter";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 720);
        MinimumSize = new Size(560, 420);
        KeyPreview = true;
        ShowIcon = false;

        _title.Font = _titleFont;
        _enabled.Checked = app.Settings.ShortcutsEnabled;
        _enabled.CheckedChanged += (_, _) =>
        {
            _app.SetShortcutsEnabled(_enabled.Checked);
            Reload();
        };
        _reset.Size = _reset.Measure();
        _reset.Click += (_, _) =>
        {
            _app.ResetAllShortcuts();
            _enabled.Checked = true;
            Reload();
        };
        _search.Box.TextChanged += (_, _) => _list.Filter = _search.Box.Text;
        _list.Changed += (_, _) => UpdateSummary();

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20, 16, 20, 8), WrapContents = false };
        header.Controls.Add(_title);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20, 0, 20, 8), WrapContents = false };
        _enabled.Margin = new Padding(0, 10, 16, 0);
        tools.Controls.Add(_enabled);
        tools.Controls.Add(_reset);
        _summary.Margin = new Padding(16, 10, 0, 0);
        tools.Controls.Add(_summary);
        var searchRow = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(20, 8, 20, 12) };
        _search.Dock = DockStyle.Fill;
        searchRow.Controls.Add(_search);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 12, 16) };
        _list.Dock = DockStyle.Fill;
        listHost.Controls.Add(_list);

        Controls.Add(listHost);
        Controls.Add(searchRow);
        Controls.Add(tools);
        Controls.Add(header);
        ApplyPalette(Theme.Current);
        UpdateSummary();
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls) Recolour(c, palette);
        _summary.ForeColor = palette.TextDim;
    }

    private static void Recolour(Control control, Palette p)
    {
        if (control is SearchBox or PillButton or ShortcutList) return;
        control.BackColor = p.Background;
        control.ForeColor = p.Text;
        foreach (Control child in control.Controls) Recolour(child, p);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Box.Focus();
    }

    private void Reload()
    {
        _list.Reload();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var bindings = _app.Shortcuts;
        int taken = bindings.Count(b => b.Status is ShortcutStatus.Taken or ShortcutStatus.Invalid);
        _summary.Text = taken > 0 ? $"⚠ {taken} shortcut{(taken == 1 ? "" : "s")} need attention" :
            _app.ShortcutsPaused ? "Shortcuts are paused" : $"{bindings.Count(b => b.Status == ShortcutStatus.Active)} shortcuts active";
        _summary.ForeColor = taken > 0 ? Theme.Current.Warning : Theme.Current.TextDim;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_list.Recording)
        {
            _list.HandleRecordingKey(keyData);
            return true;
        }
        switch (keyData)
        {
            case Keys.Escape:
                if (_search.Box.TextLength > 0) _search.Box.Clear();
                else Close();
                return true;
            case Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown:
                _list.MoveSelection(keyData);
                return true;
            case Keys.Enter:
                _list.StartRecording();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _list.CancelRecording();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The grouped, scrollable list of shortcut rows.</summary>
    private sealed class ShortcutList : Control
    {
        private static readonly Dictionary<ShortcutGroup, string> GroupNames = new()
        {
            [ShortcutGroup.Presenting] = "Presenting",
            [ShortcutGroup.OnTheTv] = "On the TV",
            [ShortcutGroup.Favourites] = "Favourites",
            [ShortcutGroup.Crop] = "Crop",
            [ShortcutGroup.Recording] = "Recording and screenshots",
            [ShortcutGroup.OnMyScreen] = "On my screen",
        };

        private readonly ScpApp _app;
        private readonly List<(string? Header, ShortcutBinding? Binding)> _rows = [];
        private string _filter = "";
        private int _selected = -1;
        private int _scroll;
        private int _hover = -1;
        private string? _recordingId;

        public ShortcutList(ScpApp app)
        {
            _app = app;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Keyboard shortcuts";
            Theme.Changed += OnThemeChanged;
            Reload();
        }

        public event EventHandler? Changed;

        public bool Recording => _recordingId is not null;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Filter
        {
            get => _filter;
            set
            {
                _filter = value;
                Reload();
            }
        }

        private int Row => LogicalToDeviceUnits(46);

        public void Reload()
        {
            string? keep = SelectedBinding?.Action.Id;
            _rows.Clear();
            var bindings = PickerLayout.Filter(_app.Shortcuts, _filter, b => b.Action.Name, b => $"{b.Action.Keywords} {b.Hotkey}");
            foreach (var group in bindings.GroupBy(b => b.Action.Group).OrderBy(g => g.Key))
            {
                _rows.Add((GroupNames[group.Key], null));
                foreach (ShortcutBinding b in group) _rows.Add((null, b));
            }
            _selected = keep is null ? FirstBindingRow() : _rows.FindIndex(r => r.Binding?.Action.Id == keep);
            if (_selected < 0) _selected = FirstBindingRow();
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private int FirstBindingRow() => _rows.FindIndex(r => r.Binding is not null);

        private ShortcutBinding? SelectedBinding => _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Binding : null;

        private int MaxScroll => Math.Max(0, _rows.Count * Row - ClientSize.Height);

        public void MoveSelection(Keys key)
        {
            int step = key is Keys.Up or Keys.PageUp ? -1 : 1;
            int count = key is Keys.PageUp or Keys.PageDown ? Math.Max(1, ClientSize.Height / Row - 1) : 1;
            int index = _selected;
            for (int moved = 0; moved < count;)
            {
                int next = index + step;
                if (next < 0 || next >= _rows.Count) break;
                index = next;
                if (_rows[index].Binding is not null) moved++;
            }
            if (_rows.ElementAtOrDefault(index).Binding is null) return;
            SelectRow(index);
        }

        private void SelectRow(int index)
        {
            _selected = index;
            int top = index * Row;
            if (top < _scroll) _scroll = Math.Max(0, top - Row); // keep the group header visible
            if (top + Row > _scroll + ClientSize.Height) _scroll = top + Row - ClientSize.Height;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        }

        public void StartRecording()
        {
            if (SelectedBinding is not { } b || !b.Action.Available) return;
            _recordingId = b.Action.Id;
            _app.SuspendShortcuts(true); // otherwise pressing an existing shortcut would trigger it
            Invalidate();
        }

        public void CancelRecording()
        {
            if (_recordingId is null) return;
            _recordingId = null;
            _app.SuspendShortcuts(false);
            Invalidate();
        }

        public void HandleRecordingKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin) return; // wait for the real key
            string id = _recordingId!;
            if (keyData == Keys.Escape)
            {
                CancelRecording();
                return;
            }
            if (keyData == Keys.Back || keyData == Keys.Delete)
            {
                Finish(id, "");
                return;
            }
            HotkeyModifiers mods = HotkeyModifiers.None;
            if ((keyData & Keys.Control) != 0) mods |= HotkeyModifiers.Control;
            if ((keyData & Keys.Alt) != 0) mods |= HotkeyModifiers.Alt;
            if ((keyData & Keys.Shift) != 0) mods |= HotkeyModifiers.Shift;
            var hotkey = new Hotkey(mods, key);
            if (!hotkey.IsValid) return; // e.g. a letter without Ctrl/Alt: keep listening
            Finish(id, hotkey.ToString());
        }

        private void Finish(string id, string hotkey)
        {
            _recordingId = null;
            _app.SuspendShortcuts(false);
            _app.SetShortcut(id, hotkey);
            Reload();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            float s = DeviceDpi / 96f;
            int right = ClientSize.Width - LogicalToDeviceUnits(10);

            if (_rows.Count == 0) Draw.Text(g, "No shortcuts match.", Theme.UiFont, new Rectangle(0, 0, ClientSize.Width, Row), p.TextDim, centre: true);

            for (int i = 0; i < _rows.Count; i++)
            {
                var r = new Rectangle(0, i * Row - _scroll, right, Row);
                if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
                (string? header, ShortcutBinding? b) = _rows[i];
                if (header is not null)
                {
                    TextRenderer.DrawText(g, header.ToUpperInvariant(), Theme.SmallFont, new Rectangle(r.Left + (int)(4 * s), r.Top + (int)(14 * s), r.Width, r.Height - (int)(14 * s)), p.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    continue;
                }
                bool selected = i == _selected, recording = b!.Action.Id == _recordingId;
                if (selected || i == _hover) Draw.Fill(g, selected ? p.Selected : p.Raised, Rectangle.Inflate(r, 0, -2), 8 * s);

                (string? note, Color noteColour) = NoteFor(b, p);
                int textLeft = r.Left + (int)(14 * s);
                if (note is null)
                {
                    Draw.Text(g, b.Action.Name, Theme.UiFont, new Rectangle(textLeft, r.Top, r.Width / 2, r.Height), b.Action.Available ? p.Text : p.TextFaint);
                }
                else
                {
                    Draw.Text(g, b.Action.Name, Theme.UiFont, new Rectangle(textLeft, r.Top + (int)(4 * s), r.Width / 2, r.Height / 2), b.Action.Available ? p.Text : p.TextFaint);
                    Draw.Text(g, note, Theme.SmallFont, new Rectangle(textLeft, r.Top + r.Height / 2 - (int)(2 * s), r.Width / 2 + (int)(80 * s), r.Height / 2), noteColour);
                }

                int capsRight = r.Right - (int)(12 * s);
                if (recording)
                    Draw.KeyCaps(g, ["Press the new keys…  (Backspace = off, Esc = cancel)"], Theme.SmallFont, capsRight, r.Top + r.Height / 2, p, s, p.Accent, p.Accent);
                else if (b.Hotkey is { } h && b.Status is not ShortcutStatus.Off)
                    Draw.KeyCaps(g, h.Parts, Theme.SmallFont, capsRight, r.Top + r.Height / 2, p, s,
                        b.Status is ShortcutStatus.Taken or ShortcutStatus.Invalid ? p.Warning : null,
                        b.Status is ShortcutStatus.Active or ShortcutStatus.Pending ? null : p.TextFaint);
                else
                    Draw.KeyCaps(g, ["Not set"], Theme.SmallFont, capsRight, r.Top + r.Height / 2, p, s, null, p.TextFaint);
            }

            if (MaxScroll > 0)
            {
                int track = ClientSize.Height;
                int thumb = Math.Max(LogicalToDeviceUnits(30), track * track / (_rows.Count * Row));
                int top = (int)((long)(track - thumb) * _scroll / MaxScroll);
                Draw.Fill(g, p.Border, new Rectangle(ClientSize.Width - LogicalToDeviceUnits(5), top, LogicalToDeviceUnits(4), thumb), 2 * s);
            }
        }

        private static (string? Note, Color Colour) NoteFor(ShortcutBinding b, Palette p) => b.Status switch
        {
            ShortcutStatus.Taken or ShortcutStatus.Invalid => (b.Note, p.Warning),
            ShortcutStatus.Unavailable => ("Coming in a later update", p.TextFaint),
            ShortcutStatus.Paused => ("Paused", p.TextFaint),
            ShortcutStatus.Off when b.Note is not null => (b.Note, p.TextDim),
            _ => (b.Note, p.TextDim),
        };

        private int HitRow(int y) => (y + _scroll) / Row;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = HitRow(e.Y);
            if (hover >= _rows.Count || _rows[hover].Binding is null) hover = -1;
            if (hover == _hover) return;
            _hover = hover;
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
            int row = HitRow(e.Y);
            if (row >= _rows.Count || _rows[row].Binding is null) return;
            CancelRecording();
            SelectRow(row);
            StartRecording();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll = Math.Clamp(_scroll - e.Delta * Row * 3 / 120, 0, MaxScroll);
            Invalidate();
        }

        private void OnThemeChanged(object? sender, EventArgs e) => Invalidate();

        protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessible(this);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                CancelRecording();
            }
            base.Dispose(disposing);
        }

        private sealed class ListAccessible(ShortcutList list) : ControlAccessibleObject(list)
        {
            public override int GetChildCount() => list._rows.Count;
            public override AccessibleObject? GetChild(int index) => index >= 0 && index < list._rows.Count ? new RowAccessible(list, index) : null;
            public override AccessibleObject? GetFocused() => list._selected >= 0 ? GetChild(list._selected) : null;
        }

        private sealed class RowAccessible(ShortcutList list, int index) : AccessibleObject
        {
            public override string? Name
            {
                get
                {
                    if (index >= list._rows.Count) return null;
                    (string? header, ShortcutBinding? b) = list._rows[index];
                    if (header is not null) return header;
                    string keys = b!.Hotkey is { } h && b.Status != ShortcutStatus.Off ? h.ToString() : "not set";
                    return $"{b.Action.Name}, {keys}{(b.Note is { } n ? ", " + n : "")}";
                }
            }

            public override AccessibleRole Role => list._rows.ElementAtOrDefault(index).Header is not null ? AccessibleRole.StaticText : AccessibleRole.ListItem;
            public override AccessibleObject? Parent => list.AccessibilityObject;
            public override AccessibleStates State => index == list._selected ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.Selectable;
            public override string? DefaultAction => "Change";
            public override void DoDefaultAction()
            {
                list.SelectRow(index);
                list.StartRecording();
            }
        }
    }
}
