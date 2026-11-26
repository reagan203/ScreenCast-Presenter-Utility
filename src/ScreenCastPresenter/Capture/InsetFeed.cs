using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The small picture's own capture in the helper (FR-72): another window, a screen, or a camera,
/// on the same GPU device as the main picture, kept as one GPU texture. The main engine's render
/// thread owns it and draws it in a corner. A window closing or a camera going away is reported
/// through <see cref="Failure"/> and the small picture is taken off.
/// </summary>
internal sealed class InsetFeed : IDisposable
{
    private readonly Interop.SourceWindow _source;
    private readonly ID3D11Device _device;
    private readonly ID2D1DeviceContext _dc;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private CameraFeed? _camera;
    private Task<CameraFeed>? _cameraStarting;
    private string? _cameraProblem;
    private ID3D11Texture2D? _held;
    private volatile bool _closed;
    private System.Drawing.Bitmap? _picture;

    private InsetFeed(Interop.SourceWindow source, ID3D11Device device, ID2D1DeviceContext dc)
    {
        _source = source;
        _device = device;
        _dc = dc;
    }

    public ID2D1Bitmap1? Bitmap { get; private set; }
    public Size Size { get; private set; }

    /// <summary>Why the small picture can't go on (closed, unplugged); null while it's fine.</summary>
    public string? Failure => _cameraProblem ?? _camera?.Failure ?? (_closed ? $"“{(_source.Title.Length > 0 ? _source.Title : "The small picture")}” closed, so the small picture was taken off." : null);

    /// <summary>The part of a frame to show: a window's client area, or everything.</summary>
    public Rectangle ContentArea => _source.IsWindow ? Direct3D.ClientAreaInFrame(_source.Handle, Size) : new Rectangle(System.Drawing.Point.Empty, Size);

    /// <summary>Starts capturing <paramref name="source"/>. Throws <see cref="InvalidOperationException"/> (plain words) when it can't.</summary>
    public static InsetFeed Start(Interop.SourceWindow source, ID3D11Device device, IDirect3DDevice? winrtDevice, ID2D1DeviceContext dc, Action wake)
    {
        var feed = new InsetFeed(source, device, dc);
        try
        {
            if (source.Picture is { } file)
            {
                feed._picture = Core.PictureSource.Load(file) ?? throw new InvalidOperationException($"“{Core.PictureSource.Name(file)}” can't be shown: it isn't a picture this PC can read.");
                wake();
                return feed;
            }
            if (source.Camera is { } camera)
            {
                // A camera takes a second or two to start: in the background, so the render thread
                // keeps drawing (and a blank or a cut to black reaches the TV) meanwhile.
                feed._cameraStarting = Task.Run(() => CameraFeed.Start(camera.Id, wake));
                feed._cameraStarting.ContinueWith(_ => wake(), TaskScheduler.Default);
                return feed;
            }
            IDirect3DDevice winrt = winrtDevice ?? throw new InvalidOperationException("The small picture couldn't start.");
            feed._item = Direct3D.CreateItem(source);
            feed._item.Closed += (_, _) =>
            {
                feed._closed = true;
                wake();
            };
            feed._poolSize = feed._item.Size;
            feed._pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, feed._poolSize);
            feed._pool.FrameArrived += (_, _) => wake();
            feed._session = feed._pool.CreateCaptureSession(feed._item);
            feed._session.IsCursorCaptureEnabled = false; // a pointer in the small picture only distracts
            Direct3D.TryDisableBorder(feed._session);
            feed._session.StartCapture();
            return feed;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            feed.Dispose();
            throw new InvalidOperationException("The small picture couldn't be captured. It may have closed.", ex);
        }
        catch
        {
            feed.Dispose();
            throw;
        }
    }

    /// <summary>Copies the newest frame into the held texture (not while <paramref name="frozen"/>). True when there's a new one.</summary>
    public bool Pull(ID3D11DeviceContext context, IDirect3DDevice? winrtDevice, bool frozen)
    {
        if (_picture is { } picture)
        {
            // Sent to the GPU once, then let go.
            CreateHeld(picture.Size);
            System.Drawing.Imaging.BitmapData data = picture.LockBits(new Rectangle(System.Drawing.Point.Empty, picture.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                context.UpdateSubresource(_held!, 0, null, data.Scan0, (uint)data.Stride, 0);
            }
            finally
            {
                picture.UnlockBits(data);
            }
            picture.Dispose();
            _picture = null;
            return true;
        }
        if (_cameraStarting is { } starting)
        {
            if (!starting.IsCompleted) return false;
            _cameraStarting = null;
            if (starting.IsCompletedSuccessfully) _camera = starting.Result;
            else
            {
                _cameraProblem = starting.Exception?.GetBaseException() is InvalidOperationException { Message.Length: > 0 } why
                    ? why.Message : "The camera couldn't be started.";
                return true;
            }
        }
        if (_camera is { } camera)
        {
            if (frozen && Bitmap is not null)
            {
                camera.TakeLatest((_, _, _) => { });
                return false;
            }
            return camera.TakeLatest((pixels, stride, size) =>
            {
                if (_held is null || Size != size) CreateHeld(size);
                context.UpdateSubresource<byte>(pixels, _held!, 0, (uint)stride, 0, null);
            });
        }
        if (_pool is null) return false;
        Direct3D11CaptureFrame? latest = null;
        while (_pool.TryGetNextFrame() is { } frame)
        {
            latest?.Dispose();
            latest = frame;
        }
        if (latest is null) return false;
        using (latest)
        {
            SizeInt32 content = latest.ContentSize;
            if ((content.Width != _poolSize.Width || content.Height != _poolSize.Height) && winrtDevice is not null)
            {
                _poolSize = content;
                _pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, content);
            }
            if (frozen && Bitmap is not null || content.Width <= 0 || content.Height <= 0) return false;
            using ID3D11Texture2D texture = Direct3D.TextureOf(latest.Surface);
            var size = new Size(content.Width, content.Height);
            if (_held is null || Size != size) CreateHeld(size);
            context.CopySubresourceRegion(_held!, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, size.Width, size.Height, 1));
            return true;
        }
    }

    private void CreateHeld(Size size)
    {
        Bitmap?.Dispose();
        _held?.Dispose();
        _held = _device.CreateTexture2D(new Texture2DDescription
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
        Bitmap = _dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        Size = size;
    }

    public void Dispose()
    {
        // Still starting: the camera is let go as soon as it has started, so its light goes off.
        _cameraStarting?.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
        }, TaskScheduler.Default);
        _cameraStarting = null;
        _camera?.Dispose();
        _picture?.Dispose();
        _picture = null;
        _session?.Dispose();
        _pool?.Dispose();
        _item = null;
        Bitmap?.Dispose();
        _held?.Dispose();
        Bitmap = null;
        _held = null;
    }
}
