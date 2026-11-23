using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.Tests;

public class RecordingTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Paused_time_is_left_out_of_the_recording()
    {
        var clock = new RecordingClock(S(100));
        Assert.Equal(S(5), clock.Elapsed(S(105)));
        clock.Pause(S(105));
        Assert.Equal(S(5), clock.Elapsed(S(130))); // frozen while paused
        clock.Pause(S(131)); // a second pause changes nothing
        clock.Resume(S(140));
        Assert.Equal(S(7), clock.Elapsed(S(142)));
        clock.Resume(S(150)); // resuming twice changes nothing
        Assert.Equal(S(15), clock.Elapsed(S(150)));
    }

    [Fact]
    public void Time_the_pc_slept_is_left_out_like_a_pause()
    {
        var clock = new RecordingClock(S(0));
        Assert.Equal(S(60), clock.Elapsed(S(60)));
        // Next loop pass happens 2 hours later (lid closed): the recorder pauses from the last pass.
        clock.Pause(S(60));
        clock.Resume(S(60 + 7200));
        Assert.Equal(S(61), clock.Elapsed(S(7261)));
    }

    [Fact]
    public void Time_never_runs_backwards() => Assert.Equal(TimeSpan.Zero, new RecordingClock(S(10)).Elapsed(S(9)));

    [Fact]
    public void New_pictures_go_in_at_most_thirty_times_a_second()
    {
        var pacer = new FramePacer();
        Assert.False(pacer.ShouldWrite(S(0), havePicture: false)); // nothing captured yet
        pacer.FrameArrived();
        Assert.True(pacer.ShouldWrite(S(0), true));
        pacer.FrameArrived();
        Assert.False(pacer.ShouldWrite(S(0.010), true)); // too soon: held back...
        Assert.True(pacer.ShouldWrite(S(0.034), true)); // ...and written at the next slot
        Assert.False(pacer.ShouldWrite(S(0.070), true)); // nothing new
    }

    [Fact]
    public void A_still_picture_is_repeated_every_second()
    {
        var pacer = new FramePacer();
        pacer.FrameArrived();
        Assert.True(pacer.ShouldWrite(S(0), true));
        Assert.False(pacer.ShouldWrite(S(0.9), true));
        Assert.Equal(S(1), pacer.NextDue(S(0.9)));
        Assert.True(pacer.ShouldWrite(S(1), true));
    }

    [Fact]
    public void Disk_guard_warns_once_and_stops_before_the_disk_is_full()
    {
        var guard = new DiskGuard();
        Assert.Equal(DiskState.Fine, guard.Check(10L << 30));
        Assert.Equal(DiskState.Fine, guard.Check(null)); // unknown: carry on
        Assert.Equal(DiskState.Low, guard.Check(DiskGuard.WarnBelow - 1));
        Assert.Equal(DiskState.Fine, guard.Check(DiskGuard.WarnBelow - 1)); // warned already
        Assert.Equal(DiskState.Critical, guard.Check(DiskGuard.StopBelow - 1));
    }
}

public class RecordingProtocolTests
{
    private static readonly IReadOnlyList<DisplayMonitor> Screens = [TestData.Laptop, TestData.Tv];

    [Theory]
    [InlineData(@"C:\Users\José Müller\Videos\Screen Recordings\Screen Recording 2026-11-23 134501.mp4")]
    [InlineData(@"\\server\share\rec (2).mp4")]
    [InlineData(@"D:\録画\会議.mp4")]
    public void Start_round_trips_any_path(string path)
    {
        var request = new RecordingRequest(TestData.Window(4242, "Webinar"), true, MeetingSound.App, true, path);
        RecordingRequest back = RecordingProtocol.ParseStart(RecordingProtocol.Start(request), Screens)!;
        Assert.Equal(path, back.Path);
        Assert.Equal(4242, back.Source.Handle);
        Assert.True(back.Cursor);
        Assert.True(back.Microphone);
        Assert.Equal(MeetingSound.App, back.Sound);
    }

    [Fact]
    public void Start_carries_screen_and_crop()
    {
        var source = SourceWindow.ForScreen(TestData.Tv) with { Crop = new CropRegion(0.25, 0, 0.5, 1) };
        RecordingRequest back = RecordingProtocol.ParseStart(RecordingProtocol.Start(new RecordingRequest(source, false, MeetingSound.None, false, @"C:\a.mp4")), Screens)!;
        Assert.Equal(TestData.Tv, back.Source.Screen);
        Assert.Equal(0.25, back.Source.Crop!.X, 10);
        Assert.Equal(MeetingSound.None, back.Sound);
    }

