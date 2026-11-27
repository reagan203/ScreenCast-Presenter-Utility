using ScreenCastPresenter.Capture;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The whiteboard's private window (FR-117): the board as the audience sees it, on the presenter's
/// own screen, to draw on. While drawing, the drawing layer covers its board; between drawing
/// sessions it shows the drawing as it stays on the TV. Hidden from capture like every SCP window.
/// </summary>
internal sealed class BoardWindow : ThemedForm
{
    private readonly ScpApp _app;
    private readonly Func<IReadOnlyList<InkStroke>> _strokes;
    private readonly PillButton _draw = new("Draw", PillKind.Primary);
    private readonly PillButton _clear = new("Clear");
    private readonly PillButton _save = new("Save as a picture");
    private readonly PillButton _close = new("Put the board away");
    private bool _light;
    private bool _closingFromApp;

    /// <summary>The app puts the board away: closes without asking the app to do it again.</summary>
    public void CloseFromApp()
    {
        _closingFromApp = true;
        Close();
    }

    public BoardWindow(ScpApp app, Func<IReadOnlyList<InkStroke>> strokes, bool light)
    {
        _app = app;
        _strokes = strokes;
        _light = light;
        Text = "ScreenCast Presenter: whiteboard";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(330));
        ClientSize = new Size(LogicalToDeviceUnits(960), LogicalToDeviceUnits(600));
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        foreach (PillButton b in new[] { _draw, _clear, _save, _close })
        {
            b.Radius = 10;
            b.Size = b.Measure();
            Controls.Add(b);
        }
        _draw.AccessibleName = "Draw on the board";
        _draw.Click += (_, _) => _ = _app.RunAsync("draw");
        _clear.Click += (_, _) => _app.Guard(_app.ClearBoard);
        _save.Click += (_, _) => _app.Guard(_app.SaveBoard);
        _close.Click += (_, _) => _app.Guard(_app.PutBoardAway);
        ApplyPalette(Theme.Current);
    }

    protected override BackdropKind WindowBackdrop => BackdropKind.None;

    /// <summary>The board's area on screen (physical pixels): what drawing is measured against.</summary>
    public Rectangle BoardOnScreen => IsDisposed || !IsHandleCreated || WindowState == FormWindowState.Minimized ? Rectangle.Empty : RectangleToScreen(BoardArea());

    public void SetLight(bool light)
    {
        _light = light;
        Invalidate();
    }

    /// <summary>The drawing changed (cleared, a session ended): show it as the TV does.</summary>
    public void ShowDrawing(bool drawing)
    {
        _draw.Text = drawing ? "Stop drawing" : "Draw";
        _draw.Size = _draw.Measure();
        LayoutButtons();
        Invalidate();
    }

    private Rectangle BoardArea()
    {
        int pad = LogicalToDeviceUnits(12);
        int bar = _draw.Height + 2 * pad;
        var room = new Rectangle(pad, pad, Math.Max(1, ClientSize.Width - 2 * pad), Math.Max(1, ClientSize.Height - bar - pad));
        Rectangle fit = AspectFit.Fit(new Size(Whiteboard.Width, Whiteboard.Height), room.Size);
        fit.Offset(room.Location);
        return fit;
    }

    private void LayoutButtons()
    {
        int pad = LogicalToDeviceUnits(12), gap = LogicalToDeviceUnits(8);
        int y = ClientSize.Height - pad - _draw.Height;
        int x = pad;
        foreach (PillButton b in new[] { _draw, _clear, _save })
        {
            b.Location = new Point(x, y);
            x += b.Width + gap;
        }
        _close.Location = new Point(ClientSize.Width - pad - _close.Width, y);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
        _app.Guard(_app.BoardMoved);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _app.Guard(_app.BoardMoved);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle board = BoardArea();
        using (var fill = new SolidBrush(Whiteboard.Board(_light))) e.Graphics.FillRectangle(fill, board);
        Whiteboard.DrawStrokes(e.Graphics, board, _strokes());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!_closingFromApp && e.CloseReason == CloseReason.UserClosing) _app.Guard(_app.PutBoardAway); // the presenter closed it: that puts the board away
    }
}
