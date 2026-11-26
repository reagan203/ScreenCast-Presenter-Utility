using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using Color4 = Vortice.Mathematics.Color4;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-102: the check before sharing.</summary>
public class PreShareCheckTests
{
    private static CropRegion Box(double x) => CropRegion.Create(x, 0.1, 0.1, 0.05, 0.0001)!;

    [Fact]
    public void A_report_says_what_was_found_and_what_isnt_covered_yet()
    {
        var report = new CheckReport([new(SensitiveKinds.Emails, Box(0.1)), new(SensitiveKinds.Emails, Box(0.3)), new(SensitiveKinds.PasswordFields, Box(0.5)), new(SensitiveKinds.Faces, Box(0.7))]);
        Assert.False(report.Clean);
        Assert.Equal("2 email addresses, a password box and a face", report.Words());
        Assert.Equal(SensitiveKinds.PasswordFields | SensitiveKinds.Faces, report.Missing(SensitiveKinds.Emails));
        Assert.Equal(SensitiveKinds.None, report.Missing(SensitiveKinds.All | SensitiveKinds.Faces));
        Assert.True(new CheckReport([]).Clean);
        Assert.Equal("nothing", new CheckReport([]).Words());
        Assert.False(CheckReport.Everything.HasFlag(SensitiveKinds.KeepLargestFace)); // the check finds every face
    }

    [Fact]
    public void The_helper_line_comes_back_the_same_and_nonsense_is_refused()
    {
        var report = new CheckReport([new(SensitiveKinds.Secrets, Box(0.2)), new(SensitiveKinds.Faces, Box(0.6))]);
        CheckReport back = CheckReport.Parse(report.ToLine())!;
        Assert.Equal(2, back.Findings.Count);
        Assert.Equal(SensitiveKinds.Secrets, back.Findings[0].Kind);
        Assert.Equal(0.2, back.Findings[0].Box.X, 4);
        Assert.Null(CheckReport.Parse("progress 1 2"));
        Assert.Null(CheckReport.Parse("checked {not json"));
        Assert.Null(CheckReport.Parse(null));
        Assert.Empty(CheckReport.Parse("checked [[3,0.1,0.1,0.1,0.1],[4096,0.1,0.1,0.1,0.1],[1,\"x\"],[1,0.1,0.1,0.1,0.1]]")!.Findings.Skip(1)); // two kinds at once, unknown kinds and junk dropped
        Assert.Equal("checked []", new CheckReport([]).ToLine());
        Assert.All(new[] { SensitiveKinds.Emails, SensitiveKinds.Phones, SensitiveKinds.Cards, SensitiveKinds.Ibans, SensitiveKinds.IdNumbers, SensitiveKinds.Secrets, SensitiveKinds.Custom, SensitiveKinds.PasswordFields, SensitiveKinds.Faces },
            k => Assert.False(string.IsNullOrEmpty(CheckReport.Label(k))));
    }

