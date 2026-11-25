using System.Drawing;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// A TV that copies the presenter's screen (Windows' "Duplicate") shows every window, so it's
/// recognised, and after it's made a separate screen the presentation goes to it.
/// </summary>
public class CopyingTvTests
{
    private static DisplayMonitor Screen(string device, bool primary, string connection = "Internal", string name = "") =>
        new(device, new Rectangle(primary ? 0 : 1920, 0, 1920, 1080), new Rectangle(primary ? 0 : 1920, 0, 1920, 1040), primary, 1.0, name, connection);

    [Fact]
    public void Two_screens_fed_by_one_picture_are_a_copy()
    {
        Assert.True(DisplayHelpers.IsCloned([(1u, 0, 0u), (1u, 0, 0u)])); // laptop and TV, same source
        Assert.False(DisplayHelpers.IsCloned([(1u, 0, 0u), (1u, 0, 1u)])); // extended: two sources
        Assert.False(DisplayHelpers.IsCloned([(1u, 0, 0u), (2u, 0, 0u)])); // two graphics adapters
        Assert.False(DisplayHelpers.IsCloned([(1u, 0, 0u)]));
        Assert.False(DisplayHelpers.IsCloned([]));
    }

    [Fact]
    public void After_extending_the_new_tv_is_chosen()
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\.\DISPLAY1" };
        DisplayMonitor laptop = Screen(@"\\.\DISPLAY1", primary: true);
        DisplayMonitor tv = Screen(@"\\.\DISPLAY2", primary: false, "HDMI");
        Assert.Same(tv, OutputChooser.SeparatedTv([laptop, tv], before));
    }

    [Fact]
    public void Renumbered_screens_still_find_the_tv_and_never_the_zoom_screen()
    {
        // Windows may renumber the screens when it switches; the main screen is never chosen while there's another.
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\.\DISPLAY9" };
        DisplayMonitor laptop = Screen(@"\\.\DISPLAY1", primary: true);
        DisplayMonitor tv = Screen(@"\\.\DISPLAY2", primary: false, "HDMI");
        DisplayMonitor zoom = Screen(@"\\.\DISPLAY3", primary: false, "Virtual", "Virtual Display");
        Assert.Same(tv, OutputChooser.SeparatedTv([laptop, zoom, tv], before));
        Assert.Null(OutputChooser.SeparatedTv([laptop], before)); // still only one screen
    }
}
