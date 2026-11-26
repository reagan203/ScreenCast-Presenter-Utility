using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-72: a small picture in a corner of the TV.</summary>
public class InsetTests
{
    private static readonly SizeF Tv = new(1920, 1080);
    private static readonly SizeF Webcam = new(1280, 720);

    [Fact]
    public void It_sits_in_the_chosen_corner_with_a_margin()
    {
        float margin = 1080 * InsetLayout.Margin;
        RectangleF br = InsetLayout.Place(Tv, Webcam, InsetCorner.BottomRight, 28);
        Assert.Equal(1920 - margin, br.Right, 2);
        Assert.Equal(1080 - margin, br.Bottom, 2);
        RectangleF tl = InsetLayout.Place(Tv, Webcam, InsetCorner.TopLeft, 28);
        Assert.Equal(margin, tl.Left, 2);
        Assert.Equal(margin, tl.Top, 2);
        Assert.Equal(margin, InsetLayout.Place(Tv, Webcam, InsetCorner.BottomLeft, 28).Left, 2);
        Assert.Equal(margin, InsetLayout.Place(Tv, Webcam, InsetCorner.TopRight, 28).Top, 2);
    }

    [Fact]
    public void Its_width_is_a_share_of_the_TV_and_its_shape_follows_what_it_shows()
    {
        RectangleF r = InsetLayout.Place(Tv, Webcam, InsetCorner.BottomRight, 20);
        Assert.Equal(384, r.Width, 2);
        Assert.Equal(216, r.Height, 2); // 16:9 like the webcam
    }

    [Fact]
    public void Something_tall_is_capped_at_part_of_the_TVs_height()
    {
        RectangleF r = InsetLayout.Place(Tv, new SizeF(600, 2000), InsetCorner.BottomRight, 36);
        Assert.Equal(1080 * InsetLayout.MaxHeight, r.Height, 2);
        Assert.Equal(r.Height * 600 / 2000, r.Width, 2);
        Assert.True(r.Top >= 0);
    }

    [Fact]
    public void Nothing_to_place_is_empty()
    {
        Assert.True(InsetLayout.Place(SizeF.Empty, Webcam, InsetCorner.BottomRight, 28).IsEmpty);
        Assert.True(InsetLayout.Place(Tv, SizeF.Empty, InsetCorner.BottomRight, 28).IsEmpty);
    }

    [Theory]
    [InlineData("TopLeft", nameof(InsetCorner.TopLeft))]
    [InlineData("bottomleft", nameof(InsetCorner.BottomLeft))]
    [InlineData("middle", nameof(InsetCorner.BottomRight))]
    [InlineData(null, nameof(InsetCorner.BottomRight))]
    [InlineData("7", nameof(InsetCorner.BottomRight))]
    public void Corners_from_a_file_are_understood_or_defaulted(string? text, string expected) =>
        Assert.Equal(expected, InsetLayout.ParseCorner(text).ToString());

    [Fact]
    public void Settings_keep_an_offered_corner_and_size()
    {
        var s = new AppSettings { InsetCorner = "nowhere", InsetSize = 90 };
        s.Normalize();
        Assert.Equal(nameof(InsetCorner.BottomRight), s.InsetCorner);
        Assert.Equal(36, s.InsetSize);
        Assert.Equal(28, new AppSettings().InsetSize);
    }

    [Fact]
    public void The_helper_is_told_what_to_show_small_and_where()
    {
        var webcam = SourceWindow.ForCamera(new CameraDevice(@"\\?\USB#cam", "Webcam"));
        string line = CaptureProtocol.Inset(webcam, InsetCorner.TopLeft, 36);
        string[] p = line.Split(' ', 3);
        Assert.Equal("inset", p[0]);
        Assert.Equal(@"\\?\USB#cam", CaptureProtocol.ParseSource(p[1], [])!.Camera!.Id);
        Assert.Equal((InsetCorner.TopLeft, 36), CaptureProtocol.ParseInsetPlace(p[2]));
        Assert.Equal("inset none", CaptureProtocol.Inset(null, InsetCorner.TopLeft, 36));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9 28")]
    [InlineData("x y")]
    public void An_odd_place_falls_back_to_the_defaults(string text) =>
        Assert.Equal((InsetCorner.BottomRight, InsetLayout.DefaultSize), CaptureProtocol.ParseInsetPlace(text));

    [Fact]
    public void Words_for_messages() => Assert.Equal("top left", InsetLayout.Words(InsetCorner.TopLeft));
}