    [Fact]
    public void The_check_finds_text_your_words_and_faces_in_a_picture_but_not_under_hidden_parts()
    {
        using var page = new Bitmap(1200, 600, PixelFormat.Format32bppRgb);
        Rectangle emailAt;
        using (Graphics g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 34, GraphicsUnit.Pixel);
            g.DrawString("Write to jane.doe@example.com", font, Brushes.Black, 20, 20);
            SizeF before = g.MeasureString("Write to ", font);
            emailAt = Rectangle.Round(new RectangleF(20 + before.Width, 20, g.MeasureString("jane.doe@example.com", font).Width, before.Height));
            g.DrawString("Client: Northwind", font, Brushes.Black, 20, 110);
            g.DrawString("Password: Tulip7Harbor", font, Brushes.Black, 20, 200);
            using var painting = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "face-painting.jpg"));
            g.DrawImage(painting, 800, 50, 300, 450);
        }
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null) return;
        var whole = new Rectangle(0, 0, page.Width, page.Height);
        SourceWindow picture = SourceWindow.ForPicture(@"C:\x.png"); // off screen: no password boxes asked for
        CheckReport report = CaptureHost.Check(page, picture, whole, whole, [], ["Northwind"]);
        Assert.Contains(report.Findings, f => f.Kind == SensitiveKinds.Emails);
        Assert.Contains(report.Findings, f => f.Kind == SensitiveKinds.Custom);
        Assert.Contains(report.Findings, f => f.Kind == SensitiveKinds.Secrets);
        if (FaceFinder.IsSupported) Assert.Contains(report.Findings, f => f.Kind == SensitiveKinds.Faces);

        // A part already hidden over the address: not reported again.
        var hidden = CropRegion.Create((emailAt.X - 10) / 1200.0, (emailAt.Y - 10) / 600.0, (emailAt.Width + 20) / 1200.0, (emailAt.Height + 20) / 600.0)!;
        CheckReport covered = CaptureHost.Check(page, picture, whole, whole, [hidden], ["Northwind"]);
        Assert.DoesNotContain(covered.Findings, f => f.Kind == SensitiveKinds.Emails);
    }

    [Fact]
    public void The_result_window_shows_what_was_found_and_lets_go_of_the_preview_file()
    {
        string preview = Path.Combine(Path.GetTempPath(), $"scp-check-test-{Guid.NewGuid():N}.png");
        using (var b = new Bitmap(800, 400)) b.Save(preview, ImageFormat.Png);
        var report = new CheckReport([new(SensitiveKinds.Emails, Box(0.2))]);
        using (var form = new UI.CheckForm("Inbox", preview, report, SensitiveKinds.Emails))
        {
            File.Delete(preview); // read into memory: the file can go at once
            Assert.Equal(UI.CheckForm.Choice.Cancel, form.Result);
        }
        Assert.Contains("1 email address".Replace("1 ", "An "), UI.CheckForm.Summary(report, SensitiveKinds.Emails), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already covers", UI.CheckForm.Summary(report, SensitiveKinds.None));
        Assert.Contains("Only you see this", UI.CheckForm.Summary(new CheckReport([]), SensitiveKinds.None));
        Assert.Equal(new Size(640, 320), UI.CheckForm.Fit(new Size(1600, 800), 640));
        Assert.Equal(new Size(100, 50), UI.CheckForm.Fit(new Size(100, 50), 640));
        Assert.Equal(Size.Empty, UI.CheckForm.Fit(Size.Empty, 640));
        using var noPreview = new UI.CheckForm("Inbox", Path.Combine(Path.GetTempPath(), "missing.png"), new CheckReport([]), SensitiveKinds.None);
    }

    [Fact]
    public void Checking_is_an_action_a_setting_and_a_helper_switch()
    {
        Assert.NotNull(ShortcutCatalog.Find("check-before-showing"));
        Assert.NotNull(UI.Icons.ForCommand("check-before-showing"));
        Assert.False(new AppSettings().CheckBeforeShowing); // off unless chosen
        Assert.False(CommandLine.IsRequest([CaptureProtocol.CheckSwitch, "window:1"])); // the helper, never a request
    }
}

/// <summary>FR-102 end to end: the real helper process reads a real window and finds what's in it.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class PreShareCheckHelperTests
{
    [Fact]
    public async Task The_helper_checks_a_real_window_and_draws_a_private_preview()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "ScreenCastPresenter.exe");
        if (!File.Exists(exe) || !Direct3D.IsCaptureSupported) return;
        using var form = new PasswordForm();
        string preview = Path.Combine(Path.GetTempPath(), $"scp-check-{Guid.NewGuid():N}.png");
        try
        {
            var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { CaptureProtocol.CheckSwitch, CaptureProtocol.SourceSpec(form.Source), "-", "redact none", "-", preview }) info.ArgumentList.Add(a);
            using Process helper = Process.Start(info)!;
            string output = await helper.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await helper.WaitForExitAsync();
            Assert.Equal(0, helper.ExitCode);
            CheckReport report = output.Split('\n').Select(l => CheckReport.Parse(l.Trim())).OfType<CheckReport>().Single();
            Assert.Contains(report.Findings, f => f.Kind == SensitiveKinds.PasswordFields); // asked of Windows, exactly
            using var picture = new Bitmap(preview);
            bool outlined = false;
            for (int x = 0; x < picture.Width && !outlined; x += 2)
            for (int y = 0; y < picture.Height && !outlined; y += 2)
                outlined = picture.GetPixel(x, y) is var c && Math.Abs(c.R - EffectStyle.ClickPrimary.R) < 8 && Math.Abs(c.G - EffectStyle.ClickPrimary.G) < 8 && Math.Abs(c.B - EffectStyle.ClickPrimary.B) < 8;
            Assert.True(outlined, "what was found is outlined in the preview");
        }
        finally
        {
            if (File.Exists(preview)) File.Delete(preview);
        }
    }
}

