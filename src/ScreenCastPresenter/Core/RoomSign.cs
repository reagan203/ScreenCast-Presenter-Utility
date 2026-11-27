using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace ScreenCastPresenter.Core;

/// <summary>What the room sign says now: the room, the time, whether it's booked, and later bookings today.</summary>
internal sealed record RoomSignState(string Room, string Clock, bool Busy, string Status, string? Detail, IReadOnlyList<string> Later, bool Unavailable)
{
    /// <summary>Everything the picture shows, as one string: the picture is made again only when this changes.</summary>
    public string Key => string.Join('\n', [Room, Clock, Busy ? "1" : "0", Status, Detail ?? "", .. Later, Unavailable ? "?" : ""]);
}

/// <summary>
/// The room sign (FR-125): a lobby or door screen saying whether the room is free, from the room's
/// calendar file (the same one schedules read, FR-108), made on this PC and kept up to date each
/// minute without a fade. Meeting titles are left out unless chosen ("Booked until 11:00"), since a
/// sign in a corridor is seen by anyone passing.
/// </summary>
internal static class RoomSign
{
    public const int MaxName = 40;
    public const int Width = 1920, Height = 1080;
    public const int MaxLater = 3;
    public const string FileName = "room-sign.png";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The sign at <paramref name="now"/> (local time) from the bookings that start today or are still
    /// going (<paramref name="bookings"/>, in order); null bookings means the calendar can't be read.
    /// </summary>
    public static RoomSignState Build(string room, IReadOnlyList<CalendarOccurrence>? bookings, DateTime now, bool titles)
    {
        string name = room.Trim().Length > 0 ? room.Trim() : "This room";
        string clock = now.ToString("HH:mm", Inv);
        if (bookings is null) return new RoomSignState(name, clock, false, "Bookings unavailable", "Check the room's calendar file", [], true);

        DateTime endOfDay = now.Date.AddDays(1);
        var current = bookings.Where(b => b.Start <= now && b.End > now).OrderBy(b => b.Start).ToList();
        var later = bookings.Where(b => b.Start > now && b.Start < endOfDay).OrderBy(b => b.Start).ToList();
        if (current.Count > 0)
        {
            // Back-to-back bookings run together: "booked until" the end of the last one that joins on.
            DateTime until = current.Max(b => b.End);
            foreach (CalendarOccurrence next in later)
            {
                if (next.Start > until) break;
                if (next.End > until) until = next.End;
            }
            string what = titles && current[0].Summary.Trim().Length > 0 ? current[0].Summary.Trim() : "Booked";
            return new RoomSignState(name, clock, true, what, "until " + Time(until, now), Later(later.Where(b => b.Start >= until), titles, now), false);
        }
        string status = later.Count > 0 ? "Free until " + Time(later[0].Start, now) : "Free for the rest of the day";
        return new RoomSignState(name, clock, false, status, null, Later(later, titles, now), false);
    }

    private static List<string> Later(IEnumerable<CalendarOccurrence> later, bool titles, DateTime now) =>
        [.. later.Take(MaxLater).Select(b => $"{Time(b.Start, now)}–{Time(b.End, now)}  {(titles && b.Summary.Trim().Length > 0 ? TrayMenuPlan.Shorten(b.Summary.Trim(), 44) : "Booked")}")];

    /// <summary>"11:00", or "tomorrow 09:00" for a time after today.</summary>
    public static string Time(DateTime at, DateTime now) => at.Date == now.Date ? at.ToString("HH:mm", Inv) : at.Date == now.Date.AddDays(1) ? "tomorrow " + at.ToString("HH:mm", Inv) : at.ToString("d MMM HH:mm", Inv);

    /// <summary>The sign as a picture: dark, the room's name, a big Free (green) or Booked (red) panel, later bookings below.</summary>
    public static Bitmap Draw(RoomSignState s)
    {
        var sign = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(sign);
        g.Clear(Color.FromArgb(0x14, 0x14, 0x18));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var white = new SolidBrush(Color.White);
        using var grey = new SolidBrush(Color.FromArgb(0xC8, 0xC8, 0xD0));
        using var nameFont = new Font("Segoe UI Semibold", 84, GraphicsUnit.Pixel);
        using var clockFont = new Font("Segoe UI", 64, GraphicsUnit.Pixel);
        using var statusFont = new Font("Segoe UI Semibold", 120, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 64, GraphicsUnit.Pixel);
        using var laterFont = new Font("Segoe UI", 48, GraphicsUnit.Pixel);
        using var near = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var far = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        const float margin = 120;
        g.DrawString(s.Clock, clockFont, grey, new RectangleF(Width - margin - 400, 100, 400, 90), far);
        g.DrawString(s.Room, nameFont, white, new RectangleF(margin, 90, Width - 2 * margin - 420, 120), near);

        // A soft panel: green when free, red when booked, grey when the calendar can't be read. Dark tones, never a bright flash.
        Color panel = s.Unavailable ? Color.FromArgb(0x3A, 0x3A, 0x42) : s.Busy ? Color.FromArgb(0x8E, 0x1E, 0x22) : Color.FromArgb(0x1D, 0x6B, 0x3A);
        var box = new RectangleF(margin, 260, Width - 2 * margin, 380);
        using (var path = Rounded(box, 36))
        using (var fill = new SolidBrush(panel))
            g.FillPath(fill, path);
        g.DrawString(s.Status, statusFont, white, new RectangleF(box.X + 70, box.Y + 70, box.Width - 140, 170), near);
        if (s.Detail is { Length: > 0 } detail) g.DrawString(detail, detailFont, white, new RectangleF(box.X + 74, box.Y + 240, box.Width - 140, 100), near);

        float y = box.Bottom + 70;
        if (s.Later.Count > 0)
        {
            g.DrawString("Later today", laterFont, grey, new RectangleF(margin, y, Width - 2 * margin, 70), near);
            y += 80;
            foreach (string line in s.Later)
            {
                g.DrawString(line, laterFont, white, new RectangleF(margin, y, Width - 2 * margin, 70), near);
                y += 70;
            }
        }
        return sign;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Writes the sign to <paramref name="folder"/> when it says something new (written aside, then
    /// moved into place, so the helper never reads half a picture). The file's path, or null when it
    /// can't be written.
    /// </summary>
    public static string? Render(string folder, RoomSignState state, ref string? lastKey)
    {
        string path = Path.Combine(folder, FileName);
        if (lastKey == state.Key && File.Exists(path)) return path;
        try
        {
            Directory.CreateDirectory(folder);
            using Bitmap sign = Draw(state);
            string temporary = path + ".tmp";
            sign.Save(temporary, ImageFormat.Png);
            File.Move(temporary, path, overwrite: true);
            lastKey = state.Key;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }
}
