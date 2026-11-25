using System.Drawing.Imaging;
using System.Drawing.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace ScreenCastPresenter.UI;

/// <summary>Windows 11's materials: Mica for windows that stay open, acrylic for flyouts.</summary>
internal enum BackdropKind { None, Mica, Acrylic }

/// <summary>
/// Windows 11 backdrops (Mica, acrylic) behind parts of SCP's own windows. The backdrop shows
/// only where the client area is transparent, and ordinary WinForms painting (GDI) can't draw
/// transparency correctly: text on it comes out see-through. So, like WinUI apps, the backdrop is
/// used for the title bar, navigation panes and fully custom-drawn surfaces (the quick-actions
/// panel), which paint through <see cref="Paint"/>: drawn with GDI+ into an alpha bitmap, then
/// alpha-blended onto the window. Content with text boxes and labels stays on a solid layer.
/// Windows 10, Windows 11 before 22H2 and High Contrast get solid colours, as before.
/// </summary>
internal static class Backdrop
{
    /// <summary>Windows 11 22H2 (build 22621) added the system backdrop attribute.</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemInformation.HighContrast;

    /// <summary>
    /// Sets the backdrop and makes the given margins of the client area (device pixels)
    /// transparent so it shows there; null makes the whole window transparent. Padding.Empty puts
    /// it behind the title bar only. Returns false where it isn't supported.
    /// </summary>
    public static unsafe bool Apply(HWND hwnd, BackdropKind kind, Padding? glass)
    {
        if (!Supported || kind == BackdropKind.None)
        {
            // Also undoes an earlier backdrop, e.g. when High Contrast is switched on while the
            // window is open. Harmless (fails quietly) on builds without the attribute.
            DWM_SYSTEMBACKDROP_TYPE none = DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE;
            _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &none, (uint)sizeof(DWM_SYSTEMBACKDROP_TYPE));
            var solid = new MARGINS();
            _ = PInvoke.DwmExtendFrameIntoClientArea(hwnd, in solid);
            return false;
        }
        DWM_SYSTEMBACKDROP_TYPE type = kind == BackdropKind.Mica ? DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW : DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW;
        HRESULT hr = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, (uint)sizeof(DWM_SYSTEMBACKDROP_TYPE));
        if (hr.Failed) return false;
        MARGINS margins = glass is { } g
            ? new MARGINS { cxLeftWidth = g.Left, cxRightWidth = g.Right, cyTopHeight = g.Top, cyBottomHeight = g.Bottom }
            : new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        return PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins).Succeeded;
    }

    /// <summary>
    /// Paints <paramref name="draw"/> so it keeps its transparency on a backdrop: into a
    /// premultiplied-alpha bitmap with GDI+ (greyscale anti-aliased text, since ClearType needs
    /// an opaque background), then alpha-blended over cleared (fully transparent) pixels.
    /// With <paramref name="solid"/> set (no backdrop), the area is filled with it first, so the
    /// same drawing code works with and without a backdrop.
    /// </summary>
    public static void Paint(Graphics target, Size size, Color? solid, Action<Graphics> draw)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(solid ?? Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = solid is null ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
            draw(g);
        }

        nint hdc = target.GetHdc();
        HDC memory = HDC.Null;
        nint hbitmap = 0;
        HGDIOBJ previous = default;
        try
        {
            hbitmap = bitmap.GetHbitmap(Color.FromArgb(0)); // keeps the premultiplied alpha
            memory = PInvoke.CreateCompatibleDC((HDC)hdc);
            previous = PInvoke.SelectObject(memory, new HGDIOBJ(hbitmap));
            // Clear to transparent black, then blend: GDI's own fills would leave alpha at 0.
            PInvoke.PatBlt((HDC)hdc, 0, 0, size.Width, size.Height, ROP_CODE.BLACKNESS);
            var blend = new BLENDFUNCTION { BlendOp = (byte)PInvoke.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA };
            PInvoke.AlphaBlend((HDC)hdc, 0, 0, size.Width, size.Height, memory, 0, 0, size.Width, size.Height, blend);
        }
        finally
        {
            if (!memory.IsNull)
            {
                PInvoke.SelectObject(memory, previous);
                PInvoke.DeleteDC(memory);
            }
            if (hbitmap != 0) PInvoke.DeleteObject(new HGDIOBJ(hbitmap));
            target.ReleaseHdc(hdc);
        }
    }

    /// <summary>
    /// Text for <see cref="Paint"/>: GDI+ text, which keeps its alpha (TextRenderer is GDI and doesn't).
    /// </summary>
    public static void Text(Graphics g, string text, Font font, RectangleF bounds, Color colour, bool centre = false, bool wrap = false)
    {
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic) // normal letter spacing, like GDI text
        {
            Alignment = centre ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = (wrap ? 0 : StringFormatFlags.NoWrap) | StringFormatFlags.NoClip,
            HotkeyPrefix = HotkeyPrefix.None,
        };
        g.DrawString(text, font, brush, bounds, format);
    }
}
