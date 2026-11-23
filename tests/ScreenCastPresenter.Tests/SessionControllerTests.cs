using System.Drawing;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Tests;

/// <summary>Session behaviour against fakes: every fade, switch and failure path without touching Windows.</summary>
public class SessionControllerTests
{
    private readonly FakePlatform _platform = new();
    private readonly SessionController _session;
    private readonly List<string> _messages = [];

    public SessionControllerTests()
    {
        _session = new SessionController(_platform);
        _session.Message += (_, m) => _messages.Add(m);
    }

    private static SourceWindow Notepad => TestData.Window(101, "Notes - Notepad");
    private static SourceWindow Deck => TestData.Window(202, "Deck - PowerPoint", "POWERPNT");

    [Fact]
    public async Task Present_creates_a_cloaked_output_then_reveals_and_fades_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, topMost: true);

        FakeOutput output = Assert.Single(_platform.Outputs);
        Assert.Equal(["ShowCloaked", "Reveal"], output.Calls.Where(c => c is "ShowCloaked" or "Reveal"));
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.Equal(1, _session.CurrentOpacity, 3);
        FakeEngine engine = Assert.Single(_platform.Engines);
        Assert.Equal(0, engine.Opacities[0], 3);     // starts invisible (NFR-12)
        Assert.Equal(1, engine.Opacities[^1], 3);
        Assert.Equal(SessionController.FirstFadeIn, _platform.Fades.Last().Duration);
    }

    [Fact]
    public async Task Switching_on_the_same_screen_reuses_the_output_and_goes_through_black()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.PresentAsync(Deck, TestData.Tv, true);

        Assert.Single(_platform.Outputs);                          // no new window, no flash
        FakeEngine first = _platform.Engines[0];
        Assert.Equal(0, first.Opacities[^1], 3);                    // old source faded to black...
        Assert.True(first.Disposed);
        Assert.Equal(0, _platform.Engines[1].Opacities[0], 3);      // ...new one starts black
        Assert.Equal(Notepad, _session.PreviousSource);
        Assert.Equal(Deck, _session.Source);
    }

    [Fact]
    public async Task Presenting_the_same_window_again_does_nothing()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        int fades = _platform.Fades.Count;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Single(_platform.Engines);
        Assert.Equal(fades, _platform.Fades.Count);
    }

    [Fact]
    public async Task Moving_to_another_screen_replaces_the_output()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.MoveOutputAsync(TestData.Third, true);
        Assert.Equal(2, _platform.Outputs.Count);
        Assert.True(_platform.Outputs[0].Disposed);
        Assert.Contains("Conceal", _platform.Outputs[0].Calls); // cloaked before closing
        Assert.Equal(TestData.Third, _session.OutputMonitor);
    }

    [Fact]
    public async Task Blank_fades_to_black_and_back_and_message_fades_in()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(withMessage: true);
        Assert.True(_session.IsBlanked);
        Assert.Equal(0, _session.CurrentOpacity, 3);
        FakeOutput output = _platform.Outputs[0];
        Assert.Equal(_session.BlankMessage, output.Message);
        Assert.Equal(1, output.MessageBrightness, 3);

        await _session.ToggleBlankAsync(withMessage: true);
        Assert.False(_session.IsBlanked);
        Assert.Null(output.Message);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Blank_with_nothing_showing_explains_instead_of_failing()
    {
        await _session.ToggleBlankAsync(false);
        Assert.Equal(SessionState.Off, _session.State);
        Assert.Contains(_messages, m => m.Contains("Nothing is being shown"));
    }

    [Fact]
    public async Task Picking_a_new_source_while_blanked_shows_it()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        await _session.PresentAsync(Deck, TestData.Tv, true);
        Assert.False(_session.IsBlanked);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Picking_the_same_source_while_blanked_unblanks()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.False(_session.IsBlanked);
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Dimming_never_goes_above_the_dimmed_level()
    {
        await _session.SetDimAsync(0.3);
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.All(_platform.Engines[0].Opacities, o => Assert.True(o <= 0.7 + 1e-9, $"{o}"));
        Assert.Equal(0.7, _session.CurrentOpacity, 3);

        await _session.SetDimAsync(5); // clamped
        Assert.Equal(1 - AppSettings.MaxDim, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Source_closing_leaves_the_idle_screen()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.Watchers[0].RaiseClosed();
        Assert.Equal(SessionState.IdleScreen, _session.State);
        Assert.Null(_session.Source);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Contains(_messages, m => m.Contains("was closed"));
        Assert.False(_platform.Outputs[0].Disposed); // idle screen stays (FR-06, FR-16)
    }

    [Fact]
    public async Task Events_from_an_old_source_are_ignored()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeWatcher old = _platform.Watchers[0];
        await _session.PresentAsync(Deck, TestData.Tv, true);
        old.RaiseClosed();
        Assert.Equal(SessionState.Presenting, _session.State);
        Assert.Equal(Deck, _session.Source);
    }

    [Fact]
    public async Task Minimise_cuts_to_black_and_restore_fades_back()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        FakeWatcher watcher = _platform.Watchers[0];
        watcher.Minimize();
        Assert.Equal(0, _session.CurrentOpacity, 3);
        watcher.Restore();
        Assert.Equal(1, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Restore_while_blanked_stays_black()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        await _session.ToggleBlankAsync(false);
        FakeWatcher watcher = _platform.Watchers[0];
        watcher.Minimize();
        watcher.Restore();
        Assert.Equal(0, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Minimised_source_is_presented_black_with_an_explanation()
    {
        _platform.NextWatcherMinimized = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Equal(0, _session.CurrentOpacity, 3);
        Assert.Contains(_messages, m => m.Contains("minimised"));
    }

    [Fact]
    public async Task Engine_failure_leaves_a_black_idle_screen_not_a_crash()
    {
        _platform.FailNextEngine = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.Equal(SessionState.IdleScreen, _session.State);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Contains(_messages, m => m.StartsWith("Couldn't show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watcher_failure_disposes_the_engine()
    {
        _platform.FailNextWatcher = true;
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        Assert.True(_platform.Engines[0].Disposed);
        Assert.Equal(SessionState.IdleScreen, _session.State);
    }

    [Fact]
    public async Task Rapid_switches_are_coalesced_to_the_last_one()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;

        Task first = _session.PresentAsync(Deck, TestData.Tv, true);         // starts fading out, then waits
        var queued = Enumerable.Range(0, 20).Select(i => _session.PresentAsync(TestData.Window(1000 + i, $"W{i}"), TestData.Tv, true)).ToList();

        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(queued.Append(first));

        // Notepad, Deck, and only the newest of the 20 queued requests.
        Assert.Equal(3, _platform.Engines.Count);
        Assert.Equal(1019, _session.Source!.Handle);
    }

    [Fact]
    public async Task Stop_cancels_queued_switches_and_closes_the_output()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task switching = _session.PresentAsync(Deck, TestData.Tv, true);
        Task queued = _session.PresentAsync(TestData.Window(7, "Queued"), TestData.Tv, true);
        Task stop = _session.StopAsync();

        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(switching, queued, stop);

        Assert.Equal(SessionState.Off, _session.State);
        Assert.DoesNotContain(_platform.Engines, e => e.Source == 7);
        Assert.All(_platform.Outputs, o => Assert.True(o.Disposed));
    }

    [Theory]
    [InlineData(501, true)]
    [InlineData(500, false)]
    public async Task Mashing_blank_runs_at_most_one_more_fade_and_keeps_parity(int presses, bool blankedAtEnd)
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        var tasks = Enumerable.Range(0, presses).Select(_ => _session.ToggleBlankAsync(false)).ToList();
        int fadesWhileHeld = _platform.Fades.Count;
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(tasks);

        Assert.Equal(blankedAtEnd, _session.IsBlanked);
        Assert.True(_platform.Fades.Count - fadesWhileHeld <= 2, $"{_platform.Fades.Count - fadesWhileHeld} extra fades: a backlog built up");
    }

    [Fact]
    public async Task Mashing_dim_applies_only_the_last_level()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task first = _session.SetDimAsync(0.1);
        var rest = Enumerable.Range(0, 50).Select(i => _session.SetDimAsync(i % 2 == 0 ? 0.4 : 0.2)).ToList();
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(rest.Append(first));
        Assert.Equal(0.2, _session.Dim, 6);
        Assert.Equal(0.8, _session.CurrentOpacity, 3);
    }

    [Fact]
    public async Task Stop_cancels_queued_blanks()
    {
        await _session.PresentAsync(Notepad, TestData.Tv, true);
        _platform.HoldFades = true;
        Task blank = _session.ToggleBlankAsync(false);
        Task queued = _session.ToggleBlankAsync(false);
        Task stop = _session.StopAsync();
        _platform.HoldFades = false;
        _platform.ReleaseHeldFades();
        await Task.WhenAll(blank, queued, stop);
        Assert.Equal(SessionState.Off, _session.State);
        Assert.False(_session.IsBlanked);
    }

    [Fact]
    public async Task Stop_when_nothing_is_showing_is_harmless()
    {
        int events = 0;
        _session.StateChanged += (_, _) => events++;
        await _session.StopAsync();
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task A_thousand_switches_leave_exactly_one_live_engine()
    {
        for (int i = 0; i < 1000; i++)
            await _session.PresentAsync(TestData.Window(5000 + i % 7, $"W{i}"), TestData.Tv, true);
        Assert.Single(_platform.Engines, e => !e.Disposed);
        Assert.Single(_platform.Watchers, w => !w.Disposed);
        Assert.Single(_platform.Outputs);
    }
}

internal sealed class FakePlatform : ISessionPlatform
{
    private readonly List<(TaskCompletionSource Done, double To, Action<double> Apply)> _held = [];

    public List<FakeOutput> Outputs { get; } = [];
    public List<FakeEngine> Engines { get; } = [];
    public List<FakeWatcher> Watchers { get; } = [];
    public List<(double From, double To, TimeSpan Duration)> Fades { get; } = [];
    public bool FailNextEngine { get; set; }
    public bool FailNextWatcher { get; set; }
    public bool NextWatcherMinimized { get; set; }
    public bool HoldFades { get; set; }

    public IOutputSurface CreateOutput(DisplayMonitor monitor, bool topMost)
    {
        var output = new FakeOutput(monitor);
        Outputs.Add(output);
        return output;
    }

    public ICaptureEngine CreateEngine(SourceWindow source)
    {
        var engine = new FakeEngine(FailNextEngine);
        FailNextEngine = false;
        Engines.Add(engine);
        return engine;
    }

    public ISourceWatcher Watch(nint hwnd)
    {
        if (FailNextWatcher)
        {
            FailNextWatcher = false;
            throw new InvalidOperationException("The window no longer exists.");
        }
        var watcher = new FakeWatcher { IsMinimized = NextWatcherMinimized };
        NextWatcherMinimized = false;
        Watchers.Add(watcher);
        return watcher;
    }

    public Task Fade(double from, double to, TimeSpan duration, Action<double> apply)
    {
        Fades.Add((from, to, duration));
        apply(from);
        if (HoldFades)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add((done, to, apply));
            return done.Task;
        }
        apply((from + to) / 2);
        apply(to);
        return Task.CompletedTask;
    }

    public void ReleaseHeldFades()
    {
        foreach ((TaskCompletionSource done, double to, Action<double> apply) in _held.ToList())
        {
            apply(to);
            done.SetResult();
        }
        _held.Clear();
    }
}

