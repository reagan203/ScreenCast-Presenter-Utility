using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// For trainers and stages (SRS 3.12, rev 10): Follow me (FR-119), a timer on the TV (FR-120),
/// time budgets (FR-121), PowerPoint's notes (FR-122), the night filter (FR-123), the speaker's
/// screen (FR-124), casting to a wireless TV (FR-126), rehearsals (FR-127), clicker keys (FR-132),
/// the small picture's shape (FR-118), copying text (FR-130) and GIFs (FR-131).
/// </summary>
internal sealed partial class ScpApp
{
    // ───────────────────────────── Follow me (FR-119) ─────────────────────────────

    private readonly ForegroundWatcher _followWatch = new();
    private readonly System.Windows.Forms.Timer _followSettle = new() { Interval = (int)FollowMe.Settle.TotalMilliseconds };
    private bool _followMe;
    private bool _followSwitching;

    public bool FollowMeOn => _followMe;

    /// <summary>"follow-me": the TV follows the presenter to the windows of the apps on the list; again turns it off.</summary>
    private void ToggleFollowMe()
    {
        if (_followMe)
        {
            SetFollowMe(false);
            Message("Follow me is off: the TV keeps showing what you chose.");
            return;
        }
        if (_session.State != SessionState.Presenting || _session.Source is not { } shown)
        {
            Message("Show a window first. Then Follow me shows each window you switch to, from the apps you've shown.");
            return;
        }
        if (shown.IsWindow && !Settings.FollowMeApps.Contains(shown.AppName, StringComparer.OrdinalIgnoreCase) && !IsNeverShown(shown))
        {
            Settings.FollowMeApps = FollowMe.Normalize([.. Settings.FollowMeApps, shown.AppName], Settings.NeverShowApps);
            Save();
        }
        if (Settings.FollowMeApps.Count == 0)
        {
            Message("Follow me follows you between apps you've shown. Show a window of an app first.");
            return;
        }
        SetFollowMe(true);
        Message($"Follow me is on: switch to a window of {AppList(Settings.FollowMeApps)} and the TV shows it. Anything else stays private. Show another app once and it's followed too.", BadgeDot.Accent,
            Button("Turn it off", () => SetFollowMe(false), primary: false));
    }

    private static string AppList(IReadOnlyList<string> apps) =>
        apps.Count switch { 0 => "no apps", 1 => apps[0], 2 => $"{apps[0]} or {apps[1]}", _ => $"{string.Join(", ", apps.Take(apps.Count - 1))} or {apps[^1]}" };

    private void SetFollowMe(bool on)
    {
        _followMe = on && !_quitting;
        if (_followMe) _followWatch.Start();
        else
        {
            _followWatch.Stop();
            _followSettle.Stop();
        }
        _tray.Refresh();
        UpdateWidget();
    }

    /// <summary>Another window came to the front: once it has stayed there a moment, the TV may follow.</summary>
    private void OnFollowForeground()
    {
        if (!_followMe) return;
        _followSettle.Stop();
        _followSettle.Start();
    }

    private async Task OnFollowSettledAsync()
    {
        _followSettle.Stop();
        if (!_followMe || _session.State != SessionState.Presenting || _session.IsFrozen) return; // frozen: the presenter works privately
        if (WindowEnumerator.GetActiveWindow() is not { } active) return; // the desktop, a dialog box, the taskbar
        if (!FollowMe.ShouldFollow(active.AppName, active.Handle, active.IsMinimized, _session.Source?.Handle ?? 0, Settings.FollowMeApps, Settings.NeverShowApps)) return;
        _followSwitching = true;
        try
        {
            await PresentAsync(active);
        }
        finally
        {
            _followSwitching = false;
        }
    }

