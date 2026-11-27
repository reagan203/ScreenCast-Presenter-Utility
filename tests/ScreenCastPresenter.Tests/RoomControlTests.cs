using System.Diagnostics;
using Microsoft.Win32;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-107: the TV over the cable (DDC/CI). Nothing here turns a screen off or switches its input.</summary>
public class TvControlTests
{
    private const string Dell = "(prot(monitor)type(LCD)model(U2415)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B 0C) 16 18 1A 52 60(0F 11 12) AA(01 02) AC AE B2 B6 C6 C8 C9 D6(01 04 05) DC(00 02 03 05) DF)mswhql(1)asset_eep(40)mccs_ver(2.1))";

    [Fact]
    public void A_screens_list_of_what_it_supports_is_read()
    {
        Dictionary<byte, List<uint>> vcp = TvControl.ParseVcp(Dell)!;
        Assert.Equal([0x0Fu, 0x11, 0x12], vcp[0x60]);
        Assert.Equal([1u, 4, 5], vcp[0xD6]);
        Assert.Empty(vcp[0x10]);
        (bool power, bool input, List<uint> inputs) = TvControl.Supports(vcp);
        Assert.True(power && input);
        Assert.Equal(["DisplayPort 1", "HDMI 1", "HDMI 2"], inputs.Select(TvControl.InputName));
        (bool noPower, bool noInput, List<uint> none) = TvControl.Supports(TvControl.ParseVcp("(prot(monitor)vcp(10 12))"));
        Assert.False(noPower || noInput);
        Assert.Empty(none);
        Assert.Null(TvControl.ParseVcp(null));
        Assert.Null(TvControl.ParseVcp("(prot(monitor)type(lcd))")); // no vcp list
        Assert.Null(TvControl.ParseVcp("vcp(60(0F 11"));            // unbalanced
        Assert.False(TvControl.Supports(null).Power);
    }

    [Theory]
    [InlineData("hdmi1", 0x11u)]
    [InlineData("HDMI 2", 0x12u)]
    [InlineData("dp1", 0x0Fu)]
    [InlineData("DisplayPort 2", 0x10u)]
    [InlineData("usb-c", 0x1Bu)]
    [InlineData("11", 0x11u)]
    [InlineData("1b", 0x1Bu)]
    public void Inputs_are_read_by_name_or_number(string text, uint code) => Assert.Equal(code, TvControl.ParseInput(text));

    [Fact]
    public void Nonsense_inputs_are_refused_and_unknown_codes_still_get_a_name()
    {
        Assert.Null(TvControl.ParseInput("hdmi9"));
        Assert.Null(TvControl.ParseInput(""));
        Assert.Null(TvControl.ParseInput("100"));
        Assert.Equal("Input 2A", TvControl.InputName(0x2A));
        Assert.Equal(["cli:tv:on"], CommandLine.Parse(["--tv", "ON"]).Request!.Actions);
        Assert.Equal(["cli:tv-input:17"], CommandLine.Parse(["--tv-input", "hdmi1"]).Request!.Actions);
        Assert.Contains("--tv needs", CommandLine.Parse(["--tv", "maybe"]).Error);
        Assert.Contains("--tv-input needs", CommandLine.Parse(["--tv-input", "scart"]).Error);
    }

    [Fact]
    public void Asking_a_real_screen_what_it_supports_never_fails()
    {
        foreach (DisplayMonitor screen in DisplayHelpers.GetMonitors())
        {
            string? caps = MonitorDdc.Capabilities(screen); // a laptop panel or a TV may not answer: null, no exception
            if (caps is not null) Assert.Contains("(", caps);
            _ = MonitorDdc.Read(screen, TvControl.InputSource);
        }
    }

    [Fact]
    public void The_tv_can_be_scheduled_and_is_an_action()
    {
        Assert.Equal("turn the TV on", new ScheduleEntry { Action = "TvOn" }.WhatWords());
        Assert.Equal("turn the TV off", new ScheduleEntry { Action = "TvOff" }.WhatWords());
        Assert.Contains(ScheduleChoices.Actions([], []), c => c.Action == ScheduleAction.TvOn);
        foreach (string id in new[] { "tv-on", "tv-off" }) Assert.NotNull(UI.Icons.ForCommand(id));
        Assert.Equal(0, new AppSettings { TvInput = 999 }.Normalize().TvInput);
        Assert.False(new AppSettings().TvWakeOnShow);
    }
}

