using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tray icon and menu (SRS 6.1): left-click opens the picker, right-click shows the menu. The menu
/// is rebuilt each time it opens, so it always reflects the current state, theme and shortcuts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ScpApp _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _idleIcon;
    private readonly Icon _presentingIcon;
    private readonly Icon _recordingIcon;

    public TrayIcon(ScpApp app)
    {
        _app = app;
        _idleIcon = AppIcon.WithStatus(null);
        _presentingIcon = AppIcon.WithStatus(Color.FromArgb(0x22, 0xC5, 0x5E)); // green: live
        _recordingIcon = AppIcon.WithStatus(Theme.Light.Recording); // red: recording

        _menu = new ContextMenuStrip { Font = Theme.UiFont, ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, _) => BuildMenu();
        ThemedMenuRenderer.ApplyPopupChrome(_menu);

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "ScreenCast Presenter",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.OpenPicker();
        };
    }

    /// <summary>Updates icon and tooltip after a state change.</summary>
    public void Refresh()
    {
        SessionController s = _app.Session;
        RecordingController r = _app.Recording;
        bool presenting = s.State == SessionState.Presenting;
        Icon icon = r.IsActive ? _recordingIcon : presenting ? _presentingIcon : _idleIcon; // FR-54: recording shows in the tray
        if (!ReferenceEquals(_notifyIcon.Icon, icon)) _notifyIcon.Icon = icon;
        string text = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => "ScreenCast Presenter: TV is blank",
            SessionState.Presenting => $"ScreenCast Presenter: showing {s.Source?.Title}",
            SessionState.IdleScreen => "ScreenCast Presenter: TV shows a blank screen",
            _ => "ScreenCast Presenter",
        };
        if (RecordingLine(r) is { } recording) text = recording + "\n" + text;
        _notifyIcon.Text = text.Length > 127 ? text[..126] + "…" : text; // tooltip limit
    }

    private void BuildMenu()
    {
        Palette p = Theme.Current;
        _menu.Renderer = new ThemedMenuRenderer(p);
        _menu.BackColor = p.Surface;
        _menu.ForeColor = p.Text;
        // Items.Clear() only detaches items; dispose them so the old submenus (real windows) are released.
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _menu.Items.Clear();
        SessionController s = _app.Session;

        var status = new ToolStripLabel(StatusText(s)) { ForeColor = p.TextDim, Padding = new Padding(4, 6, 4, 6) };
        _menu.Items.Add(status);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Present…", "picker", _app.OpenPicker));
        _menu.Items.Add(Item("Command palette…", "palette", _app.OpenPalette));
        var blank = Item(s.IsBlanked ? "Show the TV again" : "Blank the TV", "blank", () => _ = _app.RunAsync("blank"));
        blank.Enabled = s.State != SessionState.Off;
        _menu.Items.Add(blank);
        var stop = Item("Stop showing", "stop", () => _ = _app.RunAsync("stop"));
        stop.Enabled = s.State != SessionState.Off;
        _menu.Items.Add(stop);
        bool presenting = s.State == SessionState.Presenting;
        var freeze = Item(s.IsFrozen ? "Go live" : "Freeze the TV", "freeze", () => _ = _app.RunAsync("freeze"));
        freeze.Enabled = presenting;
        _menu.Items.Add(freeze);
        var crop = Item("Crop…", "crop", () => _ = _app.RunAsync("crop"));
        crop.Enabled = presenting;
        _menu.Items.Add(crop);
        if (s.Source?.Crop is not null) _menu.Items.Add(Item("Remove the crop", "remove-crop", () => _ = _app.RunAsync("remove-crop")));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Take a screenshot", "screenshot", () => _ = _app.RunAsync("screenshot")));
        AddRecordingItems(p);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(FavouritesMenu());
        _menu.Items.Add(OutputMenu());
        _menu.Items.Add(DimMenu(s));
        _menu.Items.Add(AppearanceMenu());
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Settings…", null, _app.OpenSettings));
        _menu.Items.Add(Item("Keyboard shortcuts…", "cheat-sheet", _app.OpenShortcuts));
        _menu.Items.Add(Check("Pause all shortcuts", _app.ShortcutsPaused, "pause-shortcuts", _app.TogglePauseShortcuts));
        _menu.Items.Add(Check("Show yellow outline", _app.Settings.ShowOutline, "toggle-outline", () => _app.SetShowOutline(!_app.Settings.ShowOutline)));
        _menu.Items.Add(Check("Show now-showing bar", _app.Settings.ShowWidget, "toggle-widget", () => _app.SetShowWidget(!_app.Settings.ShowWidget)));
        _menu.Items.Add(Check("Show presenting badge", _app.Settings.ShowBadge, "toggle-badge", () => _app.SetShowBadge(!_app.Settings.ShowBadge)));
        _menu.Items.Add(Check("Show mouse pointer on the TV", _app.Settings.ShowCursor, "", () => _app.SetShowCursor(!_app.Settings.ShowCursor)));
        ToolStripMenuItem engine = Submenu("Capture engine");
        ToolStripMenuItem automatic = Item("Automatic (lowest memory)", null, () => _app.SetCaptureEngine(false));
        automatic.Checked = !_app.Settings.PreferWgc;
        ToolStripMenuItem wgc = Item("Always Windows Graphics Capture (pointer, last picture when minimised)", null, () => _app.SetCaptureEngine(true));
        wgc.Checked = _app.Settings.PreferWgc;
        engine.DropDownItems.Add(automatic);
        engine.DropDownItems.Add(wgc);
        if (s.EngineName is { } active) engine.DropDownItems.Add(new ToolStripMenuItem($"In use now: {active}") { Enabled = false });
        _menu.Items.Add(engine);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", null, _app.RequestQuit));
    }

    /// <summary>FR-52–FR-54: start, pause, stop, and the sound choices for the next recording.</summary>
    private void AddRecordingItems(Palette p)
    {
        RecordingController r = _app.Recording;
        if (r.IsActive)
        {
            _menu.Items.Add(new ToolStripLabel(RecordingLine(r)) { ForeColor = p.Recording, Padding = new Padding(4, 4, 4, 4) });
            var pause = Item(r.Status == RecordingStatus.Paused ? "Resume recording" : "Pause recording", "pause-recording", r.TogglePause);
            pause.Enabled = r.Status is RecordingStatus.Recording or RecordingStatus.Paused;
            _menu.Items.Add(pause);
            var stop = Item("Stop recording", "record", r.Stop);
            stop.Enabled = r.Status != RecordingStatus.Stopping;
            _menu.Items.Add(stop);
            ToolStripMenuItem limit = Submenu("Stop automatically");
            ToolStripMenuItem never = Item("Only when the window closes or I stop it", null, () => _app.SetRecordingStopAfter(null));
            never.Checked = _app.RecordingStopAfter is null;
            limit.DropDownItems.Add(never);
            foreach (int minutes in new[] { 30, 60, 90, 120, 180, 240 })
            {
                var span = TimeSpan.FromMinutes(minutes);
                ToolStripMenuItem item = Item($"After {ScpApp.DurationWords(span)}", null, () => _app.SetRecordingStopAfter(span));
                item.Checked = _app.RecordingStopAfter == span;
                item.Enabled = r.Elapsed < span;
                limit.DropDownItems.Add(item);
            }
            _menu.Items.Add(limit);
        }
        else
        {
            var output = Item("Record what the TV shows", _app.Session.State == SessionState.Presenting ? "record" : null, _app.RecordOutput);
            output.Enabled = _app.Session.State != SessionState.Off;
            _menu.Items.Add(output);
            _menu.Items.Add(Item("Record a window or screen…", _app.Session.State == SessionState.Presenting ? null : "record", _app.OpenRecordPicker));
        }

        ToolStripMenuItem sound = Submenu("Recording sound");
        foreach ((MeetingSound choice, string label) in new[]
        {
            (MeetingSound.App, "Meeting sound: only the recorded app"),
            (MeetingSound.All, "All computer sound"),
            (MeetingSound.None, "No computer sound"),
        })
        {
            MeetingSound c = choice;
            ToolStripMenuItem item = Item(label, null, () => _app.SetRecordSound(c));
            item.Checked = _app.Settings.MeetingSound == choice;
            sound.DropDownItems.Add(item);
        }
        sound.DropDownItems.Add(new ToolStripSeparator());
        sound.DropDownItems.Add(Check("Microphone (people in the room)", _app.Settings.RecordMicrophone, "", () => _app.SetRecordMicrophone(!_app.Settings.RecordMicrophone)));
        _menu.Items.Add(sound);
        _menu.Items.Add(Item("Open recordings folder", null, _app.OpenRecordingsFolder));
    }

    private static string? RecordingLine(RecordingController r) => r.Status switch
    {
        RecordingStatus.Starting => "● Starting to record…",
        RecordingStatus.Recording => $"● Recording {ScpApp.RecordingClockText(r.Elapsed)}: {Shorten(r.Description)}",
        RecordingStatus.Paused => $"❚❚ Recording paused at {ScpApp.RecordingClockText(r.Elapsed)}",
        RecordingStatus.Stopping => "● Saving the recording…",
        _ => null,
    };

    private static string StatusText(SessionController s) => s.State switch
    {
        SessionState.Presenting when s.IsBlanked => "● TV is blank",
        SessionState.Presenting when s.OutputIsFloating => $"● Showing in a window: {Shorten(s.Source?.Title)}",
        SessionState.Presenting => $"● Showing on {s.OutputMonitor?.ShortName}: {Shorten(s.Source?.Title)}",
        SessionState.IdleScreen => "○ TV shows a blank screen",
        _ => "○ Not showing anything",
    };

    private static string Shorten(string? text) => text is null ? "" : text.Length > 40 ? text[..39] + "…" : text;

    private ToolStripMenuItem Item(string text, string? actionId, Action onClick)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => _app.Guard(onClick));
        if (!string.IsNullOrEmpty(actionId) && _app.ActiveShortcut(actionId) is { } hotkey) item.ShortcutKeyDisplayString = hotkey.ToString();
        return item;
    }

    private ToolStripMenuItem Check(string text, bool isChecked, string actionId, Action onClick)
    {
        ToolStripMenuItem item = Item(text, actionId, onClick);
        item.Checked = isChecked;
        return item;
    }

    private ToolStripMenuItem Submenu(string text)
    {
        var menu = new ToolStripMenuItem(text);
        menu.DropDown.BackColor = Theme.Current.Surface;
        menu.DropDown.Font = Theme.UiFont;
        ThemedMenuRenderer.ApplyPopupChrome(menu.DropDown);
        return menu;
    }

    private ToolStripMenuItem FavouritesMenu()
    {
        ToolStripMenuItem menu = Submenu("Favourites");
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
        {
            int s = slot;
            Favourite? f = _app.Settings.Favourites[slot - 1];
            ToolStripMenuItem item = f is null
                ? new ToolStripMenuItem($"{slot}   Empty: save with {_app.ActiveShortcut(ShortcutCatalog.SaveFavouriteId(slot))?.ToString() ?? "the palette"}") { Enabled = false }
                : Item($"{slot}   {Shorten(f.Title)} · {f.AppName}", ShortcutCatalog.ShowFavouriteId(slot), () => _ = _app.RunAsync(ShortcutCatalog.ShowFavouriteId(s)));
            menu.DropDownItems.Add(item);
        }
        if (_app.Session.Source is not null)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem save = Submenu("Save what's showing as");
            for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            {
                int s = slot;
                save.DropDownItems.Add(Item($"Favourite {slot}", ShortcutCatalog.SaveFavouriteId(slot), () => _ = _app.RunAsync(ShortcutCatalog.SaveFavouriteId(s))));
            }
            menu.DropDownItems.Add(save);
        }
        return menu;
    }

    private ToolStripMenuItem OutputMenu()
    {
        ToolStripMenuItem menu = Submenu("Show on");
        OutputOption current = _app.CurrentOutputOption;
        foreach (Interop.DisplayMonitor m in _app.Monitors)
        {
            Interop.DisplayMonitor monitor = m;
            ToolStripMenuItem item = Item(m.Label, null, () => _app.SetOutputMonitor(monitor));
            item.Checked = !current.IsWindow && current.Screen is not null && m.IsSameScreen(current.Screen);
            menu.DropDownItems.Add(item);
        }
        ToolStripMenuItem window = Item("A window (to share in Zoom)", "switch-output", _app.SetOutputWindow);
        window.Checked = current.IsWindow;
        menu.DropDownItems.Add(window);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Item("Show in Zoom…", null, () => _ = _app.ShowInZoomAsync()));
        return menu;
    }

    private ToolStripMenuItem DimMenu(SessionController s)
    {
        ToolStripMenuItem menu = Submenu("Dim the TV");
        foreach (int percent in new[] { 0, 10, 20, 30, 40 })
        {
            int value = percent;
            ToolStripMenuItem item = Item(percent == 0 ? "Off" : $"{percent}%", null, () => _app.SetDim(value / 100.0));
            item.Checked = Math.Abs(s.Dim - percent / 100.0) < 0.001;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ToolStripMenuItem AppearanceMenu()
    {
        ToolStripMenuItem menu = Submenu("Appearance");
        foreach ((ThemeMode mode, string label) in new[] { (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"), (ThemeMode.System, "Match Windows") })
        {
            ThemeMode m = mode;
            ToolStripMenuItem item = Item(label, null, () => _app.SetTheme(m));
            item.Checked = Theme.Mode == mode;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _presentingIcon.Dispose();
        _recordingIcon.Dispose();
    }
}
