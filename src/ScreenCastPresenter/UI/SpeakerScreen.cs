using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>What the speaker's screen shows, worked out by the app each half second.</summary>
internal sealed record SpeakerInfo(string Clock, string Talk, int TalkWarning, string Now, string Section, string Next, string NotesTitle, string Notes, string? Timer);

/// <summary>
/// The speaker's screen (FR-124): a screen facing the speaker (a stage monitor, a third screen)
/// showing what the audience sees, the time, how long they've talked and what's left, the part's
/// time budget, what's next, and their notes, in big type. Always dark, like a stage monitor. Hidden
/// from capture like every SCP window, so a meeting never sees it; Esc puts it away.
/// </summary>
internal sealed class SpeakerScreen : ThemedForm
{
    private static readonly Color Back = Color.FromArgb(0x0B, 0x0B, 0x0C);
    private static readonly Color Dim = Color.FromArgb(0x9A, 0x9A, 0xA2);
    private static readonly Color Warn = Color.FromArgb(0xFF, 0xC8, 0x3D);
    private static readonly Color Over = Color.FromArgb(0xFF, 0x6B, 0x6B);

    private readonly ScpApp _app;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private PickerThumbnail? _thumb;
    private nint _thumbSource;
    private SpeakerInfo? _info;
    private Rectangle _tvPane;

    public SpeakerScreen(ScpApp app, DisplayMonitor screen)
    {
        _app = app;
        Text = "ScreenCast Presenter: speaker's screen";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        Screen = screen;
        KeyPreview = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        _refresh.Tick += (_, _) => _app.Guard(() => UpdateFrom(_app));
    }

    /// <summary>The screen it's on.</summary>
    public DisplayMonitor Screen { get; }

    protected override BackdropKind WindowBackdrop => BackdropKind.None;

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _refresh.Start();
        UpdateFrom(_app);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    public void UpdateFrom(ScpApp app)
    {
        if (IsDisposed || !IsHandleCreated) return;
        _info = app.SpeakerInfo();
        (nint output, Size size, _) = app.AudienceView();
        if (output != _thumbSource || _thumb is null && output != 0)
        {
            _thumb?.Dispose();
            _thumb = null;
            _thumbSource = output;
            if (output != 0) _thumb = PickerThumbnail.TryCreate(Handle, output);
        }
        LayoutPanes(size);
        _thumb?.Update(Fit(size, _tvPane), new Rectangle(Point.Empty, size));
        Invalidate();
    }

    private void LayoutPanes(Size tv)
    {
        int pad = ClientSize.Height / 30;
        int width = ClientSize.Width * 45 / 100;
        _tvPane = new Rectangle(pad, ClientSize.Height / 6, width, width * 9 / 16);
        _ = tv;
    }

    private static Rectangle Fit(Size size, Rectangle pane)
    {
        Rectangle fit = AspectFit.Fit(size.Width > 0 && size.Height > 0 ? size : new Size(16, 9), pane.Size);
        fit.Offset(pane.Location);
        return fit;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Back);
        if (_info is not { } info) return;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int h = ClientSize.Height, w = ClientSize.Width, pad = h / 30;
        using var huge = new Font("Segoe UI Semibold", h / 10f, GraphicsUnit.Pixel);
        using var big = new Font("Segoe UI Semibold", h / 22f, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", h / 30f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", h / 42f, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var dim = new SolidBrush(Dim);
        using var talkBrush = new SolidBrush(info.TalkWarning switch { 2 => Over, 1 => Warn, _ => Color.White });
        using var near = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        using var far = new StringFormat { Alignment = StringAlignment.Far };

        g.DrawString(info.Clock, big, dim, pad, pad);
        g.DrawString(info.Talk, huge, talkBrush, new RectangleF(w / 2f, pad / 2f, w / 2f - pad, h / 7f), far);
        if (info.Timer is { } timer) g.DrawString(timer, big, white, new RectangleF(w / 2f, pad + h / 7f, w / 2f - pad, h / 18f), far);

        // What the audience sees, framed.
        using (var frame = new Pen(Color.FromArgb(0x40, 0x40, 0x48), 2)) g.DrawRectangle(frame, _tvPane);
        if (_thumbSource == 0) g.DrawString("Nothing is shown", body, dim, _tvPane, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        float y = _tvPane.Bottom + pad / 2f;
        g.DrawString("NOW", small, dim, pad, y);
        g.DrawString(info.Now, body, white, new RectangleF(pad, y + h / 36f, _tvPane.Width, h / 20f), near);
        if (info.Section.Length > 0) g.DrawString(info.Section, body, talkBrush, new RectangleF(pad, y + h / 36f + h / 20f, _tvPane.Width, h / 20f), near);
        g.DrawString("NEXT", small, dim, pad, y + h / 36f + 2 * h / 20f);
        g.DrawString(info.Next, body, white, new RectangleF(pad, y + 2 * h / 36f + 2 * h / 20f, _tvPane.Width, h / 20f), near);

        // Notes on the right, as big as fits.
        var notes = new RectangleF(_tvPane.Right + 2 * pad, _tvPane.Top, w - _tvPane.Right - 3 * pad, h - _tvPane.Top - pad);
        g.DrawString(info.NotesTitle.Length > 0 ? info.NotesTitle.ToUpperInvariant() : "NOTES", small, dim, notes.X, notes.Y);
        g.DrawString(info.Notes.Length > 0 ? info.Notes : "No notes for what's shown.", info.Notes.Length > 600 ? body : big, info.Notes.Length > 0 ? white : dim,
            new RectangleF(notes.X, notes.Y + h / 30f, notes.Width, notes.Height - h / 30f), near);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _thumb?.Dispose();
        }
        base.Dispose(disposing);
    }
}
