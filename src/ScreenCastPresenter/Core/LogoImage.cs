using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Your logo on the blank screen (FR-80): a PNG, JPEG, BMP or GIF picked in Settings, kept as a
/// copy next to the settings (so moving or deleting the original doesn't take it away), and read
/// back as a premultiplied 32-bit picture no bigger than <see cref="MaxSide"/> pixels a side.
/// </summary>
internal static class LogoImage
{
    /// <summary>Bigger files are refused: a logo is small, and a huge one would slow the blank screen down.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Plenty for a 4K TV, where the logo takes at most 40% of the width.</summary>
    public const int MaxSide = 1600;

    /// <summary>File types the Settings picker offers.</summary>
    public const string Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";

    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    /// <summary>Where the copy is kept, next to the settings file.</summary>
    public static string CopyPath(string settingsFolder, string original) =>
        Path.Combine(settingsFolder, "blank-logo" + Path.GetExtension(original).ToLowerInvariant());

    /// <summary>Why <paramref name="path"/> can't be a logo, or null when it can.</summary>
    public static string? Problem(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return "Choose a PNG, JPEG, BMP or GIF picture.";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "That picture can't be found.";
            if (info.Length > MaxBytes) return "That picture is too big (over 10 MB). Choose a smaller one.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "That picture can't be opened.";
        }
        using Bitmap? test = Load(path);
        return test is null ? "That file isn't a picture this app can show." : null;
    }

    /// <summary>The picture, premultiplied and at most <see cref="MaxSide"/> a side; null if it can't be read.</summary>
    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes || info.Length == 0) return null;
            // Read the bytes first: GDI+ keeps a file it opened locked for as long as the picture lives.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width <= 0 || image.Height <= 0) return null;
            double scale = Math.Min(1, (double)MaxSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
            var copy = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, new Rectangle(0, 0, w, h));
            }
            return copy;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or OutOfMemoryException or ExternalException or NotSupportedException)
        {
            return null;
        }
    }
}
