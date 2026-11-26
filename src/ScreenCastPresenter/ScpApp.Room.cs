using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter;

/// <summary>
/// The TV over the cable (FR-107), schedules from a calendar file (FR-108), rules from the
/// organisation and settings to a file and back (FR-109), voice commands (FR-110) and speaker
/// notes (FR-111).
/// </summary>
internal sealed partial class ScpApp
{
    // ───────────────────────────── the TV over the cable (FR-107) ─────────────────────────────

    /// <summary>The TV: where the presentation shows, or the screen chosen for it, or the first one that isn't the main screen.</summary>
    private DisplayMonitor? TvScreen =>
        _session.FullScreenOutput
        ?? _monitors.FirstOrDefault(m => m.DeviceName == Settings.OutputDevice)
        ?? _monitors.FirstOrDefault(m => !m.IsPrimary);

    /// <summary>"tv-on" / "tv-off", the schedule and "--tv": turns the TV on (and to this PC's input) or off.</summary>
    public async Task<bool> SetTvPowerAsync(bool on, bool quiet = false)
    {
        if (TvScreen is not { } tv)
        {
            if (!quiet) Message("There's no TV connected to turn on or off.");
            return false;
        }
        bool done = await Task.Run(() => MonitorDdc.SetPower(tv, on));
        if (done && on && Settings.TvInput > 0) await Task.Run(() => MonitorDdc.SetInput(tv, (uint)Settings.TvInput));
        Diag($"tv: power {(on ? "on" : "off")} {tv.Name}: {(done ? "done" : "no answer")}");
        if (!quiet)
        {
            if (done) Message(on ? $"{tv.Name} is on." : $"{tv.Name} is off (standby).", BadgeDot.Accent);
            else Message($"{tv.Name} didn't answer. Many TVs don't take commands over the cable (monitors usually do): test it in Settings › Where it shows.", BadgeDot.Warning);
        }
        return done;
    }

    /// <summary>"--tv-input": switches the TV to an input.</summary>
    public async Task<bool> SetTvInputAsync(uint code, bool quiet = false)
    {
        if (TvScreen is not { } tv)
        {
            if (!quiet) Message("There's no TV connected to switch.");
            return false;
        }
        bool done = await Task.Run(() => MonitorDdc.SetInput(tv, code));
        if (!quiet) Message(done ? $"{tv.Name} is on {TvControl.InputName(code)}." : $"{tv.Name} didn't answer: it may not take commands over the cable.", done ? BadgeDot.Accent : BadgeDot.Warning);
        return done;
    }

    /// <summary>Settings: asks the TV what it can do, and says so.</summary>
    public async Task TestTvAsync()
    {
        if (TvScreen is not { } tv)
        {
            Message("Connect the TV first, then test it.");
            return;
        }
        Message($"Asking {tv.Name} what it can do…", BadgeDot.Accent);
        string? caps = await Task.Run(() => MonitorDdc.Capabilities(tv));
        (bool power, bool input, List<uint> inputs) = TvControl.Supports(TvControl.ParseVcp(caps));
        uint? current = input ? await Task.Run(() => MonitorDdc.Read(tv, TvControl.InputSource)) : null;
        if (current is { } now && Settings.TvInput == 0)
        {
            Settings.TvInput = (int)now; // the input it's on now is this PC's
            Save();
        }
        Message(caps is null ? $"{tv.Name} doesn't take commands over the cable, so it can't be turned on or off from here."
            : $"{tv.Name} {(power ? "can be turned on and off" : "can't be turned on and off")}{(input ? $" and switched between {string.Join(", ", inputs.Count > 0 ? inputs.Select(TvControl.InputName) : ["its inputs"])}" : "")}."
              + (current is { } c ? $" It's on {TvControl.InputName(c)} now." : ""), caps is null ? BadgeDot.Warning : BadgeDot.Accent);
    }

    /// <summary>Settings › Where it shows.</summary>
    public void SetTvWakeOnShow(bool on)
    {
        Settings.TvWakeOnShow = on;
        Save();
    }

    /// <summary>Showing starts: the TV is woken and switched to this PC (when asked for), without waiting for it.</summary>
    private void WakeTvForShowing()
    {
        if (!Settings.TvWakeOnShow || _session.State != SessionState.Off) return;
        _ = GuardAsync(() => SetTvPowerAsync(on: true, quiet: true));
    }

    // ───────────────────────────── calendar (FR-108) ─────────────────────────────

    private List<CalendarEvent>? _calendarEvents;
    private DateTime _calendarFileTime;
    private DateTime _calendarChecked = DateTime.Now;

