using System.Diagnostics;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Interop;

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
/// Owns the current source, output window and capture engine, and makes every visible change go
/// through black (NFR-13). Public methods run on the UI thread; a gate serialises them so a
/// command during a fade waits for it, and superseded source switches are dropped (FR-28).
/// </summary>
internal sealed class SessionController : IDisposable
{
    public static readonly TimeSpan FirstFadeIn = TimeSpan.FromMilliseconds(150);   // NFR-12
    public static readonly TimeSpan SwitchFadeOut = TimeSpan.FromMilliseconds(100); // FR-22: 100 + 100 = 200 ms
    public static readonly TimeSpan SwitchFadeIn = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan StopFadeOut = TimeSpan.FromMilliseconds(125);
    public static readonly TimeSpan BlankFade = TimeSpan.FromMilliseconds(125);
    public static readonly TimeSpan MessageFade = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan DimFade = TimeSpan.FromMilliseconds(100);

    private readonly ISessionPlatform _platform;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOutputSurface? _output;
    private ICaptureEngine? _engine;
    private ISourceWatcher? _watcher;
    private double _opacity;
    private double _dim;
    private int _presentVersion;
    private int _blankVersion;
    private int _dimVersion;
    private bool? _desiredBlank;
    private string? _idleMessage;

    public SessionController(ISessionPlatform platform) => _platform = platform;

    public SessionState State { get; private set; }
    public bool IsBlanked { get; private set; }
    public SourceWindow? Source { get; private set; }
    public SourceWindow? PreviousSource { get; private set; }
    public DisplayMonitor? OutputMonitor => _output is { IsAlive: true } o ? o.Monitor : null;
    public nint OutputHandle => _output is { IsAlive: true } o ? o.Handle : 0;
    public double Dim => _dim;
    public bool IsSourceMinimized => _watcher?.IsMinimized ?? false;

    /// <summary>Text for "blank with a message" (FR-27).</summary>
    public string BlankMessage { get; set; } = "We'll be right back";

    /// <summary>The brightness the source is shown at: 0 when blanked, else 1 minus dimming (NFR-15).</summary>
    public double TargetOpacity => IsBlanked ? 0 : 1 - _dim;

    /// <summary>Current engine opacity; exposed for tests.</summary>
    internal double CurrentOpacity => _opacity;

    public event EventHandler? StateChanged;

    /// <summary>Plain-language status for the presenter (shown in the badge, never on the output).</summary>
    public event EventHandler<string>? Message;

    /// <summary>The source moved, resized, minimised or was restored (the outline follows it).</summary>
    public event EventHandler? SourceGeometryChanged;

