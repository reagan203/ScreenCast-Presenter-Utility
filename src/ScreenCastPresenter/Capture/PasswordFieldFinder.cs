using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Password boxes, found exactly (FR-98): Windows' UI Automation says where every password box in
/// the shown window is, so it's covered where it is, whatever it shows (dots, a revealed password,
/// a browser's autofill), without reading the picture. Asked again every <see cref="Every"/> on
/// its own thread (UI Automation can take a moment with a busy app; the picture never waits for
/// it). For a whole screen, the windows on it are asked, front to back.
/// </summary>
internal sealed class PasswordFieldFinder : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(200);

    /// <summary>A whole screen asks several windows: less often.</summary>
    public static readonly TimeSpan ScreenEvery = TimeSpan.FromMilliseconds(400);

    /// <summary>Pixels added round each box, so its edge and a "show password" button stay covered.</summary>
    public const int Padding = 4;

    /// <summary>On a whole screen, this many windows (front first) are asked.</summary>
    public const int MaxWindowsOnScreen = 6;

    private readonly SourceWindow _source;
    private readonly Action _changed;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _lock = new();
    private List<CropRegion> _regions = [];
    private volatile bool _firstDone;

    /// <param name="changed">Called on the finder's thread when what's covered changed.</param>
    public PasswordFieldFinder(SourceWindow source, Action changed)
    {
        _source = source;
        _changed = changed;
        if (source.IsOffScreen)
        {
            _firstDone = true; // a camera or a picture has no boxes
            _thread = new Thread(() => { });
            return;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "SCP password boxes" };
        _thread.Start();
    }

    /// <summary>The first look is done (or can't happen): something new may be shown.</summary>
    public bool FirstScanDone => _firstDone;

    /// <summary>Where the boxes are, as parts of what's shown (a window's client area, or the screen).</summary>
    public List<CropRegion> Regions
    {
        get
        {
            lock (_lock) return _regions;
        }
    }

    private void Run()
    {
        try
        {
            using UiAutomation? uia = UiAutomation.Create();
            if (uia is null) return;
            TimeSpan every = _source.IsScreen ? ScreenEvery : Every;
            while (!_stop.IsSet)
            {
                List<CropRegion> now = Find(uia, _source);
                bool different;
                lock (_lock)
                {
                    different = !now.SequenceEqual(_regions);
                    if (different) _regions = now;
                }
                _firstDone = true;
                if (different) _changed();
                if (_stop.Wait(every)) break;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _firstDone = true;
        }
    }

    /// <summary>The boxes in <paramref name="source"/> now, as parts of what's shown.</summary>
    public static List<CropRegion> Find(UiAutomation uia, SourceWindow source)
    {
        if (source.Screen is { } screen)
        {
            var boxes = new List<Rectangle>();
            int asked = 0;
            foreach (SourceWindow w in WindowEnumerator.GetPresentableWindows())
            {
                if (w.IsMinimized || !WindowEnumerator.ClientBoundsOnScreen(w.Handle).IntersectsWith(screen.Bounds)) continue;
                boxes.AddRange(uia.PasswordBoxes(w.Handle));
                if (++asked == MaxWindowsOnScreen) break;
            }
            return ToRegions(boxes, screen.Bounds, Padding);
        }
        if (source.IsOffScreen || source.Handle == 0) return [];
        return ToRegions(uia.PasswordBoxes(source.Handle), WindowEnumerator.ClientBoundsOnScreen(source.Handle), Padding);
    }

    /// <summary>Once, on this thread (a screenshot).</summary>
    public static List<CropRegion> FindOnce(SourceWindow source)
    {
        using UiAutomation? uia = UiAutomation.Create();
        return uia is null ? [] : Find(uia, source);
    }

    /// <summary>
    /// Screen rectangles as parts of <paramref name="shown"/> (the client area or the screen, in
    /// screen coordinates), each grown by <paramref name="padding"/> pixels and clipped to it.
    /// </summary>
    public static List<CropRegion> ToRegions(IEnumerable<Rectangle> boxes, Rectangle shown, int padding)
    {
        var regions = new List<CropRegion>();
        if (shown.Width <= 0 || shown.Height <= 0) return regions;
        foreach (Rectangle box in boxes)
        {
            Rectangle r = Rectangle.Inflate(box, padding, padding);
            r.Intersect(shown);
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (CropRegion.Create((r.X - shown.X) / (double)shown.Width, (r.Y - shown.Y) / (double)shown.Height,
                    r.Width / (double)shown.Width, r.Height / (double)shown.Height, 0.0005) is { } region)
                regions.Add(region);
            if (regions.Count == UiAutomation.MaxBoxes) break;
        }
        return regions;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(1))) return; // stuck in a hung app: it ends on its own (background thread)
        _stop.Dispose();
    }
}