/// <summary>FR-105: chapters for recordings, and a timeline of what was shown.</summary>
public class ChaptersTimelineTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Chapters_start_when_what_is_shown_changes()
    {
        var log = new ChapterLog();
        log.Add(S(3), "Slides");
        log.Add(S(40), "Slides"); // the same again: one chapter
        log.Add(S(62.7), "Budget.xlsx");
        log.Add(S(64), "Blank"); // a moment later: the short one gives way
        log.Add(S(200), "Slides");
        Assert.Equal([new Chapter(S(0), "Slides"), new Chapter(S(62), "Blank"), new Chapter(S(200), "Slides")], log.Chapters);
        log.Add(S(100), "Q&A"); // never backwards
        Assert.Equal(S(200), log.Chapters[^1].Start);
        log.Add(S(300), "   ");
        Assert.Equal(3, log.Chapters.Count);
    }

    [Fact]
    public void A_quick_look_elsewhere_and_back_is_one_chapter()
    {
        var log = new ChapterLog();
        log.Add(S(0), "Slides");
        log.Add(S(60), "Mail");
        log.Add(S(62), "Slides"); // Mail lasted 2 s: gone, and Slides goes on
        Assert.Equal([new Chapter(S(0), "Slides")], log.Chapters);
    }

    [Fact]
    public void Chapters_are_written_for_web_players_video_sites_and_ffmpeg()
    {
        var log = new ChapterLog();
        log.Add(S(0), "Intro");
        log.Add(S(75.5), "Demo = live; #1");
        log.Add(S(3725), "Questions");
        Assert.Equal("WEBVTT\n\nChapter 1\n00:00:00.000 --> 00:01:15.000\nIntro\n\nChapter 2\n00:01:15.000 --> 01:02:05.000\nDemo = live; #1\n\nChapter 3\n01:02:05.000 --> 01:05:00.000\nQuestions\n\n",
            log.WebVtt(S(3900)));
        Assert.Equal("0:00 Intro\n1:15 Demo = live; #1\n1:02:05 Questions\n", log.List());
        string meta = log.FfMetadata(S(3900));
        Assert.StartsWith(";FFMETADATA1\n", meta);
        Assert.Contains("START=75000\nEND=3725000\ntitle=Demo \\= live\\; \\#1\n", meta);
        Assert.Contains("START=3725000\nEND=3900000\n", meta);
        (string vtt, string list, string m) = ChapterLog.PathsFor(@"C:\Videos\Talk 1.mp4");
        Assert.Equal(@"C:\Videos\Talk 1.chapters.vtt", vtt);
        Assert.Equal(@"C:\Videos\Talk 1.chapters.txt", list);
        Assert.Equal(@"C:\Videos\Talk 1.ffmetadata.txt", m);
    }

    [Fact]
    public void Chapters_are_saved_beside_the_video_only_when_there_are_some()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-chapters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string video = Path.Combine(folder, "Screen Recording.mp4");
            var one = new ChapterLog();
            one.Add(S(0), "Slides");
            Assert.False(one.Save(video, S(60))); // one chapter is no chapters
            Assert.Empty(Directory.GetFiles(folder));
            var two = new ChapterLog();
            two.Add(S(0), "Slides");
            two.Add(S(30), "Demo");
            Assert.True(two.Save(video, S(60)));
            Assert.Equal(3, Directory.GetFiles(folder).Length);
            Assert.Equal("0:00 Slides\n0:30 Demo\n", File.ReadAllText(ChapterLog.PathsFor(video).List));
            Assert.False(two.Save(Path.Combine(folder, "no", "such", "folder", "v.mp4"), S(60)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        var many = new ChapterLog();
        for (int i = 0; i < 1000; i++) many.Add(S(i * 10), $"Thing {i}");
        Assert.Equal(ChapterLog.MaxChapters, many.Chapters.Count);
        Assert.True(new AppSettings().RecordingChapters); // on by default: they're just files beside the video
    }

    [Fact]
    public void A_timeline_is_written_as_things_happen()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-timeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new DateTime(2026, 11, 26, 9, 30, 0);
            (TimelineWriter? writer, string? problem) = TimelineWriter.Start(folder, start);
            Assert.Null(problem);
            writer!.Add(start.AddSeconds(5), "Showing “Slides”");
            writer.Add(start.AddSeconds(6), "Showing “Slides”"); // the same twice: once
            writer.Add(start.AddMinutes(10), "Blank");
            string soFar;
            using (var read = new FileStream(writer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) soFar = new StreamReader(read).ReadToEnd();
            Assert.Equal("[09:30:05] Showing “Slides”\r\n[09:40:00] Blank\r\n".Replace("\r\n", Environment.NewLine), soFar);
            writer.Dispose();
            Assert.Equal("Timeline 2026-11-26 0930.txt", Path.GetFileName(writer.Path));
            (TimelineWriter? second, _) = TimelineWriter.Start(folder, start);
            Assert.Equal("Timeline 2026-11-26 0930 (2).txt", Path.GetFileName(second!.Path));
            second.Dispose();
            second.Add(start, "after closing"); // no crash
            Assert.Null(TimelineWriter.Start(Path.Combine(Path.GetTempPath(), "bad|name"), start).Writer);
            Assert.False(new AppSettings().SaveTimeline);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>FR-106: the replay buffer.</summary>
public class ReplayBufferTests
{
    [Fact]
    public void Enough_parts_are_kept_and_the_newest_make_the_last_minute()
    {
        TimeSpan part = ReplayBuffer.PartLength;
        Assert.Equal((int)Math.Ceiling(60 / part.TotalSeconds) + 1, ReplayBuffer.Keep(60));
        var parts = Enumerable.Range(0, 8).Select(i => ($"p{i}", TimeSpan.FromSeconds(15))).ToList();
        Assert.Equal(["p4", "p5", "p6", "p7"], ReplayBuffer.PartsFor(parts, 60).Select(p => p.Path));
        Assert.Equal(["p6", "p7"], ReplayBuffer.PartsFor(parts, 30).Select(p => p.Path));
        Assert.Equal(["p0", "p1"], ReplayBuffer.PartsFor(parts.Take(2).ToList(), 60).Select(p => p.Path)); // all there is
        Assert.Empty(ReplayBuffer.PartsFor([], 60));
        Assert.Equal(60, ReplayBuffer.Normalize(50));
        Assert.Equal(120, ReplayBuffer.Normalize(1000));
        Assert.EndsWith("part-000042.mp4", ReplayBuffer.PartPath(@"C:\t", 42));
    }

    [Fact]
    public void The_recorder_is_told_to_keep_parts_and_says_when_it_saved()
    {
        var request = new RecordingRequest(TestData.Window(5, "Inbox"), false, MeetingSound.None, false, @"C:\Temp\Replay folder", null, SensitiveKinds.None, null, 60);
        RecordingRequest back = RecordingProtocol.ParseStart(RecordingProtocol.Start(request))!;
        Assert.True(back.IsReplay);
        Assert.Equal(60, back.ReplaySeconds);
        Assert.Equal(@"C:\Temp\Replay folder", back.Path);
        Assert.False(RecordingProtocol.ParseStart(RecordingProtocol.Start(request with { ReplaySeconds = 0 }))!.IsReplay);
        Assert.Equal(ReplayBuffer.MaxSeconds, RecordingProtocol.ParseStart(RecordingProtocol.Start(request with { ReplaySeconds = 99999 }))!.ReplaySeconds);

        RecordingReply saved = RecordingReply.Parse(RecordingProtocol.ReplaySaved(TimeSpan.FromSeconds(61), @"C:\Videos\Replay 1.mp4"))!;
        Assert.Equal(("replay-saved", 61L, @"C:\Videos\Replay 1.mp4"), (saved.Kind, saved.A, saved.Text));
        Assert.Equal("disk full", RecordingReply.Parse(RecordingProtocol.ReplayFailed("disk\nfull"))!.Text);
    }

    [Fact]
    public void The_controller_asks_for_a_save_and_reports_how_it_went()
    {
        var process = new FakeRecordingProcess();
        var controller = new RecordingController(() => process, _ => long.MaxValue, useCurrentContext: false);
        (string, TimeSpan)? saved = null;
        string? failed = null;
        controller.ReplaySaved += (_, s) => saved = s;
        controller.ReplayFailed += (_, f) => failed = f;
        controller.SaveReplay(@"C:\x.mp4"); // nothing running: nothing sent
        Assert.Empty(process.Sent);
        var request = new RecordingRequest(TestData.Window(5, "Inbox"), false, MeetingSound.None, false, Path.Combine(Path.GetTempPath(), "scp-replay-test"), ReplaySeconds: 60);
        Assert.Null(controller.Start(request, "the last minute", followsOutput: true));
        process.Reply(RecordingProtocol.Started(1920, 1080));
        controller.SaveReplay(@"C:\Videos\Replay.mp4");
        Assert.Equal("save-replay C:\\Videos\\Replay.mp4", process.Sent[^1]);
        process.Reply(RecordingProtocol.ReplaySaved(TimeSpan.FromSeconds(60), @"C:\Videos\Replay.mp4"));
        Assert.Equal((@"C:\Videos\Replay.mp4", TimeSpan.FromSeconds(60)), saved);
        process.Reply(RecordingProtocol.ReplayFailed(""));
        Assert.Equal("The last minute couldn't be saved.", failed);
        Assert.False(new AppSettings().ReplayOn); // off unless chosen
        Assert.Equal(ReplayBuffer.DefaultSeconds, new AppSettings().ReplaySeconds);
        Assert.Equal(30, new AppSettings { ReplaySeconds = 31 }.Normalize().ReplaySeconds);
        foreach (string id in new[] { "save-replay", "replay" }) Assert.NotNull(UI.Icons.ForCommand(id));
        Assert.Equal(["cli:on:replay"], CommandLine.Parse(["--on", "replay"]).Request!.Actions);
    }

    /// <summary>Three real encoded parts joined into one file, with nothing encoded again.</summary>
    [Fact]
    public void Parts_join_into_one_video_with_picture_and_sound()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-join-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var parts = new List<string>();
            using (ID3D11Device device = Mp4Writer.CreateDevice())
                for (int p = 0; p < 3; p++)
                {
                    string path = Path.Combine(folder, $"part-{p}.mp4");
                    WritePart(device, path, seconds: 2, hue: p / 3f);
                    parts.Add(path);
                }
            string joined = Path.Combine(folder, "joined.mp4");
            var clock = Stopwatch.StartNew();
            TimeSpan length = Mp4Joiner.Join(parts, joined);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"joining took {clock.Elapsed}"); // nothing is encoded again
            Assert.InRange(length.TotalSeconds, 5.8, 6.4);
            Mp4Info info = Mp4Info.Read(joined);
            Assert.True(info.HasVideo);
            Assert.True(info.HasAudio);
            Assert.Equal((640, 360), (info.Width, info.Height));
            Assert.InRange(info.Duration.TotalSeconds, 5.8, 6.4);
            Assert.InRange(Mp4Info.CountVideoFrames(joined), 3 * 2 * Mp4Writer.FrameRate - 4, 3 * 2 * Mp4Writer.FrameRate);
            Assert.Throws<ArgumentException>(() => Mp4Joiner.Join([], joined));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A still picture writes a frame a second: the joined video keeps the time it held still.</summary>
    [Fact]
    public void Parts_of_a_still_picture_keep_their_real_length()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-join-still-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var parts = new List<string>();
            using (ID3D11Device device = Mp4Writer.CreateDevice())
                for (int p = 0; p < 2; p++)
                {
                    string path = Path.Combine(folder, $"still-{p}.mp4");
                    WriteStill(device, path, frames: 3); // at 0, 1 and 2 s
                    parts.Add(path);
                }
            string joined = Path.Combine(folder, "joined.mp4");
            TimeSpan length = Mp4Joiner.Join(parts, joined, [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)]);
            Assert.InRange(length.TotalSeconds, 5.9, 6.1);
            Assert.InRange(Mp4Info.Read(joined).Duration.TotalSeconds, 5.8, 6.2);
            Assert.Equal(6, Mp4Info.CountVideoFrames(joined));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void WriteStill(ID3D11Device device, string path, int frames)
    {
        using var writer = new Mp4Writer(path, 640, 360, device, withAudio: false);
        // One texture per frame: the encoder may hold a frame until the next arrives, so none is reused.
        var textures = new List<ID3D11Texture2D>();
        for (int f = 0; f < frames; f++)
        {
            ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            using (ID3D11RenderTargetView view = device.CreateRenderTargetView(texture)) device.ImmediateContext.ClearRenderTargetView(view, new Color4(0.2f, 0.4f, 0.6f, 1));
            textures.Add(texture);
            writer.WriteVideo(texture, f * Mp4Writer.Second, Mp4Writer.FrameDuration);
        }
        writer.Finish();
        foreach (ID3D11Texture2D t in textures) t.Dispose();
    }

    internal static void WritePart(ID3D11Device device, string path, int seconds, float hue)
    {
        using var writer = new Mp4Writer(path, 640, 360, device, withAudio: true);
        var textures = new ID3D11Texture2D[4];
        var views = new ID3D11RenderTargetView[4];
        for (int i = 0; i < textures.Length; i++)
        {
            textures[i] = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            views[i] = device.CreateRenderTargetView(textures[i]);
        }
        short[] pcm = new short[AudioMixer.SampleRate / Mp4Writer.FrameRate * AudioMixer.Channels];
        for (int f = 0; f < seconds * Mp4Writer.FrameRate; f++)
        {
            while (writer.VideoInFlight >= textures.Length - 1) Thread.Sleep(1);
            int slot = f % textures.Length;
            device.ImmediateContext.ClearRenderTargetView(views[slot], new Color4(hue, 1 - hue, f / 60f, 1));
            writer.WriteVideo(textures[slot], f * Mp4Writer.FrameDuration, Mp4Writer.FrameDuration);
            writer.WriteAudio(pcm, f * Mp4Writer.FrameDuration);
        }
        writer.Finish();
        foreach (ID3D11RenderTargetView v in views) v.Dispose();
        foreach (ID3D11Texture2D t in textures) t.Dispose();
    }
}

