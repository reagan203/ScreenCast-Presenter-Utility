using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-86: scenes.</summary>
public class SceneTests
{
    private static Favourite Deck => new() { AppName = "POWERPNT", Title = "Deck" };

    [Fact]
    public void Scenes_from_a_settings_file_are_tidied()
    {
        var scenes = new Scene?[] {
            new() { Name = "  Demo\n", Source = Deck, Crop = new CropRegion(0, 0, 1, 1), Dim = 7, QrLink = "WIFI:T:WPA;S:x;P:secret;;", InsetSize = 99, InsetCorner = "Sideways" },
            new() { Name = "No source" },
            new() { Source = new Favourite { AppName = "", Title = "x" } },
            new() { Source = Deck, Crop = CropRegion.Create(0.1, 0.1, 0.5, 0.5), QrLink = "https://example.com", Inset = new Favourite { AppName = " ", Title = "y" } },
        };
        Scene?[] tidy = Scene.NormalizeAll([.. scenes, .. Enumerable.Repeat<Scene?>(new Scene { Source = Deck }, 20)]);
        Assert.Equal(Scene.Slots, tidy.Length);
        Assert.Equal("Demo", tidy[0]!.Name);
        Assert.Null(tidy[0]!.Crop);             // all of it is no crop
        Assert.Null(tidy[0]!.QrLink);           // a Wi-Fi code (with its password) is never kept
        Assert.Equal(AppSettings.MaxDim, tidy[0]!.Dim);
        Assert.Equal(InsetLayout.Sizes[^1], tidy[0]!.InsetSize);
        Assert.Equal("BottomRight", tidy[0]!.InsetCorner);
        Assert.Null(tidy[1]);
        Assert.Null(tidy[2]);
        Assert.Equal("https://example.com", tidy[3]!.QrLink);
        Assert.Null(tidy[3]!.Inset);
        Assert.Equal(new Scene?[Scene.Slots], Scene.NormalizeAll(null));
    }

    [Fact]
    public void A_scene_says_what_it_holds()
    {
        var scene = new Scene { Source = Deck, Crop = CropRegion.Create(0, 0, 0.5, 1), Inset = Deck, Caption = true, QrLink = "https://x.y", Spotlight = true, Dim = 0.2 };
        Assert.Equal("Deck · part of it · small picture · name · QR code · spotlight · dimmed 20%", scene.Words().Replace(" %", "%"));
        Assert.Equal("Scene 4", scene.Title(4));
        Assert.Equal("Demo", new Scene { Name = "Demo" }.Title(4));
        Assert.Null(Scene.KeepableQr("wifi:T:nopass;S:x;;"));
        Assert.Equal("https://a", Scene.KeepableQr("https://a"));
    }

    [Fact]
    public void Scenes_are_on_ctrl_alt_f1_to_f9_and_the_command_line()
    {
        for (int slot = 1; slot <= Scene.Slots; slot++)
            Assert.Equal($"Ctrl+Alt+F{slot}", ShortcutCatalog.Find(ShortcutCatalog.SceneId(slot))!.Default!.Value.ToString());
        Assert.Equal(["scene-3"], CommandLine.Parse(["--scene", "3"]).Request!.Actions);
        Assert.Contains("--scene needs", CommandLine.Parse(["--scene", "10"]).Error);
        Assert.Contains("--scene needs", CommandLine.Parse(["--scene"]).Error);
        Assert.Contains("--scene", CommandLine.Usage);
    }

    [Fact]
    public void Scenes_survive_saving_and_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-scenes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.Scenes[4] = new Scene { Name = "Demo", Source = Deck, Crop = CropRegion.Create(0.1, 0.1, 0.5, 0.5), Caption = true, Dim = 0.1 };
            Assert.True(store.Save());
            Scene loaded = new SettingsStore(store.FilePath).Load().Scenes[4]!;
            Assert.Equal("Demo", loaded.Name);
            Assert.Equal("Deck", loaded.Source!.Title);
            Assert.True(loaded.Caption);
            Assert.Equal(CropRegion.Create(0.1, 0.1, 0.5, 0.5), loaded.Crop);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>FR-87: playlists for lobby screens.</summary>
public class PlaylistTests
{
    private static PlaylistItem Fav(int slot, int seconds = 15) => new() { Kind = "Favourite", Slot = slot, Seconds = seconds };
    private static PlaylistItem Scn(int slot) => new() { Kind = "Scene", Slot = slot };

