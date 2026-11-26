namespace ScreenCastPresenter.Core;

/// <summary>
/// Only one SCP runs per Windows session (FR-29): hotkeys can only belong to one process, and two
/// copies would each list the other's output as a source. Launching it again signals the running
/// copy, which opens its picker.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ScreenCastPresenter.Instance";
    private const string SignalName = @"Local\ScreenCastPresenter.OpenPicker";
    private const string QuitName = @"Local\ScreenCastPresenter.Quit";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly EventWaitHandle? _quit;
    private RegisteredWaitHandle? _wait;
    private RegisteredWaitHandle? _quitWait;

    private SingleInstance(Mutex mutex, EventWaitHandle? signal, EventWaitHandle? quit)
    {
        _mutex = mutex;
        _signal = signal;
        _quit = quit;
    }

    /// <summary>True when a copy is running in this Windows session.</summary>
    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out Mutex? running)) return false;
            running.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Returns the instance owner, or null after signalling the copy that's already running
    /// (to open its picker, unless <paramref name="openPicker"/> is false: a command-line request
    /// goes to it through the command pipe instead).
    /// </summary>
    public static SingleInstance? TryAcquire(bool openPicker = true)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another copy is running (or one just crashed and left the mutex abandoned).
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous copy died; we take over
            }

            if (!owned)
            {
                mutex.Dispose();
                if (openPicker && EventWaitHandle.TryOpenExisting(SignalName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
                return null;
            }
        }

        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        return new SingleInstance(mutex, signal, quit);
    }

    /// <summary>
    /// Uninstalling from Settings › Apps: ask the running copy (if any) to quit as if Quit was
    /// chosen (a recording is finished and saved first), and wait until it has. True when no copy
    /// is running any more.
    /// </summary>
    public static bool AskRunningCopyToQuit(TimeSpan wait)
    {
        if (!Mutex.TryOpenExisting(MutexName, out Mutex? running)) return true;
        using (running)
        {
            if (EventWaitHandle.TryOpenExisting(QuitName, out EventWaitHandle? quit))
            {
                quit.Set();
                quit.Dispose();
            }
            try
            {
                if (!running.WaitOne(wait)) return false;
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing the mutex; it's gone either way.
            }
            running.ReleaseMutex();
            return true;
        }
    }

    /// <summary>Calls <paramref name="onSignal"/> on the UI thread whenever another launch signals us.</summary>
    public void ListenForSecondLaunch(Action onSignal)
    {
        if (_signal is null) return;
        SynchronizationContext ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Call from the UI thread.");
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => ui.Post(_ => onSignal(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Calls <paramref name="onQuit"/> on the UI thread when an uninstall asks this copy to quit.</summary>
    public void ListenForQuit(Action onQuit)
    {
        if (_quit is null) return;
        SynchronizationContext ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Call from the UI thread.");
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => ui.Post(_ => onQuit(), null), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _quitWait?.Unregister(null);
        _signal?.Dispose();
        _quit?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (e.g. released already); nothing to do.
        }
        _mutex.Dispose();
    }
}
