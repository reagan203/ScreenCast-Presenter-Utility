using System.Drawing;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-05: click a window on your screen to show it.</summary>
public class PickWindowTests
{
    private static readonly SourceWindow Slides = TestData.Window(1, "Deck.pptx - PowerPoint", "POWERPNT");
    private static readonly SourceWindow Browser = TestData.Window(2, "Docs - Edge", "msedge");

    [Fact]
    public void The_front_window_under_the_pointer_wins()
    {
        List<PickTarget> frontToBack =
        [
            new(Slides, new Rectangle(100, 100, 400, 300)),
            new(Browser, new Rectangle(0, 0, 1920, 1080)),
        ];
        Assert.Same(Slides, WindowPick.At(frontToBack, new Point(200, 200))!.Window);
        Assert.Same(Browser, WindowPick.At(frontToBack, new Point(1000, 800))!.Window);
    }

    [Fact]
    public void Something_that_cant_be_shown_in_front_blocks_the_window_behind()
    {
        // The taskbar over a maximised window: the click lands on the taskbar, so nothing is chosen.
        List<PickTarget> frontToBack =
        [
            new(null, new Rectangle(0, 1040, 1920, 40)),
            new(Browser, new Rectangle(0, 0, 1920, 1080)),
        ];
        Assert.Null(WindowPick.At(frontToBack, new Point(500, 1060)));
        Assert.NotNull(WindowPick.At(frontToBack, new Point(500, 500)));
    }

    [Fact]
    public void The_desktop_and_empty_space_choose_nothing()
    {
        Assert.Null(WindowPick.At([new(Slides, new Rectangle(0, 0, 100, 100))], new Point(500, 500)));
        Assert.Null(WindowPick.At([], new Point(0, 0)));
    }

    [Fact]
    public void Zero_size_windows_are_never_hit()
    {
        List<PickTarget> frontToBack = [new(Slides, new Rectangle(10, 10, 0, 50)), new(Browser, new Rectangle(0, 0, 100, 100))];
        Assert.Same(Browser, WindowPick.At(frontToBack, new Point(10, 20))!.Window);
    }

    [Fact]
    public void Edges_follow_rectangle_rules()
    {
        List<PickTarget> one = [new(Slides, new Rectangle(100, 100, 50, 50))];
        Assert.NotNull(WindowPick.At(one, new Point(100, 100)));  // top-left pixel is inside
        Assert.Null(WindowPick.At(one, new Point(150, 120)));     // right edge is outside
    }

    [Fact]
    public void Windows_on_screens_left_of_the_main_one_are_found()
    {
        List<PickTarget> one = [new(Slides, new Rectangle(-1920, 0, 1920, 1080))];
        Assert.NotNull(WindowPick.At(one, new Point(-5, 5)));
    }

    [Fact]
    public void The_hint_names_the_window_or_says_what_to_do()
    {
        Assert.Contains("Deck.pptx", WindowPick.Hint(new PickTarget(Slides, Rectangle.Empty)));
        Assert.Contains("Point at the window", WindowPick.Hint(null));
        Assert.Contains("Esc", WindowPick.Hint(null));
        // A window with no title is named by its app.
        Assert.Contains("msedge", WindowPick.Hint(new PickTarget(TestData.Window(3, "", "msedge"), Rectangle.Empty)));
        // Long titles are shortened so the hint stays on one line.
        string longTitle = new('x', 200);
        Assert.True(WindowPick.Hint(new PickTarget(TestData.Window(4, longTitle), Rectangle.Empty)).Length < 120);
    }

    [Fact]
    public void Clicking_is_a_shortcut_with_its_own_keys()
    {
        ShortcutAction pick = ShortcutCatalog.Find("pick-window")!;
        Assert.Equal("Ctrl+Alt+Shift+W", pick.Default!.Value.ToString());
        Assert.NotNull(UI.Icons.ForCommand("pick-window"));
    }
}
