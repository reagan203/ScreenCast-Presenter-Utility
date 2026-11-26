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
    public enum Section { General, Output, Recording, Privacy, Appearance, Rooms, Advanced }

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
        Section.Privacy => Icons.Lock,
        Section.Rooms => Icons.People,
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
        ["Zoom"] = Icons.Search,
        ["Countdown"] = Icons.Clock,
        ["Slideshow"] = Icons.Next,
        ["Small picture"] = Icons.OpenWindow,
        ["Small picture size"] = Icons.OpenWindow,
        ["When a TV is plugged in"] = Icons.Tv,
        ["Talk timer"] = Icons.Clock,
        ["Talk length"] = Icons.Clock,
        ["Show my clicks"] = Icons.Mouse,
        ["Ring around the mouse"] = Icons.Mouse,
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
        ["Check for updates"] = Icons.Download,
        ["Privacy"] = Icons.Lock,
        ["Never show these apps"] = Icons.Hide,
        ["Add an app by name"] = Icons.Keyboard,
        ["Remind me about notifications"] = Icons.Message,
        ["Version"] = Icons.Info,
        ["Second TV"] = Icons.Tv,
        ["Your name on the TV"] = Icons.Person,
        ["Second line"] = Icons.Person,
        ["Logo on the blank screen"] = Icons.Picture,
        ["QR code corner"] = Icons.QrCode,
        ["How hidden parts look"] = Icons.Hide,
        ["Hidden parts"] = Icons.Hide,
        ["Live captions"] = Icons.Captions,
        ["Save this setup as a room"] = Icons.People,
        ["Add a scheduled action"] = Icons.Calendar,
        ["Next scheduled"] = Icons.Calendar,
        ["Rooms"] = Icons.People,
        ["Phone remote"] = Icons.Phone,
        ["When what's shown closes"] = Icons.Hide,
        ["Remote's port"] = Icons.Phone,
        ["Live captions on the TV"] = Icons.Captions,
        ["Save a transcript"] = Icons.Captions,
        ["Transcripts folder"] = Icons.Folder,
        ["Caption size"] = Icons.Captions,
        ["Where captions go"] = Icons.Captions,
        ["Save this as a scene"] = Icons.Star,
        ["Scenes"] = Icons.Star,
        ["Make a playlist"] = Icons.List,
        ["Playlists"] = Icons.List,
        ["Play when ScreenCast Presenter starts"] = Icons.List,
    };

    private static string Label(Section s) => s switch
    {
        Section.Output => "Where it shows",
        Section.Recording => "Recording & screenshots",
        Section.Appearance => "Appearance",
        Section.Privacy => "Privacy",
        Section.Rooms => "Rooms, scenes & playlists",
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
            case Section.Privacy: BuildPrivacy(); break;
            case Section.Rooms: BuildRooms(); break;
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
        AddGroup("Phone remote");
        AddToggle("Phone remote", "Use your phone as a clicker on the same Wi-Fi: next and previous slide, blank, freeze, favourites and scenes. Off unless you switch it on. Pairing happens with a code only you see, and every command is signed, so others on the network can't take control.",
            s.PhoneRemote, on => _app.SetPhoneRemote(on), ("Pair a phone…", () => _ = _app.RunAsync("phone-remote")));
        AddToggle("Notes on the phone", "Your notes for what's shown appear on the paired phone, encrypted with its pairing key: others on the Wi-Fi can't read them.",
            s.NotesOnPhone, on => _app.SetNotesOnPhone(on));
        AddText("Remote's port", $"The network port the remote listens on (1024–65535; {Remote.RemoteServer.DefaultPort} unless changed). If it's in use, any free one is used.",
            s.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture), 5, text =>
            {
                if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port)) _app.SetRemotePort(port);
            });
        AddGroup("Voice commands");
        AddToggle("Voice commands", $"Say “Presenter, next slide” and it's done{Key("voice-commands")}. Windows' own speech recognition listens on this PC, only while something is shown; nothing is recorded or sent anywhere. Try: {string.Join(", ", VoiceCommands.Examples.Take(6).Select(e => "“Presenter, " + e + "”"))}.",
            s.VoiceCommands, _app.SetVoiceCommands);
        AddGroup("On your screen while you present");
        AddButton("My notes", $"A small window on your screen with the notes for what's on the TV{Key("notes")}. Notes belong to favourites and scenes.", "Open…", () => _ = _app.RunAsync("notes"));
        AddToggle("Yellow outline", "Marks on your screen what the audience sees. Never on the TV.", s.ShowOutline, on => _app.SetShowOutline(on));
        AddToggle("Now-showing bar", "A small bar on top while you present or record, like Teams' sharing bar. Click it to go to what's shown.", s.ShowWidget, on => _app.SetShowWidget(on));
        AddToggle("Status pill", "When the now-showing bar is hidden, a pill at the top of your screen says what's on the TV. Messages always appear there.", s.ShowBadge, on => _app.SetShowBadge(on));
        AddToggle("Mouse pointer on the TV", "Show your pointer in what you present.", s.ShowCursor, on => _app.SetShowCursor(on));
        AddToggle("Talk timer", "How long you've been presenting, in the now-showing bar. Only you see it.", s.ShowTalkTimer, _app.SetShowTalkTimer);
        AddChoice("Talk length", "You're told two minutes before the end, and when time's up. Only you see it.",
            [.. TalkTimer.Lengths.Select(m => (m == 0 ? "None" : $"{m} min", s.TalkMinutes == m, (Action)(() => _app.SetTalkMinutes(m))))]);
    }

    private void BuildOutput()
    {
        AddGroup("The TV over the cable");
        AddToggle("Turn the TV on when showing starts", "The TV wakes and switches to this PC when you show something. Works with screens that take commands over the cable (DDC/CI): most monitors, some TVs.",
            _app.Settings.TvWakeOnShow, _app.SetTvWakeOnShow, ("Test the TV", () => _ = _app.TestTvAsync()));
        AppSettings s = _app.Settings;
        OutputOption current = _app.CurrentOutputOption;
        var choices = _app.Monitors.Select(m => (m.IsVirtual ? $"Zoom screen ({m.Name})" : m.IsTv ? $"TV ({m.Name})" : m.Name,
            !current.IsWindow && current.Screen is not null && m.IsSameScreen(current.Screen), (Action)(() => _app.SetOutputMonitor(m)))).ToList();
        choices.Add(("A window", current.IsWindow, _app.SetOutputWindow));
        AddGroup("Screen");
        AddChoice("Show on", "Full screen on a TV or projector, or in a window you can move and share in a meeting.", choices);
        AddToggle("Browsers: keep to the page you chose", "If you switch tabs or pages in a browser window you're showing, the audience sees black until you go back, instead of the new page.",
            s.KeepBrowserPage, on => _app.SetKeepBrowserPage(on));
        AddToggle("When a TV is plugged in", "Offer to show the last thing you showed on it, in one click.", s.OfferNewScreen, _app.SetOfferNewScreen);
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
        AddChoice("Zoom", $"How much closer zooming in brings the TV{Key("zoom-in")}. It follows your mouse over what you show.",
            [.. ZoomView.Levels.Select(l => ($"{l:0.#}×", Math.Abs(s.ZoomLevel - l) < 0.001, (Action)(() => _app.SetZoomLevel(l))))]);
        AddChoice("Zoom follows", $"Where the zoomed-in view goes{Key("zoom-follows")}. Following where you type keeps a form or code readable as you fill it in.",
            [.. Enum.GetValues<ZoomFollows>().Select(f => (char.ToUpperInvariant(ZoomFollow.Words(f)[0]) + ZoomFollow.Words(f)[1..], s.ZoomFollowsMode == f, (Action)(() => _app.SetZoomFollows(f))))]);
        AddToggle("Show my clicks", $"A ring appears on the TV wherever you click on what you show{Key("highlight-clicks")}.", s.HighlightClicks, _app.SetHighlightClicks);
        AddToggle("Show my shortcuts", $"Key combinations you press in what's shown appear on the TV for a moment, like Ctrl + C{Key("show-keys")}. Only combinations with Ctrl, Alt or the Windows key, the F-keys and Esc: what you type never shows.", s.ShowKeys, _app.SetShowKeys);
        AddToggle("Ring around the mouse", $"A soft yellow ring follows your mouse on the TV, so a room can see it{Key("pointer-ring")}.", s.PointerRing, _app.SetPointerRing);
        string key = _app.ActiveShortcut("blank-message") is { } k ? $" ({k})" : "";
        AddText("Blank screen message", $"Shown on the TV when you blank it with a message{key}.", s.BlankMessage, AppSettings.MaxMessageLength, _app.SetBlankMessage);
        AddChoice("Slideshow", $"How long each favourite shows when they take turns{Key("slideshow")}.",
            [.. Slideshow.Seconds.Select(sec => (Slideshow.Words(sec), s.SlideshowSeconds == sec, (Action)(() => _app.SetSlideshowSeconds(sec))))]);
        InsetCorner corner = InsetLayout.ParseCorner(s.InsetCorner);
        AddChoice("Small picture", $"Where a small picture (like you on a webcam) sits over what you show{Key("inset")}.",
            [.. Enum.GetValues<InsetCorner>().Select(c => (Capitalise(InsetLayout.Words(c)), c == corner, (Action)(() => _app.SetInsetPlace(c, s.InsetSize))))]);
        AddChoice("Small picture size", "How wide it is on the TV.",
            [.. InsetLayout.Sizes.Select(p => (p == InsetLayout.Sizes[0] ? "Small" : p == InsetLayout.Sizes[^1] ? "Large" : "Medium", s.InsetSize == p, (Action)(() => _app.SetInsetPlace(corner, p))))]);
        AddChoice("Countdown", $"Blank with a countdown{Key("blank-countdown")} shows the message above and the time left. When it ends the TV stays blank until you show it again.",
            [.. BlankCountdown.Minutes.Select(m => ($"{m} min", s.CountdownMinutes == m, (Action)(() => _app.SetCountdownMinutes(m))))]);
        AddLogo(s.BlankLogo);
        AddToggle("When what's shown closes", "The TV shows the blank screen's message (and your logo) instead of plain black until you show something else.", s.IdleShowsMessage, _app.SetIdleShowsMessage);
        AddGroup("Live captions and transcripts");
        AddToggle("Live captions on the TV", $"Live captions{Key("live-captions")} show what you say on the TV. Off: they only write the transcript.", s.CaptionsOnTv, _app.SetCaptionsOnTv);
        AddToggle("Save a transcript", "Everything live captions hear is written to a text file as it's said, with subtitles (.srt and .vtt) beside it when they stop. On this PC only.", s.SaveTranscript, _app.SetSaveTranscript);
        AddToggle("Save a timeline", "What was shown when (“09:31 Showing Q3 results”), written as it happens to a text file in the transcripts folder. Names only.", s.SaveTimeline, _app.SetSaveTimeline);
        AddFolder("Transcripts folder", s.TranscriptFolder ?? SavedFiles.DefaultTranscriptFolder, s.TranscriptFolder is null, _app.SetTranscriptFolder);
        CaptionLook look = s.CaptionLook;
        AddChoice("Caption size", "Bigger captions are easier to read from the back of the room, and fit fewer words.",
        [
            ("Normal", look.Size == CaptionSize.Normal, () => _app.SetCaptionLook(look with { Size = CaptionSize.Normal })),
            ("Large", look.Size == CaptionSize.Large, () => _app.SetCaptionLook(look with { Size = CaptionSize.Large })),
            ("Extra large", look.Size == CaptionSize.ExtraLarge, () => _app.SetCaptionLook(look with { Size = CaptionSize.ExtraLarge })),
        ]);
        AddChoice("Where captions go", "At the top they don't cover the bottom of slides, where subtitles and footers are.",
        [
            ("Bottom", look.Place == CaptionPlace.Bottom, () => _app.SetCaptionLook(look with { Place = CaptionPlace.Bottom })),
            ("Top", look.Place == CaptionPlace.Top, () => _app.SetCaptionLook(look with { Place = CaptionPlace.Top })),
        ]);
        AddText("Your name on the TV", $"The name banner{Key("caption")} shows it along the bottom of the TV, like on the news.", s.CaptionName, NameCaption.MaxLength, _app.SetCaptionName);
        AddText("Second line", "Under your name, e.g. your job or team. Optional.", s.CaptionSecond, NameCaption.MaxLength, _app.SetCaptionSecond);
        InsetCorner qr = s.QrPlace;
        AddChoice("QR code corner", $"Where a QR code{Key("qr")} sits. If the small picture is in that corner, the QR code moves to the other top corner.",
            [.. Enum.GetValues<InsetCorner>().Select(c => (Capitalise(InsetLayout.Words(c)), c == qr, (Action)(() => _app.SetQrCorner(c))))]);
        AddGroup("Second TV");
        IReadOnlyList<DisplayMonitor> candidates = _app.SecondTvCandidates();
        DisplayMonitor? second = _app.SecondTvScreen;
        if (candidates.Count == 0)
            AddInfo("Second TV", $"Connect another TV or screen to show something different on it{Key("second-tv")}. The first TV keeps showing what it shows.");
        else
            AddChoice("Second TV", $"Show something different on another TV{Key("second-tv")}: choose it from the tray menu under Show on. The first TV keeps showing what it shows.",
                [.. candidates.Select(m => (m.IsTv ? $"TV ({m.Name})" : m.Name, second is not null && m.IsSameScreen(second), (Action)(() => _app.SetSecondTvScreen(m))))]);
    }

    /// <summary>Your logo above the blank screen's message (FR-80): choose a picture, or take it off.</summary>
    private void AddLogo(string? current)
    {
        Row row = NewRow("Logo on the blank screen", current is null
            ? "A picture (like your company's logo) above the message when you blank the TV with a message."
            : $"Shows above the message when you blank the TV with a message. ({Path.GetFileName(current)})");
        row.AddControl(Pill(current is null ? "Choose a picture…" : "Change…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = LogoImage.Filter, Title = "Choose your logo", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK && _app.SetBlankLogo(dialog.FileName)) Rebuild();
        }));
        if (current is not null) row.AddControl(Pill("Remove", () =>
        {
            _app.SetBlankLogo(null);
            Rebuild();
        }, PillKind.Ghost));
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
        AddToggle("Keep the last minute", $"While something is shown full screen, the last part of what the audience sees is kept on this PC (the oldest deleted as it goes), so you can save it when something goes well{Key("save-replay")}. Uses a little more of the computer while on.", s.ReplayOn, _app.SetReplay);
        AddChoice("How much is kept", "Saved as one video in your recordings folder.",
            [.. Recording.ReplayBuffer.Choices.Select(c => (Slideshow.Words(c), s.ReplaySeconds == c, (Action)(() => _app.SetReplaySeconds(c))))]);
        AddToggle("Chapters", "Each time what's shown changes while recording, a chapter starts. Saved beside the video for web players (.chapters.vtt), video sites (.chapters.txt) and FFmpeg (.ffmetadata.txt).", s.RecordingChapters, _app.SetRecordingChapters);
    }

    private void BuildPrivacy()
    {
        if (_app.Policy.Any)
        {
            AddGroup("Set by your organisation");
            AddInfo("Set by your organisation", string.Join(" ", _app.Policy.Words()) + " These can't be changed here.");
        }
        AddGroup("Never show");
        AddAppList("Never show these apps",
            "Their windows can't be chosen, and when you show a whole screen it turns black while one of them is open on it. Password managers inside a browser are part of the browser window, so they can't be told apart.",
            _app.Settings.NeverShowApps);
        AddText("Add an app by name", "The program's name, like “KeePass” for KeePass.exe. Press Enter to add it.", "", NeverShow.MaxNameLength, name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            _app.AddNeverShow(name);
            Rebuild();
        });
        AddGroup("Hidden parts");
        RedactStyle look = _app.Settings.HiddenPartStyle;
        AddChoice("How hidden parts look", $"Hide part of a window{Key("hide-part")} by dragging a box over it: the audience sees this there instead.",
        [
            ("Solid box", look == RedactStyle.Solid, () => _app.SetHiddenPartStyle(RedactStyle.Solid)),
            ("Blurred", look == RedactStyle.Blurred, () => _app.SetHiddenPartStyle(RedactStyle.Blurred)),
        ]);
        AddHiddenParts(_app.Settings.HiddenParts);
        AddGroup("Hide automatically");
        AddToggle("Check before showing", $"The first time each window or screen is shown, it's read for private details first. If something is found that wouldn't be covered, you see it and choose{Key("check-before-showing")}.", _app.Settings.CheckBeforeShowing, _app.SetCheckBeforeShowing);
        SensitiveKinds auto = _app.Settings.AutoHideKinds;
        AddInfo("Hide automatically", "Windows reads the text in what you show, on this PC, and what you tick is covered on the TV as soon as it's found. It's a moment behind what's shown, so hide parts you know about yourself, and keep private apps on the never-show list.");
        foreach ((SensitiveKinds kind, string title, string description) in new[]
        {
            (SensitiveKinds.Emails, "Email addresses", "name@example.com"),
            (SensitiveKinds.Phones, "Phone numbers", "Numbers written like phone numbers: +44 20 7946 0958, (555) 010-9999."),
            (SensitiveKinds.Cards, "Card numbers", "Payment card numbers (checked, so ordinary long numbers aren't covered)."),
            (SensitiveKinds.Ibans, "Bank account numbers", "IBANs (checked the same way)."),
            (SensitiveKinds.IdNumbers, "ID numbers", "US Social Security and UK National Insurance numbers."),
            (SensitiveKinds.Secrets, "Keys and passwords", "API keys and access tokens (AWS, GitHub, Slack, Stripe, web tokens), and what follows \"password:\", \"PIN:\" or \"API key:\"."),
            (SensitiveKinds.Custom, "Words from your list", "Client names, code names, anything else you list below."),
            (SensitiveKinds.PasswordFields, "Password boxes", "Found exactly by Windows, not by reading the picture: covered whatever they show, even a revealed password."),
        })
        {
            SensitiveKinds k = kind;
            AddToggle(title, description, auto.HasFlag(k), on => _app.SetAutoHide(on ? _app.Settings.AutoHideKinds | k : _app.Settings.AutoHideKinds & ~k));
        }
        bool faces = auto.HasFlag(SensitiveKinds.Faces), keepMine = auto.HasFlag(SensitiveKinds.KeepLargestFace);
        AddChoice("Blur faces", ScreenCastPresenter.Capture.FaceFinder.IsSupported
                ? "Faces in what's shown (a camera, a video call) are blurred on the TV, in screenshots and recordings. Windows finds them on this PC; nobody is recognised. “Everyone but me” keeps the largest face (you, at your own camera) clear."
                : "Windows can't find faces on this PC, so this does nothing here.",
        [
            ("Off", !faces, () => _app.SetAutoHide(_app.Settings.AutoHideKinds & ~(SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace))),
            ("Everyone", faces && !keepMine, () => _app.SetAutoHide((_app.Settings.AutoHideKinds | SensitiveKinds.Faces) & ~SensitiveKinds.KeepLargestFace)),
            ("Everyone but me", faces && keepMine, () => _app.SetAutoHide(_app.Settings.AutoHideKinds | SensitiveKinds.Faces | SensitiveKinds.KeepLargestFace)),
        ]);
        AddText("Your own words", $"Covered wherever they're seen, in any capitals. Separate them with commas (at most {SensitiveText.MaxCustomWords}).",
            string.Join(", ", _app.Settings.AutoHideWords), SensitiveText.MaxCustomWords * (SensitiveText.MaxCustomWordLength + 2),
            text => _app.SetAutoHideWords(text.Split(',')));
        AddGroup("Notifications");
        AddToggle("Remind me about notifications", "Windows' pop-up notifications appear on your main screen. When you show or record all of it, you're reminded to turn on Do not disturb.",
            _app.Settings.RemindDoNotDisturb, _app.SetRemindDoNotDisturb);
        AddGroup("Microphone");
        AddInfo("Live captions", $"Live captions{Key("live-captions")} listen to your microphone only while they're on. Windows' own speech recognition works out the words on this PC; nothing is recorded or sent anywhere.");
        AddInfo("Privacy", PrivacyText);
    }

    /// <summary>The apps and screens with hidden parts: each as a pill, click to forget its boxes.</summary>
    private void AddHiddenParts(IReadOnlyDictionary<string, List<CropRegion>> parts)
    {
        Row row = NewRow("Hidden parts", parts.Count == 0
            ? "Nothing is hidden yet. While you show something, hide part of it from the tray menu or with the shortcut."
            : "Kept for these apps and screens, and covered whenever they're shown. Click one to show everything in it again.");
        if (parts.Count == 0) return;
        var pills = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(560, 0) };
        foreach ((string key, List<CropRegion> boxes) in parts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string name = key.StartsWith("screen:", StringComparison.Ordinal) ? _app.Monitors.FirstOrDefault(m => m.DeviceName == key["screen:".Length..])?.Name ?? "A screen" : key;
            string forget = key;
            PillButton pill = Pill($"{name} ({boxes.Count})   ✕", () =>
            {
                _app.ForgetHiddenParts(forget);
                Rebuild();
            }, PillKind.Selected);
            pill.AccessibleName = $"{name} has {boxes.Count} hidden parts. Click to show everything in it again.";
            pills.Controls.Add(pill);
        }
        row.AddBelow(pills);
    }

    /// <summary>Room profiles (FR-82) and schedules (FR-83).</summary>
    private void BuildRooms()
    {
        AppSettings s = _app.Settings;
        AddGroup("Rooms");
        AddButton("Save this setup as a room", "Set things up for a room (the TV, dimming, extras, the blank screen, your name, favourites), then save it here. Next time, choose it from the tray menu under Rooms.",
            "Save as a room…", () =>
            {
                _app.SaveRoom();
                Rebuild();
            });
        if (s.Rooms.Count == 0) AddInfo("Rooms", "No rooms yet.");
        foreach (RoomProfile room in s.Rooms)
        {
            string name = room.Name;
            Row row = NewRow(name, RoomWords(room));
            row.AddControl(Pill("Use it", () => _ = _app.ApplyRoomAsync(name)));
            row.AddControl(Pill("Remove", () =>
            {
                _app.DeleteRoom(name);
                Rebuild();
            }, PillKind.Ghost));
        }

        AddGroup("Scenes");
        AddButton("Save this as a scene", "What's shown and everything around it (the part shown, the small picture, your name, a QR code for a link, the spotlight, dimming), on Ctrl+Alt+F1 to F9.",
            "Save as a scene…", () =>
            {
                _ = _app.RunAsync("save-scene");
                Rebuild();
            });
        bool anyScene = false;
        for (int i = 0; i < _app.Scenes.Count; i++)
        {
            if (_app.Scenes[i] is not { } scene) continue;
            anyScene = true;
            int slot = i + 1;
            Row row = NewRow($"{slot}   {scene.Title(slot)}", scene.Words() + Key(ShortcutCatalog.SceneId(slot)));
            row.AddControl(Pill("Show it", () => _ = _app.RunAsync(ShortcutCatalog.SceneId(slot))));
            row.AddControl(Pill("Notes…", () =>
            {
                _app.EditSceneNotes(slot);
                Rebuild();
            }, PillKind.Ghost));
            row.AddControl(Pill("Remove", () =>
            {
                _app.DeleteScene(slot);
                Rebuild();
            }, PillKind.Ghost));
        }
        if (!anyScene) AddInfo("Scenes", "No scenes yet.");

        AddGroup("Playlists");
        AddButton("Make a playlist", "Favourites and scenes taking turns on the TV or the second TV, round and round: a lobby or waiting-room screen.", "Make one…", () =>
        {
            _app.MakePlaylist();
            Rebuild();
        });
        foreach (Playlist playlist in s.Playlists)
        {
            string name = playlist.Name;
            Row row = NewRow(name, $"{(playlist.Where == PlaylistScreen.Second ? "Second TV" : "TV")}{(playlist.Shuffle ? " · shuffled" : "")} · " + string.Join(", ", playlist.Items.Select(item => item.Words())));
            row.AddControl(Pill("Play", () => _ = _app.StartPlaylistAsync(name)));
            bool shuffled = playlist.Shuffle;
            row.AddControl(Pill(shuffled ? "In order" : "Shuffle", () =>
            {
                _app.SetPlaylistShuffle(name, !shuffled);
                Rebuild();
            }, PillKind.Ghost));
            row.AddControl(Pill("Remove", () =>
            {
                _app.DeletePlaylist(name);
                Rebuild();
            }, PillKind.Ghost));
        }
        if (s.Playlists.Count == 0) AddInfo("Playlists", "No playlists yet.");
        else
            AddChoice("Play when ScreenCast Presenter starts", "For a screen that runs itself: with “Start with Windows” on, the PC starting up starts the playlist.",
                [("None", s.PlaylistAtStart is null, () => _app.SetPlaylistAtStart(null)), .. s.Playlists.Select(p => (p.Name, string.Equals(p.Name, s.PlaylistAtStart, StringComparison.OrdinalIgnoreCase), (Action)(() => _app.SetPlaylistAtStart(p.Name))))]);

        AddGroup("Calendar");
        CalendarLink link = s.Calendar ?? new CalendarLink();
        string nextBooking = _app.NextBooking() is { } booking ? $" Next: “{booking.Summary}” at {booking.Start:ddd HH:mm}." : "";
        AddButton("Room calendar", (link.File is { } file ? $"{Path.GetFileName(file)}.{nextBooking}" : "None.") + " An .ics file of this room's bookings (export it from Outlook or Google, or keep a synced copy): bookings set the room up and tidy it away. Read on this PC; nothing is fetched.",
            link.File is null ? "Choose a file…" : "Change…", () =>
            {
                using var dialog = new OpenFileDialog { Title = "Choose the room's calendar", Filter = "Calendar (*.ics)|*.ics" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                link.File = dialog.FileName;
                link.AtStart ??= new ScheduleEntry { Action = nameof(ScheduleAction.TvOn) };
                link.AtEnd ??= new ScheduleEntry { Action = nameof(ScheduleAction.Stop) };
                _app.SetCalendar(link);
                Rebuild();
            });
        if (link.File is not null)
        {
            var choices = ScheduleChoices.Actions(s.Favourites, s.Rooms, s.Scenes, s.Playlists);
            (string, bool, Action)[] Options(ScheduleEntry? current, Action<ScheduleEntry?> set) =>
            [
                ("Nothing", current is null, () => set(null)),
                .. choices.Select(c => (c.Words, current is { } e && e.What == c.Action && e.Slot == c.Slot && (e.Room ?? e.Playlist) == c.Room,
                    (Action)(() => set(new ScheduleEntry { Action = c.Action.ToString(), Slot = c.Slot, Room = c.Action == ScheduleAction.Room ? c.Room : null, Playlist = c.Action == ScheduleAction.StartPlaylist ? c.Room : null })))),
            ];
            AddChoice("When a booking starts", "Done this many minutes before it starts (below).", Options(link.AtStart, e => { link.AtStart = e; _app.SetCalendar(link); Rebuild(); }));
            AddChoice("Start it early", "", [.. new[] { 0, 5, 10, 15 }.Select(m => (m == 0 ? "On time" : $"{m} min before", link.LeadMinutes == m, (Action)(() => { link.LeadMinutes = m; _app.SetCalendar(link); Rebuild(); })))]);
            AddChoice("When a booking ends", "", Options(link.AtEnd, e => { link.AtEnd = e; _app.SetCalendar(link); Rebuild(); }));
            AddText("Only bookings named", "Only bookings whose title contains these words (empty for all). Press Enter to keep it.", link.Match, 60, text => { link.Match = text; _app.SetCalendar(link); });
            AddToggle("Use the calendar", "", link.Enabled, on => { link.Enabled = on; _app.SetCalendar(link); }, ("Forget it", () => { _app.SetCalendar(null); Rebuild(); }));
        }

        AddGroup("Schedule");
        AddButton("Add a scheduled action", "Start the lobby slideshow at 8:00, stop it at 18:00, switch rooms… Uses this PC's clock; ScreenCast Presenter must be running (it can start with Windows).",
            "Add…", AddScheduleEntry);
        for (int i = 0; i < s.Schedule.Count; i++)
        {
            int index = i;
            ScheduleEntry entry = s.Schedule[i];
            Row row = NewRow(entry.Words(FavouriteName), entry.Enabled ? "On" : "Off");
            var toggle = new ToggleSwitch { Checked = entry.Enabled, AccessibleName = entry.Words() };
            toggle.CheckedChanged += (_, _) => _app.Guard(() =>
            {
                _app.SetScheduleEntryEnabled(index, toggle.Checked);
                Rebuild();
            });
            row.AddControl(toggle);
            row.AddControl(Pill("Remove", () =>
            {
                _app.RemoveScheduleEntry(index);
                Rebuild();
            }, PillKind.Ghost));
        }
        if (Scheduler.Next(s.Schedule, DateTime.Now) is { } next)
            AddInfo("Next scheduled", $"{next.When.ToString("ddd HH:mm", System.Globalization.CultureInfo.CurrentCulture)}: {next.Entry.WhatWords(FavouriteName)}");
    }

    private string FavouriteName(int slot) =>
        slot >= 1 && slot <= _app.Settings.Favourites.Length && _app.Settings.Favourites[slot - 1] is { } f ? $"favourite {slot} (“{TrayMenuPlan.Shorten(f.Title, 30)}”)" : $"favourite {slot}";

    /// <summary>"TV (Screen 2) · dimmed 20% · clicks · 4 favourites".</summary>
    private string RoomWords(RoomProfile room)
    {
        var parts = new List<string>();
        if (room.OutputMode == "Window") parts.Add("in a window");
        else if (room.OutputDevice is { } device) parts.Add(_app.Monitors.FirstOrDefault(m => m.DeviceName == device)?.Name ?? "a screen not connected now");
        if (room.Dim > 0) parts.Add($"dimmed {room.Dim:P0}");
        if (room.HighlightClicks) parts.Add("clicks");
        if (room.PointerRing) parts.Add("ring around the mouse");
        if (room.CaptionName.Length > 0) parts.Add($"name: {room.CaptionName}");
        if (room.BlankLogo is not null) parts.Add("logo");
        if (room.Favourites is { } favs) parts.Add($"{favs.Count(f => f is not null)} favourites");
        return string.Join(" · ", parts);
    }

    /// <summary>A time, the days and what to do.</summary>
    private void AddScheduleEntry()
    {
        if (_app.Settings.Schedule.Count >= ScheduleEntry.Max)
        {
            ConfirmForm.Tell("The schedule is full", $"It holds up to {ScheduleEntry.Max} actions. Remove one first.");
            return;
        }
        IReadOnlyList<(string Words, ScheduleAction Action, int Slot, string? Room)> actions = ScheduleChoices.Actions(_app.Settings.Favourites, _app.Settings.Rooms, _app.Settings.Scenes, _app.Settings.Playlists);
        using var form = new FieldsForm("Do something at a set time", "This PC's clock is used. If the PC is asleep at that time, it's skipped rather than done late.", "Add it");
        (_, TextBox time) = form.AddField("Time (24-hour clock)", "08:00", 5, "08:00");
        ComboBox days = form.AddList("Days", [.. ScheduleChoices.Days.Select(d => d.Words)], 0);
        ComboBox what = form.AddList("What to do", [.. actions.Select(a => a.Words)], 0);
        form.Check = () => ScheduleEntry.ParseTime(time.Text) is null ? "Type the time like 08:00 or 18:30." : null;
        if (!form.Ask()) return;
        var chosen = actions[Math.Max(0, what.SelectedIndex)];
        _app.AddScheduleEntry(new ScheduleEntry
        {
            Time = ScheduleEntry.ParseTime(time.Text)!.Value.ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture),
            Days = ScheduleChoices.Days[Math.Max(0, days.SelectedIndex)].Days,
            Action = chosen.Action.ToString(),
            Slot = chosen.Slot,
            Room = chosen.Action == ScheduleAction.Room ? chosen.Room : null,
            Playlist = chosen.Action == ScheduleAction.StartPlaylist ? chosen.Room : null,
        });
        Rebuild();
    }

    /// <summary>What ScreenCast Presenter does and doesn't do with data.</summary>
    private const string PrivacyText = "ScreenCast Presenter sends no data anywhere and collects nothing about you. Recordings and screenshots stay on this PC. It only goes online when you ask: setting up a Zoom screen downloads the driver through Windows, and “Check for updates” (Advanced, off unless you switch it on) asks GitHub once a day which version is the newest.";

    /// <summary>
    /// The never-show list: each app as a pill (click to take it off), then "Add an app…" with the
    /// apps open now, and "Add the usual password managers" when some are missing.
    /// </summary>
    private void AddAppList(string title, string description, IReadOnlyList<string> apps)
    {
        Row row = NewRow(title, description);
        var pills = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(560, 0) };
        foreach (string app in apps)
        {
            string name = app;
            PillButton pill = Pill($"{name}   ✕", () =>
            {
                _app.RemoveNeverShow(name);
                Rebuild();
            }, PillKind.Selected);
            pill.AccessibleName = $"{name} is never shown. Click to take it off the list.";
            pills.Controls.Add(pill);
        }
        PillButton add = Pill("Add an app…", () => { });
        add.Click += (_, _) => ShowAddAppMenu(add);
        pills.Controls.Add(add);
        if (NeverShow.Defaults.Any(d => !NeverShow.IsBlocked(apps, d)))
            pills.Controls.Add(Pill("Add the usual password managers", () =>
            {
                _app.RestoreNeverShowDefaults();
                Rebuild();
            }, PillKind.Ghost));
        row.AddBelow(pills);
    }

    /// <summary>The apps with a window open now, not yet on the list.</summary>
    private void ShowAddAppMenu(Control anchor)
    {
        var menu = new ContextMenuStrip { Font = Theme.UiFont };
        ThemedMenuRenderer.ApplyPopupChrome(menu);
        var open = WindowEnumerator.GetPresentableWindows()
            .GroupBy(w => w.AppName, StringComparer.OrdinalIgnoreCase)
            .Where(g => !NeverShow.IsBlocked(_app.Settings.NeverShowApps, g.Key))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (IGrouping<string, SourceWindow> app in open)
        {
            string name = app.Key;
            string example = TrayMenuPlan.Shorten(app.First().Title, 40);
            menu.Items.Add($"{name}   ·   {example}", null, (_, _) => _app.Guard(() =>
            {
                _app.AddNeverShow(name);
                Rebuild();
            }));
        }
        if (open.Count == 0) menu.Items.Add(new ToolStripMenuItem("No other apps are open. Type a name below.") { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
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
        AddChoice("How windows are shown", "With the mouse pointer on the TV switched on (General), windows are always shown with it. Otherwise Automatic uses the least memory, and “Always show the pointer” keeps a minimised window's last picture on the TV." + inUse,
        [
            ("Automatic", !wgc, () => _app.SetCaptureEngine(false)),
            ("Always show the pointer", wgc, () => _app.SetCaptureEngine(true)),
        ]);
        AddToggle("Check for updates", "Once a day, ask GitHub whether a newer version is out, and say so here. Only that question is sent: nothing about you or this PC. Nothing is downloaded or installed for you.",
            _app.Settings.CheckForUpdates, _app.SetCheckForUpdates, ("Check now", () => _ = _app.CheckForUpdatesAsync(manual: true)));
        AddButton("Settings file", _app.SettingsFilePath, "Show in folder", () => _app.ShowInFolder(_app.SettingsFilePath));
        AddButton("Copy settings to another PC", "Save these settings to a file, then load it on another PC (or with --import-settings in a setup script). The phone remote's pairing stays on each PC.",
            "Save to a file…", _app.ExportSettings);
        AddButton("Load settings from a file", "They replace these settings, and ScreenCast Presenter starts again to use them.", "Load…", _app.ImportSettings);
        AddInfo("Version", _app.VersionText);
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>" (Ctrl+Alt+Z)" when that shortcut works; nothing otherwise.</summary>
    private string Key(string actionId) => _app.ActiveShortcut(actionId) is { } k ? $" ({k})" : "";

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
