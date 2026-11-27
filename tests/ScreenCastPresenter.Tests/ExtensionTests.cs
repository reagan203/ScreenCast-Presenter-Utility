using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>Automatic hiding of keys and passwords, and of the presenter's own words (FR-88, extended).</summary>
[Collection(SpeedCollection.Name)] // holds a timing test: never beside busy tests
public class SecretsAndOwnWordsTests
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

    private static List<SensitiveMatch> Find(string text, SensitiveKinds kinds = SensitiveKinds.All, params string[] own) =>
        SensitiveText.Find([Line(text)], kinds, SensitiveText.CustomPattern(own));

    [Theory]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("token ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ")]
    [InlineData("SLACK xoxb-123456789012-abcdefghijkl")]
    [InlineData("stripe sk_" + "live_4eC39HqLyjWDarjtT1zdp7dc")] // split so GitHub push protection doesn't read the fake key as a real one
    [InlineData("OPENAI_KEY sk-proj-abcdefghijklmnopqrstuvwx")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    public void Keys_and_tokens_are_found_by_their_shapes(string text) => Assert.Contains(Find(text), m => m.Kind == SensitiveKinds.Secrets);

    [Theory]
    [InlineData("Password: hunter22", "hunter22")]
    [InlineData("password:hunter22", "password:hunter22")] // one word on the screen: all of it is covered
    [InlineData("Wi-Fi PIN = 4821", "4821")]
    [InlineData("API key: 9f8e7d6c5b4a", "9f8e7d6c5b4a")]
    [InlineData("api_key=abc123xyz", "api_key=abc123xyz")]
    [InlineData("Passcode : 0000", "0000")]
    public void What_follows_a_password_label_is_covered_not_the_label(string text, string coveredWord)
    {
        SensitiveMatch m = Assert.Single(Find(text, SensitiveKinds.Secrets));
        Assert.Equal(SensitiveKinds.Secrets, m.Kind);
        IReadOnlyList<OcrWord> words = Line(text).Words;
        Assert.True(m.Box.Contains(words.Single(w => w.Text == coveredWord).Box), "the value is covered");
        Assert.All(words.Where(w => w.Text != coveredWord), w => Assert.False(m.Box.IntersectsWith(w.Box), $"“{w.Text}” stays readable"));
    }

    [Theory]
    [InlineData("Reset your password here")]
    [InlineData("A token bucket algorithm")]
    [InlineData("The sk- prefix is short")]
    [InlineData("Keys start with AKIA in AWS")]
    [InlineData("PIN: 1")] // too short to be a secret worth hiding
    [InlineData("eyJ is how a web token starts")]
    public void Ordinary_words_about_passwords_are_left_alone(string text) => Assert.DoesNotContain(Find(text), m => m.Kind == SensitiveKinds.Secrets);

    [Fact]
    public void Keys_are_only_looked_for_when_chosen()
    {
        Assert.Empty(Find("Password: hunter22", SensitiveKinds.Emails));
        Assert.Empty(Find("AKIAIOSFODNN7EXAMPLE", SensitiveKinds.All & ~SensitiveKinds.Secrets));
        Assert.Equal("a key or password", SensitiveText.Words(Find("Password: hunter22")));
    }

    [Fact]
    public void Own_words_are_found_whole_in_any_capitals()
    {
        List<SensitiveMatch> found = Find("Meeting with ACME about project   falcon on Friday", SensitiveKinds.Custom, "Acme", "Project Falcon");
        Assert.Equal(2, found.Count);
        Assert.All(found, m => Assert.Equal(SensitiveKinds.Custom, m.Kind));
        Assert.Empty(Find("Acmeville and Falconry", SensitiveKinds.Custom, "Acme", "Falcon")); // whole words only
        Assert.Single(Find("Shipping C++ (beta) today", SensitiveKinds.Custom, "C++ (beta)")); // special characters mean themselves
        Assert.Equal("2 words from your list", SensitiveText.Words(found));
    }

    [Fact]
    public void Own_words_are_only_hidden_when_chosen_and_listed()
    {
        Assert.Empty(Find("Project Falcon", SensitiveKinds.All & ~SensitiveKinds.Custom, "Project Falcon"));
        Assert.Empty(Find("Project Falcon", SensitiveKinds.Custom));
        Assert.Null(SensitiveText.CustomPattern([]));
        Assert.Null(SensitiveText.CustomPattern(null));
        Assert.Null(SensitiveText.CustomPattern(["  ", ""]));
    }

    [Fact]
    public void A_longer_own_word_wins_over_a_shorter_one_inside_it()
    {
        SensitiveMatch m = Assert.Single(Find("Acme Labs", SensitiveKinds.Custom, "Acme", "Acme Labs"));
        Assert.True(m.Box.Contains(RectangleF.Union(Line("Acme Labs").Words[0].Box, Line("Acme Labs").Words[1].Box)));
    }

    [Fact]
    public void A_line_that_cant_be_checked_in_time_is_covered_whole()
    {
        var slow = new System.Text.RegularExpressions.Regex("(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1));
        string text = "Notes " + new string('a', 40) + "! end";
        SensitiveMatch m = Assert.Single(SensitiveText.Find([Line(text)], SensitiveKinds.Custom, slow));
        Assert.True(m.Box.Contains(RectangleF.Union(Line(text).Words[0].Box, Line(text).Words[^1].Box)));
    }

    [Fact]
    public void The_list_is_tidied_and_limited()
    {
        List<string> tidy = SensitiveText.NormalizeWords(["  Acme ", "acme", null, "", "Falcon", new string('x', 200)]);
        Assert.Equal(["Acme", "Falcon", new string('x', SensitiveText.MaxCustomWordLength)], tidy);
        Assert.Equal(SensitiveText.MaxCustomWords, SensitiveText.NormalizeWords(Enumerable.Range(0, 100).Select(i => $"word{i}")).Count);
    }

    [Fact]
    public void The_list_travels_as_one_word_and_comes_back_the_same()
    {
        string[] words = ["Acme, Inc", "50% off", "Zoë", "a b", "x;y"];
        string token = SensitiveText.WordsToken(words);
        Assert.DoesNotContain(' ', token);
        Assert.Equal(words, SensitiveText.ParseWordsToken(token));
        Assert.Equal("-", SensitiveText.WordsToken([]));
        Assert.Empty(SensitiveText.ParseWordsToken("-"));
        Assert.Empty(SensitiveText.ParseWordsToken(null));
        Assert.Equal(["%zz"], SensitiveText.ParseWordsToken("%zz")); // not escaped properly: kept as typed, never a crash
    }

    [Fact]
    public void The_list_reaches_the_helper_live()
    {
        var helper = new WordsEffects();
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.AutoHideWords(["Project Falcon", "Acme"])));
        Assert.True(CaptureHost.ApplyMark(helper, CaptureProtocol.AutoHideWords([])));
        Assert.False(CaptureHost.ApplyMark(helper, "autohide-words"));
        Assert.Equal(["Project Falcon|Acme", ""], helper.Calls);
    }

    [Fact]
    public void A_recording_is_told_the_words_too()
    {
        var request = new Recording.RecordingRequest(TestData.Window(5, "Inbox"), false, Recording.MeetingSound.None, false, @"C:\My Videos\a b.mp4",
            null, SensitiveKinds.Custom, ["Project Falcon", "Acme, Inc"]);
        Recording.RecordingRequest back = Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request))!;
        Assert.Equal(["Project Falcon", "Acme, Inc"], back.AutoHideWords);
        Assert.Equal(@"C:\My Videos\a b.mp4", back.Path);
        Assert.Empty(Recording.RecordingProtocol.ParseStart(Recording.RecordingProtocol.Start(request with { AutoHideWords = null }))!.AutoHideWords!);
    }

    [Fact]
    public void The_settings_keep_the_words_and_the_new_kinds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-words-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.AutoHideKinds = SensitiveKinds.Secrets | SensitiveKinds.Custom;
            store.Current.AutoHideWords = ["Project Falcon", " acme ", "ACME"];
            Assert.True(store.Save());
            AppSettings loaded = new SettingsStore(store.FilePath).Load();
            Assert.Equal(SensitiveKinds.Secrets | SensitiveKinds.Custom, loaded.AutoHideKinds);
            Assert.Equal(["Project Falcon", "acme"], loaded.AutoHideWords);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        Assert.Empty(new AppSettings().AutoHideWords);
    }

    [Fact]
    public void Speed_a_full_page_with_every_kind_and_a_long_list_stays_well_under_a_frame()
    {
        var lines = Enumerable.Range(0, 300).Select(i => Line($"Row {i} jane{i}@example.com password: s3cret{i} token ghp_{new string('a', 36)} order 98765{i} Acme Falcon {i}", i * 25)).ToList();
        System.Text.RegularExpressions.Regex own = SensitiveText.CustomPattern(Enumerable.Range(0, SensitiveText.MaxCustomWords).Select(i => $"Client{i}").Append("Falcon").ToList())!;
        SensitiveText.Find(lines, SensitiveKinds.All, own); // warm up
        double best = double.MaxValue;
        for (int batch = 0; batch < 5; batch++)
        {
            var clock = Stopwatch.StartNew();
            const int runs = 5;
            for (int r = 0; r < runs; r++) SensitiveText.Find(lines, SensitiveKinds.All, own);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds / runs);
        }
        Assert.True(best < 15, $"300 busy lines took {best:F2} ms (budget 15 ms)");
    }

    /// <summary>Windows' own recognizer on a picture: a password's value and a listed name are covered, the rest isn't.</summary>
    [Fact]
    public void A_screenshot_covers_a_password_and_a_listed_name()
    {
        using var page = new Bitmap(900, 260, PixelFormat.Format32bppRgb);
        Rectangle passwordAt, nameAt, titleAt;
        using (Graphics g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 30, GraphicsUnit.Pixel);
            g.DrawString("Quarterly results", font, Brushes.Black, 20, 20);
            titleAt = Rectangle.Round(new RectangleF(new PointF(20, 20), g.MeasureString("Quarterly results", font)));
            g.DrawString("Password: Tulip7Harbor", font, Brushes.Black, 20, 100);
            SizeF label = g.MeasureString("Password: ", font);
            passwordAt = Rectangle.Round(new RectangleF(20 + label.Width, 100, g.MeasureString("Tulip7Harbor", font).Width, label.Height));
            g.DrawString("Client: Northwind", font, Brushes.Black, 20, 180);
            SizeF client = g.MeasureString("Client: ", font);
            nameAt = Rectangle.Round(new RectangleF(20 + client.Width, 180, g.MeasureString("Northwind", font).Width, client.Height));
        }
        int covered = AutoRedactor.CoverInStill(page, SensitiveKinds.Secrets | SensitiveKinds.Custom, SensitiveText.CustomPattern(["Northwind"]));
        if (covered == 0 && Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null) return; // no recognizer here
        Assert.Equal(2, covered);
        static bool Covered(Bitmap b, Rectangle r) => b.GetPixel(r.X + r.Width / 2, r.Y + r.Height / 2) is var c
            && Math.Abs(c.R - EffectStyle.RedactFill.R) <= 2 && Math.Abs(c.G - EffectStyle.RedactFill.G) <= 2 && Math.Abs(c.B - EffectStyle.RedactFill.B) <= 2;
        Assert.True(Covered(page, passwordAt), "the password");
        Assert.True(Covered(page, nameAt), "the listed name");
        Assert.False(Covered(page, titleAt), "not the title");
    }

    private sealed class WordsEffects : IEffectsEngine
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
        public void AutoHideWords(IReadOnlyList<string> words) => Calls.Add(string.Join('|', words));
    }
}

