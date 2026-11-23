using System.Drawing.Drawing2D;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenCastPresenter.UI;

/// <summary>
/// The app icon (Assets\app.ico, drawn by tools/make-icon.ps1): for windows' taskbar buttons and,
/// with a status dot, the tray. Loaded once from the embedded resource.
/// </summary>
internal static class AppIcon
{
    private static Icon? _shared;

    /// <summary>All sizes; Windows picks the right one for title bars and taskbar buttons. Don't dispose.</summary>
    public static Icon Shared => _shared ??= Load(Size.Empty);

    /// <summary>The icon at one size (e.g. the tray's small-icon size). The caller owns it.</summary>
    public static Icon Load(Size size)
    {
        using Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ScreenCastPresenter.app.ico")
            ?? throw new InvalidOperationException("The app icon resource is missing.");
        return size.IsEmpty ? new Icon(stream) : new Icon(stream, size);
    }

    /// <summary>
    /// The tray icon: the app icon, plus a dot in the corner while presenting (green) or
    /// recording (red), ringed so it stands out on the blue tile and on any taskbar colour.
    /// </summary>
    public static Icon WithStatus(Color? dot)
    {
        Size size = SystemInformation.SmallIconSize;
        using Icon baseIcon = Load(size);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.DrawIcon(baseIcon, new Rectangle(Point.Empty, size));
            if (dot is { } colour)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = size.Width * 0.5f;
                var r = new RectangleF(size.Width - d, size.Height - d, d, d);
                using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, r);
                r.Inflate(-size.Width / 16f * 1.25f, -size.Width / 16f * 1.25f);
                using var fill = new SolidBrush(colour);
                g.FillEllipse(fill, r);
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
}
