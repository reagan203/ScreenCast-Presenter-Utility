using System.Text.RegularExpressions;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// The "easier for non-technical people" pass: a short tray menu that only lists what applies,
/// messages that can be read in time, no jargon, help that does what it says, and text sizes.
/// </summary>
public class EasierToUseTests
{
    /// <summary>Words that mean nothing to someone who isn't technical (or are SCP's internal names).</summary>
    private static readonly Regex Jargon = new(@"\b(output|source|DWM|WGC|Graphics Capture|engine|palette|picker|SCP|Display \d)\b", RegexOptions.IgnoreCase);

    private static readonly TrayScreen Laptop = new(@"\\.\DISPLAY1", "This screen (Screen 1)", Chosen: false);
    private static readonly TrayScreen Tv = new(@"\\.\DISPLAY2", "TV (Screen 2)", Chosen: true);

    private static TrayMenuState Idle => new() { Session = SessionState.Off, Status = "○ Not showing anything", Screens = [Laptop, Tv] };

    private static TrayMenuState Presenting => Idle with { Session = SessionState.Presenting, Status = "● Showing on TV · HDMI: Slides" };

    private static List<MenuEntry> All(TrayMenuState state) => [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(state))];

    private static MenuEntry? Find(TrayMenuState state, string command) => All(state).FirstOrDefault(e => e.Command == command);

    // ───────────────────────────── tray menu ─────────────────────────────

    [Fact]
    public void Idle_menu_is_short_and_leads_with_showing_something()
    {
        IReadOnlyList<MenuEntry> menu = TrayMenuPlan.Build(Idle);
        int lines = menu.Count(e => e.Kind != MenuKind.Separator);
        Assert.InRange(lines, 1, 10); // it was about 30
        Assert.Equal("picker", menu.First(e => e.Kind == MenuKind.Item).Command);
        foreach (string absent in new[] { "blank", "freeze", "stop", "crop", "remove-crop", "pause-recording", "stop-recording" })
            Assert.Null(Find(Idle, absent));
        Assert.DoesNotContain(menu, e => e.Text == "Favourites"); // none saved yet, nothing to save
    }

    [Fact]
    public void Settings_level_choices_are_not_in_the_menu()
    {
        foreach (TrayMenuState state in new[] { Idle, Presenting })
        {
            List<string> texts = [.. All(state).Select(e => e.Text)];
            foreach (string moved in new[] { "Appearance", "Dim", "Capture", "outline", "badge", "now-showing", "pointer", "Pause all shortcuts", "Keyboard shortcuts", "Recording sound" })
                Assert.DoesNotContain(texts, t => t.Contains(moved, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void After_stopping_the_last_thing_can_be_shown_again_in_one_click()
    {
        // Exactly the last thing shown, not "previous" (after a stop, that's the one before it).
        MenuEntry again = Find(Idle with { ShowAgainTitle = "Quarterly results.pptx" }, "show-again")!;
        Assert.Equal("Show “Quarterly results.pptx” again", again.Text);
        string longTitle = new('x', 200);
        Assert.True(Find(Idle with { ShowAgainTitle = longTitle }, "show-again")!.Text.Length < 60, "long titles are shortened");
        Assert.Null(Find(Idle with { ShowAgainTitle = "" }, "show-again"));
        Assert.Null(Find(Presenting with { ShowAgainTitle = "Slides" }, "show-again"));
    }

    [Fact]
    public void Presenting_menu_has_the_controls_for_the_audience_screen()
    {
        Assert.Equal("Blank the TV", Find(Presenting, "blank")!.Text);
        Assert.Equal("Show the TV again", Find(Presenting with { Blanked = true }, "blank")!.Text);
        Assert.Equal("Go live again", Find(Presenting with { Frozen = true }, "freeze")!.Text);
        Assert.NotNull(Find(Presenting, "crop"));
        Assert.Null(Find(Presenting, "remove-crop"));
        Assert.NotNull(Find(Presenting with { Cropped = true }, "remove-crop"));
        Assert.Null(Find(Presenting with { Cropped = true }, "crop"));
        Assert.NotNull(Find(Presenting, "stop"));
        Assert.Equal("Show something else…", Find(Presenting, "picker")!.Text);
        Assert.Equal("Blank the Zoom screen", Find(Presenting with { Where = "the Zoom screen" }, "blank")!.Text);
        Assert.Equal("Record what the window shows", Find(Presenting with { Where = "the window" }, "record-output")!.Text);
    }

    [Fact]
    public void Blank_screen_without_a_window_offers_choosing_and_stopping_only()
    {
        var idleScreen = Idle with { Session = SessionState.IdleScreen };
        Assert.Equal("Choose what to show…", Find(idleScreen, "picker")!.Text);
        Assert.NotNull(Find(idleScreen, "stop"));
        Assert.Null(Find(idleScreen, "blank"));
        Assert.Null(Find(idleScreen, "freeze"));
    }

    [Fact]
    public void Recording_controls_appear_only_while_recording()
    {
        Assert.Null(Find(Idle, "stop-recording"));
        Assert.Equal("Record…", Find(Idle, "record-picker")!.Text);

        var recording = Idle with { Recording = RecordingStatus.Recording, RecordingLine = "● Recording 4:05: Webinar", RecordingElapsed = TimeSpan.FromMinutes(45), StopAfter = TimeSpan.FromMinutes(60) };
        List<MenuEntry> all = All(recording);
        Assert.Contains(all, e => e.Kind == MenuKind.Label && e.Text == "● Recording 4:05: Webinar");
        Assert.Equal("Pause recording", Find(recording, "pause-recording")!.Text);
        Assert.True(Find(recording, "stop-recording")!.Enabled);
        Assert.Null(Find(recording, "record-picker"));
        Assert.False(Find(recording, "stop-after:30")!.Enabled, "already past 30 minutes");
        Assert.True(Find(recording, "stop-after:60")!.Checked);
        Assert.False(Find(recording, "stop-after:never")!.Checked);

        Assert.Equal("Resume recording", Find(recording with { Recording = RecordingStatus.Paused }, "pause-recording")!.Text);
        Assert.False(Find(recording with { Recording = RecordingStatus.Stopping }, "stop-recording")!.Enabled);
        Assert.False(Find(recording with { Recording = RecordingStatus.Starting }, "pause-recording")!.Enabled);
    }

    [Fact]
    public void Favourites_appear_once_there_is_something_to_show_or_save()
    {
        Assert.Null(Find(Idle, "favourite-1"));
        var saved = Idle with { Favourites = [new TrayFavourite(3, "Budget", "EXCEL")] };
        MenuEntry three = Find(saved, "favourite-3")!;
        Assert.Contains("Budget", three.Text);
        Assert.Null(Find(saved, "save-favourite-1")); // nothing is showing, so nothing to save
        Assert.Null(Find(saved, "favourite-1")); // empty slots aren't listed

        var presentingWithOne = Presenting with { Favourites = [new TrayFavourite(3, "Budget", "EXCEL")] };
        Assert.Equal("Favourite 1", Find(presentingWithOne, "save-favourite-1")!.Text);
        Assert.Contains("replaces “Budget”", Find(presentingWithOne, "save-favourite-3")!.Text);
        Assert.NotNull(Find(Presenting, "save-favourite-9")); // presenting with none saved yet: can save
    }

    [Fact]
    public void Show_on_marks_the_chosen_screen_or_the_window()
    {
        Assert.True(Find(Idle, @"output:\\.\DISPLAY2")!.Checked);
        Assert.False(Find(Idle, @"output:\\.\DISPLAY1")!.Checked);
        Assert.False(Find(Idle, "output-window")!.Checked);
        var window = Idle with { WindowChosen = true };
        Assert.True(Find(window, "output-window")!.Checked);
        Assert.False(Find(window, @"output:\\.\DISPLAY2")!.Checked, "only one choice is ticked");
        Assert.NotNull(Find(Idle with { Screens = [] }, "output-window")); // no screens found: the window still works
    }

    public static TheoryData<string> States() => ["idle", "presenting", "blanked", "frozen", "cropped", "recording", "paused", "idle-screen", "favourites"];

    private static TrayMenuState State(string name) => name switch
    {
        "presenting" => Presenting,
        "blanked" => Presenting with { Blanked = true },
        "frozen" => Presenting with { Frozen = true },
        "cropped" => Presenting with { Cropped = true },
        "recording" => Presenting with { Recording = RecordingStatus.Recording, RecordingLine = "● Recording 0:01: TV" },
        "paused" => Idle with { Recording = RecordingStatus.Paused, RecordingLine = "❚❚ Recording paused at 0:01" },
        "idle-screen" => Idle with { Session = SessionState.IdleScreen },
        "favourites" => Presenting with { Favourites = [new TrayFavourite(1, "Slides", "POWERPNT")] },
        _ => Idle,
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Every_menu_line_does_something_real_and_is_in_plain_words(string name)
    {
        foreach (MenuEntry e in All(State(name)))
        {
            Assert.DoesNotMatch(Jargon, e.Text);
            if (e.Kind != MenuKind.Item || e.Children is not null) continue;
            Assert.NotNull(e.Command);
            Assert.True(TrayMenuPlan.IsKnownCommand(e.Command!), $"“{e.Text}” runs unknown command {e.Command}");
            if (e.Shortcut is { } shortcut) Assert.NotNull(ShortcutCatalog.Find(shortcut));
        }
    }

    [Fact]
    public void Unknown_commands_are_recognised_as_unknown()
    {
        Assert.False(TrayMenuPlan.IsKnownCommand("launch-rockets"));
        Assert.False(TrayMenuPlan.IsKnownCommand("output:"));
        Assert.False(TrayMenuPlan.IsKnownCommand("stop-after:soon"));
        Assert.True(TrayMenuPlan.IsKnownCommand("stop-after:90"));
    }

    // ───────────────────────────── wording ─────────────────────────────

    [Fact]
    public void Action_names_are_plain_words()
    {
        foreach (ShortcutAction action in ShortcutCatalog.All)
            Assert.DoesNotMatch(Jargon, action.Name);
    }

    [Fact]
    public void Screens_are_called_Screen_N_everywhere()
    {
        var plain = new Interop.DisplayMonitor(@"\\.\DISPLAY3", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), false, 1);
        Assert.Equal("Screen 3", plain.Name);
        Assert.Equal("Screen 3", plain.ShortName);
        Assert.Equal("Screen 3 · 1920 × 1080", plain.Label); // not "Screen 3 · Screen 3"
        Assert.DoesNotContain("Display", plain.Label);
    }

    /// <summary>
    /// "SCP" is the code's nickname; people see "ScreenCast Presenter". A guard against it creeping
    /// back into text: any string literal in the app that says SCP (other than SCP_ environment
    /// variables) fails, with the file and line.
    /// </summary>
    [Fact]
    public void No_text_in_the_app_calls_it_SCP()
    {
        string src = Path.Combine(RepoRoot(), "src", "ScreenCastPresenter");
        var literal = new Regex("\"[^\"\\n]*\\bSCP\\b(?!_)[^\"\\n]*\"");
        var problems = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue; // comments may say SCP
                string code = line.Split(" // ")[0];
                if (InternalName.IsMatch(code)) continue; // thread, hidden-window and audio-stream names nobody sees
                if (literal.IsMatch(code)) problems.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
            }
        }
        Assert.Empty(problems);
    }

    private static readonly Regex InternalName = new(@"(\bName|\bCaption) = ""SCP |\bsink, ""SCP ");

    internal static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ScreenCastPresenter.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    // ───────────────────────────── messages ─────────────────────────────

    [Fact]
    public void Short_messages_stay_at_least_five_seconds()
    {
        Assert.Equal(MessageTiming.Minimum, MessageTiming.For("Saved."));
        Assert.Equal(MessageTiming.Minimum, MessageTiming.For(""));
        Assert.Equal(MessageTiming.Minimum, MessageTiming.For(null));
        Assert.Equal(MessageTiming.Minimum, MessageTiming.For("   \t\n  "));
    }

    [Fact]
    public void Longer_messages_and_buttons_get_more_time_up_to_fifteen_seconds()
    {
        string twenty = string.Join(' ', Enumerable.Repeat("word", 20));
        TimeSpan reading = MessageTiming.For(twenty);
        Assert.True(reading > MessageTiming.Minimum, "20 words need more than 5 s");
        Assert.True(MessageTiming.For(twenty, buttons: 2) > reading, "deciding on buttons takes time");
        Assert.Equal(MessageTiming.Maximum, MessageTiming.For(string.Join(' ', Enumerable.Repeat("word", 500))));
        Assert.Equal(reading, MessageTiming.For(twenty, buttons: -3)); // nonsense button counts don't shorten it
    }

    // ───────────────────────────── text size ─────────────────────────────

    [Theory]
    [InlineData(1.0, 1.0f)]
    [InlineData(1.15, 1.15f)]
    [InlineData(1.3, 1.3f)]
    [InlineData(1.1, 1.15f)]
    [InlineData(1.24, 1.3f)]
    [InlineData(7.0, 1.3f)]
    [InlineData(0.5, 1.0f)]
    [InlineData(0.0, 1.0f)]
    [InlineData(-2.0, 1.0f)]
    [InlineData(double.NaN, 1.0f)]
    [InlineData(double.PositiveInfinity, 1.0f)]
    public void Text_size_snaps_to_one_of_three_steps(double value, float expected) =>
        Assert.Equal(expected, TextSize.Clamp(value), 3);

    [Fact]
    public void Text_size_is_saved_tidily_and_repaired()
    {
        Assert.Equal(1.15, new AppSettings { TextSize = TextSize.Large }.Normalize().TextSize); // not 1.149999976
        Assert.Equal(1.3, new AppSettings { TextSize = 99 }.Normalize().TextSize);
        Assert.Equal(1.0, new AppSettings { TextSize = double.NaN }.Normalize().TextSize);
        Assert.Equal(1.0, new AppSettings().TextSize); // older files without it: normal
    }

    // ───────────────────────────── help ─────────────────────────────

    [Fact]
    public void Help_topics_are_questions_in_plain_words_that_each_do_something()
    {
        Assert.NotEmpty(HelpTopics.All);
        Assert.Equal(HelpTopics.All.Count, HelpTopics.All.Select(t => t.Question).Distinct().Count());
        foreach (HelpTopic topic in HelpTopics.All)
        {
            Assert.EndsWith("?", topic.Question);
            Assert.DoesNotMatch(Jargon, topic.Question);
            Assert.DoesNotMatch(Jargon, topic.Answer);
            Assert.False(string.IsNullOrWhiteSpace(topic.Answer));
            Assert.True(TrayMenuPlan.IsKnownCommand(topic.Command), $"{topic.Question} runs unknown command {topic.Command}");
        }
    }

    [Theory]
    [InlineData("zoom", "How do I share in Zoom or Teams?")]
    [InlineData("teams", "How do I share in Zoom or Teams?")]
    [InlineData("webinar", "How do I record a webinar while I work?")]
    [InlineData("bigger text", "How do I make the text bigger?")]
    [InlineData("where are my videos", "Where are my recordings?")]
    [InlineData("projector", "How do I show a window on the TV?")]
    public void Searching_in_everyday_words_finds_the_right_help(string query, string expected)
    {
        List<HelpTopic> found = PickerLayout.Filter(HelpTopics.All, query, t => t.Question, t => $"{t.Answer} how help {t.Keywords}");
        Assert.Contains(found, t => t.Question == expected);
    }

    // ───────────────────────────── Zoom screen setup ─────────────────────────────

    [Theory]
    [InlineData(0, nameof(ZoomSetupOutcome.Installed))]
    [InlineData(ZoomScreenSetup.AlreadyInstalledCode, nameof(ZoomSetupOutcome.AlreadyInstalled))]
    [InlineData(1602, nameof(ZoomSetupOutcome.Cancelled))]
    [InlineData(1223, nameof(ZoomSetupOutcome.Cancelled))]
    [InlineData(unchecked((int)0x800704C7), nameof(ZoomSetupOutcome.Cancelled))]
    [InlineData(-1, nameof(ZoomSetupOutcome.Failed))]
    [InlineData(-2, nameof(ZoomSetupOutcome.Failed))]
    [InlineData(unchecked((int)0x8A150008), nameof(ZoomSetupOutcome.Failed))]
    public void Zoom_screen_setup_results_are_understood(int exitCode, string expected) =>
        Assert.Equal(expected, ZoomScreenSetup.Interpret(exitCode).ToString());

    [Fact]
    public void Zoom_screen_setup_runs_the_documented_command_without_questions()
    {
        string args = ZoomScreenSetup.Arguments;
        Assert.Contains("--id VirtualDrivers.Virtual-Display-Driver", args);
        Assert.Contains("--exact", args);
        Assert.Contains("--accept-package-agreements", args);
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.DoesNotContain("--force", args);
        System.Diagnostics.ProcessStartInfo start = ZoomScreenSetup.StartInfo(@"C:\winget.exe");
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void Without_winget_there_is_no_command_to_run()
    {
        Assert.Null(ZoomScreenSetup.FindWinget(_ => false));
        string? found = ZoomScreenSetup.FindWinget(path => path.EndsWith(@"WindowsApps\winget.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(found);
        Assert.StartsWith("https://github.com/VirtualDrivers/", ZoomScreenSetup.ProjectPage, StringComparison.Ordinal);
    }
}
