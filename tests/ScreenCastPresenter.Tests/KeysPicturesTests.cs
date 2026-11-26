using System.Drawing.Imaging;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-101: shortcuts on the TV, and never what's typed.</summary>
public class KeyDisplayTests
{
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Control, Alt = HotkeyModifiers.Alt, Shift = HotkeyModifiers.Shift, Win = HotkeyModifiers.Win;

    [Fact]
    public void Combinations_are_shown_with_their_keys_in_order()
    {
        Assert.Equal("Ctrl + C", KeyDisplay.Describe(Keys.C, Ctrl));
        Assert.Equal("Ctrl + Shift + S", KeyDisplay.Describe(Keys.S, Ctrl | Shift));
        Assert.Equal("Alt + F4", KeyDisplay.Describe(Keys.F4, Alt));
        Assert.Equal("Win + D", KeyDisplay.Describe(Keys.D, Win));
        Assert.Equal("Ctrl + Alt + Win + Shift + 5".Replace("Win + Shift", "Shift + Win"), KeyDisplay.Describe(Keys.D5, Ctrl | Alt | Shift | Win));
        Assert.Equal("F5", KeyDisplay.Describe(Keys.F5, HotkeyModifiers.None));
        Assert.Equal("Esc", KeyDisplay.Describe(Keys.Escape, HotkeyModifiers.None));
        Assert.Equal("Ctrl + Shift + Esc", KeyDisplay.Describe(Keys.Escape, Ctrl | Shift));
        Assert.Equal("Shift + F10", KeyDisplay.Describe(Keys.F10, Shift));
    }

    [Theory]
    [InlineData(Keys.A, (int)HotkeyModifiers.None)]
    [InlineData(Keys.A, (int)HotkeyModifiers.Shift)]
    [InlineData(Keys.D7, (int)HotkeyModifiers.Shift)]
    [InlineData(Keys.Space, (int)HotkeyModifiers.None)]
    [InlineData(Keys.Return, (int)HotkeyModifiers.None)]
    [InlineData(Keys.Back, (int)HotkeyModifiers.None)]
    [InlineData(Keys.Left, (int)HotkeyModifiers.Shift)]
    [InlineData(Keys.Tab, (int)HotkeyModifiers.None)]
    [InlineData(Keys.OemPeriod, (int)HotkeyModifiers.Control)] // a key whose meaning depends on the layout
    [InlineData(Keys.ControlKey, (int)HotkeyModifiers.Control)] // a modifier on its own
    public void Typing_is_never_shown(Keys key, int modifiers) => Assert.Null(KeyDisplay.Describe(key, (HotkeyModifiers)modifiers));

    [Fact]
    public void A_combination_that_types_a_character_is_never_shown()
    {
        // AltGr: Ctrl+Alt+Q types @ on a German keyboard, so showing it would spell out an address.
        Assert.Null(KeyDisplay.Describe(Keys.Q, Ctrl | Alt, h => h.Key == Keys.Q));
        Assert.Equal("Ctrl + Alt + T", KeyDisplay.Describe(Keys.T, Ctrl | Alt, h => h.Key == Keys.Q));
        Assert.Equal("Ctrl + Q", KeyDisplay.Describe(Keys.Q, Ctrl, _ => true)); // plain Ctrl never types
        Assert.Equal("Ctrl + Alt + Win + Q", KeyDisplay.Describe(Keys.Q, Ctrl | Alt | Win, _ => true)); // with Win it isn't AltGr
    }

    [Fact]
    public void Only_new_presses_count_and_every_watched_key_has_a_name()
    {
        var before = new HashSet<Keys> { Keys.C };
        var now = new HashSet<Keys> { Keys.C, Keys.V, Keys.F5 };
        Assert.Equal([Keys.V, Keys.F5], KeyDisplay.NewlyDown(before, now).Order());
        Assert.Empty(KeyDisplay.NewlyDown(now, now));
        Assert.Empty(KeyDisplay.NewlyDown(now, before)); // released keys aren't presses
        Assert.All(KeyDisplay.Watched, k => Assert.True(Hotkey.CanName(k) || k == Keys.Escape, k.ToString()));
        Assert.Contains(Keys.Escape, KeyDisplay.Watched);
        Assert.Contains(Keys.F24, KeyDisplay.Watched);
        Assert.DoesNotContain(Keys.ControlKey, KeyDisplay.Watched);
    }

    [Fact]
    public void The_chip_sits_centred_above_the_banner_and_never_off_the_tv()
    {
        var tv = new SizeF(1920, 1080);
        RectangleF chip = Layouts.KeyChip(tv, 300, 900);
        Assert.Equal(960, chip.X + chip.Width / 2, 1);
        Assert.True(chip.Bottom < 900);
        RectangleF high = Layouts.KeyChip(tv, 300, 10); // nothing room below: still on the TV
        Assert.True(high.Top >= 0);
        Assert.True(Layouts.KeyChip(tv, 99_999, 900).Width < tv.Width);
    }

