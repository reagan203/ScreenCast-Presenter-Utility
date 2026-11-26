using System.Drawing;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>Apps that are never shown: not chosen as a window, and a whole screen goes black while one is open on it.</summary>
public class NeverShowTests
{
    private static readonly List<string> List = ["KeePass", "olk"];

    [Fact]
    public void Apps_match_by_program_name_ignoring_case()
    {
        Assert.True(NeverShow.IsBlocked(List, "keepass"));
        Assert.True(NeverShow.IsBlocked(List, "OLK"));
        Assert.False(NeverShow.IsBlocked(List, "KeePassXC")); // a different program, not a prefix match
        Assert.False(NeverShow.IsBlocked(List, ""));
        Assert.False(NeverShow.IsBlocked(List, null));
        Assert.False(NeverShow.IsBlocked([], "KeePass"));
    }

    private static readonly Rectangle Laptop = new(0, 0, 1920, 1200);

    [Fact]
    public void A_never_show_window_on_the_shown_screen_is_found()
    {
        List<VisibleWindow> frontToBack =
        [
            new(1, "notepad", new Rectangle(100, 100, 800, 600)),
            new(2, "KeePass", new Rectangle(400, 300, 600, 400)), // behind notepad, still "open on the screen"
        ];
        Assert.Equal(2, NeverShow.FindOnScreen(frontToBack, Laptop, List)!.Handle);
    }

    [Fact]
    public void One_on_another_screen_doesnt_count()
    {
        List<VisibleWindow> frontToBack = [new(2, "KeePass", new Rectangle(1920, 0, 800, 600))];
        Assert.Null(NeverShow.FindOnScreen(frontToBack, Laptop, List)); // touching the edge isn't overlapping
    }

    [Fact]
    public void Only_the_part_of_the_screen_shown_matters()
    {
        var topHalf = new Rectangle(0, 0, 1920, 600);
        Assert.Null(NeverShow.FindOnScreen([new(2, "KeePass", new Rectangle(0, 700, 400, 300))], topHalf, List));
        Assert.NotNull(NeverShow.FindOnScreen([new(2, "KeePass", new Rectangle(0, 590, 400, 300))], topHalf, List)); // ten pixels in is enough
    }

    [Fact]
    public void A_window_straddling_two_screens_counts_on_both()
    {
        var straddling = new VisibleWindow(2, "KeePass", new Rectangle(1800, 100, 400, 300));
        Assert.NotNull(NeverShow.FindOnScreen([straddling], Laptop, List));
        Assert.NotNull(NeverShow.FindOnScreen([straddling], new Rectangle(1920, 0, 1920, 1080), List));
    }

    [Fact]
    public void Empty_lists_and_shapes_find_nothing()
    {
        List<VisibleWindow> keepass = [new(2, "KeePass", new Rectangle(0, 0, 100, 100))];
        Assert.Null(NeverShow.FindOnScreen(keepass, Laptop, []));
        Assert.Null(NeverShow.FindOnScreen(keepass, Rectangle.Empty, List));
        Assert.Null(NeverShow.FindOnScreen([new(2, "KeePass", new Rectangle(10, 10, 0, 100))], Laptop, List));
    }

    [Fact]
    public void Lists_from_a_file_are_tidied()
    {
        List<string> tidy = NeverShow.Normalize(["  KeePass ", "keepass", "", null, "olk.exe", "OLK", new string('x', 500)]);
        Assert.Equal(["KeePass", "olk"], tidy);
        Assert.Equal(NeverShow.MaxApps, NeverShow.Normalize(Enumerable.Range(0, 500).Select(i => $"app{i}")).Count);
        Assert.Empty(NeverShow.Normalize(null));
    }

    [Fact]
    public void Password_managers_are_on_the_list_from_the_start_and_for_older_files()
    {
        Assert.Contains("KeePass", new AppSettings().NeverShowApps);
        Assert.Contains("1Password", new AppSettings().NeverShowApps);
        Assert.Equal(NeverShow.Defaults.Count, NeverShow.Defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string dir = Path.Combine(Path.GetTempPath(), "scp-nevershow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"Version\": 2, \"Dim\": 0.1 }"); // written before the list existed
            var store = new SettingsStore(path);
            Assert.Contains("Bitwarden", store.Load().NeverShowApps);

            // Emptied on purpose: stays empty.
            store.Current.NeverShowApps = [];
            Assert.True(store.Save());
            Assert.Empty(new SettingsStore(path).Load().NeverShowApps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>The session holding the picture black for independent reasons (a browser's other page, a never-show app).</summary>
public class HoldTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;

    public HoldTests() => _session = new SessionController(_platform);

    private static SourceWindow Screen => SourceWindow.ForScreen(TestData.Laptop);
    private static SourceWindow Browser => TestData.Window(5, "Docs - Edge", "msedge");

    [Fact]
    public async Task Something_already_private_on_the_screen_is_never_seen_even_for_a_fade()
    {
        _session.HoldCheck = s => s.IsScreen ? HoldReason.PrivateApp : null;
        await _session.PresentAsync(Screen, TestData.Tv, true);
        Assert.True(_session.IsHeldFor(HoldReason.PrivateApp));
        Assert.All(_platform.Engines[^1].Opacities, o => Assert.Equal(0, o)); // never above black
        Assert.Equal(SessionState.Presenting, _session.State);
    }

    [Fact]
    public async Task Each_reason_must_go_before_the_picture_comes_back()
    {
        await _session.PresentAsync(Browser, TestData.Tv, true);
        _session.CutAway(HoldReason.OtherPage);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.ComeBackAsync(HoldReason.OtherPage);
        Assert.True(_session.IsHeld);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);
        await _session.ComeBackAsync(HoldReason.PrivateApp);
        Assert.False(_session.IsHeld);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task A_reason_that_isnt_holding_changes_nothing()
    {
        await _session.PresentAsync(Browser, TestData.Tv, true);
        int fades = _platform.Fades.Count;
        await _session.ComeBackAsync(HoldReason.PrivateApp);
        Assert.Equal(fades, _platform.Fades.Count);
    }

    [Fact]
    public async Task Coming_back_while_blank_stays_blank()
    {
        await _session.PresentAsync(Screen, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: false);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.ComeBackAsync(HoldReason.PrivateApp);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);
        await _session.ToggleBlankAsync(withMessage: false);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task Unblanking_while_held_stays_black()
    {
        await _session.PresentAsync(Screen, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: false);
        _session.CutAway(HoldReason.PrivateApp); // no effect on a blank picture yet…
        await _session.ToggleBlankAsync(withMessage: false);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]); // …but showing again doesn't reveal it
    }

    [Fact]
    public async Task Dimming_while_held_stays_black()
    {
        await _session.PresentAsync(Screen, TestData.Tv, true);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.SetDimAsync(0.2);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);
    }

    [Fact]
    public async Task Something_new_is_checked_afresh()
    {
        await _session.PresentAsync(Screen, TestData.Tv, true);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.PresentAsync(Browser, TestData.Tv, true); // no check: a window isn't a screen
        Assert.False(_session.IsHeld);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task The_browser_page_cut_still_works_as_before()
    {
        await _session.PresentAsync(Browser, TestData.Tv, true);
        _session.CutAway();
        Assert.True(_session.IsAwayFromPage);
        Assert.False(_session.IsHeldFor(HoldReason.PrivateApp));
        await _session.ComeBackAsync();
        Assert.False(_session.IsAwayFromPage);
    }
}
