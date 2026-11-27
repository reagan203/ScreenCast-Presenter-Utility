using System.Text.Json;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-121: time budgets for what's shown.</summary>
public class SectionClockTests
{
    private static readonly DateTime T = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Time_adds_up_each_time_something_comes_back()
    {
        var clock = new SectionClock();
        clock.Show("a", "Demo", TimeSpan.FromMinutes(5), T);
        clock.Show("b", "Q&A", TimeSpan.Zero, T.AddMinutes(2));
        clock.Show("a", "Demo", TimeSpan.FromMinutes(5), T.AddMinutes(3));
        SectionTime demo = clock.Current(T.AddMinutes(4))!;
        Assert.Equal(TimeSpan.FromMinutes(3), demo.Spent); // 2 + 1
        Assert.Equal(["Demo", "Q&A"], clock.All(T.AddMinutes(4)).Select(s => s.Name)); // in the order first shown
        Assert.Equal(TimeSpan.FromMinutes(1), clock.All(T.AddMinutes(4))[1].Spent);
    }

    [Fact]
    public void Time_stops_while_paused()
    {
        var clock = new SectionClock();
        clock.Show("a", "Demo", TimeSpan.FromMinutes(5), T);
        clock.Pause(T.AddMinutes(1));
        clock.Pause(T.AddMinutes(2)); // twice is fine
        clock.Resume(T.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(2), clock.Current(T.AddMinutes(11))!.Spent);
        clock.Reset();
        Assert.Null(clock.Current(T));
        Assert.Empty(clock.All(T));
    }

    [Fact]
    public void Warnings_come_once_each()
    {
        var clock = new SectionClock();
        clock.Show("a", "Demo", TimeSpan.FromMinutes(5), T);
        Assert.Equal(SectionAlert.None, clock.Check(T.AddMinutes(3)));
        Assert.Equal(SectionAlert.OneMinuteLeft, clock.Check(T.AddMinutes(4)));
        Assert.Equal(SectionAlert.None, clock.Check(T.AddMinutes(4.5)));
        Assert.Equal(SectionAlert.Over, clock.Check(T.AddMinutes(5)));
        Assert.Equal(SectionAlert.None, clock.Check(T.AddMinutes(6)));
        clock.Show("b", "Other", TimeSpan.Zero, T.AddMinutes(7));
        clock.Show("a", "Demo", TimeSpan.FromMinutes(5), T.AddMinutes(8));
        Assert.Equal(SectionAlert.None, clock.Check(T.AddMinutes(9))); // coming back doesn't say it again
    }

    [Fact]
    public void Jumping_past_the_minute_warning_says_only_over()
    {
        var clock = new SectionClock();
        clock.Show("a", "Demo", TimeSpan.FromMinutes(2), T);
        Assert.Equal(SectionAlert.Over, clock.Check(T.AddMinutes(3)));
        Assert.Equal(SectionAlert.None, clock.Check(T.AddMinutes(3.1)));
        var noBudget = new SectionClock();
        noBudget.Show("x", "x", TimeSpan.Zero, T);
        Assert.Equal(SectionAlert.None, noBudget.Check(T.AddHours(2)));
        var short1 = new SectionClock();
        short1.Show("x", "x", TimeSpan.FromMinutes(1), T); // a one-minute budget has no "one minute left"
        Assert.Equal(SectionAlert.None, short1.Check(T.AddSeconds(10)));
    }

    [Fact]
    public void The_bar_says_where_it_is()
    {
        Assert.Equal("Demo 3:12 of 5:00", SectionClock.Text(new SectionTime("a", "Demo", TimeSpan.FromSeconds(192), TimeSpan.FromMinutes(5))));
        Assert.Equal("Demo 1:05 over", SectionClock.Text(new SectionTime("a", "Demo", TimeSpan.FromSeconds(365), TimeSpan.FromMinutes(5))));
        Assert.Equal("Demo 0:30", SectionClock.Text(new SectionTime("a", "Demo", TimeSpan.FromSeconds(30), TimeSpan.Zero)));
        Assert.Equal(0, SectionClock.Normalize(-3));
        Assert.Equal(SectionClock.MaxMinutes, SectionClock.Normalize(9999));
    }
}