/// <summary>FR-108: schedules from a calendar file.</summary>
public class CalendarTests
{
    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        SUMMARY:Board meeting
        DTSTART;TZID=Europe/London:20261130T140000
        DTEND;TZID=Europe/London:20261130T150000
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Weekly stand-up\, team A
        DTSTART:20261123T090000Z
        DURATION:PT15M
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=6
        EXDATE:20261125T090000Z
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Holiday
        DTSTART;VALUE=DATE:20261201
        DTEND;VALUE=DATE:20261202
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        DTSTART:20261201T100000
        DTEND:20261201T110000
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:A very long title that is folded over
          two lines
        DTSTART:20261202T100000
        DTEND:20261202T103000
        RRULE:FREQ=DAILY;INTERVAL=2;UNTIL=20261206T235959Z
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Events_are_read_with_zones_durations_folds_and_cancellations()
    {
        List<CalendarEvent> events = IcsCalendar.Parse(Ics);
        Assert.Equal(4, events.Count); // the cancelled one is left out
        CalendarEvent board = events[0];
        TimeZoneInfo london = IcsCalendar.Zone("Europe/London")!;
        Assert.Equal(TimeZoneInfo.ConvertTime(new DateTime(2026, 11, 30, 14, 0, 0), london, TimeZoneInfo.Local), board.Start);
        Assert.Equal(TimeSpan.FromHours(1), board.End - board.Start);
        Assert.Equal("Weekly stand-up, team A", events[1].Summary);
        Assert.Equal(TimeSpan.FromMinutes(15), events[1].End - events[1].Start);
        Assert.True(events[2].AllDay);
        Assert.Equal("A very long title that is folded over two lines", events[3].Summary.Replace("  ", " "));
    }

