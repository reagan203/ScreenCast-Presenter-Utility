using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

internal enum PaletteKind { Action, Window, Help }

/// <summary>One thing the palette can run.</summary>
internal sealed record PaletteItem(string Title, string Detail, string? Shortcut, string Keywords, PaletteKind Kind, Func<Task> Run, char? Icon = null);

/// <summary>
/// Search and help (FR-25, the command palette): type the name of any action or window, or a
/// question, and press Enter. Everything SCP can do is reachable here, so no one has to remember
/// every shortcut. "How do I…?" topics come first; choosing one does the thing it asks about.
/// </summary>
internal sealed class CommandPalette : ThemedForm
{
    private const int RowHeight = 44, MaxRows = 9;

    private readonly ScpApp _app;
    private readonly DisplayMonitor _screen;
    private readonly SearchBox _search = new("Type what you want to do, like “share in Zoom”") { Hint = "Esc" };
    private readonly ResultList _list = new();
    private readonly Label _footer = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<PaletteItem> _items;
    private bool _closing;

    public CommandPalette(ScpApp app, DisplayMonitor screen)
    {
        _app = app;
        _screen = screen;
        _items = BuildItems(app);
        Text = "ScreenCast Presenter search and help";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Padding = new Padding(1);

        double s = screen.Scale;
        int width = Math.Min((int)(640 * s), screen.WorkArea.Width - (int)(32 * s));
        int height = (int)((60 + RowHeight * MaxRows + 16 + 40) * s);
        Bounds = new Rectangle(screen.WorkArea.Left + (screen.WorkArea.Width - width) / 2, screen.WorkArea.Top + screen.WorkArea.Height / 8, width, height);

        _search.Dock = DockStyle.Top;
        _search.Height = (int)(52 * s);
        _list.Dock = DockStyle.Fill;
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = (int)(38 * s);
        _footer.Padding = new Padding((int)(16 * s), 0, 0, 0);
        _footer.Text = $"↑ ↓ to move   ·   Enter to run   ·   {_app.ActiveShortcut("cheat-sheet")?.ToString() ?? "Keyboard shortcuts"} lists every shortcut";
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding((int)(8 * s)) };
        body.Controls.Add(_list);
        Controls.Add(body);
        Controls.Add(_footer);
        Controls.Add(_search);