/// <summary>The phone remote's new buttons (FR-90, extended): still only what's listed, and nothing that opens a window.</summary>
public class PhoneRemoteExtendedTests
{
    [Theory]
    [InlineData("zoom-in", true)]
    [InlineData("caption", true)]
    [InlineData("live-captions", true)]
    [InlineData("blank-countdown", true)]
    [InlineData("playlist-1", true)]
    [InlineData("playlist-12", true)]
    [InlineData("playlist-13", false)]
    [InlineData("playlist-0", false)]
    [InlineData("playlist-01", false)]
    [InlineData("playlist-1x", false)]
    [InlineData("playlist-", false)]
    [InlineData("playlist-100", false)]
    [InlineData("playlist:Lobby", false)]
    [InlineData("favourite-10", false)]
    [InlineData("scene-9", true)]
    [InlineData("zoom-in-more", false)]
    [InlineData("blank-message", false)]
    [InlineData("save-scene", false)]
    public void The_new_commands_are_allowed_and_nothing_else(string command, bool allowed) => Assert.Equal(allowed, RemoteCommands.IsAllowed(command));

    [Fact]
    public void Numbers_are_read_strictly()
    {
        Assert.Equal(12, RemoteCommands.Number("playlist-12", RemoteCommands.Playlist, Playlist.Max));
        Assert.Null(RemoteCommands.Number("playlist-12", RemoteCommands.Playlist, 9));
        Assert.Null(RemoteCommands.Number("scene-1", RemoteCommands.Playlist, 12));
        Assert.Null(RemoteCommands.Number("playlist-٣", RemoteCommands.Playlist, 12)); // other scripts' digits aren't numbers here
    }