    [Fact]
    public void Repeats_follow_their_days_counts_intervals_and_left_out_dates()
    {
        List<CalendarEvent> events = IcsCalendar.Parse(Ics);
        List<CalendarOccurrence> all = IcsCalendar.Occurrences(events, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));
        var standUps = all.Where(o => o.Summary.StartsWith("Weekly", StringComparison.Ordinal)).Select(o => o.Start.ToUniversalTime().Date).ToList();
        // Mon 23, (Wed 25 left out), Fri 27, Mon 30, Wed 2, Fri 4: six counted, one left out.
        Assert.Equal([new DateTime(2026, 11, 23), new DateTime(2026, 11, 27), new DateTime(2026, 11, 30), new DateTime(2026, 12, 2), new DateTime(2026, 12, 4)], standUps);
        var everyOther = all.Where(o => o.Summary.StartsWith("A very", StringComparison.Ordinal)).Select(o => o.Start.Day).ToList();
        Assert.Equal([2, 4, 6], everyOther);
        Assert.DoesNotContain(all, o => o.Summary == "Holiday"); // all-day: not a room booking
        Assert.True(all.SequenceEqual(all.OrderBy(o => o.Start)));
    }

    [Fact]
    public void The_bits_of_the_format_are_read_carefully()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), IcsCalendar.Duration("PT1H30M"));
        Assert.Equal(TimeSpan.FromDays(8), IcsCalendar.Duration("P1W1D"));
        Assert.Equal(TimeSpan.FromMinutes(-15), IcsCalendar.Duration("-PT15M"));
        Assert.Null(IcsCalendar.Duration("1H"));
        Assert.Null(IcsCalendar.Duration("P"));
        Assert.Null(IcsCalendar.Rule("FREQ=YEARLY"));
        Recurrence r = IcsCalendar.Rule("FREQ=WEEKLY;INTERVAL=2;BYDAY=1MO,TH;COUNT=99999")!;
        Assert.Equal(2, r.Interval);
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday }, r.Days);
        Assert.Equal(IcsCalendar.MaxRepeats, r.Count);
        Assert.NotNull(IcsCalendar.Zone("GMT Standard Time"));
        Assert.NotNull(IcsCalendar.Zone("/Europe/Paris"));
        Assert.Null(IcsCalendar.Zone("Mars/Olympus"));
        Assert.All(IcsCalendar.IanaToWindows.Values.Distinct(), w => Assert.True(IcsCalendar.Zone(w) is not null, $"Windows knows {w}"));
        Assert.Null(IcsCalendar.When("2026-11-26", new Dictionary<string, string>()));
        Assert.Equal(("DTSTART", "Mars/Olympus", "20261126T090000"), IcsCalendar.Property("DTSTART;TZID=\"Mars/Olympus\":20261126T090000") is var p ? (p.Name, p.Params["TZID"], p.Value) : default);
        Assert.Equal(DateTimeKind.Local, IcsCalendar.When("20261126T090000", new Dictionary<string, string> { ["TZID"] = "Mars/Olympus" })!.Value.Time.Kind); // unknown zone: the PC's own time
        Assert.Empty(IcsCalendar.Parse("garbage\nBEGIN:VEVENT\nSUMMARY:no start\nEND:VEVENT"));
    }

    [Fact]
    public void A_link_says_what_is_due_with_a_lead_a_filter_and_back_to_back_bookings()
    {
        var link = new CalendarLink { Match = "board", LeadMinutes = 10, AtStart = new ScheduleEntry { Action = "TvOn" }, AtEnd = new ScheduleEntry { Action = "Stop" } };
        var t0 = new DateTime(2026, 11, 30, 13, 0, 0);
        List<CalendarOccurrence> occ =
        [
            new(t0.AddHours(1), t0.AddHours(2), "Board meeting"),
            new(t0.AddHours(2), t0.AddHours(3), "Board follow-up"),
            new(t0.AddHours(1), t0.AddHours(2), "Lunch"),
        ];
        Assert.Empty(link.Due(occ, t0, t0.AddMinutes(49)));
        Assert.Equal([(true, "Board meeting")], link.Due(occ, t0.AddMinutes(49), t0.AddMinutes(50)).Select(d => (d.Start, d.Event.Summary)));
        // At 15:00 one ends and the next starts 10 minutes early (14:50): the start is due at 14:50, then the end at 15:00.
        var around = link.Due(occ, t0.AddMinutes(100), t0.AddMinutes(121)).Select(d => (d.Start, d.Event.Summary)).ToList();
        Assert.Equal([(true, "Board follow-up"), (false, "Board meeting")], around);
        var noLead = new CalendarLink { AtStart = new ScheduleEntry(), AtEnd = new ScheduleEntry() };
        List<(bool, CalendarOccurrence)> sameMoment = noLead.Due([new(t0, t0.AddHours(1), "A"), new(t0.AddHours(1), t0.AddHours(2), "B")], t0.AddMinutes(59), t0.AddMinutes(60));
        Assert.Equal([false, true], sameMoment.Select(d => d.Item1)); // the end first, so the next booking's setup wins
        Assert.Empty(new CalendarLink().Due(occ, t0, t0.AddDays(1))); // nothing chosen: nothing due
        Assert.Equal(CalendarLink.MaxLeadMinutes, new CalendarLink { LeadMinutes = 999 }.Normalized().LeadMinutes);
    }

    [Fact]
    public void Calendar_files_are_read_only_when_they_are_calendars()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-ics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string good = Path.Combine(folder, "room.ics"), bad = Path.Combine(folder, "notes.ics");
            File.WriteAllText(good, Ics);
            File.WriteAllText(bad, "not a calendar");
            Assert.Equal(4, CalendarLink.Read(good)!.Count);
            Assert.Null(CalendarLink.Read(bad));
            Assert.Null(CalendarLink.Read(Path.Combine(folder, "missing.ics")));
            Assert.Null(CalendarLink.Read(null));
            var s = new AppSettings { Calendar = new CalendarLink { File = good, AtStart = new ScheduleEntry { Action = "Rockets", Time = "nonsense" } } }.Normalize();
            Assert.Equal(good, s.Calendar!.File);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>FR-109: rules from the organisation, and settings to a file and back.</summary>