    [Fact]
    public void A_byte_order_mark_and_line_ending_are_tolerated()
    {
        string line = "\uFEFF" + RecordingProtocol.Start(new RecordingRequest(TestData.Window(7, "x"), false, MeetingSound.All, false, @"C:\a.mp4")) + "\r";
        Assert.Equal(@"C:\a.mp4", RecordingProtocol.ParseStart(line, Screens)!.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("record window:7 1 none app 1")] // no path
    [InlineData("record window:7 1 none app 1    ")] // blank path
    [InlineData("record window:0 1 none app 1 C:\\a.mp4")] // no window
    [InlineData("record window:7 1 none loud 1 C:\\a.mp4")] // unknown sound
    [InlineData("record window:7 1 none 7 1 C:\\a.mp4")] // number instead of a name
    [InlineData("record screen:\\\\.\\DISPLAY9 1 none app 1 C:\\a.mp4")] // unplugged screen
    [InlineData("start window:7 1 none app 1 C:\\a.mp4")]
    public void Bad_start_lines_are_refused(string? line) => Assert.Null(RecordingProtocol.ParseStart(line, Screens));

    [Fact]
    public void Replies_round_trip()
    {
        Assert.Equal(("started", 1920L, 1080L), Kind(RecordingProtocol.Started(1920, 1080)));
        RecordingReply progress = RecordingReply.Parse(RecordingProtocol.Progress(TimeSpan.FromSeconds(65.7), 123456789, true))!;
        Assert.Equal((65L, 123456789L, true), (progress.A, progress.B, progress.Paused));

        RecordingReply warning = RecordingReply.Parse(RecordingProtocol.Warning("disk", "Nearly\nfull"))!;
        Assert.Equal(("disk", "Nearly full"), (warning.Code, warning.Text));

        RecordingReply finished = RecordingReply.Parse(RecordingProtocol.Finished(RecordingEnd.SourceClosed, TimeSpan.FromHours(2), 5_000_000_000, "The recorded window closed."))!;
        Assert.Equal((RecordingEnd.SourceClosed, 7200L, 5_000_000_000L, "The recorded window closed."), (finished.End, finished.A, finished.B, finished.Text));

        RecordingReply failed = RecordingReply.Parse(RecordingProtocol.Failed("No GPU"))!;
        Assert.Equal(("failed", "No GPU"), (failed.Kind, failed.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("finished Exploded 1 2")]
    [InlineData("finished 99 1 2")]
    public void Unknown_replies_are_ignored(string line) => Assert.Null(RecordingReply.Parse(line));

    [Fact]
    public void Broken_numbers_become_zero() => Assert.Equal(0, RecordingReply.Parse("progress x 1e99 1")!.A);

    private static (string, long, long) Kind(string line)
    {
        RecordingReply r = RecordingReply.Parse(line)!;
        return (r.Kind, r.A, r.B);
    }
}

public sealed class RecordingControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "scp-rec-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRecordingProcess _process = new();

    public RecordingControllerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FilePath => Path.Combine(_folder, "Screen Recording.mp4");

    private RecordingRequest Request => new(TestData.Window(77, "Webinar"), true, MeetingSound.App, false, FilePath);

    private RecordingController NewController(long? free = 50L << 30) => new(() => _process, _ => free, useCurrentContext: false); // replies handled inline: no races with the test thread

    [Fact]
    public void A_full_recording_reports_progress_pause_and_the_saved_file()
    {
        var controller = NewController();
        RecordingResult? result = null;
        controller.Finished += (_, r) => result = r;

        Assert.Null(controller.Start(Request, "Webinar", followsOutput: false));
        Assert.Equal(RecordingStatus.Starting, controller.Status);
        Assert.StartsWith("record window:77 1 none app 0 ", _process.Sent[0]);

        _process.Reply("started 1280 720");
        Assert.Equal(RecordingStatus.Recording, controller.Status);
        _process.Reply("progress 12 3000000 0");
        Assert.Equal(TimeSpan.FromSeconds(12), controller.Elapsed);

        controller.TogglePause();
        Assert.Equal("pause", _process.Sent[^1]);
        Assert.Equal(RecordingStatus.Paused, controller.Status);
        controller.TogglePause();
        Assert.Equal("resume", _process.Sent[^1]);

        controller.Stop();
        Assert.Equal("stop", _process.Sent[^1]);
        Assert.Equal(RecordingStatus.Stopping, controller.Status);
        controller.Stop(); // twice is harmless
        Assert.Single(_process.Sent, "stop");

        File.WriteAllBytes(FilePath, new byte[1000]);
        _process.Reply("finished Stopped 61 1000 ");
        _process.Exit();

        Assert.Equal(RecordingStatus.Off, controller.Status);
        Assert.NotNull(result);
        Assert.True(result.Saved);
        Assert.Equal(RecordingEnd.Stopped, result.End);
        Assert.Equal(TimeSpan.FromSeconds(61), result.Length);
        Assert.True(_process.Disposed);
    }

    [Fact]
    public void Window_closing_is_reported_as_such()
    {
        var controller = NewController();
        RecordingResult? result = null;
        controller.Finished += (_, r) => result = r;
        controller.Start(Request, "Webinar", false);
        _process.Reply("started 1280 720");
        File.WriteAllBytes(FilePath, new byte[10]);
        _process.Reply("finished SourceClosed 3600 10 The recorded window closed.");
        _process.Exit();
        Assert.Equal(RecordingEnd.SourceClosed, result!.End);
        Assert.Equal("The recorded window closed.", result.Detail);
    }

    [Fact]
    public void A_crashed_helper_still_keeps_what_was_recorded()
    {
        var controller = NewController();
        RecordingResult? result = null;
        controller.Finished += (_, r) => result = r;
        controller.Start(Request, "Webinar", false);
        _process.Reply("started 1280 720");
        _process.Reply("progress 300 5000 0");
        File.WriteAllBytes(FilePath, new byte[5000]); // fragmented MP4: playable up to the last fragment
        _process.Exit(); // no "finished"
        Assert.Equal(RecordingEnd.Error, result!.End);
        Assert.True(result.Saved);
        Assert.Contains("unexpectedly", result.Detail);
    }

    [Fact]
    public void A_recording_that_fails_to_start_says_why()
    {
        var controller = NewController();
        RecordingResult? result = null;
        controller.Finished += (_, r) => result = r;
        controller.Start(Request, "Webinar", false);
        _process.Reply("failed That window or screen is gone.");
        _process.Exit();
        Assert.False(result!.Saved);
        Assert.Equal("That window or screen is gone.", result.Detail);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void An_accidental_one_second_recording_is_not_kept()
    {
        var controller = NewController();
        RecordingResult? result = null;
        controller.Finished += (_, r) => result = r;
        controller.Start(Request, "Webinar", false);
        _process.Reply("started 1280 720");
        controller.Stop();
        File.WriteAllBytes(FilePath, new byte[100]);
        _process.Reply("finished Stopped 0 100 ");
        _process.Exit();
        Assert.False(result!.Saved);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_nearly_full_disk_refuses_to_start_and_a_low_one_warns()
    {
        Assert.Contains("free disk space", NewController(free: 100L << 20).Start(Request, "Webinar", false));
        Assert.Empty(_process.Sent);

        var controller = NewController(free: 1L << 30);
        string? warning = null;
        controller.Warning += (_, w) => warning = w;
        Assert.Null(controller.Start(Request, "Webinar", false));
        Assert.Contains("disk space", warning);
    }

    [Fact]
    public void Only_one_recording_at_a_time()
    {
        var controller = NewController();
        controller.Start(Request, "Webinar", false);
        Assert.Equal("A recording is already running.", controller.Start(Request, "Webinar", false));
    }

    [Fact]
    public void A_folder_that_cant_be_created_is_explained()
    {
        string blocker = Path.Combine(_folder, "file");
        File.WriteAllText(blocker, "x"); // a file where the folder should be
        var request = Request with { Path = Path.Combine(blocker, "sub", "a.mp4") };
        Assert.NotNull(NewController().Start(request, "Webinar", false));
        Assert.Empty(_process.Sent);
    }

    [Fact]
    public void Pause_before_the_recorder_is_ready_is_ignored()
    {
        var controller = NewController();
        controller.Start(Request, "Webinar", false);
        controller.TogglePause();
        Assert.Equal(RecordingStatus.Starting, controller.Status);
        Assert.Single(_process.Sent); // only the start line
    }

    [Fact]
    public async Task Completion_finishes_when_the_helper_exits()
    {
        var controller = NewController();
        Assert.True(controller.Completion.IsCompleted); // nothing running
        controller.Start(Request, "Webinar", false);
        Task done = controller.Completion;
        Assert.False(done.IsCompleted);
        _process.Exit();
        await done.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Disposing_hands_the_file_over_to_the_helper()
    {
        var controller = NewController();
        controller.Start(Request, "Webinar", false);
        _process.Reply("started 1280 720");
        controller.Dispose();
        Assert.Equal("stop", _process.Sent[^1]);
        Assert.True(_process.Disposed); // closing its input also tells it to finish
    }
}

internal sealed class FakeRecordingProcess : IRecordingProcess
{
    public List<string> Sent { get; } = [];

    public bool Disposed { get; private set; }

    public bool Killed { get; private set; }

    public event Action<string?>? LineReceived;

    public void Send(string line) => Sent.Add(line);

    public void Reply(string line) => LineReceived?.Invoke(line);

    public void Exit() => LineReceived?.Invoke(null);

    public void Kill()
    {
        Killed = true;
        Exit();
    }

    public void Dispose() => Disposed = true;
}

public class RecordingSettingsTests
{
    [Theory]
    [InlineData("App", "App")]
    [InlineData("all", "All")]
    [InlineData("NONE", "None")]
    [InlineData("loud", "App")]
    [InlineData("", "App")]
    [InlineData("42", "App")]
    public void Sound_choice_is_repaired(string stored, string expected) =>
        Assert.Equal(expected, new AppSettings { RecordSound = stored }.Normalize().RecordSound);
}

public class RecordingTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(36000, "10:00:00")]
    public void Clock_text(int seconds, string expected) => Assert.Equal(expected, ScpApp.RecordingClockText(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(30, "30 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1½ hours")]
    [InlineData(120, "2 hours")]
    [InlineData(240, "4 hours")]
    public void Duration_words(int minutes, string expected) => Assert.Equal(expected, ScpApp.DurationWords(TimeSpan.FromMinutes(minutes)));
}