    [Fact]
    public void The_next_item_goes_round_and_skips_what_cant_be_shown()
    {
        List<PlaylistItem> items = [Fav(1), Fav(2), Scn(1), Fav(3)];
        Assert.Equal(0, PlaylistPlayer.Next(items, null, _ => true));
        Assert.Equal(1, PlaylistPlayer.Next(items, 0, _ => true));
        Assert.Equal(0, PlaylistPlayer.Next(items, 3, _ => true)); // round again
        Assert.Equal(2, PlaylistPlayer.Next(items, 0, i => i.IsScene || i.Slot == 3));
        Assert.Equal(2, PlaylistPlayer.Next(items, 2, i => i.IsScene)); // only one can be shown: it stays
        Assert.Null(PlaylistPlayer.Next(items, 1, _ => false));
        Assert.Null(PlaylistPlayer.Next([], null, _ => true));
        Assert.Equal(0, PlaylistPlayer.Next(items, 99, _ => true)); // a stale position starts again
    }

    [Fact]
    public void Playlists_from_a_settings_file_are_tidied()
    {
        var playlists = new List<Playlist>
        {
            new() { Name = "Lobby", Screen = "second", Items = [Fav(1, 12), Fav(0), Fav(10), Scn(2), null!] },
            new() { Name = "LOBBY", Items = [Fav(1)] },          // the same name
            new() { Name = "Empty", Items = [Fav(42)] },        // nothing left in it
            new() { Name = "  ", Items = [Fav(1)] },
            new() { Name = "Odd screen", Screen = "Ceiling", Items = [.. Enumerable.Range(0, 50).Select(_ => Fav(2))] },
            null!,
        };
        List<Playlist> tidy = Playlist.NormalizeAll(playlists);
        Assert.Equal(["Lobby", "Odd screen"], tidy.Select(p => p.Name));
        Assert.Equal(PlaylistScreen.Second, tidy[0].Where);
        Assert.Equal(2, tidy[0].Items.Count);
        Assert.Equal(Slideshow.Normalize(12), tidy[0].Items[0].Seconds);
        Assert.True(tidy[0].Items[1].IsScene);
        Assert.Equal(PlaylistScreen.Main, tidy[1].Where);
        Assert.Equal(Playlist.MaxItems, tidy[1].Items.Count);
        Assert.Same(tidy[0], Playlist.Find(tidy, " lobby "));
    }

    [Fact]
    public void A_playlist_that_plays_at_start_must_exist()
    {
        var s = new AppSettings { PlaylistAtStart = "Nowhere" };
        s.Playlists.Add(new Playlist { Name = "Lobby", Items = [Fav(1)] });
        Assert.Null(s.Normalize().PlaylistAtStart);
        s.PlaylistAtStart = "lobby";
        Assert.Equal("Lobby", s.Normalize().PlaylistAtStart);
    }

    [Fact]
    public void Everything_makes_a_playlist_of_all_saved_favourites_and_scenes()
    {
        var favourites = new Favourite?[AppSettings.SlotCount];
        favourites[0] = new Favourite { AppName = "a", Title = "A" };
        favourites[4] = new Favourite { AppName = "b", Title = "B" };
        var scenes = new Scene?[Scene.Slots];
        scenes[1] = new Scene { Source = favourites[0] };
        List<PlaylistItem> all = PlaylistPlayer.Everything(favourites, scenes, 30);
        Assert.Equal(["Favourite 1", "Favourite 5", "Scene 2"], all.Select(i => $"{i.Kind} {i.Slot}"));
        Assert.All(all, i => Assert.Equal(30, i.Seconds));
        Assert.Equal("favourite 1 for 30 seconds", all[0].Words());
        Assert.Equal("Deck for 30 seconds", all[2].Words(sceneName: _ => "Deck"));
    }

