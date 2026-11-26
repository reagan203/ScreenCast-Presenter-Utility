using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ScreenCastPresenter.Core;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using OcrResult = Windows.Media.Ocr.OcrResult;
using OcrLine = ScreenCastPresenter.Core.OcrLine;
using OcrWord = ScreenCastPresenter.Core.OcrWord;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Automatic hiding (FR-88), in the capture helper: every <see cref="ScanEvery"/>, while it's on
/// and only when the picture changed, one frame of what's shown is scaled on the GPU, read back
/// once, and read by Windows' own text recognizer on a worker thread, on the PC; what
/// <see cref="SensitiveText"/> finds is covered on the TV. The buffers are kept and reused, so
/// scanning adds no growing memory. It's a safety net a moment behind what's shown: hidden parts
/// (FR-74) and the never-show list (FR-62) are the guarantees.
/// </summary>
internal sealed class AutoRedactor : IDisposable
{
    public static readonly TimeSpan ScanEvery = TimeSpan.FromMilliseconds(400);

    /// <summary>The first scan of something new may hold the picture black this long at most.</summary>
    public static readonly TimeSpan FirstScanLimit = TimeSpan.FromSeconds(2);

    /// <summary>Frames are scanned at up to this size: enough for small print on a large screen, bounded in memory (and within what the recognizer takes).</summary>
    public const int MaxScanSide = 2560;

    /// <summary>The largest side the recognizer is given: ours, or Windows' own limit if that's smaller.</summary>
    public static int ScanLimit => (int)Math.Min(MaxScanSide, OcrEngine.MaxImageDimension);

    private readonly OcrEngine? _ocr;
    private readonly Action<List<CropRegion>, List<SensitiveMatch>> _found;
    private readonly Stopwatch _since = new();
    private Task? _running;
    private readonly FrameGrabber _grabber = new(ScanLimit);
    private volatile bool _scannedOnce;

