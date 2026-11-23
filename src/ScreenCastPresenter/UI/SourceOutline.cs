using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The yellow edge around the presented window on the presenter's screen (FR-17). Drawn as up to
/// four thin click-through strips per screen plus a "Showing on TV" tab — no big transparent
/// bitmap, so it costs almost no memory. Never drawn on the output screen; excluded from capture.
/// </summary>
internal sealed class SourceOutline : IDisposable
{
    private readonly List<OutlineStrip> _strips = [];
    private OutlineTab? _tab;

    /// <summary>Redraws the outline around <paramref name="target"/> (screen pixels), or hides it when null.</summary>
    /// <param name="target">What the audience sees: a window, the cropped part of it, or a screen.</param>
    /// <param name="monitors">All screens (cached by the caller; refreshed on display changes).</param>
    /// <param name="output">The output screen: nothing is ever drawn there.</param>
    public void Update(Rectangle? target, string tabText, IReadOnlyList<DisplayMonitor> monitors, DisplayMonitor? output)
    {
        if (target is not { Width: > 0, Height: > 0 } bounds)
        {
            Hide();
            return;
        }

        var allowed = monitors.Where(m => output is null || !m.IsSameScreen(output)).Select(m => m.Bounds).ToList();
        double scale = monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(bounds))?.Scale ?? 1;
        int thickness = Math.Max(2, (int)Math.Round(3 * scale));

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
    }

    public void Hide()
    {
        foreach (OutlineStrip strip in _strips)
            if (strip.Visible) strip.Hide();
        if (_tab is { Visible: true }) _tab.Hide();
    }

    public void Dispose()
    {
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
