using System.Runtime.InteropServices;
using ScreenCastPresenter.Interop;
using Vortice.Direct3D11;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace ScreenCastPresenter.Recording;

/// <summary>
/// Writes H.264 video and AAC audio into a fragmented MP4 (FR-52) with Media Foundation.
///
/// Video frames arrive as D3D textures and stay on the GPU: the sink writer hands them straight
/// to the hardware encoder through a DXGI device manager, so recording never copies frames into
/// CPU memory (hard rule 1). Fragmented MP4 writes a playable fragment every few seconds, so if
/// the computer crashes or loses power mid-webinar, everything up to the last fragment survives.
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    public const int FrameRate = 30;
    public const long Second = 10_000_000; // 100-ns units, Media Foundation's clock
    public const long FrameDuration = Second / FrameRate;

    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly object _device;
    private readonly uint _video;
    private readonly uint _audio = uint.MaxValue;
    private bool _finished;

    /// <param name="device">Created with video support and multithread protection (see <see cref="CreateDevice"/>).</param>
    public Mp4Writer(string path, int width, int height, ID3D11Device device, bool withAudio)
        : this(path, width, height, device, withAudio, fragmented: true, hardware: true)
    {
    }

    /// <summary>Test hook: plain MP4 and/or the software encoder, to compare behaviour.</summary>
    internal Mp4Writer(string path, int width, int height, ID3D11Device device, bool withAudio, bool fragmented, bool hardware)
    {
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"Video size must be even: {width}x{height}.");
        Width = width;
        Height = height;
        MediaFoundation.Start();

        PInvoke.MFCreateDXGIDeviceManager(out uint token, out _manager).ThrowOnFailure();
        _device = Marshal.GetObjectForIUnknown(device.NativePointer);
        _manager.ResetDevice(_device, token);

        PInvoke.MFCreateAttributes(out IMFAttributes attributes, 5).ThrowOnFailure();
        Set(attributes, PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware ? 1u : 0u);
        if (hardware) SetUnknown(attributes, PInvoke.MF_SINK_WRITER_D3D_MANAGER, _manager);
        SetGuid(attributes, PInvoke.MF_TRANSCODE_CONTAINERTYPE, fragmented ? PInvoke.MFTranscodeContainerType_FMPEG4 : PInvoke.MFTranscodeContainerType_MPEG4);
        // No throttling: otherwise the writer blocks whichever stream runs ahead (audio, while a
        // still picture sends one video frame a second) and stalls the recording thread. Video in
        // flight is bounded by the recorder instead (VideoInFlight), and audio is small.
        Set(attributes, PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
        try
        {
            PInvoke.MFCreateSinkWriterFromURL(path, null, attributes, out _writer).ThrowOnFailure();
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        IMFMediaType output = VideoType(PInvoke.MFVideoFormat_H264);
        Set(output, PInvoke.MF_MT_AVG_BITRATE, (uint)VideoBitrate(width, height));
        Set(output, PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_Main);
        _writer.AddStream(output, out _video);
        Marshal.ReleaseComObject(output);
        IMFMediaType input = VideoType(PInvoke.MFVideoFormat_ARGB32);
        _writer.SetInputMediaType(_video, input, null);
        Marshal.ReleaseComObject(input);

        if (withAudio)
        {
            IMFMediaType aac = AudioType(PInvoke.MFAudioFormat_AAC);
            Set(aac, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16_000); // 128 kbps
            Set(aac, PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // raw AAC, as MP4 expects
            _writer.AddStream(aac, out _audio);
            Marshal.ReleaseComObject(aac);
            IMFMediaType pcm = AudioType(PInvoke.MFAudioFormat_PCM);
            Set(pcm, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, AudioMixer.Channels * 2);
            Set(pcm, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioMixer.SampleRate * AudioMixer.Channels * 2);
            _writer.SetInputMediaType(_audio, pcm, null);
            Marshal.ReleaseComObject(pcm);
        }

        _writer.BeginWriting();
        _writerPointer = RawCom.SinkWriterPointer(_writer);
    }

    private readonly nint _writerPointer;

    public int Width { get; }

    public int Height { get; }

    public bool HasAudio => _audio != uint.MaxValue;

    /// <summary>A D3D device the encoder can share: BGRA for Direct2D, video support for the encoder, and thread-safe.</summary>
    public static ID3D11Device CreateDevice()
    {
        Vortice.Direct3D.FeatureLevel[] levels = [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0];
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        ID3D11Device? device;
        if (D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, flags, levels, out device).Failure)
        {
            // Some drivers (and remote sessions) have no video support: Media Foundation then uses
            // its software encoder, which reads the texture through the CPU itself.
            D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device).CheckError();
        }
        using (ID3D11Multithread multithread = device!.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true); // the encoder uses the device from its own threads
        }
        return device;
    }

    /// <summary>Roughly 0.1 bits per pixel per frame: crisp text for screen content without huge files (~6 Mbps at 1080p).</summary>
    public static int VideoBitrate(int width, int height) => Math.Clamp((int)(width * (long)height * FrameRate / 10), 1_000_000, 24_000_000);

    /// <summary>The largest even size that fits <paramref name="max"/> with the source's shape (encoders need even sizes).</summary>
    public static System.Drawing.Size FitSize(System.Drawing.Size source, System.Drawing.Size max)
    {
        if (source.Width <= 0 || source.Height <= 0) return new System.Drawing.Size(2, 2);
        double scale = Math.Min(1, Math.Min((double)max.Width / source.Width, (double)max.Height / source.Height));
        int w = Math.Max(2, (int)(source.Width * scale) & ~1);
        int h = Math.Max(2, (int)(source.Height * scale) & ~1);
        return new System.Drawing.Size(w, h);
    }

    /// <summary>
    /// Queues one video frame. The texture (and the sample reused for it) must not be written
    /// again while the encoder still has it: callers cycle through a ring of N textures and only
    /// draw the next one while <see cref="VideoInFlight"/> &lt; N - 1. Otherwise frames are lost.
    /// </summary>
    public void WriteVideo(ID3D11Texture2D texture, long time, long duration)
    {
        // One sample per texture, made once and reused (the recorder draws into a fixed ring).
        // Making a new DXGI buffer and sample for every frame cost a few hundred bytes of native
        // memory per frame that never came back: ~70 MB over a two-hour recording (NFR-09a).
        if (!_videoSamples.TryGetValue(texture.NativePointer, out IMFSample? sample))
        {
            object surface = Marshal.GetObjectForIUnknown(texture.NativePointer);
            try
            {
                PInvoke.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, surface, 0, false, out IMFMediaBuffer buffer).ThrowOnFailure();
                try
                {
                    ((IMF2DBuffer)buffer).GetContiguousLength(out uint length);
                    buffer.SetCurrentLength(length);
                    sample = NewSample(buffer, time, duration);
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer); // the sample holds it
                }
            }
            finally
            {
                Marshal.ReleaseComObject(surface);
            }
            _videoSamples[texture.NativePointer] = sample;
        }
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        _writer.WriteSample(_video, sample);
    }

    private readonly Dictionary<nint, IMFSample> _videoSamples = [];

    /// <summary>
    /// Video frames handed over but not yet encoded. Their textures may still be read by the
    /// encoder, so the recorder never draws into one of those (it drops a frame instead).
    /// </summary>
    public int VideoInFlight
    {
        get
        {
            var stats = new MF_SINK_WRITER_STATISTICS { cb = (uint)sizeof(MF_SINK_WRITER_STATISTICS) };
            _writer.GetStatistics(_video, &stats);
            return (int)Math.Min(int.MaxValue, stats.qwNumSamplesReceived - Math.Min(stats.qwNumSamplesReceived, stats.qwNumSamplesEncoded));
        }
    }

    /// <summary>Queues interleaved 16-bit stereo PCM starting at <paramref name="time"/>.</summary>
    public void WriteAudio(ReadOnlySpan<short> pcm, long time)
    {
        if (!HasAudio || pcm.IsEmpty) return;
        long duration = pcm.Length / AudioMixer.Channels * Second / AudioMixer.SampleRate;
        // Raw COM: 30 writes a second for hours must not create .NET COM wrappers (see RawCom).
        RawCom.WriteBytes(_writerPointer, _audio, MemoryMarshal.AsBytes(pcm), time, duration);
    }

    /// <summary>Flushes the encoders and closes the file so it plays everywhere. Call once.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        catch (COMException)
        {
            // Nothing was written (or the disk vanished): the fragments already on disk still play.
        }
        foreach (IMFSample sample in _videoSamples.Values) Marshal.ReleaseComObject(sample);
        _videoSamples.Clear();
        Marshal.Release(_writerPointer);
        Marshal.ReleaseComObject(_writer);
        Marshal.ReleaseComObject(_manager);
        Marshal.ReleaseComObject(_device);
        MediaFoundation.Stop();
    }

    private static IMFSample NewSample(IMFMediaBuffer buffer, long time, long duration)
    {
        PInvoke.MFCreateSample(out IMFSample sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        return sample;
    }

    private IMFMediaType VideoType(Guid subtype)
    {
        PInvoke.MFCreateMediaType(out IMFMediaType type).ThrowOnFailure();
        SetGuid(type, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        SetGuid(type, PInvoke.MF_MT_SUBTYPE, subtype);
        Set64(type, PInvoke.MF_MT_FRAME_SIZE, Pack((uint)Width, (uint)Height));
        Set64(type, PInvoke.MF_MT_FRAME_RATE, Pack(FrameRate, 1));
        Set64(type, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        Set(type, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        return type;
    }

    private static IMFMediaType AudioType(Guid subtype)
    {
        PInvoke.MFCreateMediaType(out IMFMediaType type).ThrowOnFailure();
        SetGuid(type, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
        SetGuid(type, PInvoke.MF_MT_SUBTYPE, subtype);
        Set(type, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioMixer.SampleRate);
        Set(type, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, AudioMixer.Channels);
        Set(type, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        return type;
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static void Set(IMFAttributes a, Guid key, uint value) => a.SetUINT32(&key, value);

    private static void Set64(IMFAttributes a, Guid key, ulong value) => a.SetUINT64(&key, value);

    private static void SetGuid(IMFAttributes a, Guid key, Guid value) => a.SetGUID(&key, &value);

    private static void SetUnknown(IMFAttributes a, Guid key, object value) => a.SetUnknown(&key, value);
}

/// <summary>Reference-counted MFStartup/MFShutdown so writers and readers can overlap.</summary>
internal static class MediaFoundation
{
    private static readonly object Lock = new();
    private static int _users;

    public static void Start()
    {
        lock (Lock)
        {
            if (_users++ == 0) PInvoke.MFStartup(PInvoke.MF_VERSION, 0).ThrowOnFailure();
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            if (_users > 0 && --_users == 0) PInvoke.MFShutdown();
        }
    }
}

/// <summary>
/// Opening a video file to read it back (joining the last minute, making a GIF, checking a
/// recording). Media Foundation's own way of opening a file refuses to share it with a writer
/// that hasn't let go yet (a recorder finishing its file a moment late, another app still
/// writing), which shows up as an unhelpful "unspecified error". This opens it for reading while
/// allowing others to keep writing.
/// </summary>
internal static class MediaFiles
{
    public static Windows.Win32.Foundation.HRESULT OpenReader(string path, Windows.Win32.Media.MediaFoundation.IMFAttributes? attributes, out Windows.Win32.Media.MediaFoundation.IMFSourceReader reader)
    {
        reader = null!;
        Windows.Win32.Foundation.HRESULT hr = Windows.Win32.PInvoke.MFCreateFile(
            Windows.Win32.Media.MediaFoundation.MF_FILE_ACCESSMODE.MF_ACCESSMODE_READ,
            Windows.Win32.Media.MediaFoundation.MF_FILE_OPENMODE.MF_OPENMODE_FAIL_IF_NOT_EXIST,
            Windows.Win32.Media.MediaFoundation.MF_FILE_FLAGS.MF_FILEFLAGS_ALLOW_WRITE_SHARING,
            path, out Windows.Win32.Media.MediaFoundation.IMFByteStream stream);
        if (hr.Failed) return hr;
        try
        {
            return Windows.Win32.PInvoke.MFCreateSourceReaderFromByteStream(stream, attributes, out reader);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(stream); // the reader holds its own reference
        }
    }
}
