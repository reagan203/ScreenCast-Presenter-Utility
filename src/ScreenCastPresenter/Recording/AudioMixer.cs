namespace ScreenCastPresenter.Recording;

/// <summary>
/// Mixes meeting sound and microphone (FR-53) into one 48 kHz stereo float stream.
///
/// Each source pushes whatever its device delivers; the writer pulls exactly the number of frames
/// that wall-clock time says should have been recorded. Audio devices and the wall clock drift
/// apart by a few parts per million, which adds up over an 8-hour webinar, so each source keeps a
/// small cushion:
/// - a source that runs dry (loopback delivers nothing while nothing plays, or its clock is slow)
///   gives silence until it has <see cref="RefillFrames"/> again, so the result is one short gap
///   instead of a click on every read;
/// - a source that runs ahead is trimmed back to <see cref="TrimToFrames"/>, so the audio never
///   drifts away from the video, however long the recording.
/// </summary>
internal sealed class AudioMixer
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Audio a source must have queued again after running dry (20 ms).</summary>
    public const int RefillFrames = SampleRate / 50;

    /// <summary>More than this queued (300 ms) means the source is ahead of the clock...</summary>
    public const int MaxBufferedFrames = SampleRate * 3 / 10;

    /// <summary>...and it is trimmed back to this (150 ms), dropping the oldest audio.</summary>
    public const int TrimToFrames = SampleRate * 3 / 20;

    /// <summary>Hard cap while nobody reads (e.g. before the first frame): one second.</summary>
    public const int HardLimitFrames = SampleRate;

    private readonly Queue<float>[] _sources;
    private readonly bool[] _refilling;
    private readonly float[] _gain;
    private readonly object _lock = new();

    public AudioMixer(int sourceCount, float[]? gain = null)
    {
        _sources = Enumerable.Range(0, sourceCount).Select(_ => new Queue<float>()).ToArray();
        _refilling = new bool[sourceCount];
        _gain = gain ?? Enumerable.Repeat(1f, sourceCount).ToArray();
    }

    public int SourceCount => _sources.Length;

    /// <summary>Called from a capture thread with interleaved stereo samples.</summary>
    public void Write(int source, ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            Queue<float> q = _sources[source];
            foreach (float s in samples) q.Enqueue(s);
            Drop(q, q.Count - HardLimitFrames * Channels);
        }
    }

    /// <summary>Frames queued for a source (tests and diagnostics).</summary>
    public int Buffered(int source)
    {
        lock (_lock) return _sources[source].Count / Channels;
    }

    /// <summary>Drops everything buffered (while paused).</summary>
    public void Discard()
    {
        lock (_lock)
        {
            foreach (Queue<float> q in _sources) q.Clear();
        }
    }

    /// <summary>Mixes <paramref name="frames"/> frames into <paramref name="output"/> (interleaved stereo), silence where a source has nothing.</summary>
    public void Read(int frames, Span<float> output)
    {
        int samples = frames * Channels;
        if (output.Length < samples) throw new ArgumentException("Output too small.", nameof(output));
        output[..samples].Clear();
        lock (_lock)
        {
            for (int s = 0; s < _sources.Length; s++)
            {
                Queue<float> q = _sources[s];
                if (_refilling[s])
                {
                    if (q.Count < RefillFrames * Channels) continue; // still silent
                    _refilling[s] = false;
                }
                if (q.Count > MaxBufferedFrames * Channels) Drop(q, q.Count - TrimToFrames * Channels);

                float gain = _gain[s];
                int take = Math.Min(q.Count, samples);
                for (int i = 0; i < take; i++) output[i] += q.Dequeue() * gain;
                if (take < samples) _refilling[s] = true; // ran dry: wait for a cushion before playing again
            }
        }
    }

    private static void Drop(Queue<float> q, int count)
    {
        count -= count % Channels; // keep left/right in step
        for (int i = 0; i < count; i++) q.Dequeue();
    }

    /// <summary>Float [-1, 1] to 16-bit PCM for the AAC encoder, clipping rather than wrapping.</summary>
    public static void ToPcm16(ReadOnlySpan<float> input, Span<short> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            float v = Math.Clamp(input[i], -1f, 1f);
            output[i] = (short)Math.Round(v * short.MaxValue);
        }
    }

    /// <summary>Frames that should exist after <paramref name="elapsed"/> of recording.</summary>
    public static long FramesFor(TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : (long)Math.Floor(elapsed.TotalSeconds * SampleRate);
}
