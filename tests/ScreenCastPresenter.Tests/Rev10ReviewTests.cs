using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>What the review of rev 10 found, kept fixed.</summary>
public class Rev10ReviewTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;

    public Rev10ReviewTests() => _session = new SessionController(_platform);

    private static SourceWindow Browser => TestData.Window(201, "News - Chrome", "chrome");

    [Fact]
    public async Task An_automatic_crop_is_applied_without_a_fade_and_told_apart()
    {
        await _session.PresentAsync(Browser, TestData.Tv, true);
        int opacitiesBefore = _platform.Engines[0].Opacities.Count;
        var page = new CropRegion(0, 0.12, 1, 0.88);
        int geometry = 0;
        _session.SourceGeometryChanged += (_, _) => geometry++;
        await _session.SetAutoCropAsync(page, page, fade: false);
        Assert.Equal(page, _platform.Engines[0].Crop);
        Assert.Equal(page, _session.Source!.AutoCrop);
        Assert.Null(_session.Source.ChosenCrop); // all of the page is "all of it"
        Assert.DoesNotContain(0.0, _platform.Engines[0].Opacities.Skip(opacitiesBefore)); // no trip through black
        Assert.Equal(1, geometry);
    }

    [Fact]
    public async Task Showing_the_same_source_again_takes_its_new_automatic_crop()
    {
        var clear = new CropRegion(0, 0, 1, 0.95);
        SourceWindow screen = SourceWindow.ForScreen(TestData.Laptop);
        await _session.PresentAsync(screen with { Crop = clear, AutoCrop = clear }, TestData.Tv, true);
        // The taskbar setting switched off, then shown again: no crop, and nothing automatic left behind.
        await _session.PresentAsync(screen, TestData.Tv, true);
        Assert.Null(_session.Source!.Crop);
        Assert.Null(_session.Source.AutoCrop);
        Assert.Single(_platform.Engines);
        // Only the automatic crop differs: it's taken, and the change is said.
        int geometry = 0;
        _session.SourceGeometryChanged += (_, _) => geometry++;
        await _session.SetAutoCropAsync(null, clear, fade: true);
        Assert.Equal(clear, _session.Source.AutoCrop);
        Assert.Equal(1, geometry);
    }

    [Fact]
    public void The_whiteboard_and_room_sign_are_kept_as_what_they_are()
    {
        Favourite board = FavouriteMatcher.FromSource(SourceWindow.ForPicture(@"C:\x\board-dark.png", "Whiteboard") with { IsBoard = true });
        Assert.Equal(Favourite.WhiteboardSpecial, board.Special);
        Assert.Null(board.PicturePath);
        Favourite sign = FavouriteMatcher.FromSource(SourceWindow.ForPicture(@"C:\x\room-sign.png", "Room sign") with { IsRoomSign = true });
        Assert.Equal(Favourite.RoomSignSpecial, sign.Special);
        Assert.True(board.Matches(new Favourite { AppName = "Picture", Title = "anything", Special = "whiteboard" }));
        Assert.False(board.Matches(sign));
        Assert.False(board.Matches(new Favourite { AppName = "Picture", Title = "Whiteboard", PicturePath = @"C:\x\board-dark.png" }));
        Assert.Null(FavouriteMatcher.FromSource(SourceWindow.ForPicture(@"C:\x\a.png")).Special);

        var tidy = new Favourite { AppName = "Picture", Title = "x", Special = "roomsign" };
        tidy.NormalizeRev10();
        Assert.Equal(Favourite.RoomSignSpecial, tidy.Special);
        var nonsense = new Favourite { AppName = "Picture", Title = "x", Special = "rocket" };
        nonsense.NormalizeRev10();
        Assert.Null(nonsense.Special);
    }

    [Fact]
    public void A_screen_whose_chosen_part_is_all_taskbar_falls_back_to_all_thats_clear_now()
    {
        // What RefreshTaskbarCropsAsync relies on: the part can't be kept (Inside says empty), and all that's clear
        // of the taskbar where it is now is worked out afresh, never an old crop.
        var screen = new Rectangle(0, 0, 1920, 1080);
        CropRegion nowClear = TaskbarCrop.ClearOf(screen, new Rectangle(0, 48, 1920, 1032))!; // the taskbar moved to the top
        CropRegion chosenNearTop = CropRegion.Create(0, 0, 1, 0.04)!;
        Assert.Equal((null, true), TaskbarCrop.Inside(chosenNearTop, nowClear));
        Assert.Equal((nowClear, false), TaskbarCrop.Inside(null, nowClear));
        Assert.Equal(new Rectangle(0, 48, 1920, 1032), nowClear.ToPixels(screen.Size));
    }
}

/// <summary>FR-128: words in Windows' speech dictionary, added and taken out again (a made-up word, so nothing of the presenter's is touched).</summary>
[Collection(DesktopWindowsCollection.Name)]
public class SpeechWordsTests
{
    [Fact]
    public void A_word_is_added_taken_out_and_one_already_there_is_left_alone()
    {
        string word = "Zqvx" + new string([.. Guid.NewGuid().ToString("N")[..6].Select(c => (char)('a' + c % 26))]);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using SpeechWords? first = SpeechWords.Add([word]);
                if (first is null) return; // no speech dictionary on this PC
                Assert.Equal([word], first.Added);
                using (SpeechWords? second = SpeechWords.Add([word]))
                    Assert.Empty(second!.Added); // already there: not ours to take out
                first.Dispose(); // taken out
                using SpeechWords? third = SpeechWords.Add([word]);
                Assert.Equal([word], third!.Added); // it was gone, so it's added again (and taken out at the end)
                SpeechWords.Remove([word]); // the clean-up after a helper was ended works too
                using SpeechWords? fourth = SpeechWords.Add([word]);
                Assert.Equal([word], fourth!.Added);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.Null(SpeechWords.Add([]));
    }
}

/// <summary>FR-24 (rev 10): one pointer on the presenter's screen, never two.</summary>
public class PictureCursorTests
{
    private const string Laptop = @"\.\DISPLAY1", Tv = @"\.\DISPLAY2";

    [Fact]
    public void The_pointer_is_in_the_picture_when_the_tv_is_elsewhere()
    {
        Assert.True(PictureCursor.Wanted(setting: true, laser: false, outputScreen: Tv, windowScreen: Laptop));
        Assert.True(PictureCursor.Wanted(true, false, outputScreen: null, windowScreen: Laptop)); // in a window shared in a meeting: the audience needs it
        Assert.True(PictureCursor.Wanted(true, false, Tv, windowScreen: null)); // a whole screen, a camera, a picture: as the setting says
    }

    [Fact]
    public void Covering_the_screen_the_window_is_on_leaves_it_out()
    {
        // No TV: the presentation covers the laptop's screen, where the shown window (and the real pointer) is.
        Assert.False(PictureCursor.Wanted(true, false, outputScreen: Laptop, windowScreen: Laptop));
        Assert.False(PictureCursor.Wanted(true, false, @"\.\display1", Laptop)); // any capitals
        Assert.True(PictureCursor.CoversOwnScreen(Laptop, Laptop));
        Assert.False(PictureCursor.CoversOwnScreen(Tv, Laptop));
        Assert.False(PictureCursor.CoversOwnScreen(null, Laptop));
    }

    [Fact]
    public void The_setting_and_the_laser_still_win()
    {
        Assert.False(PictureCursor.Wanted(setting: false, laser: false, Tv, Laptop)); // switched off
        Assert.False(PictureCursor.Wanted(setting: true, laser: true, Tv, Laptop)); // the laser stands in for it
    }
}
