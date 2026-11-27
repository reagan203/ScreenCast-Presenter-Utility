using System.Diagnostics;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The engine's rev 10 extras: the night filter on the picture (FR-123), the small picture's shape
/// (FR-118), a video as the main picture (FR-116), a timer on the TV (FR-120), and a picture file
/// read again in place when it changes (the room sign, FR-125). All on the render thread, apart from
/// the setters, which only update fields under the lock and wake it.
/// </summary>
internal sealed partial class WgcEngine
{
    // Asked for under _sync.
    private NightMode _night;
    private InsetShape _insetShape;
    private bool _pictureCheck;

    // Render thread only.
    private PicturePainter? _painter;
    private VideoFeed? _video;
    private IDirect3DSurface? _heldSurface;
    private DateTime _pictureWritten;

    public void Timer(string? text, InsetCorner corner, bool up) => _effects.Timer(text, corner, up);

    public void Night(NightMode mode) => Update(() => _night = mode);

    public void InsetShape(InsetShape shape) => Update(() => _insetShape = shape);

    public void Video(VideoCommand command, double seconds)
    {
        _video?.Command(command, seconds);
        _wake.Set();
    }

    /// <summary>The video ended ("ended") or couldn't play ("failed …").</summary>
    public event EventHandler<string>? VideoEvent;

    private void ReportVideo(string what)
    {
        if (_ui is { } ui) ui.Post(_ => VideoEvent?.Invoke(this, what), null);
        else VideoEvent?.Invoke(this, what); // the capture helper: no UI thread
    }

    /// <summary>A video file (FR-116): Windows' media player decodes it; frames arrive when it has opened.</summary>
    private void StartVideo(string file)
    {
        _video = VideoFeed.Start(file, sound: true, () => _wake.Set(), ReportVideo);
        lock (_sync)
        {
            _video.SetHeld(_frozen);
            _video.SetVisible(_opacity > 0.001);
        }
    }

    /// <summary>The newest video frame into the held texture (made, the video's size, when it has opened).</summary>
    private bool TakeVideoFrame()
    {
        if (_video is null) return false;
        if (_video.Failure is { } problem)
        {
            if (_heldBitmap is null) throw new InvalidOperationException(problem); // never started: say why
            return false; // it played, then failed: the last picture stays
        }
        Size size = _video.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;
        if (_held is null || _heldSize != size)
        {
            _heldSurface?.Dispose();
            _heldSurface = null;
            CreateHeldTexture(size, drawInto: true);
            _heldSurface = Direct3D.SurfaceOf(_held!);
        }
        return _video.TakeLatest(_heldSurface!);
    }

    /// <summary>Brightness changed: a video plays only while the TV shows it.</summary>
    private void OnOpacityForVideo(double opacity) => _video?.SetVisible(opacity > 0.001);

    /// <summary>A picture file changed on disk (the room sign is made again each minute): read it again, in place, without a fade.</summary>
    private bool ReloadPictureIfChanged()
    {
        bool check;
        lock (_sync)
        {
            check = _pictureCheck;
            _pictureCheck = false;
        }
        if (!check || _source.Picture is not { } file) return false;
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (written == _pictureWritten) return false;
        using System.Drawing.Bitmap? picture = PictureSource.Load(file);
        if (picture is null) return false; // half written or gone: the old picture stays
        _pictureWritten = written;
        if (_held is null || _heldSize != picture.Size) CreateHeldTexture(picture.Size);
        System.Drawing.Imaging.BitmapData data = picture.LockBits(new Rectangle(System.Drawing.Point.Empty, picture.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            _context!.UpdateSubresource(_held!, 0, null, data.Scan0, (uint)data.Stride, 0);
        }
        finally
        {
            picture.UnlockBits(data);
        }
        return true;
    }

    /// <summary>A picture onto the canvas, through the night filter when it's on (FR-123).</summary>
    private void DrawPicture(ID2D1Bitmap1 bitmap, Rectangle part, Rectangle dest, double opacity, NightMode night)
    {
        _painter ??= new PicturePainter(_dc!, _d2dFactory!);
        _painter.Draw(bitmap, part, dest, opacity, night);
    }

    /// <summary>The small picture in its shape: a rectangle, rounded corners, or a circle cut from its middle.</summary>
    private void DrawInsetShaped(ID2D1Bitmap1 bitmap, Rectangle whole, Rectangle part, System.Drawing.SizeF canvas, double opacity, InsetShape shape)
    {
        Rectangle shown = InsetShapes.Shown(part, shape);
        System.Drawing.RectangleF place = InsetLayout.Place(canvas, shown.Size, _insetCorner, _insetPercent);
        if (place.IsEmpty) return;
        _painter ??= new PicturePainter(_dc!, _d2dFactory!);
        _insetBorder ??= _dc!.CreateSolidColorBrush(new Color4(1, 1, 1, 0.35f));
        SourceWindow? small = _insetSource;
        _painter.DrawShaped(bitmap, shown, place, opacity, shape, _insetBorder, Math.Max(1.5f, canvas.Height / 540f),
            small is null ? null : () => DrawCoversOn(small, _insetCover, bitmap, whole, shown, place, opacity));
    }

    /// <summary>The video stops playing (and is silent) at once, and lets go of the engine's texture.</summary>
    private void StopVideoQuickly()
    {
        _video?.Silence();
        _heldSurface?.Dispose();
        _heldSurface = null;
    }

    private void ReleaseExtras()
    {
        _video?.Dispose(); // the sound stops with the picture
        _video = null;
        _heldSurface?.Dispose();
        _heldSurface = null;
        _painter?.Dispose();
        _painter = null;
    }

    /// <summary>The video being played now (tests).</summary>
    internal bool HasVideo => _video is not null;

    [Conditional("DEBUG")]
    private static void Trace(string what) => Debug.WriteLine("engine: " + what);
}