/// <summary>FR-120: a timer on the TV.</summary>
public class ActivityTimerTests
{
    [Fact]
    public void It_counts_down_then_says_times_up()
    {
        Assert.Equal("Exercise 4:59", ActivityTimer.Text("Exercise", TimeSpan.FromSeconds(298.5)));
        Assert.Equal("Time left 0:01", ActivityTimer.Text("  ", TimeSpan.FromMilliseconds(10)));
        Assert.Equal("Time's up", ActivityTimer.Text("Exercise", TimeSpan.Zero));
        Assert.Equal(ActivityTimer.MaxLabel, ActivityTimer.TidyLabel(new string('x', 200)).Length);
        Assert.Equal("Group work", ActivityTimer.TidyLabel(" Group\nwork "));
        Assert.Equal(1, ActivityTimer.Normalize(0));
        Assert.Equal(ActivityTimer.MaxMinutes, ActivityTimer.Normalize(500));
    }

    [Fact]
    public void It_sits_in_a_corner_nothing_else_uses()
    {
        Assert.Equal(InsetCorner.TopLeft, ActivityTimer.Corner(null, null));
        Assert.Equal(InsetCorner.TopRight, ActivityTimer.Corner(InsetCorner.TopLeft, null));
        Assert.Equal(InsetCorner.BottomLeft, ActivityTimer.Corner(InsetCorner.TopLeft, InsetCorner.TopRight));
        Assert.Equal(InsetCorner.TopLeft, ActivityTimer.Corner(InsetCorner.TopRight, InsetCorner.BottomRight));
    }

    [Fact]
    public void Its_chip_stays_on_the_tv_and_clear_of_the_captions()
    {
        var canvas = new SizeF(1920, 1080);
        foreach (InsetCorner corner in Enum.GetValues<InsetCorner>())
        {
            RectangleF chip = ActivityTimer.Chip(canvas, corner, 400, bottomLimit: 900, topLimit: 0);
            Assert.True(new RectangleF(PointF.Empty, canvas).Contains(chip), $"{corner}: {chip}");
            if (corner is InsetCorner.BottomLeft or InsetCorner.BottomRight) Assert.True(chip.Bottom <= 900, "above the captions");
        }
        Assert.True(ActivityTimer.Chip(canvas, InsetCorner.TopLeft, 400, 1080, topLimit: 200).Top >= 200, "below top captions");
        Assert.True(ActivityTimer.Chip(canvas, InsetCorner.TopLeft, 99999, 1080).Width < 1920, "a long label doesn't go past the edge");
    }
}

/// <summary>FR-123: the night filter's colours, checked without a GPU.</summary>
public class NightFilterTests
{
    [Fact]
    public void Off_leaves_colours_as_they_are_at_their_brightness()
    {
        Assert.Equal((0.2f, 0.4f, 0.6f), NightFilter.Apply(NightMode.Off, 0.2f, 0.4f, 0.6f));
        (float r, float g, float b) = NightFilter.Apply(NightMode.Off, 1, 1, 1, opacity: 0.5f);
        Assert.Equal((0.5f, 0.5f, 0.5f), (r, g, b));
    }

    [Fact]
    public void Invert_turns_white_pages_dark_and_dark_text_light()
    {
        (float r, float g, float b) white = NightFilter.Apply(NightMode.Invert, 1, 1, 1);
        Assert.True(white.r < 0.01f && white.g < 0.01f && white.b < 0.01f, $"white became {white}");
        (float r, float g, float b) black = NightFilter.Apply(NightMode.Invert, 0, 0, 0);
        Assert.True(black.r > 0.99f && black.g > 0.99f && black.b > 0.99f, $"black became {black}");
        (float r, float g, float b) red = NightFilter.Apply(NightMode.Invert, 0.8f, 0.1f, 0.1f);
        Assert.True(red.r > red.g && red.r > red.b, $"red stays reddish: {red}");
        (float r, float g, float b) faded = NightFilter.Apply(NightMode.Invert, 0, 0, 0, opacity: 0);
        Assert.Equal((0f, 0f, 0f), faded); // faded out is black, never a flash of white
    }

    [Fact]
    public void Soften_tones_down_white_and_leaves_dark_alone()
    {
        (float r, float g, float b) white = NightFilter.Apply(NightMode.Soften, 1, 1, 1);
        Assert.InRange(white.r, 0.6f, 0.8f);
        Assert.True(white.b < white.r, "warm, not blue-white");
        Assert.Equal(0, NightFilter.Apply(NightMode.Soften, 0, 0, 0).R);
        Assert.Equal(0.25f, NightFilter.Soften(0.25f), 3); // dark greys barely change
        Assert.True(NightFilter.Soften(0.9f) < 0.9f);
        Assert.Equal(NightFilter.Soften(1), NightFilter.Soften(5)); // clamped
    }

