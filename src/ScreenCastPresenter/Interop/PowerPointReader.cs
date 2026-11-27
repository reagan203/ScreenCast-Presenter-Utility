using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// Asks the PowerPoint running on this PC which slide it's on and what its notes say (FR-122),
/// through PowerPoint's own automation, on a thread of its own (PowerPoint busy with a dialog
/// can't hold up the app), every <see cref="SlideNotes.Every"/> while asked to. Changes are reported
/// on the thread that started it. Only reads: nothing in PowerPoint is changed.
/// </summary>
internal sealed partial class PowerPointReader : IDisposable
{
    private const int PlaceholderBody = 2; // ppPlaceholderBody

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CLSIDFromProgID(string progId, out Guid clsid);

    [LibraryImport("oleaut32.dll")]
    private static partial int GetActiveObject(in Guid clsid, nint reserved, out nint unknown);

    private readonly SynchronizationContext _ui;
    private readonly Action<SlideInfo?> _changed;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _watching;
    private volatile bool _stop;

    /// <param name="changed">The slide and its notes (null: PowerPoint isn't there or has nothing open), on the starting thread.</param>
    public PowerPointReader(Action<SlideInfo?> changed)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _changed = changed;
    }

    public void Watch(bool on)
    {
        _watching = on;
        if (on && _thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "SCP PowerPoint notes" };
            _thread.SetApartmentState(ApartmentState.STA); // Office automation expects a single-threaded apartment
            _thread.Start();
        }
        _wake.Set();
    }

    private void Run()
    {
        SlideInfo? last = null;
        bool first = true;
        while (!_stop)
        {
            if (_watching)
            {
                SlideInfo? now = ReadOnce();
                if (first || now != last)
                {
                    first = false;
                    last = now;
                    _ui.Post(_ => { if (!_stop && _watching) _changed(now); }, null);
                }
            }
            else first = true;
            _wake.WaitOne(_watching ? (int)SlideNotes.Every.TotalMilliseconds : Timeout.Infinite);
        }
    }

    /// <summary>Where PowerPoint is now: its slide show if one runs, otherwise the slide being edited; null when it can't say.</summary>
    internal static SlideInfo? ReadOnce()
    {
        if (CLSIDFromProgID("PowerPoint.Application", out Guid clsid) < 0) return null; // PowerPoint isn't installed
        if (GetActiveObject(clsid, 0, out nint unknown) < 0 || unknown == 0) return null; // not running
        object? app = null;
        try
        {
            app = Marshal.GetObjectForIUnknown(unknown);
            dynamic pp = app;
            dynamic? slide = null;
            bool show = false;
            int count = 0;
            if ((int)pp.SlideShowWindows.Count > 0)
            {
                dynamic window = pp.SlideShowWindows.Item(1);
                show = true;
                try
                {
                    slide = window.View.Slide; // throws on the black "end of slide show" screen
                }
                catch (COMException)
                {
                    return null;
                }
                count = (int)window.Presentation.Slides.Count;
            }
            else if ((int)pp.Windows.Count > 0)
            {
                dynamic window = pp.ActiveWindow;
                slide = window.View.Slide;
                count = (int)window.Presentation.Slides.Count;
            }
            if (slide is null) return null;
            int index = (int)slide.SlideIndex;
            return new SlideInfo(index, count, NotesOf(slide), show);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidComObjectException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex); // busy with a dialog, closing, or a view without slides: asked again shortly
            return null;
        }
        finally
        {
            if (app is not null) Marshal.FinalReleaseComObject(app);
            Marshal.Release(unknown);
        }
    }

    /// <summary>The text of a slide's notes (its notes page's body placeholder).</summary>
    private static string NotesOf(dynamic slide)
    {
        try
        {
            dynamic placeholders = slide.NotesPage.Shapes.Placeholders;
            int n = (int)placeholders.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic shape = placeholders.Item(i);
                if ((int)shape.PlaceholderFormat.Type != PlaceholderBody || !(bool)(shape.HasTextFrame != 0)) continue;
                return SlideNotes.Tidy((string)shape.TextFrame.TextRange.Text);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return "";
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
