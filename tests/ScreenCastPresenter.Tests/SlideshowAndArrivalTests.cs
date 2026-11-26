using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-69: favourites one after another.</summary>
public class SlideshowTests
{
    private static Favourite Fav(string app) => new() { AppName = app, Title = app };

    private static Favourite?[] Slots(params Favourite?[] used)
    {
        var slots = new Favourite?[AppSettings.SlotCount];
        Array.Copy(used, slots, used.Length);
        return slots;
    }

    [Fact]
    public void Starts_at_the_first_and_goes_round_skipping_empty_slots()
    {
        Favourite?[] slots = Slots(Fav("a"), null, Fav("c"), null, Fav("e"));
        Assert.Equal(1, Slideshow.Next(slots, null, _ => true));
        Assert.Equal(3, Slideshow.Next(slots, 1, _ => true));
        Assert.Equal(5, Slideshow.Next(slots, 3, _ => true));
        Assert.Equal(1, Slideshow.Next(slots, 5, _ => true)); // round again
    }

    [Fact]
    public void Ones_that_cant_be_shown_now_are_skipped()
    {
        Favourite?[] slots = Slots(Fav("a"), Fav("closed"), Fav("c"));
        Assert.Equal(3, Slideshow.Next(slots, 1, f => f.AppName != "closed"));
    }

    [Fact]
    public void Only_one_left_keeps_showing_it()
    {
        Favourite?[] slots = Slots(Fav("a"), Fav("closed"));
        Assert.Equal(1, Slideshow.Next(slots, 1, f => f.AppName != "closed"));
    }

    [Fact]
    public void None_to_show_means_none()
    {
        Assert.Null(Slideshow.Next(Slots(), null, _ => true));
        Assert.Null(Slideshow.Next(Slots(Fav("a")), null, _ => false));
        Assert.Null(Slideshow.Next([], null, _ => true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-4)]
    public void A_strange_current_slot_starts_from_the_first(int current) =>
        Assert.Equal(2, Slideshow.Next(Slots(null, Fav("b")), current, _ => true));

    [Fact]
    public void Times_read_naturally_and_snap_to_the_offered_ones()
    {
        Assert.Equal("15 seconds", Slideshow.Words(15));
        Assert.Equal("1 minute", Slideshow.Words(60));
        Assert.Equal("5 minutes", Slideshow.Words(300));
        Assert.Equal(10, Slideshow.Normalize(1));
        Assert.Equal(300, Slideshow.Normalize(100_000));
        Assert.Equal(15, new AppSettings().SlideshowSeconds);
    }

    [Fact]
    public void It_has_a_shortcut_that_isnt_a_favourite_number()
    {
        ShortcutAction slideshow = ShortcutCatalog.Find("slideshow")!;
        Assert.Equal("Ctrl+Alt+L", slideshow.Default!.Value.ToString());
        Assert.DoesNotMatch(@"\d", slideshow.Name);
    }
}

/// <summary>FR-70: noticing a TV being plugged in.</summary>
public class ScreenArrivalTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);
    private const string Laptop = "DISPLAY1", Tv = "DISPLAY2";

    [Fact]
    public void Screens_there_at_the_start_arent_new()
    {
        var arrivals = new ScreenArrivals([Laptop, Tv]);
        Assert.Empty(arrivals.Update([Laptop, Tv], T0));
    }

    [Fact]
    public void A_screen_plugged_in_later_is_new_once()
    {
        var arrivals = new ScreenArrivals([Laptop]);
        Assert.Equal([Tv], arrivals.Update([Laptop, Tv], T0));
        Assert.Empty(arrivals.Update([Laptop, Tv], T0.AddSeconds(5))); // a resolution change isn't another arrival
    }

    [Fact]
    public void A_flickering_cable_isnt_an_arrival()
    {
        var arrivals = new ScreenArrivals([Laptop, Tv]);
        Assert.Empty(arrivals.Update([Laptop], T0));
        Assert.Empty(arrivals.Update([Laptop, Tv], T0.AddSeconds(3)));
    }

    [Fact]
    public void The_same_name_back_after_a_while_is_a_new_screen()
    {
        // Unplugged from the desk monitor, plugged into the meeting-room TV: Windows calls both Screen 2.
        var arrivals = new ScreenArrivals([Laptop, Tv]);
        Assert.Empty(arrivals.Update([Laptop], T0));
        Assert.Equal([Tv], arrivals.Update([Laptop, Tv], T0 + ScreenArrivals.AwayLongEnough));
    }

    [Fact]
    public void Names_are_compared_like_Windows_does()
    {
        var arrivals = new ScreenArrivals(["display1"]);
        Assert.Empty(arrivals.Update([Laptop], T0));
    }

    [Fact]
    public void A_screen_already_dealt_with_isnt_offered_as_well()
    {
        var arrivals = new ScreenArrivals([Laptop]);
        arrivals.Know(Tv); // a copying TV was just made separate, and that said so
        Assert.Empty(arrivals.Update([Laptop, Tv], T0));
    }

    [Fact]
    public void Offered_by_default() => Assert.True(new AppSettings().OfferNewScreen);
}
