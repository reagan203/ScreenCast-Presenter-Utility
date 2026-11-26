using System.Diagnostics;
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
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// What the TV actually shows, pixel by pixel: the marks layer drawn on the real GPU into an
/// offscreen picture and read back. Hidden parts must leave nothing of what's under them (not even
/// during a fade), the spotlight must dim around the pointer, and the QR code on the TV must decode.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed class GpuStageRenderTests : IDisposable
{
    private const int W = 640, H = 360;
    private static readonly System.Drawing.RectangleF Whole = new(0, 0, W, H);

    private readonly ID3D11Device? _device;
    private readonly ID2D1Factory1? _factory;
    private readonly ID2D1Device? _d2d;
    private readonly ID2D1DeviceContext? _dc;
    private readonly IDWriteFactory? _dwrite;
    private readonly ID2D1Bitmap1? _target;

    public GpuStageRenderTests()
    {
        try
        {
            _device = Direct3D.CreateDevice();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return; // no GPU here: nothing to check
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

    /// <summary>A picture of solid <paramref name="colour"/>, or a 2-pixel checkerboard of white and black.</summary>
    private ID2D1Bitmap1 Picture(Color? colour)
    {
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            Color c = colour ?? ((x / 2 + y / 2) % 2 == 0 ? Color.White : Color.Black);
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

    /// <summary>One frame as the helper draws it: black, the picture at its brightness, the marks, the overlay.</summary>
    private byte[] Frame(EffectsLayer layer, ID2D1Bitmap1 picture, float opacity = 1, bool frozen = false, float visibility = -1, float pictureShows = -1)
    {
        Color black = UI.Theme.OutputBlack;
        _dc!.BeginDraw();
        _dc.Clear(new Color4(black.R / 255f, black.G / 255f, black.B / 255f, 1));
        _dc.DrawBitmap(picture, new Rect(0, 0, W, H), opacity, InterpolationMode.NearestNeighbor, new Rect(0, 0, W, H), null);
        layer.Render(_dc, _factory!, _dwrite!, picture, Whole, Whole, Whole, opacity, frozen);
        layer.RenderOverlay(_dc, _factory!, _dwrite!, new System.Drawing.SizeF(W, H), visibility < 0 ? opacity : visibility, pictureShows < 0 ? opacity : pictureShows);
        _dc.EndDraw().CheckError();
        return ReadBack();
    }

    private byte[] ReadBack()
    {
        using ID2D1Bitmap1 cpu = _dc!.CreateBitmap(new SizeI(W, H), IntPtr.Zero, 0,
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

    private static void Near(Color expected, Color actual, int tolerance, string what)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"{what}: expected {expected}, got {actual}");
    }

    private static (double Mean, double Spread) Stats(byte[] px, Rectangle r)
    {
        var values = new List<double>();
        for (int y = r.Top; y < r.Bottom; y++)
        for (int x = r.Left; x < r.Right; x++)
        {
            Color c = At(px, x, y);
            values.Add((c.R + c.G + c.B) / 3.0);
        }
        double mean = values.Average();
        return (mean, Math.Sqrt(values.Average(v => (v - mean) * (v - mean))));
    }

    private static readonly CropRegion Box = CropRegion.Create(0.25, 0.25, 0.5, 0.5)!; // 160..480 × 90..270
    private static readonly Rectangle Inside = new(200, 120, 240, 120);

    [Fact]
    public void A_solid_hidden_part_leaves_nothing_of_whats_under_it_even_mid_fade()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Redact([Box], RedactStyle.Solid);
        using ID2D1Bitmap1 white = Picture(Color.White), red = Picture(Color.Red);
        foreach (float opacity in new[] { 1f, 0.5f, 0.1f })
        {
            byte[] a = Frame(layer, white, opacity), b = Frame(layer, red, opacity);
            Color expected = EffectStyle.SolidOver(UI.Theme.OutputBlack, EffectStyle.RedactFill, opacity);
            foreach (Point p in new[] { new Point(170, 100), new Point(320, 180), new Point(470, 260) })
            {
                Near(expected, At(a, p.X, p.Y), 2, $"over white at {opacity}");
                Assert.Equal(At(a, p.X, p.Y), At(b, p.X, p.Y)); // identical whatever is under it
            }
            if (opacity == 1) Near(Color.White, At(a, 20, 20), 1, "outside the box the picture shows");
        }
        layer.Dispose();
    }

    [Fact]
    public void A_blurred_hidden_part_keeps_the_shape_but_nothing_readable()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Redact([Box], RedactStyle.Blurred);
        using ID2D1Bitmap1 checker = Picture(null);
        byte[] frame = Frame(layer, checker);
        (double insideMean, double insideSpread) = Stats(frame, Inside);
        (double _, double outsideSpread) = Stats(frame, new Rectangle(10, 10, 100, 60));
        Assert.True(outsideSpread > 100, $"the sharp checkerboard outside: spread {outsideSpread:F0}");
        Assert.True(insideSpread < 8, $"inside it's smooth: spread {insideSpread:F1}");
        Assert.InRange(insideMean, 40, 110); // a darkened mid grey: the page's overall tone, nothing more

        // While fading it's the solid box: a blurred copy would be brighter than the fading picture.
        byte[] fading = Frame(layer, checker, 0.5f);
        Near(EffectStyle.SolidOver(UI.Theme.OutputBlack, EffectStyle.RedactFill, 0.5f), At(fading, 320, 180), 2, "mid-fade");
        layer.Dispose();
    }

    [Fact]
    public void Hidden_parts_stay_while_frozen_but_the_spotlight_doesnt_follow_the_pointer()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Redact([Box], RedactStyle.Solid);
        layer.Spotlight(new PointF(0.1f, 0.1f));
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] frozen = Frame(layer, white, frozen: true);
        Near(Color.FromArgb(255, EffectStyle.RedactFill), At(frozen, 320, 180), 2, "still covered when frozen");
        Near(Color.White, At(frozen, 600, 340), 1, "no spotlight dimming when frozen");
        layer.Dispose();
    }

    [Fact]
    public void The_spotlight_dims_everything_but_a_circle_round_the_pointer()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Spotlight(new PointF(0.5f, 0.5f));
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] frame = Frame(layer, white);
        Near(Color.White, At(frame, 320, 180), 2, "lit at the pointer");
        Color corner = At(frame, 5, 5);
        double expected = 255 * (1 - EffectStyle.SpotlightDim);
        Assert.InRange(corner.R, expected - 12, expected + 12);
        layer.Spotlight(null);
        Near(Color.White, At(Frame(layer, white), 5, 5), 1, "off again");
        layer.Dispose();
    }

    [Fact]
    public void Arrows_boxes_and_text_draw_where_they_were_put()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        var box = new InkStroke(1, InkTool.Box, Color.Red.ToArgb(), 0.01f);
        layer.InkStart(box);
        layer.InkAdd(1, [new PointF(0.1f, 0.1f)]);
        layer.InkSet(1, [new PointF(0.1f, 0.1f), new PointF(0.4f, 0.5f)]); // dragged to here
        var arrow = new InkStroke(2, InkTool.Arrow, Color.Blue.ToArgb(), 0.01f);
        layer.InkStart(arrow);
        layer.InkAdd(2, [new PointF(0.6f, 0.8f), new PointF(0.9f, 0.8f)]);
        var text = new InkStroke(3, InkTool.Text, Color.Black.ToArgb(), 0.08f) { Text = "HELLO" };
        layer.InkStart(text);
        layer.InkAdd(3, [new PointF(0.55f, 0.1f)]);
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] frame = Frame(layer, white);
        Color edge = At(frame, (int)(0.1f * W), (int)(0.3f * H)); // on the box's left side
        Assert.True(edge.R > 200 && edge.G < 80 && edge.B < 80, $"box edge is red: {edge}");
        Near(Color.White, At(frame, (int)(0.25f * W), (int)(0.3f * H)), 1, "inside the box is untouched");
        Color shaft = At(frame, (int)(0.75f * W), (int)(0.8f * H));
        Assert.True(shaft.B > 200 && shaft.R < 80, $"arrow shaft is blue: {shaft}");
        (double textMean, _) = Stats(frame, new Rectangle((int)(0.55f * W), (int)(0.1f * H), 120, 30));
        Assert.True(textMean < 230, "the words are drawn");
        layer.InkRemove(3);
        (double cleared, _) = Stats(Frame(layer, white), new Rectangle((int)(0.55f * W), (int)(0.1f * H), 120, 30));
        Assert.True(cleared > 254, "and removed");
        layer.Dispose();
    }

    [Theory]
    [InlineData("https://example.com/agenda", 2)]       // top right
    [InlineData("WIFI:T:WPA;S:Guest;P:welcome-2025;;", 1)] // bottom left
    public void The_qr_code_on_the_tv_decodes(string text, int cornerNumber)
    {
        var corner = (InsetCorner)cornerNumber;
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Qr(text, corner);
        Assert.True(layer.HasOverlay);
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] frame = Frame(layer, white);
        // The card as the helper places it: whole-pixel modules, centred where the layout put it.
        System.Drawing.RectangleF place = Layouts.QrCard(new System.Drawing.SizeF(W, H), corner, H);
        int modules = QrCode.Encode(text)!.GetLength(0);
        float side = Layouts.QrModule(place.Width, modules) * (modules + 8);
        var card = new Rectangle((int)MathF.Round(place.X + (place.Width - side) / 2), (int)MathF.Round(place.Y + (place.Height - side) / 2), (int)side, (int)side);
        bool[,] seen = QrDecoder.Sample(frame, W * 4, card, modules);
        Assert.Equal(text, QrDecoder.Decode(seen).Text);
        layer.Dispose();
    }

    [Fact]
    public void The_banner_and_captions_show_with_the_picture_and_never_on_plain_black()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.Caption("Jane Doe", "Head of Sales");
        layer.LiveCaption("and that is why the numbers went up this quarter");
        Thread.Sleep(320); // the banner fades in (one thread: the Direct2D context is single-threaded)
        Assert.False(layer.IsAnimating);
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] shown = Frame(layer, white);
        var canvas = new System.Drawing.SizeF(W, H);
        System.Drawing.RectangleF banner = Layouts.Banner(canvas, true, 0); // its narrowest: the words here are shorter
        System.Drawing.RectangleF strip = Layouts.LiveStrip(canvas, banner.Top);
        Color bannerBody = At(shown, (int)banner.Right - 4, (int)(banner.Top + banner.Height / 2));
        Assert.True(bannerBody.R < 140, $"the banner darkens behind its words: {bannerBody}");
        Color stripEdge = At(shown, (int)strip.Left + 3, (int)(strip.Top + strip.Height / 2));
        Assert.True(stripEdge.R < 120, $"the captions have a dark band: {stripEdge}");

        // A break (blank with its message): the banner stays, but nothing said in the room appears.
        byte[] onBreak = Frame(layer, white, opacity: 0, visibility: 1, pictureShows: 0);
        Color breakStrip = At(onBreak, (int)strip.Left + 3, (int)(strip.Top + strip.Height / 2));
        Assert.True(breakStrip.R < 20, $"no caption band on a break: {breakStrip}");
        Color breakBanner = At(onBreak, (int)banner.Right - 4, (int)(banner.Top + banner.Height / 2));
        Assert.True(breakBanner.R < 60, "the banner is drawn on the black, darkly");

        byte[] black = Frame(layer, white, opacity: 0, visibility: 0); // blank, or held black for privacy
        (double mean, double spread) = Stats(black, new Rectangle(0, 0, W, H));
        Assert.True(mean < 15 && spread < 1, "plain black is plain black");

        layer.Caption(null, null);
        layer.LiveCaption(null);
        Thread.Sleep(320);
        Near(Color.White, At(Frame(layer, white), (int)(banner.Left + 30), (int)(banner.Top + banner.Height / 2)), 1, "gone again");
        layer.Dispose();
    }

    [Theory]
    [InlineData("blur")]           // twelve blurred hidden parts
    [InlineData("strokes-real")]   // 200 drawn lines, pen and highlighter
    [InlineData("strokes-random")] // 200 lines zigzagging across the whole TV
    [InlineData("mixed-tools")]    // the same with highlighters crossing themselves, arrows and boxes
    [InlineData("overlay")]        // spotlight, name banner, QR code, live captions
    [InlineData("all-random")]     // everything at once
    public void Speed_a_busy_frame_draws_well_inside_a_60_hz_frame(string scene)
    {
        if (!HasGpu) return;
        TimeScene(scene); // warm up: shaders, effects and text formats are made once
        double each = TimeScene(scene);
        Assert.True(each < 16, $"{scene}: a frame took {each:F2} ms (budget 16 ms; the TV shows a frame every 16.7 ms)");
    }

    [Fact]
    public void A_highlighter_doesnt_darken_where_it_crosses_itself()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        layer.InkStart(new InkStroke(1, InkTool.Highlighter, Color.Blue.ToArgb(), 0.05f));
        layer.InkAdd(1, [new PointF(0.2f, 0.2f), new PointF(0.8f, 0.8f), new PointF(0.8f, 0.2f), new PointF(0.2f, 0.8f)]);
        using ID2D1Bitmap1 white = Picture(Color.White);
        // While it's the line being drawn, and once finished (then it's drawn from cached triangles).
        foreach (string when in new[] { "while drawing", "finished" })
        {
            if (when == "finished")
            {
                layer.InkStart(new InkStroke(2, InkTool.Pen, Color.Red.ToArgb(), 0.005f));
                layer.InkAdd(2, [new PointF(0.05f, 0.95f)]);
            }
            byte[] frame = Frame(layer, white);
            Color single = At(frame, (int)(0.35f * W), (int)(0.35f * H)), crossing = At(frame, W / 2, H / 2);
            Assert.True(single.R < 220, $"{when}: the highlighter is there: {single}");
            Near(single, crossing, 4, $"{when}: where it crosses itself");
        }
        layer.Dispose();
    }

    /// <summary>A white picture with black words drawn by GDI+, as the GPU's bitmap.</summary>
    /// <summary>A W × H picture onto the GPU.</summary>
    private ID2D1Bitmap1 FromBitmap(Bitmap source)
    {
        using var bitmap = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap)) g.DrawImage(source, 0, 0, W, H);
        var data = bitmap.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            return _dc!.CreateBitmap(new SizeI(W, H), data.Scan0, (uint)data.Stride,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private ID2D1Bitmap1 PictureWithText(string text, Point at, int size, out Rectangle textAt)
    {
        using var bitmap = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", size, GraphicsUnit.Pixel);
            g.DrawString(text, font, Brushes.Black, at);
            textAt = Rectangle.Round(new RectangleF(at, g.MeasureString(text, font)));
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            return _dc!.CreateBitmap(new SizeI(W, H), data.Scan0, (uint)data.Stride,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [Fact]
    public void Automatic_hiding_reads_the_picture_on_the_gpu_and_covers_the_address()
    {
        if (!HasGpu) return;
        using ID2D1Bitmap1 picture = PictureWithText("Mail jane.doe@example.com today", new Point(40, 150), 30, out Rectangle textAt);
        (List<CropRegion> Regions, List<SensitiveMatch> Found) result = ([], []);
        using var done = new ManualResetEventSlim();
        using var redactor = new AutoRedactor(SensitiveKinds.Emails, (regions, f) =>
        {
            result = (regions, f);
            done.Set();
        });
        if (!redactor.Available) return; // no text recognizer for this PC's languages
        Assert.False(redactor.FirstScanDone);
        redactor.MaybeScan(_dc!, picture, new Rectangle(0, 0, W, H), changed: true, _target!);
        // Waited for here, on this thread: the Direct2D context is single-threaded (an await could move on to another thread).
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the scan finished");
        (List<CropRegion> regions, List<SensitiveMatch> matches) = result;
        Assert.True(redactor.FirstScanDone);
        Assert.Equal(SensitiveKinds.Emails, Assert.Single(matches).Kind);
        CropRegion box = Assert.Single(regions);
        var onPicture = new RectangleF((float)box.X * W, (float)box.Y * H, (float)box.Width * W, (float)box.Height * H);
        Assert.True(onPicture.Left < textAt.Left + textAt.Width * 0.25f && onPicture.Right > textAt.Left + textAt.Width * 0.7f, $"the box {onPicture} is on the address in {textAt}");
        Assert.False(onPicture.Contains(textAt.Left + 5, textAt.Top + textAt.Height / 2), "“Mail” stays readable");

        // Drawn like a hidden part: covered on the TV.
        var layer = new EffectsLayer(() => { });
        layer.AutoRedact(regions, RedactStyle.Solid);
        byte[] frame = Frame(layer, picture);
        Near(Color.FromArgb(255, EffectStyle.RedactFill), At(frame, (int)(onPicture.X + onPicture.Width / 2), (int)(onPicture.Y + onPicture.Height / 2)), 2, "the address is covered");
        layer.AutoRedact([], RedactStyle.Solid);
        (double mean, _) = Stats(Frame(layer, picture), Rectangle.Round(onPicture));
        Assert.True(mean > 150, "and shows again when uncovered");
        layer.Dispose();
    }

    [Fact]
    public void Covers_over_another_picture_land_on_it_and_stay_inside_it()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        using ID2D1Bitmap1 white = Picture(Color.White);
        // The other picture drawn small on the right half, as split screen draws a pane.
        var pane = new System.Drawing.RectangleF(W / 2f, 0, W / 2f, H / 2f);
        _dc!.BeginDraw();
        _dc.Clear(new Color4(0, 0, 0, 1));
        _dc.DrawBitmap(white, new Rect(pane.X, pane.Y, pane.Width, pane.Height), 1, InterpolationMode.NearestNeighbor, new Rect(0, 0, W, H), null);
        layer.RenderCovers(_dc, _factory!, white, Whole, Whole, pane, [(Box, RedactStyle.Solid), (CropRegion.Create(0, 0, 0.2, 0.2)!, RedactStyle.Blurred)], 1);
        _dc.EndDraw().CheckError();
        byte[] frame = ReadBack();
        Near(Color.FromArgb(255, EffectStyle.RedactFill), At(frame, (int)(pane.X + pane.Width / 2), (int)(pane.Y + pane.Height / 2)), 2, "the box is in the middle of the pane");
        Near(Color.White, At(frame, (int)pane.Right - 4, (int)pane.Bottom - 4), 1, "the rest of the pane shows");
        Near(Color.Black, At(frame, 10, 10), 1, "nothing outside the pane");
        Assert.True(At(frame, (int)pane.X + 5, 5).R < 255, "the blurred corner is darkened");
        // Nothing to cover, or a fully faded picture: nothing drawn.
        _dc.BeginDraw();
        _dc.Clear(new Color4(0, 0, 0, 1));
        layer.RenderCovers(_dc, _factory!, white, Whole, Whole, pane, [], 1);
        layer.RenderCovers(_dc, _factory!, white, Whole, Whole, pane, [(Box, RedactStyle.Solid)], 0);
        _dc.EndDraw().CheckError();
        Near(Color.Black, At(ReadBack(), (int)(pane.X + pane.Width / 2), (int)(pane.Y + pane.Height / 2)), 1, "nothing drawn");
        layer.Dispose();
    }

    [Fact]
    public void Faces_are_found_on_the_gpu_and_blurred()
    {
        if (!HasGpu || !FaceFinder.IsSupported) return;
        using var painting = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "face-painting.jpg"));
        using var scaled = new Bitmap(W, H);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.Clear(Color.Gray);
            g.DrawImage(painting, (W - H * painting.Width / painting.Height) / 2, 0, H * painting.Width / painting.Height, H);
        }
        using ID2D1Bitmap1 picture = FromBitmap(scaled);
        List<CropRegion>? found = null;
        using var done = new ManualResetEventSlim();
        using var faces = new FaceFinder(keepLargest: false, regions =>
        {
            found = regions;
            done.Set();
        });
        Assert.False(faces.FirstScanDone);
        faces.MaybeScan(_dc!, picture, new Rectangle(0, 0, W, H), changed: true, _target!);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the scan finished");
        CropRegion face = Assert.Single(found!);
        Assert.True(faces.FirstScanDone);
        Assert.InRange(face.X + face.Width / 2, 0.4, 0.6);
        var layer = new EffectsLayer(() => { });
        layer.AutoCovers([(face, RedactStyle.Blurred)]);
        var onPicture = Rectangle.Round(new System.Drawing.RectangleF((float)face.X * W, (float)face.Y * H, (float)face.Width * W, (float)face.Height * H));
        (_, double before) = Stats(Frame(new EffectsLayer(() => { }), picture), onPicture);
        (_, double after) = Stats(Frame(layer, picture), onPicture);
        Assert.True(after < before * 0.6, $"blurred: detail {before:F1} became {after:F1}");
        layer.Dispose();
    }

    [Fact]
    public void A_shortcut_shows_as_a_chip_with_the_picture_and_never_on_a_break()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        using ID2D1Bitmap1 white = Picture(Color.White);
        layer.KeyCombo("Ctrl + Shift + S");
        System.Drawing.RectangleF chip = Layouts.KeyChip(new System.Drawing.SizeF(W, H), 10, H);
        byte[] frame = Frame(layer, white);
        Assert.True(At(frame, W / 2, (int)(chip.Y + 3)).R < 120, "a dark chip in the middle at the bottom");
        Near(Color.White, At(frame, 10, 10), 1, "the rest of the picture is clear");
        byte[] onBreak = Frame(layer, white, visibility: 1, pictureShows: 0);
        Assert.True(At(onBreak, W / 2, (int)(chip.Y + 3)).R > 200 || At(onBreak, W / 2, (int)(chip.Y + 3)) == At(Frame(new EffectsLayer(() => { }), white, visibility: 1, pictureShows: 0), W / 2, (int)(chip.Y + 3)), "not on a break screen");
        layer.KeyCombo(null);
        Near(Color.White, At(Frame(layer, white), W / 2, (int)(chip.Y + 3)), 1, "gone when taken off");
        layer.Dispose();
    }

    [Fact]
    public void Top_captions_sit_at_the_top()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        var look = new CaptionLook(CaptionSize.Large, CaptionPlace.Top);
        layer.CaptionStyle(look);
        layer.LiveCaption("words at the top of the TV");
        using ID2D1Bitmap1 white = Picture(Color.White);
        byte[] frame = Frame(layer, white);
        System.Drawing.RectangleF strip = Layouts.LiveStrip(new System.Drawing.SizeF(W, H), H, look);
        Assert.True(At(frame, (int)strip.Left + 3, (int)(strip.Top + strip.Height / 2)).R < 120, "a dark band at the top");
        Near(Color.White, At(frame, W / 2, H - 20), 1, "the bottom is clear");
        layer.Dispose();
    }

    [Fact]
    public void Memory_a_busy_frame_allocates_almost_nothing()
    {
        if (!HasGpu) return;
        var layer = new EffectsLayer(() => { });
        var random = new Random(5);
        for (int s = 1; s <= 200; s++)
        {
            layer.InkStart(new InkStroke(s, (InkTool)(s % 4), Color.Red.ToArgb(), 0.005f));
            float x = (float)random.NextDouble() * 0.8f, y = (float)random.NextDouble() * 0.8f;
            layer.InkAdd(s, [.. Enumerable.Range(0, 100).Select(i => new PointF(x + i * 0.002f, y + MathF.Sin(i * 0.2f) * 0.02f))]);
        }
        layer.Redact([CropRegion.Create(0.1, 0.1, 0.2, 0.2)!], RedactStyle.Solid);
        layer.Spotlight(new PointF(0.5f, 0.5f));
        layer.Caption("Jane Doe", "Head of Sales");
        layer.Qr("https://example.com/agenda", InsetCorner.TopRight);
        using ID2D1Bitmap1 white = Picture(Color.White);
        Color4 black = new(0, 0, 0, 1);
        void Draw()
        {
            _dc!.BeginDraw();
            _dc.Clear(black);
            _dc.DrawBitmap(white, new Rect(0, 0, W, H), 1, InterpolationMode.Linear, new Rect(0, 0, W, H), null);
            layer.Render(_dc, _factory!, _dwrite!, white, Whole, Whole, Whole, 1, false);
            layer.RenderOverlay(_dc, _factory!, _dwrite!, new System.Drawing.SizeF(W, H), 1, 1);
            _dc.EndDraw().CheckError();
        }
        for (int i = 0; i < 5; i++) Draw(); // shapes, brushes and text formats are made once
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 60;
        for (int i = 0; i < frames; i++) Draw();
        double each = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)frames;
        // Before the frame lists were kept, a drawing like this copied 160 KB of points every frame; now about 0.3 KB.
        Assert.True(each < 2 * 1024, $"a frame allocated {each / 1024:F1} KB (budget 2 KB)");
        layer.Dispose();
    }

    /// <summary>The average time of a frame of <paramref name="part"/>, the GPU's work included.</summary>
    private double TimeScene(string part)
    {
        if (!HasGpu) return 0;
        var layer = new EffectsLayer(() => { });
        var random = new Random(7);
        if (part is "blur" or "all-real" or "all-random") layer.Redact([.. Enumerable.Range(0, Redactions.MaxPerSource).Select(i => CropRegion.Create(i * 0.07, 0.05, 0.06, 0.2)!)], RedactStyle.Blurred);
        if (part is "strokes-random" or "all-random" or "mixed-tools")
            for (int s = 1; s <= 200; s++)
            {
                layer.InkStart(new InkStroke(s, part == "mixed-tools" ? (InkTool)(s % 4) : InkTool.Pen, Color.Red.ToArgb(), 0.005f));
                layer.InkAdd(s, [.. Enumerable.Range(0, 100).Select(_ => new PointF((float)random.NextDouble(), (float)random.NextDouble()))]);
            }
        if (part is "strokes-real" or "all-real")
            for (int s = 1; s <= 200; s++)
            {
                float x = (float)random.NextDouble() * 0.8f, y = (float)random.NextDouble() * 0.8f;
                layer.InkStart(new InkStroke(s, (InkTool)(s % 2), Color.Red.ToArgb(), 0.005f));
                layer.InkAdd(s, [.. Enumerable.Range(0, 100).Select(i => new PointF(x + i * 0.002f, y + MathF.Sin(i * 0.2f) * 0.02f))]);
            }
        if (part is "overlay" or "all-real" or "all-random")
        {
            layer.Spotlight(new PointF(0.5f, 0.5f));
            layer.Caption("Jane Doe", "Head of Sales");
            layer.Qr("https://example.com/a/fairly/long/link/to/fill/the/code", InsetCorner.TopRight);
            layer.LiveCaption("words words words words words words words words words words");
        }
        using ID2D1Bitmap1 checker = Picture(null);
        Frame(layer, checker);
        const int frames = 60;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            _dc!.BeginDraw();
            _dc.Clear(new Color4(0, 0, 0, 1));
            _dc.DrawBitmap(checker, new Rect(0, 0, W, H), 1, InterpolationMode.Linear, new Rect(0, 0, W, H), null);
            layer.Render(_dc, _factory!, _dwrite!, checker, Whole, Whole, Whole, 1, false);
            layer.RenderOverlay(_dc, _factory!, _dwrite!, new System.Drawing.SizeF(W, H), 1, 1);
            _dc.EndDraw().CheckError();
        }
        ReadBack();
        layer.Dispose();
        return clock.Elapsed.TotalMilliseconds / frames;
    }

    public void Dispose()
    {
        if (_dc is not null) _dc.Target = null;
        _target?.Dispose();
        _dwrite?.Dispose();
        _dc?.Dispose();
        _d2d?.Dispose();
        _factory?.Dispose();
        _device?.Dispose();
    }
}