    [Fact]
    public void The_page_has_the_new_buttons_and_lists_playlists()
    {
        string html = Remote.RemotePage.Html;
        foreach (string a in new[] { "zoom-in", "caption", "live-captions", "blank-countdown" })
        {
            Assert.Contains($"data-a=\"{a}\"", html);
            Assert.True(RemoteCommands.IsAllowed(a));
        }
        Assert.Contains("id=\"playlists\"", html);
        Assert.Contains("\"playlist-\"", html);
        // Every fixed button on the page is a command the remote takes.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "data-a=\"([^\"]+)\""))
            Assert.True(RemoteCommands.IsAllowed(m.Groups[1].Value), m.Groups[1].Value);
    }
}

/// <summary>A contact card as a QR code (FR-81, extended).</summary>
public class ContactQrTests
{
    [Fact]
    public void A_contact_card_is_written_as_phones_read_it()
    {
        Assert.Equal("MECARD:N:Sam Taylor;TEL:+44 20 7946 0958;EMAIL:sam@example.com;;", QrCode.Contact(" Sam Taylor ", "+44 20 7946 0958", "sam@example.com"));
        Assert.Equal("MECARD:N:Sam;EMAIL:sam@example.com;;", QrCode.Contact("Sam", "", "sam@example.com"));
        Assert.Equal("MECARD:N:Sam;TEL:555;;", QrCode.Contact("Sam", "555", null));
        Assert.Equal(@"MECARD:N:Taylor\, Sam\; \""Dr\""\:x\\y;;", QrCode.Contact("Taylor, Sam; \"Dr\":x\\y", null, null));
    }

    [Fact]
    public void It_scans_back_exactly()
    {
        string card = QrCode.Contact("Zoë Müller", "+49 30 1234567", "zoe@example.de");
        Assert.Equal(card, QrDecoder.Decode(QrCode.Encode(card)!).Text);
    }

    [Fact]
    public void Under_the_code_only_the_name_is_written()
    {
        Assert.Equal("Contact: Sam Taylor", EffectsLayer.QrLabel(QrCode.Contact("Sam Taylor", "+44 20 7946 0958", "sam@example.com")));
        Assert.Equal("Contact: Taylor, Sam; Jr", EffectsLayer.QrLabel(QrCode.Contact("Taylor, Sam; Jr", "1", null)));
        Assert.Equal("Contact", EffectsLayer.QrLabel("MECARD:N:;TEL:1;;"));
        Assert.DoesNotContain("7946", EffectsLayer.QrLabel(QrCode.Contact("Sam", "+44 20 7946 0958", null)));
    }

    [Theory]
    [InlineData("", "555", "", "Type the name")]
    [InlineData("Sam", "", "", "phone number or an email")]
    [InlineData("Sam", "", "sam example.com", "doesn't look right")]
    [InlineData("Sam", "", "@example.com", "doesn't look right")]
    [InlineData("Sam", "", "sam@", "doesn't look right")]
    [InlineData("Sam", "call me", "", "no digits")]
    [InlineData("Sam", "555 0100", "", null)]
    [InlineData("Sam", "", "sam@example.com", null)]
    public void What_was_typed_is_checked(string name, string phone, string email, string? problem)
    {
        string? said = ScpApp.ContactInputProblem(name, phone, email);
        if (problem is null) Assert.Null(said);
        else Assert.Contains(problem, said);
    }

    [Fact]
    public void A_card_too_big_for_the_tv_is_refused()
    {
        string big = new('x', QrCode.MaxContactField);
        Assert.Null(ScpApp.ContactInputProblem("Sam", "1", "a@b.c"));
        Assert.Contains("too long", ScpApp.ContactInputProblem(big + big + big + big, big + "1", big + "@x.y") ?? "too long");
    }

    [Fact]
    public void A_scene_may_keep_a_contact_card_and_settings_are_made_safe()
    {
        string card = QrCode.Contact("Sam", "1", null);
        Assert.Equal(card, Scene.KeepableQr(card));
        var s = new AppSettings { QrKind = "Rockets", QrContactName = "  Sam  ", QrContactPhone = " ", QrContactEmail = new string('e', 500) }.Normalize();
        Assert.Equal(nameof(QrKind.Link), s.QrKind);
        Assert.Equal("Sam", s.QrContactName);
        Assert.Null(s.QrContactPhone);
        Assert.Equal(QrCode.MaxContactField, s.QrContactEmail!.Length);
        Assert.Equal(nameof(QrKind.Contact), new AppSettings { QrKind = "contact" }.Normalize().QrKind);
    }
}

