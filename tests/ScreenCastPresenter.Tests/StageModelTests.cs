using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>Hidden parts (FR-74): the boxes, how they're kept, and how they travel to the helper.</summary>
public class HiddenPartsModelTests
{
    private static SourceWindow Mail => TestData.Window(5, "Inbox - Outlook", "OUTLOOK");

    [Fact]
    public void Boxes_are_kept_per_app_per_screen_and_never_for_a_camera()
    {
        Assert.Equal("OUTLOOK", Redactions.Key(Mail));
        Assert.Equal(@"screen:\\.\DISPLAY1", Redactions.Key(SourceWindow.ForScreen(TestData.Laptop)));
        Assert.Null(Redactions.Key(SourceWindow.ForCamera(new CameraDevice("cam-1", "Webcam"))));
        Assert.Null(Redactions.Key(TestData.Window(6, "Untitled", "")));
    }

    [Fact]
    public void Adding_clearing_and_looking_up_boxes()
    {
        var s = new AppSettings();
        CropRegion box = CropRegion.Create(0.1, 0.1, 0.3, 0.2)!;
        Assert.Empty(Redactions.For(s, Mail));
        Assert.Null(Redactions.PartsFor(s, Mail));
        Assert.True(Redactions.Add(s, Mail, box));
        Assert.Equal([box], Redactions.For(s, Mail));
        Assert.Equal([box], Redactions.For(s, TestData.Window(7, "Calendar - Outlook", "outlook"))); // the same app, any window, any case
        s.HiddenPartStyle = RedactStyle.Blurred;
        Assert.Equal(RedactStyle.Blurred, Redactions.PartsFor(s, Mail)!.Style);
        for (int i = 1; i < Redactions.MaxPerSource; i++) Assert.True(Redactions.Add(s, Mail, box));
        Assert.False(Redactions.Add(s, Mail, box)); // full
        Assert.Equal(Redactions.MaxPerSource, Redactions.For(s, Mail).Count);
        Assert.True(Redactions.Clear(s, Mail));
        Assert.False(Redactions.Clear(s, Mail));
        Assert.False(Redactions.Add(s, SourceWindow.ForCamera(new CameraDevice("c", "Cam")), box));
    }

    [Fact]
    public void A_settings_file_is_tidied_nonsense_dropped_and_lists_capped()
    {
        var dirty = new Dictionary<string, List<CropRegion>>
        {
            [""] = [CropRegion.Create(0, 0, 0.5, 0.5)!],
            ["Empty"] = [],
            ["chrome"] = [.. Enumerable.Repeat(CropRegion.Create(0.2, 0.2, 0.2, 0.2)!, 20)],
        };
        Dictionary<string, List<CropRegion>> tidy = Redactions.NormalizeAll(dirty);
        Assert.Equal(["chrome"], tidy.Keys);
        Assert.Equal(Redactions.MaxPerSource, tidy["CHROME"].Count); // case-insensitive
        Assert.Empty(Redactions.Normalize(null));
        Assert.Empty(Redactions.Normalize([null, new CropRegion(double.NaN, 0, 1, 1), new CropRegion(0.5, 0.5, 0, 0)]));
        Assert.Equal(CropRegion.Full, Assert.Single(Redactions.Normalize([new CropRegion(-1, 0, 5, 5)]))); // clamped onto the window, never dropped
    }

    [Fact]
    public void Covering_everything_shown_is_noticed_through_a_crop_too()
    {
        Assert.True(Redactions.CoversAll(CropRegion.Full, null));
        Assert.False(Redactions.CoversAll(CropRegion.Create(0, 0, 0.5, 1)!, null));
        CropRegion crop = CropRegion.Create(0.5, 0, 0.5, 1)!;
        Assert.True(Redactions.CoversAll(CropRegion.Create(0.4, 0, 0.6, 1)!, crop));
        Assert.False(Redactions.CoversAll(CropRegion.Create(0.6, 0, 0.4, 1)!, crop));
    }

