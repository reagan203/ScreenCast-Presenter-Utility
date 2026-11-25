using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>What choosing a window does: show it on the TV, or record it (FR-52, FR-57).</summary>
internal enum PickerMode { Show, Record }

/// <summary>
/// The picker (Ctrl+Alt+P, SRS 6.1): a centred panel over a dimmed screen with search, live
/// window thumbnails and the output choice. Keyboard: type to search, arrows to move, Enter to
/// show, Ctrl+1–9 to save a favourite, Esc to clear/close. Opens on the presenter's screen
/// (never the output), is excluded from capture, and closes when you click elsewhere.
/// </summary>
internal sealed class PickerOverlay : ThemedForm
{
    private readonly ScpApp _app;
    private readonly DisplayMonitor _screen;
    private readonly ScrimWindow _scrim;
    private readonly SearchBox _search = new("Search windows and apps") { Hint = "Just start typing" };
    private readonly CardGrid _grid = new();
    private readonly FlowLayoutPanel _outputs = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly PillButton _show = new("Show", PillKind.Primary);
    private readonly PillButton _crop = new("Crop…") { AccessibleName = "Crop the selected window, then show it" };
    private readonly FlowLayoutPanel _screens = new();
    private readonly PillButton _theme = new("Light mode", PillKind.Secondary);
    private readonly PillButton _close = new("✕", PillKind.Ghost) { AccessibleName = "Close" };
    private readonly Label _title = new() { Text = "Choose what to show", AutoSize = true };
    private readonly Label _count = new() { AutoSize = true };
    private readonly Label _banner = new() { AutoSize = false, Visible = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _hints = new() { AutoSize = true };
    private readonly Label _footerLabel = new() { Text = "Show on", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "dim" };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private readonly Font _titleFont = Theme.NewFont(15f, FontStyle.Bold);
    private readonly Font _sectionFont = Theme.NewFont(8.5f, FontStyle.Bold);
    private List<SourceWindow> _all = [];
    private bool _dismissing;
    private PickerMode _mode;

    public PickerOverlay(ScpApp app, DisplayMonitor screen)
    {
        _app = app;
        _screen = screen;
        Text = "ScreenCast Presenter picker";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Padding = new Padding(1); // room for the border

        // Size in physical pixels for the target screen; positioned before the handle exists so it opens there at its DPI.
        double s = screen.Scale;
        int width = Math.Min((int)(1080 * s), screen.WorkArea.Width - (int)(48 * s));
        int height = Math.Min((int)(760 * s), screen.WorkArea.Height - (int)(48 * s));
        Bounds = new Rectangle(screen.WorkArea.Left + (screen.WorkArea.Width - width) / 2, screen.WorkArea.Top + (screen.WorkArea.Height - height) / 2, width, height);

        _scrim = new ScrimWindow(screen.Bounds);
        _scrim.Clicked += (_, _) => Dismiss();

        BuildLayout();
        _search.Box.TextChanged += (_, _) => ApplyFilter();
        _grid.CardActivated += async (_, window) => await ChooseAsync(window);
        _grid.SelectionChanged += (_, _) => UpdateShowButton();
        _show.Click += async (_, _) =>
        {
            if (_grid.Selected is { } w) await ChooseAsync(w);
        };
        _theme.Click += (_, _) => _app.SetTheme(Theme.Opposite);
        _crop.Click += async (_, _) =>
        {
            if (_grid.Selected is not { } w || !Visible || _dismissing) return;
            Dismiss();
            if (_mode == PickerMode.Record) await _app.RecordPartAsync(w);
            else await _app.CropAsync(w, present: true); // UC-04
        };
        _close.Click += (_, _) => Dismiss();
        _refresh.Tick += (_, _) => RefreshWindows();
        ApplyPalette(Theme.Current);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Topmost so it appears above the output when the output covers the only screen.
            cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
            return cp;
        }
    }

