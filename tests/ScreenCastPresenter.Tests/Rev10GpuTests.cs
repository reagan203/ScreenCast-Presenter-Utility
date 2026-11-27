using System.Runtime.InteropServices;
using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// Rev 10 on the real GPU, pixel by pixel (drawn offscreen and read back): the night filter on a
/// white page and on a fade (FR-123), the small picture cut round (FR-118), and the timer on the TV,
/// shown with the picture and never on plain black (FR-120).
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed class Rev10GpuTests : IDisposable
{
    private const int W = 640, H = 360;

    private readonly ID3D11Device? _device;
    private readonly ID2D1Factory1? _factory;
    private readonly ID2D1Device? _d2d;
    private readonly ID2D1DeviceContext? _dc;
    private readonly IDWriteFactory? _dwrite;
    private readonly ID2D1Bitmap1? _target;

    public Rev10GpuTests()
    {
        try
        {
            _device = Direct3D.CreateDevice();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return; // no GPU here
        }
        using IDXGIDevice dxgi = _device.QueryInterface<IDXGIDevice>();
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _d2d = _factory.CreateDevice(dxgi);
        _dc = _d2d.CreateDeviceContext(DeviceContextOptions.None);
        _dc.SetDpi(96, 96);
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _target = _dc.CreateBitmap(new SizeI(W, H), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        _dc.Target = _target;
    }

    private bool HasGpu => _dc is not null;

    private ID2D1Bitmap1 Picture(Func<int, int, Color> at)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            Color c = at(x, y);
            int i = (y * W + x) * 4;
            pixels[i] = c.B;
            pixels[i + 1] = c.G;
            pixels[i + 2] = c.R;
            pixels[i + 3] = 255;
        }
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return _dc!.CreateBitmap(new SizeI(W, H), pin.AddrOfPinnedObject(), W * 4,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        }
        finally
        {
            pin.Free();
        }
    }

    private byte[] Draw(Action draw)
    {
        Color black = UI.Theme.OutputBlack;
        _dc!.BeginDraw();
        _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));
        draw();
        _dc.EndDraw().CheckError();
        using ID2D1Bitmap1 cpu = _dc.CreateBitmap(new SizeI(W, H), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        cpu.CopyFromBitmap(_target!);
        MappedRectangle mapped = cpu.Map(MapOptions.Read);
        try
        {
            var result = new byte[W * H * 4];
            for (int y = 0; y < H; y++) Marshal.Copy(mapped.Bits + (nint)(y * mapped.Pitch), result, y * W * 4, W * 4);
            return result;
        }
        finally
        {
            cpu.Unmap();
        }
    }

    private static Color At(byte[] px, int x, int y)
    {
        int i = (y * W + x) * 4;
        return Color.FromArgb(px[i + 2], px[i + 1], px[i]);
    }

    private static int Bright(Color c) => (c.R + c.G + c.B) / 3;

    [Fact]
    public void The_night_filter_turns_a_white_page_dark_and_keeps_dark_text_readable()
    {
        if (!HasGpu) return;
        using ID2D1Bitmap1 page = Picture((x, y) => x is > 200 and < 440 && y is > 160 and < 200 ? Color.Black : Color.White); // a black line of "text" on white
        using var painter = new PicturePainter(_dc!, _factory!);
        var whole = new Rectangle(0, 0, W, H);

        byte[] off = Draw(() => painter.Draw(page, whole, whole, 1, NightMode.Off));
        Assert.True(Bright(At(off, 20, 20)) > 240, "off: the page is white");

        byte[] invert = Draw(() => painter.Draw(page, whole, whole, 1, NightMode.Invert));
        Assert.True(Bright(At(invert, 20, 20)) < 15, $"inverted: the page is dark ({At(invert, 20, 20)})");
        Assert.True(Bright(At(invert, 320, 180)) > 240, $"inverted: the text is light ({At(invert, 320, 180)})");

        byte[] soft = Draw(() => painter.Draw(page, whole, whole, 1, NightMode.Soften));
        Color paper = At(soft, 20, 20);
        Assert.InRange(Bright(paper), 150, 205);
        Assert.True(paper.B < paper.R, $"softened: warm grey, not blue-white ({paper})");
        Assert.True(Bright(At(soft, 320, 180)) < 20, "softened: dark text stays dark");
    }

    [Fact]
    public void The_night_filter_fades_through_black_with_no_flash()
    {
        if (!HasGpu) return;
        using ID2D1Bitmap1 black = Picture((_, _) => Color.Black);
        using var painter = new PicturePainter(_dc!, _factory!);
        var whole = new Rectangle(0, 0, W, H);
        // An inverted black page is white: at every point of a fade in it's no brighter than the fade says.
        foreach (double opacity in new[] { 0.0, 0.1, 0.5, 1.0 })
        {
            byte[] px = Draw(() => painter.Draw(black, whole, whole, opacity, NightMode.Invert));
            Assert.InRange(Bright(At(px, 100, 100)), 0, (int)(255 * opacity) + 12);
        }
        byte[] none = Draw(() => painter.Draw(black, whole, whole, 0, NightMode.Invert));
        Assert.True(Bright(At(none, 100, 100)) < 16, "faded out: black");
    }

    [Fact]
    public void The_small_picture_is_cut_round()
    {
        if (!HasGpu) return;
        using ID2D1Bitmap1 face = Picture((_, _) => Color.FromArgb(230, 200, 40));
        using var painter = new PicturePainter(_dc!, _factory!);
        using ID2D1SolidColorBrush edge = _dc!.CreateSolidColorBrush(new Color4(1, 1, 1, 0.35f));
        var place = new System.Drawing.RectangleF(100, 60, 240, 240);
        Rectangle shown = InsetShapes.Shown(new Rectangle(0, 0, W, H), InsetShape.Circle);
        int covered = 0;
        byte[] circle = Draw(() => painter.DrawShaped(face, shown, place, 1, InsetShape.Circle, edge, 1.5f, () => covered++));
        Assert.Equal(1, covered); // its covers are drawn, inside the shape
        Color centre = At(circle, 220, 180), corner = At(circle, 104, 64);
        Assert.True(centre.R > 200 && centre.G > 170, $"the middle shows the picture ({centre})");
        Assert.True(Bright(corner) < 20, $"the corner of its square is cut away ({corner})");

        byte[] square = Draw(() => painter.DrawShaped(face, shown, place, 1, InsetShape.Rectangle, edge, 1.5f, null));
        Assert.True(At(square, 104, 64).R > 200, "a rectangle keeps its corners");

        byte[] rounded = Draw(() => painter.DrawShaped(face, shown, place, 1, InsetShape.Rounded, edge, 1.5f, null));
        Assert.True(Bright(At(rounded, 101, 61)) < 40, "rounded: the very corner is cut");
        Assert.True(At(rounded, 104, 180).R > 200, "rounded: the edge's middle shows the picture");
    }

    [Fact]
    public void The_timer_shows_with_the_picture_and_never_on_plain_black()
    {
        if (!HasGpu) return;
        using ID2D1Bitmap1 slide = Picture((_, _) => Color.FromArgb(40, 60, 120));
        using var layer = new EffectsLayer(() => { });
        layer.Timer("Exercise 4:59", InsetCorner.TopLeft, up: false);
        Assert.True(layer.HasOverlay);
        var canvas = new System.Drawing.SizeF(W, H);
        System.Drawing.RectangleF chip = ActivityTimer.Chip(canvas, InsetCorner.TopLeft, 200, H);

        byte[] shown = Draw(() =>
        {
            _dc!.DrawBitmap(slide, new Rect(0, 0, W, H), 1, InterpolationMode.NearestNeighbor, new Rect(0, 0, W, H), null);
            layer.RenderOverlay(_dc, _factory!, _dwrite!, canvas, 1, 1);
        });
        int light = 0;
        for (int y = (int)chip.Top; y < (int)chip.Bottom; y++)
        for (int x = (int)chip.Left; x < (int)chip.Right; x++)
            if (Bright(At(shown, x, y)) > 200) light++;
        Assert.True(light > 30, $"its words are drawn in the corner ({light} light pixels)");
        Assert.Equal(At(shown, W - 10, H - 10).ToArgb(), Color.FromArgb(40, 60, 120).ToArgb()); // nothing elsewhere

        byte[] black = Draw(() => layer.RenderOverlay(_dc!, _factory!, _dwrite!, canvas, 0, 0));
        for (int y = 0; y < H; y += 7)
        for (int x = 0; x < W; x += 7)
            Assert.True(Bright(At(black, x, y)) < 20, "plain black stays black");

        layer.Timer(null, InsetCorner.TopLeft, false);
        Assert.False(layer.HasOverlay);
    }

    public void Dispose()
    {
        _target?.Dispose();
        _dwrite?.Dispose();
        _dc?.Dispose();
        _d2d?.Dispose();
        _factory?.Dispose();
        _device?.Dispose();
    }
}