    [Fact]
    public void Names_are_read_safely()
    {
        Assert.Equal(NightMode.Invert, NightFilter.Parse("invert"));
        Assert.Equal(NightMode.Off, NightFilter.Parse("purple"));
        Assert.Equal(NightMode.Off, NightFilter.Parse(null));
        Assert.Equal("Turn light pages dark", NightFilter.Words(NightMode.Invert));
        Assert.Equal(20, NightFilter.Matrix(NightMode.Soften, 1).Length);
    }
}

/// <summary>FR-118: the small picture's shape.</summary>
public class InsetShapeTests
{
    [Fact]
    public void A_circle_shows_the_middle_square()
    {
        Assert.Equal(new Rectangle(140, 0, 360, 360), InsetShapes.Shown(new Rectangle(0, 0, 640, 360), InsetShape.Circle));
        Assert.Equal(new Rectangle(10, 150, 300, 300), InsetShapes.Shown(new Rectangle(10, 0, 300, 600), InsetShape.Circle));
        Assert.Equal(new Rectangle(0, 0, 640, 360), InsetShapes.Shown(new Rectangle(0, 0, 640, 360), InsetShape.Rounded));
        Assert.Equal(Rectangle.Empty, InsetShapes.Shown(Rectangle.Empty, InsetShape.Circle));
    }

    [Fact]
    public void Corners_are_as_round_as_the_shape()
    {
        var place = new RectangleF(0, 0, 200, 100);
        Assert.Equal(0, InsetShapes.Radius(place, InsetShape.Rectangle));
        Assert.Equal(12, InsetShapes.Radius(place, InsetShape.Rounded), 3);
        Assert.Equal(50, InsetShapes.Radius(place, InsetShape.Circle));
        Assert.Equal(InsetShape.Circle, InsetShapes.Parse("CIRCLE"));
        Assert.Equal(InsetShape.Rectangle, InsetShapes.Parse("hexagon"));
    }
}

/// <summary>Rev 10's lines to the capture helper, read by the helper's own parser.</summary>
public class Rev10ProtocolTests
{
    private sealed class Recorder : IEffectsEngine
    {
        public readonly List<string> Calls = [];
        public void Click(PointF at, bool secondary) { }
        public void Pointer(PointF? at) { }
        public void Laser(PointF? at) { }
        public void ShowCursor(bool on) { }
        public void InkStart(InkStroke stroke) { }
        public void InkAdd(int id, IReadOnlyList<PointF> points) { }
        public void InkRemove(int id) { }
        public void InkClear() { }
        public void InkSet(int id, IReadOnlyList<PointF> points) { }
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) { }
        public void Spotlight(PointF? at) { }
        public void Caption(string? name, string? second) { }
        public void Logo(string? path) { }
        public void Qr(string? text, InsetCorner corner) { }
        public void LiveCaption(string? text) { }
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) { }
        public void Timer(string? text, InsetCorner corner, bool up) => Calls.Add($"timer {text ?? "null"} {corner} {up}");
        public void Night(NightMode mode) => Calls.Add("night " + mode);
        public void InsetShape(InsetShape shape) => Calls.Add("shape " + shape);
        public void Video(VideoCommand command, double seconds) => Calls.Add($"video {command} {seconds}");
        public event EventHandler<string>? InsetFailed { add { } remove { } }
    }

    [Fact]
    public void Every_new_line_reaches_the_engine_as_sent()
    {
        var engine = new Recorder();
        foreach (string line in new[]
                 {
                     CaptureProtocol.Timer("Exercise 4:59", InsetCorner.TopRight, up: false),
                     CaptureProtocol.Timer("Time's up", InsetCorner.BottomLeft, up: true),
                     CaptureProtocol.Timer(null, InsetCorner.TopLeft, false),
                     CaptureProtocol.Night(NightMode.Invert),
                     CaptureProtocol.InsetShapeLine(InsetShape.Circle),
                     CaptureProtocol.Video(VideoCommand.Seek, 10),
                     CaptureProtocol.Video(VideoCommand.Pause),
                 })
            Assert.True(CaptureHost.ApplyMark(engine, line), line);
        Assert.Equal(
        [
            "timer Exercise 4:59 TopRight False", "timer Time's up BottomLeft True", "timer null TopLeft False",
            "night Invert", "shape Circle", "video Seek 10", "video Pause 0",
        ], engine.Calls);
    }

    [Fact]
    public void Nonsense_never_puts_anything_on_the_tv()
    {
        var engine = new Recorder();
        foreach (string line in new[] { "timer 99 0 x", "timer 1", "timer x y z", "night 42", "night", "inset-shape -1", "video explode", "video seek" })
            CaptureHost.ApplyMark(engine, line);
        Assert.All(engine.Calls, c => Assert.True(c.StartsWith("timer null", StringComparison.Ordinal) || c is "night Off" or "shape Rectangle", c));
        Assert.Equal("timer none", CaptureProtocol.Timer("", InsetCorner.TopLeft, false));
        Assert.DoesNotContain('\n', CaptureProtocol.Timer("two\nlines", InsetCorner.TopRight, false));
    }
}

