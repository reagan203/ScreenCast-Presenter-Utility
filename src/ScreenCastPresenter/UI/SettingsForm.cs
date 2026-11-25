using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Settings (SRS 6.1), laid out like Windows 11 Settings: sections with icons on the left, on Mica
/// (Windows 11 22H2+), and on the right a large page title, rows grouped under small headings,
/// each with its icon. Pages slide in when you switch. Every change applies and saves
/// immediately; there is no OK/Apply. Excluded from capture like every SCP
/// window, keyboard navigable (Tab through rows, Space toggles, arrows in the section list).
/// </summary>
internal sealed class SettingsForm : ThemedForm
{
    public enum Section { General, Output, Recording, Appearance, Advanced }

    private readonly ScpApp _app;
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel _rows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
    private readonly Font _titleFont = Theme.NewFont(Theme.TitlePoints, FontStyle.Bold);
    private readonly Font _rowFont = Theme.NewFont(Theme.BodyPoints);
    private readonly Font _groupFont = Theme.NewFont(Theme.BodyPoints, FontStyle.Bold);
    private Section _section;

    /// <summary>The section showing now (to reopen at the same place).</summary>
    public Section CurrentSection => _section;

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

        // Wider with larger text, so "Recording & screenshots" isn't cut short.
        int navWidth = (int)Math.Round(216 * Math.Max(1f, Theme.TextScale)); // room for the icon and the longest name
        _nav.Width = navWidth + 24;
        _nav.Padding = new Padding(12, 16, 12, 12);
        foreach (Section s in Enum.GetValues<Section>())
        {
            var button = new NavButton(Label(s), SectionIcon(s)) { Tag = s, Width = navWidth, Height = (int)Math.Round(40 * Math.Max(1f, Theme.TextScale)), Margin = new Padding(0, 0, 0, 4) };
            button.Click += (_, _) => Show(s);
            _nav.Controls.Add(button);
        }
        _content.Padding = new Padding(28, 20, 28, 20);
        _content.Controls.Add(_rows);
        Controls.Add(_content);
        Controls.Add(_nav);
        _content.HandleCreated += (_, _) => DwmHelpers.SetDarkScrollbars((Windows.Win32.Foundation.HWND)_content.Handle, Theme.Current.IsDark);
        _nav.SizeChanged += (_, _) => ApplyBackdrop(); // the Mica strip follows the pane (DPI changes)
        _app.SettingsChanged += OnSettingsChanged;
        Show(section);
    }

    /// <summary>Mica behind the title bar and the section list, like Windows 11 Settings; the page itself stays solid.</summary>
    protected override Padding? GlassMargins => new Padding(_nav.Width, 0, 0, 0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyPalette(Theme.Current); // now that it's known whether the backdrop shows
    }

    private static char SectionIcon(Section s) => s switch
    {
        Section.Output => Icons.Tv,
        Section.Recording => Icons.Video,
        Section.Appearance => Icons.Colour,
        Section.Advanced => Icons.Repair,
        _ => Icons.Settings,
    };

    /// <summary>Each row's icon, by its title (a missing one just has none).</summary>
    private static readonly Dictionary<string, char> RowIcons = new(StringComparer.Ordinal)
    {
        ["Start with Windows"] = Icons.Power,
        ["Icon by the clock"] = Icons.Pin,
        ["Start menu"] = Icons.Download,
        ["Installed"] = Icons.Info,
        ["Keyboard shortcuts"] = Icons.Keyboard,
        ["Yellow outline"] = Icons.Highlight,
        ["Now-showing bar"] = Icons.View,
        ["Status pill"] = Icons.Message,
        ["Mouse pointer on the TV"] = Icons.Mouse,
        ["Show on"] = Icons.Tv,
        ["Keep the window on top"] = Icons.Pin,
        ["Browsers: keep to the page you chose"] = Icons.Lock,
        ["Sharing in Zoom or Teams"] = Icons.Share,
        ["Zoom screen"] = Icons.Download,
        ["Dim the TV"] = Icons.Brightness,
        ["Blank screen message"] = Icons.Hide,
        ["Recordings folder"] = Icons.Folder,
        ["Screenshots folder"] = Icons.Camera,
        ["Computer sound in recordings"] = Icons.Volume,
        ["Microphone"] = Icons.Microphone,
        ["Theme"] = Icons.Colour,
        ["Accent colour"] = Icons.Highlight,
        ["Text size"] = Icons.Font,
        ["How windows are shown"] = Icons.Window,
        ["Settings file"] = Icons.Folder,
        ["Privacy"] = Icons.Lock,
        ["Version"] = Icons.Info,
    };

    private static string Label(Section s) => s switch
    {
        Section.Output => "Where it shows",
        Section.Recording => "Recording & screenshots",
        Section.Appearance => "Appearance",
        Section.Advanced => "Advanced",
        _ => "General",
    };

    /// <summary>Switches section (also used to open Settings at a given section). A new page slides in.</summary>
    public void Show(Section section)
    {
        bool changed = section != _section || _rows.Controls.Count == 0;
        _section = section;
        foreach (NavButton b in _nav.Controls.OfType<NavButton>()) b.Selected = (Section)b.Tag! == section;
        Rebuild();
        if (changed && Visible)
        {
            _content.AutoScrollPosition = Point.Empty;
            _ = Motion.SlideIn(_rows, LogicalToDeviceUnits(18));
        }
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
        _rows.Controls.Add(new Label { Text = Label(_section), Font = _titleFont, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) });
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
        AddGroup("Starting and finding it");
        AddToggle("Start with Windows", _app.StartupDisabledInTaskManager
                ? "Switched off in Task Manager › Startup apps. Switch it on there too."
                : "ScreenCast Presenter waits by the clock, ready when you are.",
            startup || _app.StartupDisabledInTaskManager, on => _app.SetStartWithWindows(on));
        if (_app.TrayIconPinned == false)
            AddButton("Icon by the clock", "Windows hides it behind the ^ arrow next to the clock.", "Keep it visible", () =>
            {
                _app.PinTrayIcon();
                Rebuild();
            });
        if (_app.CanInstall)
            AddButton("Start menu", _app.IsInstalled
                    ? "A different copy is in the Start menu. Replace it with this one."
                    : "Add ScreenCast Presenter to the Start menu and desktop, so you can always find it.", _app.IsInstalled ? "Replace it" : "Add it", () =>
            {
                _app.Install();
                Rebuild();
            });
        else if (_app.IsInstalled)
            AddButton("Installed", "In the Start menu and in Settings › Apps. Removing it keeps your recordings, screenshots and settings.", "Uninstall…", _app.RequestUninstall);
        AddGroup("Keyboard");
        AddToggle("Keyboard shortcuts", "Ctrl+Alt shortcuts work from any app.", s.ShortcutsEnabled, on => _app.SetShortcutsEnabled(on),
            ("Edit shortcuts…", _app.OpenShortcuts));
        AddGroup("On your screen while you present");
        AddToggle("Yellow outline", "Marks on your screen what the audience sees. Never on the TV.", s.ShowOutline, on => _app.SetShowOutline(on));
        AddToggle("Now-showing bar", "A small bar on top while you present or record, like Teams' sharing bar. Click it to go to what's shown.", s.ShowWidget, on => _app.SetShowWidget(on));
        AddToggle("Status pill", "When the now-showing bar is hidden, a pill at the top of your screen says what's on the TV. Messages always appear there.", s.ShowBadge, on => _app.SetShowBadge(on));
        AddToggle("Mouse pointer on the TV", "Show your pointer in what you present.", s.ShowCursor, on => _app.SetShowCursor(on));
    }

    private void BuildOutput()
    {
        AppSettings s = _app.Settings;
        OutputOption current = _app.CurrentOutputOption;
        var choices = _app.Monitors.Select(m => (m.IsVirtual ? $"Zoom screen ({m.Name})" : m.IsTv ? $"TV ({m.Name})" : m.Name,
            !current.IsWindow && current.Screen is not null && m.IsSameScreen(current.Screen), (Action)(() => _app.SetOutputMonitor(m)))).ToList();
        choices.Add(("A window", current.IsWindow, _app.SetOutputWindow));
        AddGroup("Screen");
        AddChoice("Show on", "Full screen on a TV or projector, or in a window you can move and share in a meeting.", choices);
        AddToggle("Browsers: keep to the page you chose", "If you switch tabs or pages in a browser window you're showing, the audience sees black until you go back, instead of the new page.",
            s.KeepBrowserPage, on => _app.SetKeepBrowserPage(on));
        AddToggle("Keep the window on top", "The window stays above every other window while you present, like Teams' sharing preview.", s.FloatingOnTop, on => _app.SetFloatingOnTop(on));
        bool zoomScreen = _app.Monitors.Any(m => m.IsVirtual);
        AddGroup("Meetings");
        AddButton("Sharing in Zoom or Teams", zoomScreen
                ? "Share the Zoom screen in your meeting; your own screen stays private."
                : "Share the window “ScreenCast Presenter output” in your meeting.",
            "Show me how", () => _app.OpenShareGuide(zoomScreen ? ShareGuideKind.ZoomScreen : ShareGuideKind.Window));
        if (!zoomScreen)
            AddButton("Zoom screen", "Share a whole screen in meetings without a second monitor. Installs a free display driver; Windows asks your permission.",
                "Set it up…", () => _ = _app.SetUpZoomScreenAsync());
        AddGroup("On the TV");
        AddChoice("Dim the TV", "Softens a bright TV in a dark room.",
            [.. new[] { 0, 10, 20, 30, 40 }.Select(p => (p == 0 ? "Off" : $"{p}%", Math.Abs(s.Dim - p / 100.0) < 0.001, (Action)(() => _app.SetDim(p / 100.0))))]);
        string key = _app.ActiveShortcut("blank-message") is { } k ? $" ({k})" : "";
        AddText("Blank screen message", $"Shown on the TV when you blank it with a message{key}.", s.BlankMessage, AppSettings.MaxMessageLength, _app.SetBlankMessage);
    }

    private void BuildRecording()
    {
        AppSettings s = _app.Settings;
        AddGroup("Where they're saved");
        AddFolder("Recordings folder", s.RecordingFolder ?? SavedFiles.DefaultRecordingFolder, s.RecordingFolder is null, _app.SetRecordingFolder);
        AddFolder("Screenshots folder", s.ScreenshotFolder ?? SavedFiles.DefaultScreenshotFolder, s.ScreenshotFolder is null, _app.SetScreenshotFolder);
        MeetingSound sound = s.MeetingSound;
        AddGroup("Sound");
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
        AddChoice("Theme", "ScreenCast Presenter's own windows. The TV is always black.",
        [
            ("Light", mode == ThemeMode.Light, () => _app.SetTheme(ThemeMode.Light)),
            ("Dark", mode == ThemeMode.Dark, () => _app.SetTheme(ThemeMode.Dark)),
            ("Match Windows", mode == ThemeMode.System, () => _app.SetTheme(ThemeMode.System)),
        ]);
        bool windowsAccent = _app.Settings.UseWindowsAccent;
        AddChoice("Accent colour", "Buttons and highlights. The outline around what you show stays yellow.",
        [
            ("ScreenCast blue", !windowsAccent, () => _app.SetUseWindowsAccent(false)),
            ("My Windows colour", windowsAccent, () => _app.SetUseWindowsAccent(true)),
        ]);
        double size = _app.Settings.TextSize;
        AddChoice("Text size", "Bigger text in ScreenCast Presenter's windows, menus and messages. The TV is unaffected.",
            [.. TextSize.Choices.Select(c => (c.Label, Math.Abs(size - c.Scale) < 0.01, (Action)(() => _app.SetTextSize(c.Scale))))]);
    }

    private void BuildAdvanced()
    {
        bool wgc = _app.Settings.PreferWgc;
        string inUse = _app.Session.EngineName is { } e ? $" Now using: {e}." : "";
        AddChoice("How windows are shown", "Automatic uses the least memory. “Always show the pointer” also keeps a minimised window's last picture on the TV." + inUse,
        [
            ("Automatic", !wgc, () => _app.SetCaptureEngine(false)),
            ("Always show the pointer", wgc, () => _app.SetCaptureEngine(true)),
        ]);
        AddButton("Settings file", _app.SettingsFilePath, "Show in folder", () => _app.ShowInFolder(_app.SettingsFilePath));
        AddInfo("Privacy", "ScreenCast Presenter makes no network connections and sends no data anywhere. Recordings and screenshots stay on this PC. Setting up a Zoom screen downloads the driver through Windows, only when you ask.");
        AddInfo("Version", _app.VersionText);
    }

    // ───────────────────────────── rows ─────────────────────────────

    /// <summary>A small heading above a group of rows, like Windows 11 Settings.</summary>
    private void AddGroup(string text) =>
        _rows.Controls.Add(new Label { Text = text, Font = _groupFont, AutoSize = true, UseMnemonic = false, Margin = new Padding(2, 18, 0, 6) });

    private Row NewRow(string title, string description)
    {
        var row = new Row(title, description, _rowFont, RowIcons.TryGetValue(title, out char icon) && Icons.Available ? icon : null);
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
        _nav.BackColor = HasBackdrop ? Color.Black : palette.Surface; // black is fully transparent on Mica
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
            _groupFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>One setting: a title and a description on the left, its control on the right (or below).</summary>
    private sealed class Row : Panel
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly FlowLayoutPanel _controls = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        private readonly char? _icon;
        private Control? _below;

        public Row(string title, string description, Font titleFont, char? icon)
        {
            _icon = icon;
            // Labels first: setting Padding runs a layout pass, which needs them.
            _title = new Label { Text = title, Font = titleFont, AutoSize = true, UseMnemonic = false };
            // Sized in OnLayout so long text wraps fully; a long path that can't wrap gets an ellipsis.
            _description = new Label { Text = description, Font = Theme.SmallFont, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Tag = "dim" };
            Controls.Add(_title);
            Controls.Add(_description);
            Controls.Add(_controls);
            Padding = new Padding(16, 14, 16, 14);
            Margin = new Padding(0, 0, 0, 4); // grouped rows sit close, like Windows 11 Settings
            MinimumSize = new Size(0, 68);
        }

        /// <summary>Where the text starts: after the icon column when there is an icon.</summary>
        private int TextLeft => Padding.Left + (_icon is null ? 0 : LogicalToDeviceUnits((int)Math.Round(36 * Math.Max(1f, Theme.TextScale))));

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
            int left = TextLeft, top = Padding.Top;
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
            int height = Math.Max(MinimumSize.Height, bottom + Padding.Bottom);
            if (Height != height) Height = height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Draw.HighQuality(e.Graphics);
            Draw.Stroke(e.Graphics, Theme.Current.Border, 1, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), LogicalToDeviceUnits(6));
            if (_icon is { } icon)
            {
                int size = LogicalToDeviceUnits((int)Math.Round(20 * Math.Max(1f, Theme.TextScale)));
                int top = _description.Text.Length > 0 ? (_title.Top + _description.Bottom - size) / 2 : _title.Top + (_title.Height - size) / 2;
                Icons.Draw(e.Graphics, icon, new RectangleF(Padding.Left, top, size, size), Theme.Current.Text, size * 0.8f);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Current.Background);
            Draw.HighQuality(e.Graphics);
            Draw.Fill(e.Graphics, Theme.Current.Surface, new RectangleF(0, 0, Width, Height), LogicalToDeviceUnits(6));
        }
    }

    /// <summary>
    /// A section in the left-hand list: icon and name, a soft highlight with an accent bar when
    /// selected. On Mica it paints through <see cref="Backdrop.Paint"/> so its text keeps its alpha.
    /// </summary>
    private sealed class NavButton : Button
    {
        private readonly char _icon;
        private bool _selected;
        private bool _hover;

        public NavButton(string text, char icon)
        {
            _icon = icon;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            // One blit per paint (Backdrop.Paint): no double buffer, which would drop the alpha on Mica.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
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

        /// <summary>On Mica the parent is transparent black; otherwise the pane's surface colour.</summary>
        private bool OnGlass => Parent?.BackColor.ToArgb() == Color.Black.ToArgb();

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // Painted in OnPaint, in one blit.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            bool glass = OnGlass;
            Backdrop.Paint(e.Graphics, ClientSize, glass ? null : p.Surface, g =>
            {
                var r = new RectangleF(0, 0, Width, Height);
                float radius = LogicalToDeviceUnits(6);
                if (_selected || _hover)
                {
                    // Windows 11's subtle fills: a faint wash of the text colour over Mica.
                    Color fill = glass ? Color.FromArgb(_selected ? (p.IsDark ? 22 : 14) : (p.IsDark ? 14 : 9), p.Text) : _selected ? p.Selected : p.Raised;
                    Draw.Fill(g, fill, r, radius);
                }
                if (_selected)
                {
                    float bar = LogicalToDeviceUnits(3);
                    Draw.Fill(g, p.Accent, new RectangleF(0, Height * 0.28f, bar, Height * 0.44f), bar / 2);
                }
                if (Focused && ShowFocusCues) Draw.Stroke(g, p.Accent, LogicalToDeviceUnits(2), RectangleF.Inflate(r, -1, -1), radius);
                int iconLeft = LogicalToDeviceUnits(14);
                int iconSize = LogicalToDeviceUnits((int)Math.Round(16 * Math.Max(1f, Theme.TextScale)));
                Icons.Draw(g, _icon, new RectangleF(iconLeft, (Height - iconSize) / 2f, iconSize, iconSize), p.Text, iconSize);
                int textLeft = Icons.Available ? iconLeft + iconSize + LogicalToDeviceUnits(12) : iconLeft;
                Backdrop.Text(g, Text, Font, new RectangleF(textLeft, 0, Width - textLeft - LogicalToDeviceUnits(4), Height), _selected ? p.Text : p.TextDim);
            });
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
