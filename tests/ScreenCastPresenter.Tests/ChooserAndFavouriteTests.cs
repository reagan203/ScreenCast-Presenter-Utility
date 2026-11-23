using System.Drawing;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

internal static class TestData
{
    public static DisplayMonitor Laptop => new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1200), new Rectangle(0, 0, 1920, 1152), true, 1.25, "", "Built-in");
    public static DisplayMonitor Tv => new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080), false, 1.0, "SAMSUNG", "HDMI");
    public static DisplayMonitor Third => new(@"\\.\DISPLAY3", new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1040), false, 1.0);

    public static SourceWindow Window(nint handle, string title, string app = "notepad") => new(handle, title, app, false);
}

public class FavouriteMatcherTests
{
    private static readonly List<SourceWindow> Open =
    [
        TestData.Window(1, "Inbox - Mail", "olk"),
        TestData.Window(2, "Quarterly review.pptx [Saved] - PowerPoint", "POWERPNT"),
        TestData.Window(3, "Budget.xlsx - Excel", "EXCEL"),
        TestData.Window(4, "Notes.pptx - PowerPoint", "POWERPNT"),
    ];

    [Fact]
    public void Exact_match_wins_even_if_not_most_recent()
    {
        var fav = new Favourite { AppName = "powerpnt", Title = "Notes.pptx - PowerPoint" };
        Assert.Equal(4, FavouriteMatcher.FindBest(fav, Open)!.Handle);
    }

    [Fact]
    public void Similar_title_matches_after_the_title_changed()
    {
        var fav = new Favourite { AppName = "POWERPNT", Title = "Quarterly review.pptx - PowerPoint" };
        Assert.Equal(2, FavouriteMatcher.FindBest(fav, Open)!.Handle);
    }

    [Fact]
    public void Falls_back_to_the_most_recent_window_of_the_app()
    {
        var fav = new Favourite { AppName = "POWERPNT", Title = "Something else entirely" };
        Assert.Equal(2, FavouriteMatcher.FindBest(fav, Open)!.Handle);
    }

    [Fact]
    public void App_not_running_gives_null() =>
        Assert.Null(FavouriteMatcher.FindBest(new Favourite { AppName = "Code", Title = "x" }, Open));

    [Theory]
    [InlineData("Deck.pptx - PowerPoint", "Deck.pptx [Saved] - PowerPoint", true)]
    [InlineData("Deck.pptx — PowerPoint", "Deck.pptx - PowerPoint", true)]
    [InlineData("Deck", "Other", false)]
    [InlineData("", "x", false)]
    public void Similarity(string a, string b, bool expected) => Assert.Equal(expected, FavouriteMatcher.IsSimilar(a, b));

    [Theory]
    [InlineData("● *Quarterly review.pptx [Saved] - PowerPoint", "Quarterly review.pptx")]
    [InlineData("Report.docx [Compatibility Mode] - Word", "Report.docx")]
    [InlineData("main.cs* — Visual Studio Code", "main.cs")]
    [InlineData("Inbox", "Inbox")]
    public void Head_strips_status_markers(string title, string expected) => Assert.Equal(expected, FavouriteMatcher.Head(title));
}

public class OutputChooserTests
{
    [Fact]
    public void Prefers_saved_screen_then_a_non_primary_one()
    {
        var both = new List<DisplayMonitor> { TestData.Laptop, TestData.Tv };
        Assert.Equal(TestData.Tv, OutputChooser.Choose(both, null));
        Assert.Equal(TestData.Laptop, OutputChooser.Choose(both, @"\\.\DISPLAY1"));
        Assert.Equal(TestData.Tv, OutputChooser.Choose(both, @"\\.\DISPLAY9")); // saved TV not connected
        Assert.Equal(TestData.Laptop, OutputChooser.Choose([TestData.Laptop], null));
        Assert.Null(OutputChooser.Choose([], null));
    }

    [Fact]
    public void Next_wraps_around()
    {
        var all = new List<DisplayMonitor> { TestData.Laptop, TestData.Tv, TestData.Third };
        Assert.Equal(TestData.Tv, OutputChooser.Next(all, TestData.Laptop));
        Assert.Equal(TestData.Laptop, OutputChooser.Next(all, TestData.Third));
        Assert.Equal(TestData.Laptop, OutputChooser.Next(all, null));
    }

    [Fact]
    public void Presenter_screen_is_never_the_output_unless_it_is_the_only_one()
    {
        var both = new List<DisplayMonitor> { TestData.Laptop, TestData.Tv };
        Assert.Equal(TestData.Laptop, OutputChooser.PresenterScreen(both, TestData.Tv, underMouse: TestData.Tv));
        Assert.Equal(TestData.Laptop, OutputChooser.PresenterScreen(both, TestData.Tv, underMouse: null));
        Assert.Equal(TestData.Tv, OutputChooser.PresenterScreen(both, TestData.Laptop, underMouse: TestData.Tv));
        Assert.Equal(TestData.Laptop, OutputChooser.PresenterScreen([TestData.Laptop], TestData.Laptop, TestData.Laptop));
    }

