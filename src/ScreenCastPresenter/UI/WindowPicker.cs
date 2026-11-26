using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// "Click a window to show it" (FR-05). An invisible click layer covers the presenter's screens
/// (never the TV); the window under the pointer gets a yellow outline and its name, and a click
/// chooses it. The click goes to the layer, not to the window, so the app never sees it. Esc or a
/// right-click cancels. Like all SCP UI it's excluded from capture.
/// </summary>
internal sealed class WindowPicker : IDisposable
{
    private static readonly TimeSpan TargetsMaxAge = TimeSpan.FromMilliseconds(300);

    private readonly List<ClickLayer> _layers = [];
    private readonly OverlayStrip[] _edge = new OverlayStrip[4];
    private readonly OverlayLabel _label;
    private readonly IReadOnlyList<DisplayMonitor> _screens;
    private readonly double _scale;
    private readonly TaskCompletionSource<SourceWindow?> _result = new();
    private List<PickTarget> _targets = [];
    private DateTime _targetsAt;
    private PickTarget? _hover;
    private bool _finished;
    private Func<SourceWindow, bool> _neverShown = _ => false;

    private WindowPicker(IReadOnlyList<DisplayMonitor> screens)
    {
        _screens = screens;
        _scale = screens.Count > 0 ? screens[0].Scale : 1;
        foreach (DisplayMonitor screen in screens) _layers.Add(new ClickLayer(this, screen.Bounds));
        for (int i = 0; i < 4; i++) _edge[i] = new OverlayStrip(Theme.Current.Outline, 255);
        _label = new OverlayLabel(_scale);
    }

    /// <summary>True while a picker is open, so the shortcut can close it instead of opening a second.</summary>
    public static WindowPicker? Open { get; private set; }

    /// <summary>The window clicked, or null when cancelled. <paramref name="neverShown"/> windows can't be chosen (NeverShow).</summary>
    public static async Task<SourceWindow?> PickAsync(IReadOnlyList<DisplayMonitor> presenterScreens, Func<SourceWindow, bool> neverShown)
    {
        if (presenterScreens.Count == 0) return null;
        using var picker = new WindowPicker(presenterScreens) { _neverShown = neverShown };
        Open = picker;
        try
        {
            picker.Show();
            return await picker._result.Task;
        }
        finally
        {
            Open = null;
        }
    }

    /// <summary>Closes the open picker without choosing anything.</summary>
    public void Cancel() => Finish(null);

    private void Show()
    {
        foreach (ClickLayer layer in _layers) layer.Show();
        _layers[0].Activate();
        Hover(Cursor.Position);
        _label.Show();
    }

    private int Px(int logical) => (int)Math.Round(logical * _scale);

    private IReadOnlyList<PickTarget> Targets()
    {
        if (DateTime.UtcNow - _targetsAt > TargetsMaxAge)
        {
            _targets = WindowEnumerator.GetPickTargets();
            _targetsAt = DateTime.UtcNow;
        }
        return _targets;
    }

    private void Hover(Point at)
    {
        if (_finished) return;
        PickTarget? target = WindowPick.At(Targets(), at);
        bool blocked = target?.Window is { } w && _neverShown(w);
        if (target != _hover)
        {
            _hover = target;
            Outline(blocked ? null : target?.Bounds);
        }
        _label.SetText(blocked ? WindowPick.NeverShownHint(target!.Window!) : WindowPick.Hint(target));
        PlaceLabel(at);
    }

    /// <summary>A yellow edge around the window the click would choose, on the presenter's screens only.</summary>
    private void Outline(Rectangle? bounds)
    {
        if (bounds is not { } b)
        {
            foreach (OverlayStrip e in _edge) e.Visible = false;
            return;
        }
        int t = Math.Max(3, Px(3));
        SetStrip(_edge[0], new Rectangle(b.Left - t, b.Top - t, b.Width + 2 * t, t));
        SetStrip(_edge[1], new Rectangle(b.Left - t, b.Bottom, b.Width + 2 * t, t));
        SetStrip(_edge[2], new Rectangle(b.Left - t, b.Top, t, b.Height));
        SetStrip(_edge[3], new Rectangle(b.Right, b.Top, t, b.Height));
    }

    private void SetStrip(OverlayStrip strip, Rectangle r)
    {
        // Clipped to the presenter's screens: a window reaching onto the TV is outlined only on this side.
        Rectangle clipped = _screens.Select(s => Rectangle.Intersect(s.Bounds, r)).OrderByDescending(c => (long)c.Width * c.Height).FirstOrDefault();
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            strip.Visible = false;
            return;
        }
        strip.Bounds = clipped;
        if (!strip.Visible) strip.Show();
    }

    /// <summary>At the top of the screen the pointer is on, out of the way.</summary>
    private void PlaceLabel(Point at)
    {
        DisplayMonitor screen = _screens.FirstOrDefault(s => s.Bounds.Contains(at)) ?? _screens[0];
        Rectangle area = screen.WorkArea;
        _label.Location = new Point(area.Left + (area.Width - _label.Width) / 2, area.Top + Px(24));
    }

    private void Click(Point at)
    {
        if (_finished) return;
        _targetsAt = default; // fresh: the window may have moved or closed since the last look
        PickTarget? target = WindowPick.At(Targets(), at);
        if (target?.Window is not { } window)
        {
            _label.SetText("That can't be shown. Click a window, or press Esc to cancel");
            PlaceLabel(at);
            return;
        }
        if (_neverShown(window))
        {
            _label.SetText(WindowPick.NeverShownHint(window));
            PlaceLabel(at);
            return;
        }
        Finish(window);
    }

    private void Finish(SourceWindow? window)
    {
        if (_finished) return;
        _finished = true;
        foreach (ClickLayer layer in _layers) layer.Hide();
        foreach (OverlayStrip e in _edge) e.Hide();
        _label.Hide();
        _result.TrySetResult(window);
    }

    private bool IsOwnLayer(HWND hwnd) => _layers.Any(l => l.IsHandleCreated && l.Handle == (nint)hwnd);

    public void Dispose()
    {
        foreach (ClickLayer layer in _layers) layer.Dispose();
        foreach (OverlayStrip e in _edge) e.Dispose();
        _label.Dispose();
    }

    /// <summary>
    /// Covers one screen at alpha 1/255: invisible, but it receives the mouse, so the click never
    /// reaches the window under it (fully transparent layered pixels would let it through).
    /// </summary>
    private sealed class ClickLayer : Form
    {
        private readonly WindowPicker _owner;

        public ClickLayer(WindowPicker owner, Rectangle bounds)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = bounds;
            BackColor = Color.Black;
            KeyPreview = true;
            Cursor = Cursors.Hand;
            Text = "ScreenCast Presenter: click a window";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (int)((WINDOW_EX_STYLE)cp.ExStyle | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var hwnd = (HWND)Handle;
            PInvoke.SetLayeredWindowAttributes(hwnd, default, 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
            DwmHelpers.ExcludeFromCapture(hwnd);
            DwmHelpers.MakeBareSurface(hwnd);
        }

        private Point ToScreen(Point client) => new(client.X + Left, client.Y + Top);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _owner.Hover(ToScreen(e.Location));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _owner.Click(ToScreen(e.Location));
            else _owner.Finish(null); // right-click cancels, like Windows' own pickers
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                _owner.Finish(null);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Moving to another of our layers (a second screen) is fine; switching to an app cancels.
            BeginInvoke(() =>
            {
                HWND foreground = PInvoke.GetForegroundWindow();
                if (!_owner.IsOwnLayer(foreground)) _owner.Finish(null);
            });
        }
    }
}
