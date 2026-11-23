using System.Drawing.Drawing2D;
using ScreenCastPresenter.Interop;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.UI;

/// <summary>Tray menus in the current light/dark palette, with rounded highlights.</summary>
internal sealed class ThemedMenuRenderer(Palette palette) : ToolStripProfessionalRenderer(new ColourTable(palette))
{
    /// <summary>Rounded corners (Windows 11) and exclusion from capture for a menu or submenu.</summary>
    public static void ApplyPopupChrome(ToolStripDropDown dropDown)
    {
        void Apply()
        {
            var hwnd = (HWND)dropDown.Handle;
            DwmHelpers.ExcludeFromCapture(hwnd);
            DwmHelpers.RoundSmall(hwnd);
        }
        if (dropDown.IsHandleCreated) Apply();
        dropDown.HandleCreated += (_, _) => Apply();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(palette.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(palette.Border);
        Rectangle r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(palette.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Rectangle r = new(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = RoundedRect(r, 4);
        using var brush = new SolidBrush(palette.Raised);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.Text : palette.TextFaint;
        if (e.Item is ToolStripMenuItem && e.TextFormat.HasFlag(TextFormatFlags.Right)) e.TextColor = palette.TextFaint; // shortcut column
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = palette.TextDim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(palette.Border);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(palette.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(pen,
        [
            new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
            new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f),
        ]);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class ColourTable(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Surface;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => p.Raised;
        public override Color ImageMarginGradientBegin => p.Surface;
        public override Color ImageMarginGradientMiddle => p.Surface;
        public override Color ImageMarginGradientEnd => p.Surface;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Border;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
