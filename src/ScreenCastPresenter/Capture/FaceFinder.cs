using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ScreenCastPresenter.Core;
using Vortice.Direct2D1;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Faces, blurred (FR-100): Windows' own face detection (<c>Windows.Media.FaceAnalysis</c>), on
/// the PC, on a small copy of what's shown every <see cref="ScanEvery"/> while the picture changes.
/// Faces move, so each box is grown generously. Optionally the largest face, the presenter at their
/// own camera, stays clear. Nothing is recognised or kept: only where faces are, for this frame.
/// </summary>
internal sealed class FaceFinder : IDisposable
{
    public static readonly TimeSpan ScanEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>Faces are looked for at up to this size: small faces at the back of a room still count, and it stays quick.</summary>
    public const int MaxScanSide = 960;

    /// <summary>Each face's box grows by this part of its size on every side (more above, for hair).</summary>
    public const float Padding = 0.3f;

    public const int MaxFaces = 24;

    /// <summary>Faces this small (in the scanned picture) are still found.</summary>
    public const int SmallestFace = 20;

    private readonly FaceDetector? _detector;
    private readonly FrameGrabber _grabber = new(MaxScanSide);
    private readonly Stopwatch _since = new();
    private readonly Action<List<CropRegion>> _found;
    private Task? _running;
    private volatile bool _scannedOnce;

    /// <param name="found">Called on a worker with the faces to blur (parts of the content).</param>
    public FaceFinder(bool keepLargest, Action<List<CropRegion>> found)
    {
        KeepLargest = keepLargest;
        _found = found;
        _detector = CreateDetector();
    }

    public bool KeepLargest { get; set; }

    /// <summary>Windows can find faces on this PC.</summary>
    public bool Available => _detector is not null;

    public bool FirstScanDone => _scannedOnce || !Available;

    public bool Busy => _running is { IsCompleted: false };

    /// <summary>Whether Windows can find faces here (for Settings to say so).</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return FaceDetector.IsSupported;
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static FaceDetector? CreateDetector()
    {
        try
        {
            if (!FaceDetector.IsSupported) return null;
            FaceDetector detector = FaceDetector.CreateAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                detector.MinDetectableFaceSize = new BitmapSize { Width = SmallestFace, Height = SmallestFace };
            }
            catch (ArgumentException)
            {
                // below what this PC's detector takes: its own smallest
            }
            return detector;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or TypeLoadException)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>On the render thread, after a frame is drawn: a scan when one is due.</summary>
    public void MaybeScan(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, Rectangle area, bool changed, ID2D1Image? restore)
    {
        if (_detector is null || Busy || area.Width <= 0 || area.Height <= 0) return;
        if (_scannedOnce && (!changed || _since.Elapsed < ScanEvery)) return;
        Size size = _grabber.Grab(dc, picture, area, restore);
        if (size.IsEmpty) return;
        _since.Restart();
        byte[] pixels = _grabber.Pixels!;
        bool keep = KeepLargest;
        _running = Task.Run(() => Detect(pixels, size, keep));
    }

    private void Detect(byte[] pixels, Size size, bool keepLargest)
    {
        try
        {
            List<RectangleF> faces = DetectIn(_detector!, pixels, size.Width, size.Height).GetAwaiter().GetResult();
            List<CropRegion> regions = ToRegions(faces, new SizeF(size.Width, size.Height), keepLargest, Padding);
            _scannedOnce = true; // before telling anyone: what's covered is in place when they look
            _found(regions);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex); // a frame that couldn't be read: the next one will be
        }
        finally
        {
            _scannedOnce = true;
        }
    }

    /// <summary>Faces in a BGRA picture (rows of width × 4 bytes), in its pixels.</summary>
    internal static async Task<List<RectangleF>> DetectIn(FaceDetector detector, byte[] bgra, int width, int height)
    {
        using SoftwareBitmap color = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(0, width * height * 4), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        using SoftwareBitmap gray = SoftwareBitmap.Convert(color, BitmapPixelFormat.Gray8);
        IList<DetectedFace> faces = await detector.DetectFacesAsync(gray);
        return [.. faces.Select(f => new RectangleF(f.FaceBox.X, f.FaceBox.Y, f.FaceBox.Width, f.FaceBox.Height))];
    }

    /// <summary>Faces in a picture with a detector of its own (a screenshot, tests); null when Windows can't find faces here.</summary>
    internal static async Task<List<RectangleF>?> DetectAsync(byte[] bgra, int width, int height)
    {
        if (!IsSupported) return null;
        FaceDetector detector = await FaceDetector.CreateAsync();
        return await DetectIn(detector, bgra, width, height);
    }

    /// <summary>
    /// The faces to blur, as parts of a picture of <paramref name="size"/>: each grown by
    /// <paramref name="padding"/> of its size (half again above), clipped, at most <see cref="MaxFaces"/>.
    /// With <paramref name="keepLargest"/>, the one largest face is left out.
    /// </summary>
    public static List<CropRegion> ToRegions(IReadOnlyList<RectangleF> faces, SizeF size, bool keepLargest, float padding)
    {
        var regions = new List<CropRegion>();
        if (size.Width <= 0 || size.Height <= 0) return regions;
        int skip = -1;
        if (keepLargest && faces.Count > 0)
        {
            float largest = -1;
            for (int i = 0; i < faces.Count; i++)
            {
                float area = faces[i].Width * faces[i].Height;
                if (area > largest)
                {
                    largest = area;
                    skip = i;
                }
            }
        }
        for (int i = 0; i < faces.Count && regions.Count < MaxFaces; i++)
        {
            if (i == skip) continue;
            RectangleF f = faces[i];
            if (f.Width <= 0 || f.Height <= 0) continue;
            float dx = f.Width * padding, dy = f.Height * padding;
            var grown = RectangleF.FromLTRB(f.Left - dx, f.Top - dy * 1.5f, f.Right + dx, f.Bottom + dy);
            if (CropRegion.Create(grown.X / size.Width, grown.Y / size.Height, grown.Width / size.Width, grown.Height / size.Height, 0.0005) is { } region)
                regions.Add(region);
        }
        return regions;
    }

    /// <summary>On the render thread: waits for a running scan, then lets go of the buffers.</summary>
    public void Dispose()
    {
        try
        {
            _running?.Wait(AutoRedactor.FirstScanLimit);
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine(ex);
        }
        _grabber.Dispose();
    }
}