    /// <summary>The calendar's events, read again when the file changes; null when it can't be read.</summary>
    private List<CalendarEvent>? CalendarEvents(CalendarLink link)
    {
        DateTime written;
        try
        {
            written = link.File is { } file && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            written = DateTime.MinValue;
        }
        if (written != _calendarFileTime || _calendarEvents is null)
        {
            _calendarFileTime = written;
            _calendarEvents = CalendarLink.Read(link.File);
        }
        return _calendarEvents;
    }

    /// <summary>On the schedule's tick: a booking starting or ending does what was chosen.</summary>
    private async Task CheckCalendarAsync(DateTime now)
    {
        DateTime from = _calendarChecked;
        _calendarChecked = now;
        if (Settings.Calendar is not { Enabled: true } link || CalendarEvents(link) is not { } events) return;
        List<CalendarOccurrence> occurrences = IcsCalendar.Occurrences(events, from.AddDays(-2), now.AddMinutes(CalendarLink.MaxLeadMinutes + 1));
        foreach ((bool start, CalendarOccurrence booking) in link.Due(occurrences, from, now))
        {
            if ((start ? link.AtStart : link.AtEnd) is not { } entry) continue;
            Diag($"calendar: {(start ? "start" : "end")} of “{booking.Summary}”: {entry.WhatWords()}");
            await RunScheduledAsync(entry);
        }
    }

    /// <summary>The next booking the calendar link acts on (for Settings), or null.</summary>
    public CalendarOccurrence? NextBooking()
    {
        if (Settings.Calendar is not { } link || CalendarEvents(link) is not { } events) return null;
        DateTime now = DateTime.Now;
        return IcsCalendar.Occurrences(events, now, now.AddDays(8)).FirstOrDefault(link.Wants);
    }

    /// <summary>Settings › Rooms & schedule › Calendar: the link changed.</summary>
    public void SetCalendar(CalendarLink? link)
    {
        Settings.Calendar = link?.Normalized();
        _calendarEvents = null;
        _calendarChecked = DateTime.Now;
        Save();
        UpdateSchedule();
        if (link?.File is { } file && CalendarLink.Read(file) is null) Message("That calendar file can't be read: choose an .ics file (export the room's calendar from Outlook or Google).", BadgeDot.Warning);
    }

    // ───────────────────────────── rules from the organisation (FR-109) ─────────────────────────────

    private PolicySet _policy = PolicySet.None;

    public PolicySet Policy => _policy;

    /// <summary>At start: the organisation's rules are read and applied to the settings.</summary>
    private void ApplyPolicy()
    {
        _policy = PolicySet.Read();
        if (!_policy.Any) return;
        _policy.Apply(Settings);
        Save();
        Diag("policy: " + string.Join(" ", _policy.Words()));
    }

    /// <summary>Says an action is turned off by the organisation; true when it is (the caller stops).</summary>
    private bool Blocked(bool allowed, string what)
    {
        if (allowed) return false;
        Message($"Your organisation has turned off {what} on this PC.", BadgeDot.Warning);
        return true;
    }

    // ───────────────────────────── settings to a file and back (FR-109) ─────────────────────────────

    /// <summary>Settings › General: save the settings to a file.</summary>
    public void ExportSettings()
    {
        using var dialog = new SaveFileDialog { Title = "Save ScreenCast Presenter's settings", Filter = "Settings (*.json)|*.json", FileName = "ScreenCast Presenter settings.json" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (ExportSettingsTo(dialog.FileName) is { } problem) Message(problem, BadgeDot.Warning);
        else Message($"Settings saved to “{Path.GetFileName(dialog.FileName)}”. Load them on another PC in Settings › General.");
    }

    /// <summary>The settings to <paramref name="path"/>; why not, or null.</summary>
    public string? ExportSettingsTo(string path)
    {
        try
        {
            File.WriteAllText(path, SettingsTransfer.Export(Settings), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "The settings couldn't be saved there: " + SavedFiles.Explain(ex);
        }
    }

    /// <summary>Settings › General: load settings from a file (they replace these, then the app starts again to use them).</summary>
    public void ImportSettings()
    {
        using var dialog = new OpenFileDialog { Title = "Load ScreenCast Presenter's settings", Filter = "Settings (*.json)|*.json" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (ImportSettingsFrom(dialog.FileName) is { } problem) Message(problem, BadgeDot.Warning);
    }

    /// <summary>Settings from <paramref name="path"/> replace these and the app starts again; why not, or null.</summary>
    public string? ImportSettingsFrom(string path)
    {
        string json;
        try
        {
            if (new FileInfo(path).Length > SettingsTransfer.MaxBytes) return "That file is too big to be settings.";
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "That file can't be read: " + SavedFiles.Explain(ex);
        }
        (AppSettings? read, string? problem) = SettingsTransfer.Import(json, Settings);
        if (read is null) return problem;
        _policy.Apply(read);
        if (!_store.Replace(read)) return "The settings couldn't be saved on this PC.";
        Message("Settings loaded. ScreenCast Presenter starts again to use them.", BadgeDot.Accent);
        RestartSoon();
        return null;
    }

    /// <summary>Starts a new copy (quietly) once this one has closed.</summary>
    private void RestartSoon()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, CommandLine.QuietStartSwitch) { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Diag("restart: " + ex.Message);
            }
            RequestQuit();
        };
        timer.Start();
    }

