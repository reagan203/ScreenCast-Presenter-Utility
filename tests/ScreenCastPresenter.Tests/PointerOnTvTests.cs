using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Tests;

/// <summary>"Mouse pointer on the TV" really shows the pointer, on windows too.</summary>
public class PointerOnTvTests
{
    [Fact]
    public void A_window_is_shown_with_the_pointer_when_it_is_switched_on() =>
        Assert.True(CaptureChoice.DrawsPointerAndScreens(isScreen: false, startFrozen: false, preferWgc: false, showPointer: true));

    [Fact]
    public void Only_with_the_pointer_off_does_a_window_use_the_light_way() =>
        Assert.False(CaptureChoice.DrawsPointerAndScreens(isScreen: false, startFrozen: false, preferWgc: false, showPointer: false));

    [Theory]
    [InlineData(true, false, false)]  // a whole screen
    [InlineData(false, true, false)]  // freezing
    [InlineData(false, false, true)]  // chosen in Advanced
    public void Screens_freezing_and_the_advanced_choice_always_can(bool isScreen, bool frozen, bool prefer) =>
        Assert.True(CaptureChoice.DrawsPointerAndScreens(isScreen, frozen, prefer, showPointer: false));

    [Fact]
    public void The_pointer_is_on_by_default() => Assert.True(new AppSettings().ShowCursor);
}