/// <summary>FR-122: PowerPoint's notes with the presenter's own.</summary>
public class SlideNotesTests
{
    [Fact]
    public void Notes_are_combined_own_first()
    {
        var slide = new SlideInfo(3, 12, "Mention the Q3 dip.\rThen the plan.", SlideShow: true);
        Assert.Equal("Slide 3 of 12: Mention the Q3 dip.\nThen the plan.", SlideNotes.Combine("", slide));
        Assert.Equal("Thank Ann." + Environment.NewLine + Environment.NewLine + "Slide 3 of 12: Mention the Q3 dip.\nThen the plan.", SlideNotes.Combine("Thank Ann.", slide));
        Assert.Equal("Thank Ann.", SlideNotes.Combine("Thank Ann.", null));
        Assert.Equal("Thank Ann.", SlideNotes.Combine("Thank Ann.", slide with { Notes = "  " }));
        Assert.Equal(NotesCipher.MaxLength, SlideNotes.Combine(new string('x', 1900), slide with { Notes = new string('y', 900) }).Length);
        Assert.Equal("Slide 4", SlideNotes.Title(new SlideInfo(4, 0, "", false)));
        Assert.True(SlideNotes.IsPowerPoint("powerpnt"));
        Assert.False(SlideNotes.IsPowerPoint("WINWORD"));
        Assert.Equal("a\nb c", SlideNotes.Tidy("a\vb\u0007c "));
    }

    [Fact]
    public void Asking_powerpoint_when_it_isnt_running_is_harmless()
    {
        SlideInfo? info = PowerPointReader.ReadOnce(); // null without PowerPoint; whatever it says when it runs
        if (info is not null) Assert.True(info.Slide >= 1);
    }
}

/// <summary>FR-127: what a rehearsal found.</summary>
public class RehearsalTests
{
    private static readonly DateTime T = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Words_and_fillers_are_counted()
    {
        Assert.Equal(6, Rehearsal.CountWords("So, um, it's the Q3 plan.")); // "it's" is one word
        Assert.Equal(0, Rehearsal.CountWords(" ... "));
        List<(string Word, int Count)> fillers = Rehearsal.Fillers(["Um, so you know, it's um basically done.", "You KNOW the rest; ummm no."]);
        Assert.Equal(("um", 2), fillers[0]); // "ummm" isn't "um"
        Assert.Contains(("you know", 2), fillers);
        Assert.Contains(("basically", 1), fillers);
        Assert.DoesNotContain(fillers, f => f.Word == "like i said");
        Assert.Empty(Rehearsal.Fillers(["Summary of the term.", "humble, ummm"])); // inside other words they don't count
        Assert.Equal([("erm", 1)], Rehearsal.Fillers(["an erm-less talk"])); // a whole "erm" does
    }

