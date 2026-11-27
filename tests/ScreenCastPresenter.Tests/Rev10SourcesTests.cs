using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>A tiny real PDF, written by hand: each page white with a black box in the top left, and its number.</summary>
internal static class TestPdf
{
    public static string Make(int pages, string? folder = null)
    {
        string path = Path.Combine(folder ?? Path.GetTempPath(), $"scp-test-{Guid.NewGuid():N}.pdf");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R"))}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (int i = 0; i < pages; i++)
        {
            string content = $"0 0 0 rg 0 692 200 100 re f BT /F1 48 Tf 72 400 Td (Page {i + 1}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + i * 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }
}

/// <summary>FR-115: PDFs shown page by page.</summary>
[Collection(DesktopWindowsCollection.Name)] // heavy (real PDF rendering, text recognition): never beside the timing tests
public class PdfPagesTests
{
    [Fact]
    public void Pages_are_kept_in_a_folder_named_from_the_file()
    {
        DateTime t = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
        string a = PdfPages.FolderFor(@"C:\Cache", @"C:\Docs\Deck.pdf", 1000, t);
        Assert.Equal(a, PdfPages.FolderFor(@"C:\Cache", @"c:\docs\DECK.PDF", 1000, t)); // the same file whatever its capitals
        Assert.NotEqual(a, PdfPages.FolderFor(@"C:\Cache", @"C:\Docs\Deck.pdf", 1001, t)); // changed: new pages
        Assert.NotEqual(a, PdfPages.FolderFor(@"C:\Cache", @"C:\Docs\Deck.pdf", 1000, t.AddSeconds(1)));
        Assert.StartsWith(@"C:\Cache\pdf-", a);
        Assert.Equal(Path.Combine(a, "page-0007.png"), PdfPages.PagePath(a, 7));
    }

    [Fact]
    public void Pages_turn_within_the_document()
    {
        Assert.Equal(2, PdfPages.Turn(1, 3, +1));
        Assert.Null(PdfPages.Turn(3, 3, +1)); // the last page
        Assert.Null(PdfPages.Turn(1, 3, -1)); // the first
        Assert.Equal(5, PdfPages.Turn(4, 0, +1)); // how many isn't known yet: on
        Assert.Equal(3, PdfPages.Clamp(9, 3));
        Assert.Equal(1, PdfPages.Clamp(-4, 3));
        Assert.Equal(9, PdfPages.Clamp(9, 0));
        Assert.Equal([5, 6, 7, 4], PdfPages.ToMake(5, 20)); // it, the next ones, the one before
        Assert.Equal([3, 2], PdfPages.ToMake(3, 3));
        Assert.Equal([1, 2, 3], PdfPages.ToMake(1, 0));
    }

    [Fact]
    public void A_page_is_made_sharp_keeping_its_shape()
    {
        Assert.Equal((1978, 2560), PdfPages.RenderSize(612, 792)); // US letter, portrait
        Assert.Equal((2560, 1440), PdfPages.RenderSize(960, 540)); // a 16:9 slide
        Assert.Equal((2560, 1440), PdfPages.RenderSize(double.NaN, 5));
        Assert.Equal((2560, 1440), PdfPages.RenderSize(0, 0));
    }

    [Fact]
    public void Files_that_cant_be_shown_say_why()
    {
        Assert.Equal("Choose a PDF.", PdfPages.Problem(null));
        Assert.Equal("Choose a PDF document.", PdfPages.Problem(@"C:\x.docx"));
        Assert.Contains("can't be found", PdfPages.Problem(@"C:\nowhere\missing.pdf"));
        string empty = Path.Combine(Path.GetTempPath(), $"scp-empty-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(empty, []);
        try
        {
            Assert.Contains("is empty", PdfPages.Problem(empty));
        }
        finally
        {
            File.Delete(empty);
        }
        Assert.Equal("Handout · page 3 of 12", PdfPages.Title(@"C:\x\Handout.pdf", 3, 12));
        Assert.Equal(12, PdfPages.ParsePages("pages 12"));
        Assert.Null(PdfPages.ParsePages("pages -1"));
        Assert.Null(PdfPages.ParsePages("pages twelve"));
    }

    [Fact]
    public void A_real_pdfs_pages_are_made_into_pictures()
    {
        string pdf = TestPdf.Make(3);
        string folder = Path.Combine(Path.GetTempPath(), "scp-pages-" + Guid.NewGuid().ToString("N"));
        var said = new List<string>();
        try
        {
            Assert.Equal(0, PdfRenderer.Run([CaptureProtocol.PdfSwitch, pdf, folder, "1,3,9"], said.Add));
            Assert.Equal(["pages 3", "done 1", "done 3"], said); // page 9 doesn't exist: left out
            using (var page = new Bitmap(PdfPages.PagePath(folder, 1)))
            {
                // At least 2560 pixels long, its shape kept (Windows makes it bigger on a scaled display: never smaller).
                Assert.True(page.Height >= 2560, $"made {page.Size}");
                Assert.Equal(612.0 / 792, (double)page.Width / page.Height, 2);
                Color paper = page.GetPixel(page.Width / 2, page.Height - 20), box = page.GetPixel(40, 40);
                Assert.True(paper.R > 240 && paper.G > 240 && paper.B > 240, $"the page is white: {paper}");
                Assert.True(box.R < 40 && box.G < 40 && box.B < 40, $"the box is black: {box}");
            }
            Assert.False(File.Exists(PdfPages.PagePath(folder, 2)));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp")); // nothing half written left
        }
        finally
        {
            File.Delete(pdf);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_pdf_says_so_and_nothing_is_made()
    {
        string bad = Path.Combine(Path.GetTempPath(), $"scp-bad-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(bad, "%PDF-1.4 this is not really a PDF");
        string folder = Path.Combine(Path.GetTempPath(), "scp-pages-" + Guid.NewGuid().ToString("N"));
        var said = new List<string>();
        try
        {
            Assert.NotEqual(0, PdfRenderer.Run([CaptureProtocol.PdfSwitch, bad, folder, "1"], said.Add));
            Assert.Contains(said, l => l.StartsWith("failed", StringComparison.Ordinal) && l.Contains("damaged", StringComparison.Ordinal));
            Assert.False(File.Exists(PdfPages.PagePath(folder, 1)));
        }
        finally
        {
            File.Delete(bad);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_real_helper_process_makes_the_pages()
    {
        RemoteWgcEngine.HelperExe = Path.Combine(AppContext.BaseDirectory, "ScreenCastPresenter.exe");
        string pdf = TestPdf.Make(2);
        string folder = Path.Combine(Path.GetTempPath(), "scp-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            (int pages, IReadOnlyList<int> done, string? problem) = await PdfRenderer.RenderAsync(pdf, folder, [1, 2], TimeSpan.FromSeconds(60));
            Assert.Null(problem);
            Assert.Equal(2, pages);
            Assert.Equal([1, 2], done);
            Assert.True(File.Exists(PdfPages.PagePath(folder, 2)));
        }
        finally
        {
            RemoteWgcEngine.HelperExe = null;
            File.Delete(pdf);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_pdf_in_a_playlist_is_each_of_its_pages()
    {
        var item = new PlaylistItem { Kind = PlaylistItem.DocumentKind, Path = @"C:\Docs\Menu.pdf", Seconds = 20 };
        List<PlaylistItem> steps = PlaylistPlayer.Expand([item], _ => 3);
        Assert.Equal([1, 2, 3], steps.Select(s => s.Slot));
        Assert.All(steps, s => Assert.True(s.IsDocument && s.Seconds == 20));
        Assert.Single(PlaylistPlayer.Expand([item])); // not known yet: its first page
        Assert.Equal(PictureSource.MaxInFolder, PlaylistPlayer.Expand([item], _ => 100_000).Count); // a huge PDF: bounded
        Assert.Equal("the pages of “Menu” for 20 seconds each", item.Words());
    }
}

/// <summary>
/// FR-116: videos as sources. Played through the real capture helper process, as the app does (one
/// engine per process: a second engine in the same process can't take over an output window).
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public class VideoSourceTests
{
    [Fact]
    public void Videos_are_recognised_and_problems_said()
    {
        Assert.True(VideoSource.IsVideo(@"C:\a\Intro.MP4"));
        Assert.True(VideoSource.IsVideo("x.webm"));
        Assert.False(VideoSource.IsVideo("x.gif"));
        Assert.False(VideoSource.IsVideo(null));
        Assert.Equal("Choose a video.", VideoSource.Problem(""));
        Assert.StartsWith("Choose an MP4", VideoSource.Problem("x.txt"));
        Assert.Contains("can't be found", VideoSource.Problem(@"C:\nowhere\clip.mp4"));
        Assert.Equal("Intro", VideoSource.Name(@"C:\a\Intro.mp4"));
        Assert.Equal("1:05 of 3:20", VideoSource.Position(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(200)));
    }

    [Fact]
    public void Video_commands_travel_to_the_helper_and_back()
    {
        foreach (VideoCommand c in new[] { VideoCommand.Play, VideoCommand.Pause, VideoCommand.Restart })
            Assert.Equal((c, 0d), CaptureProtocol.ParseVideo(CaptureProtocol.Video(c)["video ".Length..]));
        Assert.Equal((VideoCommand.Seek, -10d), CaptureProtocol.ParseVideo(CaptureProtocol.Video(VideoCommand.Seek, -10)["video ".Length..]));
        Assert.Equal((VideoCommand.Seek, 3600d), CaptureProtocol.ParseVideo("seek 99999"));
        Assert.Null(CaptureProtocol.ParseVideo("seek"));
        Assert.Null(CaptureProtocol.ParseVideo("seek NaN"));
        Assert.Null(CaptureProtocol.ParseVideo("explode"));
        Assert.Null(CaptureProtocol.ParseVideo(""));
    }

    [Fact]
    public void Playing_a_video_is_said_plainly_when_it_cant()
    {
        Assert.Contains("MP4", VideoFeed.Words(Windows.Media.Playback.MediaPlayerError.SourceNotSupported, null));
        Assert.Contains("damaged", VideoFeed.Words(Windows.Media.Playback.MediaPlayerError.DecodingError, null));
        Assert.Equal("Something odd", VideoFeed.Words(Windows.Media.Playback.MediaPlayerError.Unknown, " Something odd "));
    }

    [Fact]
    public void A_real_video_plays_into_a_gpu_texture_pauses_while_held_and_ends()
    {
        if (!Direct3D.IsCaptureSupported) return;
        string mp4 = Path.Combine(Path.GetTempPath(), $"scp-video-{Guid.NewGuid():N}.mp4");
        using Vortice.Direct3D11.ID3D11Device device = Direct3D.CreateDevice();
        ReplayBufferTests.WritePart(device, mp4, 2, 0.3f);
        var events = new List<string>();
        using var wake = new AutoResetEvent(false);
        try
        {
            using Vortice.Direct3D11.ID3D11Texture2D target = device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(
                Vortice.DXGI.Format.B8G8R8A8_UNorm, 640, 360, 1, 1, Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource));
            using Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface = Direct3D.SurfaceOf(target);
            using VideoFeed feed = VideoFeed.Start(mp4, sound: false, () => wake.Set(), e => { lock (events) events.Add(e); });
            int frames = 0;
            bool Pump(Func<bool> until, TimeSpan limit)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < limit)
                {
                    if (feed.TakeLatest(surface)) frames++;
                    lock (events) if (until()) return true;
                    wake.WaitOne(50);
                }
                return false;
            }
            Assert.True(Pump(() => feed.Size != Size.Empty, TimeSpan.FromSeconds(10)), "it opened");
            Assert.Equal(new Size(640, 360), feed.Size);
            Pump(() => false, TimeSpan.FromSeconds(2));
            Assert.True(frames <= 2, $"it doesn't play while the TV doesn't show it (its first picture may come, ready): {frames} frames");
            lock (events) Assert.Empty(events);
            feed.SetVisible(true); // shown: it plays
            Assert.True(Pump(() => events.Contains("ended"), TimeSpan.FromSeconds(15)), "it played to its end: " + string.Join(", ", events));
            Assert.True(frames >= 20, $"frames arrived: {frames}");
            // Its last picture stays: the test video's last frame is (0.3, 0.7, 59/60) of full brightness.
            Assert.True(Near(ReadCentre(device, target), System.Drawing.Color.FromArgb(77, 178, 251), 30), $"its last picture is in the texture: {ReadCentre(device, target)}");

            feed.Command(VideoCommand.Restart, 0);
            feed.SetHeld(true); // frozen: paused
            Pump(() => false, TimeSpan.FromSeconds(3));
            lock (events) Assert.Single(events, e => e == "ended");
            feed.SetHeld(false); // live again: plays on to the end
            Assert.True(Pump(() => events.Count(e => e == "ended") == 2, TimeSpan.FromSeconds(15)), "played again after going live: " + string.Join(", ", events));
            lock (events) Assert.DoesNotContain(events, e => e.StartsWith("failed", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(mp4);
        }
    }

    private static System.Drawing.Color ReadCentre(Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11Texture2D texture)
    {
        using Vortice.Direct3D11.ID3D11Texture2D staging = device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = 640, Height = 360, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Staging,
            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
        });
        device.ImmediateContext.CopyResource(staging, texture);
        Vortice.Direct3D11.MappedSubresource map = device.ImmediateContext.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
        try
        {
            unsafe
            {
                byte* p = (byte*)map.DataPointer + 180 * map.RowPitch + 320 * 4;
                return System.Drawing.Color.FromArgb(p[2], p[1], p[0]);
            }
        }
        finally
        {
            device.ImmediateContext.Unmap(staging, 0);
        }
    }

    private static bool Near(System.Drawing.Color a, System.Drawing.Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    [Fact]
    public void A_file_that_isnt_a_video_says_why()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"scp-video-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(fake, "not a video at all");
        var events = new List<string>();
        using var wake = new AutoResetEvent(false);
        try
        {
            using VideoFeed feed = VideoFeed.Start(fake, sound: false, () => wake.Set(), e => { lock (events) events.Add(e); });
            Assert.True(SpinWait.SpinUntil(() => feed.Failure is not null, TimeSpan.FromSeconds(10)), "it said it couldn't play");
            Assert.Equal(Size.Empty, feed.Size); // nothing to show
            lock (events) Assert.StartsWith("failed ", Assert.Single(events));
            feed.Pump(); // asking more of a failed video is harmless
            feed.Command(VideoCommand.Play, 0);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public void A_video_can_only_be_the_main_picture()
    {
        Assert.Throws<InvalidOperationException>(() => InsetFeed.Start(SourceWindow.ForVideo(@"C:\x.mp4"), null!, null, null!, () => { }));
    }
}

/// <summary>FR-117: the whiteboard and its drawing.</summary>
public class WhiteboardTests
{
    [Fact]
    public void The_board_is_made_once_in_each_colour()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-board-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dark = Whiteboard.Render(folder, light: false)!;
            DateTime written = File.GetLastWriteTimeUtc(dark);
            Assert.Equal(dark, Whiteboard.Render(folder, light: false));
            Assert.Equal(written, File.GetLastWriteTimeUtc(dark)); // not made again
            string light = Whiteboard.Render(folder, light: true)!;
            Assert.NotEqual(dark, light);
            using var d = new Bitmap(dark);
            using var l = new Bitmap(light);
            Assert.Equal(new Size(Whiteboard.Width, Whiteboard.Height), d.Size);
            Assert.Equal(Whiteboard.Dark.ToArgb(), d.GetPixel(10, 10).ToArgb());
            Assert.Equal(Whiteboard.Light.ToArgb(), l.GetPixel(10, 10).ToArgb());
            Assert.True(Whiteboard.Dark.GetBrightness() < 0.2, "dark by default: no bright flash");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_saved_board_has_its_drawing_where_the_tv_draws_it()
    {
        var pen = new InkStroke(1, InkTool.Pen, Color.Red.ToArgb(), 0.01f);
        pen.Add(new PointF(0.1f, 0.5f));
        pen.Add(new PointF(0.9f, 0.5f));
        var box = new InkStroke(2, InkTool.Box, Color.Lime.ToArgb(), 0.01f);
        box.Add(new PointF(0.2f, 0.1f));
        box.Add(new PointF(0.4f, 0.3f));
        var arrow = new InkStroke(3, InkTool.Arrow, Color.Blue.ToArgb(), 0.01f);
        arrow.Add(new PointF(0.6f, 0.8f));
        arrow.Add(new PointF(0.9f, 0.9f));
        var text = new InkStroke(4, InkTool.Text, Color.White.ToArgb(), 0.06f) { Text = "Hello" };
        text.Add(new PointF(0.05f, 0.7f));
        var dot = new InkStroke(5, InkTool.Highlighter, Color.Yellow.ToArgb(), 0.03f);
        dot.Add(new PointF(0.8f, 0.2f));
        using Bitmap board = Whiteboard.Snapshot([pen, box, arrow, text, dot, new InkStroke(6, InkTool.Pen, 0, 0.01f)], light: false, 400, 300);
        Color line = board.GetPixel(200, 150);
        Assert.True(line.R > 200 && line.G < 60, $"the pen line is where it was drawn: {line}");
        Color edge = board.GetPixel(80, 60); // the box's left edge at x = 0.2
        Assert.True(edge.G > 150 && edge.R < 120, $"the box's edge: {edge}");
        Assert.Equal(Whiteboard.Dark.ToArgb(), board.GetPixel(120, 60).ToArgb()); // inside the box: the board
        Color marker = board.GetPixel(320, 60);
        Assert.True(marker.R > Whiteboard.Dark.R + 30 && marker.G > Whiteboard.Dark.G + 30, $"the highlighter dot shows, see-through: {marker}");
        Assert.NotEqual(Whiteboard.Dark.ToArgb(), board.GetPixel(330, 262).ToArgb()); // on the arrow (from 240,240 to 360,270)
        using var empty = new Bitmap(10, 10);
        using (Graphics g = Graphics.FromImage(empty)) Whiteboard.DrawStrokes(g, RectangleF.Empty, [pen]); // nowhere to draw: nothing happens
        Assert.StartsWith("Whiteboard 2026-11-26 14.05.09", Whiteboard.SaveName(new DateTime(2026, 11, 26, 14, 5, 9)));
    }
}

/// <summary>FR-125: the room sign.</summary>
public class RoomSignTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 10, 30, 0);

    private static CalendarOccurrence At(int h1, int m1, int h2, int m2, string what, int day = 0) =>
        new(Now.Date.AddDays(day).AddHours(h1).AddMinutes(m1), Now.Date.AddDays(day).AddHours(h2).AddMinutes(m2), what);

    [Fact]
    public void Free_until_the_next_booking()
    {
        RoomSignState s = RoomSign.Build("Board room", [At(13, 0, 14, 0, "Design sync"), At(15, 0, 16, 0, "1:1")], Now, titles: false);
        Assert.False(s.Busy);
        Assert.Equal("Free until 13:00", s.Status);
        Assert.Equal(["13:00–14:00  Booked", "15:00–16:00  Booked"], s.Later); // titles left out
        Assert.Equal("10:30", s.Clock);
        Assert.Equal("Free for the rest of the day", RoomSign.Build("Board room", [], Now, false).Status);
    }

    [Fact]
    public void Booked_until_back_to_back_bookings_end()
    {
        RoomSignState s = RoomSign.Build("Board room", [At(10, 0, 11, 0, "Weekly review"), At(11, 0, 12, 0, "Planning"), At(14, 0, 15, 0, "Demo")], Now, titles: true);
        Assert.True(s.Busy);
        Assert.Equal("Weekly review", s.Status);
        Assert.Equal("until 12:00", s.Detail); // the next one starts as it ends: booked through it
        Assert.Equal(["14:00–15:00  Demo"], s.Later);
        Assert.Equal("Booked", RoomSign.Build("x", [At(10, 0, 11, 0, "Secret merger talks")], Now, titles: false).Status);
    }

    [Fact]
    public void Later_bookings_are_today_only_and_few()
    {
        List<CalendarOccurrence> many = [.. Enumerable.Range(11, 8).Select(h => At(h, 0, h, 30, "Slot " + h)), At(9, 0, 10, 0, "Tomorrow", day: 1)];
        RoomSignState s = RoomSign.Build("  ", many, Now, titles: true);
        Assert.Equal(RoomSign.MaxLater, s.Later.Count);
        Assert.DoesNotContain(s.Later, l => l.Contains("Tomorrow", StringComparison.Ordinal));
        Assert.Equal("This room", s.Room); // no name given
    }

    [Fact]
    public void A_calendar_that_cant_be_read_never_says_free()
    {
        RoomSignState s = RoomSign.Build("Board room", null, Now, titles: false);
        Assert.True(s.Unavailable);
        Assert.False(s.Busy);
        Assert.DoesNotContain("Free", s.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_past_today_say_so()
    {
        Assert.Equal("11:00", RoomSign.Time(Now.Date.AddHours(11), Now));
        Assert.Equal("tomorrow 09:00", RoomSign.Time(Now.Date.AddDays(1).AddHours(9), Now));
        Assert.Equal("28 Nov 09:00", RoomSign.Time(Now.Date.AddDays(2).AddHours(9), Now));
    }

    [Fact]
    public void The_sign_is_drawn_and_written_only_when_it_says_something_new()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-sign-" + Guid.NewGuid().ToString("N"));
        try
        {
            RoomSignState free = RoomSign.Build("Board room", [], Now, false);
            string? key = null;
            string path = RoomSign.Render(folder, free, ref key)!;
            Assert.Equal(free.Key, key);
            DateTime first = File.GetLastWriteTimeUtc(path);
            Thread.Sleep(30);
            Assert.Equal(path, RoomSign.Render(folder, free, ref key));
            Assert.Equal(first, File.GetLastWriteTimeUtc(path)); // the same: not written again
            RoomSignState busy = RoomSign.Build("Board room", [At(10, 0, 11, 0, "x")], Now, false);
            RoomSign.Render(folder, busy, ref key);
            Assert.Equal(busy.Key, key);
            using var sign = new Bitmap(path);
            Assert.Equal(new Size(RoomSign.Width, RoomSign.Height), sign.Size);
            Color panel = sign.GetPixel(200, 300);
            Assert.True(panel.R > panel.G, $"booked: a red panel ({panel})");
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>The new playlist steps and favourites (FR-115, FR-116, FR-125).</summary>
public class Rev10PlaylistAndFavouriteTests
{
    [Fact]
    public void New_kinds_of_step_are_tidied_from_a_file()
    {
        Assert.Equal(PlaylistItem.VideoKind, new PlaylistItem { Kind = "video", Path = @" C:\v\a.mp4 " }.Normalized()!.Kind);
        Assert.Null(new PlaylistItem { Kind = "Video", Path = "relative.mp4" }.Normalized());
        Assert.Null(new PlaylistItem { Kind = "Pdf", Path = null }.Normalized());
        Assert.Equal(PlaylistItem.RoomSignKind, new PlaylistItem { Kind = "RoomSign" }.Normalized()!.Kind);
        foreach (PlaylistItem item in new[] { new PlaylistItem { Kind = "Video" }, new PlaylistItem { Kind = "Pdf" }, new PlaylistItem { Kind = "RoomSign" } })
            Assert.False(item.IsSlot);
        Assert.Equal("the video “Intro” to its end", new PlaylistItem { Kind = "Video", Path = @"C:\v\Intro.mp4", Seconds = 30 }.Words());
        Assert.Equal("the room sign for 1 minute", new PlaylistItem { Kind = "RoomSign", Seconds = 60 }.Words());
    }

    [Fact]
    public void Favourites_of_videos_and_pdfs_match_by_file()
    {
        var a = new Favourite { AppName = "Video", Title = "Intro", VideoPath = @"C:\v\Intro.mp4" };
        Assert.True(a.Matches(new Favourite { AppName = "Video", Title = "x", VideoPath = @"c:\V\intro.MP4" }));
        Assert.False(a.Matches(new Favourite { AppName = "Video", Title = "Intro", VideoPath = @"C:\v\Other.mp4" }));
        var doc = new Favourite { AppName = "Picture", Title = "Menu · page 1 of 3", DocumentPath = @"C:\d\Menu.pdf", DocumentPage = 1 };
        Assert.True(doc.Matches(new Favourite { AppName = "Picture", Title = "Menu · page 3 of 3", DocumentPath = @"C:\d\Menu.pdf", DocumentPage = 3 })); // any page
        Assert.False(doc.Matches(a));
    }

    [Fact]
    public void A_pdf_page_is_saved_as_the_pdf_and_its_page()
    {
        SourceWindow page = SourceWindow.ForPicture(@"C:\Cache\pdf-1\page-0004.png", "Menu · page 4 of 9") with { Document = @"C:\d\Menu.pdf", Page = 4, Pages = 9 };
        Favourite f = FavouriteMatcher.FromSource(page);
        Assert.Null(f.PicturePath); // the made picture isn't kept: it's made again
        Assert.Equal(@"C:\d\Menu.pdf", f.DocumentPath);
        Assert.Equal(4, f.DocumentPage);
        Assert.Null(FavouriteMatcher.FromSource(SourceWindow.ForPicture(@"C:\b\board-dark.png") with { IsBoard = true }).PicturePath);
        Assert.Equal(@"C:\v\a.mp4", FavouriteMatcher.FromSource(SourceWindow.ForVideo(@"C:\v\a.mp4")).VideoPath);
    }
}

/// <summary>FR-131: a short GIF from a recording. (Not with the GPU engine tests, which run first: Media Foundation in the same process unsettles their windows.)</summary>
[Collection(DesktopWindowsCollection.Name)]
public class GifTests
{
    private static int[] Solid(int w, int h, Color c) => [.. Enumerable.Repeat(c.ToArgb(), w * h)];

    [Fact]
    public void Frames_come_back_exactly_with_their_timing_and_the_loop()
    {
        using var stream = new MemoryStream();
        using (var gif = new GifEncoder(stream, 32, 16))
        {
            gif.AddFrame(Solid(32, 16, Color.FromArgb(255, 200, 40, 40)), TimeSpan.FromMilliseconds(500));
            gif.AddFrame(Solid(32, 16, Color.FromArgb(255, 40, 80, 220)), TimeSpan.FromMilliseconds(100));
            Assert.Equal(2, gif.Frames);
        }
        byte[] bytes = stream.ToArray();
        Assert.Equal("GIF89a", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Contains("NETSCAPE2.0", Encoding.ASCII.GetString(bytes));
        Assert.Equal(0x3B, bytes[^1]);
        using var image = Image.FromStream(new MemoryStream(bytes));
        Assert.Equal(2, image.GetFrameCount(FrameDimension.Time));
        image.SelectActiveFrame(FrameDimension.Time, 0);
        Assert.Equal(Color.FromArgb(255, 200, 40, 40).ToArgb() & 0xF8F8F8, ((Bitmap)image).GetPixel(5, 5).ToArgb() & 0xF8F8F8);
        image.SelectActiveFrame(FrameDimension.Time, 1);
        Assert.Equal(Color.FromArgb(255, 40, 80, 220).ToArgb() & 0xF8F8F8, ((Bitmap)image).GetPixel(5, 5).ToArgb() & 0xF8F8F8);
        byte[] delays = image.GetPropertyItem(0x5100)!.Value!; // the frames' delays, in hundredths
        Assert.Equal(50, BitConverter.ToInt32(delays, 0));
        Assert.Equal(10, BitConverter.ToInt32(delays, 4));
    }

    [Fact]
    public void Many_colours_are_fitted_to_a_palette_closely()
    {
        const int w = 256, h = 64;
        var pixels = new int[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            pixels[y * w + x] = Color.FromArgb(255, x, (x * 3 + y) % 256, y * 4).ToArgb();
        (byte[] palette, byte[] indices) = GifEncoder.Quantize(pixels);
        Assert.Equal(768, palette.Length);
        double error = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            Color c = Color.FromArgb(pixels[i]);
            int p = indices[i] * 3;
            error += Math.Abs(c.R - palette[p]) + Math.Abs(c.G - palette[p + 1]) + Math.Abs(c.B - palette[p + 2]);
        }
        Assert.True(error / pixels.Length < 30, $"average error {error / pixels.Length:F1} a pixel");
    }

    [Fact]
    public void A_big_busy_frame_survives_the_code_table_filling_up()
    {
        const int w = 640, h = 360;
        var rng = new Random(7);
        var pixels = new int[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)).ToArgb();
        using var stream = new MemoryStream();
        using (var gif = new GifEncoder(stream, w, h)) gif.AddFrame(pixels, TimeSpan.FromMilliseconds(100));
        stream.Position = 0;
        using var image = new Bitmap(stream);
        Assert.Equal(new Size(w, h), image.Size);
        (byte[] palette, byte[] indices) = GifEncoder.Quantize(pixels);
        // The decoded picture is exactly the palette's colours at each pixel: nothing lost by the compression.
        foreach (int i in new[] { 0, 1, 4095, 4096, 50_000, w * h - 1 })
        {
            Color got = image.GetPixel(i % w, i / w);
            int p = indices[i] * 3;
            Assert.Equal((palette[p], palette[p + 1], palette[p + 2]), (got.R, got.G, got.B));
        }
    }

    [Fact]
    public void Sizes_and_misuse_are_handled()
    {
        Assert.Equal((720, 405), GifEncoder.FitWidth(1920, 1080, 720));
        Assert.Equal((400, 300), GifEncoder.FitWidth(400, 300, 720));
        Assert.Equal((0, 0), GifEncoder.FitWidth(0, 10, 720));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoder(new MemoryStream(), 0, 10));
        using var stream = new MemoryStream();
        var gif = new GifEncoder(stream, 4, 4);
        Assert.Throws<ArgumentException>(() => gif.AddFrame(new int[3], TimeSpan.FromSeconds(1)));
        gif.Finish();
        gif.Finish(); // twice is fine
        Assert.Throws<InvalidOperationException>(() => gif.AddFrame(new int[16], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_real_recording_becomes_a_gif()
    {
        if (!Direct3D.IsCaptureSupported) return;
        string mp4 = Path.Combine(Path.GetTempPath(), $"scp-gif-{Guid.NewGuid():N}.mp4");
        string gif = Path.ChangeExtension(mp4, ".gif");
        using (Vortice.Direct3D11.ID3D11Device device = Direct3D.CreateDevice()) ReplayBufferTests.WritePart(device, mp4, 2, 0.5f);
        try
        {
            int frames = GifMaker.Make(mp4, gif, TimeSpan.FromSeconds(10));
            Assert.InRange(frames, 10, 25); // about 2 seconds at 10 a second
            using (var image = Image.FromFile(gif))
            {
                Assert.Equal(640, image.Width);
                Assert.Equal(frames, image.GetFrameCount(FrameDimension.Time));
            }
            File.Delete(gif);

            RemoteWgcEngine.HelperExe = Path.Combine(AppContext.BaseDirectory, "ScreenCastPresenter.exe");
            Assert.Null(await GifMaker.MakeAsync(mp4, gif, 1, null, TimeSpan.FromMinutes(1))); // through the real helper process
            Assert.True(File.Exists(gif));
            Assert.NotNull(await GifMaker.MakeAsync(mp4 + ".missing.mp4", gif + "2.gif", 1, null, TimeSpan.FromMinutes(1)));
        }
        finally
        {
            RemoteWgcEngine.HelperExe = null;
            File.Delete(mp4);
            File.Delete(gif);
        }
    }
}

/// <summary>FR-130: copy the text in what's shown.</summary>
[Collection(DesktopWindowsCollection.Name)] // heavy (real PDF rendering, text recognition): never beside the timing tests
public class CopyTextTests
{
    [Fact]
    public void Lines_are_joined_tidily_and_capped()
    {
        var lines = new List<OcrLine>
        {
            new([new OcrWord("Quarterly", RectangleF.Empty), new OcrWord("results", RectangleF.Empty)]),
            new([new OcrWord(" ", RectangleF.Empty)]),
            new([new OcrWord("Q3\t2026", RectangleF.Empty)]),
        };
        Assert.Equal(["Quarterly results", "Q3 2026"], CopiedText.Lines(lines));
        Assert.Equal("a" + Environment.NewLine + "b", CopiedText.Join(["a", " ", "b "]));
        Assert.Equal(CopiedText.MaxCharacters, CopiedText.Join([new string('x', CopiedText.MaxCharacters + 50)]).Length);
        Assert.Equal("", CopiedText.Join([]));
        Assert.Equal("Copied one line of text.", CopiedText.Said(1));
        Assert.Equal("Copied 4 lines of text.", CopiedText.Said(4));
    }

    [Fact]
    public void The_text_in_a_real_picture_is_read()
    {
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null) return; // no recognizer here
        string png = Path.Combine(Path.GetTempPath(), $"scp-text-{Guid.NewGuid():N}.png");
        using (var picture = new Bitmap(900, 300))
        using (Graphics g = Graphics.FromImage(picture))
        using (var font = new Font("Segoe UI", 40, GraphicsUnit.Pixel))
        {
            g.Clear(Color.White);
            g.DrawString("Quarterly results", font, Brushes.Black, 40, 60);
            g.FillRectangle(Brushes.Black, 40, 160, 500, 60); // a covered part: nothing to read
            picture.Save(png, ImageFormat.Png);
        }
        var said = new List<string>();
        try
        {
            Assert.Equal(0, ScreenText.Run([CaptureProtocol.ReadTextSwitch, png], said.Add));
            Assert.Contains(said, l => l.StartsWith("line ", StringComparison.Ordinal) && l.Contains("Quarterly", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, ScreenText.Run([CaptureProtocol.ReadTextSwitch, png + ".missing"], said.Add));
        }
        finally
        {
            File.Delete(png);
        }
    }
}
