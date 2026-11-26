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

    /// <summary>Frames are scanned at up to this size: enough for small print on a large screen, bounded in memory.</summary>
    public const int MaxScanSide = 2560;

    private readonly OcrEngine? _ocr;
    private readonly Action<List<CropRegion>, List<SensitiveMatch>> _found;
    private readonly Stopwatch _since = new();
    private Task? _running;
    private byte[]? _pixels;
    private ID2D1Bitmap1? _scanTarget, _scanCpu;
    private Size _scanSize;
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

    /// <summary>Windows has a text recognizer for one of the user's languages.</summary>
    public bool Available => _ocr is not null;

    /// <summary>Something new may be shown: its first scan is done (or can't happen).</summary>
    public bool FirstScanDone => _scannedOnce || !Available || Kinds == SensitiveKinds.None;

    /// <summary>A scan is running on the worker.</summary>
    public bool Busy => _running is { IsCompleted: false };

    /// <summary>Scale for a frame of <paramref name="area"/>: never up, at most <see cref="MaxScanSide"/> a side.</summary>
    public static Size ScanSize(Size area)
    {
        if (area.Width <= 0 || area.Height <= 0) return Size.Empty;
        double scale = Math.Min(1, (double)MaxScanSide / Math.Max(area.Width, area.Height));
        return new Size(Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
    }

    /// <summary>
    /// On the render thread, after a frame is drawn: starts a scan of <paramref name="area"/> of
    /// <paramref name="picture"/> when one is due. <paramref name="restore"/> is put back as the target.
    /// </summary>
    public void MaybeScan(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, Rectangle area, bool changed, ID2D1Image restore)
    {
        if (_ocr is null || Kinds == SensitiveKinds.None || Busy || area.Width <= 0 || area.Height <= 0) return;
        if (_scannedOnce && (!changed || _since.Elapsed < ScanEvery)) return;
        Size size = ScanSize(area.Size);
        try
        {
            EnsureBuffers(dc, size);
            dc.Target = _scanTarget;
            dc.BeginDraw();
            dc.DrawBitmap(picture, new Rect(0, 0, size.Width, size.Height), 1f, InterpolationMode.HighQualityCubic, new Rect(area.X, area.Y, area.Width, area.Height), null);
            dc.EndDraw().CheckError();
        }
        finally
        {
            dc.Target = restore;
        }
        _scanCpu!.CopyFromBitmap(_scanTarget!);
        MappedRectangle map = _scanCpu.Map(MapOptions.Read);
        try
        {
            int row = size.Width * 4;
            for (int y = 0; y < size.Height; y++) Marshal.Copy(map.Bits + (nint)(y * map.Pitch), _pixels!, y * row, row);
        }
        finally
        {
            _scanCpu.Unmap();
        }
        _since.Restart();
        SensitiveKinds kinds = Kinds;
        byte[] pixels = _pixels!;
        _running = Task.Run(() => Recognize(pixels, size, kinds));
    }

    private void EnsureBuffers(ID2D1DeviceContext dc, Size size)
    {
        if (_scanTarget is not null && _scanSize == size) return;
        _scanTarget?.Dispose();
        _scanCpu?.Dispose();
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);
        _scanTarget = dc.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        _scanCpu = dc.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        if (_pixels is null || _pixels.Length < size.Width * size.Height * 4) _pixels = new byte[size.Width * size.Height * 4];
        _scanSize = size;
    }

    private void Recognize(byte[] pixels, Size size, SensitiveKinds kinds)
    {
        try
        {
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(0, size.Width * size.Height * 4), BitmapPixelFormat.Bgra8, size.Width, size.Height, BitmapAlphaMode.Premultiplied);
            OcrResult result = _ocr!.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            List<OcrLine> lines = Lines(result);
            List<SensitiveMatch> found = SensitiveText.Find(lines, kinds);
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
        _scanTarget?.Dispose();
        _scanCpu?.Dispose();
        _scanTarget = _scanCpu = null;
        _pixels = null;
    }
}
