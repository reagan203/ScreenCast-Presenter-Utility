using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.System.Power;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Recording;

/// <summary>
/// Records a window, screen or region to MP4 (FR-52) inside the recording helper process, while
/// the user carries on working (FR-57).
///
/// One thread owns every GPU object. Captured frames are copied GPU-to-GPU into a held texture,
/// drawn with Direct2D into one of a ring of fixed-size textures (crop, aspect fit, black bars
/// if the window is resized) and handed to the hardware encoder. Nothing touches CPU memory
/// except audio. Audio is pulled from the mixer by recording time, so sound and picture stay in
/// sync however long the webinar runs.
/// </summary>
internal sealed class Recorder : IDisposable
{
    /// <summary>Biggest video we encode; bigger sources are scaled down (hardware encoders top out around 4K).</summary>
    public static readonly Size MaxVideo = new(3840, 2160);

    /// <summary>Audio is written this far behind real time so every device has delivered it (see <see cref="AudioMixer"/>).</summary>
    private static readonly TimeSpan AudioLag = TimeSpan.FromMilliseconds(100);

    private const int RingSize = 6;

    /// <summary>A pass of the loop takes at most ~40 ms; a gap this long means the PC was asleep.</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(5);

    private readonly RecordingRequest _request;
    private readonly Action<string> _send;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _pauseRequested;
    private volatile bool _itemClosed;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private readonly ID3D11Texture2D?[] _ring = new ID3D11Texture2D?[RingSize];
    private readonly ID2D1Bitmap1?[] _ringTargets = new ID2D1Bitmap1?[RingSize];
    private int _ringNext;
    private ID3D11Texture2D? _held;
    private ID2D1Bitmap1? _heldBitmap;
    private Size _heldSize;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private Mp4Writer? _writer;
    private AudioMixer? _mixer;
    private readonly List<AudioCapture> _audio = [];

    public Recorder(RecordingRequest request, Action<string> send)
    {
        _request = request;
        _send = send;
        _thread = new Thread(Run) { IsBackground = false, Name = "SCP recording" }; // foreground: the file must be finished even if everything else ends
    }

    public RecordingEnd End { get; private set; } = RecordingEnd.Stopped;

    public void Start() => _thread.Start();

    public void Pause()
    {
        _pauseRequested = true;
        _wake.Set();
    }

