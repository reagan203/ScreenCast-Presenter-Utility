using System.Diagnostics;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.Core;

internal enum SessionState
{
    /// <summary>No output window.</summary>
    Off,
    /// <summary>A source is on the output.</summary>
    Presenting,
    /// <summary>The output is up but shows the black idle screen (e.g. the source closed).</summary>
    IdleScreen,
}

/// <summary>
/// Owns the current source, output window and capture engine, and makes every visible change
/// go through black (NFR-13). All public methods run on the UI thread; a gate serialises them
/// so a click during a fade simply waits for the fade to finish.
/// </summary>
internal sealed class SessionController : IDisposable
{
    private static readonly TimeSpan FirstFadeIn = TimeSpan.FromMilliseconds(150);   // NFR-12
    private static readonly TimeSpan SwitchFadeOut = TimeSpan.FromMilliseconds(100); // FR-22: 100 + 100 = 200 ms total
    private static readonly TimeSpan SwitchFadeIn = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopFadeOut = TimeSpan.FromMilliseconds(125);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OutputWindow? _output;
    private ICaptureEngine? _engine;
    private WindowWatcher? _watcher;
    private double _opacity;

    public SessionState State { get; private set; }
    public SourceWindow? Source { get; private set; }
    public DisplayMonitor? OutputMonitor => _output?.Monitor;

    public event EventHandler? StateChanged;

    /// <summary>Plain-language status for the UI, e.g. "Notepad was closed."</summary>
    public event EventHandler<string>? Message;

    /// <summary>
    /// Shows <paramref name="source"/> on <paramref name="monitor"/>. If something is already
    /// showing there, it fades to black, swaps, and fades back in.
    /// </summary>
    /// <param name="topMost">Keep the output above other windows. False when the output covers the presenter's own screen.</param>
    public async Task PresentAsync(SourceWindow source, DisplayMonitor monitor, bool topMost)
    {
        await _gate.WaitAsync();
        try
        {
            bool reuseOutput = _output is { IsDisposed: false } o && o.Monitor == monitor;

            await FadeAsync(0, SwitchFadeOut);
            StopEngine();

            if (!reuseOutput)
            {
                CloseOutput();
                _output = new OutputWindow(monitor, topMost);
                _output.Resize += OnOutputResized;
                _output.ShowCloaked();
            }
            OutputWindow output = _output!;

            var engine = new DwmThumbnailEngine();
            try
            {
                await engine.StartAsync(source.Handle, output.Handle, output.ClientSize);
                _watcher = new WindowWatcher(source.Handle);
            }
            catch
            {
                engine.Dispose();
                throw;
            }
            _engine = engine;
            _opacity = 0;
            _watcher.Closed += OnSourceClosed;
            _watcher.Changed += OnSourceChanged;
            _watcher.Minimized += OnSourceMinimized;
            _watcher.Restored += OnSourceRestored;

            Source = source;
            output.Reveal();
            SetState(SessionState.Presenting);

            if (_watcher.IsMinimized)
                Message?.Invoke(this, $"“{source.Title}” is minimised. Restore it to show it.");
            else
                await FadeAsync(1, reuseOutput ? SwitchFadeIn : FirstFadeIn);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecoverToIdle($"Couldn't show “{source.Title}”. It may have closed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fades out and closes the output.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await FadeAsync(0, StopFadeOut);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex); // a failed fade must not prevent stopping
        }
        finally
        {
            StopEngine();
            CloseOutput();
            Source = null;
            SetState(SessionState.Off);
            _gate.Release();
        }
    }

    /// <summary>Last-resort handler for unexpected UI-thread exceptions: drop the engine, keep the app alive.</summary>
    public void RecoverFromError(Exception ex)
    {
        Debug.WriteLine(ex);
        RecoverToIdle("Something went wrong, so the TV is showing a blank screen.");
    }

    private void RecoverToIdle(string message)
    {
        StopEngine();
        Source = null;
        SetState(_output is { IsDisposed: false } ? SessionState.IdleScreen : SessionState.Off);
        Message?.Invoke(this, message);
    }

    private Task FadeAsync(double to, TimeSpan duration)
    {
        ICaptureEngine? engine = _engine;
        if (engine is null || _opacity == to) return Task.CompletedTask;
        return Fader.RunAsync(_opacity, to, duration, value =>
        {
            _opacity = value;
            engine.SetOpacity(value);
        });
    }

    private void OnOutputResized(object? sender, EventArgs e)
    {
        if (_output is { } output) _engine?.OnOutputResized(output.ClientSize);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (sender == _watcher) _engine?.OnSourceChanged();
    }

    private async void OnSourceClosed(object? sender, EventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _watcher) return; // stale event from a previous source
            // The source is gone, so DWM already draws nothing: the output is black. Keep it as the idle screen (FR-06, FR-16).
            string title = Source?.Title ?? "The window";
            StopEngine();
            Source = null;
            SetState(SessionState.IdleScreen);
            Message?.Invoke(this, $"“{title}” was closed. The TV is showing a blank screen.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async void OnSourceMinimized(object? sender, EventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _watcher || _engine is null) return;
            // Cut to black at once: DWM would otherwise show the tiny minimised caption bar.
            _opacity = 0;
            _engine.SetOpacity(0);
            _engine.OnSourceChanged();
            Message?.Invoke(this, $"“{Source?.Title}” is minimised. Restore it to show it again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async void OnSourceRestored(object? sender, EventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _watcher || _engine is null) return;
            _engine.OnSourceChanged();
            await FadeAsync(1, FirstFadeIn);
            Message?.Invoke(this, $"Showing “{Source?.Title}”.");
        }
        catch (Exception ex)
        {
            RecoverFromError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopEngine()
    {
        _watcher?.Dispose();
        _watcher = null;
        _engine?.Dispose();
        _engine = null;
        _opacity = 0;
    }

    private void CloseOutput()
    {
        if (_output is null) return;
        _output.Resize -= OnOutputResized;
        _output.Conceal(); // cloak first so closing can never show a last frame
        _output.Close();
        _output.Dispose();
        _output = null;
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopEngine();
        CloseOutput();
        _gate.Dispose();
    }
}
