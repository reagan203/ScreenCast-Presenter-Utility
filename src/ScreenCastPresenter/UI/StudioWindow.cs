using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>
/// A small private window on the presenter's screen (FR-75, FR-76). Left: exactly what the
/// audience sees, live: a copy of the TV's own window, so blank, freeze, the countdown, drawings,
/// hidden parts and the small picture all show as they are. Right: what's next, made ready
/// privately, shown with Go (one key). Hidden from capture like every SCP window, so it never
/// reaches the TV or a meeting.
/// </summary>
internal sealed class StudioWindow : ThemedForm
{
    private readonly ScpApp _app;
    private readonly Label _tvTitle = new() { AutoSize = true, Tag = "dim", UseMnemonic = false, Text = "WHAT THE AUDIENCE SEES" };
    private readonly Label _tvState = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label _nextTitle = new() { AutoSize = true, Tag = "dim", UseMnemonic = false, Text = "NEXT" };
    private readonly Label _nextState = new() { AutoSize = true, UseMnemonic = false };
    private readonly PillButton _ready = new("Get something ready…");
    private readonly PillButton _go = new("Show it now", PillKind.Primary);
    private readonly PillButton _clear = new("Clear");
    /// <summary>How often it checks the TV's window and what's next (the copies themselves are live).</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(500);

    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    private PickerThumbnail? _tvThumb, _nextThumb;
    private nint _tvSource, _nextSource;
    private Rectangle _tvPane, _nextPane;
    private string _tvMessage = "", _nextMessage = "";

    public StudioWindow(ScpApp app)
    {
        _app = app;
        Text = "ScreenCast Presenter: what the audience sees";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(280));
        ClientSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(330));
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        foreach (PillButton b in new[] { _ready, _go, _clear })
        {
            b.Radius = 10;
            b.Size = b.Measure();
            Controls.Add(b);
        }
        _ready.AccessibleName = "Get something ready to show next";
        _go.AccessibleName = "Show what's ready now";
        _clear.AccessibleName = "Clear what's ready";
        _ready.Click += (_, _) => _ = _app.RunAsync("get-ready");
        _go.Click += (_, _) => _ = _app.RunAsync("go");
        _clear.Click += (_, _) => _app.Guard(_app.ClearNext);
        Controls.AddRange([_tvTitle, _tvState, _nextTitle, _nextState]);
        _refresh.Tick += (_, _) => _app.Guard(() => UpdateFrom(_app));
        ApplyPalette(Theme.Current);
    }

    protected override BackdropKind WindowBackdrop => BackdropKind.None;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _refresh.Start();
    }

    /// <summary>Takes what's on the TV and what's next from the app: the copies follow.</summary>
    public void UpdateFrom(ScpApp app)
    {
        if (IsDisposed || !IsHandleCreated) return;
        (nint output, Size outputSize, string state) = app.AudienceView();
        _tvState.Text = state;
        _tvMessage = output == 0 ? "Nothing is on the TV" : "";
        SetThumbnail(ref _tvThumb, ref _tvSource, output);
        Size tvSize = outputSize;

        (SourceWindow? next, Rectangle nextPart) = app.NextView();
        _nextState.Text = next is null ? "Nothing ready" : TrayMenuPlan.Shorten(next.Screen is { } s ? s.Name : next.Title.Length > 0 ? next.Title : next.AppName, 48);
        _nextMessage = next is null ? $"Get something ready{KeyText(app, "get-ready")}: you see it here, the audience doesn't."
            : next.IsWindow ? "" : next.IsCamera ? "A camera: its picture shows when it's on the TV." : "A whole screen: it shows as it is when it's on the TV.";
        SetThumbnail(ref _nextThumb, ref _nextSource, next is { IsWindow: true } w ? w.Handle : 0);
        _go.Enabled = next is not null;
        _clear.Enabled = next is not null;
        _go.Text = next is null ? "Show it now" : $"Show it now{KeyText(app, "go", brackets: true)}";
        _go.Size = _go.Measure();
        LayoutPanes();
        _tvThumb?.Update(FitIn(tvSize, _tvPane), new Rectangle(Point.Empty, tvSize));
        _nextThumb?.Update(FitIn(nextPart.Size, _nextPane), nextPart);
        Invalidate();
    }

    /// <summary>Where a picture of <paramref name="size"/> goes in <paramref name="pane"/>: as big as fits, centred.</summary>
    private static Rectangle FitIn(Size size, Rectangle pane)
    {
        Rectangle fit = AspectFit.Fit(size.Width > 0 && size.Height > 0 ? size : new Size(16, 9), pane.Size);
        fit.Offset(pane.Location);
        return fit;
    }

    private static string KeyText(ScpApp app, string action, bool brackets = false) =>
        app.ActiveShortcut(action) is { } key ? brackets ? $"  ({key})" : $" ({key})" : "";

    private void SetThumbnail(ref PickerThumbnail? thumb, ref nint current, nint source)
    {
        if (source == current && (thumb is not null || source == 0)) return;
        thumb?.Dispose();
        thumb = null;
        current = source;
        if (source != 0) thumb = PickerThumbnail.TryCreate(Handle, source);
    }

    private void LayoutPanes()
    {
        int pad = LogicalToDeviceUnits(16), gap = LogicalToDeviceUnits(16);
        int top = pad + _tvTitle.Height + _tvState.Height + LogicalToDeviceUnits(10);
        int buttonsHeight = _go.Height + LogicalToDeviceUnits(14);
        int width = (ClientSize.Width - pad * 2 - gap) / 2;
        int height = Math.Max(LogicalToDeviceUnits(60), ClientSize.Height - top - buttonsHeight - pad);
        _tvPane = new Rectangle(pad, top, width, height);
        _nextPane = new Rectangle(pad + width + gap, top, width, height);
        _tvTitle.Location = new Point(pad, pad);
        _tvState.Location = new Point(pad, pad + _tvTitle.Height + LogicalToDeviceUnits(2));
        _nextTitle.Location = new Point(_nextPane.Left, pad);
        _nextState.Location = new Point(_nextPane.Left, _tvState.Top);
        int y = ClientSize.Height - pad - _go.Height;
        _ready.Location = new Point(_nextPane.Left, y);
        _go.Location = new Point(_nextPane.Right - _go.Width, y);
        _clear.Location = new Point(_go.Left - _clear.Width - LogicalToDeviceUnits(8), y);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) UpdateFrom(_app);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Palette p = Theme.Current;
        Graphics g = e.Graphics;
        Draw.HighQuality(g);
        foreach ((Rectangle pane, string message) in new[] { (_tvPane, _tvMessage), (_nextPane, _nextMessage) })
        {
            if (pane.Width <= 0) continue;
            Draw.Fill(g, Theme.OutputBlack, pane, LogicalToDeviceUnits(6));
            Draw.Stroke(g, p.Border, 1, pane, LogicalToDeviceUnits(6));
            if (message.Length > 0)
                TextRenderer.DrawText(g, message, Font, Rectangle.Inflate(pane, -LogicalToDeviceUnits(16), 0), Color.FromArgb(0x9A, 0x9A, 0xA2),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    protected override void ApplyPalette(Palette palette)
    {
        base.ApplyPalette(palette);
        foreach (Control c in Controls)
        {
            if (c is PillButton) continue;
            c.BackColor = palette.Background;
            c.ForeColor = c.Tag is "dim" ? palette.TextDim : palette.Text;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _tvThumb?.Dispose();
            _nextThumb?.Dispose();
        }
        base.Dispose(disposing);
    }
}