    [Fact]
    public void Each_part_gets_its_words_time_and_pace()
    {
        var sections = new List<SectionTime>
        {
            new("a", "Intro", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
            new("b", "Demo", TimeSpan.FromMinutes(3), TimeSpan.Zero),
        };
        var switches = new List<(DateTime, string)> { (T, "a"), (T.AddMinutes(2), "b") };
        var said = new List<SaidLine>
        {
            new("one two three four", T.AddSeconds(5), T.AddSeconds(8)),
            new("five six", T.AddSeconds(30), T.AddSeconds(32)),
            new("seven eight nine", T.AddMinutes(2.5), T.AddMinutes(2.6)),
        };
        RehearsalReport r = Rehearsal.Build(T, T.AddMinutes(5), sections, switches, said, talkMinutes: 4);
        Assert.Equal(9, r.Words);
        Assert.Equal(TimeSpan.FromMinutes(5), r.Total);
        Assert.Equal(9 / 5.0, r.WordsPerMinute, 3);
        Assert.Equal(6, r.Sections[0].Words);
        Assert.Equal(3, r.Sections[1].Words);
        Assert.Equal(TimeSpan.FromMinutes(1), r.Sections[0].Over);
        Assert.True(r.LongestPause > TimeSpan.FromMinutes(1.9));
        string summary = Rehearsal.Summary(r);
        Assert.Contains("of 4 minutes", summary);
        Assert.Contains("1 part over time", summary);
        string text = Rehearsal.Text(r);
        Assert.Contains("Intro: 2:00 of 1:00, 1:00 over; 6 words", text);
        Assert.Contains("1:00 over)", text);
    }

    [Fact]
    public void An_empty_rehearsal_reads_sensibly()
    {
        RehearsalReport r = Rehearsal.Build(T, T, [], [], [], 0);
        Assert.Equal(0, r.WordsPerMinute);
        Assert.Contains("You took 0:00", Rehearsal.Summary(r));
        Assert.Contains("nothing was shown", Rehearsal.Text(r));
        Assert.Equal("a little slow", Rehearsal.Pace(80));
        Assert.Equal("comfortable", Rehearsal.Pace(140));
        Assert.Equal("a little fast", Rehearsal.Pace(200));
        Assert.EndsWith(".txt", Rehearsal.FileName(T));
    }
}

/// <summary>FR-128: words for live captions.</summary>
public class CaptionVocabularyTests
{
    [Fact]
    public void The_list_is_tidied()
    {
        Assert.Equal(["Ninthmind", "Wanjiru"], CaptionVocabulary.FromText(" Ninthmind, ninthmind ;\nWanjiru,, "));
        Assert.Equal(CaptionVocabulary.MaxWords, CaptionVocabulary.Normalize(Enumerable.Range(0, 300).Select(i => "w" + i)).Count);
        Assert.Equal(CaptionVocabulary.MaxLength, CaptionVocabulary.Normalize([new string('x', 99)])[0].Length);
        List<string> words = ["Ninthmind", "Kubernetes, Inc.", "Zoë"];
        Assert.Equal(words, CaptionVocabulary.FromToken(CaptionVocabulary.Token(words)));
    }

    [Fact]
    public void Heard_words_are_spelled_as_listed()
    {
        string[] words = ["Ninthmind", "Kubernetes"];
        Assert.Equal("Welcome to Ninthmind today", CaptionVocabulary.Respell("Welcome to ninth mind today", words));
        Assert.Equal("Ninthmind and Kubernetes", CaptionVocabulary.Respell("ninth-mind and kubernetes", words));
        Assert.Equal("ninthminded people", CaptionVocabulary.Respell("ninthminded people", words)); // only whole words
        Assert.Equal("as heard", CaptionVocabulary.Respell("as heard", []));
        Assert.Equal("an ai", CaptionVocabulary.Respell("an ai", ["AI"])); // too short to guess at: left as heard
    }
}

/// <summary>FR-129 and the new options: the command line.</summary>
public class Rev10CommandLineTests
{
    private static CliRequest Ok(params string[] args)
    {
        (CliRequest? request, string? error) = CommandLine.Parse(args);
        Assert.True(request is not null, error);
        return request!;
    }

    private static string Bad(params string[] args)
    {
        (CliRequest? request, string? error) = CommandLine.Parse(args);
        Assert.Null(request);
        return error!;
    }

    [Fact]
    public void Status_and_is_read_what_is_on()
    {
        Assert.Equal(["cli:status"], Ok("--status").Actions);
        Assert.Equal(["cli:is:spotlight"], Ok("--is", "Spotlight").Actions);
        Assert.Equal(["cli:is:showing"], Ok("--is=showing").Actions);
        Assert.Contains("--is needs one of", Bad("--is", "rockets"));
        Assert.Contains("--is needs one of", Bad("--is"));
        Assert.Contains("follow-me", CommandLine.Switches);
        Assert.Contains("night-filter", CommandLine.Switches);
        Assert.All(CommandLine.Switches, s => Assert.Contains(s, CommandLine.StatusNames));
    }