/// <summary>FR-106 end to end: the real recorder keeping short parts of the real screen, then saving the last of them.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class ReplayRecorderTests
{
    [Fact]
    public void The_recorder_keeps_parts_deletes_old_ones_and_saves_the_last_minute()
    {
        if (!Direct3D.IsCaptureSupported) return;
        DisplayMonitor screen = DisplayHelpers.GetMonitors().First(m => m.IsPrimary);
        string folder = Path.Combine(Path.GetTempPath(), "scp-replay-" + Guid.NewGuid().ToString("N"));
        string saved = Path.Combine(Path.GetTempPath(), $"scp-replay-{Guid.NewGuid():N}.mp4");
        TimeSpan before = ReplayBuffer.PartLength;
        ReplayBuffer.PartLength = TimeSpan.FromSeconds(1.5); // short parts so the test is quick
        var lines = new List<string>();
        using var savedSignal = new ManualResetEventSlim();
        try
        {
            var request = new RecordingRequest(SourceWindow.ForScreen(screen), false, MeetingSound.None, false, folder, ReplaySeconds: 30);
            using var recorder = new Recorder(request, line =>
            {
                lock (lines) lines.Add(line);
                if (line.StartsWith("replay-", StringComparison.Ordinal)) savedSignal.Set();
            });
            recorder.Start();
            Thread.Sleep(TimeSpan.FromSeconds(6));
            int kept = Directory.GetFiles(folder, "part-*.mp4").Length;
            recorder.SaveReplay(saved);
            Assert.True(savedSignal.Wait(TimeSpan.FromSeconds(30)), "the save finished");
            recorder.Stop();
            recorder.Wait();
            string reply;
            lock (lines) reply = lines.Last(l => l.StartsWith("replay-", StringComparison.Ordinal));
            Assert.StartsWith("replay-saved", reply);
            Assert.InRange(kept, 2, ReplayBuffer.Keep(30) + 1);
            Mp4Info info = Mp4Info.Read(saved);
            Assert.True(info.HasVideo);
            Assert.InRange(info.Duration.TotalSeconds, 2, 8); // every part kept so far (less than the 30 s asked for)
            lock (lines) Assert.Contains(lines, l => l.StartsWith("finished", StringComparison.Ordinal));
        }
        finally
        {
            ReplayBuffer.PartLength = before;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (File.Exists(saved)) File.Delete(saved);
        }
    }
}
