using System.Drawing.Drawing2D;
using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// Tray icon and menu (SRS 6.1): left-click opens the picker, right-click shows the menu. The menu
/// is rebuilt each time it opens, so it always reflects the current state, theme and shortcuts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ScpApp _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _idleIcon;
    private readonly Icon _presentingIcon;

    public TrayIcon(ScpApp app)
    {
        _app = app;
        _idleIcon = DrawIcon(presenting: false);
        _presentingIcon = DrawIcon(presenting: true);

        _menu = new ContextMenuStrip { Font = Theme.UiFont, ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, _) => BuildMenu();
        ThemedMenuRenderer.ApplyPopupChrome(_menu);

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "ScreenCast Presenter",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.OpenPicker();
        };
    }

    /// <summary>Updates icon and tooltip after a state change.</summary>
    public void Refresh()
    {
        SessionController s = _app.Session;
        bool presenting = s.State == SessionState.Presenting;
        _notifyIcon.Icon = presenting ? _presentingIcon : _idleIcon;
        string text = s.State switch
        {
            SessionState.Presenting when s.IsBlanked => "ScreenCast Presenter: TV is blank",
            SessionState.Presenting => $"ScreenCast Presenter: showing {s.Source?.Title}",
            SessionState.IdleScreen => "ScreenCast Presenter: TV shows a blank screen",
            _ => "ScreenCast Presenter",
        };
        _notifyIcon.Text = text.Length > 127 ? text[..126] + "…" : text; // tooltip limit
    }

    private void BuildMenu()
    {
        Palette p = Theme.Current;
        _menu.Renderer = new ThemedMenuRenderer(p);
        _menu.BackColor = p.Surface;
        _menu.ForeColor = p.Text;
        // Items.Clear() only detaches items; dispose them so the old submenus (real windows) are released.
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _menu.Items.Clear();
        SessionController s = _app.Session;

        var status = new ToolStripLabel(StatusText(s)) { ForeColor = p.TextDim, Padding = new Padding(4, 6, 4, 6) };
        _menu.Items.Add(status);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Present…", "picker", _app.OpenPicker));
        _menu.Items.Add(Item("Command palette…", "palette", _app.OpenPalette));
        var blank = Item(s.IsBlanked ? "Show the TV again" : "Blank the TV", "blank", () => _ = _app.RunAsync("blank"));
        blank.Enabled = s.State != SessionState.Off;
        _menu.Items.Add(blank);
        var stop = Item("Stop showing", "stop", () => _ = _app.RunAsync("stop"));
        stop.Enabled = s.State != SessionState.Off;
        _menu.Items.Add(stop);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(FavouritesMenu());
        _menu.Items.Add(OutputMenu());
        _menu.Items.Add(DimMenu(s));
        _menu.Items.Add(AppearanceMenu());
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Keyboard shortcuts…", "cheat-sheet", _app.OpenShortcuts));
        _menu.Items.Add(Check("Pause all shortcuts", _app.ShortcutsPaused, "pause-shortcuts", _app.TogglePauseShortcuts));
        _menu.Items.Add(Check("Show yellow outline", _app.Settings.ShowOutline, "toggle-outline", () => _app.SetShowOutline(!_app.Settings.ShowOutline)));
        _menu.Items.Add(Check("Show presenting badge", _app.Settings.ShowBadge, "toggle-badge", () => _app.SetShowBadge(!_app.Settings.ShowBadge)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", null, _app.RequestQuit));
    }

    private static string StatusText(SessionController s) => s.State switch
    {
        SessionState.Presenting when s.IsBlanked => "● TV is blank",
        SessionState.Presenting => $"● Showing on {s.OutputMonitor?.ShortName}: {Shorten(s.Source?.Title)}",
        SessionState.IdleScreen => "○ TV shows a blank screen",
        _ => "○ Not showing anything",
    };

    private static string Shorten(string? text) => text is null ? "" : text.Length > 40 ? text[..39] + "…" : text;

    private ToolStripMenuItem Item(string text, string? actionId, Action onClick)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => _app.Guard(onClick));
        if (actionId is not null && _app.ActiveShortcut(actionId) is { } hotkey) item.ShortcutKeyDisplayString = hotkey.ToString();
        return item;
    }

    private ToolStripMenuItem Check(string text, bool isChecked, string actionId, Action onClick)
    {
        ToolStripMenuItem item = Item(text, actionId, onClick);
        item.Checked = isChecked;
        return item;
    }

    private ToolStripMenuItem Submenu(string text)
    {
        var menu = new ToolStripMenuItem(text);
        menu.DropDown.BackColor = Theme.Current.Surface;
        menu.DropDown.Font = Theme.UiFont;
        ThemedMenuRenderer.ApplyPopupChrome(menu.DropDown);
        return menu;
    }

    private ToolStripMenuItem FavouritesMenu()
    {
        ToolStripMenuItem menu = Submenu("Favourites");
        for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
        {
            int s = slot;
            Favourite? f = _app.Settings.Favourites[slot - 1];
            ToolStripMenuItem item = f is null
                ? new ToolStripMenuItem($"{slot}   Empty: save with {_app.ActiveShortcut(ShortcutCatalog.SaveFavouriteId(slot))?.ToString() ?? "the palette"}") { Enabled = false }
                : Item($"{slot}   {Shorten(f.Title)} · {f.AppName}", ShortcutCatalog.ShowFavouriteId(slot), () => _ = _app.RunAsync(ShortcutCatalog.ShowFavouriteId(s)));
            menu.DropDownItems.Add(item);
        }
        if (_app.Session.Source is not null)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem save = Submenu("Save what's showing as");
            for (int slot = 1; slot <= AppSettings.SlotCount; slot++)
            {
                int s = slot;
                save.DropDownItems.Add(Item($"Favourite {slot}", ShortcutCatalog.SaveFavouriteId(slot), () => _ = _app.RunAsync(ShortcutCatalog.SaveFavouriteId(s))));
            }
            menu.DropDownItems.Add(save);
        }
        return menu;
    }

    private ToolStripMenuItem OutputMenu()
    {
        ToolStripMenuItem menu = Submenu("Show on");
        Interop.DisplayMonitor? chosen = _app.ChosenOutput;
        foreach (Interop.DisplayMonitor m in _app.Monitors)
        {
            Interop.DisplayMonitor monitor = m;
            ToolStripMenuItem item = Item(m.Label, null, () => _app.SetOutputMonitor(monitor));
            item.Checked = chosen is not null && m.IsSameScreen(chosen);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ToolStripMenuItem DimMenu(SessionController s)
    {
        ToolStripMenuItem menu = Submenu("Dim the TV");
        foreach (int percent in new[] { 0, 10, 20, 30, 40 })
        {
            int value = percent;
            ToolStripMenuItem item = Item(percent == 0 ? "Off" : $"{percent}%", null, () => _app.SetDim(value / 100.0));
            item.Checked = Math.Abs(s.Dim - percent / 100.0) < 0.001;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ToolStripMenuItem AppearanceMenu()
    {
        ToolStripMenuItem menu = Submenu("Appearance");
        foreach ((ThemeMode mode, string label) in new[] { (ThemeMode.System, "Match Windows"), (ThemeMode.Dark, "Dark"), (ThemeMode.Light, "Light") })
        {
            ThemeMode m = mode;
            ToolStripMenuItem item = Item(label, null, () => _app.SetTheme(m));
            item.Checked = Theme.Mode == mode;
            menu.DropDownItems.Add(item);
        }
        return menu;
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
                using var dot = new SolidBrush(Theme.Dark.Accent);
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _presentingIcon.Dispose();
    }
}
