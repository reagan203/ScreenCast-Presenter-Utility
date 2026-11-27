using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// The whiteboard (FR-117): a blank board on the TV to draw on with the pen, highlighter, arrows,
/// boxes and text, from a private board window on the presenter's own screen. Dark by default (a
/// big white screen in a dark room is a flash), light when chosen. The drawing stays on the board
/// between drawing sessions until it's cleared or the board is put away, and can be saved as a
/// picture. Strokes are drawn here with GDI+ for the board window and the saved picture, exactly
/// where the TV draws them (the same fractions of the board).
/// </summary>
internal static class Whiteboard
{
    public const int Width = 1920, Height = 1080;
    public const string App = "Whiteboard";

    public static readonly Color Dark = Color.FromArgb(0x1B, 0x22, 0x1E);
    public static readonly Color Light = Color.FromArgb(0xF4, 0xF4, 0xF0);

    public static Color Board(bool light) => light ? Light : Dark;

    /// <summary>The pen's colour that shows on the board: white on dark, near-black on light.</summary>
    public static Color DefaultPen(bool light) => light ? Color.FromArgb(0x11, 0x11, 0x11) : Color.FromArgb(0xFA, 0xFA, 0xFA);

    /// <summary>The board's file in <paramref name="folder"/>: one for dark and one for light, made once.</summary>
    public static string? Render(string folder, bool light)
    {
        string path = Path.Combine(folder, light ? "board-light.png" : "board-dark.png");
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(folder);
            using var board = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(board)) g.Clear(Board(light));
            string temporary = path + ".tmp";
            board.Save(temporary, ImageFormat.Png);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>The board with its drawing, as a picture to save (the caller owns it).</summary>
    public static Bitmap Snapshot(IReadOnlyList<InkStroke> strokes, bool light, int width = Width, int height = Height)
    {
        var picture = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(picture);
        g.Clear(Board(light));
        DrawStrokes(g, new RectangleF(0, 0, width, height), strokes);
        return picture;
    }

    /// <summary>Draws <paramref name="strokes"/> into <paramref name="area"/> (their fractions are of it), like the TV does.</summary>
    public static void DrawStrokes(Graphics g, RectangleF area, IReadOnlyList<InkStroke> strokes)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        PointF At(PointF p) => new(area.X + p.X * area.Width, area.Y + p.Y * area.Height);
        foreach (InkStroke stroke in strokes)
        {
            if (stroke.Points.Count == 0) continue;
            Color colour = Color.FromArgb(stroke.Argb);
            float width = Math.Max(1, stroke.Width * area.Height);
            switch (stroke.Tool)
            {
                case InkTool.Text when stroke.Text is { Length: > 0 } text:
                {
                    using var font = new Font("Segoe UI Semibold", Math.Max(6, width), GraphicsUnit.Pixel);
                    using var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                    using var brush = new SolidBrush(colour);
                    PointF at = At(stroke.Points[0]);
                    g.DrawString(text, font, shadow, at.X + width * 0.05f, at.Y + width * 0.05f);
                    g.DrawString(text, font, brush, at);
                    break;
                }
                case InkTool.Arrow when stroke.Points.Count >= 2:
                {
                    using var pen = new Pen(colour, width) { StartCap = LineCap.Round, CustomEndCap = new AdjustableArrowCap(4, 5) };
                    g.DrawLine(pen, At(stroke.Points[0]), At(stroke.Points[^1]));
                    break;
                }
                case InkTool.Box when stroke.Points.Count >= 2:
                {
                    PointF a = At(stroke.Points[0]), b = At(stroke.Points[^1]);
                    using var pen = new Pen(colour, width) { LineJoin = LineJoin.Round };
                    g.DrawRectangle(pen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                    break;
                }
                case InkTool.Pen or InkTool.Highlighter:
                {
                    Color c = stroke.Tool == InkTool.Highlighter ? Color.FromArgb(110, colour) : colour;
                    using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    if (stroke.Points.Count == 1)
                    {
                        PointF p = At(stroke.Points[0]);
                        using var dot = new SolidBrush(c);
                        g.FillEllipse(dot, p.X - width / 2, p.Y - width / 2, width, width);
                    }
                    else g.DrawLines(pen, [.. stroke.Points.Select(At)]);
                    break;
                }
            }
        }
    }

    /// <summary>A file name for a saved board: "Whiteboard 2026-11-26 14.05.png", next to screenshots.</summary>
    public static string SaveName(DateTime now) => $"Whiteboard {now:yyyy-MM-dd HH.mm.ss}.png";
}
