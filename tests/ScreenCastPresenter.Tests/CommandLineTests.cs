using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-43: ScreenCastPresenter.exe --present "PowerPoint" --output 2, and the pipe to the running copy.</summary>
public class CommandLineTests
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
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    [Fact]
    public void The_example_from_the_requirements_works()
    {
        CliRequest r = Ok("--present", "PowerPoint", "--output", "2");
        Assert.Equal(new CliShow(CliShowKind.Window, "PowerPoint"), r.Show);
        Assert.Equal(new CliOutput(2, false, false), r.Output);
        Assert.Empty(r.Actions);
    }

    [Theory]
    [InlineData("--screen", "1", nameof(CliShowKind.Screen))]
    [InlineData("--favourite", "9", nameof(CliShowKind.Favourite))]
    [InlineData("--favorite", "3", nameof(CliShowKind.Favourite))]
    [InlineData("--camera", "Document camera", nameof(CliShowKind.Camera))]
    public void Each_kind_of_thing_to_show(string option, string value, string kind) =>
        Assert.Equal(new CliShow(Enum.Parse<CliShowKind>(kind), value), Ok(option, value).Show);

    [Fact]
    public void Again_needs_no_value() => Assert.Equal(CliShowKind.Again, Ok("--again").Show!.Kind);

    [Fact]
    public void Values_can_be_joined_with_an_equals_sign_and_options_are_any_case()
    {
        CliRequest r = Ok("--PRESENT=Excel", "--Output=window");
        Assert.Equal("Excel", r.Show!.Value);
        Assert.True(r.Output!.Window);
        Assert.True(Ok("--output", "ZOOM").Output!.Zoom);
    }

    [Fact]
    public void Actions_keep_their_order_and_are_exact_states()
    {
        CliRequest r = Ok("--favourite", "1", "--blank", "--action", "dim", "--unblank", "--freeze", "--unfreeze", "--start-recording", "--stop-recording", "--screenshot", "--choose", "--stop");
        Assert.Equal(["cli:blank-on", "dim", "cli:blank-off", "cli:freeze-on", "cli:freeze-off", "cli:record-on", "stop-recording", "screenshot", "picker", "stop"], r.Actions);
    }

    [Fact]
    public void Every_action_name_from_Settings_is_accepted()
    {
        foreach (ShortcutAction action in ShortcutCatalog.All)
            Assert.Equal([action.Id], Ok("--action", action.Id).Actions);
        Assert.Contains("isn't an action", Bad("--action", "launch-rockets"));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_is_recognised(string option) => Assert.True(Ok(option).Help);

    [Fact]
    public void Mistakes_say_what_was_wrong()
    {
        Assert.Contains("needs a value", Bad("--present"));
        Assert.Contains("needs a value", Bad("--present", "--output", "2")); // the next option isn't a value
        Assert.Contains("screen number", Bad("--screen", "two"));
        Assert.Contains("screen number", Bad("--screen", "0"));
        Assert.Contains("1 to 9", Bad("--favourite", "10"));
        Assert.Contains("1 to 9", Bad("--favourite", "-1"));
        Assert.Contains("only one", Bad("--present", "Word", "--screen", "1"));
        Assert.Contains("“window” or “zoom”", Bad("--output", "tv"));
        Assert.Contains("once", Bad("--output", "1", "--output", "2"));
        Assert.Contains("isn't an option", Bad("--launch"));
        Assert.Contains("in quotes", Bad("--present", "My", "slides"));
        Assert.Contains("doesn't take a value", Bad("--blank=yes"));
        Assert.Contains("Nothing to do", Bad("--startup"));
    }

    [Fact]
    public void The_exes_own_switches_are_not_requests()
    {
        Assert.False(CommandLine.IsRequest([]));
        Assert.False(CommandLine.IsRequest(["--startup"]));
        Assert.False(CommandLine.IsRequest(["--uninstall"]));
        Assert.False(CommandLine.IsRequest(["--capture-host"]));
        Assert.False(CommandLine.IsRequest(["--still"]));
        Assert.False(CommandLine.IsRequest(["--record"]));
        Assert.False(CommandLine.IsRequest([UpdateCheck.Switch]));
        Assert.True(CommandLine.IsRequest(["--blank"]));
        Assert.True(CommandLine.IsRequest(["/?"]));
        // Started with Windows and asked to do something too: the request counts.
        Assert.True(CommandLine.IsRequest(["--startup", "--favourite", "1"]));
    }

    [Fact]
    public void Usage_mentions_every_option()
    {
        foreach (string option in new[] { "--present", "--screen", "--camera", "--favourite", "--again", "--output", "--blank", "--unblank", "--freeze", "--unfreeze", "--stop", "--start-recording", "--stop-recording", "--screenshot", "--choose", "--action", "--help" })
            Assert.Contains(option, CommandLine.Usage);
        Assert.DoesNotContain("SCP", CommandLine.Usage);
    }

    // ───────────────────────────── finding the window ─────────────────────────────

    private static readonly List<SourceWindow> Open =
    [
        TestData.Window(1, "Inbox - Outlook", "olk"),
        TestData.Window(2, "Notes about PowerPoint.txt - Notepad", "notepad"),
        TestData.Window(3, "Deck.pptx - PowerPoint", "POWERPNT"),
        TestData.Window(4, "Résumé.docx - Word", "WINWORD"),
    ];

    [Fact]
    public void An_exact_app_name_beats_a_title_that_mentions_it()
    {
        Assert.Equal(3, CommandLine.FindWindow(Open, "powerpnt")!.Handle);
        Assert.Equal(2, CommandLine.FindWindow(Open, "notepad")!.Handle);
    }

    [Fact]
    public void Titles_match_like_the_search_box_ignoring_case_and_accents()
    {
        Assert.Equal(4, CommandLine.FindWindow(Open, "resume")!.Handle);
        Assert.Equal(3, CommandLine.FindWindow(Open, "deck")!.Handle);
        // "PowerPoint" is in two titles; one starting with it wins over one merely containing it.
        Assert.NotNull(CommandLine.FindWindow(Open, "PowerPoint"));
    }

    [Fact]
    public void Nothing_matches_or_nothing_asked()
    {
        Assert.Null(CommandLine.FindWindow(Open, "Photoshop"));
        Assert.Null(CommandLine.FindWindow(Open, "   "));
        Assert.Null(CommandLine.FindWindow([], "Word"));
    }

    // ───────────────────────────── the pipe ─────────────────────────────

    [Theory]
    [InlineData("--present", "Deck \"final\" ✓ 日本語")]
    [InlineData("--present", "")]
    public void Arguments_survive_the_trip_exactly(string a, string b)
    {
        string line = CommandChannel.EncodeArgs([a, b]);
        Assert.DoesNotContain('\n', line);
        Assert.Equal([a, b], CommandChannel.DecodeArgs(line)!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void A_garbled_line_is_rejected(string line) => Assert.Null(CommandChannel.DecodeArgs(line));

    [Fact]
    public void Replies_keep_to_one_line()
    {
        CliReply reply = CommandChannel.DecodeReply(CommandChannel.EncodeReply(new CliReply(false, "line one\nline\ttwo")));
        Assert.False(reply.Ok);
        Assert.Equal("line one line two", reply.Message);
        Assert.True(CommandChannel.DecodeReply("ok\tShowing on TV").Ok);
        Assert.False(CommandChannel.DecodeReply(null).Ok);
        Assert.False(CommandChannel.DecodeReply("garbage").Ok);
    }

    /// <summary>A real named pipe on this PC, with its own name so a running copy is never reached.</summary>
    [Fact]
    public async Task A_launch_reaches_the_running_copy_and_gets_its_answer()
    {
        string name = "ScreenCastPresenter.Tests." + Guid.NewGuid().ToString("N");
        var received = new List<string[]>();
        using var server = new CommandServer(args =>
        {
            received.Add(args);
            return Task.FromResult(new CliReply(args.Contains("--fail") ? false : true, "done: " + string.Join(' ', args)));
        }, name);
        Task running = server.RunAsync();

        CliReply? first = await CommandChannel.SendAsync(["--favourite", "2"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name);
        CliReply? second = await CommandChannel.SendAsync(["--fail"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name);
        Assert.Equal(new CliReply(true, "done: --favourite 2"), first);
        Assert.Equal(new CliReply(false, "done: --fail"), second);
        Assert.Equal(2, received.Count);

        server.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(5)); // stops promptly
    }

    [Fact]
    public async Task Nobody_listening_means_start_the_app()
    {
        string name = "ScreenCastPresenter.Tests." + Guid.NewGuid().ToString("N");
        Assert.Null(await CommandChannel.SendAsync(["--blank"], TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), name));
    }

    [Fact]
    public async Task A_client_that_says_nothing_doesnt_block_the_next_one()
    {
        string name = "ScreenCastPresenter.Tests." + Guid.NewGuid().ToString("N");
        using var server = new CommandServer(args => Task.FromResult(new CliReply(true, "ok")), name);
        Task running = server.RunAsync();
        using (var silent = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
        {
            await silent.ConnectAsync(2000);
            // Connected, says nothing, then goes away.
        }
        CliReply? reply = await CommandChannel.SendAsync(["--blank"], TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(5), name);
        Assert.True(reply?.Ok);
        server.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
