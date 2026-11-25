using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;
using ScreenCastPresenter.UI;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// The modern look: the quick-actions panel's tiles, Windows' accent colour kept readable,
/// icons that exist in Windows' icon font, and the Windows 11 type ramp.
/// </summary>
public partial class ModernLookTests
{
    private static readonly Regex Jargon = new(@"\b(output|source|DWM|WGC|engine|palette|picker|SCP)\b", RegexOptions.IgnoreCase);

    private static QuickPanelState Idle => new() { Session = SessionState.Off };

    private static QuickPanelState Presenting => new() { Session = SessionState.Presenting };

    private static QuickTile? Tile(QuickPanelState s, string command) => QuickPanelPlan.Tiles(s).FirstOrDefault(t => t.Command == command);

    // ───────────────────────────── quick-actions panel ─────────────────────────────

    [Fact]
    public void Idle_panel_leads_with_show_something_in_the_accent_colour()
    {
        IReadOnlyList<QuickTile> tiles = QuickPanelPlan.Tiles(Idle);
        Assert.Equal("picker", tiles[0].Command);
        Assert.Equal(TileTone.Accent, tiles[0].Tone);
        Assert.Null(Tile(Idle, "blank"));
        Assert.Null(Tile(Idle, "freeze"));
        Assert.Null(Tile(Idle, "show-again"));
        Assert.NotNull(Tile(Idle with { ShowAgainTitle = "Slides" }, "show-again"));
        Assert.Equal("record-picker", Tile(Idle, "record-picker")!.Command);
        Assert.DoesNotContain(QuickPanelPlan.Footer(Idle), f => f.Command == "stop");
    }

    [Fact]
    public void Presenting_panel_has_toggles_that_show_their_state_and_keep_it_open()
    {
        Assert.False(Tile(Presenting, "blank")!.On);
        QuickTile blank = Tile(Presenting with { Blanked = true }, "blank")!;
        Assert.True(blank.On);
        Assert.True(blank.KeepOpen);
        Assert.True(Tile(Presenting with { Frozen = true }, "freeze")!.On);
        Assert.NotNull(Tile(Presenting, "crop"));
        Assert.True(Tile(Presenting with { Cropped = true }, "remove-crop")!.On);
        Assert.Equal("record-output", Tile(Presenting, "record-output")!.Command);
        Assert.Contains(QuickPanelPlan.Footer(Presenting), f => f.Command == "stop");
        Assert.False(Tile(Presenting, "picker")!.KeepOpen, "opening the picker closes the panel");
        Assert.Contains("the Zoom screen", Tile(Presenting with { Where = "the Zoom screen" }, "blank")!.Description);
    }

    [Fact]
    public void Recording_shows_a_red_stop_tile_and_pause()
    {
        var recording = Presenting with { Recording = RecordingStatus.Recording };
        QuickTile stop = Tile(recording, "stop-recording")!;
        Assert.Equal(TileTone.Recording, stop.Tone);
        Assert.True(stop.Enabled);
        Assert.Equal("Pause recording", Tile(recording, "pause-recording")!.Label);
        Assert.Null(Tile(recording, "record-output"));
        QuickTile resume = Tile(recording with { Recording = RecordingStatus.Paused }, "pause-recording")!;
        Assert.Equal("Resume recording", resume.Label);
        Assert.True(resume.On);
        Assert.False(Tile(recording with { Recording = RecordingStatus.Stopping }, "stop-recording")!.Enabled);
        Assert.False(Tile(recording with { Recording = RecordingStatus.Starting }, "pause-recording")!.Enabled);
        Assert.NotNull(Tile(Idle with { Recording = RecordingStatus.Recording }, "stop-recording")); // a background webinar
    }

    public static TheoryData<string> PanelStates() => ["idle", "again", "presenting", "blanked", "cropped", "recording", "paused", "idle-screen", "background"];

    private static QuickPanelState PanelState(string name) => name switch
    {
        "again" => Idle with { ShowAgainTitle = new string('x', 300) },
        "presenting" => Presenting,
        "blanked" => Presenting with { Blanked = true, Frozen = true },
        "cropped" => Presenting with { Cropped = true },
        "recording" => Presenting with { Recording = RecordingStatus.Recording },
        "paused" => Presenting with { Recording = RecordingStatus.Paused, Cropped = true, Blanked = true },
        "idle-screen" => Idle with { Session = SessionState.IdleScreen },
        "background" => Idle with { Recording = RecordingStatus.Recording, ShowAgainTitle = "Slides" },
        _ => Idle,
    };

