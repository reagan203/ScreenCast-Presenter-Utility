using System.Runtime.InteropServices;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace ScreenCastPresenter.Recording;

/// <summary>
/// Joins MP4 parts made by the same recording one after another into one plain MP4, without
/// encoding anything again (FR-106: the parts of the replay buffer become "the last minute"): each
/// part's compressed video and sound go straight into the new file, their times moved on by the
/// parts before. The parts must have the same formats (they do: one recorder makes them all).
/// </summary>
internal static unsafe class Mp4Joiner
{
    private const uint AnyStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ANY_STREAM);
    private const uint EndOfStream = (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM;
    private const uint ReadError = (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR;

    /// <summary>Joins <paramref name="parts"/> (in order) into <paramref name="output"/>; its length.</summary>
    /// <param name="lengths">How long each part really lasted (a part ending on a still picture lasts past its last frame); null to go by the frames.</param>
    public static TimeSpan Join(IReadOnlyList<string> parts, string output, IReadOnlyList<TimeSpan>? lengths = null)
    {
        if (parts.Count == 0) throw new ArgumentException("Nothing to join.", nameof(parts));
        MediaFoundation.Start();
        IMFSinkWriter? writer = null;
        nint raw = 0;
        try
        {
            PInvoke.MFCreateAttributes(out IMFAttributes attributes, 1).ThrowOnFailure();
            try
            {
                Guid key = PInvoke.MF_TRANSCODE_CONTAINERTYPE, value = PInvoke.MFTranscodeContainerType_MPEG4;
                attributes.SetGUID(&key, &value);
                PInvoke.MFCreateSinkWriterFromURL(output, null, attributes, out writer).ThrowOnFailure();
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            var map = new Dictionary<uint, uint>(); // the parts' streams → the new file's
            long offset = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                MediaFiles.OpenReader(part, null, out IMFSourceReader reader).ThrowOnFailure();
                try
                {
                    if (raw == 0)
                    {
                        // The first part says what's in the file: the same compressed formats, passed straight through.
                        for (uint stream = 0; stream < 8; stream++)
                        {
                            IMFMediaType type;
                            try
                            {
                                reader.GetNativeMediaType(stream, 0, out type);
                            }
                            catch (COMException)
                            {
                                break; // no more streams
                            }
                            try
                            {
                                writer.AddStream(type, out uint index);
                                writer.SetInputMediaType(index, type, null);
                                map[stream] = index;
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(type);
                            }
                        }
                        writer.BeginWriting();
                        raw = RawCom.SinkWriterPointer(writer);
                    }
                    offset = CopyPart(reader, raw, map, offset, lengths is { } l && i < l.Count ? l[i].Ticks : null);
                }
                finally
                {
                    Marshal.ReleaseComObject(reader);
                }
            }
            writer.Finalize();
            return TimeSpan.FromTicks(offset);
        }
        finally
        {
            if (raw != 0) Marshal.Release(raw);
            if (writer is not null) Marshal.ReleaseComObject(writer);
            MediaFoundation.Stop();
        }
    }

    /// <summary>Every sample of one part, its time moved on by <paramref name="offset"/>; where the part ended.</summary>
    private static long CopyPart(IMFSourceReader reader, nint writer, Dictionary<uint, uint> map, long offset, long? length)
    {
        // Each sample waits for the next one in its stream, which says how long it lasts: a still
        // picture is written once a second, and the new file's timeline comes from durations.
        var pending = new Dictionary<uint, (nint Sample, long Time)>();
        long end = offset;
        var ended = new HashSet<uint>();
        try
        {
            while (ended.Count < map.Count)
            {
                uint actual, flags;
                long time;
                IMFSample_unmanaged* sample = null;
                reader.ReadSample(AnyStream, 0, &actual, &flags, &time, &sample);
                if (sample is not null)
                {
                    if (map.TryGetValue(actual, out uint to))
                    {
                        long at = time + offset;
                        sample->SetSampleTime(at);
                        sample->GetSampleDuration(out long duration);
                        end = Math.Max(end, at + Math.Max(0, duration));
                        if (pending.Remove(to, out (nint Sample, long Time) before)) Write(writer, to, before.Sample, at - before.Time);
                        pending[to] = ((nint)sample, at);
                    }
                    else sample->Release();
                }
                if ((flags & ReadError) != 0) throw new InvalidDataException("A part of the replay couldn't be read.");
                if ((flags & EndOfStream) != 0) ended.Add(actual);
            }
            // The part's real length (when known) ends its last samples: the time it held still counts.
            if (length is { } known) end = Math.Max(end, offset + known);
            foreach ((uint to, (nint last, long at)) in pending) Write(writer, to, last, end - at);
            pending.Clear();
            return end;
        }
        finally
        {
            foreach ((nint left, _) in pending.Values) ((IMFSample_unmanaged*)left)->Release();
        }
    }

    /// <summary>Writes a sample lasting <paramref name="duration"/> (at least a moment), then lets go of it.</summary>
    private static void Write(nint writer, uint stream, nint sample, long duration)
    {
        try
        {
            if (duration > 0) ((IMFSample_unmanaged*)sample)->SetSampleDuration(duration);
            RawCom.WriteSample(writer, stream, sample);
        }
        finally
        {
            ((IMFSample_unmanaged*)sample)->Release();
        }
    }
}