    [Fact]
    public void New_things_to_show_take_their_files()
    {
        string pdf = Ok("--pdf", @"C:\Docs\Menu.pdf").Actions[0];
        Assert.Equal(@"cli:pdf:C:\Docs\Menu.pdf", pdf);
        Assert.Equal(@"cli:pdf-page:3:C:\Docs\Menu.pdf", Ok("--pdf", @"C:\Docs\Menu.pdf", "--page", "3").Actions[0]);
        Assert.Equal(@"cli:video:C:\V\Intro.mp4", Ok("--video", @"C:\V\Intro.mp4").Actions[0]);
        Assert.Equal(@"cli:gif:C:\R\Clip.mp4", Ok("--gif", @"C:\R\Clip.mp4").Actions[0]);
        Assert.Contains("needs a PDF", Bad("--pdf", @"C:\x.docx"));
        Assert.Contains("needs an MP4", Bad("--video", @"C:\x.txt"));
        Assert.Contains("needs an MP4 recording", Bad("--gif", @"C:\x.mov"));
        Assert.Contains("--page goes after --pdf", Bad("--page", "2"));
        Assert.Contains("needs a page number", Bad("--pdf", @"C:\a.pdf", "--page", "zero"));
        Assert.Contains("needs a file", Bad("--video"));
    }

    [Fact]
    public void The_timer_night_filter_and_simple_switches()
    {
        Assert.Equal(["timer:10"], Ok("--timer", "10").Actions);
        Assert.Equal(["cli:timer-off"], Ok("--timer", "OFF").Actions);
        Assert.Contains("--timer needs minutes", Bad("--timer", "0"));
        Assert.Contains("--timer needs minutes", Bad("--timer", "999"));
        Assert.Equal(["night:invert"], Ok("--night", "Invert").Actions);
        Assert.Contains("--night needs", Bad("--night", "purple"));
        Assert.Equal(["show-whiteboard", "room-sign", "copy-text", "cast"], Ok("--whiteboard", "--room-sign", "--copy-text", "--cast").Actions);
        Assert.Contains("doesn't take a value", Bad("--whiteboard=yes"));
        Assert.Equal(["cli:on:follow-me", "cli:off:clicker-keys"], Ok("--on", "follow-me", "--off", "CLICKER-KEYS").Actions);
        Assert.Contains("--whiteboard", CommandLine.Usage);
        Assert.Contains("--status", CommandLine.Usage);
    }

    [Fact]
    public void The_exes_own_new_switches_are_never_requests()
    {
        Assert.False(CommandLine.IsRequest([CaptureProtocol.PdfSwitch]));
        Assert.False(CommandLine.IsRequest([CaptureProtocol.ReadTextSwitch]));
        Assert.False(CommandLine.IsRequest([CaptureProtocol.GifSwitch]));
    }
}

/// <summary>Rev 10's settings: saved, loaded and made safe.</summary>
public class Rev10SettingsTests
{
    [Fact]
    public void Privacy_guards_are_on_from_the_start()
    {
        var s = new AppSettings();
        Assert.True(s.LeaveOutTaskbar);
        Assert.True(s.HideDesktopIcons);
        Assert.True(s.HoldForFileBoxes);
        Assert.True(s.BrowserPageOnly);
        Assert.True(s.WarnWholeScreenShare);
        Assert.False(s.ClickerKeys); // takes keys away from the presenter: only when chosen
        Assert.Equal(NightMode.Off, s.Night);
    }

    [Fact]
    public void Nonsense_from_a_file_is_made_safe()
    {
        var s = new AppSettings
        {
            TimerMinutes = 999,
            TimerLabel = "",
            NightFilter = "purple",
            InsetShape = "hexagon",
            FollowMeApps = ["POWERPNT", "powerpnt", "KeePass", ""],
            CaptionWords = ["Ninthmind", "ninthmind", " "],
            RoomSignName = "  Board\nroom  ",
            SpeakerScreenDevice = " ",
        };
        s.Favourites[0] = new Favourite { AppName = "Video", Title = "x", Minutes = -5, DocumentPage = -2, VideoPath = " " };
        s.Scenes[0] = new Scene { Source = new Favourite { AppName = "x", Title = "y", Minutes = 5000 }, InsetShape = "blob", Night = "neon" };
        s.Normalize();
        Assert.Equal(ActivityTimer.MaxMinutes, s.TimerMinutes);
        Assert.Equal(ActivityTimer.DefaultLabel, s.TimerLabel);
        Assert.Equal("Off", s.NightFilter);
        Assert.Equal("Rectangle", s.InsetShape);
        Assert.Equal(["POWERPNT"], s.FollowMeApps); // KeePass is a never-show app
        Assert.Equal(["Ninthmind"], s.CaptionWords);
        Assert.Equal("Board room", s.RoomSignName);
        Assert.Null(s.SpeakerScreenDevice);
        Assert.Equal(0, s.Favourites[0]!.Minutes);
        Assert.Equal(1, s.Favourites[0]!.DocumentPage);
        Assert.Null(s.Favourites[0]!.VideoPath);
        Assert.Equal(SectionClock.MaxMinutes, s.Scenes[0]!.Source!.Minutes);
        Assert.Equal("Rectangle", s.Scenes[0]!.InsetShape);
        Assert.Equal("Off", s.Scenes[0]!.Night);
    }