    [Fact]
    public void Shortcuts_reach_the_helper_and_are_an_action_and_a_switch()
    {
        var helper = new TextEffectsWithKeys();
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.Keys("Ctrl + C")));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.Keys(null)));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.Keys("a\nb")));
        Assert.Equal(["Ctrl + C", "none", "a b"], helper.Calls);
        Assert.Equal(["cli:on:show-keys"], CommandLine.Parse(["--on", "show-keys"]).Request!.Actions);
        foreach (string id in new[] { "show-keys", "zoom-follows" })
        {
            Assert.NotNull(ShortcutCatalog.Find(id));
            Assert.NotNull(UI.Icons.ForCommand(id));
        }
        Assert.False(new AppSettings().ShowKeys); // off unless chosen
    }

    private sealed class TextEffectsWithKeys : IEffectsEngine
    {
        public List<string> Calls { get; } = [];
        public void Click(PointF at, bool secondary) { }
        public void Pointer(PointF? at) { }
        public void Laser(PointF? at) { }
        public void ShowCursor(bool on) { }
        public void InkStart(InkStroke stroke) { }
        public void InkAdd(int id, IReadOnlyList<PointF> points) { }
        public void InkSet(int id, IReadOnlyList<PointF> points) { }
        public void InkRemove(int id) { }
        public void InkClear() { }
        public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) { }
        public void Spotlight(PointF? at) { }
        public void Caption(string? name, string? second) { }
        public void Logo(string? path) { }
        public void Qr(string? text, InsetCorner corner) { }
        public void LiveCaption(string? text) { }
        public void Inset(SourceWindow? source, InsetCorner corner, int percent) { }
        public event EventHandler<string>? InsetFailed { add { } remove { } }
        public void KeyCombo(string? text) => Calls.Add(text ?? "none");
    }
}

/// <summary>FR-99: zoom that follows typing.</summary>
public class ZoomFollowTests
{
    private static readonly PointF Mouse = new(0.2f, 0.2f), Caret = new(0.7f, 0.6f);
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Each_mode_follows_what_it_says()
    {
        Assert.Equal(Mouse, ZoomFollow.Choose(ZoomFollows.Pointer, Mouse, Ms(10), Caret, Ms(5)));
        Assert.Equal(Caret, ZoomFollow.Choose(ZoomFollows.Typing, Mouse, Ms(10), Caret, Ms(5000)));
        Assert.Null(ZoomFollow.Choose(ZoomFollows.Typing, Mouse, Ms(10), null, Ms(5))); // nowhere typed: it stays
        Assert.Null(ZoomFollow.Choose(ZoomFollows.Pointer, null, Ms(10), Caret, Ms(5)));
    }

    [Fact]
    public void Both_follows_typing_until_the_mouse_moves()
    {
        Assert.Equal(Caret, ZoomFollow.Choose(ZoomFollows.Both, Mouse, Ms(3000), Caret, Ms(200))); // typing just now, mouse still
        Assert.Equal(Mouse, ZoomFollow.Choose(ZoomFollows.Both, Mouse, Ms(100), Caret, Ms(300))); // the mouse moved after
        Assert.Equal(Mouse, ZoomFollow.Choose(ZoomFollows.Both, Mouse, Ms(9000), Caret, Ms(5000))); // typing stopped a while ago
        Assert.Equal(Mouse, ZoomFollow.Choose(ZoomFollows.Both, Mouse, Ms(9000), null, Ms(0)));
    }

    [Fact]
    public void The_text_cursor_is_placed_on_what_is_shown()
    {
        var area = new Rectangle(100, 100, 400, 200);
        PointF on = ZoomFollow.On(new Rectangle(299, 190, 2, 20), area)!.Value;
        Assert.Equal(0.5f, on.X, 3);
        Assert.Equal(0.5f, on.Y, 3);
        Assert.Null(ZoomFollow.On(new Rectangle(10, 10, 2, 20), area)); // elsewhere
        Assert.Null(ZoomFollow.On(new Rectangle(200, 150, 2, 0), area));
        Assert.Null(ZoomFollow.On(new Rectangle(200, 150, 2, 20), Rectangle.Empty));
    }

    [Fact]
    public void The_setting_reads_and_describes()
    {
        Assert.Equal(ZoomFollows.Both, ZoomFollow.Parse("both"));
        Assert.Equal(ZoomFollows.Pointer, ZoomFollow.Parse("sideways"));
        Assert.Equal(ZoomFollows.Pointer, new AppSettings().ZoomFollowsMode);
        Assert.Equal(nameof(ZoomFollows.Typing), new AppSettings { ZoomFollows = "typing" }.Normalize().ZoomFollows);
        Assert.All(Enum.GetValues<ZoomFollows>(), f => Assert.NotEmpty(ZoomFollow.Words(f)));
    }

