using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// "It should stick to what I chose": a browser keeps to the page chosen, and a screen plugged in
/// (HDMI) while set to show in a window gets the presentation instead of showing the desktop.
/// </summary>
public class StickToChosenTests
{
    // ───────────────────────────── browsers ─────────────────────────────

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("CHROME", true)]
    [InlineData("msedge", true)]
    [InlineData("firefox", true)]
    [InlineData("brave", true)]
    [InlineData("chrome.exe", true)]
    [InlineData("POWERPNT", false)]
    [InlineData("notepad", false)]
    [InlineData("Code", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Browsers_are_recognised_by_their_app(string? app, bool browser) =>
        Assert.Equal(browser, BrowserTab.IsBrowser(app));

    [Theory]
    [InlineData("MFI Edge - Google Chrome", "MFI Edge - Google Chrome", true)]
    [InlineData("Inbox - Gmail - Google Chrome", "(3) Inbox - Gmail - Google Chrome", true)] // an unread counter appears
    [InlineData("(12) Inbox - Outlook", "(13) Inbox - Outlook", true)]
    [InlineData("• Chat - Teams", "Chat - Teams", true)]
    [InlineData("Report  -  Google Chrome", "Report - Google Chrome", true)]
    [InlineData("MFI Edge - Google Chrome", "New Tab - Google Chrome", false)]          // switched tabs
    [InlineData("MFI Edge - Google Chrome", "Nairobi Fridge Options - Kimi - Google Chrome", false)]
    [InlineData("Slides - Google Chrome", "Slides (2) - Google Chrome", false)]         // a counter elsewhere is part of the page
    [InlineData("", "", false)]                                                          // no title: never "the same"
    [InlineData(null, "Anything", false)]
    public void Pages_are_told_apart_by_title_ignoring_unread_counters(string? chosen, string? now, bool same) =>
        Assert.Equal(same, BrowserTab.SamePage(chosen, now));

    // ───────────────────────────── a screen plugged in ─────────────────────────────

    private static readonly DisplayMonitor Laptop = TestData.Laptop, Tv = TestData.Tv, Monitor = TestData.Third;
    private static DisplayMonitor ZoomScreen => new(@"\\.\DISPLAY4", new Rectangle(3840, 0, 1920, 1080), new Rectangle(3840, 0, 1920, 1080), false, 1.0, "Virtual Display", "Virtual");

    [Fact]
    public void An_HDMI_TV_plugged_in_after_choosing_the_window_is_offered()
    {
        Assert.Equal(Tv.DeviceName, OutputChooser.NewScreenSinceWindowChosen([Laptop, Tv], [Laptop.DeviceName], showingInWindow: true)?.DeviceName);
    }

    [Fact]
    public void Older_settings_without_the_list_count_only_the_main_screen()
    {
        // This is the reported case: settings from before, set to "a window", then an HDMI TV.
        Assert.Equal(Tv.DeviceName, OutputChooser.NewScreenSinceWindowChosen([Laptop, Tv], null, showingInWindow: true)?.DeviceName);
    }

    [Fact]
    public void A_screen_that_was_there_when_the_window_was_chosen_isnt_offered_again()
    {
        Assert.Null(OutputChooser.NewScreenSinceWindowChosen([Laptop, Tv], [Laptop.DeviceName, Tv.DeviceName], showingInWindow: true));
    }

    [Fact]
    public void Nothing_is_offered_when_not_showing_in_a_window_or_with_one_screen()
    {
        Assert.Null(OutputChooser.NewScreenSinceWindowChosen([Laptop, Tv], null, showingInWindow: false));
        Assert.Null(OutputChooser.NewScreenSinceWindowChosen([Laptop], null, showingInWindow: true));
        Assert.Null(OutputChooser.NewScreenSinceWindowChosen([], null, showingInWindow: true));
    }

    [Fact]
    public void A_virtual_Zoom_screen_is_never_offered_and_a_TV_comes_before_a_monitor()
    {
        Assert.Null(OutputChooser.NewScreenSinceWindowChosen([Laptop, ZoomScreen], null, showingInWindow: true));
        Assert.Equal(Tv.DeviceName, OutputChooser.NewScreenSinceWindowChosen([Laptop, Monitor, Tv], null, showingInWindow: true)?.DeviceName);
    }

    [Fact]
    public void Settings_keep_to_the_page_by_default_and_remember_the_screens()
    {
        var settings = new AppSettings();
        Assert.True(settings.KeepBrowserPage);
        Assert.Null(settings.WindowModeScreens);
        string dir = Path.Combine(Path.GetTempPath(), "scp-stick-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        store.Load();
        store.Current.KeepBrowserPage = false;
        store.Current.WindowModeScreens = [@"\\.\DISPLAY1"];
        Assert.True(store.Save());
        AppSettings again = new SettingsStore(store.FilePath).Load();
        Assert.False(again.KeepBrowserPage);
        Assert.Equal([@"\\.\DISPLAY1"], again.WindowModeScreens!);
        Directory.Delete(dir, recursive: true);
    }
}