    [Theory]
    [MemberData(nameof(PanelStates))]
    public void Every_tile_fits_runs_a_real_command_has_an_icon_and_plain_words(string name)
    {
        QuickPanelState state = PanelState(name);
        IReadOnlyList<QuickTile> tiles = QuickPanelPlan.Tiles(state);
        Assert.InRange(tiles.Count, 3, QuickPanelPlan.MaxTiles);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Command).Distinct().Count());
        foreach (QuickTile tile in tiles.Concat(QuickPanelPlan.Footer(state)))
        {
            Assert.True(TrayMenuPlan.IsKnownCommand(tile.Command), $"{tile.Label} runs unknown command {tile.Command}");
            Assert.Contains(tile.Icon, Icons.All);
            Assert.DoesNotMatch(Jargon, tile.Label);
            Assert.DoesNotMatch(Jargon, tile.Description ?? "");
            Assert.DoesNotContain(".", tile.Label.TrimEnd('…')); // no abbreviations like "rec."
            Assert.True(tile.Label.Length <= 22, $"“{tile.Label}” is too long for a tile");
            if (tile.Shortcut is { } shortcut) Assert.NotNull(ShortcutCatalog.Find(shortcut));
        }
    }

    // ───────────────────────────── icons ─────────────────────────────

    [Fact]
    public void Every_menu_and_tile_command_has_an_icon()
    {
        var commands = new HashSet<string>();
        foreach (QuickPanelState state in new[] { "idle", "again", "presenting", "blanked", "cropped", "recording", "paused", "idle-screen", "background" }.Select(PanelState))
            foreach (QuickTile tile in QuickPanelPlan.Tiles(state).Concat(QuickPanelPlan.Footer(state))) commands.Add(tile.Command);
        var tray = new TrayMenuState { Session = SessionState.Presenting, Screens = [new(@"\\.\DISPLAY2", "TV (Screen 2)", true)], Favourites = [new(1, "Slides", "POWERPNT")], Recording = RecordingStatus.Recording, RecordingLine = "● REC" };
        foreach (MenuEntry e in TrayMenuPlan.Flatten(TrayMenuPlan.Build(tray)).Concat(TrayMenuPlan.Flatten(TrayMenuPlan.Build(new TrayMenuState { ShowAgainTitle = "x" }))))
            if (e.Command is { } c) commands.Add(c);
        foreach (string command in commands) Assert.True(Icons.ForCommand(command) is not null, $"no icon for {command}");
        Assert.Null(Icons.ForCommand(null));
        Assert.Null(Icons.ForCommand("launch-rockets"));
    }

    [Fact]
    public void Every_icon_exists_in_Windows_icon_font()
    {
        if (Icons.Family is not { } family) return; // neither icon font: icons are left out, nothing to check
        nint dc = CreateCompatibleDC(0);
        nint font = CreateFontW(-24, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, family);
        nint old = SelectObject(dc, font);
        try
        {
            foreach (char icon in Icons.All)
            {
                var glyph = new ushort[1];
                Assert.Equal(1u, GetGlyphIndicesW(dc, icon.ToString(), 1, glyph, 1 /* GGI_MARK_NONEXISTING_GLYPHS */));
                Assert.True(glyph[0] != 0xFFFF, $"U+{(int)icon:X4} isn't in {family}");
            }
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
        }
        Assert.Equal(Icons.All.Count, Icons.All.Distinct().Count());
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetGlyphIndicesW(nint hdc, string text, int count, [Out] ushort[] glyphs, uint flags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    // ───────────────────────────── accent colour ─────────────────────────────

    public static TheoryData<int> Accents() =>
    [
        0x0078D4, // Windows blue
        0xFFB900, // yellow (white text would be unreadable)
        0xFFFFFF, // white
        0x000000, // black
        0x767676, // mid grey
        0x00CC6A, // light green
        0xE81123, // red
        0x881798, // purple
        0x2D7D9A, // teal
        0xF7F7F7, // nearly the light page colour
        0x101010, // nearly the dark page colour
    ];

    [Theory]
    [MemberData(nameof(Accents))]
    public void Any_Windows_accent_stays_readable_on_both_themes(int rgb)
    {
        Color accent = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        foreach (Palette page in new[] { Theme.Light, Theme.Dark })
        {
            Palette fitted = Theme.WithAccent(page, accent);
            Assert.True(AccentColour.Ratio(fitted.Accent, fitted.OnAccent) >= 4.5, $"button text on {fitted.Accent} in {page.Name}");
            Assert.True(AccentColour.Ratio(fitted.Accent, page.Background) >= 3, $"{fitted.Accent} stands out from the {page.Name} page");
            Assert.Equal(255, fitted.Accent.A);
            Assert.Equal(page.Outline, fitted.Outline); // the presented-source outline stays yellow
            Assert.Equal(page.Background, fitted.Background);
        }
    }

    [Fact]
    public void A_readable_accent_is_used_unchanged()
    {
        Color windowsBlue = Color.FromArgb(0x00, 0x5F, 0xB8);
        Assert.Equal(windowsBlue.ToArgb(), AccentColour.Fit(windowsBlue, Theme.Light.Background).Accent.ToArgb());
    }

    [Fact]
    public void Windows_accent_palette_and_DWM_value_are_read_as_Windows_does()
    {
        byte[] palette = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            palette[i * 4] = (byte)(i * 10);
            palette[i * 4 + 1] = (byte)(i * 10 + 1);
            palette[i * 4 + 2] = (byte)(i * 10 + 2);
            palette[i * 4 + 3] = 0xFF;
        }
        Assert.Equal(Color.FromArgb(40, 41, 42).ToArgb(), AccentColour.FromPalette(palette, dark: false)!.Value.ToArgb()); // Dark1
        Assert.Equal(Color.FromArgb(10, 11, 12).ToArgb(), AccentColour.FromPalette(palette, dark: true)!.Value.ToArgb()); // Light2
        Assert.Null(AccentColour.FromPalette(null, false));
        Assert.Null(AccentColour.FromPalette(new byte[12], false)); // malformed
        Assert.Equal(Color.FromArgb(0x11, 0x22, 0x33).ToArgb(), AccentColour.FromDwm(unchecked((int)0xFF332211)).ToArgb());
    }

    [Fact]
    public void Contrast_maths_matches_WCAG()
    {
        Assert.Equal(21, AccentColour.Ratio(Color.Black, Color.White), 1);
        Assert.Equal(1, AccentColour.Ratio(Color.Red, Color.Red), 3);
        Assert.Equal(Color.FromArgb(128, 128, 128).ToArgb(), AccentColour.Mix(Color.Black, Color.White, 0.5).ToArgb());
    }

    [Fact]
    public void Settings_remember_the_accent_choice_and_older_files_use_SCP_blue()
    {
        Assert.False(new AppSettings().UseWindowsAccent);
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "scp-accent-" + Guid.NewGuid().ToString("N"), "settings.json"));
        store.Load();
        store.Current.UseWindowsAccent = true;
        Assert.True(store.Save());
        var again = new SettingsStore(store.FilePath);
        Assert.True(again.Load().UseWindowsAccent);
        Directory.Delete(Path.GetDirectoryName(store.FilePath)!, recursive: true);
    }

    // ───────────────────────────── type ─────────────────────────────

    [Fact]
    public void Text_follows_the_Windows_11_type_ramp()
    {
        Assert.Equal(10.5f, Theme.BodyPoints); // 14 px
        Assert.Equal(9f, Theme.CaptionPoints); // 12 px
        Assert.Equal(15f, Theme.SubtitlePoints); // 20 px
        Assert.Equal(21f, Theme.TitlePoints); // 28 px
        using Font title = Theme.CreateFont(Theme.TitlePoints, FontStyle.Bold);
        using Font body = Theme.CreateFont(Theme.BodyPoints, FontStyle.Regular);
        Assert.Equal(Theme.TitlePoints, title.SizeInPoints, 1);
        if (title.FontFamily.Name.Contains("Semib", StringComparison.Ordinal))
            Assert.False(title.Bold, "semibold comes from the family, not synthetic bold");
        Assert.StartsWith("Segoe UI", body.FontFamily.Name);
        using Font pixels = Theme.CreateFont(28, FontStyle.Bold, GraphicsUnit.Pixel);
        Assert.Equal(GraphicsUnit.Pixel, pixels.Unit);
    }
}