    [Fact]
    public void Everything_new_survives_saving_and_loading()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scp-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Load();
            AppSettings s = store.Current;
            s.LeaveOutTaskbar = false;
            s.FollowMeApps = ["chrome"];
            s.Night = NightMode.Soften;
            s.InsetShapeMode = InsetShape.Circle;
            s.ClickerKeys = true;
            s.TimerMinutes = 12;
            s.TimerLabel = "Group work";
            s.CaptionWords = ["Ninthmind"];
            s.RoomSignName = "Lab";
            s.RoomSignTitles = true;
            s.WhiteboardLight = true;
            s.Favourites[2] = new Favourite { AppName = PdfPages.App, Title = "Menu", DocumentPath = @"C:\d\Menu.pdf", DocumentPage = 4, Minutes = 7 };
            s.Playlists = [new Playlist { Name = "Lobby", Items = [new PlaylistItem { Kind = "Video", Path = @"C:\v\loop.mp4" }, new PlaylistItem { Kind = "RoomSign" }] }];
            Assert.True(store.Save());
            AppSettings back = new SettingsStore(path).Load();
            Assert.False(back.LeaveOutTaskbar);
            Assert.Equal(["chrome"], back.FollowMeApps);
            Assert.Equal(NightMode.Soften, back.Night);
            Assert.Equal(InsetShape.Circle, back.InsetShapeMode);
            Assert.True(back.ClickerKeys);
            Assert.Equal((12, "Group work"), (back.TimerMinutes, back.TimerLabel));
            Assert.Equal(["Ninthmind"], back.CaptionWords);
            Assert.Equal(("Lab", true, true), (back.RoomSignName, back.RoomSignTitles, back.WhiteboardLight));
            Assert.Equal((@"C:\d\Menu.pdf", 4, 7), (back.Favourites[2]!.DocumentPath, back.Favourites[2]!.DocumentPage, back.Favourites[2]!.Minutes));
            Assert.Equal(["Video", "RoomSign"], back.Playlists[0].Items.Select(i => i.Kind));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Every rev 10 action is in the tray (as the presenter asked), has an icon and a plain "How do I…?".</summary>
public class Rev10MenuTests
{
    private static readonly string[] NewActions =
        ["show-pdf", "show-video", "show-whiteboard", "room-sign", "follow-me", "clicker-keys", "timer", "night-filter", "inset-shape", "cast", "speaker-screen", "rehearse", "copy-text", "make-gif"];

    private static readonly TrayMenuState Idle = new() { Status = "Not showing anything" };
    private static readonly TrayMenuState Presenting = Idle with { Session = SessionState.Presenting, Status = "● Showing on TV" };

    private static List<MenuEntry> All(TrayMenuState s) => [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(s))];

    [Fact]
    public void Every_new_action_is_known_named_plainly_and_has_an_icon()
    {
        foreach (string id in NewActions)
        {
            ShortcutAction action = ShortcutCatalog.Find(id) ?? throw new Xunit.Sdk.XunitException($"{id} isn't in the catalogue");
            Assert.DoesNotMatch(@"(?i)\b(source|output|picker|palette|ocr|dwm|wgc)\b", action.Name);
            Assert.NotNull(Icons.ForCommand(id));
            Assert.True(TrayMenuPlan.IsKnownCommand(id));
        }
        foreach (char icon in new[] { Icons.Document, Icons.Edit, Icons.Copy, Icons.Stopwatch }) Assert.Contains(icon, Icons.All);
    }

    [Fact]
    public void Every_new_action_is_in_the_tray()
    {
        var everywhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (TrayMenuState s in new[] { Idle, Presenting, Presenting with { TimerOn = true }, Presenting with { Page = 2, Pages = 5 } })
            foreach (MenuEntry e in All(s).Where(e => e.Command is not null)) everywhere.Add(e.Command!);
        foreach (string id in NewActions.Where(a => a is not ("night-filter" or "inset-shape")))
            Assert.Contains(id, everywhere);
        Assert.Contains("night:invert", everywhere); // the night filter and the shape are chosen in submenus
        Assert.Contains("inset-shape:circle", everywhere);
        Assert.Contains("timer:5", everywhere);
    }

