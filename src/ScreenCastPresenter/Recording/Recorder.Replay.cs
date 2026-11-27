using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScreenCastPresenter.Recording;

/// <summary>
/// The replay buffer's rules (FR-106), pure so they're tested: parts of <see cref="PartLength"/>,
/// how many are kept, and which make up "the last minute".
/// </summary>
internal static class ReplayBuffer
{
    /// <summary>
    /// Each part's length: short enough that little more than asked for is kept, long enough that
    /// starting a new one is rare. (Tests shorten it.)
    /// </summary>
    public static TimeSpan PartLength { get; internal set; } = TimeSpan.FromSeconds(15);

    /// <summary>The next part's encoder is made this long before it's needed (it can take a second or two).</summary>
    public static TimeSpan PrepareAhead => TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(4).Ticks, PartLength.Ticks / 2));

    public static IReadOnlyList<int> Choices { get; } = [30, 60, 120];

    public const int DefaultSeconds = 60, MaxSeconds = 300;

    public static int Normalize(int seconds) => Choices.MinBy(c => Math.Abs(c - seconds));

    public static string PartPath(string folder, int n) => Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"part-{n:D6}.mp4"));

    /// <summary>Finished parts kept for <paramref name="seconds"/>: enough to cover it while the current part is still being written.</summary>
    public static int Keep(int seconds) => (int)Math.Ceiling(TimeSpan.FromSeconds(seconds) / PartLength) + 1;

    /// <summary>The newest parts (oldest first) that together cover <paramref name="seconds"/>, or all there are.</summary>
    public static List<(string Path, TimeSpan Length)> PartsFor(IReadOnlyList<(string Path, TimeSpan Length)> parts, int seconds)
    {
        var use = new List<(string Path, TimeSpan Length)>();
        TimeSpan covered = TimeSpan.Zero;
        for (int i = parts.Count - 1; i >= 0 && covered < TimeSpan.FromSeconds(seconds); i--)
        {
            use.Insert(0, parts[i]);
            covered += parts[i].Length;
        }
        return use;
    }
}

/// <summary>The replay buffer in the recorder (FR-106): short parts, the oldest deleted, joined on request.</summary>
internal sealed partial class Recorder
{
    private int _part;
    private TimeSpan _partStart;
    private long _partAudioStart;
    private Task<Mp4Writer>? _nextWriter;
    private readonly List<(string Path, TimeSpan Length)> _parts = [];
    private readonly HashSet<string> _partsInUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _partsLock = new();
    private volatile string? _saveTo;
    private Task? _saving;

    /// <summary>Joins the last part of what was recorded into <paramref name="path"/> (from the command thread).</summary>
    public void SaveReplay(string path)
    {
        if (!_request.IsReplay || path.Length == 0) return;
        _saveTo = path;
        _wake.Set();
    }

    /// <summary>On the recording thread: a new part when this one is long enough (or a save wants it finished now).</summary>
    private void RotateIfDue(TimeSpan elapsed, long audioFrames)
    {
        if (_writer is null) return;
        TimeSpan inPart = elapsed - _partStart;
        if (_nextWriter is null && inPart >= ReplayBuffer.PartLength - ReplayBuffer.PrepareAhead) _nextWriter = PrepareWriter(_part + 1);
        bool save = _saveTo is not null && _saving is not { IsCompleted: false };
        if (inPart >= ReplayBuffer.PartLength || save && inPart >= TimeSpan.FromSeconds(1)) Rotate(elapsed, audioFrames);
        if (save) StartSaving();
    }

    private Task<Mp4Writer> PrepareWriter(int part)
    {
        int width = _writer!.Width, height = _writer.Height;
        bool audio = _writer.HasAudio;
        return Task.Run(() => new Mp4Writer(ReplayBuffer.PartPath(_request.Path, part), width, height, _device!, withAudio: audio));
    }

    private void Rotate(TimeSpan elapsed, long audioFrames)
    {
        Mp4Writer next = (_nextWriter ?? PrepareWriter(_part + 1)).GetAwaiter().GetResult(); // made ahead: ready by now
        _nextWriter = null;
        Mp4Writer old = _writer!;
        string oldPath = ReplayBuffer.PartPath(_request.Path, _part);
        TimeSpan length = elapsed - _partStart;
        _writer = next;
        _part++;
        _partStart = elapsed;
        _partAudioStart = audioFrames;
        bool finished = false;
        try
        {
            old.Finish(); // its frames in flight are written before the ring is drawn into again
            finished = true;
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex); // e.g. a part with nothing in it: it's no use, and a save must never trip over it
        }
        finally
        {
            old.Dispose();
        }
        lock (_partsLock)
        {
            if (finished) _parts.Add((oldPath, length));
            else TryDelete(oldPath);
            while (_parts.Count > ReplayBuffer.Keep(_request.ReplaySeconds) && !_partsInUse.Contains(_parts[0].Path))
            {
                TryDelete(_parts[0].Path);
                _parts.RemoveAt(0);
            }
        }
    }

    private void StartSaving()
    {
        string target = _saveTo!;
        _saveTo = null;
        List<(string Path, TimeSpan Length)> use;
        lock (_partsLock)
        {
            use = ReplayBuffer.PartsFor(_parts, _request.ReplaySeconds);
            _partsInUse.UnionWith(use.Select(u => u.Path));
        }
        if (use.Count == 0)
        {
            _send(RecordingProtocol.ReplayFailed("Nothing is kept yet. Try again in a few seconds."));
            return;
        }
        _saving = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A part Windows can't read back is left out, so the rest is still saved rather than nothing.
                List<(string Path, TimeSpan Length)> readable = [.. use.Where(u => Readable(u.Path))];
                if (readable.Count == 0) throw new InvalidDataException("No part of the replay can be read.");
                TimeSpan length = Mp4Joiner.Join([.. readable.Select(u => u.Path)], target, [.. readable.Select(u => u.Length)]);
                _send(RecordingProtocol.ReplaySaved(length, target));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Debug.WriteLine(ex);
                TryDelete(target);
                _send(RecordingProtocol.ReplayFailed(ex is IOException or UnauthorizedAccessException ? Core.SavedFiles.Explain(ex) : "The last minute couldn't be put together."));
            }
            finally
            {
                lock (_partsLock) _partsInUse.ExceptWith(use.Select(u => u.Path));
            }
        });
    }

    /// <summary>Whether Windows can open a finished part to read it back.</summary>
    private static bool Readable(string part)
    {
        try
        {
            return Mp4Info.Read(part).HasVideo;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"replay part {part} can't be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stopping: a save in progress finishes; an encoder made ahead is let go.</summary>
    private void EndReplay()
    {
        if (!_request.IsReplay) return;
        try
        {
            _saving?.Wait(TimeSpan.FromSeconds(60));
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine(ex);
        }
        if (_nextWriter is { } prepared)
        {
            try
            {
                Mp4Writer unused = prepared.GetAwaiter().GetResult();
                unused.Finish();
                unused.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                Debug.WriteLine(ex);
            }
            _nextWriter = null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex); // deleted with the folder when the buffer stops
        }
    }
}