    /// <param name="found">Called on a worker thread with the boxes (fractions of the content) and what they cover.</param>
    public AutoRedactor(SensitiveKinds kinds, Action<List<CropRegion>, List<SensitiveMatch>> found)
    {
        Kinds = kinds;
        _found = found;
        try
        {
            _ocr = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
    }

    public SensitiveKinds Kinds { get; set; }

    /// <summary>The presenter's own words (<see cref="SensitiveText.CustomPattern"/>), or null.</summary>
    public System.Text.RegularExpressions.Regex? Words { get; set; }

    /// <summary>Windows has a text recognizer for one of the user's languages.</summary>
    public bool Available => _ocr is not null;

    /// <summary>Something new may be shown: its first scan is done (or can't happen).</summary>
    public bool FirstScanDone => _scannedOnce || !Available || (Kinds & SensitiveKinds.TextKinds) == SensitiveKinds.None;

    /// <summary>A scan is running on the worker.</summary>
    public bool Busy => _running is { IsCompleted: false };

    /// <summary>Scale for a frame of <paramref name="area"/>: never up, at most <see cref="MaxScanSide"/> a side.</summary>
    public static Size ScanSize(Size area) => FrameGrabber.ScanSize(area, ScanLimit);

    /// <summary>
    /// On the render thread, after a frame is drawn: starts a scan of <paramref name="area"/> of
    /// <paramref name="picture"/> when one is due. <paramref name="restore"/> is put back as the target.
    /// </summary>
    public void MaybeScan(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, Rectangle area, bool changed, ID2D1Image? restore)
    {
        if (_ocr is null || (Kinds & SensitiveKinds.TextKinds) == SensitiveKinds.None || Busy || area.Width <= 0 || area.Height <= 0) return;
        if (_scannedOnce && (!changed || _since.Elapsed < ScanEvery)) return;
        Size size = _grabber.Grab(dc, picture, area, restore);
        if (size.IsEmpty) return;
        _since.Restart();
        SensitiveKinds kinds = Kinds;
        System.Text.RegularExpressions.Regex? words = Words;
        byte[] pixels = _grabber.Pixels!;
        _running = Task.Run(() => Recognize(pixels, size, kinds, words));
    }

    private void Recognize(byte[] pixels, Size size, SensitiveKinds kinds, System.Text.RegularExpressions.Regex? words)
    {
        try
        {
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(0, size.Width * size.Height * 4), BitmapPixelFormat.Bgra8, size.Width, size.Height, BitmapAlphaMode.Premultiplied);
            OcrResult result = _ocr!.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            List<OcrLine> lines = Lines(result);
            List<SensitiveMatch> found = SensitiveText.Find(lines, kinds, words);
            List<CropRegion> regions = SensitiveText.ToRegions(found, new SizeF(size.Width, size.Height));
            _scannedOnce = true; // before telling anyone: what's covered is in place when they look
            _found(regions, found);
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

    /// <summary>The recognizer's lines as plain records (text and boxes in the scanned picture's pixels).</summary>
    internal static List<OcrLine> Lines(OcrResult result) =>
        [.. result.Lines.Select(line => new OcrLine([.. line.Words.Select(w => new OcrWord(w.Text, new RectangleF((float)w.BoundingRect.X, (float)w.BoundingRect.Y, (float)w.BoundingRect.Width, (float)w.BoundingRect.Height)))]))];

    /// <summary>Reads text in a picture (for tests and the still check): BGRA pixels, rows of width × 4 bytes.</summary>
    internal static async Task<List<OcrLine>?> ReadAsync(byte[] bgra, int width, int height)
    {
        OcrEngine? ocr = OcrEngine.TryCreateFromUserProfileLanguages();
        if (ocr is null) return null;
        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(0, width * height * 4), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        return Lines(await ocr.RecognizeAsync(bitmap));
    }

    /// <summary>
    /// A still picture (a screenshot, FR-51): what <paramref name="kinds"/> finds in its text is
    /// covered with solid boxes, and faces (when chosen) are pixelated. Scaled down first when it's
    /// bigger than the recognizer takes. Returns how many were covered (0 when nothing was, or
    /// Windows can't read text or find faces here). Password boxes are covered by the caller, which
    /// knows the window.
    /// </summary>
    internal static int CoverInStill(Bitmap image, SensitiveKinds kinds, System.Text.RegularExpressions.Regex? words = null)
    {
        List<SensitiveMatch> found = FindInStill(image, kinds, words);
        if (found.Count == 0) return 0;
        using var brush = new SolidBrush(EffectStyle.RedactFill);
        foreach (SensitiveMatch m in found)
        {
            if (m.Kind == SensitiveKinds.Faces) Pixelate(image, Rectangle.Round(m.Box));
            else
            {
                using Graphics cover = Graphics.FromImage(image);
                cover.FillRectangle(brush, m.Box);
            }
        }
        return found.Count;
    }

    /// <summary>
    /// What <paramref name="kinds"/> finds in a still picture, in its own pixels: text read by the
    /// recognizer, and faces (padded, the largest left out when asked). Scaled down first when it's
    /// bigger than the recognizer takes. Password boxes need the window: the caller asks Windows.
    /// </summary>
    internal static List<SensitiveMatch> FindInStill(Bitmap image, SensitiveKinds kinds, System.Text.RegularExpressions.Regex? words = null)
    {
        var found = new List<SensitiveMatch>();
        bool text = (kinds & SensitiveKinds.TextKinds) != 0, faces = kinds.HasFlag(SensitiveKinds.Faces);
        if (!text && !faces) return found;
        double scale = Math.Min(1, (double)ScanLimit / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        using var scan = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(scan))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, new Rectangle(0, 0, w, h));
        }
        System.Drawing.Imaging.BitmapData data = scan.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var bgra = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
        }
        finally
        {
            scan.UnlockBits(data);
        }
        if (faces && FaceFinder.DetectAsync(bgra, w, h).GetAwaiter().GetResult() is { } people)
            foreach (CropRegion r in FaceFinder.ToRegions(people, new SizeF(w, h), kinds.HasFlag(SensitiveKinds.KeepLargestFace), FaceFinder.Padding))
                found.Add(new SensitiveMatch(SensitiveKinds.Faces, new RectangleF((float)(r.X * image.Width), (float)(r.Y * image.Height), (float)(r.Width * image.Width), (float)(r.Height * image.Height))));
        if (text && ReadAsync(bgra, w, h).GetAwaiter().GetResult() is { } lines)
            foreach (SensitiveMatch m in SensitiveText.Find(lines, kinds, words))
                found.Add(m with { Box = RectangleF.FromLTRB(m.Box.Left / (float)scale, m.Box.Top / (float)scale, m.Box.Right / (float)scale, m.Box.Bottom / (float)scale) });
        return found;
    }

    /// <summary>Blocks of <see cref="PixelBlocks"/> across: nobody can be recognised, the picture still reads as a person.</summary>
    public const int PixelBlocks = 8;

    /// <summary>Pixelates <paramref name="area"/> of <paramref name="image"/> in place (a face in a screenshot).</summary>
    internal static void Pixelate(Bitmap image, Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        if (area.Width <= 0 || area.Height <= 0) return;
        int bw = Math.Max(1, Math.Min(PixelBlocks, area.Width)), bh = Math.Max(1, (int)Math.Round(bw * (double)area.Height / area.Width));
        using var tiny = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(tiny))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(image, new Rectangle(0, 0, bw, bh), area, GraphicsUnit.Pixel);
        }
        using Graphics back = Graphics.FromImage(image);
        back.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        back.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        back.DrawImage(tiny, area, new Rectangle(0, 0, bw, bh), GraphicsUnit.Pixel);
    }

    /// <summary>On the render thread: waits for a running scan, then lets go of the buffers.</summary>
    public void Dispose()
    {
        try
        {
            _running?.Wait(FirstScanLimit); // a scan never takes longer than the first one may
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine(ex);
        }
        _grabber.Dispose();
    }
}
