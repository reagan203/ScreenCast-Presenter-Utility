using System.Drawing.Drawing2D;
using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Minimal tray icon for M1 so SCP is always one click away: left-click opens the picker,
/// right-click shows a short menu. M2 adds the full menu from SRS 6.1.
/// The picker is created on demand and disposed when closed, to keep idle memory low.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly SessionController _controller;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _stopItem;
    private readonly Icon _idleIcon;
    private readonly Icon _presentingIcon;
    private TempPickerForm? _picker;

    public event EventHandler? QuitRequested;

    public TrayIcon(SessionController controller)
    {
        _controller = controller;
        _idleIcon = DrawIcon(presenting: false);
        _presentingIcon = DrawIcon(presenting: true);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Show a window…", null, (_, _) => ShowPicker());
        _stopItem = new ToolStripMenuItem("Stop showing", null, async (_, _) => await _controller.StopAsync());
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));
        _menu.Opening += (_, _) => _stopItem.Enabled = _controller.State != SessionState.Off;

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "ScreenCast Presenter",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowPicker();
        };

        _controller.StateChanged += OnStateChanged;
    }

    public void ShowPicker()
    {
        if (_picker is { IsDisposed: false })
        {
            if (_picker.WindowState == FormWindowState.Minimized) _picker.WindowState = FormWindowState.Normal;
            _picker.Activate();
            return;
        }
        _picker = new TempPickerForm(_controller);
        _picker.FormClosed += (_, _) =>
        {
            _picker?.Dispose();
            _picker = null;
        };
        _picker.Show();
        _picker.Activate();
    }

    public void ClosePicker() => _picker?.Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool presenting = _controller.State == SessionState.Presenting;
        _notifyIcon.Icon = presenting ? _presentingIcon : _idleIcon;
        string text = presenting ? $"ScreenCast Presenter: showing {_controller.Source?.Title}" : "ScreenCast Presenter";
        _notifyIcon.Text = text.Length > 127 ? text[..126] + "…" : text; // tooltip limit
    }

    /// <summary>A small screen outline in grey; an accent dot is added while presenting (SRS 6.1).</summary>
    private static Icon DrawIcon(bool presenting)
    {
        Size size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size.Width / 16f;
            using var pen = new Pen(Color.FromArgb(0xB4, 0xB4, 0xBC), 1.5f * s);
            g.DrawRectangle(pen, 1.5f * s, 2.5f * s, 13f * s, 8.5f * s);
            g.DrawLine(pen, 5f * s, 14f * s, 11f * s, 14f * s);
            if (presenting)
            {
                using var dot = new SolidBrush(DarkTheme.Accent);
                g.FillEllipse(dot, 9f * s, 7.5f * s, 7f * s, 7f * s);
            }
        }

        // GetHicon creates an unmanaged icon we must destroy; clone it into a managed Icon first.
        nint hicon = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(hicon);
            return (Icon)temporary.Clone();
        }
        finally
        {
            PInvoke.DestroyIcon((HICON)hicon);
        }
    }

    public void Dispose()
    {
        _controller.StateChanged -= OnStateChanged;
        _picker?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _presentingIcon.Dispose();
    }
}