internal sealed class FakeOutput(DisplayMonitor monitor) : IOutputSurface
{
    public List<string> Calls { get; } = [];
    public string? Message { get; private set; }
    public double MessageBrightness { get; private set; }
    public bool Disposed { get; private set; }
    public DisplayMonitor Monitor { get; } = monitor;
    public nint Handle => 999;
    public Size ClientSize => Monitor.Bounds.Size;
    public bool IsAlive => !Disposed;
    public event EventHandler? Resized { add { } remove { } }
    public void ShowCloaked() => Calls.Add("ShowCloaked");
    public void Reveal() => Calls.Add("Reveal");
    public void Conceal() => Calls.Add("Conceal");

    public void SetIdleMessage(string? message, double brightness = 1)
    {
        Message = message;
        MessageBrightness = brightness;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeEngine(bool fail) : ICaptureEngine
{
    public List<double> Opacities { get; } = [];
    public bool Disposed { get; private set; }
    public nint Source { get; private set; }
    public string Name => "Fake";

    public Task StartAsync(nint source, nint output, Size outputClientSize)
    {
        Source = source;
        if (fail) throw new InvalidOperationException("DWM refused the thumbnail.");
        Opacities.Add(0);
        return Task.CompletedTask;
    }

    public void SetOpacity(double opacity) => Opacities.Add(opacity);
    public void OnSourceChanged() { }
    public void OnOutputResized(Size outputClientSize) { }
    public void Dispose() => Disposed = true;
}

internal sealed class FakeWatcher : ISourceWatcher
{
    public bool IsMinimized { get; set; }
    public bool Disposed { get; private set; }
    public event EventHandler? Closed;
    public event EventHandler? Changed;
    public event EventHandler? Minimized;
    public event EventHandler? Restored;

    public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Minimize()
    {
        IsMinimized = true;
        Minimized?.Invoke(this, EventArgs.Empty);
    }

    public void Restore()
    {
        IsMinimized = false;
        Restored?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disposed = true;
}