    [Fact]
    public void The_text_cursor_tracker_starts_and_stops_cleanly()
    {
        using (var tracker = new CaretTracker())
        {
            Thread.Sleep(150);
            _ = tracker.Latest; // whatever is focused: no exception
        }
        _ = CaretTracker.SystemCaret();
    }
}

/// <summary>FR-103: pictures and cards on the TV and in playlists.</summary>
public class PicturesAndCardsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "scp-pictures-" + Guid.NewGuid().ToString("N"));

    public PicturesAndCardsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Png(string name, int w = 64, int h = 48, Color? colour = null)
    {
        string path = Path.Combine(_folder, name);
        using var b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b)) g.Clear(colour ?? Color.SteelBlue);
        b.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void Pictures_are_checked_loaded_without_locking_and_scaled_down()
    {
        string ok = Png("Welcome slide.png");
        Assert.Null(PictureSource.Problem(ok));
        Assert.Contains("can't be found", PictureSource.Problem(Path.Combine(_folder, "missing.png")));
        Assert.Contains("PNG, JPEG", PictureSource.Problem(Path.Combine(_folder, "notes.txt")));
        Assert.NotNull(PictureSource.Problem(""));
        using (Bitmap? loaded = PictureSource.Load(ok))
        {
            Assert.Equal(new Size(64, 48), loaded!.Size);
            File.Delete(ok); // not locked while loaded
        }
        string big = Png("big.png", 5000, 100);
        using Bitmap? scaled = PictureSource.Load(big);
        Assert.Equal(PictureSource.MaxSide, scaled!.Width);
        File.WriteAllText(Path.Combine(_folder, "broken.png"), "not a picture");
        Assert.Null(PictureSource.Load(Path.Combine(_folder, "broken.png")));
        Assert.Equal("Welcome slide", PictureSource.Name(@"C:\x\Welcome slide.png"));
    }

    [Fact]
    public void A_folders_pictures_are_listed_by_name_and_other_files_skipped()
    {
        Png("b.png");
        Png("A.jpg".Replace(".jpg", ".png"));
        Png("c.PNG");
        File.WriteAllText(Path.Combine(_folder, "readme.txt"), "x");
        Assert.Equal(["A.png", "b.png", "c.PNG"], PictureSource.InFolder(_folder).Select(Path.GetFileName));
        Assert.Empty(PictureSource.InFolder(Path.Combine(_folder, "nope")));
        Assert.Empty(PictureSource.InFolder(null));
    }

    [Fact]
    public void Card_words_are_tidied_and_the_first_line_is_the_title()
    {
        Assert.Equal("Welcome\nWi-Fi: Guest", TextCard.Tidy("  Welcome \r\n\r\n Wi-Fi: Guest  \n"));
        Assert.Equal("Welcome", TextCard.Title("Welcome\nWi-Fi: Guest"));
        Assert.Equal(TextCard.MaxLines, TextCard.Tidy(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"))).Split('\n').Length);
        Assert.Equal(TextCard.MaxLength, TextCard.Tidy(new string('x', 1000)).Length);
        Assert.Equal("", TextCard.Tidy("   \n  "));
        Assert.Equal("ab", TextCard.Tidy("a\u0007b"));
    }

    [Fact]
    public void A_card_is_drawn_once_and_reused()
    {
        string? first = TextCard.Render(_folder, "Welcome to Acme\nWe start at 9:30");
        Assert.NotNull(first);
        DateTime made = File.GetLastWriteTimeUtc(first);
        Assert.Equal(first, TextCard.Render(_folder, "  Welcome to Acme \nWe start at 9:30"));
        Assert.Equal(made, File.GetLastWriteTimeUtc(first)); // the same words: the same file, not drawn again
        Assert.NotEqual(first, TextCard.Render(_folder, "Lunch at 12:30"));
        Assert.Null(TextCard.Render(_folder, "  "));
        using var card = new Bitmap(first);
        Assert.Equal(new Size(TextCard.Width, TextCard.Height), card.Size);
        Color corner = card.GetPixel(5, 5);
        Assert.True(corner.R < 40 && corner.G < 40, "a dark card");
        bool white = false;
        for (int x = 200; x < 1720 && !white; x += 3)
        for (int y = 300; y < 700 && !white; y += 3)
            white = card.GetPixel(x, y) is { R: > 230, G: > 230, B: > 230 };
        Assert.True(white, "the title is written in white");
    }

    [Fact]
    public void Pictures_are_sources_of_their_own()
    {
        string path = Png("slide.png");
        SourceWindow picture = SourceWindow.ForPicture(path);
        Assert.True(picture.IsPicture);
        Assert.True(picture.IsOffScreen);
        Assert.False(picture.IsWindow);
        Assert.False(picture.IsScreen);
        Assert.Equal("slide", picture.Title);
        Assert.Equal(SourceWindow.PictureApp, picture.AppName);
        Assert.True(picture.IsSameSource(SourceWindow.ForPicture(path.ToUpperInvariant(), "other title")));
        Assert.False(picture.IsSameSource(SourceWindow.ForPicture(Png("other.png"))));
        Assert.False(picture.IsSameSource(TestData.Window(0, "slide")));
        Assert.Null(Redactions.Key(picture)); // nothing on screen to hide by dragging
        // Through the helper's protocol, spaces and accents and all.
        string odd = Png("Café menu 2.png");
        SourceWindow back = CaptureProtocol.ParseSource(CaptureProtocol.SourceSpec(SourceWindow.ForPicture(odd)))!;
        Assert.Equal(odd, back.Picture);
        Assert.DoesNotContain(' ', CaptureProtocol.SourceSpec(SourceWindow.ForPicture(odd)));
        Assert.Null(CaptureProtocol.ParseSource("picture:relative%2Fpath.png")); // only full paths
        Assert.Null(CaptureProtocol.ParseSource("picture:"));
        // A favourite can be a picture.
        Favourite f = FavouriteMatcher.FromSource(picture);
        Assert.Equal(path, f.PicturePath);
        Assert.True(f.Matches(new Favourite { PicturePath = path.ToUpperInvariant() }));
        Assert.False(f.Matches(new Favourite { AppName = SourceWindow.PictureApp, Title = "slide" }));
    }

    [Fact]
    public void Playlist_steps_can_be_pictures_folders_and_cards()
    {
        Png("1.png");
        Png("2.png");
        string single = Png("poster.png");
        List<PlaylistItem> items =
        [
            new() { Kind = "Favourite", Slot = 1, Seconds = 10 },
            new() { Kind = "picture", Path = _folder, Seconds = 30 },
            new() { Kind = "Card", Text = "  Welcome \n Wi-Fi: Guest ", Seconds = 20 },
            new() { Kind = "Picture", Path = "relative.png" }, // not a full path: dropped
            new() { Kind = "Card", Text = "   " }, // no words: dropped
            new() { Kind = "Scene", Slot = 99 }, // no such scene: dropped
        ];
        var playlist = new Playlist { Name = "Lobby", Items = items };
        Playlist tidy = Assert.Single(Playlist.NormalizeAll([playlist]));
        Assert.Equal(3, tidy.Items.Count);
        Assert.True(tidy.Items[1].IsPicture);
        Assert.Equal("Welcome\nWi-Fi: Guest", tidy.Items[2].Text);
        Assert.Contains("the pictures in", tidy.Items[1].Words());
        Assert.Contains("each", tidy.Items[1].Words());
        Assert.Contains("the card “Welcome”", tidy.Items[2].Words());
        Assert.Contains("“poster”", new PlaylistItem { Kind = "Picture", Path = single, Seconds = 10 }.Words());

        List<PlaylistItem> steps = PlaylistPlayer.Expand(tidy.Items);
        Assert.Equal(1 + 3 + 1, steps.Count); // the folder's three pictures, each its own step
        Assert.All(steps.Skip(1).Take(3), s => Assert.True(s.IsPicture && s.Seconds == 30));
        Assert.Equal(["1.png", "2.png", "poster.png"], steps.Skip(1).Take(3).Select(s => Path.GetFileName(s.Path!)));
    }

    [Fact]
    public void Pictures_and_cards_are_actions_and_command_line_options()
    {
        string path = Png("slide.png");
        Assert.Equal(["cli:picture:" + path], CommandLine.Parse(["--picture", path]).Request!.Actions);
        Assert.Contains("PNG, JPEG", CommandLine.Parse(["--picture", "notes.txt"]).Error);
        Assert.Contains("--picture needs", CommandLine.Parse(["--picture"]).Error);
        Assert.Equal(["cli:card:Welcome\\nWe start at 9:30"], CommandLine.Parse(["--card", "Welcome\\nWe start at 9:30"]).Request!.Actions);
        Assert.Contains("--card needs", CommandLine.Parse(["--card", "   "]).Error);
        foreach (string id in new[] { "show-picture", "show-card" })
        {
            Assert.NotNull(ShortcutCatalog.Find(id));
            Assert.NotNull(UI.Icons.ForCommand(id));
            Assert.True(TrayMenuPlan.IsKnownCommand(id));
        }
        AppSettings s = new AppSettings { LastCard = "  hi \n there ", LastPictureFolder = "  " }.Normalize();
        Assert.Equal("hi\nthere", s.LastCard);
        Assert.Null(s.LastPictureFolder);
    }
}
