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
internal sealed class WgcEngine : ICaptureEngine
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
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private volatile bool _itemClosed;

    /// <param name="showCursor">Include the mouse pointer in the picture (FR-24).</param>
    /// <param name="startFrozen">Freeze on the first frame (used to freeze a DWM-presented source seamlessly).</param>
    public WgcEngine(bool showCursor, bool startFrozen = false)
    {
        _showCursor = showCursor;
        _frozen = startFrozen;
        StartFrozen = startFrozen;
    }

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

    public void SetOpacity(double opacity) => Update(() => _opacity = Math.Clamp(opacity, 0, 1));
    public void OnSourceChanged() => Update(() => { });
    public void OnOutputResized(Size outputClientSize) => Update(() => _outputSize = outputClientSize);
    public void SetCrop(CropRegion? crop) => Update(() => _crop = crop);
    public void SetFrozen(bool frozen) => Update(() => _frozen = frozen);

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
                _wake.WaitOne(_firstFrame.Task.IsCompleted ? 500 : 50);
                if (_stop) break;
                if (_itemClosed)
                {
                    Fail("The captured window or screen went away.");
                    return;
                }

                bool newFrame = TakeLatestFrame();
                bool dirty;
                lock (_sync)
                {
                    dirty = _dirty;
                    _dirty = false;
                }
                if (newFrame || dirty || !_firstFrame.Task.IsCompleted) Render();

                // First frame reached the compositor (or the source has nothing to show, e.g.
                // it's minimised): the output may be revealed. It's drawn at opacity 0 = black.
                if (!_firstFrame.Task.IsCompleted && (_heldBitmap is not null || clock.Elapsed > FirstFrameTimeout))
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

    private void CreateHeldTexture(Size size)
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
            BindFlags = BindFlags.ShaderResource,
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
        lock (_sync)
        {
            opacity = _opacity;
            output = _outputSize;
            crop = _crop;
            message = _message;
            messageBrightness = _messageBrightness;
        }
        if (output.Width <= 0 || output.Height <= 0) return;

        // Windows show their client area only (same as the DWM engine); screens show everything.
        Rectangle? shown = null;
        if (_heldBitmap is not null)
        {
            Rectangle area = _source.IsScreen ? new Rectangle(Point.Empty, _heldSize) : Direct3D.ClientAreaInFrame(_source.Handle, _heldSize);
            shown = crop is null ? area : Offset(crop.ToPixels(area.Size), area.Location);
        }

        // The swap chain only needs the source's own resolution; DWM stretches it to the screen.
        // A small window on a big TV then costs a few MB instead of two full-screen buffers (NFR-03).
        Size canvas = CanvasSize(output, shown?.Size);
        if (Math.Abs(canvas.Width - _targetSize.Width) > 4 || Math.Abs(canvas.Height - _targetSize.Height) > 4) ResizeTarget(canvas);
        canvas = _targetSize;

        _dc!.BeginDraw();
        Color black = Theme.OutputBlack;
        _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));

        if (shown is { } part && opacity > 0.001)
        {
            Rectangle dest = AspectFit.Fit(part.Size, canvas);
            _dc.DrawBitmap(_heldBitmap!, ToRect(dest), (float)opacity, InterpolationMode.HighQualityCubic, ToRect(part), null);
        }
        if (message is { Length: > 0 } && messageBrightness > 0.001) DrawMessage(message, messageBrightness, canvas);

        _dc.EndDraw().CheckError();
        _swapChain!.Present(1, PresentFlags.None).CheckError();
    }

    /// <summary>
    /// Smallest canvas with the output's shape that still shows the source pixel-for-pixel (never
    /// bigger than the output). At least a quarter of the output, so the blank message stays sharp.
    /// </summary>
    internal static Size CanvasSize(Size output, Size? shown)
    {
        double scale = 0.25;
        if (shown is { Width: > 0, Height: > 0 } s)
        {
            Rectangle fit = AspectFit.Fit(s, output);
            if (fit.Width > 0 && fit.Height > 0) scale = Math.Max((double)s.Width / fit.Width, (double)s.Height / fit.Height);
        }
        scale = Math.Clamp(scale, 0.25, 1);
        return new Size(Math.Max(1, (int)Math.Ceiling(output.Width * scale)), Math.Max(1, (int)Math.Ceiling(output.Height * scale)));
    }

    private void DrawMessage(string message, double brightness, Size output)
    {
        float size = Math.Max(9f, output.Height / 22f);
        if (_textFormat is null || Math.Abs(_textFormat.FontSize - size) > 0.5f)
        {
            _textFormat?.Dispose();
            _textFormat = _dwrite!.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            _textFormat.TextAlignment = TextAlignment.Center;
            _textFormat.ParagraphAlignment = ParagraphAlignment.Center;
        }
        _textBrush!.Opacity = (float)brightness;
        _dc!.DrawText(message, _textFormat, new Rect(0, 0, output.Width, output.Height), _textBrush);
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
        _session?.Dispose();
        _pool?.Dispose();
        _item = null;
        _winrtDevice?.Dispose();
        if (_dc is not null) _dc.Target = null;
        _textFormat?.Dispose();
        _textBrush?.Dispose();
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
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(3)))
            Debug.WriteLine("Capture thread didn't stop in time.");
        _firstFrame.TrySetCanceled();
        _wake.Dispose();
    }
}