        _search.Box.TextChanged += (_, _) => ApplyFilter();
        _list.ItemActivated += async (_, item) => await RunAsync(item);
        ApplyPalette(Theme.Current);
        ApplyFilter();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
            return cp;
        }
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls)
        {
            if (c is SearchBox) continue;
            c.BackColor = palette.Background;
        }
        _footer.BackColor = palette.Background;
        _footer.ForeColor = palette.TextFaint;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Box.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_closing) BeginInvoke(Close);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Current.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
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
                if (_list.Selected is { } item) _ = RunAsync(item);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ApplyFilter() =>
        _list.Items = PickerLayout.Filter(_items, _search.Box.Text, i => i.Title, i => $"{i.Detail} {i.Keywords}");

    private async Task RunAsync(PaletteItem item)
    {
        if (_closing) return;
        Close();
        await item.Run();
    }

    /// <summary>Every available action, plus "show" for each open window.</summary>
    private static List<PaletteItem> BuildItems(ScpApp app)
    {
        var items = new List<PaletteItem>();
        string? Key(string id) => app.ActiveShortcut(id)?.ToString();

        foreach (HelpTopic topic in HelpTopics.All)
            items.Add(new(topic.Question, topic.Answer, null, "how help " + topic.Keywords, PaletteKind.Help, () => app.RunCommandAsync(topic.Command), Icons.Help));

        foreach (ShortcutAction action in ShortcutCatalog.All.Where(a => a.Available && a.Group != ShortcutGroup.Favourites && a.Id != "palette"))
            items.Add(new(action.Name, "", Key(action.Id), action.Keywords, PaletteKind.Action, () => app.RunAsync(action.Id), Icons.ForCommand(action.Id)));

        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
        {
            int s = slot;
            if (app.Settings.Favourites[slot - 1] is { } f)
                items.Add(new($"Show favourite {slot}: {f.Title}", f.AppName, Key(ShortcutCatalog.ShowFavouriteId(slot)), "favourite", PaletteKind.Action,
                    () => app.RunAsync(ShortcutCatalog.ShowFavouriteId(s))));
            if (app.Session.Source is not null)
                items.Add(new($"Save what's showing as favourite {slot}", "", Key(ShortcutCatalog.SaveFavouriteId(slot)), "favourite save", PaletteKind.Action,
                    () => app.RunAsync(ShortcutCatalog.SaveFavouriteId(s))));
        }

        foreach (DisplayMonitor m in app.Monitors)
        {
            DisplayMonitor monitor = m;
            items.Add(new($"Show on {m.Label}", "", null, "output screen monitor tv display", PaletteKind.Action, () =>
            {
                app.SetOutputMonitor(monitor);
                return Task.CompletedTask;
            }));
        }
        foreach (int percent in new[] { 0, 10, 20, 30, 40 })
        {
            double value = percent / 100.0;
            items.Add(new(percent == 0 ? "Dim the TV: off" : $"Dim the TV: {percent}%", "", null, "brightness night", PaletteKind.Action, () =>
            {
                app.SetDim(value);
                return Task.CompletedTask;
            }));
        }
        foreach ((ThemeMode mode, string label) in new[] { (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"), (ThemeMode.System, "Match Windows") })
        {
            ThemeMode m = mode;
            items.Add(new($"Appearance: {label}", "", null, "theme mode light dark", PaletteKind.Action, () =>
            {
                app.SetTheme(m);
                return Task.CompletedTask;
            }));
        }
        items.Add(new(app.Settings.ShowCursor ? "Hide the mouse pointer on the TV" : "Show the mouse pointer on the TV", "", null, "cursor mouse pointer", PaletteKind.Action, () =>
        {
            app.SetShowCursor(!app.Settings.ShowCursor);
            return Task.CompletedTask;
        }));
        items.Add(new(app.Settings.PreferWgc ? "Windows: back to automatic (least memory)" : "Windows: always show the pointer and keep a minimised window's picture", "", null,
            "dwm wgc engine capture pointer minimised", PaletteKind.Action, () =>
        {
            app.SetCaptureEngine(!app.Settings.PreferWgc);
            return Task.CompletedTask;
        }));
        foreach ((float scale, string label) in TextSize.Choices)
        {
            float value = scale;
            items.Add(new($"Text size: {label}", Math.Abs(app.Settings.TextSize - scale) < 0.01 ? "current" : "", null, "text size bigger larger font read", PaletteKind.Action, () =>
            {
                app.SetTextSize(value);
                return Task.CompletedTask;
            }));
        }
        foreach (DisplayMonitor m in app.Monitors)
        {
            SourceWindow screen = SourceWindow.ForScreen(m);
            items.Add(new($"Show the whole of {m.Name} ({m.ShortName})", "Screen", null, "screen monitor desktop whole", PaletteKind.Action, () => app.PresentAsync(screen)));
        }
        items.Add(new("Settings", "", null, "preferences options configure startup folder", PaletteKind.Action, () =>
        {
            app.OpenSettings();
            return Task.CompletedTask;
        }));
        items.Add(new(app.StartWithWindows ? "Don't start with Windows" : "Start with Windows", "", null, "startup login boot autostart", PaletteKind.Action, () =>
        {
            app.SetStartWithWindows(!app.StartWithWindows);
            return Task.CompletedTask;
        }));

        // Outputs (FR-11, FR-12).
        items.Add(new("Show in a window", "Where it shows", null, "floating zoom share window output", PaletteKind.Action, () =>
        {
            app.SetOutputWindow();
            return Task.CompletedTask;
        }));
        items.Add(new("Show in Zoom or Teams", app.Monitors.Any(m => m.IsVirtual) ? "on the Zoom screen" : "in a window you share", null, "zoom teams meet share virtual display", PaletteKind.Action, app.ShowInZoomAsync));
        if (!app.Monitors.Any(m => m.IsVirtual))
            items.Add(new("Set up a Zoom screen", "share a whole screen without a second monitor", null, "zoom teams virtual display driver install", PaletteKind.Action, app.SetUpZoomScreenAsync));

        // Recording (FR-52/53): the record shortcut entry above toggles; these pick a source or the sound.
        items.Add(new("Record a window or screen…", "Recording", null, "record video webinar capture", PaletteKind.Action, () =>
        {
            app.OpenRecordPicker();
            return Task.CompletedTask;
        }));
        if (app.Session.State != SessionState.Off && !app.Recording.IsActive)
        {
            items.Add(new("Record what the TV shows", "Recording", null, "record video output presentation", PaletteKind.Action, () =>
            {
                app.RecordOutput();
                return Task.CompletedTask;
            }));
        }
        foreach ((Recording.MeetingSound sound, string label) in new[]
        {
            (Recording.MeetingSound.App, "Recording sound: only the recorded app"),
            (Recording.MeetingSound.All, "Recording sound: all computer sound"),
            (Recording.MeetingSound.None, "Recording sound: no computer sound"),
        })
        {
            Recording.MeetingSound s = sound;
            items.Add(new(label, app.Settings.MeetingSound == sound ? "current" : "", null, "audio meeting sound record", PaletteKind.Action, () =>
            {
                app.SetRecordSound(s);
                return Task.CompletedTask;
            }));
        }
        items.Add(new(app.Settings.RecordMicrophone ? "Recording: turn the microphone off" : "Recording: turn the microphone on", "", null, "mic microphone audio room record", PaletteKind.Action, () =>
        {
            app.SetRecordMicrophone(!app.Settings.RecordMicrophone);
            return Task.CompletedTask;
        }));
        items.Add(new("Open recordings folder", "", null, "videos files record", PaletteKind.Action, () =>
        {
            app.OpenRecordingsFolder();
            return Task.CompletedTask;
        }));
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Icon is not null || items[i].Kind != PaletteKind.Action) continue;
            string t = items[i].Title;
            char? icon = t switch
            {
                _ when t.StartsWith("Show favourite", StringComparison.Ordinal) || t.StartsWith("Save what", StringComparison.Ordinal) => Icons.Star,
                _ when t.StartsWith("Show on", StringComparison.Ordinal) || t.StartsWith("Show the whole", StringComparison.Ordinal) => Icons.Tv,
                _ when t.StartsWith("Dim", StringComparison.Ordinal) => Icons.Brightness,
                _ when t.StartsWith("Appearance", StringComparison.Ordinal) => Icons.Colour,
                _ when t.Contains("pointer", StringComparison.Ordinal) && !t.StartsWith("Windows", StringComparison.Ordinal) => Icons.Mouse,
                _ when t.StartsWith("Windows:", StringComparison.Ordinal) || t == "Show in a window" => Icons.Window,
                _ when t.StartsWith("Text size", StringComparison.Ordinal) => Icons.Font,
                _ when t == "Settings" => Icons.Settings,
                _ when t.Contains("start with Windows", StringComparison.OrdinalIgnoreCase) => Icons.Power,
                _ when t.StartsWith("Show in Zoom", StringComparison.Ordinal) => Icons.Share,
                _ when t.StartsWith("Set up a Zoom", StringComparison.Ordinal) => Icons.Download,
                _ when t.StartsWith("Record", StringComparison.Ordinal) => Icons.Record,
                _ when t.StartsWith("Recording sound", StringComparison.Ordinal) => Icons.Volume,
                _ when t.Contains("microphone", StringComparison.Ordinal) => Icons.Microphone,
                _ when t.StartsWith("Open recordings", StringComparison.Ordinal) => Icons.Folder,
                _ when t.StartsWith("Quit", StringComparison.Ordinal) => Icons.Power,
                _ => null,
            };
            if (icon is not null) items[i] = items[i] with { Icon = icon };
        }

        List<SourceWindow> windows = WindowEnumerator.GetPresentableWindows();
        foreach (SourceWindow w in windows)
        {
            SourceWindow window = w;
            items.Add(new($"Record “{w.Title}”", w.AppName, null, "record window webinar", PaletteKind.Window, () =>
            {
                app.RecordSource(window);
                return Task.CompletedTask;
            }));
        }
        items.Add(new("Quit ScreenCast Presenter", "", null, "exit close", PaletteKind.Action, () =>
        {
            app.RequestQuit();
            return Task.CompletedTask;
        }));

        foreach (SourceWindow w in windows)
        {
            SourceWindow window = w;
            items.Add(new($"Show “{w.Title}”", w.AppName, null, "window present", PaletteKind.Window, () => app.PresentAsync(window)));
        }
        return items;
    }

    /// <summary>Custom-drawn result rows with keyboard selection and Narrator support.</summary>
    private sealed class ResultList : Control
    {
        private IReadOnlyList<PaletteItem> _items = [];
        private int _selected;
        private int _top;
        private int _hover = -1;

        public ResultList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Results";
        }

        public event EventHandler<PaletteItem>? ItemActivated;

        public PaletteItem? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public IReadOnlyList<PaletteItem> Items
        {
            get => _items;
            set
            {
                _items = value;
                _selected = value.Count > 0 ? 0 : -1;
                _top = 0;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }
        }

        private int Row => LogicalToDeviceUnits(RowHeight);
        private int VisibleRows => Math.Max(1, ClientSize.Height / Row);

        public void MoveSelection(Keys key)
        {
            if (_items.Count == 0) return;
            int next = key switch
            {
                Keys.Up => _selected - 1,
                Keys.Down => _selected + 1,
                Keys.PageUp => _selected - VisibleRows,
                _ => _selected + VisibleRows,
            };
            Select(Math.Clamp(next, 0, _items.Count - 1));
        }

        private void Select(int index)
        {
            _selected = index;
            if (_selected < _top) _top = _selected;
            if (_selected >= _top + VisibleRows) _top = _selected - VisibleRows + 1;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.Clear(p.Background);
            Draw.HighQuality(g);
            if (_items.Count == 0)
            {
                Draw.Text(g, "Nothing matches. Try a different word.", Theme.UiFont, new Rectangle(0, 0, ClientSize.Width, Row), p.TextDim, centre: true);
                return;
            }
            float s = DeviceDpi / 96f;
            for (int i = _top; i < _items.Count && (i - _top) * Row < ClientSize.Height; i++)
            {
                PaletteItem item = _items[i];
                var r = new Rectangle(0, (i - _top) * Row, ClientSize.Width, Row);
                if (i == _selected || i == _hover) Draw.Fill(g, i == _selected ? p.Selected : p.Raised, r, 8 * s);

                var icon = new RectangleF(r.Left + 12 * s, r.Top + (r.Height - 20 * s) / 2, 20 * s, 20 * s);
                if (item.Kind == PaletteKind.Window) Draw.LetterBadge(g, item.Detail, icon, Theme.SmallFont);
                else if (item.Kind == PaletteKind.Help)
                {
                    Draw.Fill(g, p.Selected, RectangleF.Inflate(icon, 2 * s, 2 * s), icon.Width / 2 + 2 * s);
                    if (Icons.Available) Icons.Draw(g, Icons.Help, icon, p.Accent, 14 * s);
                    else Draw.Text(g, "?", Theme.BoldFont, Rectangle.Round(icon), p.Accent, centre: true);
                }
                else if (item.Icon is { } glyph && Icons.Available) Icons.Draw(g, glyph, icon, p.TextDim, 16 * s);
                else Draw.Stroke(g, p.TextDim, 1.6f * s, RectangleF.Inflate(icon, -4 * s, -4 * s), 3 * s);

                int right = r.Right - (int)(12 * s);
                if (item.Shortcut is { } shortcut)
                {
                    Size size = TextRenderer.MeasureText(shortcut, Theme.SmallFont);
                    TextRenderer.DrawText(g, shortcut, Theme.SmallFont, new Rectangle(right - size.Width, r.Top, size.Width, r.Height), p.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    right -= size.Width + (int)(12 * s);
                }
                int left = (int)(icon.Right + 12 * s);
                if (item.Kind == PaletteKind.Help)
                {
                    // The question, then its one-line answer, dimmer.
                    int question = Math.Min(TextRenderer.MeasureText(item.Title, Theme.BoldFont).Width, right - left);
                    Draw.Text(g, item.Title, Theme.BoldFont, new Rectangle(left, r.Top, question, r.Height), p.Text);
                    int answer = left + question + (int)(10 * s);
                    if (right - answer > 40 * s) Draw.Text(g, item.Detail, Theme.UiFont, new Rectangle(answer, r.Top, right - answer, r.Height), p.TextDim);
                    continue;
                }
                string text = item.Kind == PaletteKind.Window ? $"{item.Title}  ·  {item.Detail}" : item.Title;
                Draw.Text(g, text, Theme.UiFont, new Rectangle(left, r.Top, right - left, r.Height), p.Text);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = _top + e.Y / Row;
            hover = hover < _items.Count ? hover : -1;
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
            int index = _top + e.Y / Row;
            if (index < _items.Count) ItemActivated?.Invoke(this, _items[index]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _items.Count - VisibleRows));
            Invalidate();
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessible(this);

        private sealed class ListAccessible(ResultList list) : ControlAccessibleObject(list)
        {
            public override int GetChildCount() => list._items.Count;
            public override AccessibleObject? GetChild(int index) => index >= 0 && index < list._items.Count ? new ItemAccessible(list, index) : null;
            public override AccessibleObject? GetFocused() => list._selected >= 0 ? GetChild(list._selected) : null;
        }

        private sealed class ItemAccessible(ResultList list, int index) : AccessibleObject
        {
            public override string? Name => index < list._items.Count ? list._items[index].Title + (list._items[index].Kind == PaletteKind.Help ? ". " + list._items[index].Detail : "") + (list._items[index].Shortcut is { } k ? $", {k}" : "") : null;
            public override AccessibleRole Role => AccessibleRole.ListItem;
            public override AccessibleObject? Parent => list.AccessibilityObject;
            public override AccessibleStates State => AccessibleStates.Selectable | (index == list._selected ? AccessibleStates.Selected | AccessibleStates.Focused : 0);
            public override string? DefaultAction => "Run";
            public override void DoDefaultAction()
            {
                if (index < list._items.Count) list.ItemActivated?.Invoke(list, list._items[index]);
            }
        }
    }
}
