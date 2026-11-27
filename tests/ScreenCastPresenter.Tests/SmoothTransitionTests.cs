using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;
using ScreenCastPresenter.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// From a user report (one screen, the output in a window): the window seemed to minimise, the
/// picture shook, and Settings flickered. Each cause is kept fixed here.
/// </summary>
public class BrowserPageSteadyTests
{
    private static readonly Rectangle Client = new(100, 100, 1600, 1000);
    private static readonly Rectangle Page = new(100, 220, 1600, 880); // below the tabs and the address bar

    [Fact]
    public void An_answer_says_whether_it_is_sure()
    {
        (CropRegion? crop, bool sure) = BrowserPage.Read(Client, Page);
        Assert.True(sure);
        Assert.Equal(BrowserPage.Crop(Client, Page), crop);
        Assert.NotNull(crop);

        Assert.Equal((null, true), BrowserPage.Read(Client, Client)); // full screen: the page is the window, nothing to leave out
        Assert.Equal((null, false), BrowserPage.Read(Client, null)); // no page: between two loads
        Assert.Equal((null, false), BrowserPage.Read(Client, new Rectangle(100, 100, 200, 200))); // a sliver
        Assert.Equal((null, false), BrowserPage.Read(Client, new Rectangle(5000, 5000, 100, 100))); // nowhere on the window
        Assert.Equal((null, false), BrowserPage.Read(Rectangle.Empty, Page));
    }

    [Fact]
    public void A_page_between_two_loads_keeps_its_crop_for_a_moment_instead_of_jumping_to_the_whole_window()
    {
        CropRegion page = BrowserPage.Crop(Client, Page)!;
        int unsure = 0;
        for (int i = 1; i <= BrowserPage.Patience; i++)
            Assert.Equal(page, BrowserPage.Steady(page, (null, false), ref unsure)); // tabs and address bar stay out
        Assert.Null(BrowserPage.Steady(page, (null, false), ref unsure)); // 2 s without a page: the whole window, as before
        Assert.Equal(BrowserPage.Patience + 1, unsure);
    }

    [Fact]
    public void A_sure_answer_is_taken_at_once_and_starts_the_patience_again()
    {
        CropRegion page = BrowserPage.Crop(Client, Page)!;
        CropRegion other = BrowserPage.Crop(Client, new Rectangle(100, 260, 1600, 840))!; // the bookmarks bar shown
        int unsure = 0;
        Assert.Equal(page, BrowserPage.Steady(page, (null, false), ref unsure));
        Assert.Equal(other, BrowserPage.Steady(page, (other, true), ref unsure));
        Assert.Equal(0, unsure);
        Assert.Null(BrowserPage.Steady(other, (null, true), ref unsure)); // F11: the page fills the window, shown at once
        Assert.Null(BrowserPage.Steady(null, (null, false), ref unsure)); // shown whole already: stays whole
    }
}

/// <summary>Settings' pages compared by what they show, so a save that changes nothing on screen leaves the page alone.</summary>
public class SettingsPageTextTests
{
    private static Control[] Page(bool on, PillKind theme, string description, string box = "We'll be right back") =>
    [
        new Label { Text = "General" },
        new Panel { Controls = { new Label { Text = "Talk timer" }, new Label { Text = description }, new ToggleSwitch { Checked = on, AccessibleName = "Talk timer" } } },
        new PillButton("Dark", theme) { AccessibleName = theme == PillKind.Selected ? "Theme: Dark (current)" : "Theme: Dark" },
        new TextBox { Text = box },
    ];

    private static string Text(Control[] page)
    {
        try
        {
            return SettingsForm.PageText(page);
        }
        finally
        {
            foreach (Control control in page) control.Dispose();
        }
    }

    [Fact]
    public void Pages_that_read_the_same_are_the_same_and_any_visible_change_is_seen()
    {
        string page = Text(Page(true, PillKind.Selected, "In the now-showing bar."));
        Assert.Equal(page, Text(Page(true, PillKind.Selected, "In the now-showing bar.")));
        Assert.NotEqual(page, Text(Page(false, PillKind.Selected, "In the now-showing bar."))); // a switch
        Assert.NotEqual(page, Text(Page(true, PillKind.Secondary, "In the now-showing bar."))); // a choice
        Assert.NotEqual(page, Text(Page(true, PillKind.Selected, "Now using: WGC."))); // a description
        Assert.NotEqual(page, Text(Page(true, PillKind.Selected, "In the now-showing bar.", "Back soon"))); // a text box
    }
}

/// <summary>The real floating window: reshaped at the moment of a switch, and stepping back without seeming to minimise.</summary>
[Collection(DesktopWindowsCollection.Name)]
public class FloatingWindowSmoothTests
{
    private static DisplayMonitor Screen => DisplayHelpers.GetMonitors().First();

    [Fact]
    public void A_shape_given_now_is_taken_at_once_and_a_resizing_source_waits()
    {
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: false, new Rectangle(Screen.WorkArea.Left + 20, Screen.WorkArea.Top + 20, 600, 600)));
        window.CreateControl();
        _ = window.Handle;
        window.SetContentAspect(new Size(1600, 900)); // the first: at once, before it's seen
        Assert.Equal(1600.0 / 900, (double)window.ClientSize.Width / window.ClientSize.Height, 1);

        window.SetContentAspect(new Size(1200, 900), now: true); // a switch
        Assert.Equal(1200.0 / 900, (double)window.ClientSize.Width / window.ClientSize.Height, 1);

        window.SetContentAspect(new Size(1600, 900)); // a source being resized: not yet
        Assert.Equal(1200.0 / 900, (double)window.ClientSize.Width / window.ClientSize.Height, 1);
    }

    [Fact]
    public void Stepping_back_takes_it_off_the_top_but_never_below_the_desktop()
    {
        Rectangle area = Screen.WorkArea;
        using var window = new OutputWindow(new OutputPlacement(Screen, TopMost: true, new Rectangle(area.Left + 40, area.Top + 40, 480, 300)));
        window.ShowCloaked(); // on screen in the order of windows, but never seen during the test
        var hwnd = (HWND)window.Handle;
        Assert.True(IsTopMost(hwnd));

        window.StepBack(true);
        Assert.False(IsTopMost(hwnd), "it doesn't cover the presenter's work");
        HWND desktop = PInvoke.FindWindowEx(HWND.Null, HWND.Null, "Progman", null);
        if (!desktop.IsNull)
            Assert.True(OutputWindow.IsBehind(desktop, hwnd), "sent below the desktop, it looked as if it had been minimised");

        window.StepBack(true); // again (another window became active): still there
        Assert.False(IsTopMost(hwnd));
        if (!desktop.IsNull) Assert.True(OutputWindow.IsBehind(desktop, hwnd));

        window.StepBack(false);
        Assert.True(IsTopMost(hwnd), "back on top when the presenter switches to something else");
        window.Conceal();
    }

    private static bool IsTopMost(HWND window) =>
        ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);
}