    /// <summary>Settings › General › Follow me: take an app off the list.</summary>
    public void RemoveFollowMeApp(string app)
    {
        Settings.FollowMeApps.RemoveAll(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    // ───────────────────────────── clicker keys (FR-132) ─────────────────────────────

    private SlideKeyCatcher? _clicker;
    private bool _toldClickerTaken;

    /// <summary>Page Down / Page Up are taken only while something they can move is shown: a window, a PDF or a video.</summary>
    private void UpdateClickerKeys()
    {
        bool want = !_quitting && Settings.ClickerKeys && _session.State == SessionState.Presenting
            && _session.Source is { } s && (s.IsWindow || s.IsDocument || s.IsVideo);
        if (!want)
        {
            _clicker?.Stop();
            return;
        }
        if (_clicker is null)
        {
            _clicker = new SlideKeyCatcher();
            _clicker.Pressed += (_, forward) => Guard(() => SendSlide(forward, fromClicker: true));
        }
        if (_clicker.IsOn) return;
        if (!_clicker.Start(Settings.ClickerArrows) && !_toldClickerTaken)
        {
            _toldClickerTaken = true;
            Message("Another app already uses Page Down or Page Up, so they can't be sent to what's shown.", BadgeDot.Warning);
        }
    }

    /// <summary>Settings › General › Clicker keys.</summary>
    public void SetClickerKeys(bool on, bool arrows)
    {
        Settings.ClickerKeys = on;
        Settings.ClickerArrows = arrows;
        Save();
        _clicker?.Stop(); // taken again with the new keys
        UpdateClickerKeys();
        _tray.Refresh();
    }

    // ───────────────────────────── a timer on the TV (FR-120) ─────────────────────────────

    private long? _timerEnd;           // Environment.TickCount64 when it ends
    private long? _timerUpSince;
    private string? _timerSent;

    public bool TimerOn => _timerEnd is not null;

    /// <summary>"timer": asks how long and what it's for, then counts down in a corner of the TV; again takes it off.</summary>
    private void AskTimer()
    {
        if (TimerOn)
        {
            StopTimer("The timer is off the TV.");
            return;
        }
        if (_session.State == SessionState.Off)
        {
            Message("Show something first, then put a timer on it.");
            return;
        }
        using var form = new FieldsForm("A timer on the TV", "It counts down in a corner of the TV while what's shown stays up: for an exercise, group work or questions.", "Start it");
        (_, TextBox minutes) = form.AddField("Minutes", Settings.TimerMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, "5");
        (_, TextBox label) = form.AddField("What it's for", Settings.TimerLabel, ActivityTimer.MaxLabel, "Exercise");
        form.Check = () => int.TryParse(minutes.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int m) && m is >= 1 and <= ActivityTimer.MaxMinutes
            ? null : $"Type a number of minutes from 1 to {ActivityTimer.MaxMinutes}.";
        if (!form.Ask()) return;
        StartTimer(int.Parse(minutes.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture), label.Text);
    }

    /// <summary>Starts the timer on the TV (also "timer:N" from the tray, with the last words).</summary>
    public void StartTimer(int minutes, string? label = null)
    {
        if (_session.State == SessionState.Off)
        {
            Message("Show something first, then put a timer on it.");
            return;
        }
        Settings.TimerMinutes = ActivityTimer.Normalize(minutes);
        Settings.TimerLabel = ActivityTimer.TidyLabel(label ?? Settings.TimerLabel);
        Save();
        _timerEnd = Environment.TickCount64 + Settings.TimerMinutes * 60_000L;
        _timerUpSince = null;
        _timerSent = null;
        ApplyMarksChange(); // shown the light way: again through the helper, which draws it
        UpdateExtrasClock(); // it ticks each second from now
        TickExtras();
        _tray.Refresh();
        Message($"“{Settings.TimerLabel}” counts down {Settings.TimerMinutes} minute{(Settings.TimerMinutes == 1 ? "" : "s")} on {AudienceScreenName}.", BadgeDot.Accent,
            Button("Take it off", () => StopTimer(null), primary: false));
    }

    private void StopTimer(string? why)
    {
        if (_timerEnd is null) return;
        _timerEnd = null;
        _timerUpSince = null;
        _timerSent = null;
        _session.Effects?.Timer(null, InsetCorner.TopLeft, false);
        ApplyMarksChange();
        UpdateExtrasClock();
        _tray.Refresh();
        if (why is not null) Message(why);
    }

    private InsetCorner TimerCorner() =>
        ActivityTimer.Corner(_qrText is not null ? QrCorner() : null, _inset is not null ? InsetLayout.ParseCorner(Settings.InsetCorner) : null);

    private void TickTimer()
    {
        if (_timerEnd is not { } end) return;
        long now = Environment.TickCount64;
        var left = TimeSpan.FromMilliseconds(Math.Max(0, end - now));
        if (left == TimeSpan.Zero && _timerUpSince is null)
        {
            _timerUpSince = now;
            Message($"Time's up for “{Settings.TimerLabel}”.", BadgeDot.Accent, Button("Take it off", () => StopTimer(null), primary: false));
        }
        if (_timerUpSince is { } up && now - up > ActivityTimer.UpFor.TotalMilliseconds)
        {
            StopTimer(null);
            return;
        }
        string text = ActivityTimer.Text(Settings.TimerLabel, left);
        if (text == _timerSent) return;
        _timerSent = text;
        _session.Effects?.Timer(text, TimerCorner(), left == TimeSpan.Zero);
    }

    // ───────────────────────────── time budgets (FR-121) ─────────────────────────────

    private readonly SectionClock _sections = new();

    /// <summary>The favourite or scene about to be shown, so its budget is used: set just before showing it.</summary>
    private (string Key, string Name, int Minutes)? _pendingSection;

    /// <summary>Something was shown: its time starts (adding to any time it had), and Follow me learns its app.</summary>
    private void AfterPresenterShowed(SourceWindow source)
    {
        (string key, string name, int minutes) = _pendingSection ?? (CaptureProtocol.CoverKey(source), SourceName(source), 0);
        if (_pendingSection is null && source.IsDocument) key = "pdf:" + source.Document; // every page is one part
        _pendingSection = null;
        if (!_turningPage || _sections.Current(DateTime.UtcNow)?.Key != key)
            _sections.Show(key, source.IsDocument ? PdfPages.Name(source.Document!) : name, TimeSpan.FromMinutes(minutes), DateTime.UtcNow);
        _rehearsal?.Switches.Add((DateTime.UtcNow, key));
        if (source.IsBoard && !BoardOut && !_playlistSwitching)
        {
            // The whiteboard shown from a favourite or a scene: its window comes with it, to draw on.
            _boardInk ??= new InkDrawing();
            OpenBoardWindow();
            StartDrawingOnBoard();
        }
        UpdateExtrasClock(); // a budget needs the clock to warn
        UpdateWidget();

        if (_followMe && !_followSwitching && source.IsWindow && !IsNeverShown(source)
            && !Settings.FollowMeApps.Contains(source.AppName, StringComparer.OrdinalIgnoreCase) && Settings.FollowMeApps.Count < FollowMe.MaxApps)
        {
            Settings.FollowMeApps = FollowMe.Normalize([.. Settings.FollowMeApps, source.AppName], Settings.NeverShowApps);
            Save();
            string app = source.AppName;
            Message($"Follow me now follows {app} too.", BadgeDot.Accent, Button("Undo", () => RemoveFollowMeApp(app), primary: false));
        }
    }

    /// <summary>The section's warnings, and its time paused while the TV is black.</summary>
    private void TickSections()
    {
        DateTime now = DateTime.UtcNow;
        if (_session.State != SessionState.Presenting || _session.IsBlanked || _session.IsHeld) _sections.Pause(now);
        else _sections.Resume(now);
        switch (_sections.Check(now))
        {
            case SectionAlert.OneMinuteLeft when _sections.Current(now) is { } c:
                Message($"One minute left for “{c.Name}”.");
                break;
            case SectionAlert.Over when _sections.Current(now) is { } c:
                Message($"“{c.Name}” has had its {BlankCountdown.Clock(c.Budget)}.", BadgeDot.Warning);
                break;
        }
    }

    /// <summary>"Demo 3:12 of 5:00" for the now-showing bar, when what's shown has a budget (or a rehearsal runs).</summary>
    private string SectionText() =>
        _sections.Current(DateTime.UtcNow) is { } c && (c.HasBudget || _rehearsal is not null) ? SectionClock.Text(c) : "";

    /// <summary>The favourite or scene behind what's shown, whose minutes the notes form edits.</summary>
    private Favourite? MinutesTarget()
    {
        if (_session.Source is not { } shown) return null;
        Favourite now = FavouriteMatcher.FromSource(shown);
        return Settings.Scenes.OfType<Scene>().Select(s => s.Source).OfType<Favourite>().FirstOrDefault(f => f.Matches(now))
            ?? Settings.Favourites.OfType<Favourite>().FirstOrDefault(f => f.Matches(now));
    }

    // ───────────────────────────── the night filter and the small picture's shape (FR-123, FR-118) ─────────────────────────────

    /// <summary>Settings and the tray: how the TV's picture is toned for a dark room.</summary>
    public void SetNightFilter(NightMode mode)
    {
        Settings.Night = mode;
        Save();
        ApplyMarksChange(); // shown the light way: again through the helper, which can tone the picture
        _session.Effects?.Night(mode);
        _tray.Refresh();
    }

    /// <summary>"night-filter": off → soften white pages → turn light pages dark → off.</summary>
    private void CycleNightFilter()
    {
        NightMode next = (NightMode)(((int)Settings.Night + 1) % 3);
        SetNightFilter(next);
        Message(next == NightMode.Off ? "The night filter is off." : $"Night filter: {NightFilter.Words(next).ToLowerInvariant()}. Your name, captions and QR codes stay as they are.");
    }

    /// <summary>Settings and the tray: the small picture's shape.</summary>
    public void SetInsetShape(InsetShape shape)
    {
        Settings.InsetShapeMode = shape;
        Save();
        _session.Effects?.InsetShape(shape);
        _tray.Refresh();
    }

    // ───────────────────────────── the speaker's screen (FR-124) ─────────────────────────────

    private SpeakerScreen? _speaker;

    public bool SpeakerScreenOpen => _speaker is { IsDisposed: false };

    /// <summary>Screens the speaker's screen may go on: any that isn't a TV showing something.</summary>
    public List<DisplayMonitor> SpeakerScreenCandidates()
    {
        DisplayMonitor? tv = _session.FullScreenOutput, second = _second?.FullScreenOutput;
        return [.. _monitors.Where(m => !(tv is not null && m.IsSameScreen(tv)) && !(second is not null && m.IsSameScreen(second)) && !m.IsVirtual)];
    }

    /// <summary>"speaker-screen": the speaker's screen on its screen; again puts it away.</summary>
    private void ToggleSpeakerScreen()
    {
        if (SpeakerScreenOpen)
        {
            _speaker!.Close();
            return;
        }
        List<DisplayMonitor> candidates = SpeakerScreenCandidates();
        DisplayMonitor? screen = candidates.FirstOrDefault(m => m.DeviceName == Settings.SpeakerScreenDevice)
            ?? candidates.FirstOrDefault(m => !m.IsPrimary) ?? candidates.FirstOrDefault();
        if (screen is null)
        {
            Message("The speaker's screen needs a screen that isn't showing the presentation.");
            return;
        }
        Settings.SpeakerScreenDevice = screen.DeviceName;
        Save();
        _speaker = new SpeakerScreen(this, screen);
        _speaker.FormClosed += (_, _) =>
        {
            _speaker = null;
            ApplyMarksChange();
            UpdateExtrasClock();
            _tray.Refresh();
            ScheduleTrim();
        };
        ApplyMarksChange(); // its copy of the TV needs the helper, like the audience view
        _speaker.Show();
        UpdateExtrasClock();
        _tray.Refresh();
        Message(screen.IsPrimary
            ? $"The speaker's screen covers {screen.Name}, your own screen. Press Esc on it to put it away."
            : $"The speaker's screen is on {screen.Name}: the time, your notes and what's next. Only you see it.");
    }

    /// <summary>Settings › Where it shows › Speaker's screen.</summary>
    public void SetSpeakerScreen(string? device)
    {
        Settings.SpeakerScreenDevice = device;
        Save();
        if (SpeakerScreenOpen && _speaker!.Screen.DeviceName != device)
        {
            _speaker.Close();
            if (device is not null) ToggleSpeakerScreen();
        }
    }

    /// <summary>What the speaker's screen shows now.</summary>
    internal SpeakerInfo SpeakerInfo()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan talked = _talk.Elapsed(now);
        int warning = 0;
        string talk = _talk.Started is null ? "0:00" : BlankCountdown.Clock(talked);
        if (Settings.TalkMinutes > 0 && _talk.Started is not null)
        {
            TimeSpan left = TimeSpan.FromMinutes(Settings.TalkMinutes) - talked;
            talk = left > TimeSpan.Zero ? $"{BlankCountdown.Clock(left)} left" : $"{BlankCountdown.Clock(-left)} over";
            warning = left <= TimeSpan.Zero ? 2 : left <= TimeSpan.FromMinutes(2) ? 1 : 0;
        }
        string notesTitle = NotesTarget()?.Title ?? (_slideNotes is { } slide ? SlideNotes.Title(slide) : "");
        string? timer = _timerEnd is { } end ? ActivityTimer.Text(Settings.TimerLabel, TimeSpan.FromMilliseconds(Math.Max(0, end - Environment.TickCount64))) : null;
        return new SpeakerInfo(DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), talk, warning,
            AudienceView().State, SectionText(), _next is { } next ? SourceName(next) : "Nothing ready", notesTitle, CurrentNotes(), timer);
    }