    public void Resume()
    {
        _pauseRequested = false;
        _wake.Set();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    public void Wait() => _thread.Join();

    // ───────────────────────────── recording thread ─────────────────────────────

    private void Run()
    {
        // Keep the computer awake while recording (a webinar with no keyboard use must not be cut
        // off by sleep). The screen may still turn off. Cleared when this thread ends.
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        TimeSpan recorded = TimeSpan.Zero;
        string detail = "";
        try
        {
            if (!Direct3D.IsCaptureSupported) throw new NotSupportedException("Screen capture isn't available on this version of Windows.");
            Directory.CreateDirectory(Path.GetDirectoryName(_request.Path)!);
            var steps = Stopwatch.StartNew();
            InitialiseCapture();
            DiagLog.Write($"recorder: capture ready {steps.ElapsedMilliseconds} ms");
            Size video = WaitForFirstFrame();
            DiagLog.Write($"recorder: first frame {steps.ElapsedMilliseconds} ms, video {video.Width}x{video.Height}");

            // Loading the hardware encoder takes a second or two on some drivers: start the sound
            // devices meanwhile (what they capture before time zero is dropped).
            Task audio = Task.Run(StartAudio);
            try
            {
                _writer = new Mp4Writer(_request.Path, video.Width, video.Height, _device!, withAudio: WantsAudio);
            }
            finally
            {
                audio.Wait();
            }
            DiagLog.Write($"recorder: encoder ready {steps.ElapsedMilliseconds} ms");
            CreateRing(video);
            DiagLog.Write($"recorder: audio ready {steps.ElapsedMilliseconds} ms ({_audio.Count} sources)");
            _send(RecordingProtocol.Started(video.Width, video.Height));
            (recorded, detail) = Loop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            End = RecordingEnd.Error;
            detail = IsDiskFull(ex) ? "The disk is full." : ex is UnauthorizedAccessException or IOException ? SavedFiles.Explain(ex) : ex.Message;
            if (IsDiskFull(ex)) End = RecordingEnd.DiskFull;
            if (_writer is null)
            {
                _send(RecordingProtocol.Failed(detail));
                Release();
                return;
            }
        }
        finally
        {
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }

        StopAudio();
        try
        {
            _writer?.Finish();
        }
        catch (COMException ex)
        {
            // The fragments already written still play; say what happened.
            Debug.WriteLine(ex);
            if (End == RecordingEnd.Stopped) End = RecordingEnd.Error;
            detail = IsDiskFull(ex) ? "The disk is full." : "The end of the recording couldn't be saved.";
        }
        Release();
        _send(RecordingProtocol.Finished(End, recorded, FileSize(), detail));
    }

    private (TimeSpan Recorded, string Detail) Loop()
    {
        var wall = Stopwatch.StartNew();
        var clock = new RecordingClock(wall.Elapsed);
        var pacer = new FramePacer();
        var disk = new DiskGuard();
        long audioFrames = 0;
        float[] mix = new float[AudioMixer.SampleRate / 10 * AudioMixer.Channels];
        short[] pcm = new short[mix.Length];
        TimeSpan nextReport = TimeSpan.Zero;
        TimeSpan lastPass = wall.Elapsed;
        bool minimised = false;
        _mixer?.Discard(); // time zero is now: drop anything captured while starting up

        while (true)
        {
            TimeSpan now = wall.Elapsed;
            TimeSpan elapsed = clock.Elapsed(now);
            TimeSpan wait = clock.IsPaused ? TimeSpan.FromMilliseconds(250) : pacer.NextDue(elapsed) - elapsed;
            _wake.WaitOne(Math.Clamp((int)wait.TotalMilliseconds, 1, 40)); // audio wants feeding at least every 40 ms

            now = wall.Elapsed;
            if (_stop) return (clock.Elapsed(now), "");
            if (_itemClosed)
            {
                End = RecordingEnd.SourceClosed;
                return (clock.Elapsed(now), _request.Source.IsScreen ? "The screen was disconnected." : "The recorded window closed.");
            }

            // The PC slept (lid closed, power button): leave the missing time out of the file, as if
            // paused, instead of filling it with a frozen picture and silence.
            TimeSpan gap = now - lastPass;
            lastPass = now;
            if (gap > SleepGap && !clock.IsPaused)
            {
                clock.Pause(now - gap);
                clock.Resume(now);
                _mixer?.Discard();
                _send(RecordingProtocol.Warning("asleep", "The computer was asleep, so the recording skips that time."));
            }

            if (_pauseRequested && !clock.IsPaused) clock.Pause(now);
            else if (!_pauseRequested && clock.IsPaused) clock.Resume(now);
            elapsed = clock.Elapsed(now);

            if (TakeLatestFrame()) pacer.FrameArrived();

            if (clock.IsPaused)
            {
                _mixer?.Discard(); // nothing said while paused ends up in the file
            }
            else
            {
                if (pacer.ShouldWrite(elapsed, _heldBitmap is not null)) WriteFrame(elapsed);
                audioFrames = WriteAudio(elapsed, audioFrames, mix, pcm);
            }

            if (now >= nextReport)
            {
                nextReport = now + TimeSpan.FromSeconds(1);
                _send(RecordingProtocol.Progress(elapsed, FileSize(), clock.IsPaused));
                if (DiagLog.Enabled && (int)elapsed.TotalSeconds % 10 == 0)
                {
                    using var self = Process.GetCurrentProcess();
                    DiagLog.Write($"recorder: {(int)elapsed.TotalSeconds}s private={self.PrivateMemorySize64 >> 20} MB managed={GC.GetTotalMemory(false) >> 20} MB gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)} inFlight={_writer?.VideoInFlight}");
                }

                switch (disk.Check(SavedFiles.FreeSpace(Path.GetDirectoryName(_request.Path)!)))
                {
                    case DiskState.Low:
                        _send(RecordingProtocol.Warning("disk", "The disk is nearly full. The recording will stop by itself before it runs out."));
                        break;
                    case DiskState.Critical:
                        End = RecordingEnd.DiskFull;
                        return (elapsed, "The disk was nearly full, so the recording stopped and was saved.");
                }

                if (!_request.Source.IsScreen)
                {
                    // Safety net: Windows doesn't always report a closed window to the capture (FR-59).
                    if (!WindowEnumerator.IsAlive(_request.Source.Handle))
                    {
                        End = RecordingEnd.SourceClosed;
                        return (elapsed, "The recorded window closed.");
                    }
                    bool nowMinimised = WindowEnumerator.IsMinimized(_request.Source.Handle);
                    if (nowMinimised != minimised)
                    {
                        minimised = nowMinimised;
                        _send(nowMinimised
                            ? RecordingProtocol.Warning("minimised", "The recorded window is minimised, so the recording shows its last picture. Sound is still recorded.")
                            : RecordingProtocol.Warning("restored", "The recorded window is back."));
                    }
                }
            }
        }
    }

    private long WriteAudio(TimeSpan elapsed, long written, float[] mix, short[] pcm)
    {
        if (_mixer is null || _writer is not { HasAudio: true }) return written;
        long target = AudioMixer.FramesFor(elapsed - AudioLag);
        while (written < target)
        {
            int frames = (int)Math.Min(target - written, mix.Length / AudioMixer.Channels);
            int samples = frames * AudioMixer.Channels;
            _mixer.Read(frames, mix);
            AudioMixer.ToPcm16(mix.AsSpan(0, samples), pcm);
            _writer.WriteAudio(pcm.AsSpan(0, samples), written * Mp4Writer.Second / AudioMixer.SampleRate);
            written += frames;
        }
        return written;
    }

    private void WriteFrame(TimeSpan elapsed)
    {
        // Never draw into a texture the encoder may still be reading: drop this frame instead
        // (the next one comes within 33 ms, and a still picture is repeated every second anyway).
        if (_writer!.VideoInFlight >= RingSize - 1) return;

        int slot = _ringNext;
        _ringNext = (_ringNext + 1) % RingSize;
        Size video = new(_writer.Width, _writer.Height);

        Rectangle area = _request.Source.IsScreen ? new Rectangle(Point.Empty, _heldSize) : Direct3D.ClientAreaInFrame(_request.Source.Handle, _heldSize);
        Rectangle shown = _request.Source.Crop is { } crop ? Offset(crop.ToPixels(area.Size), area.Location) : area;

        _dc!.Target = _ringTargets[slot];
        _dc.BeginDraw();
        System.Drawing.Color black = Theme.OutputBlack;
        _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));
        if (shown.Width > 0 && shown.Height > 0)
        {
            Rectangle dest = AspectFit.Fit(shown.Size, video);
            _dc.DrawBitmap(_heldBitmap!, ToRect(dest), 1f, InterpolationMode.HighQualityCubic, ToRect(shown), null);
        }
        _dc.EndDraw().CheckError();
        _dc.Target = null;

