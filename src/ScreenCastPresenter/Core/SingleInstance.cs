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

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _signal;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle? signal)
    {
        _mutex = mutex;
        _signal = signal;
    }

    /// <summary>
    /// Returns the instance owner, or null after signalling the copy that's already running.
    /// </summary>
    public static SingleInstance? TryAcquire()
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
                if (EventWaitHandle.TryOpenExisting(SignalName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
                return null;
            }
        }

        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal);
    }

    /// <summary>Calls <paramref name="onSignal"/> on the UI thread whenever another launch signals us.</summary>
    public void ListenForSecondLaunch(Action onSignal)
    {
        if (_signal is null) return;
        SynchronizationContext ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Call from the UI thread.");
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => ui.Post(_ => onSignal(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal?.Dispose();
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