    [Fact]
    public void Playlists_are_on_the_command_line_the_tray_and_the_schedule()
    {
        Assert.Equal(["playlist:Lobby"], CommandLine.Parse(["--playlist", "Lobby"]).Request!.Actions);
        Assert.Equal(["stop-playlist"], CommandLine.Parse(["--playlist", "off"]).Request!.Actions);
        Assert.True(TrayMenuPlan.IsKnownCommand("playlist:Lobby"));
        Assert.False(TrayMenuPlan.IsKnownCommand("playlist:"));
        var state = new TrayMenuState { Session = SessionState.Off, Status = "Ready", Playlists = ["Lobby"], PlaylistPlaying = true, Scenes = [(2, "Demo")] };
        List<MenuEntry> menu = [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(state))];
        Assert.Contains(menu, e => e.Command == "playlist:Lobby");
        Assert.Contains(menu, e => e.Command == "stop-playlist");
        Assert.Contains(menu, e => e.Command == "scene-2");
        Assert.Contains(menu, e => e.Command == "phone-remote");
        Assert.DoesNotContain(menu, e => e.Command == "save-scene"); // nothing shown to save
        Assert.Equal("play “Lobby”", new ScheduleEntry { Action = "StartPlaylist", Playlist = "Lobby" }.WhatWords());
        Assert.Equal("show scene 4", new ScheduleEntry { Action = "Scene", Slot = 4 }.WhatWords());
        Assert.Empty(ScheduleEntry.NormalizeAll([new ScheduleEntry { Time = "08:00", Action = "StartPlaylist", Playlist = " " }]));
        Assert.Single(ScheduleEntry.NormalizeAll([new ScheduleEntry { Time = "08:00", Action = "StartPlaylist", Playlist = "Lobby", Room = "ignored" }]));
    }
}

