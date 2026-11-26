using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>Problems an independent review found, each pinned down so it stays fixed.</summary>
public class ReviewFixTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;

    public ReviewFixTests() => _session = new SessionController(_platform);

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");
    private static SourceWindow Deck => TestData.Window(202, "Deck - PowerPoint", "POWERPNT");

    [Fact]
    public async Task A_slideshow_switch_never_undoes_a_blank_that_came_meanwhile()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: false);
        await _session.PresentAsync(Deck, new OutputPlacement(TestData.Tv, true), onlyIfLive: true);
        Assert.Equal(101, _session.Source!.Handle); // skipped
        Assert.True(_session.IsBlanked);
    }

    [Fact]
    public async Task Nor_a_blank_still_queued()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task dim = _session.SetDimAsync(0.1);                       // something holding the gate
        Task blank = _session.ToggleBlankAsync(withMessage: false);  // queued behind it
        Task slide = _session.PresentAsync(Deck, new OutputPlacement(TestData.Tv, true), onlyIfLive: true);
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(dim, blank, slide);
        Assert.True(_session.IsBlanked);
        Assert.Equal(101, _session.Source!.Handle);
    }

    [Fact]
    public async Task Nor_a_freeze_or_a_hold()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _session.CutAway(HoldReason.PrivateApp);
        await _session.PresentAsync(Deck, new OutputPlacement(TestData.Tv, true), onlyIfLive: true);
        Assert.Equal(101, _session.Source!.Handle);
        // The presenter choosing something is never "only if live".
        await _session.PresentAsync(Deck, TestData.Tv, true);
        Assert.Equal(202, _session.Source!.Handle);
    }

    [Fact]
    public async Task A_never_show_app_back_before_the_hold_lifts_keeps_it_black()
    {
        bool privateAppOpen = true;
        _session.HoldCheck = s => privateAppOpen ? HoldReason.PrivateApp : null;
        await _session.PresentAsync(SourceWindow.ForScreen(TestData.Laptop), TestData.Tv, true);
        Assert.True(_session.IsHeldFor(HoldReason.PrivateApp));

        // It went away and the hold is to lift, but it's back by the time the gate is free.
        await _session.ComeBackAsync(HoldReason.PrivateApp);
        Assert.True(_session.IsHeldFor(HoldReason.PrivateApp));
        Assert.Equal(0, _platform.Engines[^1].Opacities[^1]);

        privateAppOpen = false;
        await _session.ComeBackAsync(HoldReason.PrivateApp);
        Assert.False(_session.IsHeld);
        Assert.Equal(1, _platform.Engines[^1].Opacities[^1], 3);
    }

    [Fact]
    public async Task A_restart_never_drops_a_switch_already_waiting()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task dim = _session.SetDimAsync(0.1);
        Task present = _session.PresentAsync(Deck, TestData.Tv, true); // chosen first, waiting
        Task restart = _session.RestartEngineAsync();                  // then a mark switched on
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(dim, present, restart);
        Assert.Equal(202, _session.Source!.Handle);
    }

    [Fact]
    public async Task Finished_rings_and_trail_stop_the_helper_drawing_even_while_black()
    {
        var layer = new EffectsLayer(() => { });
        Assert.False(layer.IsAnimating);
        layer.Click(new PointF(0.5f, 0.5f), secondary: false);
        layer.Laser(new PointF(0.4f, 0.4f));
        Assert.True(layer.IsAnimating);
        await Task.Delay(EffectStyle.RingLife + TimeSpan.FromMilliseconds(80)); // nothing rendered meanwhile (a blank TV)
        Assert.False(layer.IsAnimating);
    }

    [Fact]
    public void The_quiet_start_for_a_command_line_isnt_a_request_itself()
    {
        Assert.False(CommandLine.IsRequest([CommandLine.QuietStartSwitch]));
        Assert.Contains("returns as soon as it's done", CommandLine.Usage);
    }
}
