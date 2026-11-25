using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>The floating output window steps behind while you work in what it shows, and comes back after.</summary>
public class StepBackTests
{
    private const nint Source = 100, Other = 200, Output = 300;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_window_a_meeting_could_share_never_steps_back(bool inPresented, bool onShownScreen) =>
        // Privacy first: some meeting apps copy the window's area of the screen, so a covered window would leak what covers it.
        Assert.False(FloatingStepBack.ShouldStepBack(shareable: true, inPresented ? Source : Other, Source, false, onShownScreen));

    [Fact]
    public void Working_in_the_presented_window_or_its_dialogs_steps_back() =>
        Assert.True(FloatingStepBack.ShouldStepBack(shareable: false, activeRoot: Source, sourceRoot: Source, activeIsOutput: false, activeOnShownScreen: false));

    [Fact]
    public void Switching_to_another_app_brings_it_back() =>
        Assert.False(FloatingStepBack.ShouldStepBack(shareable: false, Other, Source, false, activeOnShownScreen: true));

    [Fact]
    public void Going_to_the_output_window_itself_keeps_it_in_front() =>
        Assert.False(FloatingStepBack.ShouldStepBack(shareable: false, Output, Source, activeIsOutput: true, false));

    [Fact]
    public void Nothing_active_counts_as_somewhere_else() =>
        Assert.False(FloatingStepBack.ShouldStepBack(shareable: false, 0, Source, false, false));

    [Theory]
    [InlineData(true, true)]   // anything on the presented screen
    [InlineData(false, false)] // on another screen
    public void For_a_whole_screen_it_steps_back_while_you_work_on_that_screen(bool onShownScreen, bool expected) =>
        Assert.Equal(expected, FloatingStepBack.ShouldStepBack(shareable: false, Other, sourceRoot: 0, false, onShownScreen));

    [Fact]
    public void The_real_window_steps_behind_without_being_hidden_or_minimised_and_comes_back_on_top()
    {
        DisplayMonitor screen = DisplayHelpers.GetMonitors().First();
        using var window = new OutputWindow(new OutputPlacement(screen, TopMost: true, OutputPlacement.DefaultWindow(screen)));
        window.CreateControl();
        var hwnd = (HWND)window.Handle;
        bool TopMost() => ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);

        window.ShowCloaked(); // shown but never visible: created cloaked
        Assert.True(TopMost());
        window.StepBack(true);
        Assert.False(TopMost());
        Assert.False(window.IsMinimized, "a minimised window would share nothing");
        Assert.True(window.Visible, "still open, so a meeting keeps sharing it");
        window.StepBack(false);
        Assert.True(TopMost(), "back on top (Keep the window on top)");
    }

    [Fact]
    public void A_full_screen_output_never_steps_back()
    {
        DisplayMonitor screen = DisplayHelpers.GetMonitors().First();
        using var window = new OutputWindow(new OutputPlacement(screen, TopMost: true));
        window.CreateControl();
        var hwnd = (HWND)window.Handle;
        window.StepBack(true);
        Assert.True(((WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST));
    }
}