/// <summary>FR-88: what automatic hiding covers, and what it leaves alone.</summary>
[Collection(SpeedCollection.Name)]
public class SensitiveTextTests
{
    /// <summary>A line of words, each 10 pixels wide per letter, one pixel apart, 20 high.</summary>
    private static OcrLine Line(string text, float y = 0)
    {
        var words = new List<OcrWord>();
        float x = 0;
        foreach (string w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new OcrWord(w, new RectangleF(x, y, w.Length * 10, 20)));
            x += w.Length * 10 + 10;
        }
        return new OcrLine(words);
    }

    private static List<SensitiveMatch> Find(string text, SensitiveKinds kinds = SensitiveKinds.All) => SensitiveText.Find([Line(text)], kinds);

    [Theory]
    [InlineData("Write to jane.doe@example.com today")]
    [InlineData("a.b-c+tag@mail.example.co.uk")]
    [InlineData("CONTACT: SALES@EXAMPLE.ORG.")]
    public void Email_addresses_are_found(string text) => Assert.Contains(Find(text), m => m.Kind == SensitiveKinds.Emails);

    [Theory]
    [InlineData("Call +44 20 7946 0958 now")]
    [InlineData("(555) 010-9999")]
    [InlineData("555-010-9999")]
    [InlineData("0161 496 0000")]
    [InlineData("+1 (202) 555-0143")]
    [InlineData("0049 30 1234567")]
    [InlineData("555.010.9999")]
    [InlineData("01.23.45.67.89")]
    public void Phone_numbers_are_found(string text) => Assert.Contains(Find(text), m => m.Kind == SensitiveKinds.Phones);

    [Theory]
    [InlineData("Order 1234567890")]           // a plain number
    [InlineData("Total 1,234,567.89")]
    [InlineData("Updated 2025-11-26")]           // a date
    [InlineData("26/11/2025 at 10:30")]
    [InlineData("Version 10.0.19041.5072")]
    [InlineData("Page 12 of 340")]
    [InlineData("Server 192.168.100.200")]       // a network address
    [InlineData("Build 3.14.159.26")]
    public void Ordinary_numbers_are_left_alone(string text) => Assert.DoesNotContain(Find(text), m => m.Kind is SensitiveKinds.Phones or SensitiveKinds.Cards);

    [Theory]
    [InlineData("Card 4111 1111 1111 1111 exp 12/28", true)]
    [InlineData("5500-0000-0000-0004", true)]
    [InlineData("378282246310005", true)]            // 15-digit Amex
    [InlineData("4111 1111 1111 1112", false)]      // fails the Luhn check
    public void Card_numbers_are_checked(string text, bool card) =>
        Assert.Equal(card, Find(text).Any(m => m.Kind == SensitiveKinds.Cards));

    [Theory]
    [InlineData("IBAN GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("GB82 WEST 1234 5698 7654 33", false)] // wrong check digits
    public void Ibans_are_checked(string text, bool iban) =>
        Assert.Equal(iban, Find(text).Any(m => m.Kind == SensitiveKinds.Ibans));

    [Theory]
    [InlineData("SSN 123-45-6789")]
    [InlineData("NI number AB 12 34 56 C")]
    [InlineData("AB123456C")]
    public void Id_numbers_are_found(string text) => Assert.Contains(Find(text), m => m.Kind == SensitiveKinds.IdNumbers);

    [Fact]
    public void Only_the_kinds_asked_for_are_covered()
    {
        string text = "mail jane@example.com phone +44 20 7946 0958";
        Assert.Equal([SensitiveKinds.Emails], Find(text, SensitiveKinds.Emails).Select(m => m.Kind));
        Assert.Equal([SensitiveKinds.Phones], Find(text, SensitiveKinds.Phones).Select(m => m.Kind));
        Assert.Empty(Find(text, SensitiveKinds.None));
        Assert.Equal(2, Find(text).Count);
    }

    [Fact]
    public void The_box_covers_exactly_the_words_with_a_little_margin()
    {
        List<SensitiveMatch> found = Find("Call +44 20 7946 0958 now", SensitiveKinds.Phones);
        RectangleF box = Assert.Single(found).Box;
        // "Call" is 0–40; the number's four words run from 50 to 210; "now" starts at 220.
        float pad = 20 * SensitiveText.Padding;
        Assert.Equal(50 - pad, box.Left, 2);
        Assert.Equal(210 + pad, box.Right, 2);
        Assert.Equal(-pad, box.Top, 2);
        Assert.Equal(20 + pad, box.Bottom, 2);
    }

    [Fact]
    public void Several_lines_and_several_finds()
    {
        List<SensitiveMatch> found = SensitiveText.Find([Line("a@b.co and c@d.co", 0), Line("nothing here", 30), Line("SSN 123-45-6789", 60)], SensitiveKinds.All);
        Assert.Equal(3, found.Count);
        Assert.Equal("2 email addresses and an ID number", SensitiveText.Words(found));
        Assert.Equal("nothing", SensitiveText.Words([]));
        Assert.Equal("a phone number", SensitiveText.Words([new SensitiveMatch(SensitiveKinds.Phones, RectangleF.Empty)]));
    }

    [Fact]
    public void Boxes_become_fractions_of_the_picture_and_are_capped()
    {
        List<CropRegion> regions = SensitiveText.ToRegions([new SensitiveMatch(SensitiveKinds.Emails, new RectangleF(100, 50, 200, 20))], new SizeF(1000, 500));
        CropRegion r = Assert.Single(regions);
        Assert.Equal(0.1, r.X, 4);
        Assert.Equal(0.04, r.Height, 4); // small boxes are kept (a word in small print)
        var many = Enumerable.Range(0, 200).Select(i => new SensitiveMatch(SensitiveKinds.Emails, new RectangleF(i, i, 20, 10))).ToList();
        Assert.Equal(Redactions.MaxAutomatic, SensitiveText.ToRegions(many, new SizeF(1000, 1000)).Count);
        Assert.Empty(SensitiveText.ToRegions(many, SizeF.Empty));
    }

    [Fact]
    public void Scans_never_scale_up_and_stay_bounded()
    {
        Assert.Equal(new Size(1920, 1080), AutoRedactor.ScanSize(new Size(1920, 1080)));
        Assert.Equal(new Size(AutoRedactor.MaxScanSide, 1440), AutoRedactor.ScanSize(new Size(3840, 2160)));
        Assert.Equal(new Size(1440, AutoRedactor.MaxScanSide), AutoRedactor.ScanSize(new Size(2160, 3840)));
        Assert.Equal(Size.Empty, AutoRedactor.ScanSize(Size.Empty));
    }

    [Fact]
    public void Automatic_hiding_is_off_unless_chosen_and_survives_saving()
    {
        var s = new AppSettings();
        Assert.Equal(SensitiveKinds.None, s.AutoHideKinds);
        s.AutoHide = "Emails, Cards, Rockets";
        Assert.Equal(SensitiveKinds.None, s.Normalize().AutoHideKinds); // unreadable: off, never guessed
        s.AutoHideKinds = SensitiveKinds.Emails | SensitiveKinds.Cards;
        Assert.Equal("Emails, Cards", s.Normalize().AutoHide);
        Assert.Equal(SensitiveKinds.Emails | SensitiveKinds.Cards, s.AutoHideKinds);
    }

    [Fact]
    public void Speed_a_full_page_is_checked_in_well_under_a_frame()
    {
        var lines = Enumerable.Range(0, 300).Select(i => Line($"Row {i} jane{i}@example.com +44 20 7946 {i:0000} order 98765{i} total 1,234.{i % 100:00} 2025-11-26", i * 25)).ToList();
        SensitiveText.Find(lines, SensitiveKinds.All); // warm up the expressions
        // The fastest of a few batches: other tests share the CPU, and a slowdown in the code shows in every batch.
        double best = double.MaxValue;
        for (int batch = 0; batch < 5; batch++)
        {
            var clock = Stopwatch.StartNew();
            const int runs = 5;
            for (int r = 0; r < runs; r++) SensitiveText.Find(lines, SensitiveKinds.All);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds / runs);
        }
        Assert.True(best < 10, $"300 lines took {best:F2} ms (budget 10 ms)");
    }

    /// <summary>Windows' own text recognizer on a picture with an email address in it: the box lands on it.</summary>
    [Fact]
    public async Task Real_text_recognition_finds_the_address_where_it_is()
    {
        const int w = 900, h = 220;
        using var picture = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        Rectangle emailAt;
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 28, GraphicsUnit.Pixel);
            g.DrawString("Quarterly results", font, Brushes.Black, 20, 20);
            g.DrawString("Contact:", font, Brushes.Black, 20, 110);
            SizeF before = g.MeasureString("Contact: ", font);
            SizeF email = g.MeasureString("jane.doe@example.com", font);
            g.DrawString("jane.doe@example.com", font, Brushes.Black, 20 + before.Width, 110);
            emailAt = Rectangle.Round(new RectangleF(20 + before.Width, 110, email.Width, email.Height));
        }
        BitmapData data = picture.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var bgra = new byte[w * h * 4];
        Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
        picture.UnlockBits(data);
        List<OcrLine>? lines = await AutoRedactor.ReadAsync(bgra, w, h);
        if (lines is null) return; // no text recognizer for this PC's languages
        SensitiveMatch match = Assert.Single(SensitiveText.Find(lines, SensitiveKinds.All));
        Assert.Equal(SensitiveKinds.Emails, match.Kind);
        RectangleF overlap = RectangleF.Intersect(match.Box, emailAt);
        Assert.True(overlap.Width * overlap.Height >= emailAt.Width * emailAt.Height * 0.8f, $"box {match.Box} covers the address at {emailAt}");
        Assert.DoesNotContain(lines.SelectMany(l => l.Words), word => word.Text.Contains("Quarterly") && match.Box.IntersectsWith(word.Box));
    }
}