    [Fact]
    public void Cycle_is_stable_and_wraps()
    {
        var windows = new List<SourceWindow>
        {
            TestData.Window(30, "b", "zeta"),
            TestData.Window(10, "a", "alpha"),
            TestData.Window(20, "c", "alpha"),
        };
        Assert.Equal(10, OutputChooser.Cycle(windows, null, +1)!.Handle);
        Assert.Equal(20, OutputChooser.Cycle(windows, windows[1], +1)!.Handle);
        Assert.Equal(10, OutputChooser.Cycle(windows, windows[0], +1)!.Handle);   // wraps
        Assert.Equal(30, OutputChooser.Cycle(windows, windows[1], -1)!.Handle);   // wraps backwards
        Assert.Null(OutputChooser.Cycle([], null, 1));
    }
}

public class OutputPlacementTests
{
    private static DisplayMonitor Virtual => new(@"\.\DISPLAY4", new Rectangle(3840, 0, 1920, 1080), new Rectangle(3840, 0, 1920, 1080), false, 1.0, "Virtual Display", "Virtual");

    [Fact]
    public void Virtual_displays_are_called_Zoom_screen()
    {
        Assert.True(Virtual.IsVirtual);
        Assert.Equal("Zoom screen", Virtual.ShortName);
        Assert.True(new DisplayMonitor(@"\.\DISPLAY5", default, default, false, 1, "IddSampleDriver Device", "").IsVirtual); // named, not flagged
        Assert.False(TestData.Tv.IsVirtual);
        Assert.False(TestData.Laptop.IsVirtual);
    }

    [Fact]
    public void A_real_tv_is_chosen_before_a_virtual_display()
    {
        Assert.Equal(TestData.Tv, OutputChooser.Choose([TestData.Laptop, Virtual, TestData.Tv], null));
        Assert.Equal(Virtual, OutputChooser.Choose([TestData.Laptop, Virtual], null)); // no TV: Zoom screen
        Assert.Equal(Virtual, OutputChooser.Choose([TestData.Laptop, Virtual, TestData.Tv], Virtual.DeviceName)); // the user's choice wins
    }

    [Fact]
    public void Output_choices_cycle_through_screens_then_the_window()
    {
        IReadOnlyList<DisplayMonitor> screens = [TestData.Laptop, TestData.Tv];
        OutputOption tv = OutputOption.Next(screens, new OutputOption(TestData.Laptop));
        Assert.Equal(TestData.Tv, tv.Screen);
        OutputOption window = OutputOption.Next(screens, tv);
        Assert.True(window.IsWindow);
        Assert.Equal(TestData.Laptop, OutputOption.Next(screens, window).Screen); // wraps
        Assert.Equal(TestData.Laptop, OutputOption.Next(screens, null).Screen);
        Assert.True(OutputOption.Next([], null).IsWindow); // no screens known: the window still works
    }

    [Fact]
    public void Default_window_is_a_16_by_9_window_in_the_corner_clear_of_the_taskbar()
    {
        Rectangle r = OutputPlacement.DefaultWindow(TestData.Laptop);
        Assert.True(TestData.Laptop.WorkArea.Contains(r), r.ToString());
        Assert.True(r.Width >= OutputPlacement.MinimumWindow.Width && r.Height >= OutputPlacement.MinimumWindow.Height);
        Assert.True(r.Right > TestData.Laptop.WorkArea.Width * 3 / 4 && r.Bottom > TestData.Laptop.WorkArea.Height * 3 / 4); // bottom-right
    }

    [Fact]
    public void Saved_window_positions_are_restored_only_when_still_reachable()
    {
        IReadOnlyList<DisplayMonitor> screens = [TestData.Laptop, TestData.Tv];
        var onTv = new Rectangle(2000, 100, 800, 450);
        Assert.Equal(onTv, OutputPlacement.Restore(onTv, screens));
        Assert.Null(OutputPlacement.Restore(onTv, [TestData.Laptop]));                     // TV unplugged: title bar unreachable
        Assert.Null(OutputPlacement.Restore(new Rectangle(100, -500, 800, 450), screens));  // title bar above the screen
        Assert.Null(OutputPlacement.Restore(new Rectangle(100, 100, 100, 50), screens));    // too small
        Assert.Null(OutputPlacement.Restore(new Rectangle(0, 0, 5000, 3000), screens));     // bigger than any screen
        Assert.Null(OutputPlacement.Restore(null, screens));
    }
}
