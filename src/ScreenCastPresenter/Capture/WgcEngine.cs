using System.Diagnostics;
using System.Numerics;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using ScreenCastPresenter.UI;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Windows.Graphics.Capture engine (FR-31): whole screens, freeze (the last frame stays on the
/// GPU), the mouse pointer (FR-24) and minimised windows (the last picture stays, UC-06).
///
/// One render thread owns every GPU object. Captured frames are copied GPU-to-GPU into a held
/// texture and drawn with Direct2D into a flip-model swap chain on the output window: crop,
/// aspect fit, letterbox and fade happen in one draw. Frames never touch CPU memory (rule 1).
/// UI-thread calls just update a few fields and wake the thread.
/// </summary>
internal sealed partial class WgcEngine : ICaptureEngine, IEffectsEngine
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly bool _showCursor;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private SynchronizationContext? _ui;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _failed;

    // Shared with the UI thread under _sync.
    private double _opacity;
    private Size _outputSize;
    private CropRegion? _crop;
    private bool _frozen;
    private string? _message;
    private double _messageBrightness;
    private bool _dirty;
    private bool _cursor;
    private string? _logoPath;
    private bool _logoChanged;

    // Automatic hiding (FR-88): asked for under _sync, run on the render thread and a worker.
    private SensitiveKinds _autoKinds;
    private RedactStyle _autoStyle;
    private bool _autoChanged;
    private AutoCover? _cover;
    private bool _pictureFresh;
    private Rectangle _lastArea;
    private string _autoWords = "";
    private System.Text.RegularExpressions.Regex? _autoPattern;

    // Marks over the picture (FR-65, FR-71): their own lock; a change wakes the render thread.
    private readonly EffectsLayer _effects;
    private bool _cursorApplied;

    // Render-thread only.
    private SourceWindow _source = null!;
    private nint _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _target;
    private Size _targetSize;
    private ID3D11Texture2D? _held;
    private ID2D1Bitmap1? _heldBitmap;
    private Size _heldSize;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _textFormat;
    private ID2D1SolidColorBrush? _textBrush;
    private ID2D1Bitmap1? _logo;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private volatile bool _itemClosed;
    private CameraFeed? _camera; // a camera instead of screen capture (FR-68)

    // The small picture (FR-72): asked for on any thread, set up and drawn on the render thread.
    private (SourceWindow Source, InsetCorner Corner, int Percent)? _insetWanted;
    private bool _insetChanged;
    private InsetFeed? _inset;
    private SourceWindow? _insetSource;
    private InsetCorner _insetCorner;
    private int _insetPercent = InsetLayout.DefaultSize;
    private ID2D1SolidColorBrush? _insetBorder;

    /// <summary>The small picture couldn't start or went away (closed, unplugged); it has been taken off.</summary>
    public event EventHandler<string>? InsetFailed;

    public void Inset(SourceWindow? source, InsetCorner corner, int percent) => Update(() =>
    {
        _insetWanted = source is null ? null : (source, corner, percent);
        _insetChanged = true;
    });

    /// <param name="showCursor">Include the mouse pointer in the picture (FR-24).</param>
    /// <param name="startFrozen">Freeze on the first frame (used to freeze a DWM-presented source seamlessly).</param>
    /// <param name="autoHide">Automatic hiding from the start (FR-88): the first picture waits for the first scan.</param>
    /// <param name="autoWords">The presenter's own words for automatic hiding.</param>
    public WgcEngine(bool showCursor, bool startFrozen = false, SensitiveKinds autoHide = SensitiveKinds.None, RedactStyle autoStyle = RedactStyle.Solid,
        IReadOnlyList<string>? autoWords = null)
    {
        _autoKinds = autoHide;
        _autoPattern = SensitiveText.CustomPattern(autoWords);
        _autoStyle = autoStyle;
        _autoChanged = autoHide != SensitiveKinds.None;
        _showCursor = showCursor;
        _cursor = showCursor;
        _cursorApplied = showCursor;
        _frozen = startFrozen;
        StartFrozen = startFrozen;
        _effects = new EffectsLayer(() => Update(() => { }));
    }

    // ───────────────────────────── marks on the TV ─────────────────────────────

    public void Click(PointF at, bool secondary) => _effects.Click(at, secondary);
    public void Pointer(PointF? at) => _effects.Pointer(at);
    public void Laser(PointF? at) => _effects.Laser(at);
    public void InkStart(InkStroke stroke) => _effects.InkStart(stroke);
    public void InkAdd(int id, IReadOnlyList<PointF> points) => _effects.InkAdd(id, points);
    public void InkRemove(int id) => _effects.InkRemove(id);
    public void InkClear() => _effects.InkClear();
    public void InkSet(int id, IReadOnlyList<PointF> points) => _effects.InkSet(id, points);
    public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) => _effects.Redact(regions, style);
    public void Spotlight(PointF? at) => _effects.Spotlight(at);
    public void Caption(string? name, string? second) => _effects.Caption(name, second);
    public void Qr(string? text, InsetCorner corner) => _effects.Qr(text, corner);
    public void LiveCaption(string? text) => _effects.LiveCaption(text);
    public void CaptionStyle(CaptionLook look) => _effects.CaptionStyle(look);
    public void KeyCombo(string? text) => _effects.KeyCombo(text);

    public void AutoHide(SensitiveKinds kinds, RedactStyle style) => Update(() =>
    {
        _autoKinds = kinds;
        _autoStyle = style;
        _autoChanged = true;
    });

    public void AutoHideWords(IReadOnlyList<string> words)
    {
        System.Text.RegularExpressions.Regex? pattern = SensitiveText.CustomPattern(words); // built here, not on the render thread
        Update(() =>
        {
            _autoPattern = pattern;
            _autoChanged = true;
        });
    }

    /// <summary>Automatic hiding covered something new (in words).</summary>
    public event EventHandler<string>? AutoHidden;

    public void Logo(string? path) => Update(() =>
    {
        if (string.Equals(path, _logoPath, StringComparison.OrdinalIgnoreCase)) return;
        _logoPath = path;
        _logoChanged = true;
    });

    /// <summary>Only if it was asked for at the start (the pointer setting): the laser can hide it, never add it.</summary>
    public void ShowCursor(bool on) => Update(() => _cursor = on && _showCursor);

    public string Name => "Windows Graphics Capture";
    public bool SupportsFreeze => true;
    public bool IsFrozen { get { lock (_sync) return _frozen; } }
    private bool StartFrozen { get; }

    public event EventHandler<string>? Failed;

    public Task StartAsync(SourceWindow source, nint output, Size outputClientSize)
    {
        if (_thread is not null) throw new InvalidOperationException("Engine already started.");
        if (!Direct3D.IsCaptureSupported) throw new NotSupportedException("Screen capture isn't available on this version of Windows.");
        _ui = SynchronizationContext.Current;
        _source = source;
        _output = output;
        _outputSize = outputClientSize;
        _crop = source.Crop;
        _opacity = 0;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "SCP capture" };
        _thread.Start();
        return _firstFrame.Task;
    }

    public void SetOpacity(double opacity)
    {
        Update(() => _opacity = Math.Clamp(opacity, 0, 1));
        OnOpacityForVideo(Math.Clamp(opacity, 0, 1));
    }

    public void OnSourceChanged() => Update(() => _pictureCheck = true);
    public void OnOutputResized(Size outputClientSize) => Update(() => _outputSize = outputClientSize);
    public void SetCrop(CropRegion? crop) => Update(() => _crop = crop);
    public void SetFrozen(bool frozen)
    {
        Update(() => _frozen = frozen);
        _video?.SetHeld(frozen); // frozen: a video pauses, and plays on from there
    }

    public void SetIdleMessage(string? message, double brightness) => Update(() =>
    {
        _message = message;
        _messageBrightness = Math.Clamp(brightness, 0, 1);
    });

    private void Update(Action change)
    {
        lock (_sync)
        {
            change();
            _dirty = true;
        }
        _wake.Set();
    }

    // ───────────────────────────── render thread ─────────────────────────────

    private void RenderLoop()
    {
        try
        {
            Initialise();
            var clock = Stopwatch.StartNew();
            while (!_stop)
            {
                bool animating = _effects.IsAnimating;
                _wake.WaitOne(animating ? 16 : _firstFrame.Task.IsCompleted ? 500 : 50);
                if (_stop) break;
                if (_itemClosed)
                {
                    Fail("The captured window or screen went away.");
                    return;
                }
                if (_camera?.Failure is { } cameraProblem)
                {
                    Fail(cameraProblem);
                    return;
                }

                bool newFrame = TakeLatestFrame();
                newFrame |= PullInset();
                newFrame |= PullSplit();
                bool dirty;
                lock (_sync)
                {
                    dirty = _dirty;
                    _dirty = false;
                }
                if (newFrame || dirty || animating || !_firstFrame.Task.IsCompleted) Render();
                ScanForAutoHide(newFrame);

                // First frame reached the compositor (or the source has nothing to show, e.g.
                // it's minimised): the output may be revealed. It's drawn at opacity 0 = black.
                // With automatic hiding on, the first frame waits for its first scan (FR-88).
                bool scanned = (_cover?.FirstScanDone ?? true) && PanesScanned();
                TimeSpan limit = FirstFrameTimeout + (_cover is not null ? AutoRedactor.FirstScanLimit : TimeSpan.Zero);
                if (!_firstFrame.Task.IsCompleted && (_heldBitmap is not null && scanned || clock.Elapsed > limit))
                    _firstFrame.TrySetResult();
            }
            ClearToBlack(); // the output must never be left showing a stale picture
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (!_firstFrame.TrySetException(ex)) Fail("The capture stopped working (" + ex.Message + ").");
        }
        finally
        {
            Release();
        }
    }

    private void Initialise()
    {
        _device = Direct3D.CreateDevice();
        _context = _device.ImmediateContext;

        using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
        using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)CanvasSize(_outputSize, null).Width, // small until the first frame says how big it needs to be
                Height = (uint)CanvasSize(_outputSize, null).Height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForHwnd(_device, _output, desc);
            factory.MakeWindowAssociation(_output, WindowAssociationFlags.IgnoreAll); // no Alt+Enter fullscreen switching
            _targetSize = CanvasSize(_outputSize, null);

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        }
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dc.SetDpi(96, 96); // draw in pixels
        CreateTarget();

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _textBrush = _dc.CreateSolidColorBrush(new Color4(0x9A / 255f, 0x9A / 255f, 0xA2 / 255f, 1));

        if (_source.Picture is { } file)
        {
            // A picture (FR-103): read once, held like a frozen frame.
            using Bitmap picture = PictureSource.Load(file) ?? throw new InvalidOperationException($"“{PictureSource.Name(file)}” can't be shown: it isn't a picture this PC can read.");
            CreateHeldTexture(picture.Size);
            System.Drawing.Imaging.BitmapData data = picture.LockBits(new Rectangle(Point.Empty, picture.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                _context.UpdateSubresource(_held!, 0, null, data.Scan0, (uint)data.Stride, 0);
            }
            finally
            {
                picture.UnlockBits(data);
            }
            _pictureFresh = true;
            try { _pictureWritten = File.GetLastWriteTimeUtc(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
            ClearToBlack();
            return;
        }
        if (_source.Video is { } videoFile)
        {
            // A video (FR-116): Windows' media player decodes it; its first frame arrives once it has opened.
            StartVideo(videoFile);
            ClearToBlack();
            return;
        }
        if (_source.Camera is { } camera)
        {
            // A camera: frames come from Windows' camera pipeline instead of screen capture.
            _camera = CameraFeed.Start(camera.Id, () => _wake.Set());
            ClearToBlack();
            return;
        }

        // Start capturing.
        _winrtDevice = Direct3D.CreateWinRTDevice(_device);
        _item = Direct3D.CreateItem(_source);
        _item.Closed += (_, _) =>
        {
            _itemClosed = true;
            _wake.Set();
        };
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize); // one buffer: we keep our own copy
        _pool.FrameArrived += (_, _) => _wake.Set();
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _showCursor;
        Direct3D.TryDisableBorder(_session);
        _session.StartCapture();
        ClearToBlack();
    }

    /// <summary>On the render thread: applies a change to automatic hiding, then scans a new frame when one is due.</summary>
    private void ScanForAutoHide(bool newFrame)
    {
        SensitiveKinds kinds;
        RedactStyle style;
        bool changed;
        System.Text.RegularExpressions.Regex? pattern;
        lock (_sync)
        {
            kinds = _autoKinds;
            style = _autoStyle;
            pattern = _autoPattern;
            changed = _autoChanged;
            _autoChanged = false;
        }
        if (changed)
        {
            if (kinds == SensitiveKinds.None)
            {
                _cover?.Dispose();
                _cover = null;
                _effects.AutoCovers([]);
                _autoWords = "";
            }
            else
            {
                if (_cover is null)
                {
                    AutoCover? made = null;
                    made = new AutoCover(_source, () =>
                    {
                        if (made is { } c && ReferenceEquals(_cover, c)) _effects.AutoCovers(c.Covers());
                    }, OnAutoFound);
                    _cover = made;
                }
                _cover.Configure(kinds, pattern, style);
                newFrame = true; // what to look for changed: scan again now
            }
            ConfigurePaneCovers(kinds, pattern, style);
        }
        if (_cover is { } cover && _heldBitmap is not null && _target is not null && !IsFrozen)
            cover.MaybeScan(_dc!, _heldBitmap, _lastArea, newFrame, _target);
        ScanPanes();
    }

    /// <summary>Something new was covered (in words): reported once, on the UI thread when there is one.</summary>
    private void OnAutoFound(string words)
    {
        if (words == _autoWords) return;
        _autoWords = words;
        if (_ui is { } ui) ui.Post(_ => AutoHidden?.Invoke(this, words), null);
        else AutoHidden?.Invoke(this, words); // the capture helper: no UI thread
    }

    private void CreateTarget()
    {
        using IDXGISurface backBuffer = _swapChain!.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _dc!.CreateBitmapFromDxgiSurface(backBuffer, props);
        _dc.Target = _target;
    }

    /// <summary>
    /// Copies the newest captured frame (dropping older ones) into the held texture, unless frozen.
    /// A minimised window simply stops sending frames, so the last picture stays (UC-06).
    /// </summary>
    private bool TakeLatestFrame()
    {
        if (_camera is not null) return TakeCameraFrame();
        if (_video is not null) return TakeVideoFrame();
        if (_source.IsPicture)
        {
            bool fresh = _pictureFresh | ReloadPictureIfChanged();
            _pictureFresh = false;
            return fresh;
        }
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
                // The source was resized: grow/shrink the pool so the next frames fit.
                _poolSize = content;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, content);
            }

            bool frozen;
            lock (_sync) frozen = _frozen && _heldBitmap is not null;
            if (frozen || content.Width <= 0 || content.Height <= 0) return false;

            using ID3D11Texture2D frameTexture = Direct3D.TextureOf(latest.Surface);
            var size = new Size(content.Width, content.Height);
            if (_held is null || _heldSize != size) CreateHeldTexture(size);
            _context!.CopySubresourceRegion(_held!, 0, 0, 0, 0, frameTexture, 0, new Box(0, 0, 0, size.Width, size.Height, 1));
            return true;
        }
    }

    /// <summary>
    /// Starts, changes or stops the small picture as asked, and takes its newest frame (not while
    /// frozen: the whole TV holds still). True when there's something new to draw.
    /// </summary>
    private bool PullInset()
    {
        (SourceWindow Source, InsetCorner Corner, int Percent)? wanted;
        bool changed, frozen;
        lock (_sync)
        {
            wanted = _insetWanted;
            changed = _insetChanged;
            _insetChanged = false;
            frozen = _frozen;
        }
        if (changed)
        {
            if (_inset is not null && !(wanted is { } keep && keep.Source.IsSameSource(_insetSource)))
            {
                _inset.Dispose();
                _inset = null;
                _insetSource = null;
            }
            if (wanted is { } w)
            {
                _insetCorner = w.Corner;
                _insetPercent = w.Percent;
                if (_inset is null)
                {
                    try
                    {
                        _winrtDevice ??= Direct3D.CreateWinRTDevice(_device!); // a camera main picture has none of its own
                        _inset = InsetFeed.Start(w.Source, _device!, _winrtDevice, _dc!, () => _wake.Set());
                        _insetSource = w.Source;
                    }
                    catch (InvalidOperationException ex)
                    {
                        ReportInset(ex.Message);
                    }
                }
            }
            return true;
        }
        if (_inset is null) return false;
        if (_inset.Failure is { } reason)
        {
            _inset.Dispose();
            _inset = null;
            _insetSource = null;
            lock (_sync) _insetWanted = null;
            ReportInset(reason);
            return true;
        }
        bool fresh = _inset.Pull(_context!, _winrtDevice, frozen);
        if (_inset.Failure is { } problem) // e.g. a camera that couldn't start, found out just now
        {
            _inset.Dispose();
            _inset = null;
            _insetSource = null;
            lock (_sync) _insetWanted = null;
            ReportInset(problem);
            return true;
        }
        return fresh;
    }

    private void ReportInset(string reason)
    {
        if (_ui is { } ui) ui.Post(_ => InsetFailed?.Invoke(this, reason), null);
        else InsetFailed?.Invoke(this, reason); // the capture helper: no UI thread
    }

    /// <summary>The small picture in its corner, at the picture's brightness, with a faint edge so it stands apart.</summary>
    private void DrawInset(Size canvas, double opacity)
    {
        if (_inset?.Bitmap is not { } bitmap || opacity <= 0.001) return;
        if (_insetCover is { FirstScanDone: false } || _insetSource is not null && !_insetSource.IsSameSource(_insetCoverSource) && _paneKinds != SensitiveKinds.None) return; // not looked at yet
        DrawInsetShaped(bitmap, _inset.WholeArea, _inset.ContentArea, new System.Drawing.SizeF(canvas.Width, canvas.Height), opacity, _insetShapeNow);
    }

    // What the frame being drawn uses (render thread), taken under the lock at its start.
    private NightMode _nightNow;
    private InsetShape _insetShapeNow;

    /// <summary>The newest camera frame onto the GPU (unless frozen: then frames are dropped and the picture stays).</summary>
    private bool TakeCameraFrame()
    {
        bool frozen;
        lock (_sync) frozen = _frozen && _heldBitmap is not null;
        if (frozen)
        {
            _camera!.TakeLatest((_, _, _) => { });
            return false;
        }
        return _camera!.TakeLatest((pixels, stride, size) =>
        {
            if (_held is null || _heldSize != size) CreateHeldTexture(size);
            _context!.UpdateSubresource<byte>(pixels, _held!, 0, (uint)stride, 0, null);
        });
    }

    /// <param name="drawInto">Something draws into it on the GPU (a video's frames), not just copies.</param>
    private void CreateHeldTexture(Size size, bool drawInto = false)
    {
        _heldBitmap?.Dispose();
        _held?.Dispose();
        _held = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = drawInto ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource,
        });
        using IDXGISurface surface = _held.QueryInterface<IDXGISurface>();
        _heldBitmap = _dc!.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        _heldSize = size;
    }

    private void Render()
    {
        double opacity;
        Size output;
        CropRegion? crop;
        string? message;
        double messageBrightness;
        bool frozen, cursor, logoChanged;
        string? logoPath;
        lock (_sync)
        {
            _nightNow = _night;
            _insetShapeNow = _insetShape;
            logoChanged = _logoChanged;
            _logoChanged = false;
            logoPath = _logoPath;
            opacity = _opacity;
            output = _outputSize;
            crop = _crop;
            message = _message;
            messageBrightness = _messageBrightness;
            frozen = _frozen;
            cursor = _cursor;
        }
        if (output.Width <= 0 || output.Height <= 0) return;
        if (logoChanged)
        {
            _logo?.Dispose();
            _logo = LoadLogo(logoPath);
        }
        if (cursor != _cursorApplied && _session is not null)
        {
            try
            {
                _session.IsCursorCaptureEnabled = cursor;
                _cursorApplied = cursor;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Debug.WriteLine(ex); // the pointer just stays as it was
                _cursorApplied = cursor;
            }
        }

        // Windows show their client area only (same as the DWM engine); screens show everything.
        Rectangle? shown = null;
        Rectangle area = Rectangle.Empty;
        if (_heldBitmap is not null)
        {
            area = _source.IsWindow ? Direct3D.ClientAreaInFrame(_source.Handle, _heldSize) : new Rectangle(Point.Empty, _heldSize);
            _lastArea = area;
            shown = crop is null ? area : Offset(crop.ToPixels(area.Size), area.Location);
        }

        // The swap chain only needs the source's own resolution; DWM stretches it to the screen.
        // A small window on a big TV then costs a few MB instead of two full-screen buffers (NFR-03).
        // Words, a code or a logo on top need enough pixels to stay sharp on the TV.
        bool showsMessage = message is { Length: > 0 } && messageBrightness > 0.001;
        Size canvas = CanvasSize(output, shown?.Size, _panes.Count > 0 ? 0.75 : _effects.HasOverlay || (showsMessage && _logo is not null) ? 0.5 : 0.25);
        if (Math.Abs(canvas.Width - _targetSize.Width) > 4 || Math.Abs(canvas.Height - _targetSize.Height) > 4) ResizeTarget(canvas);
        canvas = _targetSize;

        _dc!.BeginDraw();
        Color black = Theme.OutputBlack;
        _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));

        if (shown is { } part && opacity > 0.001)
        {
            List<System.Drawing.RectangleF>? cells = SplitCells(canvas);
            Rectangle dest = cells is null ? AspectFit.Fit(part.Size, canvas) : Rectangle.Round(SplitScreen.Fit(part.Size, cells[0]));
            DrawPicture(_heldBitmap!, part, dest, opacity, _nightNow);
            _effects.Render(_dc, _d2dFactory!, _dwrite!, _heldBitmap!, area, part, dest, (float)opacity, frozen);
            if (cells is not null) DrawPanes(cells, opacity);
            else DrawInset(canvas, opacity);
        }
        if (showsMessage) DrawMessage(message!, messageBrightness, canvas);
        // Words and codes on top show with the picture or the black screen's message, never on plain black.
        float picture = shown is not null ? (float)opacity : 0;
        float visibility = (float)Math.Max(picture, showsMessage ? messageBrightness : 0);
        _effects.RenderOverlay(_dc, _d2dFactory!, _dwrite!, new System.Drawing.SizeF(canvas.Width, canvas.Height), visibility, picture);

        _dc.EndDraw().CheckError();
        _swapChain!.Present(1, PresentFlags.None).CheckError();
    }

    /// <summary>
    /// Smallest canvas with the output's shape that still shows the source pixel-for-pixel (never
    /// bigger than the output). At least a quarter of the output, so the blank message stays sharp.
    /// </summary>
    internal static Size CanvasSize(Size output, Size? shown, double minimum = 0.25)
    {
        double scale = minimum;
        if (shown is { Width: > 0, Height: > 0 } s)
        {
            Rectangle fit = AspectFit.Fit(s, output);
            if (fit.Width > 0 && fit.Height > 0) scale = Math.Max((double)s.Width / fit.Width, (double)s.Height / fit.Height);
        }
        scale = Math.Clamp(scale, Math.Clamp(minimum, 0.25, 1), 1);
        return new Size(Math.Max(1, (int)Math.Ceiling(output.Width * scale)), Math.Max(1, (int)Math.Ceiling(output.Height * scale)));
    }

    /// <summary>The blank screen's message, with your logo above it (FR-80) when there is one.</summary>
    private void DrawMessage(string message, double brightness, Size output)
    {
        float size = Math.Max(9f, output.Height / 22f);
        if (_textFormat is null || Math.Abs(_textFormat.FontSize - size) > 0.5f)
        {
            _textFormat?.Dispose();
            _textFormat = _dwrite!.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            _textFormat.TextAlignment = TextAlignment.Center;
        }
        var canvas = new System.Drawing.SizeF(output.Width, output.Height);
        System.Drawing.RectangleF logo = _logo is null ? System.Drawing.RectangleF.Empty
            : Layouts.Logo(canvas, new System.Drawing.SizeF(_logo.PixelSize.Width, _logo.PixelSize.Height), withMessage: true);
        if (!logo.IsEmpty) _dc!.DrawBitmap(_logo!, new Rect(logo.X, logo.Y, logo.Width, logo.Height), (float)brightness, InterpolationMode.HighQualityCubic, null, null);
        System.Drawing.RectangleF text = Layouts.Message(canvas, logo);
        _textFormat.ParagraphAlignment = logo.IsEmpty ? ParagraphAlignment.Center : ParagraphAlignment.Near;
        _textBrush!.Opacity = (float)brightness;
        _dc!.DrawText(message, _textFormat, new Rect(text.X, text.Y, text.Width, text.Height), _textBrush);
    }

    /// <summary>The logo file onto the GPU; null when there's none or it can't be read (the blank screen just shows its message).</summary>
    private ID2D1Bitmap1? LoadLogo(string? path)
    {
        using System.Drawing.Bitmap? image = LogoImage.Load(path);
        if (image is null) return null;
        System.Drawing.Imaging.BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            return _dc!.CreateBitmap(new SizeI(image.Width, image.Height), data.Scan0, (uint)data.Stride,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    private void ResizeTarget(Size size)
    {
        _dc!.Target = null;
        _target?.Dispose();
        _swapChain!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTarget();
        _targetSize = size;
    }

    private void ClearToBlack()
    {
        if (_dc is null || _swapChain is null) return;
        try
        {
            _dc.BeginDraw();
            Color black = Theme.OutputBlack;
            _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));
            _dc.EndDraw();
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Debug.WriteLine(ex); // device lost: the window falls back to its own black background
        }
    }

    private void Fail(string reason)
    {
        if (_failed) return;
        _failed = true;
        if (_ui is { } ui) ui.Post(_ => Failed?.Invoke(this, reason), null);
        else Failed?.Invoke(this, reason); // capture helper process: no UI thread
    }

    private static Rectangle Offset(Rectangle r, Point by)
    {
        r.Offset(by);
        return r;
    }

    private static Rect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private void Release()
    {
        StopVideoQuickly(); // its sound stops now; the player itself is let go last (it can take a moment)
        _cover?.Dispose();
        _cover = null;
        DisposePanes();
        _camera?.Dispose(); // the camera's light goes off with the picture
        _inset?.Dispose();
        _inset = null;
        _insetBorder?.Dispose();
        _session?.Dispose();
        _pool?.Dispose();
        _item = null;
        _winrtDevice?.Dispose();
        if (_dc is not null) _dc.Target = null;
        _effects.Dispose();
        _textFormat?.Dispose();
        _textBrush?.Dispose();
        _logo?.Dispose();
        _dwrite?.Dispose();
        _heldBitmap?.Dispose();
        _held?.Dispose();
        _target?.Dispose();
        _dc?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _swapChain?.Dispose();
        if (_context is not null && _device is not null)
        {
            // Hand the driver's internal allocations back to Windows now that we're done (Microsoft's
            // advice for GPU apps going idle): otherwise they linger in our process memory.
            _context.ClearState();
            _context.Flush();
            try
            {
                using IDXGIDevice3 dxgi = _device.QueryInterface<IDXGIDevice3>();
                dxgi.Trim();
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                Debug.WriteLine(ex);
            }
        }
        _context?.Dispose();
        _device?.Dispose();
        ReleaseExtras(); // after the swap chain: a new engine on the same window can start at once
    }

    private bool _disposed;

    /// <summary>Stops the render thread (which presents a last black frame). Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _wake.Set();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(3)))
            Debug.WriteLine("Capture thread didn't stop in time.");
        _firstFrame.TrySetCanceled();
        _wake.Dispose();
    }
}
