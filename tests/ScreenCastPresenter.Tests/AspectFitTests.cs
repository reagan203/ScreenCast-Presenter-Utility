using System.Drawing;
using ScreenCastPresenter.Capture;

namespace ScreenCastPresenter.Tests;

public class AspectFitTests
{
    [Fact]
    public void Same_aspect_fills_destination()
    {
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), AspectFit.Fit(new Size(1280, 720), new Size(1920, 1080)));
    }

    [Fact]
    public void Wider_source_gets_bars_top_and_bottom()
    {
        // 21:9 source on a 16:9 TV
        Rectangle r = AspectFit.Fit(new Size(2560, 1080), new Size(1920, 1080));
        Assert.Equal(1920, r.Width);
        Assert.Equal(810, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(135, r.Y);
    }

    [Fact]
    public void Taller_source_gets_bars_left_and_right()
    {
        // Portrait window on a 16:9 TV
        Rectangle r = AspectFit.Fit(new Size(600, 800), new Size(1920, 1080));
        Assert.Equal(1080, r.Height);
        Assert.Equal(810, r.Width);
        Assert.Equal(555, r.X);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Small_source_is_scaled_up_to_fit()
    {
        Assert.Equal(new Rectangle(240, 0, 1440, 1080), AspectFit.Fit(new Size(400, 300), new Size(1920, 1080)));
    }

    [Fact]
    public void Result_always_stays_inside_destination()
    {
        var dest = new Size(3840, 2160);
        for (int w = 1; w < 3000; w += 37)
        for (int h = 1; h < 3000; h += 41)
        {
            Rectangle r = AspectFit.Fit(new Size(w, h), dest);
            Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= dest.Width && r.Bottom <= dest.Height, $"{w}x{h} -> {r}");
        }
    }

    [Theory]
    [InlineData(0, 100, 1920, 1080)]
    [InlineData(100, 0, 1920, 1080)]
    [InlineData(100, 100, 0, 1080)]
    [InlineData(100, 100, 1920, -1)]
    public void Empty_sizes_give_empty_rectangle(int sw, int sh, int dw, int dh)
    {
        Assert.Equal(Rectangle.Empty, AspectFit.Fit(new Size(sw, sh), new Size(dw, dh)));
    }
}