        _writer.WriteVideo(_ring[slot]!, elapsed.Ticks, Mp4Writer.FrameDuration);
    }

    private void InitialiseCapture()
    {
        _device = Mp4Writer.CreateDevice();
        _context = _device.ImmediateContext;
        using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        }
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dc.SetDpi(96, 96);

        _winrtDevice = Direct3D.CreateWinRTDevice(_device);
        _item = Direct3D.CreateItem(_request.Source);
        _item.Closed += (_, _) =>
        {
            _itemClosed = true;
            _wake.Set();
        };
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += (_, _) => _wake.Set();
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _request.Cursor;
        Direct3D.TryDisableBorder(_session);
        _session.StartCapture();
    }

    /// <summary>
    /// The video size is fixed for the whole file: the first picture's size (cropped, at most 4K,
    /// even). A minimised window sends no picture, so its window size is used instead.
    /// </summary>
    private Size WaitForFirstFrame()
    {
        var clock = Stopwatch.StartNew();
        while (_heldBitmap is null && clock.ElapsedMilliseconds < 1500 && !_stop && !_itemClosed)
        {
            _wake.WaitOne(50);
            TakeLatestFrame();
        }

        Size source;
        if (_heldBitmap is not null)
        {
            Rectangle area = _request.Source.IsScreen ? new Rectangle(Point.Empty, _heldSize) : Direct3D.ClientAreaInFrame(_request.Source.Handle, _heldSize);
            source = _request.Source.Crop is { } crop ? crop.ToPixels(area.Size).Size : area.Size;
        }
        else
        {
            source = new Size(_poolSize.Width, _poolSize.Height);
            if (source.Width < 320 || source.Height < 200) source = new Size(1280, 720);
        }
        return Mp4Writer.FitSize(source, MaxVideo);
    }

    private void CreateRing(Size video)
    {
        var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw);
        for (int i = 0; i < RingSize; i++)
        {
            _ring[i] = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)video.Width, (uint)video.Height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            using IDXGISurface surface = _ring[i]!.QueryInterface<IDXGISurface>();
            _ringTargets[i] = _dc!.CreateBitmapFromDxgiSurface(surface, props);
        }
    }

    /// <summary>Copies the newest captured frame into the held texture. False if nothing new arrived.</summary>
    private bool TakeLatestFrame()
    {
        Direct3D11CaptureFrame? latest = null;
        while (_pool!.TryGetNextFrame() is { } frame)
        {
            latest?.Dispose();
            latest = frame;
        }
        if (latest is null) return false;

        using (latest)
        {
            SizeInt32 content = latest.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                _poolSize = content;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
            if (content.Width <= 0 || content.Height <= 0) return false;

            using ID3D11Texture2D frameTexture = Direct3D.TextureOf(latest.Surface);
            var size = new Size(content.Width, content.Height);
            if (_held is null || _heldSize != size) CreateHeldTexture(size);
            _context!.CopySubresourceRegion(_held!, 0, 0, 0, 0, frameTexture, 0, new Box(0, 0, 0, size.Width, size.Height, 1));
            return true;
        }
    }

    private void CreateHeldTexture(Size size)
    {
        _heldBitmap?.Dispose();
        _held?.Dispose();
        _held = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.ShaderResource));
        using IDXGISurface surface = _held.QueryInterface<IDXGISurface>();
        _heldBitmap = _dc!.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        _heldSize = size;
    }

    /// <summary>
    /// Starts the chosen sound sources. A missing microphone or a Windows version without app-only
    /// capture doesn't stop the recording: SCP says what happened and records what it can.
    /// </summary>
    private bool WantsAudio => _request.Sound != MeetingSound.None || _request.Microphone;

    private void StartAudio()
    {
        if (!WantsAudio) return;
        int count = (_request.Sound != MeetingSound.None ? 1 : 0) + (_request.Microphone ? 1 : 0);
        _mixer = new AudioMixer(count);
        int index = 0;

        if (_request.Sound != MeetingSound.None)
        {
            int slot = index++;
            void Sink(ReadOnlySpan<float> s) => _mixer.Write(slot, s);
            uint pid = _request.Source.IsScreen ? 0 : WindowEnumerator.ProcessOf(_request.Source.Handle);
            if (_request.Sound == MeetingSound.App && pid != 0)
            {
                try
                {
                    _audio.Add(AudioCapture.ProcessLoopback(pid, Sink));
                }
                catch (Exception ex) when (ex is COMException or TimeoutException or InvalidCastException or EntryPointNotFoundException)
                {
                    Debug.WriteLine(ex);
                    _send(RecordingProtocol.Warning("appsound", "This version of Windows can't record one app's sound on its own, so all computer sound is recorded."));
                }
            }
            if (_audio.Count == 0) TryAudio(() => AudioCapture.SystemLoopback(Sink), "sound", "The computer's sound can't be recorded (no speakers or headphones found).");
        }

        if (_request.Microphone)
        {
            int slot = index;
            TryAudio(() => AudioCapture.Microphone(s => _mixer.Write(slot, s)), "mic", "No microphone was found, so the recording has no microphone sound.");
        }
    }

    private void TryAudio(Func<AudioCapture> start, string code, string warning)
    {
        try
        {
            _audio.Add(start());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine(ex);
            _send(RecordingProtocol.Warning(code, warning));
        }
    }

    private void StopAudio()
    {
        foreach (AudioCapture a in _audio) a.Dispose();
        _audio.Clear();
    }

    private long FileSize()
    {
        try
        {
            return new FileInfo(_request.Path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool IsDiskFull(Exception ex) => ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    private static Rectangle Offset(Rectangle r, Point by)
    {
        r.Offset(by);
        return r;
    }

    private static Rect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private void Release()
    {
        StopAudio();
        _session?.Dispose();
        _pool?.Dispose();
        _item = null;
        _winrtDevice?.Dispose();
        _writer?.Dispose();
        _writer = null;
        if (_dc is not null) _dc.Target = null;
        foreach (ID2D1Bitmap1? b in _ringTargets) b?.Dispose();
        foreach (ID3D11Texture2D? t in _ring) t?.Dispose();
        _heldBitmap?.Dispose();
        _held?.Dispose();
        _dc?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _context?.ClearState();
        _context?.Dispose();
        _device?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _thread.Join(TimeSpan.FromSeconds(30)); // finishing a long file can take a moment
        _wake.Dispose();
    }
}