public class PolicyTransferTests
{
    [Fact]
    public void Rules_come_from_the_machine_first_then_the_user()
    {
        var machine = new Dictionary<string, object?> { ["AllowPhoneRemote"] = 0, ["NeverShowApps"] = new[] { "KeePass", "1Password" }, ["AutoHide"] = (int)SensitiveKinds.Emails };
        var user = new Dictionary<string, object?> { ["AllowPhoneRemote"] = 1, ["AllowRecording"] = 0, ["NeverShowApps"] = "Bitwarden;Signal" };
        PolicySet p = PolicySet.From(machine, user);
        Assert.False(p.PhoneRemoteAllowed); // the machine's rule wins
        Assert.False(p.RecordingAllowed);   // the user's fills in
        Assert.True(p.ScreenshotsAllowed);
        Assert.Equal(["KeePass", "1Password", "Bitwarden", "Signal"], p.NeverShowApps);
        Assert.Equal(SensitiveKinds.Emails, p.AutoHide);
        Assert.True(p.Any);
        Assert.False(PolicySet.None.Any);
        Assert.False(PolicySet.From(null, null).Any);
        Assert.Contains("Turned off: the phone remote, recording.", p.Words());
        Assert.True(p.KeepsNeverShow("keepass"));
    }

    [Fact]
    public void Rules_are_applied_to_the_settings()
    {
        var p = new PolicySet { NeverShowApps = ["KeePass"], AutoHide = SensitiveKinds.Cards, CheckBeforeShowing = true, PhoneRemoteAllowed = false, ReplayAllowed = false, UpdateCheckAllowed = false, VoiceCommandsAllowed = false };
        var s = new AppSettings { PhoneRemote = true, ReplayOn = true, CheckForUpdates = true, VoiceCommands = true, AutoHideKinds = SensitiveKinds.Emails, NeverShowApps = ["Signal"] };
        p.Apply(s);
        Assert.False(s.PhoneRemote || s.ReplayOn || s.CheckForUpdates || s.VoiceCommands);
        Assert.True(s.CheckBeforeShowing);
        Assert.Equal(SensitiveKinds.Emails | SensitiveKinds.Cards, s.AutoHideKinds);
        Assert.Contains("KeePass", s.NeverShowApps);
        Assert.Contains("Signal", s.NeverShowApps);
    }

    [Fact]
    public void Rules_are_read_from_the_registry()
    {
        string path = @"Software\ScpTestPolicies\" + Guid.NewGuid().ToString("N");
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("AllowScreenshots", 0, RegistryValueKind.DWord);
                key.SetValue("NeverShowApps", new[] { "KeePass" }, RegistryValueKind.MultiString);
                key.SetValue("CheckBeforeShowing", 1, RegistryValueKind.DWord);
            }
            PolicySet p = PolicySet.Read(path);
            Assert.False(p.ScreenshotsAllowed);
            Assert.True(p.CheckBeforeShowing);
            Assert.Equal(["KeePass"], p.NeverShowApps);
            Assert.False(PolicySet.Read(path + "-none").Any);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ScpTestPolicies", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Settings_travel_without_what_belongs_to_this_pc()
    {
        var s = new AppSettings { RemoteKey = "secret-key", OutputDevice = @"\\.\DISPLAY2", ShowKeys = true, SplitLayout = "Grid", AutoHideWords = ["Acme"] };
        s.Favourites[0] = new Favourite { AppName = "POWERPNT", Title = "Deck", Notes = "Start with Q3" };
        string json = SettingsTransfer.Export(s);
        Assert.DoesNotContain("secret-key", json);
        Assert.DoesNotContain("DISPLAY2", json);
        var here = new AppSettings { RemoteKey = "mine", OutputDevice = @"\\.\DISPLAY3" };
        (AppSettings? read, string? problem) = SettingsTransfer.Import(json, here);
        Assert.Null(problem);
        Assert.True(read!.ShowKeys);
        Assert.Equal("Grid", read.SplitLayout);
        Assert.Equal(["Acme"], read.AutoHideWords);
        Assert.Equal("Start with Q3", read.Favourites[0]!.Notes);
        Assert.Equal("mine", read.RemoteKey); // this PC's own pairing stays
        Assert.Equal(@"\\.\DISPLAY3", read.OutputDevice);
        Assert.Contains("isn't", SettingsTransfer.Import("{not json", here).Problem);
        Assert.Equal(["cli:export-settings:" + Path.GetFullPath("x.json")], CommandLine.Parse(["--export-settings", "x.json"]).Request!.Actions);
        Assert.Equal(["cli:import-settings:" + Path.GetFullPath("x.json")], CommandLine.Parse(["--import-settings=x.json"]).Request!.Actions);
        Assert.Contains("needs a file", CommandLine.Parse(["--import-settings"]).Error);
    }
}

