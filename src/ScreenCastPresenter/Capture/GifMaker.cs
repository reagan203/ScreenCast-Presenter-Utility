using System.Globalization;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Recording;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// --make-gif &lt;mp4&gt; &lt;gif&gt; &lt;seconds&gt;: the start of a recording made into a short animated
/// picture (FR-131), for a bug report, a how-to or a chat. Decoded with Windows' Media Foundation
/// in a helper process, at most <see cref="Fps"/> frames a second and <see cref="MaxWidth"/> wide,
/// then written by <see cref="GifEncoder"/>. A still moment keeps its length rather than repeating
/// frames. "progress N" as it goes, then "done" or "failed" and why.
/// </summary>
internal static class GifMaker
{
    public const int Fps = 10;
    public const int MaxWidth = 720;
    public const int DefaultSeconds = 15, MaxSeconds = 60;

    /// <summary>What the helper was doing (for the diagnostic log when something fails).</summary>
    private static string Step = "start";

    private const uint FirstVideo = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);

    public static int Run(string[] args, Action<string> send)
    {
        if (args.Length < 3) return 2;
        string input = args[1], output = args[2];
        int seconds = args.Length > 3 && int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? Math.Clamp(s, 1, MaxSeconds) : DefaultSeconds;
        string temporary = output + ".tmp";
        try
        {
            int frames = Make(input, temporary, TimeSpan.FromSeconds(seconds), p => send(string.Create(CultureInfo.InvariantCulture, $"progress {p}")));
            if (frames == 0)
            {
                File.Delete(temporary);
                send("failed The video has no pictures to make a GIF from.");
                return 4;
            }
            File.Move(temporary, output, overwrite: true);
            send("done");
            return 0;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            send("detail " + Step + " " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message.ReplaceLineEndings(" ")); // for the diagnostic log only
            send(ex is IOException { HResult: unchecked((int)0x80070070) } ? "failed The disk is full." : "failed The video couldn't be read to make a GIF.");
            return 4;
        }
    }

    /// <summary>Makes the GIF; how many frames it has.</summary>
    internal static unsafe int Make(string input, string output, TimeSpan length, Action<int>? progress = null)
    {
        MediaFoundation.Start();
        IMFSourceReader? reader = null;
        try
        {
            Step = "attributes";
            PInvoke.MFCreateAttributes(out IMFAttributes attributes, 1).ThrowOnFailure();
            try
            {
                Step = "open";
                Guid key = PInvoke.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                attributes.SetUINT32(&key, 1); // the reader turns any video into plain pixels
                MediaFiles.OpenReader(input, attributes, out reader).ThrowOnFailure(); // read while others may still be writing it
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }
            PInvoke.MFCreateMediaType(out IMFMediaType wanted).ThrowOnFailure();
            try
            {
                Guid major = PInvoke.MF_MT_MAJOR_TYPE, video = PInvoke.MFMediaType_Video, sub = PInvoke.MF_MT_SUBTYPE, rgb = PInvoke.MFVideoFormat_RGB32;
                wanted.SetGUID(&major, &video);
                wanted.SetGUID(&sub, &rgb);
                Step = "pixels";
                reader.SetCurrentMediaType(FirstVideo, null, wanted);
            }
            finally
            {
                Marshal.ReleaseComObject(wanted);
            }
            Step = "size";
            reader.GetCurrentMediaType(FirstVideo, out IMFMediaType current);
            int width, height, stride;
            try
            {
                Guid sizeKey = PInvoke.MF_MT_FRAME_SIZE, strideKey = PInvoke.MF_MT_DEFAULT_STRIDE;
                current.GetUINT64(&sizeKey, out ulong size);
                width = (int)(size >> 32);
                height = (int)(size & 0xFFFFFFFF);
                try
                {
                    current.GetUINT32(&strideKey, out uint raw);
                    stride = unchecked((int)raw);
                }
                catch (COMException)
                {
                    stride = width * 4;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(current);
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("No picture size.");
            (int gifWidth, int gifHeight) = GifEncoder.FitWidth(width, height, MaxWidth);

            using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            using var gif = new GifEncoder(file, gifWidth, gifHeight);
            using var frame = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var small = new Bitmap(gifWidth, gifHeight, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var pixels = new int[gifWidth * gifHeight];
            long step = TimeSpan.TicksPerSecond / Fps, end = length.Ticks, next = 0;
            int[]? pending = null;
            long pendingAt = 0, lastTime = 0;
            int lastPercent = -1;
            while (true)
            {
                uint flags;
                long time;
                IMFSample_unmanaged* sample = null;
                Step = "read";
                reader.ReadSample(FirstVideo, 0, null, &flags, &time, &sample);
                bool ended = (flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample is not null) sample->Release();
                    throw new InvalidDataException("The video can't be decoded.");
                }
                if (sample is not null)
                {
                    try
                    {
                        if (time >= end) ended = true;
                        else if (time >= next)
                        {
                            Copy(sample, frame, width, height, stride);
                            using (Graphics g = Graphics.FromImage(small))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(frame, new Rectangle(0, 0, gifWidth, gifHeight));
                            }
                            System.Drawing.Imaging.BitmapData data = small.LockBits(new Rectangle(0, 0, gifWidth, gifHeight), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                            try
                            {
                                for (int y = 0; y < gifHeight; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * gifWidth, gifWidth);
                            }
                            finally
                            {
                                small.UnlockBits(data);
                            }
                            // The frame before lasts until this one: still moments keep their length.
                            if (pending is not null) gif.AddFrame(pending, TimeSpan.FromTicks(time - pendingAt));
                            pending = (int[])pixels.Clone();
                            pendingAt = time;
                            next = time + step;
                        }
                        lastTime = Math.Max(lastTime, time);
                    }
                    finally
                    {
                        sample->Release();
                    }
                    int percent = (int)Math.Clamp(lastTime * 100 / Math.Max(1, end), 0, 100);
                    if (percent / 10 != lastPercent / 10)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent);
                    }
                }
                if (ended) break;
            }
            if (pending is not null) gif.AddFrame(pending, TimeSpan.FromTicks(Math.Max(step, Math.Min(end, lastTime + step) - pendingAt)));
            gif.Finish();
            return gif.Frames;
        }
        finally
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
            MediaFoundation.Stop();
        }
    }

    /// <summary>One decoded frame (32-bit pixels, top-down or bottom-up by <paramref name="stride"/>'s sign) into <paramref name="frame"/>.</summary>
    private static unsafe void Copy(IMFSample_unmanaged* sample, Bitmap frame, int width, int height, int stride)
    {
        IMFMediaBuffer_unmanaged* buffer = null;
        sample->ConvertToContiguousBuffer(out buffer);
        try
        {
            byte* bytes;
            uint max, length;
            buffer->Lock(&bytes, &max, &length);
            try
            {
                int rowBytes = width * 4, abs = Math.Abs(stride);
                if ((long)abs * height > length) throw new InvalidDataException("A frame is smaller than its size says.");
                System.Drawing.Imaging.BitmapData data = frame.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        int from = stride >= 0 ? y : height - 1 - y; // a negative stride is a picture stored bottom up
                        Buffer.MemoryCopy(bytes + (long)from * abs, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, rowBytes);
                    }
                }
                finally
                {
                    frame.UnlockBits(data);
                }
            }
            finally
            {
                buffer->Unlock();
            }
        }
        finally
        {
            buffer->Release();
        }
    }

    /// <summary>Runs the helper from the app: null when the GIF was made, else why not.</summary>
    public static async Task<string?> MakeAsync(string mp4, string gif, int seconds, Action<int>? progress, TimeSpan timeout)
    {
        var info = new System.Diagnostics.ProcessStartInfo(CaptureHost.HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        foreach (string a in new[] { CaptureProtocol.GifSwitch, mp4, gif, seconds.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(a);
        using System.Diagnostics.Process? helper = System.Diagnostics.Process.Start(info);
        if (helper is null) return "The GIF couldn't be made.";
        using var cancel = new CancellationTokenSource(timeout);
        string? problem = null;
        bool done = false;
        try
        {
            string? line;
            while ((line = await helper.StandardOutput.ReadLineAsync(cancel.Token)) is not null)
            {
                if (line == "done") done = true;
                else if (line.StartsWith("failed ", StringComparison.Ordinal)) problem ??= line[7..];
                else if (line.StartsWith("detail ", StringComparison.Ordinal)) Core.DiagLog.Write("gif: " + line[7..]);
                else if (line.StartsWith("progress ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out int p)) progress?.Invoke(p);
            }
            await helper.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            return "Making the GIF took too long.";
        }
        return done ? null : problem ?? "The GIF couldn't be made.";
    }
}
