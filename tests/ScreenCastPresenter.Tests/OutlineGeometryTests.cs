using System.Drawing;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

public class OutlineGeometryTests
{
    private static readonly Rectangle Laptop = new(0, 0, 1920, 1200);
    private static readonly Rectangle Tv = new(1920, 0, 1920, 1080);

    private static Rectangle Union(IEnumerable<Rectangle> rects) => rects.Aggregate(Rectangle.Union);

    [Fact]
    public void Floating_window_gets_four_strips_just_outside()
    {
        var window = new Rectangle(100, 100, 800, 600);
        List<Rectangle> strips = OutlineGeometry.Strips(window, 3, [Laptop]);
        Assert.Equal(4, strips.Count);
        Assert.Equal(Rectangle.Inflate(window, 3, 3), Union(strips));
        Assert.All(strips, s => Assert.False(s.IntersectsWith(window), $"{s} covers the window"));
    }

    [Fact]
    public void Maximised_window_gets_the_outline_just_inside_the_screen()
    {
        var window = new Rectangle(0, 0, 1920, 1152); // work area
        List<Rectangle> strips = OutlineGeometry.Strips(window, 3, [Laptop]);
        Assert.All(strips, s => Assert.True(Laptop.Contains(s), $"{s} is off screen"));
        Assert.Contains(strips, s => s.Top == 0 && s.Height == 3);        // top edge inside
        Assert.Contains(strips, s => s.Left == 0 && s.Width == 3);        // left edge inside
        Assert.Contains(strips, s => s.Top == 1152 && s.Height == 3);     // bottom edge outside (room below the work area)
    }

    [Fact]
    public void Nothing_is_ever_drawn_on_the_output_screen()
    {
        // Window straddles the laptop and the TV; only the laptop is allowed.
        var window = new Rectangle(1500, 200, 1000, 600);
        List<Rectangle> strips = OutlineGeometry.Strips(window, 3, [Laptop]);
        Assert.NotEmpty(strips);
        Assert.All(strips, s => Assert.False(s.IntersectsWith(Tv), $"{s} touches the TV"));
        // No line along the cut at the screen border: nothing at x = 1917..1920 spanning the window height.
        Assert.DoesNotContain(strips, s => s.Left >= 1917 && s.Height > 100);
    }

    [Fact]
    public void Window_spanning_two_allowed_screens_has_no_seam()
    {
        var window = new Rectangle(1500, 200, 1000, 600);
        List<Rectangle> strips = OutlineGeometry.Strips(window, 3, [Laptop, Tv]);
        // Vertical strips only at the window's real left and right edges.
        var verticals = strips.Where(s => s.Height > 100).ToList();
        Assert.Equal(2, verticals.Count);
        Assert.Contains(verticals, s => s.Right == 1500);
        Assert.Contains(verticals, s => s.Left == 2500);
    }

    [Fact]
    public void Window_entirely_on_the_output_screen_gets_no_outline() =>
        Assert.Empty(OutlineGeometry.Strips(new Rectangle(2000, 100, 800, 600), 3, [Laptop]));

    [Fact]
    public void Tiny_window_is_filled_rather_than_inverted()
    {
        List<Rectangle> strips = OutlineGeometry.Strips(new Rectangle(0, 0, 4, 4), 3, [Laptop]);
        Assert.All(strips, s => Assert.True(s.Width > 0 && s.Height > 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 10)]
    public void Empty_window_or_line_gives_nothing(int width, int thickness)
    {
        Assert.Empty(OutlineGeometry.Strips(new Rectangle(10, 10, width, 10), Math.Max(thickness, 1), [Laptop]));
        Assert.Empty(OutlineGeometry.Strips(new Rectangle(10, 10, 100, 100), 0, [Laptop]));
    }

    [Fact]
    public void Tab_sits_above_the_window_or_inside_when_there_is_no_room()
    {
        var size = new Size(120, 24);
        Rectangle above = OutlineGeometry.Tab(new Rectangle(100, 300, 800, 600), size, 3, 12, [Laptop])!.Value;
        Assert.Equal(300 - 3 - 24, above.Top);
        Assert.Equal(112, above.Left);

        Rectangle inside = OutlineGeometry.Tab(new Rectangle(0, 0, 1920, 1152), size, 3, 12, [Laptop])!.Value;
        Assert.Equal(3, inside.Top);
    }

    [Fact]
    public void Tab_is_not_placed_on_the_output_screen() =>
        Assert.Null(OutlineGeometry.Tab(new Rectangle(2000, 300, 800, 600), new Size(120, 24), 3, 12, [Laptop]));
}
