using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;

namespace ScreenCastPresenter.Recording;

/// <summary>What a saved video file contains, read back with Media Foundation (used by tests and to check a finished recording).</summary>
internal sealed record Mp4Info(TimeSpan Duration, int Width, int Height, bool HasVideo, bool HasAudio, Guid VideoSubtype, Guid AudioSubtype)
{
    private const uint FirstVideo = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    private const uint FirstAudio = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
    private const uint MediaSource = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_MEDIASOURCE);

    public static unsafe Mp4Info Read(string path)
    {
        MediaFoundation.Start();
        try
        {
            MediaFiles.OpenReader(path, null, out IMFSourceReader reader).ThrowOnFailure();
            try
            {
                Guid key = PInvoke.MF_PD_DURATION;
                reader.GetPresentationAttribute(MediaSource, &key, out PROPVARIANT duration);
                var length = TimeSpan.FromTicks((long)duration.Anonymous.Anonymous.Anonymous.uhVal); // 100-ns units = ticks

                (bool hasVideo, Guid videoSubtype, ulong frameSize) = Stream(reader, FirstVideo, PInvoke.MF_MT_FRAME_SIZE);
                (bool hasAudio, Guid audioSubtype, _) = Stream(reader, FirstAudio, null);
                return new Mp4Info(length, (int)(frameSize >> 32), (int)(frameSize & 0xFFFFFFFF), hasVideo, hasAudio, videoSubtype, audioSubtype);
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }
        }
        finally
        {
            MediaFoundation.Stop();
        }
    }

    /// <summary>Decodes every video frame and counts them (slow; tests only).</summary>
    public static unsafe int CountVideoFrames(string path)
    {
        MediaFoundation.Start();
        try
        {
            MediaFiles.OpenReader(path, null, out IMFSourceReader reader).ThrowOnFailure();
            try
            {
                int frames = 0;
                while (true)
                {
                    uint flags;
                    long time;
                    IMFSample_unmanaged* sample = null;
                    reader.ReadSample(FirstVideo, 0, null, &flags, &time, &sample);
                    if (sample is not null)
                    {
                        frames++;
                        sample->Release();
                    }
                    if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return frames;
                    if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0) throw new InvalidDataException("The video can't be decoded.");
                }
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }
        }
        finally
        {
            MediaFoundation.Stop();
        }
    }

    private static unsafe (bool Present, Guid Subtype, ulong Value) Stream(IMFSourceReader reader, uint stream, Guid? key)
    {
        IMFMediaType type;
        try
        {
            reader.GetNativeMediaType(stream, 0, out type);
        }
        catch (COMException)
        {
            return (false, Guid.Empty, 0); // no such stream
        }
        try
        {
            Guid subtypeKey = PInvoke.MF_MT_SUBTYPE, subtype;
            type.GetGUID(&subtypeKey, &subtype);
            ulong value = 0;
            if (key is { } k) type.GetUINT64(&k, out value);
            return (true, subtype, value);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }
}
