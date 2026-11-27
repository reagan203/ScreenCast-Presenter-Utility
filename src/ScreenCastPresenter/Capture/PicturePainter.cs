using System.Numerics;
using ScreenCastPresenter.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Draws a picture onto the TV's canvas (render thread): through the night filter when it's on
/// (FR-123), and the small picture in its shape (FR-118). Its Direct2D effects are made once and
/// reused every frame. Used by the engine and, unchanged, by the GPU tests.
/// </summary>
internal sealed class PicturePainter(ID2D1DeviceContext dc, ID2D1Factory1 factory) : IDisposable
{
    private Vortice.Direct2D1.Effects.ColorMatrix? _matrix;
    private Vortice.Direct2D1.Effects.TableTransfer? _table;

    /// <summary>
    /// <paramref name="part"/> of <paramref name="bitmap"/> into <paramref name="dest"/> at
    /// <paramref name="opacity"/> (over the TV's black), toned by <paramref name="night"/>.
    /// </summary>
    public void Draw(ID2D1Bitmap1 bitmap, Rectangle part, RectangleF dest, double opacity, NightMode night)
    {
        if (dest.Width <= 0 || dest.Height <= 0 || part.Width <= 0 || part.Height <= 0 || opacity <= 0.001) return;
        if (night == NightMode.Off)
        {
            dc.DrawBitmap(bitmap, new Rect(dest.X, dest.Y, dest.Width, dest.Height), (float)opacity, InterpolationMode.HighQualityCubic, new Rect(part.X, part.Y, part.Width, part.Height), null);
            return;
        }
        using ID2D1Image image = NightImage(bitmap, night, (float)opacity);
        Matrix3x2 before = dc.Transform;
        float sx = dest.Width / part.Width, sy = dest.Height / part.Height;
        dc.Transform = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(dest.X, dest.Y) * before;
        try
        {
            dc.DrawImage(image, Vector2.Zero, new Vortice.RawRectF(part.Left, part.Top, part.Right, part.Bottom), InterpolationMode.HighQualityCubic, CompositeMode.SourceOver);
        }
        finally
        {
            dc.Transform = before;
        }
    }

    private ID2D1Image NightImage(ID2D1Bitmap1 bitmap, NightMode night, float opacity)
    {
        _matrix ??= new Vortice.Direct2D1.Effects.ColorMatrix(dc);
        float[] m = NightFilter.Matrix(night, opacity);
        _matrix.Matrix = new Matrix5x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15], m[16], m[17], m[18], m[19]);
        if (night == NightMode.Soften)
        {
            if (_table is null)
            {
                _table = new Vortice.Direct2D1.Effects.TableTransfer(dc);
                float[] curve = [.. NightFilter.SoftenCurve];
                _table.RedTable = curve;
                _table.GreenTable = curve;
                _table.BlueTable = curve;
                _table.AlphaDisable = true;
                _table.ClampOutput = true;
            }
            _table.SetInput(0, bitmap, true);
            using ID2D1Image softened = _table.Output;
            _matrix.SetInput(0, softened, true);
        }
        else _matrix.SetInput(0, bitmap, true);
        return _matrix.Output;
    }

    /// <summary>
    /// The small picture in <paramref name="shape"/> at <paramref name="place"/>, showing
    /// <paramref name="shown"/> of <paramref name="bitmap"/>; <paramref name="covers"/> draws its covers
    /// (inside the same mask, so nothing hidden shows at a curved edge). The faint edge goes round its shape.
    /// </summary>
    public void DrawShaped(ID2D1Bitmap1 bitmap, Rectangle shown, RectangleF place, double opacity, InsetShape shape, ID2D1SolidColorBrush edgeBrush, float edge, Action? covers)
    {
        var dest = new Rect(place.X, place.Y, place.Width, place.Height);
        float radius = InsetShapes.Radius(place, shape);
        edgeBrush.Opacity = (float)opacity;
        if (radius <= 0)
        {
            dc.DrawBitmap(bitmap, dest, (float)opacity, InterpolationMode.HighQualityCubic, new Rect(shown.X, shown.Y, shown.Width, shown.Height), null);
            covers?.Invoke();
            dc.DrawRectangle(dest, edgeBrush, edge);
            return;
        }
        var rounded = new RoundedRectangle(new RectangleF(place.X, place.Y, place.Width, place.Height), radius, radius);
        using ID2D1RoundedRectangleGeometry mask = factory.CreateRoundedRectangleGeometry(rounded);
        dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(place.Left, place.Top, place.Right, place.Bottom),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1,
            LayerOptions = LayerOptions1.None,
        }, null!); // no layer object: Direct2D manages one (Windows 8 and later)
        try
        {
            dc.DrawBitmap(bitmap, dest, (float)opacity, InterpolationMode.HighQualityCubic, new Rect(shown.X, shown.Y, shown.Width, shown.Height), null);
            covers?.Invoke();
        }
        finally
        {
            dc.PopLayer();
        }
        dc.DrawRoundedRectangle(rounded, edgeBrush, edge);
    }

    public void Dispose()
    {
        _matrix?.Dispose();
        _matrix = null;
        _table?.Dispose();
        _table = null;
    }
}