    [Fact]
    public void Boxes_and_the_look_survive_saving_and_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.HiddenParts["OUTLOOK"] = [CropRegion.Create(0.1, 0.2, 0.3, 0.4)!];
            store.Current.HiddenPartStyle = RedactStyle.Blurred;
            Assert.True(store.Save());
            var again = new SettingsStore(store.FilePath);
            AppSettings loaded = again.Load();
            Assert.Equal(RedactStyle.Blurred, loaded.HiddenPartStyle);
            Assert.Equal(CropRegion.Create(0.1, 0.2, 0.3, 0.4), Assert.Single(loaded.HiddenParts["outlook"]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_look_written_by_a_newer_version_falls_back_to_the_safest()
    {
        var s = new AppSettings { HiddenPartLook = "Pixelated" }.Normalize();
        Assert.Equal(RedactStyle.Solid, s.HiddenPartStyle);
        Assert.Equal("Solid", s.HiddenPartLook);
    }

    [Fact]
    public void Boxes_travel_to_the_helper_and_back_unchanged()
    {
        CropRegion a = CropRegion.Create(0.1, 0.2, 0.3, 0.4)!, b = CropRegion.Create(0.5, 0.5, 0.25, 0.25)!;
        string line = CaptureProtocol.Redact([a, b], RedactStyle.Blurred);
        Assert.StartsWith("redact 1 ", line);
        var parsed = CaptureProtocol.ParseRedact(line["redact ".Length..])!.Value;
        Assert.Equal(RedactStyle.Blurred, parsed.Style);
        Assert.Equal(2, parsed.Regions.Count);
        Assert.Equal(a.X, parsed.Regions[0].X, 4);
        Assert.Equal(b.Height, parsed.Regions[1].Height, 4);
        Assert.Equal("redact none", CaptureProtocol.Redact([], RedactStyle.Solid));
        Assert.Null(CaptureProtocol.ParseRedact("none"));
        Assert.Null(CaptureProtocol.ParseRedact("9 0,0,1,1"));         // unknown look
        Assert.Null(CaptureProtocol.ParseRedact("0 nonsense"));
        Assert.Equal(Redactions.MaxPerSource, CaptureProtocol.ParseRedact("0 " + string.Join(' ', Enumerable.Repeat("0.1,0.1,0.2,0.2", 30)))!.Value.Regions.Count);
    }

    [Fact]
    public void Screenshots_and_recordings_cover_the_boxes_where_they_fall_in_the_picture()
    {
        var client = new Rectangle(10, 20, 800, 600);
        // The whole client area shown: a box at the top-left quarter lands there, shifted to the picture's origin.
        Assert.Equal([new Rectangle(0, 0, 400, 300)], StillCapture.Covers(client, client, [CropRegion.Create(0, 0, 0.5, 0.5)!]));
        // Cropped to the right half: a box over the middle is cut to the part that's in the picture.
        var rightHalf = new Rectangle(410, 20, 400, 600);
        Assert.Equal([new Rectangle(0, 150, 200, 300)], StillCapture.Covers(client, rightHalf, [CropRegion.Create(0.25, 0.25, 0.5, 0.5)!]));
        // A box wholly outside the crop isn't in the picture at all.
        Assert.Empty(StillCapture.Covers(client, rightHalf, [CropRegion.Create(0, 0, 0.3, 0.3)!]));
        Assert.Empty(StillCapture.Covers(client, client, []));
    }

    [Fact]
    public void A_recording_is_told_the_boxes_and_keeps_a_path_with_spaces_last()
    {
        CropRegion a = CropRegion.Create(0.1, 0.2, 0.3, 0.4)!, b = CropRegion.Create(0.5, 0.5, 0.25, 0.25)!;
        var request = new Recording.RecordingRequest(TestData.Window(4242, "Inbox"), true, Recording.MeetingSound.None, false, @"C:\My Videos\Screen Recording 1.mp4", [a, b]);
        Recording.RecordingRequest back = Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request))!;
        Assert.Equal(@"C:\My Videos\Screen Recording 1.mp4", back.Path);
        Assert.Equal([a, b], back.Hidden);
        Recording.RecordingRequest none = Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request with { Hidden = null }))!;
        Assert.Empty(none.Hidden!);
        Assert.Contains(" - ", Recording.RecordingProtocol.Start(request with { Hidden = [] }));
    }

    [Fact]
    public void A_solid_box_is_fully_opaque_at_every_brightness_so_nothing_shows_through()
    {
        Color black = UI.Theme.OutputBlack;
        Assert.Equal(Color.FromArgb(255, black), EffectStyle.SolidOver(black, EffectStyle.RedactFill, 0));
        Assert.Equal(Color.FromArgb(255, EffectStyle.RedactFill), EffectStyle.SolidOver(black, EffectStyle.RedactFill, 1));
        for (float o = 0; o <= 1; o += 0.05f) Assert.Equal(255, EffectStyle.SolidOver(black, EffectStyle.RedactFill, o).A);
        Assert.Equal(Color.FromArgb(255, black), EffectStyle.SolidOver(black, EffectStyle.RedactFill, float.NaN));
    }
}

/// <summary>Arrows, boxes and text on the TV (FR-78).</summary>
public class ShapesAndTextTests
{
    [Fact]
    public void A_shape_keeps_its_start_and_its_end_follows_the_drag()
    {
        var arrow = new InkStroke(1, InkTool.Arrow, Color.Red.ToArgb(), 0.006f);
        Assert.True(arrow.IsShape);
        arrow.SetEnd(new PointF(0.1f, 0.1f));
        arrow.SetEnd(new PointF(0.5f, 0.5f));
        arrow.SetEnd(new PointF(0.7f, 0.6f));
        Assert.Equal([new PointF(0.1f, 0.1f), new PointF(0.7f, 0.6f)], arrow.Points);
        arrow.SetEnd(new PointF(float.NaN, 0));
        Assert.Equal(new PointF(0.7f, 0.6f), arrow.Points[^1]);
        Assert.False(new InkStroke(2, InkTool.Pen, 0, 0.01f).IsShape);
        Assert.False(new InkStroke(3, InkTool.Text, 0, 0.01f).IsShape);
    }

