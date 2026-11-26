using System.Diagnostics;
using System.Text;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Live captions (FR-84), the app side: runs the speech helper (<see cref="CaptionsHost"/>) and
/// hands each line it prints to the UI thread. Stopping closes the helper's input, which ends it;
/// if it doesn't end promptly it is ended.
/// </summary>
internal sealed class CaptionsProcess : IDisposable
{
    private readonly Process _process;
    private readonly SynchronizationContext? _ui;
    private bool _stopping;

    /// <summary>How long the helper gets to stop by itself once its input closes.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(1.5);

    private CaptionsProcess(Process process)
    {
        _process = process;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>A line from the helper: "ready", "partial …", "final …" or "error …".</summary>
    public event EventHandler<string>? Line;

    /// <summary>The helper ended without being asked to (it couldn't start, or it crashed).</summary>
    public event EventHandler? Ended;

    /// <summary>Starts the helper; null (with the reason) when Windows won't start it.</summary>
    public static (CaptionsProcess? Process, string? Problem) Start()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "ScreenCastPresenter.exe", LiveCaptions.Switch)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        try
        {
            Process? process = Process.Start(info);
            if (process is null) return (null, "The live captions helper couldn't be started.");
            var captions = new CaptionsProcess(process);
            captions.ReadLines();
            return (captions, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return (null, "The live captions helper couldn't be started.");
        }
    }

    private void ReadLines()
    {
        var reader = new Thread(() =>
        {
            try
            {
                while (_process.StandardOutput.ReadLine() is { } line) Post(() => Line?.Invoke(this, line));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Debug.WriteLine(ex);
            }
            Post(() =>
            {
                if (!_stopping) Ended?.Invoke(this, EventArgs.Empty);
            });
        }) { IsBackground = true, Name = "SCP captions reader" };
        reader.Start();
    }

    private void Post(Action action)
    {
        if (_ui is { } ui) ui.Post(_ => action(), null);
        else action();
    }

    /// <summary>Ends the helper: its input closes, and it's ended if it hasn't gone a moment later.</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(StopWait)) _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException)
        {
            Debug.WriteLine(ex); // already gone
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