    // ───────────────────────────── voice commands (FR-110) ─────────────────────────────

    private CaptionsProcess? _voice;

    public bool VoiceListening => _voice is not null;

    /// <summary>Settings and "--on voice-commands": listen for "Presenter, …".</summary>
    public void SetVoiceCommands(bool on)
    {
        if (on && Blocked(_policy.VoiceCommandsAllowed, "voice commands")) return;
        Settings.VoiceCommands = on;
        Save();
        UpdateVoice();
        _tray.Refresh();
        if (on) Message("Voice commands on: say “Presenter, next slide”. Windows listens on this PC while something is shown; nothing is recorded or sent anywhere.", BadgeDot.Accent);
        else Message("Voice commands are off.");
    }

    /// <summary>Listens only while voice commands are on and something is shown.</summary>
    private void UpdateVoice()
    {
        bool want = !_quitting && Settings.VoiceCommands && _session.State == SessionState.Presenting;
        if (want == (_voice is not null)) return;
        if (!want)
        {
            CaptionsProcess? stopping = _voice;
            _voice = null;
            stopping?.Dispose();
            return;
        }
        (CaptionsProcess? process, string? problem) = CaptionsProcess.Start();
        if (process is null)
        {
            Message(problem ?? "Voice commands couldn't start.", BadgeDot.Warning);
            return;
        }
        _voice = process;
        process.Line += (_, line) => Guard(() => OnVoiceLine(process, line));
        process.Ended += (_, _) => Guard(() =>
        {
            if (ReferenceEquals(_voice, process)) _voice = null;
        });
    }

    private void OnVoiceLine(CaptionsProcess process, string line)
    {
        if (!ReferenceEquals(_voice, process)) return;
        (string kind, string text) = LiveCaptions.Split(line);
        if (kind == "error")
        {
            _voice = null;
            process.Dispose();
            Message("Voice commands stopped: " + text, BadgeDot.Warning);
            return;
        }
        if (kind != "final" || VoiceCommands.Match(text) is not { } heard) return;
        Diag($"voice: {heard.Command}");
        _ = GuardAsync(() => RunVoiceAsync(heard.Command, heard.Phrase));
    }

    /// <summary>Runs a heard command: only those on the voice list.</summary>
    internal async Task RunVoiceAsync(string command, string phrase)
    {
        if (!VoiceCommands.Commands.Contains(command) && !(command.StartsWith("favourite-", StringComparison.Ordinal) || command.StartsWith("scene-", StringComparison.Ordinal))) return;
        Message($"Heard “{phrase}”.", BadgeDot.Accent);
        switch (command)
        {
            case "next-slide": SendSlide(forward: true); break;
            case "previous-slide": SendSlide(forward: false); break;
            case "voice:blank-on":
                if (_session.State == SessionState.Presenting && !_session.IsBlanked) await _session.ToggleBlankAsync(withMessage: false);
                break;
            case "voice:blank-off":
                if (_session.IsBlanked) await _session.ToggleBlankAsync(withMessage: false);
                break;
            case "voice:freeze-on":
                if (_session.State == SessionState.Presenting && !_session.IsFrozen) await _session.ToggleFreezeAsync();
                break;
            case "voice:freeze-off":
                if (_session.IsFrozen) await _session.ToggleFreezeAsync();
                break;
            case "voice:on:spotlight": await SwitchAsync("spotlight", true); break;
            case "voice:off:spotlight": await SwitchAsync("spotlight", false); break;
            case "voice:zoom-in":
                if (_zoom <= 0) await ToggleZoomAsync();
                break;
            case "voice:zoom-out":
                if (_zoom > 0) await ToggleZoomAsync();
                break;
            default: await RunAsync(command); break;
        }
    }

    // ───────────────────────────── speaker notes (FR-111) ─────────────────────────────

