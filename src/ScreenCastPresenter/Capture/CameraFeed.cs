using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using ScreenCastPresenter.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// A camera's picture for the capture helper (FR-68): Windows' camera pipeline delivers each frame
/// as 32-bit colour in ordinary memory; the newest one is kept, the render thread copies it to the
/// GPU and draws it like any other frame. Runs only in the helper, so camera drivers never load
/// into ScreenCast Presenter itself. Problems are told in plain words (no access, in use, unplugged).
/// </summary>
internal sealed class CameraFeed : IDisposable
{
    private readonly object _sync = new();
    private readonly Action _wake;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _pixels = [];
    private int _stride;
    private System.Drawing.Size _size;
    private bool _fresh;
    private volatile string? _failure;

    private CameraFeed(Action wake) => _wake = wake;

    /// <summary>Why the camera stopped (unplugged, taken by another app); null while it works.</summary>
    public string? Failure => _failure;

    /// <summary>Starts the camera with that id. Throws <see cref="InvalidOperationException"/> with a plain reason when it can't.</summary>
    public static CameraFeed Start(string id, Action wake)
    {
        var feed = new CameraFeed(wake);
        try
        {
            feed.StartAsync(id).GetAwaiter().GetResult();
            return feed;
        }
        catch
        {
            feed.Dispose();
            throw;
        }
    }

    private async Task StartAsync(string id)
    {
        IReadOnlyList<MediaFrameSourceGroup> groups = await MediaFrameSourceGroup.FindAllAsync();
        MediaFrameSourceGroup group = groups.FirstOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("That camera isn't connected any more.");

        // Its own picture size first; if another app is using it, share what that app gets.
        bool exclusive = true;
        _capture = await InitialiseAsync(group, MediaCaptureSharingMode.ExclusiveControl);
        if (_capture is null)
        {
            exclusive = false;
            _capture = await InitialiseAsync(group, MediaCaptureSharingMode.SharedReadOnly)
                       ?? throw new InvalidOperationException("The camera is being used by another app. Close it there, then try again.");
        }
        _capture.Failed += (_, _) =>
        {
            _failure = "The camera stopped: it was unplugged, or another app took it.";
            _wake();
        };

        MediaFrameSource source = _capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderBy(s => s.Info.MediaStreamType switch { Windows.Media.Capture.MediaStreamType.VideoRecord => 0, Windows.Media.Capture.MediaStreamType.VideoPreview => 1, _ => 2 })
            .FirstOrDefault() ?? throw new InvalidOperationException("That camera has no picture to show.");

        if (exclusive) // sharing another app's camera, its picture size isn't ours to change
        {
            var formats = source.SupportedFormats
                .Where(f => f.VideoFormat is not null && f.FrameRate.Denominator != 0)
                .Select(f => (Format: f, Option: new CameraFormat((int)f.VideoFormat.Width, (int)f.VideoFormat.Height, (double)f.FrameRate.Numerator / f.FrameRate.Denominator)))
                .ToList();
            if (CameraFormats.Choose(formats.Select(f => f.Option)) is { } best)
            {
                try
                {
                    await source.SetFormatAsync(formats.First(f => f.Option == best).Format);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex); // keep the camera's own choice
                }
            }
        }

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        MediaFrameReaderStartStatus status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException(status == MediaFrameReaderStartStatus.DeviceNotAvailable
                ? "The camera is being used by another app. Close it there, then try again."
                : "The camera couldn't be started.");
    }

    private static async Task<MediaCapture?> InitialiseAsync(MediaFrameSourceGroup group, MediaCaptureSharingMode sharing)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = sharing,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            return capture;
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException("Windows isn't letting apps use the camera. Turn on camera access in Settings › Privacy & security › Camera, including for desktop apps.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            capture.Dispose();
            return null;
        }
    }

    /// <summary>On a camera thread: keep the newest frame (as 32-bit colour) and wake the render thread.</summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
            SoftwareBitmap? bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            SoftwareBitmap? converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            try
            {
                SoftwareBitmap bgra = converted ?? bitmap;
                int width = bgra.PixelWidth, height = bgra.PixelHeight;
                int stride;
                using (BitmapBuffer locked = bgra.LockBuffer(BitmapBufferAccessMode.Read)) stride = locked.GetPlaneDescription(0).Stride;
                if (width <= 0 || height <= 0 || stride < width * 4) return;
                lock (_sync)
                {
                    int bytes = stride * height;
                    if (_pixels.Length != bytes) _pixels = new byte[bytes];
                    bgra.CopyToBuffer(_pixels.AsBuffer());
                    _stride = stride;
                    _size = new System.Drawing.Size(width, height);
                    _fresh = true;
                }
            }
            finally
            {
                converted?.Dispose();
            }
            _wake();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex); // one bad frame; the next one may be fine
        }
    }

    /// <summary>
    /// Hands the newest frame (if there is one since last time) to <paramref name="upload"/>:
    /// its pixels, bytes per row and size. False when nothing new has arrived.
    /// </summary>
    public bool TakeLatest(Action<byte[], int, System.Drawing.Size> upload)
    {
        lock (_sync)
        {
            if (!_fresh) return false;
            _fresh = false;
            upload(_pixels, _stride, _size);
            return true;
        }
    }

    public void Dispose()
    {
        if (_reader is { } reader)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            reader.Dispose();
        }
        _reader = null;
        _capture?.Dispose(); // the camera light goes off
        _capture = null;
    }
}
