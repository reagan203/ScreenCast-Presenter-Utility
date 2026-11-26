using System.Diagnostics;
using System.Text;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-81: the QR code is made on the PC, to the standard, so any phone reads it.</summary>
[Collection(SpeedCollection.Name)]
public class QrCodeTests
{
    [Fact]
    public void Reed_Solomon_matches_the_standards_worked_examples()
    {
        // "HELLO WORLD" as version 1-Q and 1-M (the well-known worked examples).
        byte[] q = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236];
        Assert.Equal(new byte[] { 168, 72, 22, 82, 217, 54, 156, 0, 46, 15, 180, 122, 16 }, QrCode.ReedSolomon(q, 13));
        byte[] m = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        Assert.Equal(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 }, QrCode.ReedSolomon(m, 10));
    }

    [Theory]
    [InlineData(1, 0, "111011111000100")] // level L, mask 0
    [InlineData(0, 0, "101010000010010")] // level M, mask 0
    [InlineData(0, 1, "101000100100101")]
    [InlineData(0, 7, "100101010100000")]
    public void Format_bits_match_the_standards_table(int level, int mask, string expected) =>
        Assert.Equal(expected, Convert.ToString(QrCode.FormatBits(level, mask), 2).PadLeft(15, '0'));

    [Theory]
    [InlineData(7, "000111110010010100")]
    [InlineData(8, "001000010110111100")]
    [InlineData(10, "001010010011010011")]
    public void Version_bits_match_the_standards_table(int version, string expected) =>
        Assert.Equal(expected, Convert.ToString(QrCode.VersionBits(version), 2).PadLeft(18, '0'));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("HELLO WORLD")]
    [InlineData("a")]
    [InlineData("Café ☕ — naïve résumé, 東京")] // UTF-8, several bytes a letter
    [InlineData("WIFI:T:WPA;S:Guest Wi-Fi;P:correct horse battery staple;;")]
    public void Codes_decode_back_to_their_text(string text)
    {
        bool[,] modules = QrCode.Encode(text)!;
        QrDecoder.Result read = QrDecoder.Decode(modules);
        Assert.Equal(text, read.Text);
        Assert.Equal(0, read.Level); // M
    }

    [Fact]
    public void Every_version_from_1_to_10_decodes()
    {
        var seen = new HashSet<int>();
        for (int length = 1; length <= QrCode.MaxBytes; length += 3)
        {
            string text = new([.. Enumerable.Range(0, length).Select(i => (char)('a' + i * 7 % 26))]);
            bool[,] modules = QrCode.Encode(text)!;
            QrDecoder.Result read = QrDecoder.Decode(modules);
            Assert.Equal(text, read.Text);
            Assert.Equal(QrCode.VersionFor(length), read.Version);
            Assert.Equal(QrCode.Size(read.Version), modules.GetLength(0));
            seen.Add(read.Version);
        }
        Assert.Equal(Enumerable.Range(1, 10), seen.Order());
    }

    [Fact]
    public void Every_mask_is_used_somewhere_and_each_decodes()
    {
        var masks = new HashSet<int>();
        for (int i = 0; i < 400 && masks.Count < 8; i++)
        {
            string text = $"https://example.com/{i}/{i * 31 % 97}";
            masks.Add(QrDecoder.Decode(QrCode.Encode(text)!).Mask);
        }
        Assert.True(masks.Count >= 6, $"masks seen: {string.Join(",", masks.Order())}");
    }

    [Fact]
    public void Capacity_ends_exactly_at_213_bytes()
    {
        Assert.Equal(213, QrCode.MaxBytes);
        Assert.NotNull(QrCode.Encode(new string('x', 213)));
        Assert.Null(QrCode.Encode(new string('x', 214)));
        Assert.Null(QrCode.Encode(new string('é', 107))); // 214 bytes in UTF-8
        Assert.Equal(1, QrCode.VersionFor(14));  // 4 + 8 + 14×8 = 124 bits ≤ 128
        Assert.Equal(2, QrCode.VersionFor(15));
        Assert.Equal(0, QrCode.VersionFor(214));
    }

    [Fact]
    public void The_empty_text_is_still_a_valid_code() => Assert.Equal("", QrDecoder.Decode(QrCode.Encode("")!).Text);

    [Fact]
    public void Wifi_details_are_in_the_form_phones_read_with_special_characters_escaped()
    {
        Assert.Equal("WIFI:T:WPA;S:Guest;P:12345678;;", QrCode.WiFi("Guest", "12345678", open: false));
        Assert.Equal(@"WIFI:T:WPA;S:My\;Net\,1;P:p\:a\\ss\""w;;", QrCode.WiFi("My;Net,1", "p:a\\ss\"w", open: false));
        Assert.Equal("WIFI:T:nopass;S:Lobby;;", QrCode.WiFi("Lobby", "ignored", open: true));
        Assert.Equal(QrCode.WiFi("Café", "pässwörd", false), QrDecoder.Decode(QrCode.Encode(QrCode.WiFi("Café", "pässwörd", false))!).Text);
    }

    [Fact]
    public void The_label_under_a_wifi_code_names_the_network_never_the_password()
    {
        Assert.Equal("Wi-Fi: My;Net", Capture.EffectsLayer.QrLabel(QrCode.WiFi("My;Net", "secret-password", false)));
        Assert.DoesNotContain("secret", Capture.EffectsLayer.QrLabel(QrCode.WiFi("Guest", "secret-password", false)));
        Assert.Equal("https://example.com", Capture.EffectsLayer.QrLabel("https://example.com"));
    }

    [Theory]
    [InlineData(false, "", "", "", false, "Type or paste a link.")]
    [InlineData(false, "https://example.com", "", "", false, null)]
    [InlineData(true, "", "", "", false, "Type the Wi-Fi network's name.")]
    [InlineData(true, "", "Guest", "short", false, "A Wi-Fi password has 8 to 63 characters. Tick “no password” for an open network.")]
    [InlineData(true, "", "Guest", "", true, null)]
    [InlineData(true, "", "Guest", "12345678", false, null)]
    public void The_qr_form_says_what_is_missing(bool wifi, string link, string network, string password, bool open, string? problem) =>
        Assert.Equal(problem, ScpApp.QrInputProblem(wifi, link, network, password, open));

    [Fact]
    public void A_link_too_long_for_a_code_is_refused_in_plain_words() =>
        Assert.Contains("too long", ScpApp.QrInputProblem(false, "https://example.com/" + new string('x', 300), "", "", false));

    [Fact]
    public void Speed_the_largest_code_takes_well_under_a_frame()
    {
        string text = new('x', QrCode.MaxBytes);
        QrCode.Encode(text); // warm up
        var clock = Stopwatch.StartNew();
        const int runs = 50;
        for (int i = 0; i < runs; i++) QrCode.Encode(text + "");
        double each = clock.Elapsed.TotalMilliseconds / runs;
        Assert.True(each < 10, $"a version-10 code took {each:F2} ms (budget 10 ms)");
    }
}