    // ───────────────────────────── PowerPoint's notes (FR-122) ─────────────────────────────

    private PowerPointReader? _powerPoint;
    private SlideInfo? _slideNotes;

    /// <summary>Asked only while PowerPoint is shown and its notes would be seen somewhere (the notes window, the phone, the speaker's screen).</summary>
    private void UpdateSlideNotes()
    {
        bool want = !_quitting && Settings.PowerPointNotes && _session.State == SessionState.Presenting && _session.Source is { IsWindow: true } s
            && SlideNotes.IsPowerPoint(s.AppName) && (NotesOpen || SpeakerScreenOpen || PhoneRemoteOn && Settings.NotesOnPhone);
        if (!want)
        {
            _powerPoint?.Watch(false);
            if (_slideNotes is not null)
            {
                _slideNotes = null;
                UpdateNotes();
            }
            return;
        }
        _powerPoint ??= new PowerPointReader(slide => Guard(() =>
        {
            if (slide == _slideNotes) return;
            _slideNotes = slide;
            UpdateNotes();
            PushRemoteStatus();
        }));
        _powerPoint.Watch(true);
    }

    /// <summary>Settings › General › PowerPoint's notes.</summary>
    public void SetPowerPointNotes(bool on)
    {
        Settings.PowerPointNotes = on;
        Save();
        UpdateSlideNotes();
    }

