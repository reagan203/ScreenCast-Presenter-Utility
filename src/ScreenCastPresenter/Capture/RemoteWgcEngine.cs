using System.Diagnostics;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The WGC engine as SCP sees it: a proxy that runs the real <see cref="WgcEngine"/> in the
/// capture helper process (see <see cref="CaptureHost"/>) and forwards every call over a pipe.
/// When it's disposed the helper exits and all its GPU memory returns to Windows.
/// </summary>
internal sealed class RemoteWgcEngine : ICaptureEngine, IEffectsEngine
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private Process? _helper;
    private StreamWriter? _input;
    private SynchronizationContext? _ui;
    private volatile bool _disposed;
    private readonly bool _showCursor;
    private readonly bool _startFrozen;
    private bool _frozen;

    public RemoteWgcEngine(bool showCursor, bool startFrozen)
    {
        _showCursor = showCursor;
        _startFrozen = startFrozen;
        _frozen = startFrozen;
    }

    /// <summary>The exe to run as the helper; tests point it at ScreenCast Presenter's (otherwise it's this process's own exe).</summary>
    internal static string? HelperExe { get; set; }

    public string Name => "Windows Graphics Capture";
    public bool SupportsFreeze => true;
    public bool IsFrozen => _frozen;

    public event EventHandler<string>? Failed;

    public async Task StartAsync(SourceWindow source, nint output, Size outputClientSize)
    {
        if (_helper is not null) throw new InvalidOperationException("Engine already started.");
        if (!Direct3D.IsCaptureSupported) throw new NotSupportedException("Screen capture isn't available on this version of Windows.");
        _ui = SynchronizationContext.Current;

        var info = new ProcessStartInfo(HelperExe ?? Environment.ProcessPath ?? throw new InvalidOperationException("Can't find ScreenCast Presenter's own exe."), CaptureProtocol.HostSwitch)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        CaptureHost.UseUtf8(info);
        // The helper is short-lived: start it fast (tiered JIT) and skip profile-guided instrumentation.
        info.Environment["DOTNET_TieredCompilation"] = "1";
        info.Environment["DOTNET_TieredPGO"] = "0";
        info.Environment["DOTNET_TC_QuickJitForLoops"] = "1";
        _helper = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start the capture helper.");
        _input = _helper.StandardInput;
        StreamReader replies = _helper.StandardOutput;
        new Thread(() => ReadReplies(replies)) { IsBackground = true, Name = "SCP capture replies" }.Start();

        Send(CaptureProtocol.Start(output, source, _showCursor, _startFrozen, outputClientSize, StartAutoHide, StartAutoStyle, StartAutoWords));
        try
        {
            await _ready.Task.WaitAsync(StartTimeout);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ReadReplies(StreamReader output)
    {
        try
        {
            string? line;
            while ((line = output.ReadLine()) is not null)
            {
                if (line == "ready") _ready.TrySetResult();
                else if (line.StartsWith("inset-failed", StringComparison.Ordinal))
                {
                    string why = line.Length > 13 ? line[13..] : "The small picture went away.";
                    if (_ui is { } ui) ui.Post(_ => { if (!_disposed) InsetFailed?.Invoke(this, why); }, null);
                    else if (!_disposed) InsetFailed?.Invoke(this, why);
                }
                else if (line.StartsWith("autohid ", StringComparison.Ordinal))
                {
                    string words = line["autohid ".Length..];
                    if (_ui is { } ui) ui.Post(_ => { if (!_disposed) AutoHidden?.Invoke(this, words); }, null);
                    else if (!_disposed) AutoHidden?.Invoke(this, words);
                }
                else if (line.StartsWith("failed", StringComparison.Ordinal))
                {
                    string reason = line.Length > 7 ? line[7..] : "capture failed";
                    if (!_ready.TrySetException(new InvalidOperationException(reason))) RaiseFailed(reason);
                }
            }
        }
        catch (IOException)
        {
            // pipe broken: treated as the helper exiting
        }
        if (!_ready.TrySetException(new InvalidOperationException("The capture helper stopped before it started."))) RaiseFailed("The capture helper stopped.");
    }

    private void RaiseFailed(string reason)
    {
        if (_disposed) return;
        if (_ui is { } ui) ui.Post(_ => { if (!_disposed) Failed?.Invoke(this, reason); }, null);
        else Failed?.Invoke(this, reason);
    }

    private void Send(string line)
    {
        lock (_writeLock)
        {
            if (_input is null) return;
            try
            {
                _input.WriteLine(line);
                _input.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the reply reader reports the helper's exit
            }
        }
    }

    public void SetOpacity(double opacity) => Send(CaptureProtocol.Opacity(Math.Clamp(opacity, 0, 1)));
    public void OnSourceChanged() => Send("changed");
    public void OnOutputResized(Size outputClientSize) => Send(CaptureProtocol.OutputSize(outputClientSize));
    public void SetCrop(CropRegion? crop) => Send("crop " + CaptureProtocol.Crop(crop));

    public void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        Send(CaptureProtocol.Freeze(frozen));
    }

    public void SetIdleMessage(string? message, double brightness) => Send(CaptureProtocol.Message(message, brightness));

    // ───────────── marks on the TV (IEffectsEngine), forwarded to the helper ─────────────

    public void Click(PointF at, bool secondary) => Send(CaptureProtocol.Click(at, secondary));
    public void Pointer(PointF? at) => Send(CaptureProtocol.Pointer(at));
    public void Laser(PointF? at) => Send(CaptureProtocol.Laser(at));
    public void ShowCursor(bool on) => Send(CaptureProtocol.Cursor(on));
    public void InkStart(InkStroke stroke) => Send(CaptureProtocol.InkStart(stroke));

    /// <summary>A long line goes in pieces, so no single command grows without bound.</summary>
    public void InkAdd(int id, IReadOnlyList<PointF> points)
    {
        for (int i = 0; i < points.Count; i += 200) Send(CaptureProtocol.InkAdd(id, points.Skip(i).Take(200)));
    }

    public void InkRemove(int id) => Send(CaptureProtocol.InkRemove(id));
    public void InkClear() => Send(CaptureProtocol.InkClear);
    public void InkSet(int id, IReadOnlyList<PointF> points) => Send(CaptureProtocol.InkSet(id, points.Take(200)));
    public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) => Send(CaptureProtocol.Redact(regions, style));
    public void Spotlight(PointF? at) => Send(CaptureProtocol.Spotlight(at));
    public void Caption(string? name, string? second) => Send(CaptureProtocol.Caption(name, second));
    public void Logo(string? path) => Send(CaptureProtocol.Logo(path));
    public void Qr(string? text, InsetCorner corner) => Send(CaptureProtocol.Qr(text, corner));
    public void LiveCaption(string? text) => Send(CaptureProtocol.Live(text));
    public void CaptionStyle(CaptionLook look) => Send(CaptureProtocol.CaptionStyle(look));
    public void AutoHide(SensitiveKinds kinds, RedactStyle style) => Send(CaptureProtocol.AutoHide(kinds, style));
    public void AutoHideWords(IReadOnlyList<string> words) => Send(CaptureProtocol.AutoHideWords(words));
    public void Split(SplitLayout layout, IReadOnlyList<SourceWindow> others) => Send(CaptureProtocol.Split(layout, others));
    public void KeyCombo(string? text) => Send(CaptureProtocol.Keys(text));
    public void HiddenFor(SourceWindow source, IReadOnlyList<CropRegion> regions, RedactStyle style) => Send(CaptureProtocol.HiddenFor(source, regions, style));

    /// <summary>Automatic hiding covered something (FR-88), in words.</summary>
    public event EventHandler<string>? AutoHidden;

    /// <summary>Automatic hiding from the first frame (FR-88): set before starting.</summary>
    public SensitiveKinds StartAutoHide { get; init; }
    public RedactStyle StartAutoStyle { get; init; }
    public IReadOnlyList<string>? StartAutoWords { get; init; }
    public void Inset(SourceWindow? source, InsetCorner corner, int percent) => Send(CaptureProtocol.Inset(source, corner, percent));

    public event EventHandler<string>? InsetFailed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Send("stop");
        if (_helper is { } helper)
        {
            try
            {
                // The helper clears its surface to black and exits; give it a moment, then insist.
                if (!helper.WaitForExit(1500)) helper.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            helper.Dispose();
        }
        lock (_writeLock)
        {
            _input?.Dispose();
            _input = null;
        }
        _ready.TrySetCanceled();
    }
}
