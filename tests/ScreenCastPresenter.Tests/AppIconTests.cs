using ScreenCastPresenter.UI;

namespace ScreenCastPresenter.Tests;

public class AppIconTests
{
    [Fact]
    public void The_icon_is_embedded_with_every_size_windows_asks_for()
    {
        // The sizes in the .ico directory (Explorer reads the 256 px entry directly).
        using Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ScreenCastPresenter.app.ico")!;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16();
        Assert.Equal(1, reader.ReadUInt16()); // an icon, not a cursor
        int count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            byte width = reader.ReadByte();
            sizes.Add(width == 0 ? 256 : width);
            reader.ReadBytes(15);
        }
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 128, 256], sizes);

        foreach (int size in new[] { 16, 20, 24, 32, 48 }) // what the app itself loads
        {
            using Icon icon = AppIcon.Load(new Size(size, size));
            Assert.Equal(size, icon.Width);
        }
    }

    [Fact]
    public void Tray_icons_show_a_status_dot_in_the_corner()
    {
        using Icon plain = AppIcon.WithStatus(null);
        using Icon live = AppIcon.WithStatus(Color.FromArgb(0x22, 0xC5, 0x5E));
        using Bitmap a = plain.ToBitmap(), b = live.ToBitmap();
        Point corner = new(a.Width - a.Width / 4, a.Height - a.Height / 4);
        Assert.NotEqual(a.GetPixel(corner.X, corner.Y), b.GetPixel(corner.X, corner.Y));
        Color centre = a.GetPixel(a.Width / 2, a.Height / 2);
        Assert.True(centre.R > 200 && centre.G > 150 && centre.B < 120, $"the yellow window should be in the middle, got {centre}"); // brand cue
    }
}