/// <summary>FR-110: voice commands.</summary>
public class VoiceCommandTests
{
    [Theory]
    [InlineData("Presenter, next slide.", "next-slide")]
    [InlineData("presenter next please", "next-slide")]
    [InlineData("Presenter, go back!", "previous-slide")]
    [InlineData("Presenter, blank the screen", "voice:blank-on")]
    [InlineData("Presenter show the screen", "voice:blank-off")]
    [InlineData("Presenter, show favourite two", "favourite-2")]
    [InlineData("Presenter, show favorite 3", "favourite-3")]
    [InlineData("Presenter, scene nine", "scene-9")]
    [InlineData("Presenter, zoom in", "voice:zoom-in")]
    [InlineData("Presenter, take a break", "blank-countdown")]
    [InlineData("PRESENTER, SAVE THE LAST MINUTE", "save-replay")]
    public void Phrases_after_the_wake_word_run_their_command(string heard, string command) => Assert.Equal(command, VoiceCommands.Match(heard)?.Command);

    [Theory]
    [InlineData("next slide")] // no wake word
    [InlineData("the presenter said next slide")]
    [InlineData("Presenter, next slide now")] // more than the phrase
    [InlineData("Presenter, show favourite ten")]
    [InlineData("Presenter, show favourite 0")]
    [InlineData("Presenter, quit")]
    [InlineData("Presenter, open settings")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_does_nothing(string? heard) => Assert.Null(VoiceCommands.Match(heard));

    [Fact]
    public void Without_the_wake_word_the_phrase_alone_counts_and_nothing_records()
    {
        Assert.Equal("next-slide", VoiceCommands.Match("Next slide.", wakeWord: false)?.Command);
        Assert.Equal("presenter 2 next", VoiceCommands.Tidy("Presenter, two... next!"));
        Assert.DoesNotContain(VoiceCommands.Commands, c => c.Contains("record", StringComparison.Ordinal) || c.Contains("settings", StringComparison.Ordinal) || c == "quit");
        Assert.All(VoiceCommands.Examples, e => Assert.NotNull(VoiceCommands.Match("presenter " + e)));
        Assert.False(new AppSettings().VoiceCommands);
        Assert.Equal(["cli:on:voice-commands"], CommandLine.Parse(["--on", "voice-commands"]).Request!.Actions);
        Assert.NotNull(UI.Icons.ForCommand("voice-commands"));
    }
}

/// <summary>FR-111: speaker notes, on the presenter's screen and encrypted to the phone.</summary>
public class SpeakerNotesTests
{
    [Fact]
    public void Notes_are_encrypted_so_only_the_paired_phone_reads_them()
    {
        byte[] key = RemoteAuth.NewKey();
        string notes = "Start with the Q3 numbers.\nAsk Zoë about the 2027 plan. ✓";
        (string n, string c, string t) = NotesCipher.Encrypt(key, notes);
        Assert.Equal(notes, NotesCipher.Decrypt(key, n, c, t));
        // The words aren't in what's sent: checked on the bytes (a two-letter check on the text could match random letters by chance).
        byte[] sent = RemoteAuth.FromBase64UrlLong(c)!;
        Assert.Equal(-1, sent.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("Q3 numbers")));
        Assert.Equal(-1, sent.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("Zoë")));
        Assert.Null(NotesCipher.Decrypt(RemoteAuth.NewKey(), n, c, t)); // another key
        Assert.Null(NotesCipher.Decrypt(key, n, c[..^2] + (c[^2] == 'A' ? "B" : "A") + c[^1], t)); // tampered with
        Assert.Null(NotesCipher.Decrypt(key, n, c, t[..^1] + (t[^1] == '0' ? "1" : "0")));
        (string n2, string c2, _) = NotesCipher.Encrypt(key, notes);
        Assert.NotEqual(n, n2); // a fresh nonce every time
        Assert.NotEqual(c, c2);
        string longNotes = new('x', NotesCipher.MaxLength);
        (string ln, string lc, string lt) = NotesCipher.Encrypt(key, longNotes);
        Assert.Equal(longNotes, NotesCipher.Decrypt(key, ln, lc, lt)); // many keystream blocks
    }

    [Fact]
    public void The_phone_page_decrypts_exactly_what_the_pc_encrypted()
    {
        if (PhoneRemoteHttpTests.FindNode() is not { } node) return;
        string html = Remote.RemotePage.Html;
        string script = html[(html.IndexOf("<script>", StringComparison.Ordinal) + 8)..html.IndexOf("</script>", StringComparison.Ordinal)];
        string crypto = script[..script.IndexOf("const KEY_STORE", StringComparison.Ordinal)];
        byte[] key = RemoteAuth.NewKey();
        string notes = "Line one\nZoë: ✓ “quoted”";
        (string n, string c, string t) = NotesCipher.Encrypt(key, notes);
        string file = Path.Combine(Path.GetTempPath(), $"scp-notes-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, crypto + $$"""

            const key = fromBase64Url("{{RemoteAuth.Base64Url(key)}}");
            const good = decryptNotes(key, { n: "{{n}}", c: "{{c}}", t: "{{t}}" });
            const bad = decryptNotes(key, { n: "{{n}}", c: "{{c}}", t: "{{t[..^1]}}0" });
            process.stdout.write(JSON.stringify({ good, bad }));
            """);
        try
        {
            var info = new ProcessStartInfo(node, $"\"{file}\"") { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true };
            using Process p = Process.Start(info)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var result = System.Text.Json.Nodes.JsonNode.Parse(output)!;
            Assert.Equal(notes, (string?)result["good"]);
            if (!t.EndsWith('0')) Assert.Null((string?)result["bad"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Notes_are_kept_with_favourites_and_scenes_tidied()
    {
        Assert.Equal("a\nb", NotesCipher.Tidy("  a\r\nb \u0007 "));
        Assert.Equal(NotesCipher.MaxLength, NotesCipher.Tidy(new string('n', 5000)).Length);
        Assert.Equal("", NotesCipher.Tidy(null));
        var s = new AppSettings();
        s.Favourites[2] = new Favourite { AppName = "x", Title = "y", Notes = "  hi  " };
        s.Scenes[0] = new Scene { Source = new Favourite { AppName = "x", Title = "y" }, Notes = "  scene notes " };
        s.Normalize();
        Assert.Equal("hi", s.Favourites[2]!.Notes);
        Assert.Equal("scene notes", s.Scenes[0]!.Notes);
        Assert.Contains("notes", s.Scenes[0]!.Words());
        Assert.True(new AppSettings().NotesOnPhone);
        foreach (string id in new[] { "notes", "edit-notes" }) Assert.NotNull(UI.Icons.ForCommand(id));
    }

    [Fact]
    public void The_notes_window_says_what_it_shows()
    {
        using var window = new UI.NotesWindow(() => { });
        window.ShowNotes("Deck", "Start with Q3\nThen the demo", showing: true);
        Assert.Equal("Notes: Deck", window.Showing.Title);
        Assert.Contains("Then the demo", window.Showing.Notes);
        window.ShowNotes("Deck", "", showing: true);
        Assert.Contains("Choose Edit", window.Showing.Notes);
        window.ShowNotes(null, "", showing: true);
        Assert.Contains("favourites and scenes", window.Showing.Notes);
        window.ShowNotes(null, "", showing: false);
        Assert.Equal("Nothing is shown", window.Showing.Title);
    }
}
