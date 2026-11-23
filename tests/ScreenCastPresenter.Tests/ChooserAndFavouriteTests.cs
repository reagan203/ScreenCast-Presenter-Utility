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