/// <summary>FR-89: transcripts and caption sizes.</summary>
public class TranscriptTests
{
    private static readonly DateTime T0 = new(2025, 3, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void What_was_said_is_written_as_it_is_said_with_subtitles_at_the_end()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-transcript-" + Guid.NewGuid().ToString("N"));
        try
        {
            (TranscriptWriter? writer, string? problem) = TranscriptWriter.Start(folder, T0);
            Assert.Null(problem);
            writer!.Add("Welcome, everyone.", T0.AddSeconds(1), T0.AddSeconds(2.5));
            writer.Add("  Let's begin.\n", T0.AddSeconds(65), T0.AddSeconds(66));
            writer.Add("   ", T0.AddSeconds(70), T0.AddSeconds(71)); // nothing: not written
            // Written as it's said: readable before the end (a crash keeps it).
            string soFar;
            using (var read = new FileStream(writer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) soFar = new StreamReader(read).ReadToEnd();
            Assert.Contains("[00:00:01] Welcome, everyone.", soFar);
            Assert.Contains("[00:01:05] Let's begin.", soFar);
            writer.Dispose();
            Assert.StartsWith("Transcript", Path.GetFileName(writer.Path));
            string srt = File.ReadAllText(writer.SubtitlePath);
            Assert.StartsWith("1\n00:00:01,000 --> 00:00:02,500\nWelcome, everyone.\n\n2\n00:01:05,000 --> 00:01:06,500\nLet's begin.", srt);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Subtitles_never_overlap_and_short_ones_stay_long_enough_to_read()
    {
        string srt = TranscriptWriter.Srt(
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2), "Hi."),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), "Next one."),
            new(TimeSpan.FromHours(1.5), TimeSpan.FromHours(1.5) + TimeSpan.FromSeconds(3), "Late."),
        ]);
        Assert.Contains("00:00:01,000 --> 00:00:02,000", srt); // stretched to 1.5 s, cut where the next begins
        Assert.Contains("00:00:02,000 --> 00:00:05,000", srt);
        Assert.Contains("01:30:00,000 --> 01:30:03,000", srt);
        Assert.Equal("", TranscriptWriter.Srt([]));
    }

    [Fact]
    public void A_folder_that_cant_be_written_says_why()
    {
        (TranscriptWriter? writer, string? problem) = TranscriptWriter.Start(Path.Combine(Path.GetTempPath(), "bad|name"), T0);
        Assert.Null(writer);
        Assert.StartsWith("The transcript can't be saved", problem);
    }

    [Fact]
    public void Captions_know_when_each_thing_said_began()
    {
        var live = new LiveCaptions();
        live.Heard("partial welcome", T0);
        live.Heard("partial welcome every", T0.AddSeconds(1));
        live.Heard("final Welcome, everyone.", T0.AddSeconds(2));
        Assert.Equal(("Welcome, everyone.", T0, T0.AddSeconds(2)), live.LastSaid);
        live.Heard("final Straight to it.", T0.AddSeconds(10)); // no guesses before it
        Assert.Equal(("Straight to it.", T0.AddSeconds(10), T0.AddSeconds(10)), live.LastSaid);
        live.Limit = 20;
        live.Heard("final One two three four five six seven eight.", T0.AddSeconds(12));
        Assert.True(live.Shown.Length <= 20);
        live.Reset();
        Assert.Null(live.LastSaid);
    }

    [Fact]
    public void Caption_sizes_and_places()
    {
        Assert.True(new CaptionLook(CaptionSize.ExtraLarge, CaptionPlace.Bottom).TextHeight > new CaptionLook(CaptionSize.Large, CaptionPlace.Bottom).TextHeight);
        Assert.True(new CaptionLook(CaptionSize.ExtraLarge, CaptionPlace.Bottom).MaxShown < CaptionLook.Default.MaxShown);
        Assert.Equal(CaptionSize.Normal, CaptionLook.ParseSize("Gigantic"));
        Assert.Equal(CaptionPlace.Top, CaptionLook.ParsePlace("top"));
        var tv = new SizeF(1920, 1080);
        RectangleF top = Layouts.LiveStrip(tv, tv.Height, new CaptionLook(CaptionSize.Large, CaptionPlace.Top));
        Assert.Equal(tv.Height * Layouts.Margin, top.Top, 1);
        RectangleF card = Layouts.QrCard(tv, InsetCorner.TopRight, tv.Height, top.Bottom);
        Assert.True(card.Top >= top.Bottom, "a top QR code sits below top captions");
        var s = new AppSettings { CaptionSize = "huge", CaptionPlace = "Top" }.Normalize();
        Assert.Equal(new CaptionLook(CaptionSize.Normal, CaptionPlace.Top), s.CaptionLook);
    }

    [Fact]
    public void Caption_look_and_automatic_hiding_reach_the_helper()
    {
        var helper = new TextEffectsWithStyle();
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.CaptionStyle(new CaptionLook(CaptionSize.ExtraLarge, CaptionPlace.Top))));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.AutoHide(SensitiveKinds.Emails | SensitiveKinds.Ibans, RedactStyle.Blurred)));
        Assert.True(CaptureHost.ApplyMark(helper, "autohide 9999 7")); // nonsense: only real kinds, a safe look
        Assert.Equal(["style ExtraLarge Top", "autohide Emails, Ibans Blurred", "autohide Emails, Phones, Cards, Ibans, Faces, KeepLargestFace Solid"], helper.Calls); // 9999 keeps its known bits only
    }

    private sealed class TextEffectsWithStyle : IEffectsEngine
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
        public void Inset(Interop.SourceWindow? source, InsetCorner corner, int percent) { }
        public event EventHandler<string>? InsetFailed { add { } remove { } }
        public void CaptionStyle(CaptionLook look) => Calls.Add($"style {look.Size} {look.Place}");
        public void AutoHide(SensitiveKinds kinds, RedactStyle style) => Calls.Add($"autohide {kinds} {style}");
    }
}

