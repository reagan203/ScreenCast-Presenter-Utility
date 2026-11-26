using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tray icon and menu (SRS 6.1): left-click opens the quick-actions panel (like Windows 11's Quick
/// Settings), right-click shows the menu. The menu is rebuilt each time it opens from
/// <see cref="TrayMenuPlan"/>, so it only lists what applies right now, in the current theme, with
/// the current shortcuts, each with its icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ScpApp _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _idleIcon;
    private readonly Icon _presentingIcon;
    private readonly Icon _recordingIcon;
    private readonly List<Image> _menuImages = [];

    public TrayIcon(ScpApp app)
    {
        _app = app;
        _idleIcon = AppIcon.WithStatus(null);
        _presentingIcon = AppIcon.WithStatus(Color.FromArgb(0x22, 0xC5, 0x5E)); // green: live
        _recordingIcon = AppIcon.WithStatus(Theme.Light.Recording); // red: recording

        // Icons in the image column; a ticked choice shows a check icon there instead.
        _menu = new ContextMenuStrip { Font = Theme.UiFont, ShowImageMargin = true, ShowCheckMargin = false };
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
            if (e.Button == MouseButtons.Left) _app.ToggleQuickPanel();
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
            _ => "ScreenCast Presenter: click to show something",
        };
        if (RecordingLine(r) is { } recording) text = recording + "\n" + text;
        _notifyIcon.Text = text.Length > 127 ? text[..126] + "…" : text; // tooltip limit
    }

    /// <summary>What the menu depends on, right now.</summary>
    private TrayMenuState CaptureState()
    {
        SessionController s = _app.Session;
        RecordingController r = _app.Recording;
        OutputOption output = _app.CurrentOutputOption;
        return new TrayMenuState
        {
            Session = s.State,
            Blanked = s.IsBlanked,
            Frozen = s.IsFrozen,
            Cropped = s.Source?.Crop is not null,
            Status = StatusText(s),
            Where = _app.AudienceScreenName,
            Recording = r.Status,
            RecordingLine = RecordingLine(r),
            RecordingElapsed = r.Elapsed,
            StopAfter = _app.RecordingStopAfter,
            StopAt = _app.RecordingStopAt,
            ShowAgainTitle = s.State == SessionState.Off ? _app.Settings.LastSource?.Title : null,
            Favourites = [.. _app.Settings.Favourites.Select((f, i) => f is null ? null : new TrayFavourite(i + 1, f.Title, f.AppName)).OfType<TrayFavourite>()],
            Screens = [.. _app.Monitors.Select(m => new TrayScreen(m.DeviceName, ScreenText(m), output.Screen is not null && m.IsSameScreen(output.Screen)))],
            WindowChosen = output.IsWindow,
            Zoomed = _app.IsZoomed,
            Drawing = _app.IsDrawing,
            LaserOn = _app.IsLaserOn,
            ClicksShown = _app.Settings.HighlightClicks,
            HasInset = _app.HasInset,
            SplitCount = _app.SplitPictures.Count,
            ShowKeys = _app.Settings.ShowKeys,
            ReplayOn = _app.Settings.ReplayOn,
            VoiceCommands = _app.Settings.VoiceCommands,
            NotesOpen = _app.NotesOpen,
            ReplayRunning = _app.ReplayRunning,
            Slideshow = _app.SlideshowOn,
            HasHiddenParts = s.Source is { } shown && Redactions.For(_app.Settings, shown).Count > 0,
            SpotlightOn = _app.IsSpotlightOn,
            CaptionOn = _app.IsCaptionOn,
            HasQr = _app.HasQr,
            LiveCaptions = _app.LiveCaptionsOn,
            AudienceView = _app.AudienceViewOpen,
            NextTitle = _app.Next is { } next ? next.Screen?.Name ?? (next.Title.Length > 0 ? next.Title : next.AppName) : null,
            SecondTvOn = _app.SecondTvOn,
            SecondTvPossible = _app.SecondTvOn || _app.SecondTvCandidates().Count > 0,
            Rooms = [.. _app.Settings.Rooms.Select(r => r.Name)],
            Scenes = [.. _app.Settings.Scenes.Select((sc, i) => (Slot: i + 1, Scene: sc)).Where(x => x.Scene is not null).Select(x => (x.Slot, x.Scene!.Title(x.Slot)))],
            Playlists = [.. _app.Settings.Playlists.Select(p => p.Name)],
            PlaylistPlaying = _app.PlayingOn(PlaylistScreen.Main) is not null || _app.PlayingOn(PlaylistScreen.Second) is not null,
            PhoneRemote = _app.PhoneRemoteOn,
            AutoHide = _app.Settings.AutoHideKinds != SensitiveKinds.None,
        };
    }

    /// <summary>"TV (Screen 2)", "Zoom screen (Screen 3)", "This screen (Screen 1)".</summary>
    private static string ScreenText(Interop.DisplayMonitor m) => $"{(m.IsTv ? "TV" : m.ShortName)} ({m.Name})";

    private void BuildMenu()
    {
        Palette p = Theme.Current;
        _menu.Renderer = new ThemedMenuRenderer(p);
        _menu.BackColor = p.Surface;
        _menu.ForeColor = p.Text;
        _menu.Font = Theme.UiFont; // follows Settings › Appearance › Text size
        // Items.Clear() only detaches items; dispose them so the old submenus (real windows) are released.
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _menu.Items.Clear();
        foreach (Image image in _menuImages) image.Dispose(); // menu items don't dispose their images
        _menuImages.Clear();
        foreach (MenuEntry entry in TrayMenuPlan.Build(CaptureState())) _menu.Items.Add(Create(entry, p));
    }

    private ToolStripItem Create(MenuEntry entry, Palette p)
    {
        switch (entry.Kind)
        {
            case MenuKind.Separator:
                return new ToolStripSeparator();
            case MenuKind.Label:
                return new ToolStripLabel(entry.Text) { ForeColor = entry.Text == RecordingLine(_app.Recording) ? p.Recording : p.TextDim, Padding = new Padding(4, 6, 4, 6) };
        }

        var item = new ToolStripMenuItem(entry.Text) { Enabled = entry.Enabled, Checked = entry.Checked, Image = MenuImage(entry, p) };
        if (entry.Shortcut is { } id && _app.ActiveShortcut(id) is { } hotkey) item.ShortcutKeyDisplayString = hotkey.ToString();
        if (entry.Children is { } children)
        {
            item.DropDown.BackColor = p.Surface;
            item.DropDown.Font = Theme.UiFont;
            if (item.DropDown is ToolStripDropDownMenu dropDown)
            {
                dropDown.ShowImageMargin = true;
                dropDown.ShowCheckMargin = false;
            }
            ThemedMenuRenderer.ApplyPopupChrome(item.DropDown);
            foreach (MenuEntry child in children) item.DropDownItems.Add(Create(child, p));
        }
        else if (entry.Command is { } command)
        {
            item.Click += (_, _) => _ = _app.RunCommandAsync(command);
        }
        return item;
    }

    /// <summary>The line's icon from Windows' icon font (a check for a ticked choice); null without one.</summary>
    private Image? MenuImage(MenuEntry entry, Palette p)
    {
        char? icon = entry.Checked ? Icons.Check : Icons.ForCommand(entry.Command) ?? entry.Text switch
        {
            "Show on" => Icons.Tv,
            "Favourites" or "Save what's showing as" => Icons.Star,
            "Stop automatically" => Icons.Clock,
            _ => null,
        };
        if (icon is not { } glyph) return null;
        int size = Math.Max(16, _menu.ImageScalingSize.Height);
        Bitmap? image = Icons.Bitmap(glyph, size, entry.Enabled ? (entry.Command is "stop-recording" ? p.Recording : p.Text) : p.TextFaint);
        if (image is not null) _menuImages.Add(image);
        return image;
    }

    private static string StatusText(SessionController s) => s.State switch
    {
        SessionState.Presenting when s.IsBlanked => "● The TV is blank",
        SessionState.Presenting when s.OutputIsFloating => $"● Showing in a window: {TrayMenuPlan.Shorten(s.Source?.Title)}",
        SessionState.Presenting => $"● Showing on {s.OutputMonitor?.ShortName}: {TrayMenuPlan.Shorten(s.Source?.Title)}",
        SessionState.IdleScreen => "○ The TV shows a blank screen",
        _ => "○ Not showing anything",
    };

    private static string? RecordingLine(RecordingController r) => r.Status switch
    {
        RecordingStatus.Starting => "● Starting to record…",
        RecordingStatus.Recording => $"● Recording {ScpApp.RecordingClockText(r.Elapsed)}: {TrayMenuPlan.Shorten(r.Description)}",
        RecordingStatus.Paused => $"❚❚ Recording paused at {ScpApp.RecordingClockText(r.Elapsed)}",
        RecordingStatus.Stopping => "● Saving the recording…",
        _ => null,
    };

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (Image image in _menuImages) image.Dispose();
        _idleIcon.Dispose();
        _presentingIcon.Dispose();
        _recordingIcon.Dispose();
    }
}
