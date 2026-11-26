using System.Diagnostics;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// Speed budgets for the work done while presenting (NFR-04: nothing noticeable). Each budget is
/// several times what this code needs on an ordinary laptop, so a slow test machine doesn't fail
/// them, while a real slowdown (something accidentally quadratic, a lookup per frame) does.
/// The GPU's share is measured in GpuStageRenderTests; QR codes in QrCodeTests.
/// </summary>
[Collection(SpeedCollection.Name)]
public class StageSpeedTests
{
    /// <summary>Average milliseconds per run of <paramref name="work"/>, after a warm-up.</summary>
    private static double Each(int runs, Action work)
    {
        work();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++) work();
        return clock.Elapsed.TotalMilliseconds / runs;
    }

    private static void Within(double ms, double budget, string what) =>
        Assert.True(ms < budget, $"{what}: {ms:F4} ms each (budget {budget} ms)");

    [Fact]
    public void The_never_show_check_on_a_busy_desktop()
    {
        // 300 windows, front to back, none of them a never-show app: the worst case (every one looked at).
        var windows = Enumerable.Range(0, 300).Select(i => new VisibleWindow(i + 1, $"app{i}", new Rectangle(i % 20 * 90, i / 20 * 70, 800, 600))).ToList();
        List<string> list = [.. NeverShow.Defaults, .. Enumerable.Range(0, 30).Select(i => $"Private{i}")];
        Within(Each(2_000, () => NeverShow.FindOnScreen(windows, new Rectangle(0, 0, 1920, 1080), list)), 0.5, "never-show check");
    }

    [Fact]
    public void Hidden_parts_going_to_the_helper_and_being_read_there()
    {
        List<CropRegion> boxes = [.. Enumerable.Range(0, Redactions.MaxPerSource).Select(i => CropRegion.Create(i * 0.07, 0.1, 0.06, 0.3)!)];
        var engine = new NullEffects();
        Within(Each(10_000, () => CaptureHost.ApplyMark(engine, CaptureProtocol.Redact(boxes, RedactStyle.Blurred))), 0.1, "redact line out and in");
    }

    [Fact]
    public void The_helper_keeps_up_with_the_pointer_and_everything_else()
    {
        var engine = new NullEffects();
        string[] lines =
        [
            CaptureProtocol.Spotlight(new PointF(0.4f, 0.6f)),
            CaptureProtocol.InkSet(3, [new PointF(0.1f, 0.2f), new PointF(0.5f, 0.6f)]),
            CaptureProtocol.Caption("Jane Doe", "Head of Sales"),
            CaptureProtocol.Live("and that is why the numbers went up this quarter"),
            CaptureProtocol.Qr("https://example.com", InsetCorner.TopRight),
        ];
        Within(Each(50_000, () =>
        {
            foreach (string line in lines) CaptureHost.ApplyMark(engine, line);
        }), 0.05, "five stage lines through the helper's parser");
    }

    [Fact]
    public void A_big_drawing_is_sent_again_to_a_new_engine_at_once()
    {
        var drawing = new InkDrawing();
        var random = new Random(3);
        for (int s = 0; s < InkDrawing.MaxStrokes; s++)
        {
            InkStroke stroke = drawing.Begin((InkTool)(s % 5), Color.Red, 0.005f, "words");
            for (int p = 0; p < 100; p++) stroke.Add(new PointF((float)random.NextDouble(), (float)random.NextDouble()));
        }
        var engine = new NullEffects();
        Within(Each(20, () => drawing.Replay(engine)), 20, "replaying 500 marks of 100 points");
    }

    [Fact]
    public void Scheduled_actions_are_checked_cheaply()
    {
        List<ScheduleEntry> entries = [.. Enumerable.Range(0, ScheduleEntry.Max).Select(i => new ScheduleEntry { Time = $"{i % 24:00}:{i * 7 % 60:00}", Days = "Mon,Wed,Fri" })];
        DateTime now = new(2025, 3, 3, 12, 0, 0);
        Within(Each(5_000, () => Scheduler.Due(entries, now, now.AddSeconds(30))), 0.2, "a schedule check (every 30 seconds)");
        Within(Each(1_000, () => Scheduler.Next(entries, now)), 0.5, "finding the next scheduled time");
    }

    [Fact]
    public void Live_captions_keep_up_with_speech()
    {
        var live = new LiveCaptions();
        DateTime t = DateTime.UtcNow;
        int i = 0;
        Within(Each(20_000, () => live.Heard(i++ % 7 == 0 ? $"final Sentence number {i} goes here." : $"partial words so far {i}", t)), 0.02, "a caption line");
    }

    [Fact]
    public void The_marks_layer_takes_updates_quickly_on_the_command_thread()
    {
        int changes = 0;
        var layer = new EffectsLayer(() => changes++);
        int n = 0;
        Within(Each(20_000, () =>
        {
            n++;
            layer.Spotlight(new PointF(n % 100 / 100f, 0.5f));
            layer.Laser(new PointF(0.5f, n % 100 / 100f));
            _ = layer.IsAnimating;
        }), 0.05, "pointer-rate updates");
        Assert.True(changes > 0);
    }

    [Fact]
    public void Settings_with_rooms_schedules_and_hidden_parts_save_and_load_quickly()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-speed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            AppSettings s = store.Current;
            for (int i = 0; i < RoomProfile.Max; i++) s.Rooms.Add(RoomProfile.From($"Room {i}", s, withFavourites: true));
            for (int i = 0; i < ScheduleEntry.Max; i++) s.Schedule.Add(new ScheduleEntry { Time = $"{i:00}:30" });
            for (int i = 0; i < 60; i++) s.HiddenParts[$"app{i}"] = [.. Enumerable.Repeat(CropRegion.Create(0.1, 0.1, 0.2, 0.2)!, Redactions.MaxPerSource)];
            Within(Each(10, () => store.Save()), 60, "saving full settings");
            Within(Each(10, () => new SettingsStore(store.FilePath).Load()), 60, "loading full settings");
            Assert.True(new FileInfo(store.FilePath).Length < SettingsStore.MaxFileBytes / 4, "well under the file size limit");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_tray_menu_with_every_stage_tool_builds_instantly()
    {
        var state = new TrayMenuState
        {
            Session = SessionState.Presenting,
            Status = "● Showing on TV: Slides",
            Rooms = [.. Enumerable.Range(0, RoomProfile.Max).Select(i => $"Room {i}")],
            Favourites = [.. Enumerable.Range(1, 9).Select(i => new TrayFavourite(i, $"Deck {i}", "POWERPNT"))],
            SpotlightOn = true,
            HasQr = true,
            NextTitle = "Budget.xlsx",
            SecondTvPossible = true,
        };
        Within(Each(2_000, () => TrayMenuPlan.Build(state)), 0.5, "building the tray menu");
    }

    [Fact]
    public async Task A_command_line_round_trip_is_quick()
    {
        string name = "ScreenCastPresenter.Tests.Speed." + Guid.NewGuid().ToString("N");
        using var server = new CommandServer(args => Task.FromResult(new CliReply(true, "done")), name);
        Task running = server.RunAsync();
        await CommandChannel.SendAsync(["--action", "spotlight"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name); // warm up
        var clock = Stopwatch.StartNew();
        const int runs = 20;
        for (int i = 0; i < runs; i++)
            Assert.NotNull(await CommandChannel.SendAsync(["--room", "Lobby"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name));
        double each = clock.Elapsed.TotalMilliseconds / runs;
        server.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Within(each, 150, "a command-line request and its answer");
    }

    /// <summary>Takes everything and does nothing: only the caller's cost is measured.</summary>
    private sealed class NullEffects : IEffectsEngine
    {
        public void Click(PointF at, bool secondary) { }
        public void Pointer(PointF? at) { }
        public void Laser(PointF? at) { }
        public void ShowCursor(bool on) { }
        public void InkStart(InkStroke stroke) { }
        public void InkAdd(int id, IReadOnlyList<PointF> points) { }
        public void InkSet(int id, IReadOnlyList<PointF> points) { }
        public void InkRemove(int id) { }
        public void InkClear() { }
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) { }
        public void Spotlight(PointF? at) { }
        public void Caption(string? name, string? second) { }
        public void Logo(string? path) { }
        public void Qr(string? text, InsetCorner corner) { }
        public void LiveCaption(string? text) { }
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) { }
        public event EventHandler<string>? InsetFailed { add { } remove { } }
    }
}
