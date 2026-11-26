using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

public class CaptureProtocolTests
{
    [Fact]
    public void Window_source_round_trips()
    {
        SourceWindow source = CaptureProtocol.ParseSource(CaptureProtocol.SourceSpec(TestData.Window(123456, "x")))!;
        Assert.Equal(123456, source.Handle);
        Assert.False(source.IsScreen);
    }

    [Fact]
    public void Screen_source_round_trips_against_the_connected_screens()
    {
        string spec = CaptureProtocol.SourceSpec(SourceWindow.ForScreen(TestData.Tv));
        Assert.Equal(@"screen:\\.\DISPLAY2", spec);
        SourceWindow source = CaptureProtocol.ParseSource(spec, [TestData.Laptop, TestData.Tv])!;
        Assert.Equal(TestData.Tv, source.Screen);
        Assert.Null(CaptureProtocol.ParseSource(spec, [TestData.Laptop])); // unplugged
    }

    [Theory]
    [InlineData("")]
    [InlineData("window:")]
    [InlineData("window:0")]
    [InlineData("window:abc")]
    [InlineData("tab:12")]
    public void Bad_source_specs_give_null(string spec) => Assert.Null(CaptureProtocol.ParseSource(spec, []));

    [Fact]
    public void Crop_round_trips_exactly()
    {
        var crop = new CropRegion(0.1234567890123, 0.2, 0.33333333333333, 0.5);
        CropRegion back = CaptureProtocol.ParseCrop(CaptureProtocol.Crop(crop))!;
        Assert.Equal(crop.X, back.X, 12);
        Assert.Equal(crop.Y, back.Y, 12);
        Assert.Equal(crop.Width, back.Width, 12);
        Assert.Equal(crop.Height, back.Height, 12);
        Assert.Null(CaptureProtocol.ParseCrop(CaptureProtocol.Crop(null)));
        Assert.Null(CaptureProtocol.ParseCrop("1,2"));
        Assert.Null(CaptureProtocol.ParseCrop("a,b,c,d"));
    }

    [Fact]
    public void Start_command_has_every_field()
    {
        string start = CaptureProtocol.Start(4242, TestData.Window(77, "x") with { Crop = new CropRegion(0, 0, 0.5, 0.5) }, true, false, new Size(1920, 1080));
        string[] p = start.Split(' ');
        Assert.Equal(["start", "4242", "window:77", "1", "0", "0,0,0.5,0.5", "1920", "1080", "0", "0"], p); // automatic hiding off, solid
        string withAutoHide = CaptureProtocol.Start(4242, TestData.Window(77, "x"), true, false, new Size(1920, 1080), SensitiveKinds.Emails | SensitiveKinds.Cards, RedactStyle.Blurred);
        string[] q = withAutoHide.Split(' ');
        Assert.Equal((SensitiveKinds.Emails | SensitiveKinds.Cards, RedactStyle.Blurred), CaptureProtocol.ParseAutoHide(q[8], q[9]));
    }

    [Fact]
    public void Message_with_spaces_and_newlines_stays_on_one_line()
    {
        // The line break survives as U+2028, which the helper draws as a new line (a countdown under the message).
        string line = CaptureProtocol.Message("We'll be\nright back\r\n4:59", 0.5);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("message 0.5 We'll be\u2028right back\u20284:59", line);
    }

    [Theory]
    [InlineData("NaN", 0)]
    [InlineData("1e400", 0)]
    [InlineData("0.25", 0.25)]
    public void Numbers_are_parsed_safely(string text, double expected) => Assert.Equal(expected, CaptureProtocol.ParseDouble(text));
}

public class CanvasSizeTests
{
    [Fact]
    public void Small_source_on_big_screen_uses_a_small_canvas_with_the_screen_shape()
    {
        Size canvas = WgcEngine.CanvasSize(new Size(1920, 1200), new Size(900, 562));
        Assert.True(canvas.Width < 1000 && canvas.Height < 650, canvas.ToString());
        Assert.Equal(1920.0 / 1200, (double)canvas.Width / canvas.Height, 2); // DWM stretches it without distortion
    }

    [Fact]
    public void Source_bigger_than_screen_uses_the_full_screen() =>
        Assert.Equal(new Size(1920, 1080), WgcEngine.CanvasSize(new Size(1920, 1080), new Size(3840, 2160)));

    [Fact]
    public void Tiny_source_keeps_at_least_a_quarter_so_text_stays_readable() =>
        Assert.Equal(new Size(480, 270), WgcEngine.CanvasSize(new Size(1920, 1080), new Size(40, 40)));

    [Fact]
    public void Unknown_source_starts_small() => Assert.Equal(new Size(480, 300), WgcEngine.CanvasSize(new Size(1920, 1200), null));
}

public class SavedFilesTests
{
    [Fact]
    public void Names_follow_the_snipping_tool_style_and_never_overwrite()
    {
        var now = new DateTime(2026, 11, 23, 13, 45, 1);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:\\x", "Screenshot 2026-11-23 134501.png"),
            Path.Combine("C:\\x", "Screenshot 2026-11-23 134501 (2).png"),
        };
        Assert.Equal(Path.Combine("C:\\x", "Screenshot 2026-11-23 134501 (3).png"), SavedFiles.NextPath("C:\\x", "Screenshot", now, ".png", existing.Contains));
        Assert.Equal(Path.Combine("C:\\x", "Screen Recording 2026-11-23 134501.mp4"), SavedFiles.NextPath("C:\\x", "Screen Recording", now, ".mp4", _ => false));
    }

    [Fact]
    public void Failures_are_explained_in_plain_language()
    {
        Assert.Contains("isn't allowed", SavedFiles.Explain(new UnauthorizedAccessException()));
        Assert.Contains("disk is full", SavedFiles.Explain(new IOException("x", unchecked((int)0x80070070))));
    }
}
