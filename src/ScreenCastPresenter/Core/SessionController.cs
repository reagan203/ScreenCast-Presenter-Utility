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
/// command during a fade waits for it, and superseded requests are dropped (FR-28).
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
    public bool IsFrozen { get; private set; }
    public SourceWindow? Source { get; private set; }
    public SourceWindow? PreviousSource { get; private set; }
    public DisplayMonitor? OutputMonitor => _output is { IsAlive: true } o ? o.Monitor : null;

    /// <summary>The output is a floating window (FR-11), not a whole screen.</summary>
    public bool OutputIsFloating => _output is { IsAlive: true, IsFloating: true };

    /// <summary>The screen the output fills, or null for none or a floating window (which shares its screen with the presenter).</summary>
    public DisplayMonitor? FullScreenOutput => _output is { IsAlive: true, IsFloating: false } o ? o.Monitor : null;

    /// <summary>The floating output window's normal bounds, to reopen it there; null otherwise.</summary>
    public Rectangle? FloatingBounds { get; private set; }
    public nint OutputHandle => _output is { IsAlive: true } o ? o.Handle : 0;
    public double Dim => _dim;
    public bool IsSourceMinimized => _watcher?.IsMinimized ?? false;

    /// <summary>The active engine's name, for Settings › Advanced and the palette (FR-32).</summary>
    public string? EngineName => _engine?.Name;

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
    public Task PresentAsync(SourceWindow source, DisplayMonitor monitor, bool topMost) =>
        PresentAsync(source, new OutputPlacement(monitor, topMost));

    public async Task PresentAsync(SourceWindow source, OutputPlacement placement)
    {
        int version = Interlocked.Increment(ref _presentVersion);
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _presentVersion)) return; // superseded by a newer request (FR-28)
            await PresentCoreAsync(source, placement);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The output already there can be reused: the same floating window, or fullscreen on the same screen at the same size.</summary>
    private bool IsSameOutput(OutputPlacement placement) => _output is { IsAlive: true } current && (placement.IsFloating
        ? current.IsFloating
        : !current.IsFloating && current.Monitor.IsSameScreen(placement.Monitor) && current.Monitor.Bounds == placement.Monitor.Bounds);

    private async Task PresentCoreAsync(SourceWindow source, OutputPlacement placement)
    {
        bool sameOutput = IsSameOutput(placement);

        // Already showing this source here: just apply a new crop and make sure it's visible.
        if (sameOutput && _engine is not null && source.IsSameSource(Source))
        {
            if (source.Crop != Source!.Crop) await ChangeCropCoreAsync(source.Crop);
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
                OpenOutput(placement);
            }
            IOutputSurface output = _output!;
            await HideIdleMessageAsync();
            IsBlanked = false;

            (ICaptureEngine engine, ISourceWatcher watcher) = await StartEngineAsync(source, output);
            Attach(engine, watcher);

            if (previous is not null && !previous.IsSameSource(source)) PreviousSource = previous;
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
            ScpApp.Diag("present failed: " + ex);
            RecoverToIdle($"Couldn't show “{source.Title}”. It may have closed.");
        }
    }

    /// <summary>Starts the right engine, falling back to the other one if it fails (SRS 4.4).</summary>
    private async Task<(ICaptureEngine, ISourceWatcher)> StartEngineAsync(SourceWindow source, IOutputSurface output, bool startFrozen = false)
    {
        ICaptureEngine engine = _platform.CreateEngine(source, startFrozen);
        try
        {
            await engine.StartAsync(source, output.Handle, output.ClientSize);
        }
        catch (Exception first) when (_platform.CreateFallbackEngine(source, engine) is { } fallback)
        {
            Debug.WriteLine(first);
            engine.Dispose();
            engine = fallback;
            try
            {
                await engine.StartAsync(source, output.Handle, output.ClientSize);
            }
            catch
            {
                engine.Dispose();
                throw;
            }
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        try
        {
            return (engine, _platform.Watch(source));
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private void Attach(ICaptureEngine engine, ISourceWatcher watcher)
    {
        _engine = engine;
        _watcher = watcher;
        _opacity = 0;
        IsFrozen = engine is RemoteWgcEngine { IsFrozen: true } or WgcEngine { IsFrozen: true };
        engine.Failed += OnEngineFailed;
        watcher.Closed += OnSourceClosed;
        watcher.Changed += OnSourceChanged;
        watcher.Minimized += OnSourceMinimized;
        watcher.Restored += OnSourceRestored;
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
        if (_engine is not null && !HidesWhenMinimized) await FadeAsync(TargetOpacity, BlankFade);
        SetState(State);
    }

    /// <summary>
    /// Freeze / go live (FR-20). The WGC engine just stops taking frames. A DWM-shown window is
    /// handed over to a WGC engine that freezes on its first frame, drawn underneath the DWM
    /// thumbnail; removing the thumbnail then reveals the identical, frozen picture — no flicker.
    /// </summary>
    public async Task ToggleFreezeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != SessionState.Presenting || _engine is null || Source is not { } source || _output is not { IsAlive: true } output)
            {
                Message?.Invoke(this, "Nothing is being shown right now.");
                return;
            }

            if (_engine.SupportsFreeze)
            {
                IsFrozen = !IsFrozen;
                _engine.SetFrozen(IsFrozen);
                SetState(State);
                return;
            }
            if (IsSourceMinimized)
            {
                Message?.Invoke(this, "Restore the window first: there's no picture to freeze while it's minimised.");
                return;
            }

            // Hand over from DWM to a frozen WGC engine.
            ICaptureEngine frozen = _platform.CreateEngine(source, startFrozen: true);
            try
            {
                await frozen.StartAsync(source, output.Handle, output.ClientSize);
            }
            catch (Exception ex)
            {
                frozen.Dispose();
                Debug.WriteLine(ex);
                Message?.Invoke(this, "Couldn't freeze this window.");
                return;
            }
            frozen.SetIdleMessage(_idleMessage, _idleMessage is null ? 0 : 1);
            frozen.SetOpacity(_opacity);
            DwmHelpersFlush();
            ICaptureEngine old = _engine;
            old.Failed -= OnEngineFailed;
            old.Dispose(); // DWM thumbnail gone: the frozen picture underneath shows
            _engine = frozen;
            frozen.Failed += OnEngineFailed;
            IsFrozen = true;
            SetState(State);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Wait one compositor frame so the WGC picture is on screen before the thumbnail is removed.
    private static void DwmHelpersFlush()
    {
        try
        {
            DwmHelpers.Flush();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Unit tests run without DWM; nothing to wait for.
        }
    }

    /// <summary>Shows only part of the source (FR-04), fading through black.</summary>
    public async Task SetCropAsync(CropRegion? crop)
    {
        await _gate.WaitAsync();
        try
        {
            if (Source is null || _engine is null) return;
            await ChangeCropCoreAsync(crop);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ChangeCropCoreAsync(CropRegion? crop)
    {
        if (crop is { IsFull: true }) crop = null;
        if (Source is not { } source || _engine is not { } engine || source.Crop == crop) return;
        double target = TargetOpacity;
        await FadeAsync(0, SwitchFadeOut);
        engine.SetCrop(crop);
        Source = source with { Crop = crop };
        SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
        if (!HidesWhenMinimized) await FadeAsync(target, SwitchFadeIn);
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
            if (_engine is not null && !IsBlanked && !HidesWhenMinimized)
                await FadeAsync(TargetOpacity, DimFade);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves the output (and whatever it shows) to another screen.</summary>
    public Task MoveOutputAsync(DisplayMonitor monitor, bool topMost) => MoveOutputAsync(new OutputPlacement(monitor, topMost));

    /// <summary>Moves the output (and whatever it shows) to another screen, or into / out of a floating window.</summary>
    public async Task MoveOutputAsync(OutputPlacement placement)
    {
        if (Source is { } source && State == SessionState.Presenting)
        {
            await PresentAsync(source, placement);
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_output is null) return;
            string? message = _idleMessage;
            CloseOutput();
            OpenOutput(placement);
            if (message is not null) _output!.SetIdleMessage(message);
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

    /// <summary>
    /// True when a minimised source must show black: the DWM engine can't draw it. The WGC engine
    /// keeps the last picture instead (UC-06).
    /// </summary>
    private bool HidesWhenMinimized => IsSourceMinimized && _engine is not { SupportsFreeze: true };

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
        ICaptureEngine? engine = _engine;
        await _platform.Fade(0, 1, MessageFade, b =>
        {
            output.SetIdleMessage(message, b);
            engine?.SetIdleMessage(message, b);
        });
    }

    private async Task HideIdleMessageAsync()
    {
        if (_idleMessage is not { } message || _output is not { IsAlive: true } output)
        {
            _idleMessage = null;
            return;
        }
        ICaptureEngine? engine = _engine;
        await _platform.Fade(1, 0, MessageFade, b =>
        {
            output.SetIdleMessage(message, b);
            engine?.SetIdleMessage(message, b);
        });
        output.SetIdleMessage(null);
        engine?.SetIdleMessage(null, 0);
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

    /// <summary>The engine broke mid-session (GPU reset, capture refused): try the other engine, else go idle.</summary>
    private async void OnEngineFailed(object? sender, string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _engine || _engine is null || Source is not { } source || _output is not { IsAlive: true } output) return;
            Debug.WriteLine($"Engine failed: {reason}");
            ICaptureEngine failed = _engine;
            failed.Failed -= OnEngineFailed;
            failed.Dispose();
            _engine = null;

            if (_platform.CreateFallbackEngine(source, failed) is { } fallback)
            {
                try
                {
                    await fallback.StartAsync(source, output.Handle, output.ClientSize);
                    _engine = fallback;
                    fallback.Failed += OnEngineFailed;
                    _opacity = 0;
                    IsFrozen = false;
                    if (!IsBlanked && !HidesWhenMinimized) await FadeAsync(TargetOpacity, SwitchFadeIn);
                    SetState(State);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    fallback.Dispose();
                }
            }
            RecoverToIdle($"Capturing “{source.Title}” stopped working, so the TV is showing a blank screen.");
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

    private async void OnSourceClosed(object? sender, EventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            if (sender != _watcher) return; // stale event from a previous source
            // The source is gone, so the engines have nothing new to draw. Go to the idle screen (FR-06, FR-16).
            string title = Source?.Title ?? "The window";
            await FadeAsync(0, StopFadeOut); // WGC still holds the last picture: fade it out properly
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
            SourceGeometryChanged?.Invoke(this, EventArgs.Empty);
            if (_engine.SupportsFreeze)
            {
                // UC-06: keep showing the last picture.
                Message?.Invoke(this, $"“{Source?.Title}” is minimised. The TV keeps showing its last picture.");
                return;
            }
            // Cut to black at once: DWM would otherwise show the tiny minimised caption bar.
            _opacity = 0;
            _engine.SetOpacity(0);
            _engine.OnSourceChanged();
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
        if (_engine is { } engine)
        {
            engine.Failed -= OnEngineFailed;
            engine.Dispose();
        }
        _engine = null;
        _opacity = 0;
        IsFrozen = false;
    }

    private void OpenOutput(OutputPlacement placement)
    {
        _output = _platform.CreateOutput(placement);
        _output.Resized += OnOutputResized;
        _output.CloseRequested += OnOutputCloseRequested;
        _output.MinimizedChanged += OnOutputMinimizedChanged;
        _output.ShowCloaked();
    }

    /// <summary>The ✕ on the floating window: stop showing, as if Stop was pressed.</summary>
    private void OnOutputCloseRequested(object? sender, EventArgs e)
    {
        if (sender != _output) return;
        _ = StopAsync();
    }

    /// <summary>Zoom can't share a minimised window: say so, once per minimise.</summary>
    private void OnOutputMinimizedChanged(object? sender, EventArgs e)
    {
        if (sender != _output || _output is not { IsMinimized: true }) return;
        Message?.Invoke(this, "The output window is minimised, so Zoom shows nothing. Restore it, and move it aside or behind other windows instead.");
    }

    private void CloseOutput()
    {
        if (_output is null) return;
        _output.Resized -= OnOutputResized;
        _output.CloseRequested -= OnOutputCloseRequested;
        _output.MinimizedChanged -= OnOutputMinimizedChanged;
        if (_output.IsFloating && _output.IsAlive) FloatingBounds = _output.WindowBounds; // reopen it where the user left it
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
