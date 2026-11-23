using ScreenCastPresenter.Interop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.Tests;

public class WindowFilterTests
{
    private const uint OwnPid = 1000;

    private static WindowCandidate Normal() => new(
        Handle: 42,
        Title: "Untitled - Notepad",
        ProcessId: 2000,
        ExStyle: WINDOW_EX_STYLE.WS_EX_WINDOWEDGE,
        IsVisible: true,
        IsCloaked: false,
        HasOwner: false,
        IsShellWindow: false,
        IsMinimized: false,
        Width: 800,
        Height: 600);

    private static bool Presentable(WindowCandidate w) => WindowFilter.IsPresentable(w, OwnPid);

    [Fact]
    public void Normal_app_window_is_listed() => Assert.True(Presentable(Normal()));

    [Fact]
    public void Invisible_window_is_hidden() => Assert.False(Presentable(Normal() with { IsVisible = false }));

    [Fact]
    public void Cloaked_window_is_hidden() => Assert.False(Presentable(Normal() with { IsCloaked = true }));

    [Fact]
    public void Own_windows_including_output_are_hidden() => Assert.False(Presentable(Normal() with { ProcessId = OwnPid }));

    [Fact]
    public void Untitled_window_is_hidden() => Assert.False(Presentable(Normal() with { Title = "  " }));

    [Fact]
    public void Desktop_shell_window_is_hidden() => Assert.False(Presentable(Normal() with { IsShellWindow = true }));

    [Fact]
    public void Zero_size_window_is_hidden() => Assert.False(Presentable(Normal() with { Width = 0 }));

    [Fact]
    public void Minimised_window_is_listed_even_though_its_rect_is_tiny() =>
        Assert.True(Presentable(Normal() with { IsMinimized = true, Width = 0, Height = 0 }));

    [Fact]
    public void Tool_window_is_hidden() =>
        Assert.False(Presentable(Normal() with { ExStyle = WINDOW_EX_STYLE.WS_EX_TOOLWINDOW }));

    [Fact]
    public void No_activate_window_is_hidden() =>
        Assert.False(Presentable(Normal() with { ExStyle = WINDOW_EX_STYLE.WS_EX_NOACTIVATE }));

    [Fact]
    public void Owned_popup_is_hidden() => Assert.False(Presentable(Normal() with { HasOwner = true }));

    [Fact]
    public void AppWindow_style_overrides_tool_and_owner_rules() =>
        Assert.True(Presentable(Normal() with
        {
            ExStyle = WINDOW_EX_STYLE.WS_EX_APPWINDOW | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            HasOwner = true,
        }));

    [Fact]
    public void AppWindow_style_does_not_override_cloaking() =>
        Assert.False(Presentable(Normal() with { ExStyle = WINDOW_EX_STYLE.WS_EX_APPWINDOW, IsCloaked = true }));
}
