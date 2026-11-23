using System.Drawing;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

public class CropRegionTests
{
    [Fact]
    public void Selection_on_screen_becomes_fractions_of_the_client_area()
    {
        var client = new Rectangle(100, 50, 1000, 500);
        CropRegion crop = CropRegion.FromScreen(new Rectangle(350, 175, 500, 250), client)!;
        Assert.Equal(new CropRegion(0.25, 0.25, 0.5, 0.5), crop);
        Assert.Equal(new Rectangle(350, 175, 500, 250), crop.ToScreen(client));
    }

    [Fact]
    public void Crop_scales_with_the_window()
    {
        var crop = new CropRegion(0.25, 0.25, 0.5, 0.5);
        Assert.Equal(new Rectangle(250, 125, 500, 250), crop.ToPixels(new Size(1000, 500)));
        Assert.Equal(new Rectangle(500, 250, 1000, 500), crop.ToPixels(new Size(2000, 1000)));
    }

    [Fact]
    public void Selection_outside_the_window_is_clamped()
    {
        CropRegion crop = CropRegion.FromScreen(new Rectangle(-500, -500, 2000, 2000), new Rectangle(0, 0, 1000, 500))!;
        Assert.True(crop.IsFull);
    }

    [Fact]
    public void Dragging_backwards_still_works()
    {
        CropRegion crop = CropRegion.Create(0.6, 0.6, -0.4, -0.4)!;
        Assert.Equal(0.2, crop.X, 9);
        Assert.Equal(0.2, crop.Y, 9);
        Assert.Equal(0.4, crop.Width, 9);
        Assert.Equal(0.4, crop.Height, 9);
    }

    [Theory]
    [InlineData(0.5, 0.5, 0.001, 0.4)]     // too thin
    [InlineData(0.5, 0.5, 0.4, 0.0)]       // zero height
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    [InlineData(2, 2, 0.5, 0.5)]           // entirely outside
    public void Nonsense_gives_null(double x, double y, double w, double h) => Assert.Null(CropRegion.Create(x, y, w, h));

    [Fact]
    public void Pixels_are_always_inside_and_at_least_one_pixel()
    {
        var crop = new CropRegion(0.99, 0.99, 0.01, 0.01);
        Rectangle r = crop.ToPixels(new Size(10, 10));
        Assert.True(r.Width >= 1 && r.Height >= 1);
        Assert.True(new Rectangle(0, 0, 10, 10).Contains(r));
        Assert.Equal(Rectangle.Empty, crop.ToPixels(Size.Empty));
    }

    [Fact]
    public void Empty_client_gives_null() => Assert.Null(CropRegion.FromScreen(new Rectangle(0, 0, 10, 10), Rectangle.Empty));

    [Fact]
    public void Saved_crops_are_validated_on_load()
    {
        var s = new AppSettings();
        s.SavedCrops["POWERPNT"] = new CropRegion(0.1, 0.1, 0.5, 0.5);
        s.SavedCrops["bad"] = new CropRegion(0.5, 0.5, 0, 0);
        s.SavedCrops["full"] = CropRegion.Full;
        s.Normalize();
        Assert.True(s.SavedCrops.ContainsKey("powerpnt")); // app names match case-insensitively
        Assert.False(s.SavedCrops.ContainsKey("bad"));
        Assert.False(s.SavedCrops.ContainsKey("full"));
    }
}
