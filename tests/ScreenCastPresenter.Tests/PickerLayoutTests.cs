using System.Drawing;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

public class PickerLayoutTests
{
    [Theory]
    [InlineData(1000, 190, 14, 4)]
    [InlineData(100, 190, 14, 1)]   // narrower than one card: still one column
    [InlineData(0, 190, 14, 1)]
    public void Columns(int width, int min, int gap, int expected) => Assert.Equal(expected, PickerLayout.Columns(width, min, gap));

    [Fact]
    public void Grid_places_cards_in_rows_without_overlap()
    {
        List<Rectangle> cards = PickerLayout.Grid(10, 1000, 4, 14, 150);
        Assert.Equal(10, cards.Count);
        Assert.Equal(new Rectangle(0, 0, 239, 150), cards[0]);
        Assert.Equal(164, cards[4].Top); // second row
        for (int i = 0; i < cards.Count; i++)
            for (int j = i + 1; j < cards.Count; j++)
                Assert.False(cards[i].IntersectsWith(cards[j]));
        Assert.Equal(3 * 150 + 2 * 14, PickerLayout.ContentHeight(10, 4, 14, 150));
        Assert.Empty(PickerLayout.Grid(0, 1000, 4, 14, 150));
    }

    [Fact]
    public void Fully_visible_thumbnail_shows_the_whole_source()
    {
        var dest = new Rectangle(10, 10, 160, 100);
        var clip = PickerLayout.Clip(dest, new Rectangle(0, 0, 500, 500), new Size(1600, 1000))!.Value;
        Assert.Equal(dest, clip.Destination);
        Assert.Equal(new Rectangle(0, 0, 1600, 1000), clip.Source);
    }

    [Fact]
    public void Half_scrolled_thumbnail_is_cropped_top_and_source_matches()
    {
        // Top half above the viewport (under the search box).
        var dest = new Rectangle(0, 50, 160, 100);
        var clip = PickerLayout.Clip(dest, new Rectangle(0, 100, 500, 500), new Size(1600, 1000))!.Value;
        Assert.Equal(new Rectangle(0, 100, 160, 50), clip.Destination);
        Assert.Equal(new Rectangle(0, 500, 1600, 500), clip.Source);
    }

    [Fact]
    public void Off_screen_or_empty_thumbnail_is_dropped()
    {
        Assert.Null(PickerLayout.Clip(new Rectangle(0, 600, 160, 100), new Rectangle(0, 0, 500, 500), new Size(1600, 1000)));
        Assert.Null(PickerLayout.Clip(new Rectangle(0, 0, 160, 100), new Rectangle(0, 0, 500, 500), Size.Empty)); // minimised
    }

    [Theory]
    [InlineData(0, Keys.Right, 1)]
    [InlineData(0, Keys.Left, 0)]       // clamps at the start
    [InlineData(1, Keys.Up, 1)]         // top row stays
    [InlineData(1, Keys.Down, 5)]
    [InlineData(6, Keys.Down, 9)]       // into a short last row: lands on the last card
    [InlineData(9, Keys.Right, 9)]      // clamps at the end
    [InlineData(5, Keys.Home, 0)]
    [InlineData(2, Keys.End, 9)]
    [InlineData(9, Keys.PageUp, 1)]     // same column, first row
    [InlineData(-1, Keys.Down, 0)]      // nothing selected yet
    public void Keyboard_movement(int from, Keys key, int expected) =>
        Assert.Equal(expected, PickerLayout.Move(from, 10, 4, 2, key));

    [Fact]
    public void Movement_in_an_empty_grid_selects_nothing() => Assert.Equal(-1, PickerLayout.Move(0, 0, 4, 2, Keys.Down));

    [Fact]
    public void Scroll_brings_a_card_into_view()
    {
        Assert.Equal(300, PickerLayout.ScrollToShow(new Rectangle(0, 300, 10, 100), 400, 200)); // above: scroll up
        Assert.Equal(200, PickerLayout.ScrollToShow(new Rectangle(0, 300, 10, 100), 0, 200));   // below: scroll down
        Assert.Equal(250, PickerLayout.ScrollToShow(new Rectangle(0, 300, 10, 100), 250, 200)); // visible: unchanged
    }

    private sealed record W(string Title, string App);

    private static readonly List<W> Windows =
    [
        new("Inbox - Mail", "olk"),
        new("Présentation trimestrielle - PowerPoint", "POWERPNT"),
        new("Budget 2027 - Excel", "EXCEL"),
        new("Notes about the budget", "notepad"),
        new("Weekly sync", "Zoom"),
    ];

    private static List<string> Search(string q) => PickerLayout.Filter(Windows, q, w => w.Title, w => w.App).Select(w => w.Title).ToList();

    [Fact]
    public void Empty_query_keeps_everything_in_order() => Assert.Equal(Windows.Select(w => w.Title), Search("  "));

    [Fact]
    public void Search_ignores_case_and_accents() => Assert.Equal(["Présentation trimestrielle - PowerPoint"], Search("PRESENTATION"));

    [Fact]
    public void Search_matches_app_names() => Assert.Equal(["Weekly sync"], Search("zoom"));

    [Fact]
    public void Every_word_must_match() => Assert.Equal(["Budget 2027 - Excel"], Search("budget excel"));

    [Fact]
    public void Title_prefix_ranks_first() => Assert.Equal(["Budget 2027 - Excel", "Notes about the budget"], Search("budget"));

    [Fact]
    public void No_match_gives_empty() => Assert.Empty(Search("nothing like this"));

    [Fact]
    public void Fold_handles_odd_input()
    {
        Assert.Equal("", PickerLayout.Fold(""));
        Assert.Equal("cafe", PickerLayout.Fold("CAFÉ"));
        Assert.NotNull(PickerLayout.Fold("bad \ud800 surrogate"));
    }
}
