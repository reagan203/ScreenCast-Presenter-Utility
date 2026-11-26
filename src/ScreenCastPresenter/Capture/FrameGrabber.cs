using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// One frame of what's shown, scaled on the GPU and read back once into a buffer that's kept and
/// reused: what automatic hiding reads (text, faces). On the render thread. No growing memory.
/// </summary>
internal sealed class FrameGrabber(int maxSide) : IDisposable
{
    private ID2D1Bitmap1? _target, _cpu;
    private Size _size;

    /// <summary>BGRA pixels of the last grab, rows of width × 4 bytes (reused: copy what must outlive the next grab).</summary>
    public byte[]? Pixels { get; private set; }

    public int MaxSide { get; } = maxSide;

    /// <summary>Scale for <paramref name="area"/>: never up, at most <paramref name="maxSide"/> a side.</summary>
    public static Size ScanSize(Size area, int maxSide)
    {
        if (area.Width <= 0 || area.Height <= 0) return Size.Empty;
        double scale = Math.Min(1, (double)maxSide / Math.Max(area.Width, area.Height));
        return new Size(Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
    }

    /// <summary>
    /// Draws <paramref name="area"/> of <paramref name="picture"/> scaled into the buffer; the size
    /// it ended up. <paramref name="restore"/> is put back as the target.
    /// </summary>
    public Size Grab(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, Rectangle area, ID2D1Image? restore)
    {
        Size size = ScanSize(area.Size, MaxSide);
        if (size.IsEmpty) return size;
        EnsureBuffers(dc, size);
        try
        {
            dc.Target = _target;
            dc.BeginDraw();
            dc.DrawBitmap(picture, new Rect(0, 0, size.Width, size.Height), 1f, InterpolationMode.HighQualityCubic, new Rect(area.X, area.Y, area.Width, area.Height), null);
            dc.EndDraw().CheckError();
        }
        finally
        {
            dc.Target = restore;
        }
        _cpu!.CopyFromBitmap(_target!);
        MappedRectangle map = _cpu.Map(MapOptions.Read);
        try
        {
            int row = size.Width * 4;
            for (int y = 0; y < size.Height; y++) Marshal.Copy(map.Bits + (nint)(y * map.Pitch), Pixels!, y * row, row);
        }
        finally
        {
            _cpu.Unmap();
        }
        return size;
    }

    private void EnsureBuffers(ID2D1DeviceContext dc, Size size)
    {
        if (_target is not null && _size == size) return;
        _target?.Dispose();
        _cpu?.Dispose();
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);
        _target = dc.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        _cpu = dc.CreateBitmap(new SizeI(size.Width, size.Height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        if (Pixels is null || Pixels.Length < size.Width * size.Height * 4) Pixels = new byte[size.Width * size.Height * 4];
        _size = size;
    }

    /// <summary>On the render thread.</summary>
    public void Dispose()
    {
        _target?.Dispose();
        _cpu?.Dispose();
        _target = _cpu = null;
        Pixels = null;
    }
}