    // ───────────────────────────── words for captions (FR-128) ─────────────────────────────

    /// <summary>Settings › Where it shows › Words for captions: used the next time captions start.</summary>
    public void SetCaptionWords(string text)
    {
        List<string> words = CaptionVocabulary.FromText(text);
        if (words.SequenceEqual(Settings.CaptionWords, StringComparer.Ordinal)) return;
        Settings.CaptionWords = words;
        Save();
        if (LiveCaptionsOn) Message("The new words are used the next time live captions start. Spelling is put right at once.");
    }

    // ───────────────────────────── rehearsing (FR-127) ─────────────────────────────

    private sealed class RehearsalLog(DateTime started)
    {
        public DateTime Started { get; } = started;
        public List<(DateTime At, string Key)> Switches { get; } = [];
        public List<SaidLine> Said { get; } = [];
        public bool StartedCaptions { get; set; }
    }

    private RehearsalLog? _rehearsal;
    private (string Text, DateTime Started, DateTime Ended)? _lastRehearsed;

    public bool Rehearsing => _rehearsal is not null;

    /// <summary>"rehearse": a private practice, timed and listened to; again ends it and shows the report.</summary>
    private void ToggleRehearsal()
    {
        if (_rehearsal is not null)
        {
            FinishRehearsal();
            return;
        }
        DateTime now = DateTime.UtcNow;
        _rehearsal = new RehearsalLog(now);
        _sections.Reset();
        if (_session.State == SessionState.Presenting && _session.Source is { } shown)
        {
            _sections.Show(CaptureProtocol.CoverKey(shown), SourceName(shown), TimeSpan.Zero, now);
            _rehearsal.Switches.Add((now, CaptureProtocol.CoverKey(shown)));
        }
        if (_session.State != SessionState.Off)
        {
            _talk.Reset();
            _talk.Start(now);
        }
        if (_captions is null)
        {
            StartCaptionsForRehearsal();
            _rehearsal.StartedCaptions = _captions is not null;
        }
        UpdateExtrasClock();
        _tray.Refresh();
        UpdateWidget();
        Message(_captions is null
                ? "Rehearsing: each part you show is timed. (Live captions couldn't start, so your pace isn't measured.) Rehearse again to finish."
                : "Rehearsing: each part you show is timed, and what you say is heard on this PC to measure your pace. Nothing is recorded. Rehearse again to finish.",
            BadgeDot.Accent, Button("Finish", FinishRehearsal));
    }