    [Fact]
    public void Text_is_one_tidy_line_of_at_most_120_characters()
    {
        var text = new InkStroke(1, InkTool.Text, 0, 0.04f) { Text = "  Look\there\r\nnow " + (char)0x2028 + "!" };
        Assert.Equal("Look here  now  !", text.Text);
        text.Text = new string('x', 500);
        Assert.Equal(InkStroke.MaxTextLength, text.Text!.Length);
        Assert.Null(new InkStroke(2, InkTool.Text, 0, 0.04f) { Text = null }.Text);
    }

    [Fact]
    public void The_drawing_keeps_text_and_drops_a_shape_that_was_only_clicked()
    {
        var drawing = new InkDrawing();
        InkStroke text = drawing.Begin(InkTool.Text, Color.White, 0.045f, "Revenue");
        InkStroke pen = drawing.Begin(InkTool.Pen, Color.Red, 0.004f, "ignored for a pen");
        Assert.Equal("Revenue", text.Text);
        Assert.Null(pen.Text);
        InkStroke box = drawing.Begin(InkTool.Box, Color.Red, 0.006f);
        Assert.True(drawing.Remove(box.Id));
        Assert.False(drawing.Remove(box.Id));
        Assert.Equal([text, pen], drawing.Strokes);
    }

    [Fact]
    public void Text_and_shapes_reach_the_helper_and_back()
    {
        var text = new InkStroke(9, InkTool.Text, Color.Yellow.ToArgb(), 0.045f) { Text = "Q3 revenue: up 12%" };
        string line = CaptureProtocol.InkStart(text);
        InkStroke parsed = CaptureHost.ParseInkStart(line)!;
        Assert.Equal(InkTool.Text, parsed.Tool);
        Assert.Equal("Q3 revenue: up 12%", parsed.Text);
        Assert.Null(CaptureHost.ParseInkStart(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ink-start 9 {(int)InkTool.Text} 0 0.045")));   // text with no words
        Assert.Null(CaptureHost.ParseInkStart(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ink-start 9 {(int)InkTool.Text} 0 0.045    "))); // only spaces
        var arrow = new InkStroke(3, InkTool.Arrow, Color.Red.ToArgb(), 0.006f);
        Assert.Equal(InkTool.Arrow, CaptureHost.ParseInkStart(CaptureProtocol.InkStart(arrow))!.Tool);
        Assert.Equal("ink-set 3 0.1,0.2 0.5,0.6", CaptureProtocol.InkSet(3, [new PointF(0.1f, 0.2f), new PointF(0.5f, 0.6f)]));
    }

    [Fact]
    public void An_arrow_head_points_back_along_the_line_and_a_dot_has_none()
    {
        Layouts.ArrowHeadLines head = Layouts.ArrowHead(new Vector2(0, 0), new Vector2(100, 0), 20)!.Value;
        Assert.Equal(new Vector2(100, 0), head.Tip);
        foreach (Vector2 end in new[] { head.Left, head.Right })
        {
            Assert.True(end.X < 100, "the head's lines go back towards the tail");
            Assert.Equal(20, Vector2.Distance(head.Tip, end), 2);
        }
        Assert.Equal(-head.Left.Y, head.Right.Y, 3); // symmetric
        Assert.Null(Layouts.ArrowHead(new Vector2(5, 5), new Vector2(5, 5), 20));
        Layouts.ArrowHeadLines small = Layouts.ArrowHead(new Vector2(0, 0), new Vector2(10, 0), 20)!.Value;
        Assert.True(Vector2.Distance(small.Tip, small.Left) <= 6.01f, "a short arrow keeps a small head");
    }
}

/// <summary>The name banner (FR-79), the logo (FR-80) and where everything on top of the picture goes.</summary>
public class TvLayoutTests
{
    private static readonly SizeF Tv = new(1920, 1080);

    [Fact]
    public void The_name_is_tidied_and_written_as_one_line()
    {
        Assert.Equal("Jane Doe", NameCaption.Tidy("  Jane\nDoe "));
        Assert.Equal("", NameCaption.Tidy(null));
        Assert.Equal(NameCaption.MaxLength, NameCaption.Tidy(new string('x', 100)).Length);
        Assert.Equal("Jane Doe · Head of Sales", NameCaption.Words("Jane Doe", "Head of Sales"));
        Assert.Equal("Jane Doe", NameCaption.Words("Jane Doe", ""));
    }

    [Fact]
    public void The_banner_is_a_lower_third_on_the_tv_as_wide_as_its_words()
    {
        RectangleF one = Layouts.Banner(Tv, twoLines: false, textWidth: 300);
        RectangleF two = Layouts.Banner(Tv, twoLines: true, textWidth: 300);
        Assert.True(two.Height > one.Height);
        Assert.True(one.Top > Tv.Height * 0.66f && one.Bottom < Tv.Height, "in the lower third, off the very edge");
        Assert.True(Layouts.Banner(Tv, false, 5000).Right <= Tv.Width * 0.95f, "never wider than the TV");
        Assert.True(Layouts.Banner(Tv, false, 1).Width >= Tv.Height * 0.3f, "never a sliver");
    }

    [Fact]
    public void Live_captions_sit_above_the_banner_and_a_bottom_qr_code_above_both()
    {
        RectangleF banner = Layouts.Banner(Tv, true, 400);
        RectangleF live = Layouts.LiveStrip(Tv, banner.Top);
        Assert.True(live.Bottom <= banner.Top);
        Assert.Equal(Tv.Width / 2, live.Left + live.Width / 2, 1);
        RectangleF alone = Layouts.LiveStrip(Tv, Tv.Height);
        Assert.True(alone.Bottom <= Tv.Height * 0.95f + 0.01f);
        foreach (InsetCorner corner in Enum.GetValues<InsetCorner>())
        {
            RectangleF card = Layouts.QrCard(Tv, corner, live.Top);
            Assert.True(card.Left >= 0 && card.Top >= 0 && card.Right <= Tv.Width && card.Bottom <= Tv.Height, corner.ToString());
            if (corner is InsetCorner.BottomLeft or InsetCorner.BottomRight) Assert.True(card.Bottom <= live.Top, "nothing covers a bottom code");
            Assert.Equal(card.Width, card.Height);
        }
    }

    [Fact]
    public void Qr_modules_are_whole_pixels_with_room_for_the_quiet_zone()
    {
        foreach (int modules in new[] { 21, 25, 57 })
        foreach (float side in new[] { 120f, 324f, 648f })
        {
            float module = Layouts.QrModule(side, modules);
            Assert.Equal(MathF.Floor(module), module);
            Assert.True(module >= 1);
            Assert.True(module * (modules + 8) <= Math.Max(side, modules + 8));
        }
    }

    [Fact]
    public void The_logo_fits_above_the_message_and_small_logos_arent_blown_up()
    {
        RectangleF big = Layouts.Logo(Tv, new SizeF(4000, 1000), withMessage: true);
        Assert.True(big.Width <= Tv.Width * 0.4f + 0.5f && big.Height <= Tv.Height * 0.3f + 0.5f);
        Assert.Equal(4f, big.Width / big.Height, 2);
        RectangleF tiny = Layouts.Logo(Tv, new SizeF(40, 20), withMessage: true);
        Assert.Equal(80, tiny.Width, 1); // at most twice its own size
        RectangleF message = Layouts.Message(Tv, big);
        Assert.True(message.Top > big.Bottom);
        Assert.Equal(new RectangleF(0, 0, Tv.Width, Tv.Height), Layouts.Message(Tv, RectangleF.Empty));
        Assert.True(Layouts.Logo(Tv, SizeF.Empty, true).IsEmpty);
        Assert.Equal(Tv.Height / 2, Layouts.Logo(Tv, new SizeF(100, 100), withMessage: false).Top + 100, 1);
    }

    [Fact]
    public void A_logo_file_is_checked_before_it_is_used()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-logo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string png = Path.Combine(folder, "logo.png");
            using (var bitmap = new Bitmap(300, 100))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(Color.OrangeRed);
                bitmap.Save(png, ImageFormat.Png);
            }
            Assert.Null(LogoImage.Problem(png));
            using (Bitmap? loaded = LogoImage.Load(png))
            {
                Assert.NotNull(loaded);
                Assert.Equal(PixelFormat.Format32bppPArgb, loaded!.PixelFormat);
                Assert.Equal(new Size(300, 100), loaded.Size);
            }
            File.Delete(png); // not locked: the picture was read into memory

            string fake = Path.Combine(folder, "fake.png");
            File.WriteAllText(fake, "not a picture");
            Assert.Equal("That file isn't a picture this app can show.", LogoImage.Problem(fake));
            Assert.Equal("Choose a PNG, JPEG, BMP or GIF picture.", LogoImage.Problem(Path.Combine(folder, "logo.svg")));
            Assert.Equal("That picture can't be found.", LogoImage.Problem(Path.Combine(folder, "gone.png")));
            string huge = Path.Combine(folder, "huge.png");
            using (var stream = File.Create(huge)) stream.SetLength(LogoImage.MaxBytes + 1);
            Assert.Contains("too big", LogoImage.Problem(huge));
            Assert.Null(LogoImage.Load(null));
            Assert.Null(LogoImage.Load(huge));
            Assert.Equal(Path.Combine(folder, "blank-logo.jpg"), LogoImage.CopyPath(folder, @"C:\Pictures\Company.JPG"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_huge_logo_is_scaled_down_to_a_sensible_size()
    {
        string file = Path.Combine(Path.GetTempPath(), $"scp-huge-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new Bitmap(4000, 1000)) bitmap.Save(file, ImageFormat.Png);
            using Bitmap loaded = LogoImage.Load(file)!;
            Assert.Equal(LogoImage.MaxSide, loaded.Width);
            Assert.Equal(400, loaded.Height);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>Room profiles (FR-82).</summary>
public class RoomProfileTests
{
    [Fact]
    public void A_room_is_made_from_how_things_are_now_and_sets_them_back()
    {
        var s = new AppSettings
        {
            OutputDevice = @"\\.\DISPLAY2",
            Dim = 0.2,
            HighlightClicks = true,
            PointerRing = true,
            ShowCursor = false,
            BlankMessage = "Back soon",
            CaptionName = "Jane Doe",
            CaptionSecond = "Head of Sales",
            SecondOutputDevice = @"\\.\DISPLAY3",
        };
        s.Favourites[0] = new Favourite { AppName = "POWERPNT", Title = "Deck" };
        RoomProfile room = RoomProfile.From("  Board room ", s, withFavourites: true);
        Assert.Equal("Board room", room.Name);

        var other = new AppSettings();
        other.Favourites[4] = new Favourite { AppName = "notepad", Title = "Notes" };
        room.ApplyTo(other);
        Assert.Equal(@"\\.\DISPLAY2", other.OutputDevice);
        Assert.Equal(0.2, other.Dim);
        Assert.True(other.HighlightClicks && other.PointerRing && !other.ShowCursor);
        Assert.Equal("Back soon", other.BlankMessage);
        Assert.Equal("Jane Doe", other.CaptionName);
        Assert.Equal(@"\\.\DISPLAY3", other.SecondOutputDevice);
        Assert.Equal("Deck", other.Favourites[0]!.Title);
        Assert.Null(other.Favourites[4]); // the room's favourites replace them

        // A copy, not the same objects: changing the room later doesn't change the settings.
        room.Favourites![0]!.Title = "Changed";
        Assert.Equal("Deck", other.Favourites[0]!.Title);
    }

    [Fact]
    public void A_room_saved_without_favourites_leaves_them_alone()
    {
        var other = new AppSettings();
        other.Favourites[2] = new Favourite { AppName = "notepad", Title = "Notes" };
        RoomProfile.From("Lobby", new AppSettings(), withFavourites: false).ApplyTo(other);
        Assert.Equal("Notes", other.Favourites[2]!.Title);
    }

    [Fact]
    public void The_second_tv_is_never_set_to_the_first_tvs_screen()
    {
        var room = new RoomProfile { Name = "x", OutputDevice = @"\\.\DISPLAY2", SecondOutputDevice = @"\\.\DISPLAY2" };
        var s = new AppSettings();
        room.ApplyTo(s);
        Assert.Null(s.SecondOutputDevice);
    }

    [Fact]
    public void Rooms_from_a_settings_file_are_tidied()
    {
        var rooms = new List<RoomProfile>
        {
            new() { Name = "Board room", Dim = 9 },
            new() { Name = "BOARD ROOM" }, // the same name: dropped
            new() { Name = "   " },
            null!,
            new() { Name = "Lab", Dim = double.NaN, OutputMode = "weird", Favourites = new Favourite?[3], BlankMessage = "" },
        };
        rooms.AddRange(Enumerable.Range(0, 20).Select(i => new RoomProfile { Name = $"Room {i}" }));
        List<RoomProfile> tidy = RoomProfile.NormalizeAll(rooms);
        Assert.Equal(RoomProfile.Max, tidy.Count);
        Assert.Equal(AppSettings.MaxDim, tidy[0].Dim);
        RoomProfile lab = tidy[1];
        Assert.Equal(0, lab.Dim);
        Assert.Equal("Screen", lab.OutputMode);
        Assert.Equal(AppSettings.SlotCount, lab.Favourites!.Length);
        Assert.Equal("We'll be right back", lab.BlankMessage);
        Assert.Same(tidy[0], RoomProfile.Find(tidy, " board ROOM "));
        Assert.Null(RoomProfile.Find(tidy, "Nowhere"));
        Assert.Null(RoomProfile.Find(tidy, null));
    }

    [Fact]
    public void Rooms_and_schedules_survive_saving_and_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-rooms-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.Rooms.Add(new RoomProfile { Name = "Lobby", Dim = 0.1, Favourites = new Favourite?[AppSettings.SlotCount] });
            store.Current.Schedule.Add(new ScheduleEntry { Time = "8:00", Days = "Mon,Fri", Action = "StartSlideshow" });
            store.Current.Schedule.Add(new ScheduleEntry { Time = "18:00", Action = "Room", Room = "Lobby" });
            store.Current.CaptionName = "Jane";
            store.Current.QrLink = "https://example.com";
            store.Current.QrWifiName = "Guest";
            store.Current.SecondOutputDevice = @"\\.\DISPLAY3";
            Assert.True(store.Save());
            Assert.DoesNotContain(typeof(AppSettings).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)); // a Wi-Fi password is never kept
            AppSettings loaded = new SettingsStore(store.FilePath).Load();
            Assert.Equal("Lobby", Assert.Single(loaded.Rooms).Name);
            Assert.Equal(2, loaded.Schedule.Count);
            Assert.Equal("08:00", loaded.Schedule[0].Time);
            Assert.Equal(ScheduleAction.Room, loaded.Schedule[1].What);
            Assert.Equal("Jane", loaded.CaptionName);
            Assert.Equal("Guest", loaded.QrWifiName);
            Assert.Equal(@"\\.\DISPLAY3", loaded.SecondOutputDevice);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Schedules (FR-83).</summary>
public class ScheduleTests
{
    private static ScheduleEntry At(string time, string days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", ScheduleAction action = ScheduleAction.StartSlideshow) =>
        new() { Time = time, Days = days, Action = action.ToString() };

    // Monday 25 Nov 2024 (a known Monday).
    private static DateTime Monday(int hour, int minute = 0, int second = 0) => new(2024, 11, 25, hour, minute, second);

    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("8:05", 8, 5)]
    [InlineData("23:59", 23, 59)]
    [InlineData(" 00:00 ", 0, 0)]
    public void Times_are_read_as_24_hour(string text, int hour, int minute) => Assert.Equal(new TimeSpan(hour, minute, 0), ScheduleEntry.ParseTime(text));

    [Theory]
    [InlineData("24:00")]
    [InlineData("8")]
    [InlineData("8:5")]
    [InlineData("eight")]
    [InlineData("")]
    [InlineData(null)]
    public void Nonsense_times_are_refused(string? text) => Assert.Null(ScheduleEntry.ParseTime(text));

    [Fact]
    public void Days_are_read_and_described_in_plain_words()
    {
        Assert.Equal("Weekdays", ScheduleEntry.DaysWords(ScheduleEntry.ParseDays("Mon,Tue,Wed,Thu,Fri")));
        Assert.Equal("Every day", ScheduleEntry.DaysWords(ScheduleEntry.ParseDays("sun mon tue wed thu fri sat")));
        Assert.Equal("Weekends", ScheduleEntry.DaysWords(ScheduleEntry.ParseDays("Sat;Sun")));
        Assert.Equal("Mon, Wed", ScheduleEntry.DaysWords(ScheduleEntry.ParseDays("Wednesday,Monday")));
        Assert.Equal("Never", ScheduleEntry.DaysWords(ScheduleEntry.ParseDays("")));
        Assert.Equal("Mon,Sun", ScheduleEntry.DaysText([DayOfWeek.Sunday, DayOfWeek.Monday])); // the week starts on Monday
    }

    [Fact]
    public void An_entry_says_what_it_does()
    {
        Assert.Equal("Weekdays at 08:00: start the slideshow", At("08:00", "Mon,Tue,Wed,Thu,Fri").Words());
        Assert.Equal("show favourite 3", new ScheduleEntry { Action = "ShowFavourite", Slot = 3 }.WhatWords());
        Assert.Equal("show Deck", new ScheduleEntry { Action = "ShowFavourite", Slot = 3 }.WhatWords(_ => "Deck"));
        Assert.Equal("set up for “Lobby”", new ScheduleEntry { Action = "Room", Room = "Lobby" }.WhatWords());
        foreach (ScheduleAction action in Enum.GetValues<ScheduleAction>())
            Assert.NotEqual("", new ScheduleEntry { Action = action.ToString(), Room = "R" }.WhatWords());
    }

    [Fact]
    public void Entries_from_a_settings_file_are_tidied()
    {
        var entries = new List<ScheduleEntry>
        {
            At("8:00"),
            At("25:00"),                                                   // no such time
            At("09:00", days: "Funday"),                                   // no day
            new() { Time = "10:00", Action = "Room", Room = "  " },         // a room without a name
            new() { Time = "11:00", Action = "LaunchRockets" },            // unknown: the slideshow
            new() { Time = "12:00", Action = "ShowFavourite", Slot = 99 },
            null!,
        };
        List<ScheduleEntry> tidy = ScheduleEntry.NormalizeAll(entries);
        Assert.Equal(["08:00", "11:00", "12:00"], tidy.Select(e => e.Time));
        Assert.Equal(ScheduleAction.StartSlideshow, tidy[1].What);
        Assert.Equal(AppSettings.SlotCount, tidy[2].Slot);
        Assert.Equal(ScheduleEntry.Max, ScheduleEntry.NormalizeAll([.. Enumerable.Range(0, 40).Select(_ => At("07:00"))]).Count);
    }

    [Fact]
    public void An_entry_is_due_once_when_its_time_passes()
    {
        ScheduleEntry eight = At("08:00");
        Assert.Empty(Scheduler.Due([eight], Monday(7, 59, 0), Monday(7, 59, 30)));
        Assert.Equal([eight], Scheduler.Due([eight], Monday(7, 59, 30), Monday(8, 0, 0)));
        Assert.Empty(Scheduler.Due([eight], Monday(8, 0, 0), Monday(8, 0, 30))); // not twice
    }

    [Fact]
    public void Only_on_its_days()
    {
        ScheduleEntry weekdays = At("08:00", "Mon,Tue,Wed,Thu,Fri");
        Assert.Single(Scheduler.Due([weekdays], Monday(7, 59), Monday(8, 1)));
        Assert.Empty(Scheduler.Due([weekdays], Monday(7, 59).AddDays(-1), Monday(8, 1).AddDays(-1))); // Sunday
    }

    [Fact]
    public void A_pc_that_slept_through_it_skips_it_rather_than_doing_it_late()
    {
        ScheduleEntry eight = At("08:00");
        Assert.Empty(Scheduler.Due([eight], Monday(7, 0), Monday(14, 0)));          // woke at two
        Assert.Single(Scheduler.Due([eight], Monday(7, 0), Monday(8, 4)));           // four minutes late: still done
        Assert.Empty(Scheduler.Due([eight], Monday(7, 0), Monday(8, 6)));            // six: skipped
    }

    [Fact]
    public void Clocks_going_back_never_repeat_anything()
    {
        Assert.Empty(Scheduler.Due([At("08:00")], Monday(8, 30), Monday(7, 30)));
        Assert.Empty(Scheduler.Due([At("08:00")], Monday(8, 0), Monday(8, 0)));
    }

    [Fact]
    public void Midnight_and_order_are_handled()
    {
        ScheduleEntry late = At("23:59"), midnight = At("00:00"), stop = At("00:00", action: ScheduleAction.StopSlideshow);
        // In time order; at the same time, in the order they're listed.
        Assert.Equal([late, stop, midnight], Scheduler.Due([stop, midnight, late], Monday(23, 58), Monday(23, 58).AddMinutes(3)));
    }

    [Fact]
    public void Switched_off_entries_are_never_due()
    {
        ScheduleEntry off = At("08:00");
        off.Enabled = false;
        Assert.Empty(Scheduler.Due([off], Monday(7, 59), Monday(8, 1)));
        Assert.Null(Scheduler.Next([off], Monday(7)));
    }

    [Fact]
    public void The_next_time_is_found_within_a_week()
    {
        ScheduleEntry friday = At("09:30", "Fri"), daily = At("18:00");
        Assert.Equal((Monday(18), daily), Scheduler.Next([friday, daily], Monday(9)));
        Assert.Equal((Monday(9, 30).AddDays(4), friday), Scheduler.Next([friday], Monday(10)));
        Assert.Equal((Monday(9, 30).AddDays(4), friday), Scheduler.Next([friday], Monday(9, 30).AddDays(4).AddSeconds(-1)));
    }

    [Fact]
    public void The_add_form_offers_every_action_with_the_favourites_and_rooms_there_are()
    {
        var favourites = new Favourite?[AppSettings.SlotCount];
        favourites[1] = new Favourite { AppName = "POWERPNT", Title = "Quarterly results" };
        var scenes = new Scene?[Scene.Slots];
        scenes[2] = new Scene { Name = "Demo", Source = new Favourite { AppName = "POWERPNT", Title = "Deck" } };
        var actions = ScheduleChoices.Actions(favourites, [new RoomProfile { Name = "Lobby" }], scenes,
            [new Playlist { Name = "Signage", Items = [new PlaylistItem { Slot = 2 }] }]);
        Assert.Contains(actions, a => a.Action == ScheduleAction.Scene && a.Slot == 3 && a.Words.Contains("Demo"));
        Assert.Contains(actions, a => a.Action == ScheduleAction.StartPlaylist && a.Room == "Signage");
        Assert.Contains(actions, a => a.Action == ScheduleAction.ShowFavourite && a.Slot == 2 && a.Words.Contains("Quarterly"));
        Assert.Contains(actions, a => a.Action == ScheduleAction.Room && a.Room == "Lobby");
        foreach (ScheduleAction action in Enum.GetValues<ScheduleAction>())
            Assert.Contains(actions, a => a.Action == action);
        Assert.All(ScheduleChoices.Days, d => Assert.NotEmpty(ScheduleEntry.ParseDays(d.Days)));
    }
}

/// <summary>Live captions (FR-84): turning the speech helper's lines into what the TV shows.</summary>
public class LiveCaptionTextTests
{
    private static readonly DateTime T0 = new(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Guesses_show_as_they_come_and_are_replaced_by_what_was_said()
    {
        var live = new LiveCaptions();
        Assert.True(live.Heard("partial welcome", T0));
        Assert.Equal("welcome", live.Shown);
        Assert.True(live.Heard("partial welcome every", T0));
        Assert.True(live.Heard("final Welcome, everyone.", T0));
        Assert.Equal("Welcome, everyone.", live.Shown);
        live.Heard("partial today we", T0);
        Assert.Equal("Welcome, everyone. today we", live.Shown);
        Assert.False(live.Heard("partial today we", T0)); // no change, nothing to send
    }

    [Fact]
    public void Only_the_last_few_sentences_are_kept_and_the_end_is_what_shows()
    {
        var live = new LiveCaptions();
        for (int i = 0; i < 10; i++) live.Heard($"final Sentence number {i} is a fairly long one to fill the line.", T0);
        Assert.True(live.Shown.Length <= LiveCaptions.MaxShown);
        Assert.StartsWith("… ", live.Shown);
        Assert.EndsWith("Sentence number 9 is a fairly long one to fill the line.", live.Shown);
    }

    [Fact]
    public void Silence_clears_the_words_after_a_few_seconds()
    {
        var live = new LiveCaptions();
        live.Heard("final Hello.", T0);
        Assert.False(live.Tick(T0 + LiveCaptions.Silence - TimeSpan.FromMilliseconds(1)));
        Assert.True(live.Tick(T0 + LiveCaptions.Silence));
        Assert.Equal("", live.Shown);
        Assert.False(live.Tick(T0 + LiveCaptions.Silence * 2));
    }

    [Fact]
    public void Other_lines_and_junk_change_nothing()
    {
        var live = new LiveCaptions();
        Assert.False(live.Heard("ready", T0));
        Assert.False(live.Heard("error No microphone", T0));
        Assert.False(live.Heard("", T0));
        Assert.True(live.Heard("partial tab\there" + (char)0x2028 + "x", T0));
        Assert.Equal("tab here x", live.Shown);
        Assert.Equal(("partial", "a b"), LiveCaptions.Split("partial a b"));
        Assert.Equal(("ready", ""), LiveCaptions.Split("ready"));
        Assert.Equal(("", ""), LiveCaptions.Split(null));
    }

    [Fact]
    public void A_long_tail_starts_at_a_whole_word()
    {
        string tail = LiveCaptions.Tail(string.Join(' ', Enumerable.Repeat("word", 60)), 30);
        Assert.True(tail.Length <= 30);
        Assert.StartsWith("… word", tail);
        Assert.Equal("short", LiveCaptions.Tail("  short "));
    }

    [Fact]
    public void Why_listening_failed_is_said_in_plain_words()
    {
        Assert.Contains("isn't installed for your language", CaptionsHost.Explain(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x8004503A))));
        Assert.Contains("No microphone", CaptionsHost.Explain(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80045056))));
        Assert.Contains("didn't allow the microphone", CaptionsHost.Explain(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070005))));
        Assert.StartsWith("Windows' speech recognition couldn't start", CaptionsHost.Explain(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Live_captions_travel_to_the_helper_as_one_line()
    {
        Assert.Equal("live hello world", CaptureProtocol.Live("hello\nworld"));
        Assert.Equal("live", CaptureProtocol.Live(null));
        Assert.Equal("caption Jane Doe" + CaptureProtocol.LineSeparator + "Head of Sales", CaptureProtocol.Caption("Jane Doe", "Head of Sales"));
        Assert.Equal("caption none", CaptureProtocol.Caption(null, null));
        Assert.Equal("caption a b" + CaptureProtocol.LineSeparator + "c d", CaptureProtocol.Caption("a" + CaptureProtocol.LineSeparator + "b", "c\rd"));
        Assert.Equal("logo C:\\My Pictures\\logo.png", CaptureProtocol.Logo("C:\\My Pictures\\logo.png"));
        Assert.Equal("logo none", CaptureProtocol.Logo(null));
        Assert.Equal("qr 2 https://example.com", CaptureProtocol.Qr("https://example.com", InsetCorner.TopRight));
        Assert.Equal("qr none", CaptureProtocol.Qr(null, InsetCorner.TopRight));
        Assert.Equal("spotlight 0.5,0.25", CaptureProtocol.Spotlight(new PointF(0.5f, 0.25f)));
        Assert.Equal("spotlight none", CaptureProtocol.Spotlight(null));
    }
}

/// <summary>The command line knows the new things (FR-43 with FR-81, FR-82).</summary>
public class StageCommandLineTests
{
    [Fact]
    public void A_room_and_a_qr_code_can_be_asked_for()
    {
        (CliRequest? r, string? error) = CommandLine.Parse(["--room", "Board room", "--qr", "https://example.com", "--qr=off"]);
        Assert.Null(error);
        Assert.Equal(["room:Board room", "cli:qr:https://example.com", "cli:qr-off"], r!.Actions);
    }

    [Fact]
    public void Mistakes_are_explained()
    {
        Assert.Contains("--room needs", CommandLine.Parse(["--room"]).Error);
        Assert.Contains("--qr needs", CommandLine.Parse(["--qr"]).Error);
        Assert.Contains("too long", CommandLine.Parse(["--qr", new string('x', 300)]).Error);
    }

    [Fact]
    public void Every_new_action_works_by_name()
    {
        foreach (string id in new[] { "hide-part", "show-hidden", "audience-view", "get-ready", "go", "spotlight", "caption", "qr", "live-captions", "second-tv", "save-room" })
            Assert.Equal([id], CommandLine.Parse(["--action", id]).Request!.Actions);
    }

    [Fact]
    public void The_captions_helper_switch_is_never_a_request()
    {
        Assert.False(CommandLine.IsRequest([LiveCaptions.Switch]));
        Assert.Contains("--room", CommandLine.Usage);
        Assert.Contains("--qr", CommandLine.Usage);
    }
}