    /// <summary>
    /// Shows <paramref name="source"/> on <paramref name="monitor"/>, fading through black if
    /// something is already showing. If several calls are queued, only the newest one runs.
    /// </summary>
    /// <param name="topMost">Keep the output above other windows (false when it covers the presenter's only screen).</param>
    public async Task PresentAsync(SourceWindow source, DisplayMonitor monitor, bool topMost)
    {
        int version = Interlocked.Increment(ref _presentVersion);
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _presentVersion)) return; // superseded by a newer request (FR-28)
            await PresentCoreAsync(source, monitor, topMost);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PresentCoreAsync(SourceWindow source, DisplayMonitor monitor, bool topMost)
    {
        bool sameOutput = _output is { IsAlive: true } current && current.Monitor.IsSameScreen(monitor) && current.Monitor.Bounds == monitor.Bounds;

        // Already showing this window here: just make sure it's visible.
        if (sameOutput && _engine is not null && Source?.Handle == source.Handle)
        {
            if (IsBlanked) await UnblankCoreAsync();
            return;
        }

        try
        {
            await FadeAsync(0, SwitchFadeOut);
            SourceWindow? previous = Source;
            StopEngine();

            if (!sameOutput)
            {
                CloseOutput();
                _output = _platform.CreateOutput(monitor, topMost);
                _output.Resized += OnOutputResized;
                _output.ShowCloaked();
            }
            IOutputSurface output = _output!;
            await HideIdleMessageAsync();
            IsBlanked = false;

            ICaptureEngine engine = _platform.CreateEngine(source);
            ISourceWatcher watcher;
            try
            {
                await engine.StartAsync(source.Handle, output.Handle, output.ClientSize);
                watcher = _platform.Watch(source.Handle);
            }
            catch
            {
                engine.Dispose();
                throw;
            }

            _engine = engine;
            _watcher = watcher;
            _opacity = 0;
            watcher.Closed += OnSourceClosed;
            watcher.Changed += OnSourceChanged;
            watcher.Minimized += OnSourceMinimized;
            watcher.Restored += OnSourceRestored;

            if (previous is not null && previous.Handle != source.Handle) PreviousSource = previous;
            Source = source;
            output.Reveal();
            SetState(SessionState.Presenting);
            SourceGeometryChanged?.Invoke(this, EventArgs.Empty);

            if (watcher.IsMinimized)
                Message?.Invoke(this, $"“{source.Title}” is minimised. Restore it to show it.");
            else
                await FadeAsync(TargetOpacity, sameOutput ? SwitchFadeIn : FirstFadeIn);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecoverToIdle($"Couldn't show “{source.Title}”. It may have closed.");
        }
    }

    /// <summary>Fades out and closes the output.</summary>
    public async Task StopAsync()
    {
        // A stop cancels everything still queued: switches, blanks, dims (FR-28).
        Interlocked.Increment(ref _presentVersion);
        Interlocked.Increment(ref _blankVersion);
        Interlocked.Increment(ref _dimVersion);
        _desiredBlank = null;
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
            bool wasOn = State != SessionState.Off;
            StopEngine();
            CloseOutput();
            Source = null;
            IsBlanked = false;
            if (wasOn) SetState(SessionState.Off);
            _gate.Release();
        }
    }

    /// <summary>Blank / show again (FR-20, UC-05); with a message, the idle screen shows <see cref="BlankMessage"/> (FR-27).</summary>
    public async Task ToggleBlankAsync(bool withMessage)
    {
        // Each press flips the *intended* state immediately; only the newest queued press acts on
        // it, so mashing the key never builds a backlog of fades (FR-28).
        bool desired = !(_desiredBlank ?? IsBlanked);
        _desiredBlank = desired;
        int version = Interlocked.Increment(ref _blankVersion);
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _blankVersion)) return;
            _desiredBlank = null;

            if (State == SessionState.Off)
            {
                Message?.Invoke(this, "Nothing is being shown right now.");
                return;
            }
            if (desired == IsBlanked) return; // pressed an even number of times

            if (IsBlanked)
            {
                await UnblankCoreAsync();
                return;
            }

            IsBlanked = true;
            await FadeAsync(0, BlankFade);
            if (withMessage) await ShowIdleMessageAsync(BlankMessage);
            SetState(State);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecoverToIdle("Something went wrong, so the TV is showing a blank screen.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UnblankCoreAsync()
    {
        await HideIdleMessageAsync();
        IsBlanked = false;
        if (_engine is not null && !(_watcher?.IsMinimized ?? false)) await FadeAsync(TargetOpacity, BlankFade);
        SetState(State);
    }

    /// <summary>Dims the source on the output by 0–40% (NFR-15).</summary>
    public async Task SetDimAsync(double dim)
    {
        double target = double.IsFinite(dim) ? Math.Clamp(dim, 0, AppSettings.MaxDim) : 0;
        int version = Interlocked.Increment(ref _dimVersion);
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _dimVersion)) return; // a newer dim level is queued
            _dim = target;
            if (_engine is not null && !IsBlanked && !(_watcher?.IsMinimized ?? false))
                await FadeAsync(TargetOpacity, DimFade);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves the output (and whatever it shows) to another screen.</summary>
    public async Task MoveOutputAsync(DisplayMonitor monitor, bool topMost)
    {
        if (Source is { } source && State == SessionState.Presenting)
        {
            await PresentAsync(source, monitor, topMost);
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_output is null) return;
            string? message = _idleMessage;
            CloseOutput();
            _output = _platform.CreateOutput(monitor, topMost);
            _output.Resized += OnOutputResized;
            _output.ShowCloaked();
            if (message is not null) _output.SetIdleMessage(message);
            _idleMessage = message;
            _output.Reveal();
            SetState(State);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            RecoverToIdle("Couldn't move the output to that screen.");
        }
        finally
        {
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
        IsBlanked = false;
        SetState(_output is { IsAlive: true } ? SessionState.IdleScreen : SessionState.Off);
        Message?.Invoke(this, message);
    }

    private Task FadeAsync(double to, TimeSpan duration)
    {
        ICaptureEngine? engine = _engine;
        if (engine is null || Math.Abs(_opacity - to) < 0.001) return Task.CompletedTask;
        return _platform.Fade(_opacity, to, duration, value =>
        {
            _opacity = value;
            engine.SetOpacity(value);
        });
    }

    private async Task ShowIdleMessageAsync(string message)
    {
        if (_output is not { IsAlive: true } output) return;
        _idleMessage = message;
        await _platform.Fade(0, 1, MessageFade, b => output.SetIdleMessage(message, b));
    }

    private async Task HideIdleMessageAsync()
    {
        if (_idleMessage is not { } message || _output is not { IsAlive: true } output)
        {
            _idleMessage = null;
            return;
        }
        await _platform.Fade(1, 0, MessageFade, b => output.SetIdleMessage(message, b));
        output.SetIdleMessage(null);
        _idleMessage = null;
    }

    private void OnOutputResized(object? sender, EventArgs e)
    {
        if (_output is { } output) _engine?.OnOutputResized(output.ClientSize);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (sender != _watcher) return;
        _engine?.OnSourceChanged();
        SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
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
            IsBlanked = false;
            SetState(SessionState.IdleScreen);
            SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
            Message?.Invoke(this, $"“{title}” was closed. The TV is showing a blank screen.");
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
            SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
            Message?.Invoke(this, $"“{Source?.Title}” is minimised, so the TV is black. Restore it to show it again.");
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

    private async void OnSourceRestored(object? sender, EventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _watcher || _engine is null) return;
            _engine.OnSourceChanged();
            SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
            if (!IsBlanked) await FadeAsync(TargetOpacity, FirstFadeIn);
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
        if (_watcher is { } watcher)
        {
            watcher.Closed -= OnSourceClosed;
            watcher.Changed -= OnSourceChanged;
            watcher.Minimized -= OnSourceMinimized;
            watcher.Restored -= OnSourceRestored;
            watcher.Dispose();
        }
        _watcher = null;
        _engine?.Dispose();
        _engine = null;
        _opacity = 0;
    }

    private void CloseOutput()
    {
        if (_output is null) return;
        _output.Resized -= OnOutputResized;
        _output.Conceal(); // cloak first so closing can never show a last frame
        _output.Dispose();
        _output = null;
        _idleMessage = null;
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
