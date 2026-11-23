using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Settings (SRS 6.1): sections on the left, plain-language rows on the right. Every change
/// applies and saves immediately; there is no OK/Apply. Excluded from capture like every SCP
/// window, keyboard navigable (Tab through rows, Space toggles, arrows in the section list).
/// </summary>
internal sealed class SettingsForm : ThemedForm
{
    public enum Section { General, Output, Recording, Appearance, Advanced }

    private readonly ScpApp _app;
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel _rows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
    private readonly Font _titleFont = new(Theme.UiFont.FontFamily, 15f, FontStyle.Bold);
    private readonly Font _rowFont = new(Theme.UiFont.FontFamily, 10f, FontStyle.Bold);
    private Section _section;

    public SettingsForm(ScpApp app, Section section = Section.General)
    {
        _app = app;
        _section = section;
        Text = "Settings · ScreenCast Presenter";
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(640, 460);
        KeyPreview = true;

        _nav.Width = 200;
        _nav.Padding = new Padding(12, 16, 12, 12);
        foreach (Section s in Enum.GetValues<Section>())
        {
            var button = new NavButton(Label(s)) { Tag = s, Width = 176, Height = 40, Margin = new Padding(0, 0, 0, 4) };
            button.Click += (_, _) => Show(s);
            _nav.Controls.Add(button);
        }
        _content.Padding = new Padding(28, 20, 28, 20);
        _content.Controls.Add(_rows);
        Controls.Add(_content);
        Controls.Add(_nav);
        _content.HandleCreated += (_, _) => DwmHelpers.SetDarkScrollbars((Windows.Win32.Foundation.HWND)_content.Handle, Theme.Current.IsDark);
        _app.SettingsChanged += OnSettingsChanged;
        Show(section);
    }

    private static string Label(Section s) => s switch
    {
        Section.Output => "Output",
        Section.Recording => "Recording & screenshots",
        Section.Appearance => "Appearance",
        Section.Advanced => "Advanced",
        _ => "General",
    };

    /// <summary>Switches section (also used to open Settings at a given section).</summary>
    public void Show(Section section)
    {
        _section = section;
        foreach (NavButton b in _nav.Controls.OfType<NavButton>()) b.Selected = (Section)b.Tag! == section;
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        // Something changed elsewhere (tray, shortcut): show the current values, keeping focus where it is.
        if (!ContainsFocus || ActiveControl is not TextBox) Rebuild();
    }