    /// <summary>Something said in full while rehearsing.</summary>
    private void NoteRehearsalLine()
    {
        if (_rehearsal is null || _liveText.LastSaid is not { } said || said == _lastRehearsed) return;
        _lastRehearsed = said;
        _rehearsal.Said.Add(new SaidLine(said.Text, said.Started.ToUniversalTime(), said.Ended.ToUniversalTime()));
    }

    private void FinishRehearsal()
    {
        if (_rehearsal is not { } log) return;
        _rehearsal = null;
        DateTime now = DateTime.UtcNow;
        _sections.Pause(now);
        RehearsalReport report = Rehearsal.Build(log.Started, now, _sections.All(now), log.Switches, log.Said, Settings.TalkMinutes);
        if (log.StartedCaptions) StopLiveCaptions(null);
        UpdateExtrasClock();
        _tray.Refresh();
        UpdateWidget();
        string folder = Settings.TranscriptFolder ?? SavedFiles.DefaultTranscriptFolder;
        string? file = null;
        try
        {
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, Rehearsal.FileName(log.Started));
            File.WriteAllText(file, Rehearsal.Text(report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag("rehearsal report: " + ex.Message);
            file = null;
        }
        string said = Rehearsal.Summary(report);
        if (file is not null) Message(said, BadgeDot.Accent, Button("Open the report", () => ShellOpen(file)), Button("Show in folder", () => ShowInFolder(file), primary: false));
        else Message(said + " (The report couldn't be saved: the transcripts folder can't be written.)", BadgeDot.Accent);
    }

    // ───────────────────────────── cast to a wireless TV (FR-126) ─────────────────────────────

    /// <summary>"cast": Windows' own Cast panel, to connect a wireless TV or projector; once it's connected the usual offer to show on it follows.</summary>
    private void CastToWirelessTv()
    {
        ShellOpen("ms-settings-connectabledevices:devicediscovery");
        Message("Choose your TV or projector in Windows' Cast list. When it connects, ScreenCast Presenter offers to show on it, as a screen of its own (never a copy of yours).");
    }

    // ───────────────────────────── copy the text (FR-130) ─────────────────────────────

    /// <summary>"copy-text": the words in what's shown, read on this PC, onto the clipboard.</summary>
    private async Task CopyTextAsync()
    {
        if (_session.State != SessionState.Presenting || _session.Source is not { } source)
        {
            Message("Show something first, then copy the text in it.");
            return;
        }
        // A window or a screen: a picture of it as a screenshot takes it (hidden parts and automatic hiding
        // covered). A PDF page, a picture, the whiteboard, the room sign, a camera or a video isn't on the
        // presenter's screen: the output window itself, which shows exactly what the audience sees, covers and all.
        SourceWindow? target = !source.IsOffScreen ? source : _session.OutputHandle is var output and not 0 ? new SourceWindow(output, "", "", false) : null;
        if (target is null)
        {
            Message("The text couldn't be read: nothing is on the TV right now.");
            return;
        }
        string png = Path.Combine(Path.GetTempPath(), $"scp-text-{Guid.NewGuid():N}.png");
        try
        {
            Message("Reading the text in what's shown…");
            bool captured = source.IsOffScreen
                ? await CaptureHost.CaptureStillToFileAsync(target, cursor: false, png) // already covered on the TV
                : await CaptureHost.CaptureStillToFileAsync(target, cursor: false, png, Redactions.For(Settings, source), Settings.AutoHideKinds, Settings.AutoHideWords);
            if (!captured)
            {
                Message("The text couldn't be read: what's shown couldn't be captured.", BadgeDot.Warning);
                return;
            }
            (List<string> lines, string? problem) = await ScreenText.ReadAsync(png, TimeSpan.FromSeconds(20));
            if (problem is not null)
            {
                Message(problem, BadgeDot.Warning);
                return;
            }
            string text = CopiedText.Join(lines);
            if (text.Length == 0)
            {
                Message("No text was found in what's shown.");
                return;
            }
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Message("The text was read, but another app is holding the clipboard. Try again.", BadgeDot.Warning);
                return;
            }
            Message(CopiedText.Said(lines.Count) + " Paste it anywhere.");
        }
        finally
        {
            try { File.Delete(png); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diag("copy text: " + ex.Message); }
        }
    }