/// <summary>Shuffled playlists (FR-87, extended).</summary>
public class PlaylistShuffleTests
{
    private static List<PlaylistItem> Items(int n) => [.. Enumerable.Range(1, n).Select(s => new PlaylistItem { Slot = s })];

    [Fact]
    public void Shuffled_never_shows_the_same_one_twice_running_and_reaches_them_all()
    {
        List<PlaylistItem> items = Items(5);
        var random = new Random(1234);
        int? current = null;
        var seen = new HashSet<int>();
        for (int step = 0; step < 200; step++)
        {
            int next = PlaylistPlayer.Next(items, current, _ => true, shuffle: true, random)!.Value;
            Assert.NotEqual(current, next);
            seen.Add(next);
            current = next;
        }
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void Shuffled_skips_what_cant_be_shown_and_keeps_a_lone_one()
    {
        List<PlaylistItem> items = Items(4);
        var random = new Random(7);
        for (int step = 0; step < 50; step++)
            Assert.Contains(PlaylistPlayer.Next(items, null, i => i.Slot is 2 or 4, shuffle: true, random), new int?[] { 1, 3 });
        Assert.Equal(2, PlaylistPlayer.Next(items, 2, i => i.Slot == 3, shuffle: true, random)); // only one can be shown: it stays
        Assert.Null(PlaylistPlayer.Next(items, 1, _ => false, shuffle: true, random));
        Assert.Null(PlaylistPlayer.Next([], null, _ => true, shuffle: true, random));
    }

    [Fact]
    public void In_order_is_unchanged()
    {
        List<PlaylistItem> items = Items(3);
        var random = new Random(1);
        Assert.Equal(1, PlaylistPlayer.Next(items, 0, _ => true, shuffle: false, random));
        Assert.Equal(0, PlaylistPlayer.Next(items, 2, _ => true, shuffle: false, random));
    }

    [Fact]
    public void Shuffle_survives_saving_and_loading()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-shuffle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.Playlists.Add(new Playlist { Name = "Lobby", Shuffle = true, Items = Items(2) });
            store.Current.Playlists.Add(new Playlist { Name = "Talks", Items = Items(2) });
            Assert.True(store.Save());
            List<Playlist> loaded = new SettingsStore(store.FilePath).Load().Playlists;
            Assert.True(loaded[0].Shuffle);
            Assert.False(loaded[1].Shuffle);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Web subtitles beside the transcript (FR-89, extended).</summary>
public class WebSubtitleTests
{
    private static readonly DateTime T0 = new(2025, 3, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WebVtt_has_the_same_cues_with_dots_for_milliseconds()
    {
        List<TranscriptLine> lines =
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "Welcome, everyone."),
            new(TimeSpan.FromHours(1.5), TimeSpan.FromHours(1.5) + TimeSpan.FromSeconds(3), "Late."),
        ];
        string vtt = TranscriptWriter.Vtt(lines);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nWelcome, everyone.\n\n01:30:00.000 --> 01:30:03.000\nLate.\n\n", vtt);
        Assert.Equal("WEBVTT\n\n", TranscriptWriter.Vtt([]));
    }

    [Fact]
    public void A_cue_with_a_comma_in_its_words_keeps_it()
    {
        string vtt = TranscriptWriter.Vtt([new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Yes, 1,000 people")]);
        Assert.Contains("00:00:00.000 --> 00:00:02.000\nYes, 1,000 people", vtt);
    }

    [Fact]
    public void Both_subtitle_files_are_written_at_the_end_and_only_when_something_was_said()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-vtt-" + Guid.NewGuid().ToString("N"));
        try
        {
            (TranscriptWriter? writer, _) = TranscriptWriter.Start(folder, T0);
            writer!.Add("Hello.", T0.AddSeconds(1), T0.AddSeconds(2));
            Assert.False(File.Exists(writer.WebSubtitlePath));
            writer.Dispose();
            Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello.", File.ReadAllText(writer.WebSubtitlePath)); // stretched to be readable, like the .srt
            Assert.Equal(".vtt", Path.GetExtension(writer.WebSubtitlePath));
            Assert.Equal(Path.ChangeExtension(writer.SubtitlePath, ".vtt"), writer.WebSubtitlePath);

            (TranscriptWriter? quiet, _) = TranscriptWriter.Start(Path.Combine(folder, "quiet"), T0);
            quiet!.Dispose();
            Assert.False(File.Exists(quiet.WebSubtitlePath));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Scenes keep live captions on or off (FR-86, extended).</summary>
public class SceneCaptionsTests
{
    private static Favourite Deck => new() { AppName = "POWERPNT", Title = "Deck" };

    [Fact]
    public void A_scene_says_when_it_turns_live_captions_on()
    {
        Assert.Equal("Deck · live captions", new Scene { Source = Deck, LiveCaptions = true }.Words());
        Assert.Equal("Deck", new Scene { Source = Deck, LiveCaptions = false }.Words());
        Assert.Equal("Deck", new Scene { Source = Deck }.Words());
    }

    [Fact]
    public void Older_scenes_leave_captions_as_they_are()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scp-scenecap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            store.Current.Scenes[0] = new Scene { Source = Deck, LiveCaptions = true };
            store.Current.Scenes[1] = new Scene { Source = Deck, LiveCaptions = false };
            store.Current.Scenes[2] = new Scene { Source = Deck };
            Assert.True(store.Save());
            Scene?[] loaded = new SettingsStore(store.FilePath).Load().Scenes;
            Assert.True(loaded[0]!.LiveCaptions);
            Assert.False(loaded[1]!.LiveCaptions);
            Assert.Null(loaded[2]!.LiveCaptions); // saved before scenes kept them: left alone when shown
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>"--on NAME" and "--off NAME": exact states for button decks and scripts.</summary>
public class SwitchCommandLineTests
{
    [Theory]
    [InlineData("spotlight")]
    [InlineData("caption")]
    [InlineData("live-captions")]
    [InlineData("auto-hide")]
    [InlineData("laser")]
    [InlineData("highlight-clicks")]
    [InlineData("pointer-ring")]
    [InlineData("phone-remote")]
    [InlineData("audience-view")]
    [InlineData("outline")]
    [InlineData("badge")]
    [InlineData("widget")]
    public void Each_switch_can_be_set_on_and_off(string what)
    {
        Assert.Equal(["cli:on:" + what], CommandLine.Parse(["--on", what]).Request!.Actions);
        Assert.Equal(["cli:off:" + what], CommandLine.Parse(["--off=" + what.ToUpperInvariant()]).Request!.Actions);
        Assert.Contains(what, CommandLine.Usage);
    }

    [Fact]
    public void Several_run_in_the_order_given_with_other_actions()
    {
        CliRequest r = CommandLine.Parse(["--favourite", "2", "--on", "spotlight", "--off", "laser", "--blank"]).Request!;
        Assert.Equal(["cli:on:spotlight", "cli:off:laser", "cli:blank-on"], r.Actions);
    }

    [Theory]
    [InlineData("--on")]
    [InlineData("--off", "rockets")]
    [InlineData("--on", "quit")]
    [InlineData("--on", "settings")]
    public void Anything_else_is_refused_and_says_what_can_be(params string[] args)
    {
        (CliRequest? request, string? error) = CommandLine.Parse(args);
        Assert.Null(request);
        Assert.Contains("spotlight, caption", error);
    }

    [Fact]
    public void The_switches_are_actions_the_app_knows_or_settings_it_sets()
    {
        // The toggles run by their own name: each must be a real action.
        foreach (string what in new[] { "spotlight", "caption", "live-captions", "auto-hide", "laser", "audience-view" })
            Assert.NotNull(ShortcutCatalog.Find(what));
    }
}
