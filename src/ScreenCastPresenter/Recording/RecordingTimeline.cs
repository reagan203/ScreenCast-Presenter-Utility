namespace ScreenCastPresenter.Recording;

/// <summary>
/// Recording time: wall-clock time since the start minus the time spent paused (FR-54). Paused
/// stretches simply don't exist in the file. Times come from the caller so tests control them.
/// </summary>
internal sealed class RecordingClock
{
    private TimeSpan _start;
    private TimeSpan _pausedTotal;
    private TimeSpan? _pausedAt;

    public RecordingClock(TimeSpan now) => _start = now;

    public bool IsPaused => _pausedAt is not null;

    public void Pause(TimeSpan now) => _pausedAt ??= now;

    public void Resume(TimeSpan now)
    {
        if (_pausedAt is not { } at) return;
        _pausedTotal += now - at;
        _pausedAt = null;
    }

    public TimeSpan Elapsed(TimeSpan now)
    {
        TimeSpan end = _pausedAt ?? now;
        TimeSpan elapsed = end - _start - _pausedTotal;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}

/// <summary>
/// Decides when a video frame goes into the file: new pictures at most 30 times a second, and
/// the last picture again at least once a second while nothing changes (a still slide, or a
/// minimised window), so players can seek and a crash loses at most a moment.
/// </summary>
internal sealed class FramePacer
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromTicks(Mp4Writer.FrameDuration * 9 / 10); // a little slack for timer jitter
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(1);

    private TimeSpan? _last;
    private bool _pending;

    /// <summary>A new picture arrived from the capture.</summary>
    public void FrameArrived() => _pending = true;

    /// <summary>True when a frame should be written at recording time <paramref name="now"/>; records that it was.</summary>
    public bool ShouldWrite(TimeSpan now, bool havePicture)
    {
        if (!havePicture) return false;
        bool due = _last is not { } last
            || (_pending && now - last >= MinInterval)
            || now - last >= MaxInterval;
        if (!due) return false;
        _last = now;
        _pending = false;
        return true;
    }

    /// <summary>Earliest time worth waking up for.</summary>
    public TimeSpan NextDue(TimeSpan now) =>
        _last is not { } last ? now : _pending ? last + MinInterval : last + MaxInterval;
}

internal enum DiskState { Fine, Low, Critical }

/// <summary>
/// FR-60 long-recording safety: warn once when the disk gets low (2 GB), stop cleanly before it is
/// full (300 MB) so the file is always finalised and playable.
/// </summary>
internal sealed class DiskGuard
{
    public const long WarnBelow = 2L * 1024 * 1024 * 1024;
    public const long StopBelow = 300L * 1024 * 1024;

    private bool _warned;

    /// <summary>What to do now; <see cref="DiskState.Low"/> is returned only once.</summary>
    public DiskState Check(long? freeBytes)
    {
        if (freeBytes is not { } free) return DiskState.Fine; // unknown (network drive): carry on
        if (free < StopBelow) return DiskState.Critical;
        if (free < WarnBelow && !_warned)
        {
            _warned = true;
            return DiskState.Low;
        }
        return DiskState.Fine;
    }
}