    private void BuildLayout()
    {
        int Px(int logical) => (int)(logical * _screen.Scale);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(Px(24), Px(18), Px(24), Px(12)) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(48))); // search
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // "Windows" + count
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // grid
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // "Whole screens"
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // screen buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // footer

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, Px(14)) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _title.Font = _titleFont;
        _title.Anchor = AnchorStyles.Left;
        var escHint = new Label { Text = "Esc to close", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(Px(12), 0, Px(4), 0), Tag = "dim" };
        _theme.Size = _theme.Measure();
        _close.Size = new Size(Px(40), Px(40));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(_theme, 1, 0);
        header.Controls.Add(escHint, 2, 0);
        header.Controls.Add(_close, 3, 0);

        _search.Dock = DockStyle.Fill;
        _search.Margin = new Padding(0);

        _banner.Dock = DockStyle.Fill;
        _banner.Height = Px(40);
        _banner.Margin = new Padding(0, Px(10), 0, 0);
        _banner.Padding = new Padding(Px(12), 0, Px(12), 0);

        var sectionRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Px(16), 0, Px(10)), WrapContents = false };
        var section = new Label { Text = "WINDOWS", AutoSize = true, Font = _sectionFont, Tag = "dim" };
        _count.Margin = new Padding(Px(8), Px(1), 0, 0);
        _count.Tag = "faint";
        sectionRow.Controls.Add(section);
        sectionRow.Controls.Add(_count);

        _grid.Dock = DockStyle.Fill;
        _grid.Margin = new Padding(0);

        var screensLabel = new Label { Text = "WHOLE SCREENS", AutoSize = true, Font = _sectionFont, Tag = "dim", Margin = new Padding(0, Px(14), 0, Px(8)) };
        _screens.AutoSize = true;
        _screens.WrapContents = true;
        _screens.Dock = DockStyle.Fill;
        _screens.Margin = new Padding(0);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, Px(14), 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footerLabel.Margin = new Padding(0, 0, Px(10), 0);
        _show.Anchor = AnchorStyles.Right;
        _crop.Anchor = AnchorStyles.Right;
        _crop.Radius = 10;
        _crop.Margin = new Padding(0, 0, Px(10), 0);
        _crop.Size = _crop.Measure();
        _hints.Tag = "faint";
        _hints.Margin = new Padding(0, Px(10), 0, 0);
        footer.Controls.Add(_footerLabel, 0, 0);
        footer.Controls.Add(_outputs, 1, 0);
        footer.Controls.Add(_crop, 3, 0);
        footer.Controls.Add(_show, 4, 0);
        footer.Controls.Add(_hints, 0, 1);
        footer.SetColumnSpan(_hints, 5);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_search, 0, 1);
        root.Controls.Add(_banner, 0, 2);
        root.Controls.Add(sectionRow, 0, 3);
        root.Controls.Add(_grid, 0, 4);
        root.Controls.Add(screensLabel, 0, 5);
        root.Controls.Add(_screens, 0, 6);
        root.Controls.Add(footer, 0, 7);
        Controls.Add(root);
    }

    /// <summary>FR-02: whole screens as sources. The screen holding the output can't show itself (FR-14).</summary>
    private void BuildScreens()
    {
        foreach (Control old in _screens.Controls.Cast<Control>().ToList()) old.Dispose(); // Clear() alone would leak their handles
        // The screen the output fills, or the one the floating window is on: neither can show itself (FR-14).
        DisplayMonitor? output = _app.Session.State != SessionState.Off ? _app.Session.OutputMonitor : _app.ChosenPlacement?.Monitor;
        bool record = _mode == PickerMode.Record;
        if (record && _app.Session.State != SessionState.Off)
        {
            // FR-52 default: exactly what the audience sees, following switches, blank and freeze.
            var tv = new PillButton("What the TV shows", PillKind.Primary)
            {
                Detail = "  · follows what you present",
                Radius = 10,
                AccessibleName = "Record what the TV shows",
                Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
            };
            tv.Size = tv.Measure();
            tv.Click += (_, _) =>
            {
                if (!Visible || _dismissing) return;
                Dismiss();
                _app.RecordOutput();
            };
            _screens.Controls.Add(tv);
        }
        foreach (DisplayMonitor m in _app.Monitors)
        {
            DisplayMonitor monitor = m;
            // Recording the TV screen is fine, and so is showing your own screen in a floating window
            // (a private preview); only a full-screen output can't show the screen it covers.
            bool floating = _app.Session.State != SessionState.Off ? _app.Session.OutputIsFloating : _app.Settings.OutputInWindow;
            bool isOutput = !record && !floating && output is not null && m.IsSameScreen(output);
            var pill = new PillButton(m.Name, PillKind.Secondary)
            {
                Detail = isOutput ? "  · your presentation is shown here" : $"  · {m.ShortName} · {m.Bounds.Width} × {m.Bounds.Height}",
                Enabled = !isOutput,
                Radius = 10,
                AccessibleName = isOutput ? $"{m.Name}: your presentation is shown on it, so it can't be shown" : $"{(record ? "Record" : "Show")} the whole of {m.Label}",
                Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
            };
            pill.Size = pill.Measure();
            pill.Click += async (_, _) =>
            {
                if (!Visible || _dismissing) return;
                Dismiss();
                if (record) _app.RecordSource(SourceWindow.ForScreen(monitor));
                else await _app.PresentAsync(SourceWindow.ForScreen(monitor));
            };
            _screens.Controls.Add(pill);
        }
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        BackColor = palette.Background;
        foreach (Control c in AllControls(this))
        {
            c.BackColor = c is SearchBox or PillButton or CardGrid ? c.BackColor : palette.Background;
            c.ForeColor = c.Tag switch { "dim" => palette.TextDim, "faint" => palette.TextFaint, _ => palette.Text };
        }
        _banner.BackColor = palette.IsDark ? Color.FromArgb(0x21, 0x1D, 0x12) : Color.FromArgb(0xFB, 0xF3, 0xDE);
        _banner.ForeColor = palette.Text;
        _theme.Text = palette.IsDark ? "Light mode" : "Dark mode";
        _theme.AccessibleName = palette.IsDark ? "Switch to light mode" : "Switch to dark mode";
        if (IsHandleCreated) _theme.Size = _theme.Measure();
        _scrim?.Invalidate();
        Invalidate(true);
    }

    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (Control child in AllControls(c)) yield return child;
        }
    }

    /// <summary>The screen this picker was laid out for; a different screen means a new picker.</summary>
    public DisplayMonitor Screen => _screen;

    /// <summary>Raised when the picker hides (Esc, click elsewhere, a window was chosen).</summary>
    public event EventHandler? Dismissed;

    /// <summary>
    /// Creates every window handle in advance (hidden), so the first open is as fast as later ones.
    /// The picker is created once and reused: building ~20 controls costs ~65 ms, more than the
    /// whole open budget (NFR-08).
    /// </summary>
    public void Prepare() => CreateControl();

    /// <summary>Fresh list and state for this opening.</summary>
    private void PrepareContent()
    {
        _search.Box.Clear();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RefreshWindows();
        ScpApp.Diag($"picker: enumerated {_all.Count} windows in {clock.ElapsedMilliseconds} ms");
        bool record = _mode == PickerMode.Record;
        _title.Text = record ? "Choose what to record" : "Choose what to show";
        Text = record ? "ScreenCast Presenter: record" : "ScreenCast Presenter picker";
        _footerLabel.Text = record ? "Sound" : "Show on";
        _crop.Text = record ? "Part of it…" : "Crop…";
        _crop.AccessibleName = record ? "Record part of the selected window" : "Crop the selected window, then show it";
        _crop.Size = _crop.Measure();
        if (record) BuildSoundOptions();
        else BuildOutputs();
        BuildScreens();
        UpdateShowButton();
        UpdateHints();
        _banner.Text = "⚠  Your TV is copying your whole screen. It's made a separate screen before anything is shown.";
        _banner.Visible = _app.IsDisplayDuplicated;
    }

    /// <summary>Show (present) or Record; set when the picker opens.</summary>
    public PickerMode Mode => _mode;

    public void Open(PickerMode mode = PickerMode.Show)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _dismissing = false;
        _mode = mode;
        PrepareContent();

        // Both are topmost; the panel is shown second so it sits above the scrim. (Not "owned" by
        // the scrim: closing an owner closes its owned forms, which would recurse on Close.)
        _scrim.Show();
        if (IsHandleCreated)
        {
            // Reopening: repaint while cloaked so the previous opening's content never shows for a frame.
            var hwnd = (HWND)Handle;
            DwmHelpers.SetCloaked(hwnd, true);
            Show();
            DwmHelpers.PaintNow(hwnd);
            _grid.UpdateThumbnails();
            DwmHelpers.Flush();
            DwmHelpers.SetCloaked(hwnd, false);
        }
        else
        {
            Show(); // first time: ThemedForm uncloaks after the first full paint
        }
        Activate();
        _search.Box.Focus();
        _refresh.Start();
        ScpApp.Diag($"picker: open {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Hides the picker and releases every live thumbnail (SRS 4.1). The window is kept for next time.</summary>
    public void Dismiss()
    {
        if (!Visible || _dismissing) return;
        _dismissing = true;
        _refresh.Stop();
        _grid.ReleaseThumbnails();
        Hide();
        _scrim.Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _grid.UpdateThumbnails();
        _search.Box.Focus();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _grid.UpdateThumbnails();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Clicking another app hides the picker, like a launcher.
        if (Visible && !_dismissing) BeginInvoke(Dismiss);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 and similar just hide it; only app shutdown really closes it.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Dismiss();
        }
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
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;

        if (keyData == Keys.Escape)
        {
            if (_search.Box.TextLength > 0) _search.Box.Clear();
            else Dismiss();
            return true;
        }
        if (keyData == Keys.Enter && _search.Box.Focused)
        {
            _grid.ActivateSelected();
            return true;
        }
        // Arrows move through the cards while typing; Left/Right only when the search box is empty.
        bool arrows = key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown ||
                      (key is Keys.Left or Keys.Right && _search.Box.TextLength == 0) ||
                      (key is Keys.Home or Keys.End && mods == Keys.Control);
        if (arrows && mods is Keys.None or Keys.Control && (_search.Box.Focused || _grid.Focused))
        {
            _grid.MoveSelection(key);
            return true;
        }
        if (mods == Keys.Control && key is >= Keys.D1 and <= Keys.D9 && _grid.Selected is { } selected)
        {
            int slot = key - Keys.D0;
            _app.SaveFavourite(selected, slot);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshWindows()
    {
        List<SourceWindow> windows = WindowEnumerator.GetPresentableWindows();
        if (windows.Count == _all.Count && windows.Zip(_all).All(p => p.First == p.Second)) return; // nothing changed
        _all = windows;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = _search.Box.Text;
        List<SourceWindow> shown = PickerLayout.Filter(_all, query, w => w.Title, w => w.AppName);
        _grid.EmptyText = _all.Count == 0
            ? "No windows to show. Open the app you want to present."
            : $"No windows match “{query}”. Try the app name, like “PowerPoint”.";
        _grid.Items = shown;
        _count.Text = query.Trim().Length > 0 ? $"{shown.Count} of {_all.Count} open" : $"{_all.Count} open";
        UpdateShowButton();
    }

    private void BuildOutputs()
    {
        // Controls.Clear() only detaches controls; dispose the old buttons or their window handles leak.
        foreach (Control old in _outputs.Controls.Cast<Control>().ToList()) old.Dispose();
        OutputOption current = _app.CurrentOutputOption;
        foreach (DisplayMonitor m in _app.Monitors)
        {
            DisplayMonitor monitor = m;
            bool selected = !current.IsWindow && current.Screen is not null && m.IsSameScreen(current.Screen);
            var pill = new PillButton(OutputLabel(m), selected ? PillKind.Selected : PillKind.Secondary)
            {
                Detail = $"  {m.Name}",
                AccessibleName = $"Show on {m.Label}",
                Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0),
            };
            pill.Size = pill.Measure();
            pill.Click += (_, _) =>
            {
                _app.SetOutputMonitor(monitor);
                BuildOutputs();
                UpdateShowButton();
            };
            _outputs.Controls.Add(pill);
        }

        // FR-11: a floating window, e.g. to share in Zoom without a second screen.
        var window = new PillButton("Window", current.IsWindow ? PillKind.Selected : PillKind.Secondary)
        {
            Detail = "  for Zoom",
            AccessibleName = "Show in a floating window you can share in Zoom",
            Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0),
        };
        window.Size = window.Measure();
        window.Click += (_, _) =>
        {
            _app.SetOutputWindow();
            BuildOutputs();
            UpdateShowButton();
        };
        _outputs.Controls.Add(window);
        ApplyPalette(Theme.Current);
    }

    /// <summary>
    /// Record mode's footer: which sound goes in (FR-53, FR-58). The meeting-sound pill cycles
    /// app only → all computer sound → none; the microphone pill toggles. Saved for next time.
    /// </summary>
    private void BuildSoundOptions()
    {
        foreach (Control old in _outputs.Controls.Cast<Control>().ToList()) old.Dispose();
        Recording.MeetingSound sound = _app.Settings.MeetingSound;
        var meeting = new PillButton(sound switch
        {
            Recording.MeetingSound.App => "Meeting sound: this app only",
            Recording.MeetingSound.All => "Meeting sound: all computer sound",
            _ => "No computer sound",
        }, sound == Recording.MeetingSound.None ? PillKind.Secondary : PillKind.Selected)
        {
            AccessibleName = sound switch
            {
                Recording.MeetingSound.App => "Computer sound: only the recorded app. Click for all computer sound.",
                Recording.MeetingSound.All => "Computer sound: everything. Click for none.",
                _ => "Computer sound: none. Click for only the recorded app.",
            },
            Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0),
        };
        meeting.Size = meeting.Measure();
        meeting.Click += (_, _) =>
        {
            _app.SetRecordSound(sound switch
            {
                Recording.MeetingSound.App => Recording.MeetingSound.All,
                Recording.MeetingSound.All => Recording.MeetingSound.None,
                _ => Recording.MeetingSound.App,
            });
            BuildSoundOptions();
        };
        bool mic = _app.Settings.RecordMicrophone;
        var microphone = new PillButton(mic ? "Microphone on" : "Microphone off", mic ? PillKind.Selected : PillKind.Secondary)
        {
            AccessibleName = mic ? "Microphone is recorded. Click to turn it off." : "Microphone is not recorded. Click to turn it on.",
            Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0),
        };
        microphone.Size = microphone.Measure();
        microphone.Click += (_, _) =>
        {
            _app.SetRecordMicrophone(!_app.Settings.RecordMicrophone);
            BuildSoundOptions();
        };
        _outputs.Controls.Add(meeting);
        _outputs.Controls.Add(microphone);
        ApplyPalette(Theme.Current);
    }

    private static string OutputLabel(DisplayMonitor m) =>
        m.IsVirtual ? "Zoom screen" : m.IsTv ? "TV" : m.Connection == "Built-in" ? "This screen" : m.FriendlyName.Length > 0 ? m.FriendlyName : "Screen";

    private void UpdateShowButton()
    {
        OutputOption current = _app.CurrentOutputOption;
        DisplayMonitor? output = current.Screen;
        string target = _mode == PickerMode.Record ? "● Start recording"
            : current.IsWindow ? "Show in a window"
            : output is null ? "Show" : output.IsVirtual ? "Show on the Zoom screen" : output.IsTv ? "Show on TV"
            : _app.Monitors.Count < 2 ? "Show on this screen" : $"Show on {output.Name}";
        _show.Text = target;
        _show.Enabled = _grid.Selected is not null;
        _crop.Enabled = _grid.Selected is { IsMinimized: false };
        _show.Size = _show.Measure();
    }

    private void UpdateHints()
    {
        string save = "Ctrl+1–9 saves a favourite";
        _hints.Text = _mode == PickerMode.Record
            ? "Arrow keys to move   ·   Enter to start recording   ·   It keeps recording while you work, even when the window is covered"
            : $"Arrow keys to move   ·   Enter to show   ·   {save}   ·   {_app.ActiveShortcut("palette")?.ToString() ?? "The palette"} runs any action";
    }

    private async Task ChooseAsync(SourceWindow window)
    {
        if (!Visible || _dismissing) return;
        Dismiss(); // vanish first; it is excluded from capture anyway
        if (_mode == PickerMode.Record) _app.RecordSource(window);
        else await _app.PresentAsync(window);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _scrim.Dispose();
            // Controls don't dispose fonts assigned to them; these are ours.
            _titleFont.Dispose();
            _sectionFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Dims the presenter's screen behind the picker. Click it to close the picker.</summary>
    private sealed class ScrimWindow : Form
    {
        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

        public ScrimWindow(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = bounds;
            BackColor = Color.Black;
            Opacity = Theme.Current.IsDark ? 0.6 : 0.35;
        }

        public event EventHandler? Clicked;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DwmHelpers.ExcludeFromCapture((HWND)Handle);
            DwmHelpers.MakeBareSurface((HWND)Handle);
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Black);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Clicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
