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
    /// <summary>The source (its client area for windows, cropped if it has a crop), or null if no frame came in time.</summary>
    public static Task<Bitmap?> CaptureAsync(SourceWindow source, bool includeCursor, TimeSpan timeout) =>
        Task.Run(() => Capture(source, includeCursor, timeout));

    private static Bitmap? Capture(SourceWindow source, bool includeCursor, TimeSpan timeout)
    {
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
            if (source.Crop is { } crop)
            {
                Rectangle part = crop.ToPixels(area.Size);
                part.Offset(area.Location);
                area = part;
            }

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
