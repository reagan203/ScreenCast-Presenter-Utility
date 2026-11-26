using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// A changed way of showing (the pointer, marks on the TV, the Advanced choice) applies at once:
/// the same thing is shown again with a newly chosen engine. Before, presenting the same window
/// again kept the old engine, so the change only applied to the next thing shown.
/// </summary>
public class RestartEngineTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;

    public RestartEngineTests() => _session = new SessionController(_platform);

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");

    [Fact]
    public async Task Presenting_the_same_window_again_keeps_its_engine_but_a_restart_doesnt()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Single(_platform.Engines); // why the old "show it again" never changed anything

        int changes = 0;
        _session.EngineChanged += (_, _) => changes++;
        await _session.RestartEngineAsync();
        Assert.Equal(2, _platform.Engines.Count);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Equal(1, changes);
        Assert.Single(_platform.Outputs); // the same output: nothing reopened on the TV
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task Blank_holds_zoom_and_the_message_carry_over()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        var view = new CropRegion(0.25, 0.25, 0.5, 0.5);
        await _session.SetViewAsync(view);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.BlankWithMessageAsync("Back soon");
        await _session.RestartEngineAsync();
        Assert.True(_session.IsBlanked);
        Assert.True(_session.IsHeldFor(HoldReason.PrivateApp));
        Assert.Equal(view, _session.View);
        Assert.Equal(view, _platform.Engines[^1].Crop);
        Assert.Equal("Back soon", _platform.Engines[^1].IdleMessage);
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]); // still black
    }

    [Fact]
    public async Task Not_while_frozen_or_with_nothing_shown()
    {
        await _session.RestartEngineAsync();
        Assert.Empty(_platform.Engines);

        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleFreezeAsync(); // the frozen picture would be lost
        int engines = _platform.Engines.Count;
        await _session.RestartEngineAsync();
        Assert.Equal(engines, _platform.Engines.Count);
    }

    [Fact]
    public async Task A_switch_queued_meanwhile_wins()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task restart = _session.RestartEngineAsync();
        Task present = _session.PresentAsync(TestData.Window(202, "Deck"), TestData.Tv, true);
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(restart, present);
        Assert.Equal(202, _session.Source!.Handle);
    }
}