    private NotesWindow? _notesWindow;

    public bool NotesOpen => _notesWindow is { IsDisposed: false };

    /// <summary>The favourite or scene behind what's shown (the one with notes first), with its name; null when it's neither.</summary>
    internal (string Title, Func<string> Get, Action<string> Set)? NotesTarget()
    {
        if (_session.Source is not { } shown) return null;
        Favourite now = FavouriteMatcher.FromSource(shown);
        (string, Func<string>, Action<string>)? first = null;
        for (int i = 0; i < Settings.Scenes.Length; i++)
        {
            if (Settings.Scenes[i] is not { Source: { } src } scene || !src.Matches(now)) continue;
            int slot = i + 1;
            (string, Func<string>, Action<string>) target = (scene.Title(slot), () => scene.Notes, text => scene.Notes = NotesCipher.Tidy(text));
            if (scene.Notes.Length > 0) return target;
            first ??= target;
        }
        for (int i = 0; i < Settings.Favourites.Length; i++)
        {
            if (Settings.Favourites[i] is not { } f || !f.Matches(now)) continue;
            (string, Func<string>, Action<string>) target = (TrayMenuPlan.Shorten(f.Title.Length > 0 ? f.Title : f.AppName, 40), () => f.Notes, text => f.Notes = NotesCipher.Tidy(text));
            if (f.Notes.Length > 0) return target;
            first ??= target;
        }
        return first;
    }

    /// <summary>The notes for what's shown now, or "".</summary>
    internal string CurrentNotes() => NotesTarget() is { } t ? t.Get() : "";

    /// <summary>"notes": the private notes window on / off.</summary>
    private void ToggleNotes()
    {
        if (NotesOpen)
        {
            _notesWindow!.Close();
            _notesWindow = null;
            return;
        }
        _notesWindow = new NotesWindow(EditNotes);
        _notesWindow.FormClosed += (_, _) => _notesWindow = null;
        UpdateNotes();
        _notesWindow.Show();
    }

    /// <summary>What's shown changed: the notes window and the phone follow.</summary>
    private void UpdateNotes()
    {
        if (NotesOpen) _notesWindow!.ShowNotes(NotesTarget() is { } t ? t.Title : null, CurrentNotes(), _session.State == SessionState.Presenting);
    }

    /// <summary>"edit-notes": the notes for what's shown, for its favourite or scene.</summary>
    private void EditNotes()
    {
        if (NotesTarget() is not { } target)
        {
            Message("Notes belong to a favourite or a scene. Save what's shown as one first (tray › Favourites, or Save this as a scene), then add its notes.");
            return;
        }
        using var form = new FieldsForm($"Notes for “{target.Title}”", "Only you see these: on your own screen and on your paired phone, never on the TV.", "Save");
        (_, TextBox box) = form.AddField("Notes", target.Get(), NotesCipher.MaxLength, "Start with the Q3 numbers. Ask about…", lines: 8);
        if (!form.Ask()) return;
        target.Set(box.Text);
        Save();
        UpdateNotes();
        PushRemoteStatus();
    }

    /// <summary>Settings › Scenes › Notes…: a scene's notes, without showing it.</summary>
    public void EditSceneNotes(int slot)
    {
        if (slot is < 1 or > Scene.Slots || Settings.Scenes[slot - 1] is not { } scene) return;
        using var form = new FieldsForm($"Notes for “{scene.Title(slot)}”", "Only you see these: on your own screen and on your paired phone, never on the TV.", "Save");
        (_, TextBox box) = form.AddField("Notes", scene.Notes, NotesCipher.MaxLength, "Start with the Q3 numbers. Ask about…", lines: 8);
        if (!form.Ask()) return;
        scene.Notes = NotesCipher.Tidy(box.Text);
        Save();
        UpdateNotes();
        PushRemoteStatus();
    }

    /// <summary>Settings › General › Phone remote: notes on the phone.</summary>
    public void SetNotesOnPhone(bool on)
    {
        Settings.NotesOnPhone = on;
        Save();
        PushRemoteStatus();
    }

    /// <summary>The notes for the phone, encrypted with its pairing key; null when there are none or no key.</summary>
    private System.Text.Json.Nodes.JsonObject? NotesForPhone()
    {
        string notes = CurrentNotes();
        if (notes.Length == 0 || RemoteAuth.Unprotect(Settings.RemoteKey) is not { } key) return null;
        (string n, string c, string t) = NotesCipher.Encrypt(key, notes);
        return new System.Text.Json.Nodes.JsonObject { ["n"] = n, ["c"] = c, ["t"] = t };
    }
}
