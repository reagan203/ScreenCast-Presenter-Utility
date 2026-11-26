using System.Diagnostics;
using System.Text;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Remote;

/// <summary>
/// The phone remote, the app side (FR-90): runs the remote helper, gives it the key on its input
/// (never on the command line, which other programs can read), and hands what it says to the UI
/// thread. Closing its input stops it.
/// </summary>
internal sealed class RemoteProcess : IDisposable
{
    private readonly Process _process;
    private readonly SynchronizationContext? _ui;
    private readonly object _writeLock = new();
    private bool _stopping;

    /// <summary>How long the helper gets to stop by itself once its input closes.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private RemoteProcess(Process process)
    {
        _process = process;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Listening: its port and this PC's addresses on the local network.</summary>
    public event Action<int, IReadOnlyList<string>>? Listening;

    /// <summary>A command the phone sent, already checked (signed, allowed, not replayed).</summary>
    public event Action<string>? Command;

    /// <summary>A phone talked to it.</summary>
    public event Action<string>? Seen;

    /// <summary>It couldn't start, or it stopped without being asked.</summary>
    public event Action<string>? Problem;

    public static (RemoteProcess? Process, string? Problem) Start(byte[] key, int port, bool loopbackOnly = false)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "ScreenCastPresenter.exe", RemoteHost.Switch)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        try
        {
            Process? process = Process.Start(info);
            if (process is null) return (null, "The phone remote couldn't be started.");
            var remote = new RemoteProcess(process);
            remote.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"start {RemoteAuth.Base64Url(key)} {port} {(loopbackOnly ? 1 : 0)}"));
            remote.ReadLines();
            return (remote, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return (null, "The phone remote couldn't be started.");
        }
    }

    /// <summary>What's on now, for the phone (JSON); sent after every command and whenever it changes.</summary>
    public void SendStatus(string json) => Write("status " + Capture.CaptureProtocol.OneLine(json));

    private void Write(string line)
    {
        lock (_writeLock)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Debug.WriteLine(ex); // it ended: Problem is raised by the reader
            }
        }
    }

    private void ReadLines()
    {
        var reader = new Thread(() =>
        {
            try
            {
                while (_process.StandardOutput.ReadLine() is { } line) Post(() => Handle(line));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Debug.WriteLine(ex);
            }
            Post(() =>
            {
                if (!_stopping) Problem?.Invoke("The phone remote stopped.");
            });
        }) { IsBackground = true, Name = "SCP remote reader" };
        reader.Start();
    }

    /// <summary>One line from the helper (internal for tests).</summary>
    internal void Handle(string line)
    {
        (string kind, string rest) = LiveCaptions.Split(line);
        switch (kind)
        {
            case "listening":
                string[] parts = rest.Split(' ', 2);
                if (int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
                    Listening?.Invoke(port, parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : []);
                break;
            case "cmd" when RemoteCommands.IsAllowed(rest):
                Command?.Invoke(rest); // checked again here: the app runs only what's on the list
                break;
            case "seen":
                Seen?.Invoke(rest);
                break;
            case "error":
                _stopping = true;
                Problem?.Invoke(rest.Length > 0 ? rest : "The phone remote couldn't start.");
                break;
        }
    }

    private void Post(Action action)
    {
        if (_ui is { } ui) ui.Post(_ => action(), null);
        else action();
    }

    public void Stop()
    {
        if (_stopping && _process.HasExited) return;
        _stopping = true;
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(StopWait)) _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException)
        {
            Debug.WriteLine(ex);
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
