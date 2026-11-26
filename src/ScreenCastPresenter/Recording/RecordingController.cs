using System.Diagnostics;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using Timer = System.Threading.Timer;

namespace ScreenCastPresenter.Recording;

internal enum RecordingStatus { Off, Starting, Recording, Paused, Stopping }

/// <summary>How a recording ended, for the "saved" card (FR-51-style actions: Open, Show in folder).</summary>
internal sealed record RecordingResult(string Path, RecordingEnd End, TimeSpan Length, long Bytes, string Detail, bool Saved);

/// <summary>The recording helper process as the controller sees it (a fake in tests).</summary>
internal interface IRecordingProcess : IDisposable
{
    /// <summary>One reply line; null when the process has exited.</summary>
    event Action<string?>? LineReceived;

    void Send(string line);

    void Kill();
}

/// <summary>
/// SCP's side of recording (FR-52–FR-61): starts the recording helper, relays pause / resume /
/// stop, and turns its replies into state the badge and tray show. The helper does the work and
/// survives SCP: if SCP closes or crashes, the helper finishes the file on its own (FR-61).
/// Events arrive on the thread that created the controller (the UI thread).
/// </summary>
internal sealed class RecordingController : IDisposable
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<IRecordingProcess> _startProcess;
    private readonly Func<string, long?> _freeSpace;
    private readonly SynchronizationContext? _ui;
    private readonly object _sync = new();
    private IRecordingProcess? _process;
    private TaskCompletionSource? _finished;
    private Timer? _startTimer;
    private bool _gotFinished;
    private RecordingEnd _end;
    private string _detail = "";
    private int _generation;

    /// <param name="useCurrentContext">Raise events on the creating thread's context (the UI thread). Tests pass false to get them inline.</param>
    public RecordingController(Func<IRecordingProcess> startProcess, Func<string, long?>? freeSpace = null, bool useCurrentContext = true)
    {
        _startProcess = startProcess;
        _freeSpace = freeSpace ?? SavedFiles.FreeSpace;
        _ui = useCurrentContext ? SynchronizationContext.Current : null;
    }

    public RecordingStatus Status { get; private set; }

    public bool IsActive => Status != RecordingStatus.Off;

    public TimeSpan Elapsed { get; private set; }

    public long Bytes { get; private set; }

    public string? Path { get; private set; }

    /// <summary>What is being recorded, in words ("What the TV shows", "Zoom", "Display 2").</summary>
    public string Description { get; private set; } = "";

    /// <summary>The recording follows the output (records what the TV shows).</summary>
    public bool FollowsOutput { get; private set; }

    /// <summary>The recorded window, or 0 for a screen.</summary>
    public nint SourceHandle { get; private set; }

    public event EventHandler? Changed;

    public event EventHandler<string>? Warning;

    public event EventHandler<RecordingResult>? Finished;

    /// <summary>Starts recording. Returns a plain-language reason when it can't start, else null.</summary>
    public string? Start(RecordingRequest request, string description, bool followsOutput)
    {
        if (IsActive) return "A recording is already running.";
        string folder = System.IO.Path.GetDirectoryName(request.Path) ?? "";
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SavedFiles.Explain(ex);
        }
        long? free = _freeSpace(folder);
        if (free < DiskGuard.StopBelow)
            return $"There isn't enough free disk space to record ({free.Value / (1024 * 1024)} MB left). Free some space or choose another folder.";

        IRecordingProcess process;
        try
        {
            process = _startProcess();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return "The recorder couldn't start (" + ex.Message + ").";
        }

        int generation;
        lock (_sync)
        {
            generation = ++_generation;
            _process = process;
            _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gotFinished = false;
            _end = RecordingEnd.Stopped;
            _detail = "";
        }
        Status = RecordingStatus.Starting;
        Elapsed = TimeSpan.Zero;
        Bytes = 0;
        Path = request.Path;
        Description = description;
        FollowsOutput = followsOutput;
        SourceHandle = request.Source.IsScreen ? 0 : request.Source.Handle;
        process.LineReceived += line => OnUi(() => OnLine(generation, line));
        process.Send(RecordingProtocol.Start(request));
        _startTimer = new Timer(_ => OnUi(() => OnStartTimeout(generation)), null, StartTimeout, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(this, EventArgs.Empty);
        if (free < DiskGuard.WarnBelow)
            Warning?.Invoke(this, $"Only {free.Value / (1024 * 1024)} MB of disk space is left. The recording will stop by itself before the disk is full.");
        return null;
    }

    public void TogglePause()
    {
        if (Status is not (RecordingStatus.Recording or RecordingStatus.Paused)) return;
        bool pause = Status == RecordingStatus.Recording;
        _process?.Send(pause ? "pause" : "resume");
        Status = pause ? RecordingStatus.Paused : RecordingStatus.Recording;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The replay buffer (FR-106): joins what it kept into <paramref name="path"/>. <see cref="ReplaySaved"/> or <see cref="ReplayFailed"/> follows.</summary>
    public void SaveReplay(string path)
    {
        if (Status is not (RecordingStatus.Recording or RecordingStatus.Paused)) return;
        _process?.Send("save-replay " + path);
    }

    /// <summary>The replay was saved: the file and how long it is.</summary>
    public event EventHandler<(string Path, TimeSpan Length)>? ReplaySaved;

    /// <summary>The replay couldn't be saved, and why.</summary>
    public event EventHandler<string>? ReplayFailed;

    /// <summary>Asks the helper to finish the file. <see cref="Finished"/> follows.</summary>
    public void Stop()
    {
        if (Status is RecordingStatus.Off or RecordingStatus.Stopping) return;
        _process?.Send("stop");
        Status = RecordingStatus.Stopping;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Completes when the current recording has finished (immediately when none runs). Await it; never block on it.</summary>
    public Task Completion
    {
        get
        {
            lock (_sync) return IsActive && _finished is { } f ? f.Task : Task.CompletedTask;
        }
    }

    private void OnLine(int generation, string? line)
    {
        lock (_sync)
        {
            if (generation != _generation) return; // a previous recording's late reply
        }
        if (line is null)
        {
            Complete(generation);
            return;
        }
        if (RecordingReply.Parse(line) is not { } reply) return;
        switch (reply.Kind)
        {
            case "started":
                _startTimer?.Dispose();
                _startTimer = null;
                if (Status == RecordingStatus.Starting) Status = RecordingStatus.Recording;
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "progress":
                Elapsed = TimeSpan.FromSeconds(reply.A);
                Bytes = reply.B;
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "warning":
                Warning?.Invoke(this, reply.Text);
                break;
            case "finished":
                _gotFinished = true;
                _end = reply.End;
                _detail = reply.Text;
                Elapsed = TimeSpan.FromSeconds(reply.A);
                Bytes = reply.B;
                break;
            case "replay-saved":
                ReplaySaved?.Invoke(this, (reply.Text, TimeSpan.FromSeconds(reply.A)));
                break;
            case "replay-failed":
                ReplayFailed?.Invoke(this, reply.Text.Length > 0 ? reply.Text : "The last minute couldn't be saved.");
                break;
            case "failed":
                _gotFinished = true;
                _end = RecordingEnd.Error;
                _detail = reply.Text.Length > 0 ? reply.Text : "The recording couldn't start.";
                break;
        }
    }

    private void OnStartTimeout(int generation)
    {
        if (generation != _generation || Status != RecordingStatus.Starting) return;
        _end = RecordingEnd.Error;
        _detail = "The recording didn't start in time.";
        _gotFinished = true;
        _process?.Kill();
    }

    /// <summary>The helper exited: report how it went and reset.</summary>
    private void Complete(int generation)
    {
        IRecordingProcess? process;
        TaskCompletionSource? finished;
        lock (_sync)
        {
            if (generation != _generation) return;
            process = _process;
            finished = _finished;
            _process = null;
        }
        _startTimer?.Dispose();
        _startTimer = null;
        process?.Dispose();

        string path = Path ?? "";
        bool exists = File.Exists(path);
        RecordingEnd end = _gotFinished ? _end : RecordingEnd.Error;
        string detail = _gotFinished ? _detail : "The recorder stopped unexpectedly.";

        // A recording stopped within its first second is an accident: don't leave an empty file behind.
        if (exists && end == RecordingEnd.Stopped && Elapsed < TimeSpan.FromSeconds(1))
        {
            TryDelete(path);
            exists = false;
            detail = "The recording was too short to keep.";
        }
        long bytes = exists ? new FileInfo(path).Length : 0;

        Status = RecordingStatus.Off;
        var result = new RecordingResult(path, end, Elapsed, bytes, detail, exists && bytes > 0);
        Changed?.Invoke(this, EventArgs.Empty);
        Finished?.Invoke(this, result);
        finished?.TrySetResult();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        // App exit: never wait here (completion is reported on the UI thread). The helper finishes
        // the file by itself once told to stop or once its input closes (FR-61).
        _startTimer?.Dispose();
        IRecordingProcess? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
            _generation++; // ignore anything it still says
        }
        process?.Send("stop");
        process?.Dispose();
    }
}

