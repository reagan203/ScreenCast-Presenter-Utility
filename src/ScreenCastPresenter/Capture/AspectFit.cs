using System.Drawing;

namespace ScreenCastPresenter.Capture;

internal static class AspectFit
{
    /// <summary>
    /// Largest rectangle with the source's aspect ratio that fits inside <paramref name="destination"/>,
    /// centred, leaving black bars on two sides (FR-10). Empty if either size is empty.
    /// </summary>
    public static Rectangle Fit(Size source, Size destination)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
            return Rectangle.Empty;

        // Compare aspect ratios by cross-multiplying, so equal ratios are detected exactly.
        long sw = source.Width, sh = source.Height, dw = destination.Width, dh = destination.Height;
        int width, height;
        if (sw * dh >= dw * sh)
        {
            // Source is wider (or equal): fill the width, bars top and bottom.
            width = destination.Width;
            height = (int)Math.Round((double)dw * sh / sw);
        }
        else
        {
            // Source is taller: fill the height, bars left and right.
            height = destination.Height;
            width = (int)Math.Round((double)dh * sw / sh);
        }

        return new Rectangle((destination.Width - width) / 2, (destination.Height - height) / 2, width, height);
    }
}
