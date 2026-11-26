using System.Diagnostics;
using System.Drawing.Imaging;
using ScreenCastPresenter.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// One still frame of a window or screen, for screenshots (FR-50). This is the single place a
/// frame is copied to CPU memory (NFR-09d): one frame, released by the caller right after saving.
/// SCP's own windows are excluded from capture, so they never appear in screenshots (FR-56).
/// </summary>
internal static class StillCapture
{
    /// <summary>
    /// The source (its client area for windows, cropped if it has a crop), or null if no frame came
    /// in time. Parts hidden from the audience (FR-74) are covered with a solid box: a screenshot of
    /// what's shown never holds what the audience was kept from seeing.
    /// </summary>
    /// <param name="autoHide">Automatic hiding (FR-88): what it finds in the picture is covered too.</param>
    /// <param name="words">The presenter's own words for automatic hiding.</param>
    public static Task<Bitmap?> CaptureAsync(SourceWindow source, bool includeCursor, TimeSpan timeout, IReadOnlyList<Core.CropRegion>? hidden = null,
        Core.SensitiveKinds autoHide = Core.SensitiveKinds.None, IReadOnlyList<string>? words = null) =>
        Task.Run(() =>
        {
            Bitmap? image = Cover(Capture(source, includeCursor, timeout, out Rectangle client, out Rectangle shown), client, shown, hidden);
            if (image is null) return null;
            if (autoHide.HasFlag(Core.SensitiveKinds.PasswordFields) && !source.IsOffScreen)
                Cover(image, client, shown, PasswordFieldFinder.FindOnce(source)); // exactly where Windows says they are
            AutoRedactor.CoverInStill(image, autoHide, Core.SensitiveText.CustomPattern(words));
            return image;
        });

    /// <summary>The source as it is, nothing covered, with where its client area and the part shown are (the check before sharing, FR-102).</summary>
    internal static Task<(Bitmap? Image, Rectangle Client, Rectangle Shown)> CaptureUncoveredAsync(SourceWindow source, TimeSpan timeout) =>
        Task.Run(() =>
        {
            Bitmap? image = Capture(source, false, timeout, out Rectangle client, out Rectangle shown);
            return (image, client, shown);
        });

    /// <summary>Where each hidden box falls in a picture of <paramref name="shown"/> (part of a client area <paramref name="client"/>).</summary>
    internal static List<Rectangle> Covers(Rectangle client, Rectangle shown, IReadOnlyList<Core.CropRegion> hidden)
    {
        var covers = new List<Rectangle>();
        foreach (Core.CropRegion box in hidden)
        {
            Rectangle r = box.ToPixels(client.Size);
            r.Offset(client.Location);
            r.Intersect(shown);
            if (r.Width <= 0 || r.Height <= 0) continue;
            r.Offset(-shown.X, -shown.Y);
            covers.Add(r);
        }
        return covers;
    }

    private static Bitmap? Cover(Bitmap? image, Rectangle client, Rectangle shown, IReadOnlyList<Core.CropRegion>? hidden)
    {
        if (image is null || hidden is not { Count: > 0 }) return image;
        using Graphics g = Graphics.FromImage(image);
        using var brush = new SolidBrush(Core.EffectStyle.RedactFill);
        foreach (Rectangle r in Covers(client, shown, hidden)) g.FillRectangle(brush, r);
        return image;
    }

    private static Bitmap? Capture(SourceWindow source, bool includeCursor, TimeSpan timeout, out Rectangle client, out Rectangle shown)
    {
        client = shown = Rectangle.Empty;
        using ID3D11Device device = Direct3D.CreateDevice();
        ID3D11DeviceContext context = device.ImmediateContext;
        var winrt = Direct3D.CreateWinRTDevice(device);
        try
        {
            GraphicsCaptureItem item = Direct3D.CreateItem(source);
            using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using GraphicsCaptureSession session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = includeCursor;
            Direct3D.TryDisableBorder(session);
            using var arrived = new ManualResetEventSlim();
            pool.FrameArrived += (_, _) => arrived.Set();
            session.StartCapture();
            if (!arrived.Wait(timeout)) return null;

            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is null) return null;
            var content = new Size(frame.ContentSize.Width, frame.ContentSize.Height);
            if (content.Width <= 0 || content.Height <= 0) return null;

            Rectangle area = source.IsScreen ? new Rectangle(Point.Empty, content) : Direct3D.ClientAreaInFrame(source.Handle, content);
            client = area;
            if (source.Crop is { } crop)
            {
                Rectangle part = crop.ToPixels(area.Size);
                part.Offset(area.Location);
                area = part;
            }

            shown = area;
            using ID3D11Texture2D texture = Direct3D.TextureOf(frame.Surface);
            return ReadPixels(device, context, texture, area);
        }
        finally
        {
            winrt.Dispose();
        }
    }

    private static unsafe Bitmap ReadPixels(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, Rectangle area)
    {
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)area.Width,
            Height = (uint)area.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0,
            new Vortice.Mathematics.Box(area.Left, area.Top, 0, area.Right, area.Bottom, 1));

        MappedSubresource map = context.Map(staging, 0, MapMode.Read);
        try
        {
            // 32bppRgb: the capture's alpha channel is ignored, so the PNG is fully opaque.
            var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < area.Height; y++)
                {
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, area.Width * 4L);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