/// <summary>The real recording helper: this exe with --record, talking UTF-8 over stdin/stdout.</summary>
internal sealed class RecordingHelperProcess : IRecordingProcess
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly object _writeLock = new();

    public RecordingHelperProcess()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Can't find ScreenCast Presenter's own exe."), RecordingProtocol.RecordSwitch)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        CaptureHost.UseUtf8(info);
        info.Environment["DOTNET_TieredCompilation"] = "1"; // start fast, like the capture helper
        info.Environment["DOTNET_TieredPGO"] = "0";
        info.Environment["DOTNET_TC_QuickJitForLoops"] = "1";
        _process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start the recorder.");
        _input = _process.StandardInput;
        StreamReader output = _process.StandardOutput;
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = output.ReadLine()) is not null) LineReceived?.Invoke(line);
            }
            catch (IOException)
            {
                // pipe broken: treated as exit
            }
            try
            {
                _process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // SCP is quitting and already let go of the helper (it finishes the file by itself)
            }
            LineReceived?.Invoke(null);
        }) { IsBackground = true, Name = "SCP recording replies" }.Start();
    }

    public event Action<string?>? LineReceived;

    public void Send(string line)
    {
        lock (_writeLock)
        {
            try
            {
                _input.WriteLine(line);
                _input.Flush();
            }
            catch (IOException)
            {
                // the helper has gone; its exit is reported separately
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Kill()
    {
        try
        {
            _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            try
            {
                _input.Dispose(); // closing stdin tells the helper to finish up
            }
            catch (IOException)
            {
            }
        }
        _process.Dispose();
    }
}