    [Fact]
    public void The_menu_shows_what_is_on()
    {
        Assert.True(All(Presenting with { FollowMe = true }).Single(e => e.Command == "follow-me").Checked);
        Assert.True(All(Presenting with { Night = NightMode.Soften }).Single(e => e.Command == "night:soften").Checked);
        Assert.False(All(Presenting with { Night = NightMode.Soften }).Single(e => e.Command == "night:off").Checked);
        Assert.True(All(Presenting with { InsetShape = InsetShape.Circle }).Single(e => e.Command == "inset-shape:circle").Checked);
        Assert.Equal("Take the timer off", All(Presenting with { TimerOn = true }).Single(e => e.Command == "timer").Text);
        Assert.DoesNotContain(All(Presenting with { TimerOn = true }), e => e.Command == "timer:5");
        Assert.True(All(Idle with { SpeakerScreen = true }).Single(e => e.Command == "speaker-screen").Checked);
        Assert.Equal("Put the whiteboard away", All(Idle with { BoardOut = true }).Single(e => e.Command == "show-whiteboard").Text);
        Assert.True(All(Idle with { Rehearsing = true, ClickerKeys = true }).Where(e => e.Command is "rehearse" or "clicker-keys").All(e => e.Checked));
    }

    [Fact]
    public void A_pdf_shown_offers_its_pages()
    {
        List<MenuEntry> middle = All(Presenting with { Page = 2, Pages = 5 });
        Assert.Equal("Next page (3 of 5)", middle.Single(e => e.Command == "next-slide").Text);
        Assert.True(middle.Single(e => e.Command == "previous-slide").Enabled);
        List<MenuEntry> last = All(Presenting with { Page = 5, Pages = 5 });
        Assert.False(last.Single(e => e.Command == "next-slide").Enabled);
        Assert.DoesNotContain(All(Presenting), e => e.Command == "next-slide"); // not a PDF: no pages
    }

    [Fact]
    public void The_idle_menu_stays_short_and_plain()
    {
        Assert.InRange(TrayMenuPlan.Build(Idle).Count(e => e.Kind != MenuKind.Separator), 1, 10);
        foreach (MenuEntry e in All(Presenting).Concat(All(Idle)))
            Assert.DoesNotMatch(@"(?i)\b(dim|pointer|outline|badge|capture)\b", e.Text);
    }

    [Fact]
    public void Commands_with_values_are_checked()
    {
        Assert.True(TrayMenuPlan.IsKnownCommand("timer:5"));
        Assert.False(TrayMenuPlan.IsKnownCommand("timer:0"));
        Assert.False(TrayMenuPlan.IsKnownCommand("timer:999"));
        Assert.False(TrayMenuPlan.IsKnownCommand("timer:soon"));
        Assert.True(TrayMenuPlan.IsKnownCommand("night:soften"));
        Assert.False(TrayMenuPlan.IsKnownCommand("night:purple"));
        Assert.True(TrayMenuPlan.IsKnownCommand("inset-shape:rounded"));
        foreach (string c in new[] { "timer:5", "night:off", "inset-shape:circle" }) Assert.NotNull(Icons.ForCommand(c));
    }

    [Fact]
    public void Every_new_question_does_something()
    {
        foreach (string id in NewActions)
            Assert.Contains(HelpTopics.All, t => t.Command == id);
        Assert.Equal(HelpTopics.All.Count, HelpTopics.All.Select(t => t.Question).Distinct().Count());
        List<HelpTopic> found = PickerLayout.Filter(HelpTopics.All, "taskbar", t => t.Question, t => $"{t.Answer} how help {t.Keywords}");
        Assert.Contains(found, t => t.Command == "settings:privacy");
    }
}

/// <summary>FR-132: clicker keys taken and given back.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class SlideKeyCatcherTests
{
    [Fact]
    public void Page_keys_are_taken_only_while_on()
    {
        using var catcher = new SlideKeyCatcher();
        Assert.True(catcher.Start(arrows: false), "Page Down and Page Up taken");
        Assert.True(catcher.IsOn);
        using (var second = new SlideKeyCatcher())
            Assert.False(second.Start(arrows: false), "taken already: a second can't have them");
        catcher.Stop();
        Assert.False(catcher.IsOn);
        using var again = new SlideKeyCatcher();
        Assert.True(again.Start(arrows: true), "given back");
        again.Stop();
    }
}
