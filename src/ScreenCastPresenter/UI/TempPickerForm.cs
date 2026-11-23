using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.UI;

/// <summary>
/// M1 stand-in for the picker overlay: a plain list of windows, a monitor choice, Show and Stop.
/// Replaced by PickerOverlay in M2.
/// </summary>
internal sealed class TempPickerForm : DarkForm
{
    private readonly SessionController _controller;
    private readonly ListBox _windows = new();
    private readonly ComboBox _monitors = new();
    private readonly Button _show = new() { Text = "Show on TV" };
    private readonly Button _stop = new() { Text = "Stop" };
    private readonly Button _refresh = new() { Text = "Refresh list" };
    private readonly Label _status = new();
    private readonly SolidBrush _surfaceBrush = new(DarkTheme.Surface);
    private readonly SolidBrush _selectedBrush = new(DarkTheme.SurfaceHover);
    private readonly SolidBrush _accentBrush = new(DarkTheme.Accent);

    public TempPickerForm(SessionController controller)
    {
        _controller = controller;

        Text = "ScreenCast Presenter";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(420, 400);
        KeyPreview = true;

        _windows.DrawMode = DrawMode.OwnerDrawFixed;
        _windows.ItemHeight = 44;
        _windows.BorderStyle = BorderStyle.None;
        _windows.BackColor = DarkTheme.Surface;
        _windows.ForeColor = DarkTheme.Text;
        _windows.IntegralHeight = false;
        _windows.Dock = DockStyle.Fill;
        _windows.DrawItem += DrawWindowItem;
        _windows.DoubleClick += async (_, _) => await ShowSelectedAsync();

        _monitors.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitors.FlatStyle = FlatStyle.Flat;
        _monitors.BackColor = DarkTheme.Surface;
        _monitors.ForeColor = DarkTheme.Text;
        _monitors.Dock = DockStyle.Fill;

        DarkTheme.StyleButton(_show, primary: true);
        DarkTheme.StyleButton(_stop, primary: false);
        DarkTheme.StyleButton(_refresh, primary: false);
        _show.Click += async (_, _) => await ShowSelectedAsync();
        _stop.Click += async (_, _) => await _controller.StopAsync();
        _refresh.Click += (_, _) => RefreshWindows();

        _status.AutoSize = true;
        _status.ForeColor = DarkTheme.TextDim;
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(0, 6, 0, 0);

        Controls.Add(BuildLayout());
        AcceptButton = _show;

        _controller.StateChanged += OnSessionStateChanged;
        _controller.Message += OnSessionMessage;
    }

    private TableLayoutPanel BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 6,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 1. heading
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // window list
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // refresh
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 2. heading + monitor
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // status

        layout.Controls.Add(Heading("1. Choose a window to show"));
        layout.Controls.Add(_windows);