    // ───────────────────────────── GIFs (FR-131) ─────────────────────────────

    private bool _makingGif;

    /// <summary>"make-gif": choose a recording; its start becomes a short animated picture beside it.</summary>
    private async Task ChooseGifSourceAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Make a GIF from a recording",
            Filter = "Videos (*.mp4)|*.mp4",
            InitialDirectory = Settings.RecordingFolder is { } f && Directory.Exists(f) ? f : SavedFiles.DefaultRecordingFolder,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        await MakeGifAsync(dialog.FileName);
    }

    /// <summary>Makes a GIF of the start of <paramref name="mp4"/>, beside it; said when done.</summary>
    public async Task MakeGifAsync(string mp4)
    {
        if (_makingGif)
        {
            Message("A GIF is being made already. One at a time.");
            return;
        }
        if (!File.Exists(mp4))
        {
            Message($"“{Path.GetFileName(mp4)}” can't be found.");
            return;
        }
        string gif = UniqueFile(Path.ChangeExtension(mp4, ".gif"));
        _makingGif = true;
        try
        {
            Message($"Making a GIF of the first {GifMaker.DefaultSeconds} seconds of “{Path.GetFileNameWithoutExtension(mp4)}”…");
            string? problem = await GifMaker.MakeAsync(mp4, gif, GifMaker.DefaultSeconds, null, TimeSpan.FromMinutes(3));
            if (problem is not null)
            {
                Message(problem, BadgeDot.Warning);
                return;
            }
            Message($"The GIF is ready: “{Path.GetFileName(gif)}”.", BadgeDot.Accent, Button("Open it", () => ShellOpen(gif)), Button("Show in folder", () => ShowInFolder(gif), primary: false));
        }
        finally
        {
            _makingGif = false;
        }
    }

    /// <summary>"clip.gif", or "clip (2).gif" when that's taken: nothing is ever written over.</summary>
    internal static string UniqueFile(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; n < 1000; n++)
        {
            string next = Path.Combine(folder, $"{name} ({n}){ext}");
            if (!File.Exists(next)) return next;
        }
        return Path.Combine(folder, $"{name} {Guid.NewGuid():N}{ext}");
    }

    // ───────────────────────────── the command line's --status and --is (FR-129) ─────────────────────────────

    /// <summary>What's on now as JSON for button decks and scripts: what's shown, where, and every switch.</summary>
    internal string StatusJson()
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["showing"] = _session.State == SessionState.Presenting,
            ["state"] = _session.State switch { SessionState.Presenting => "presenting", SessionState.IdleScreen => "blank-screen", _ => "off" },
            ["title"] = _session.Source is { } s ? (s.Title.Length > 0 ? s.Title : s.AppName) : null,
            ["app"] = _session.Source?.AppName,
            ["where"] = _session.State == SessionState.Off ? null : AudienceScreenName,
            ["blank"] = _session.IsBlanked,
            ["frozen"] = _session.IsFrozen,
            ["held"] = _session.IsHeld,
            ["recording"] = _recording.IsActive,
            ["playlist"] = PlayingOn(PlaylistScreen.Main),
            ["timer"] = _timerEnd is { } end ? ActivityTimer.Text(Settings.TimerLabel, TimeSpan.FromMilliseconds(Math.Max(0, end - Environment.TickCount64))) : null,
            ["section"] = SectionText() is { Length: > 0 } section ? section : null,
        };
        var switches = new System.Text.Json.Nodes.JsonObject();
        foreach (string name in CommandLine.StatusNames) switches[name] = IsSwitchOn(name);
        json["switches"] = switches;
        return json.ToJsonString();
    }

    /// <summary>Whether a switch (a name from --on, --off and --is) is on now.</summary>
    internal bool IsSwitchOn(string name) => IsSwitchedOn(name) ?? name switch
    {
        "whiteboard" => _session.Source is { IsBoard: true },
        "split-screen" => _split.Count > 0,
        "inset" => _inset is not null,
        "qr" => _qrText is not null,
        "zoom" => _zoom > 0,
        "draw" => _drawing is not null,
        "blank" => _session.IsBlanked,
        "freeze" => _session.IsFrozen,
        "recording" => _recording.IsActive,
        "showing" => _session.State == SessionState.Presenting,
        _ => false,
    };

    // ───────────────────────────── the pointer in the picture ─────────────────────────────

    private bool? _pictureCursorSent;

    /// <summary>
    /// The pointer drawn into the picture (FR-24): as the setting says, but never while the laser
    /// stands in for it, and never while the presentation covers the screen the shown window is on
    /// (no TV: the presenter's own pointer is already there, so the copy would be a second one).
    /// </summary>
    private void ApplyPictureCursor(bool force = false)
    {
        if (_session.Effects is not { } effects)
        {
            _pictureCursorSent = null;
            return;
        }
        string? windowScreen = _session.State == SessionState.Presenting && _session.Source is { IsWindow: true } source && WindowEnumerator.IsAlive(source.Handle)
            ? DisplayHelpers.FromWindow(source.Handle)?.DeviceName : null;
        bool want = PictureCursor.Wanted(Settings.ShowCursor, _laserOn, _session.FullScreenOutput?.DeviceName, windowScreen);
        if (!force && _pictureCursorSent == want) return;
        _pictureCursorSent = want;
        effects.ShowCursor(want); // the engine can hide the pointer it was started with, never add one it wasn't
    }

    // ───────────────────────────── shared ─────────────────────────────

    private readonly System.Windows.Forms.Timer _extrasClock = new() { Interval = 250 };

    /// <summary>The extras that need the helper to draw them (in addition to the stage's): the timer, the night filter, the speaker's copy of the TV, the board.</summary>
    private bool ExtrasWantHelper => TimerOn || Settings.Night != NightMode.Off || SpeakerScreenOpen || _session.Source is { IsBoard: true } || Settings.InsetShapeMode != InsetShape.Rectangle && _inset is not null;

    /// <summary>Runs only while something needs it: the TV timer, a budget, a rehearsal, the speaker's screen.</summary>
    private void UpdateExtrasClock()
    {
        bool want = !_quitting && _session.State != SessionState.Off && (TimerOn || _sections.Current(DateTime.UtcNow) is { HasBudget: true } || _rehearsal is not null || SpeakerScreenOpen);
        if (want == _extrasClock.Enabled) return;
        if (want) _extrasClock.Start();
        else _extrasClock.Stop();
    }

    private string? _lastSectionText;

    private void TickExtras()
    {
        TickTimer();
        TickSections();
        string section = SectionText();
        if (section != _lastSectionText)
        {
            _lastSectionText = section;
            UpdateWidget();
        }
    }

    /// <summary>A new engine took over: the timer, the night filter, the small picture's shape and the board's drawing go to it.</summary>
    private void ReplayExtras(IEffectsEngine effects)
    {
        if (Settings.Night != NightMode.Off) effects.Night(Settings.Night);
        if (Settings.InsetShapeMode != InsetShape.Rectangle) effects.InsetShape(Settings.InsetShapeMode);
        _timerSent = null;
        if (TimerOn) TickTimer();
        if (_drawing is null && _boardInk is not null && _session.Source is { IsBoard: true }) _boardInk.Replay(effects);
        effects.VideoEvent -= OnVideoEvent;
        effects.VideoEvent += OnVideoEvent;
    }

    private void StartExtras()
    {
        StartPrivacyExtras();
        StartSources();
        _followWatch.Changed += (_, _) => Guard(OnFollowForeground);
        _followSettle.Tick += (_, _) => _ = GuardAsync(OnFollowSettledAsync);
        _extrasClock.Tick += (_, _) => Guard(TickExtras);
    }

    /// <summary>What's shown changed (or stopped): every rev 10 extra follows.</summary>
    private void UpdateExtras()
    {
        if (_session.State == SessionState.Off)
        {
            if (_followMe) SetFollowMe(false);
            if (TimerOn) StopTimer(null);
            DateTime now = DateTime.UtcNow;
            _sections.Pause(now);
            if (_rehearsal is null) _sections.Reset();
        }
        UpdatePrivacyExtras();
        UpdateSources();
        UpdateClickerKeys();
        ApplyPictureCursor(); // the window moved to another screen, a TV came or went: one pointer, never two
        UpdateSlideNotes();
        UpdateExtrasClock();
        _speaker?.UpdateFrom(this);
    }

    private void DisposeExtras()
    {
        DisposePrivacyExtras();
        DisposeSources();
        _followWatch.Dispose();
        _followSettle.Dispose();
        _extrasClock.Dispose();
        _clicker?.Dispose();
        _powerPoint?.Dispose();
        _speaker?.Dispose();
    }
}