/// <summary>Every new setting survives saving, and nonsense in the file is made safe.</summary>
public class RoomRunningSettingsTests
{
    [Fact]
    public void Everything_new_survives_saving_and_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            AppSettings s = store.Current;
            byte[] key = Core.RemoteAuth.NewKey();
            s.PhoneRemote = true;
            s.RemotePort = 50123;
            s.RemoteKey = Core.RemoteAuth.Protect(key);
            s.AutoHideKinds = SensitiveKinds.Emails | SensitiveKinds.IdNumbers;
            s.CaptionsOnTv = false;
            s.SaveTranscript = true;
            s.TranscriptFolder = folder;
            s.CaptionLook = new CaptionLook(CaptionSize.Large, CaptionPlace.Top);
            s.Playlists.Add(new Playlist { Name = "Lobby", Screen = "Second", Items = [new PlaylistItem { Slot = 1, Seconds = 30 }] });
            s.PlaylistAtStart = "Lobby";
            Assert.True(store.Save());
            Assert.DoesNotContain(Core.RemoteAuth.Base64Url(key), File.ReadAllText(store.FilePath)); // the key is never written readable
            AppSettings loaded = new SettingsStore(store.FilePath).Load();
            Assert.True(loaded.PhoneRemote);
            Assert.Equal(50123, loaded.RemotePort);
            Assert.Equal(key, Core.RemoteAuth.Unprotect(loaded.RemoteKey));
            Assert.Equal(SensitiveKinds.Emails | SensitiveKinds.IdNumbers, loaded.AutoHideKinds);
            Assert.False(loaded.CaptionsOnTv);
            Assert.True(loaded.SaveTranscript);
            Assert.Equal(folder, loaded.TranscriptFolder);
            Assert.Equal(new CaptionLook(CaptionSize.Large, CaptionPlace.Top), loaded.CaptionLook);
            Assert.Equal(PlaylistScreen.Second, Assert.Single(loaded.Playlists).Where);
            Assert.Equal("Lobby", loaded.PlaylistAtStart);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Nonsense_is_made_safe()
    {
        var s = new AppSettings { RemotePort = 80, RemoteKey = "  ", TranscriptFolder = " ", PhoneRemote = false }.Normalize();
        Assert.Equal(Remote.RemoteServer.DefaultPort, s.RemotePort); // below 1024 needs admin rights: the default instead
        Assert.Null(s.RemoteKey);
        Assert.Null(s.TranscriptFolder);
        Assert.False(new AppSettings().PhoneRemote);   // off unless switched on
        Assert.Equal(SensitiveKinds.None, new AppSettings().AutoHideKinds);
        Assert.False(new AppSettings().SaveTranscript);
        Assert.True(new AppSettings().CaptionsOnTv);
    }
}
