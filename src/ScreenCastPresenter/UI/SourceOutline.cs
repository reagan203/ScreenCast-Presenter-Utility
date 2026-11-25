using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The yellow edge around the presented window on the presenter's screen (FR-17). Drawn as up to
/// four thin click-through strips per screen plus a "Showing on TV" tab — no big transparent
/// bitmap, so it costs almost no memory. Never drawn on the output screen; excluded from capture.
/// Around a window it is stacked directly above that window rather than above everything, so
/// windows in front of the presented one cover its outline too: it marks only that window. When
/// the presented window comes to the front, the outline follows it. Around a whole screen it stays
/// on top, as it frames everything.
/// </summary>
internal sealed class SourceOutline : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private static readonly HWND Top = (HWND)(nint)0, TopMost = (HWND)(nint)(-1), NoTopMost = (HWND)(nint)(-2);

    private readonly List<OutlineStrip> _strips = [];
    private OutlineTab? _tab;
    private HWND _stackAbove;
    private UnhookWinEventSafeHandle? _foregroundHook;
    private WINEVENTPROC? _foregroundCallback; // must stay referenced while the hook exists

    /// <summary>Line thickness in device pixels: 2 at 100% scaling, never less than 1.</summary>
    public static int Thickness(double scale) => Math.Max(1, (int)Math.Round(2 * scale));

    /// <summary>Redraws the outline around <paramref name="target"/> (screen pixels), or hides it when null.</summary>
    /// <param name="target">What the audience sees: a window, the cropped part of it, or a screen.</param>
    /// <param name="monitors">All screens (cached by the caller; refreshed on display changes).</param>
    /// <param name="output">The output screen: nothing is ever drawn there.</param>
    /// <param name="window">The presented window (0 for a whole screen): the outline is stacked just above it.</param>
    public void Update(Rectangle? target, string tabText, IReadOnlyList<DisplayMonitor> monitors, DisplayMonitor? output, nint window = 0)
    {
        if (target is not { Width: > 0, Height: > 0 } bounds)
        {
            Hide();
            return;
        }

        var allowed = monitors.Where(m => output is null || !m.IsSameScreen(output)).Select(m => m.Bounds).ToList();
        double scale = monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(bounds))?.Scale ?? 1;
        int thickness = Thickness(scale);

        List<Rectangle> rects = OutlineGeometry.Strips(bounds, thickness, allowed);
        Color colour = Theme.Current.Outline;
        while (_strips.Count < rects.Count) _strips.Add(new OutlineStrip());
        for (int i = 0; i < _strips.Count; i++)
        {
            OutlineStrip strip = _strips[i];
            if (i < rects.Count)
            {
                strip.BackColor = colour;
                strip.Bounds = rects[i];
                if (!strip.Visible) strip.Show();
            }
            else if (strip.Visible)
            {
                strip.Hide();
            }
        }

        _tab ??= new OutlineTab();
        _tab.SetText(tabText, scale, colour);
        Rectangle? tabRect = OutlineGeometry.Tab(bounds, _tab.PreferredSize, thickness, (int)(12 * scale), allowed);
        if (tabRect is { } r)
        {
            _tab.Bounds = r;
            if (!_tab.Visible) _tab.Show();
        }
        else if (_tab.Visible)
        {
            _tab.Hide();
        }

        _stackAbove = (HWND)window;
        if (window != 0) WatchForeground();
        else StopWatchingForeground();
        Restack();
    }

    public void Hide()
    {
        foreach (OutlineStrip strip in _strips)
            if (strip.Visible) strip.Hide();
        if (_tab is { Visible: true }) _tab.Hide();
        StopWatchingForeground();
    }

    private IEnumerable<Form> Overlays => _strips.Where(s => s.Visible).Cast<Form>().Concat(_tab is { Visible: true } tab ? [tab] : []);

    /// <summary>
    /// Puts the outline directly above the presented window (or on top of everything, for a whole
    /// screen). Windows in front of the presented one then cover the outline as well.
    /// </summary>
    private void Restack()
    {
        const SET_WINDOW_POS_FLAGS Flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        var overlays = Overlays.Select(o => (HWND)o.Handle).ToList();
        if (overlays.Count == 0) return;
        if (_stackAbove.IsNull || !PInvoke.IsWindow(_stackAbove))
        {
            foreach (HWND overlay in overlays) PInvoke.SetWindowPos(overlay, TopMost, 0, 0, 0, 0, Flags);
            return;
        }

        bool sourceOnTop = IsTopMost(_stackAbove); // an "always on top" app: the outline has to be too
        HWND above = PInvoke.GetWindow(_stackAbove, GET_WINDOW_CMD.GW_HWNDPREV);
        while (!above.IsNull && overlays.Contains(above)) above = PInvoke.GetWindow(above, GET_WINDOW_CMD.GW_HWNDPREV);
        // Directly above the source = just below the window above it. If that one is "always on
        // top" (or there is none), the source leads its band, so the outline goes to the top of it.
        HWND insertAfter = above.IsNull || IsTopMost(above) != sourceOnTop ? (sourceOnTop ? TopMost : Top) : above;
        foreach (HWND overlay in overlays)
        {
            if (!sourceOnTop && IsTopMost(overlay)) PInvoke.SetWindowPos(overlay, NoTopMost, 0, 0, 0, 0, Flags);
            PInvoke.SetWindowPos(overlay, insertAfter, 0, 0, 0, 0, Flags);
        }
    }

    private static bool IsTopMost(HWND hwnd) =>
        ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);

    /// <summary>
    /// Clicking the presented window brings it to the front, above its outline: put the outline back
    /// above it. Any window coming to the front is checked, which is a handful of calls; callbacks
    /// arrive on the UI thread and nothing is injected into other apps.
    /// </summary>
    private void WatchForeground()
    {
        if (_foregroundHook is not null) return;
        _foregroundCallback = (_, _, _, _, _, _, _) => Restack();
        UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(EventSystemForeground, EventSystemForeground, default, _foregroundCallback,
            0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid) hook.Dispose();
        else _foregroundHook = hook;
    }

    private void StopWatchingForeground()
    {
        _foregroundHook?.Dispose();
        _foregroundHook = null;
    }

    public void Dispose()
    {
        StopWatchingForeground();
        foreach (OutlineStrip strip in _strips) strip.Dispose();
        _strips.Clear();
        _tab?.Dispose();
    }

    private sealed class OutlineStrip : OverlayForm
    {
        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);
    }

    private sealed class OutlineTab : OverlayForm
    {
        private string _text = "";
        private Font? _font;
        private double _scale;

        public new Size PreferredSize { get; private set; }

        public void SetText(string text, double scale, Color colour)
        {
            BackColor = colour;
            if (text == _text && Math.Abs(scale - _scale) < 0.01 && _font is not null) return;
            _text = text;
            _scale = scale;
            _font?.Dispose();
            _font = Theme.PixelFont(12, scale, FontStyle.Bold);
            Size textSize = TextRenderer.MeasureText(text, _font);
            PreferredSize = new Size(textSize.Width + (int)(24 * scale), (int)(24 * scale));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_font is null) return;
            // Dark text on yellow in both themes: high contrast.
            TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, Theme.OutputBlack,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font?.Dispose();
            base.Dispose(disposing);
        }
    }
}
