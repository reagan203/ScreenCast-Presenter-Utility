using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;

namespace ScreenCastPresenter.Tests;

/// <summary>The new things for the TV in the tray menu, the quick-actions panel and Search and help.</summary>
public class NewMenuTests
{
    private static readonly TrayMenuState Idle = new() { Session = SessionState.Off, Status = "Not showing anything" };
    private static readonly TrayMenuState Presenting = new() { Session = SessionState.Presenting, Status = "● Showing on TV: Slides", Where = "the TV" };

    private static List<MenuEntry> All(TrayMenuState s) => [.. TrayMenuPlan.Flatten(TrayMenuPlan.Build(s))];

    private static MenuEntry? Find(TrayMenuState s, string command) => All(s).FirstOrDefault(e => e.Command == command);

    [Fact]
    public void While_presenting_one_submenu_holds_the_extras_for_the_TV()
    {
        MenuEntry submenu = TrayMenuPlan.Build(Presenting).Single(e => e.Text == "On the TV");
        Assert.Equal(["zoom-in", "draw", "laser", "highlight-clicks", "inset"], submenu.Children!.Where(c => c.Command is not null).Select(c => c.Command!).Take(5));
        Assert.Contains(submenu.Children!, c => c.Text == "Blank with a countdown" && c.Children!.Any(m => m.Command == "countdown:5"));
        Assert.Equal("On the Zoom screen", TrayMenuPlan.Build(Presenting with { Where = "the Zoom screen" }).Single(e => e.Children is not null && e.Text.StartsWith("On ", StringComparison.Ordinal)).Text);
    }

    [Fact]
    public void The_extras_say_what_they_will_do_now()
    {
        Assert.Equal("Zoom out", Find(Presenting with { Zoomed = true }, "zoom-in")!.Text);
        Assert.Equal("Stop drawing", Find(Presenting with { Drawing = true }, "draw")!.Text);
        Assert.True(Find(Presenting with { LaserOn = true }, "laser")!.Checked);
        Assert.True(Find(Presenting with { ClicksShown = true }, "highlight-clicks")!.Checked);
        Assert.Equal("Take the small picture off", Find(Presenting with { HasInset = true }, "inset")!.Text);
    }

    [Fact]
    public void Nothing_for_the_TV_while_nothing_is_shown()
    {
        foreach (string command in new[] { "zoom-in", "draw", "laser", "inset", "countdown:5" })
            Assert.Null(Find(Idle, command));
    }

    [Fact]
    public void The_slideshow_is_offered_once_there_are_favourites()
    {
        Assert.Null(Find(Presenting, "slideshow"));
        var withFavourites = Presenting with { Favourites = [new TrayFavourite(1, "Slides", "POWERPNT"), new TrayFavourite(2, "Budget", "EXCEL")] };
        Assert.Equal("Show them one after another", Find(withFavourites, "slideshow")!.Text);
        Assert.Equal("Stop the slideshow", Find(withFavourites with { Slideshow = true }, "slideshow")!.Text);
        Assert.NotNull(Find(Idle with { Favourites = [new TrayFavourite(1, "Slides", "POWERPNT")] }, "slideshow"));
    }

    [Fact]
    public void The_panel_offers_zoom_and_drawing_while_presenting()
    {
        var presenting = new QuickPanelState { Session = SessionState.Presenting };
        IReadOnlyList<QuickTile> tiles = QuickPanelPlan.Tiles(presenting);
        Assert.Contains(tiles, t => t.Command == "zoom-in" && t.KeepOpen);
        Assert.Contains(tiles, t => t.Command == "draw" && !t.KeepOpen); // drawing happens on the screen, not in the panel
        Assert.True(QuickPanelPlan.Tiles(presenting with { Zoomed = true }).Single(t => t.Command == "zoom-in").On);
        Assert.Contains("the Zoom screen", QuickPanelPlan.Tiles(presenting with { Where = "the Zoom screen" }).Single(t => t.Command == "draw").Description);
    }

    [Fact]
    public void While_recording_the_recording_tiles_keep_their_room()
    {
        var recording = new QuickPanelState { Session = SessionState.Presenting, Recording = RecordingStatus.Recording };
        IReadOnlyList<QuickTile> tiles = QuickPanelPlan.Tiles(recording);
        Assert.InRange(tiles.Count, 1, QuickPanelPlan.MaxTiles);
        Assert.Contains(tiles, t => t.Command == "stop-recording");
        Assert.Contains(tiles, t => t.Command == "pause-recording");
        Assert.Contains(tiles, t => t.Command == "zoom");      // sharing in a meeting is still there
        Assert.DoesNotContain(tiles, t => t.Command == "draw");
    }

    [Theory]
    [InlineData("zoom in on the screen", "How do I zoom in on part of the screen?")]
    [InlineData("magnify", "How do I zoom in on part of the screen?")]
    [InlineData("draw", "How do I draw on the TV?")]
    [InlineData("laser", "How do I use a laser pointer?")]
    [InlineData("coffee break", "How do I show a countdown during a break?")]
    [InlineData("webcam corner", "How do I show my webcam in a corner?")]
    [InlineData("document camera", "How do I show a webcam or document camera?")]
    [InlineData("lobby", "How do I show several things one after another?")]
    [InlineData("password", "How do I make sure an app is never shown?")]
    [InlineData("click", "How do I show a window by clicking it?")]
    public void Help_finds_the_new_things_in_everyday_words(string query, string expected)
    {
        List<HelpTopic> found = PickerLayout.Filter(HelpTopics.All, query, t => t.Question, t => $"{t.Answer} how help {t.Keywords}");
        Assert.Contains(found, t => t.Question == expected);
    }

    [Fact]
    public void Every_new_action_is_in_Search_and_help()
    {
        // The palette lists every action except the numbered favourites and itself.
        foreach (string id in new[] { "pick-window", "zoom-in", "zoom-in-more", "highlight-clicks", "pointer-ring", "laser", "draw", "inset", "blank-countdown", "restart-timer", "slideshow", "check-updates" })
        {
            ShortcutAction action = ShortcutCatalog.Find(id)!;
            Assert.True(action.Available);
            Assert.NotEqual(ShortcutGroup.Favourites, action.Group);
        }
    }
}
