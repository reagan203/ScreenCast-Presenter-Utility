using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Pictures as sources (FR-103): a picture file shown on the TV like a window, with fades, zoom,
/// marks and automatic hiding, without opening an app. For lobby screens, notices and slides
/// exported as images.
/// </summary>
internal static class PictureSource
{
    /// <summary>Bigger files are refused: a lobby picture is a photo or an exported slide.</summary>
    public const long MaxBytes = 40L * 1024 * 1024;

    /// <summary>Pictures are shown at up to 4K: bigger ones are scaled down when read.</summary>
    public const int MaxSide = 3840;

    /// <summary>At most this many pictures from one folder.</summary>
    public const int MaxInFolder = 200;

    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public const string Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";

    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="path"/> can't be shown, or null when it can.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a picture.";
        if (!IsPicture(path)) return "Choose a PNG, JPEG, BMP, GIF or TIFF picture.";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return $"“{Path.GetFileName(path)}” can't be found.";
            if (info.Length > MaxBytes) return $"“{info.Name}” is too big (over {MaxBytes / (1024 * 1024)} MB).";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "That picture can't be opened.";
        }
    }

    /// <summary>
    /// The picture, read into memory (the file isn't kept open or locked) and scaled down to
    /// <see cref="MaxSide"/>, as 32-bit premultiplied pixels; null when it can't be read.
    /// </summary>
    public static Bitmap? Load(string path)
    {
        if (Problem(path) is not null) return null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            double scale = Math.Min(1, (double)MaxSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(bitmap);
            g.Clear(Color.Black); // a transparent picture shows on black, like the TV
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, new Rectangle(0, 0, w, h));
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException or UnauthorizedAccessException or ExternalException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>The pictures in a folder, by name (as the file explorer sorts them, ignoring case), at most <see cref="MaxInFolder"/>.</summary>
    public static List<string> InFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        try
        {
            if (!Directory.Exists(folder)) return [];
            return [.. Directory.EnumerateFiles(folder).Where(IsPicture).Order(StringComparer.OrdinalIgnoreCase).Take(MaxInFolder)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>A picture's name for messages and the tray: its file name without the extension.</summary>
    public static string Name(string path) => Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name ? name : path;
}

/// <summary>
/// Text cards (FR-103): a few lines of words ("Welcome, Acme", "Lunch is at 12:30") made into a
/// picture on the PC, then shown like any picture. The first line is the title.
/// </summary>
internal static class TextCard
{
    public const int Width = 1920, Height = 1080;
    public const int MaxLength = 300, MaxLines = 6;

    /// <summary>Words tidied: line breaks kept (at most <see cref="MaxLines"/>), each line trimmed, empty lines dropped, at most <see cref="MaxLength"/> characters.</summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string joined = string.Join('\n', lines.Select(l => new string([.. l.Where(c => !char.IsControl(c))]).Trim()).Where(l => l.Length > 0).Take(MaxLines));
        return joined.Length <= MaxLength ? joined : joined[..MaxLength].TrimEnd();
    }

    /// <summary>The title: the first line.</summary>
    public static string Title(string text) => Tidy(text).Split('\n')[0];

    /// <summary>Where a card's picture is kept: named from its words, so the same words make the same file.</summary>
    public static string PathFor(string folder, string text)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Tidy(text))))[..16];
        return Path.Combine(folder, $"card-{hash}.png");
    }

    /// <summary>The card as a picture file in <paramref name="folder"/> (made once); null when the words are empty or it can't be written.</summary>
    public static string? Render(string folder, string text)
    {
        string tidy = Tidy(text);
        if (tidy.Length == 0) return null;
        string path = PathFor(folder, tidy);
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(folder);
            using Bitmap card = Draw(tidy);
            string temporary = path + ".tmp";
            card.Save(temporary, ImageFormat.Png);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>The card: dark background, the title large and white, the other lines smaller below, all centred.</summary>
    public static Bitmap Draw(string text)
    {
        string[] lines = Tidy(text).Split('\n');
        var card = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(card);
        g.Clear(Color.FromArgb(0x14, 0x14, 0x18));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new SolidBrush(EffectStyle.ClickPrimary);
        using var titleFont = new Font("Segoe UI Semibold", 96, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", 52, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
        float margin = 140, width = Width - 2 * margin;
        SizeF titleSize = g.MeasureString(lines[0], titleFont, (int)width, format);
        float bodyHeight = 0;
        var bodies = new List<(string Text, SizeF Size)>();
        foreach (string line in lines.Skip(1))
        {
            SizeF s = g.MeasureString(line, bodyFont, (int)width, format);
            bodies.Add((line, s));
            bodyHeight += s.Height + 12;
        }
        float total = Math.Min(Height - 2 * 80f, titleSize.Height + (bodies.Count > 0 ? 48 + bodyHeight : 0));
        float y = (Height - total) / 2;
        g.FillRectangle(accent, (Width - 160) / 2f, y - 48, 160, 10); // a short accent bar above the title
        g.DrawString(lines[0], titleFont, Brushes.White, new RectangleF(margin, y, width, titleSize.Height + 4), format);
        y += titleSize.Height + 48;
        using var grey = new SolidBrush(Color.FromArgb(0xD0, 0xD0, 0xD8));
        foreach ((string line, SizeF size) in bodies)
        {
            if (y + size.Height > Height - 60) break;
            g.DrawString(line, bodyFont, grey, new RectangleF(margin, y, width, size.Height + 4), format);
            y += size.Height + 12;
        }
        return card;
    }
}