    private void Rebuild()
    {
        _rows.SuspendLayout();
        foreach (Control old in _rows.Controls.Cast<Control>().ToList()) old.Dispose(); // Clear() alone would leak handles
        _rows.Controls.Add(new Label { Text = Label(_section), Font = _titleFont, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 16) });
        switch (_section)
        {
            case Section.General: BuildGeneral(); break;
            case Section.Output: BuildOutput(); break;
            case Section.Recording: BuildRecording(); break;
            case Section.Appearance: BuildAppearance(); break;
            case Section.Advanced: BuildAdvanced(); break;
        }
        _rows.ResumeLayout();
        ApplyPalette(Theme.Current);
        LayoutRows();
    }

    // ───────────────────────────── sections ─────────────────────────────

    private void BuildGeneral()
    {
        AppSettings s = _app.Settings;
        bool startup = _app.StartWithWindows;
        AddToggle("Start with Windows", _app.StartupDisabledInTaskManager
                ? "Switched off in Task Manager › Startup apps. Switch it on there too."
                : "SCP waits in the tray, ready for your shortcuts.",
            startup || _app.StartupDisabledInTaskManager, on => _app.SetStartWithWindows(on));
        AddToggle("Keyboard shortcuts", "Ctrl+Alt shortcuts work from any app.", s.ShortcutsEnabled, on => _app.SetShortcutsEnabled(on),
            ("Edit shortcuts…", _app.OpenShortcuts));
        AddToggle("Yellow outline", "Shows on your screen what the audience sees. Never on the TV.", s.ShowOutline, on => _app.SetShowOutline(on));
        AddToggle("Presenting badge", "The small status pill at the top of your screen.", s.ShowBadge, on => _app.SetShowBadge(on));
        AddToggle("Mouse pointer on the TV", "Show your pointer in what you present.", s.ShowCursor, on => _app.SetShowCursor(on));
    }

    private void BuildOutput()
    {
        AppSettings s = _app.Settings;
        OutputOption current = _app.CurrentOutputOption;
        var choices = _app.Monitors.Select(m => (m.IsVirtual ? $"Zoom screen (Display {m.Number})" : m.IsTv ? $"TV (Display {m.Number})" : $"Display {m.Number}",
            !current.IsWindow && current.Screen is not null && m.IsSameScreen(current.Screen), (Action)(() => _app.SetOutputMonitor(m)))).ToList();
        choices.Add(("A window", current.IsWindow, _app.SetOutputWindow));
        AddChoice("Show on", "Fullscreen on a screen, or a window you can move and share in Zoom.", choices);
        AddButton("Sharing in Zoom", _app.Monitors.Any(m => m.IsVirtual)
                ? "A Zoom screen is connected: share that screen in Zoom and your own screen stays private."
                : "Share the window “ScreenCast Presenter output” in Zoom, or install a virtual display driver to get a Zoom screen.",
            "Show in Zoom", () => _ = _app.ShowInZoomAsync());
        AddChoice("Dim the TV", "Softens a bright TV in a dark room.",
            [.. new[] { 0, 10, 20, 30, 40 }.Select(p => (p == 0 ? "Off" : $"{p}%", Math.Abs(s.Dim - p / 100.0) < 0.001, (Action)(() => _app.SetDim(p / 100.0))))]);
        AddText("Blank screen message", "Shown on the TV with Ctrl+Alt+Shift+B.", s.BlankMessage, AppSettings.MaxMessageLength, _app.SetBlankMessage);
    }

    private void BuildRecording()
    {
        AppSettings s = _app.Settings;
        AddFolder("Recordings folder", s.RecordingFolder ?? SavedFiles.DefaultRecordingFolder, s.RecordingFolder is null, _app.SetRecordingFolder);
        AddFolder("Screenshots folder", s.ScreenshotFolder ?? SavedFiles.DefaultScreenshotFolder, s.ScreenshotFolder is null, _app.SetScreenshotFolder);
        MeetingSound sound = s.MeetingSound;
        AddChoice("Computer sound in recordings", "“This app only” leaves out your own notifications and music (Windows 11).",
        [
            ("This app only", sound == MeetingSound.App, () => _app.SetRecordSound(MeetingSound.App)),
            ("All computer sound", sound == MeetingSound.All, () => _app.SetRecordSound(MeetingSound.All)),
            ("None", sound == MeetingSound.None, () => _app.SetRecordSound(MeetingSound.None)),
        ]);
        AddToggle("Microphone", "Also record people in the room.", s.RecordMicrophone, on => _app.SetRecordMicrophone(on));
    }

    private void BuildAppearance()
    {
        ThemeMode mode = _app.Settings.ThemeMode;
        AddChoice("Theme", "SCP's own windows. The TV is always black.",
        [
            ("Light", mode == ThemeMode.Light, () => _app.SetTheme(ThemeMode.Light)),
            ("Dark", mode == ThemeMode.Dark, () => _app.SetTheme(ThemeMode.Dark)),
            ("Match Windows", mode == ThemeMode.System, () => _app.SetTheme(ThemeMode.System)),
        ]);
    }

    private void BuildAdvanced()
    {
        bool wgc = _app.Settings.PreferWgc;
        string inUse = _app.Session.EngineName is { } e ? $"In use now: {e}." : "Nothing is being shown.";
        AddChoice("Capture engine", $"Automatic uses the least memory. Windows Graphics Capture shows the pointer and keeps a minimised window's last picture. {inUse}",
        [
            ("Automatic", !wgc, () => _app.SetCaptureEngine(false)),
            ("Windows Graphics Capture", wgc, () => _app.SetCaptureEngine(true)),
        ]);
        AddButton("Settings file", _app.SettingsFilePath, "Show in folder", () => _app.ShowInFolder(_app.SettingsFilePath));
        AddInfo("Privacy", "SCP makes no network connections and sends no data anywhere. Recordings and screenshots stay on this PC.");
        AddInfo("Version", _app.VersionText);
    }

    // ───────────────────────────── rows ─────────────────────────────

    private Row NewRow(string title, string description)
    {
        var row = new Row(title, description, _rowFont);
        _rows.Controls.Add(row);
        return row;
    }

    private void AddToggle(string title, string description, bool value, Action<bool> set, (string Text, Action Click)? extra = null)
    {
        Row row = NewRow(title, description);
        var toggle = new ToggleSwitch { Checked = value, AccessibleName = title };
        toggle.CheckedChanged += (_, _) => _app.Guard(() => set(toggle.Checked));
        row.AddControl(toggle);
        if (extra is { } e) row.AddControl(Pill(e.Text, e.Click));
    }

    private void AddChoice(string title, string description, IReadOnlyList<(string Text, bool Selected, Action Pick)> options)
    {
        Row row = NewRow(title, description);
        var pills = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(560, 0) };
        foreach ((string text, bool selected, Action pick) in options)
        {
            PillButton pill = Pill(text, () =>
            {
                pick();
                Rebuild();
            }, selected ? PillKind.Selected : PillKind.Secondary);
            pill.AccessibleName = $"{title}: {text}{(selected ? " (current)" : "")}";
            pills.Controls.Add(pill);
        }
        row.AddBelow(pills);
    }

    private void AddButton(string title, string description, string button, Action click)
    {
        Row row = NewRow(title, description);
        row.AddControl(Pill(button, click));
    }

    private void AddInfo(string title, string description) => NewRow(title, description);

    private void AddText(string title, string description, string value, int maxLength, Action<string> save)
    {
        Row row = NewRow(title, description);
        var box = new TextBox { Text = value, MaxLength = maxLength, Width = 360, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 8, 0, 0), AccessibleName = title };
        box.Leave += (_, _) => _app.Guard(() => save(box.Text));
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _app.Guard(() => save(box.Text));
        };
        row.AddBelow(box);
    }

    private void AddFolder(string title, string path, bool isDefault, Action<string?> set)
    {
        Row row = NewRow(title, isDefault ? $"{path} (default)" : path);
        row.AddControl(Pill("Change…", () =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = Directory.Exists(path) ? path : "", ShowNewFolderButton = true, Description = title };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                set(dialog.SelectedPath);
                Rebuild();
            }
        }));
        row.AddControl(Pill("Open", () => _app.ShowInFolder(path)));
        if (!isDefault) row.AddControl(Pill("Use default", () =>
        {
            set(null);
            Rebuild();
        }, PillKind.Ghost));
    }

    private PillButton Pill(string text, Action click, PillKind kind = PillKind.Secondary)
    {
        var pill = new PillButton(text, kind) { Radius = 10, Margin = new Padding(0, 0, 8, 6) };
        pill.Size = pill.Measure();
        pill.Click += (_, _) => _app.Guard(click);
        return pill;
    }

    private void LayoutRows()
    {
        // From the outer width, so rows are equally wide whether or not the scroll bar shows.
        int width = Math.Max(320, _content.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Row row in _rows.Controls.OfType<Row>()) row.Width = width;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) LayoutRows();
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        _nav.BackColor = palette.Surface;
        _content.BackColor = palette.Background;
        _rows.BackColor = palette.Background;
        foreach (Control c in _rows.Controls) Recolour(c, palette, onSurface: c is Row);
        if (_content.IsHandleCreated) DwmHelpers.SetDarkScrollbars((Windows.Win32.Foundation.HWND)_content.Handle, palette.IsDark);
        foreach (Control c in _nav.Controls) c.Invalidate();
    }

    private static void Recolour(Control control, Palette p, bool onSurface)
    {
        switch (control)
        {
            case PillButton or ToggleSwitch:
                control.Invalidate();
                return;
            case TextBox box:
                box.BackColor = p.IsDark ? p.Raised : p.Surface;
                box.ForeColor = p.Text;
                return;
        }
        control.BackColor = onSurface ? p.Surface : p.Background;
        control.ForeColor = control.Tag as string == "dim" ? p.TextDim : p.Text;
        foreach (Control child in control.Controls) Recolour(child, p, onSurface);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        // Ctrl+Tab / Ctrl+Shift+Tab move between sections, like browser tabs.
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            int count = Enum.GetValues<Section>().Length;
            int step = keyData.HasFlag(Keys.Shift) ? count - 1 : 1;
            Show((Section)(((int)_section + step) % count));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.SettingsChanged -= OnSettingsChanged;
            _titleFont.Dispose();
            _rowFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>One setting: a title and a description on the left, its control on the right (or below).</summary>
    private sealed class Row : Panel
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly FlowLayoutPanel _controls = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        private Control? _below;

        public Row(string title, string description, Font titleFont)
        {
            // Labels first: setting Padding runs a layout pass, which needs them.
            _title = new Label { Text = title, Font = titleFont, AutoSize = true, UseMnemonic = false };
            // Sized in OnLayout so long text wraps fully; a long path that can't wrap gets an ellipsis.
            _description = new Label { Text = description, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Tag = "dim" };
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_controls);
            Padding = new Padding(16, 12, 16, 12);
            Margin = new Padding(0, 0, 0, 8);
        }

        public void AddControl(Control control) => _controls.Controls.Add(control);

        public void AddBelow(Control control)
        {
            _below = control;
            Controls.Add(control);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_title is null || _description is null) return; // during construction
            int left = Padding.Left, top = Padding.Top;
            bool hasControls = _controls.Controls.Count > 0;
            _controls.Visible = hasControls;
            Size controls = hasControls ? _controls.GetPreferredSize(Size.Empty) : Size.Empty;
            _controls.Bounds = new Rectangle(Width - Padding.Right - controls.Width, top, controls.Width, controls.Height);
            int textWidth = Math.Max(120, (hasControls ? _controls.Left - 16 : Width - Padding.Right) - left);
            _title.Location = new Point(left, top);
            Size description = _description.Text.Length == 0 ? Size.Empty
                : TextRenderer.MeasureText(_description.Text, _description.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
            _description.Bounds = new Rectangle(left, _title.Bottom + 4, textWidth, description.Height);
            int bottom = Math.Max(_description.Bottom, hasControls ? _controls.Bottom : 0);
            if (_below is not null)
            {
                _below.Location = new Point(left, bottom + 4);
                bottom = _below.Bottom;
            }
            int height = bottom + Padding.Bottom;
            if (Height != height) Height = height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Draw.HighQuality(e.Graphics);
            Draw.Stroke(e.Graphics, Theme.Current.Border, 1, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Current.Background);
            Draw.HighQuality(e.Graphics);
            Draw.Fill(e.Graphics, Theme.Current.Surface, new RectangleF(0, 0, Width, Height), 10);
        }
    }

    /// <summary>A section in the left-hand list: a rounded highlight with an accent bar when selected.</summary>
    private sealed class NavButton : Button
    {
        private bool _selected;
        private bool _hover;

        public NavButton(string text)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleRole = AccessibleRole.PageTab;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                AccessibleName = value ? $"{Text} (selected)" : Text;
                Invalidate();
            }
        }

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
            g.Clear(p.Surface);
            Draw.HighQuality(g);
            var r = new RectangleF(0, 0, Width, Height);
            if (_selected || _hover) Draw.Fill(g, _selected ? p.Selected : p.Raised, r, 8);
            if (_selected)
            {
                float bar = LogicalToDeviceUnits(3);
                Draw.Fill(g, p.Accent, new RectangleF(0, Height * 0.25f, bar, Height * 0.5f), bar / 2);
            }
            if (Focused && ShowFocusCues) Draw.Stroke(g, p.Accent, LogicalToDeviceUnits(2), RectangleF.Inflate(r, -1, -1), 8);
            Draw.Text(g, Text, Font, new Rectangle(LogicalToDeviceUnits(14), 0, Width - LogicalToDeviceUnits(14), Height), _selected ? p.Text : p.TextDim);
        }
    }
}

/// <summary>An on/off switch drawn in the palette. Space or Enter toggles it; it reads as a checkbox to screen readers.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            AccessibleDescription = value ? "On" : "Off";
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Surface);
        Draw.HighQuality(g);
        float pad = 2;
        var track = new RectangleF(pad, pad, Width - 2 * pad - 1, Height - 2 * pad - 1);
        Draw.Fill(g, _checked ? p.Accent : p.Raised, track, track.Height / 2);
        if (!_checked) Draw.Stroke(g, p.Border, 1, track, track.Height / 2);
        float knob = track.Height - 6;
        float x = _checked ? track.Right - knob - 3 : track.Left + 3;
        Draw.Fill(g, _checked ? p.OnAccent : p.TextDim, new RectangleF(x, track.Top + 3, knob, knob), knob / 2);
        if (Focused) Draw.Stroke(g, p.Accent, 2, RectangleF.Inflate(track, 1.5f, 1.5f), (track.Height + 3) / 2);
    }
}