        var refreshRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 12) };
        refreshRow.Controls.Add(_refresh);
        layout.Controls.Add(refreshRow);

        var outputRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
        outputRow.Controls.Add(Heading("2. Choose where to show it"));
        outputRow.Controls.Add(_monitors);
        layout.Controls.Add(outputRow);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_show);
        buttons.Controls.Add(_stop);
        layout.Controls.Add(buttons);

        layout.Controls.Add(_status);
        return layout;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = DarkTheme.TitleFont,
        ForeColor = DarkTheme.Text,
        Margin = new Padding(0, 0, 0, 8),
    };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _windows.ItemHeight = LogicalToDeviceUnits(44);
        RefreshWindows();
        RefreshMonitors();
        UpdateButtons();
        _windows.Select();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close(); // SCP keeps running in the tray
        }
        else if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            RefreshWindows();
        }
    }

    private void RefreshWindows()
    {
        var selected = _windows.SelectedItem as SourceWindow;
        _windows.BeginUpdate();
        _windows.Items.Clear();
        foreach (SourceWindow w in WindowEnumerator.GetPresentableWindows())
        {
            int i = _windows.Items.Add(w);
            if (selected is not null && w.Handle == selected.Handle) _windows.SelectedIndex = i;
        }
        if (_windows.SelectedIndex < 0 && _windows.Items.Count > 0) _windows.SelectedIndex = 0;
        _windows.EndUpdate();
    }

    private void RefreshMonitors()
    {
        List<DisplayMonitor> monitors = DisplayHelpers.GetMonitors();
        DisplayMonitor? here = DisplayHelpers.FromWindow(Handle);

        _monitors.Items.Clear();
        foreach (DisplayMonitor m in monitors) _monitors.Items.Add(m);

        // Prefer the current output, then any screen other than the one this picker is on (the TV).
        DisplayMonitor? preferred = _controller.OutputMonitor
            ?? monitors.FirstOrDefault(m => m.DeviceName != here?.DeviceName)
            ?? monitors.FirstOrDefault();
        if (preferred is not null) _monitors.SelectedIndex = monitors.FindIndex(m => m.DeviceName == preferred.DeviceName);

        if (monitors.Count < 2)
            SetStatus("Only one screen found. Connect the TV, or test on this screen (Alt+Tab brings this window back).");
    }

    private async Task ShowSelectedAsync()
    {
        if (_windows.SelectedItem is not SourceWindow source)
        {
            SetStatus("Choose a window first.");
            return;
        }
        if (_monitors.SelectedItem is not DisplayMonitor monitor)
        {
            SetStatus("Choose a screen first.");
            return;
        }
        if (!WindowEnumerator.IsAlive(source.Handle))
        {
            RefreshWindows();
            SetStatus($"“{source.Title}” has closed. Choose another window.");
            return;
        }

        // On the presenter's own screen the output must not sit above everything, or this window
        // (and the Stop button) would be unreachable.
        bool coversThisScreen = monitor.DeviceName == DisplayHelpers.FromWindow(Handle)?.DeviceName;

        _show.Enabled = false;
        try
        {
            await _controller.PresentAsync(source, monitor, topMost: !coversThisScreen);
        }
        finally
        {
            if (!IsDisposed) _show.Enabled = true;
        }

        if (coversThisScreen && !IsDisposed) Activate();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        UpdateButtons();
        switch (_controller.State)
        {
            case SessionState.Presenting:
                SetStatus($"Showing “{_controller.Source?.Title}” on {_controller.OutputMonitor?.Label}.");
                break;
            case SessionState.Off:
                SetStatus("Stopped.");
                break;
        }
    }

    private void OnSessionMessage(object? sender, string message) => SetStatus(message);

    private void UpdateButtons() => _stop.Enabled = _controller.State != SessionState.Off;

    private void SetStatus(string text)
    {
        if (!IsDisposed) _status.Text = text;
    }

    private void DrawWindowItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _windows.Items.Count) return;
        var window = (SourceWindow)_windows.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;

        Graphics g = e.Graphics;
        g.FillRectangle(selected ? _selectedBrush : _surfaceBrush, e.Bounds);
        if (selected)
            g.FillRectangle(_accentBrush, e.Bounds.Left, e.Bounds.Top + 6, LogicalToDeviceUnits(3), e.Bounds.Height - 12);

        int pad = LogicalToDeviceUnits(12);
        int half = e.Bounds.Height / 2;
        var titleRect = new Rectangle(e.Bounds.Left + pad, e.Bounds.Top + 2, e.Bounds.Width - 2 * pad, half);
        var appRect = new Rectangle(titleRect.Left, e.Bounds.Top + half - 2, titleRect.Width, half);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.Bottom;

        TextRenderer.DrawText(g, window.Title, Font, titleRect, DarkTheme.Text, flags);
        string detail = window.IsMinimized ? $"{window.AppName} · minimised (restore it to show it)" : window.AppName;
        TextRenderer.DrawText(g, detail, Font, appRect, DarkTheme.TextDim, flags & ~TextFormatFlags.Bottom | TextFormatFlags.Top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.StateChanged -= OnSessionStateChanged;
            _controller.Message -= OnSessionMessage;
            _surfaceBrush.Dispose();
            _selectedBrush.Dispose();
            _accentBrush.Dispose();
        }
        base.Dispose(disposing);
    }
}
