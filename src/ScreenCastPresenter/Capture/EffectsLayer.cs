using System.Diagnostics;
using System.Numerics;
using ScreenCastPresenter.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;
using SizeF = System.Drawing.SizeF;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Marks drawn over the picture on the TV, inside the output, so the TV, a meeting sharing the
/// output window and one sharing the Zoom screen all see exactly the same thing: hidden parts
/// (FR-74), drawings with shapes and text (FR-71, FR-78), the laser (FR-71), the spotlight (FR-77),
/// click rings and the pointer ring (FR-65). These fade with the picture (blank and holds hide
/// them). While the picture is frozen the presenter may be working privately, so clicks, the
/// pointer ring, the laser and the spotlight are not drawn then; drawings and hidden parts are.
/// On top, independent of the picture: the name banner (FR-79), a QR code (FR-81) and live
/// captions (FR-84). Commands arrive on the helper's command thread; drawing happens on the render thread.
/// </summary>
internal sealed class EffectsLayer : IDisposable
{
    private static readonly TimeSpan BannerFade = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly Action _changed;
    private readonly List<(PointF At, bool Secondary, long Start)> _rings = [];
    private readonly List<(PointF At, long Time)> _trail = [];
    private readonly List<InkStroke> _strokes = [];
    private PointF? _pointer;
    private PointF? _laser;
    private PointF? _spotlight;
    private List<CropRegion> _redactions = [];
    private List<(CropRegion Region, RedactStyle Style)> _autoCovers = [];
    private string? _keys;

    // What a frame draws, taken under the lock into lists kept between frames (render thread only),
    // so a frame allocates nothing new. A line's points are copied only when it changed.
    private readonly List<(PointF At, bool Secondary, long Start)> _frameRings = [];
    private readonly List<(PointF At, long Time)> _frameTrail = [];
    private readonly List<(InkStroke Stroke, PointF[] Points)> _frameStrokes = [];
    private readonly List<(CropRegion Region, RedactStyle Style)> _frameRedactions = [];
    private readonly Dictionary<int, PointF[]> _pointCopies = [];
    private CaptionLook _look = CaptionLook.Default;
    private RedactStyle _redactStyle;
    private (string Name, string Second)? _caption;
    private (string Name, string Second) _captionShown = ("", "");
    private long _captionChanged;
    private (string Text, InsetCorner Corner, bool[,] Modules)? _qr;
    private string? _live;
    private string _qrLabel = "";
    private (string Text, InsetCorner Corner, bool Up)? _timer;

    /// <summary>The label under a QR code is cut to this many characters.</summary>
    public const int QrLabelLength = 40;

    // Render-thread resources, created on first use.
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private Vortice.Direct2D1.Effects.GaussianBlur? _blur;
    private ID2D1GradientStopCollection? _spotStops;
    private ID2D1RadialGradientBrush? _spotBrush;
    private readonly Dictionary<(int Size, bool Bold), IDWriteTextFormat> _formats = [];
    private readonly Dictionary<(string Text, IDWriteTextFormat Format), float> _widths = [];

    /// <summary>
    /// Each line's shape, kept between frames (render thread): built once in the content's own
    /// fractions, then placed on the canvas with a transform that changes only when the window
    /// moves or the view is zoomed. A drawing of hundreds of lines then costs almost nothing a frame.
    /// </summary>
    private readonly Dictionary<int, StrokeShape> _shapes = [];

    private sealed class StrokeShape : IDisposable
    {
        public int Count;
        public PointF First, Last;
        public ID2D1PathGeometry? Path;
        public Matrix3x2 Placed;
        public ID2D1TransformedGeometry? OnCanvas;

        /// <summary>The line already cut into triangles at its width on the canvas: drawing it again costs next to nothing.</summary>
        public ID2D1GeometryRealization? Realized;
        public float RealizedWidth;

        public void Dispose()
        {
            Realized?.Dispose();
            OnCanvas?.Dispose();
            Path?.Dispose();
            Realized = null;
            OnCanvas = null;
            Path = null;
        }
    }

    /// <param name="changed">Called after every change, so the render thread draws a new frame.</param>
    public EffectsLayer(Action changed) => _changed = changed;

    private static long Now => Stopwatch.GetTimestamp();

    private void Change(Action change)
    {
        lock (_sync) change();
        _changed();
    }

    public void Click(PointF at, bool secondary) => Change(() =>
    {
        _rings.Add((at, secondary, Now));
        if (_rings.Count > 32) _rings.RemoveAt(0); // a frantic clicker can't grow this
    });

    public void Pointer(PointF? at) => Change(() => _pointer = at);

    public void Laser(PointF? at) => Change(() =>
    {
        _laser = at;
        if (at is { } p) _trail.Add((p, Now));
        if (_trail.Count > 240) _trail.RemoveRange(0, _trail.Count - 240);
    });

    public void Spotlight(PointF? at) => Change(() => _spotlight = at);

    public void Redact(IReadOnlyList<CropRegion> regions, RedactStyle style) => Change(() =>
    {
        _redactions = [.. regions.Take(Redactions.MaxPerSource)];
        _redactStyle = style;
    });

    public void Caption(string? name, string? second) => Change(() =>
    {
        bool wasOn = _caption is not null;
        _caption = name is null ? null : (InkStroke.TidyText(name), InkStroke.TidyText(second ?? ""));
        if (_caption is { } c) _captionShown = c;
        if (wasOn != (_caption is not null)) _captionChanged = Now; // fading in or out
    });

    public void Qr(string? text, InsetCorner corner) => Change(() =>
    {
        _qr = text is { Length: > 0 } && QrCode.Encode(text) is { } modules ? (text, corner, modules) : null;
        _qrLabel = _qr is null ? "" : TrayMenuPlan.Shorten(QrLabel(text!), QrLabelLength); // worked out once, not every frame
    });

    public void LiveCaption(string? text) => Change(() => _live = text is null ? null : LiveCaptions.Tail(InkStroke.TidyText(text, 2000), _look.MaxShown));

    /// <summary>Live captions' size and place (FR-89).</summary>
    public void CaptionStyle(CaptionLook look) => Change(() => _look = look);

    /// <summary>A timer in a corner (FR-120): "Exercise 4:59"; null takes it off.</summary>
    public void Timer(string? text, InsetCorner corner, bool up) => Change(() =>
        _timer = text is { Length: > 0 } ? (InkStroke.TidyText(text, 60), corner, up) : null);

    /// <summary>A shortcut the presenter pressed (FR-101), shown along the bottom; null takes it off.</summary>
    public void KeyCombo(string? text) => Change(() => _keys = text is { Length: > 0 } ? text : null);

    /// <summary>Most automatic covers at once: text found, password boxes and faces.</summary>
    public const int MaxAutoCovers = Redactions.MaxAutomatic + Interop.UiAutomation.MaxBoxes + FaceFinder.MaxFaces;

    /// <summary>What automatic hiding covers now.</summary>
    public IReadOnlyList<CropRegion> AutoRegions
    {
        get
        {
            lock (_sync) return [.. _autoCovers.Select(c => c.Region)];
        }
    }

    /// <summary>What automatic hiding found (FR-88), covered like hidden parts, all in one look; an empty list uncovers.</summary>
    public void AutoRedact(IReadOnlyList<CropRegion> regions, RedactStyle style) => AutoCovers([.. regions.Select(r => (r, style))]);

    /// <summary>What automatic hiding covers (FR-88, FR-98, FR-100), each with its own look; an empty list uncovers.</summary>
    public void AutoCovers(IReadOnlyList<(CropRegion Region, RedactStyle Style)> covers) => Change(() => _autoCovers = [.. covers.Take(MaxAutoCovers)]);

    public void InkStart(InkStroke stroke) => Change(() =>
    {
        _strokes.RemoveAll(s => s.Id == stroke.Id);
        if (_strokes.Count >= InkDrawing.MaxStrokes) _strokes.RemoveAt(0);
        _strokes.Add(new InkStroke(stroke.Id, stroke.Tool, stroke.Argb, Math.Clamp(stroke.Width, 0.0005f, 0.2f)) { Text = stroke.Text });
    });

    public void InkAdd(int id, IReadOnlyList<PointF> points) => Change(() =>
    {
        if (_strokes.FirstOrDefault(s => s.Id == id) is not { } stroke) return;
        foreach (PointF p in points) stroke.Add(p);
    });

    public void InkSet(int id, IReadOnlyList<PointF> points) => Change(() =>
    {
        if (_strokes.FirstOrDefault(s => s.Id == id) is not { } stroke) return;
        stroke.Points.Clear();
        foreach (PointF p in points) stroke.Add(p);
    });

    public void InkRemove(int id) => Change(() => _strokes.RemoveAll(s => s.Id == id));

    public void InkClear() => Change(_strokes.Clear);

    /// <summary>
    /// Something is still moving (a ring growing, the laser trail fading, the banner fading):
    /// keep drawing frames. Finished ones are dropped here too, so while the picture is black
    /// (nothing is drawn) the render thread still goes back to resting.
    /// </summary>
    public bool IsAnimating
    {
        get
        {
            lock (_sync)
            {
                long now = Now;
                Prune(now);
                return _rings.Count > 0 || _trail.Count > 0 || Age(_captionChanged, now) < BannerFade;
            }
        }
    }

    /// <summary>Drops rings and trail points that have finished. Call under the lock.</summary>
    private void Prune(long now)
    {
        _rings.RemoveAll(r => EffectStyle.Ring(Age(r.Start, now)) is null);
        _trail.RemoveAll(t => EffectStyle.TrailAlpha(Age(t.Time, now)) is null);
    }

    /// <summary>Parts hidden from the audience: drawn whenever the picture is, frozen or not.</summary>
    public bool HasRedactions
    {
        get
        {
            lock (_sync) return _redactions.Count > 0 || _autoCovers.Count > 0;
        }
    }

    /// <summary>Words or a code laid over the TV: the canvas then needs enough pixels to keep them sharp.</summary>
    public bool HasOverlay
    {
        get
        {
            lock (_sync) return _caption is not null || _qr is not null || _live is not null || _timer is not null;
        }
    }

    /// <summary>
    /// Draws the marks over the picture. <paramref name="area"/> is the content's whole area in the
    /// captured frame, <paramref name="shown"/> the part of it shown (crop or zoom), <paramref name="dest"/>
    /// where that part is drawn on the canvas. Marks never leave <paramref name="dest"/>.
    /// </summary>
    public void Render(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite, ID2D1Bitmap1 picture,
        RectangleF area, RectangleF shown, RectangleF dest, float opacity, bool frozen)
    {
        if (opacity <= 0.001f || dest.Width <= 0 || dest.Height <= 0 || shown.Width <= 0 || shown.Height <= 0) return;
        long now = Now;
        List<(PointF At, bool Secondary, long Start)> rings = _frameRings;
        List<(PointF At, long Time)> trail = _frameTrail;
        List<(InkStroke Stroke, PointF[] Points)> strokes = _frameStrokes;
        List<(CropRegion Region, RedactStyle Style)> redactions = _frameRedactions;
        rings.Clear();
        trail.Clear();
        strokes.Clear();
        redactions.Clear();
        PointF? pointer, laser, spotlight;
        lock (_sync)
        {
            Prune(now);
            rings.AddRange(_rings);
            trail.AddRange(_trail);
            foreach (InkStroke s in _strokes) strokes.Add((s, PointsOf(s)));
            foreach (CropRegion r in _redactions) redactions.Add((r, _redactStyle));
            redactions.AddRange(_autoCovers);
            pointer = _pointer;
            laser = _laser;
            spotlight = _spotlight;
        }
        if (_pointCopies.Count > strokes.Count) ForgetPointCopies(strokes);
        if (frozen)
        {
            // The presenter may be working privately: nothing follows the pointer.
            rings.Clear();
            trail.Clear();
            pointer = laser = spotlight = null;
        }
        if (rings.Count == 0 && trail.Count == 0 && strokes.Count == 0 && redactions.Count == 0 && pointer is null && laser is null && spotlight is null) return;
        EnsureResources(dc, factory);

        Vector2 Map(PointF p)
        {
            PointF m = EffectStyle.Map(p, area, shown, dest);
            return new Vector2(m.X, m.Y);
        }
        float h = dest.Height;
        float contentHeight = area.Height * dest.Height / shown.Height; // a stroke's width follows the content
        float sx = dest.Width / shown.Width, sy = dest.Height / shown.Height;
        Matrix3x2 place = Matrix3x2.CreateScale(area.Width * sx, area.Height * sy)
            * Matrix3x2.CreateTranslation(dest.X + (area.X - shown.X) * sx, dest.Y + (area.Y - shown.Y) * sy);

        dc.PushAxisAlignedClip(new Vortice.RawRectF(dest.Left, dest.Top, dest.Right, dest.Bottom), AntialiasMode.PerPrimitive);
        try
        {
            // Hidden parts first, so nothing else is drawn on top of what's under them.
            DrawRedactions(dc, picture, area, shown, dest, redactions, opacity);

            int newest = strokes.Count > 0 ? strokes[^1].Stroke.Id : -1; // the line being drawn, most likely
            Func<PointF, Vector2> map = Map; // one delegate a frame, not one per line
            foreach ((InkStroke stroke, PointF[] points) in strokes) DrawStroke(dc, factory, dwrite, stroke, points, map, place, contentHeight, opacity, stroke.Id == newest);
            ForgetShapes(strokes);

            if (spotlight is { } spot) DrawSpotlight(dc, Map(spot), dest, h, opacity);

            // The laser: a fading trail, a soft glow and a bright dot.
            for (int i = 1; i < trail.Count; i++)
            {
                if (EffectStyle.TrailAlpha(Age(trail[i].Time, now)) is not { } a) continue;
                SetColour(EffectStyle.Laser, a * 0.8f * opacity);
                dc.DrawLine(Map(trail[i - 1].At), Map(trail[i].At), _brush!, h * EffectStyle.LaserRadius * 1.2f * a, _round);
            }
            if (laser is { } l)
            {
                Vector2 at = Map(l);
                float r = h * EffectStyle.LaserRadius;
                SetColour(EffectStyle.Laser, 0.3f * opacity);
                dc.FillEllipse(new Ellipse(at, r * 2.4f, r * 2.4f), _brush!);
                SetColour(EffectStyle.Laser, opacity);
                dc.FillEllipse(new Ellipse(at, r, r), _brush!);
            }

            foreach ((PointF at, bool secondary, long start) in rings)
            {
                if (EffectStyle.Ring(Age(start, now)) is not { } ring) continue;
                SetColour(secondary ? EffectStyle.ClickSecondary : EffectStyle.ClickPrimary, ring.Alpha * opacity);
                float r = h * ring.Radius;
                dc.DrawEllipse(new Ellipse(Map(at), r, r), _brush!, Math.Max(2f, h * EffectStyle.RingStroke));
            }
            if (pointer is { } p)
            {
                Vector2 at = Map(p);
                float r = h * EffectStyle.PointerRadius;
                SetColour(EffectStyle.PointerRing, 0.28f * opacity);
                dc.FillEllipse(new Ellipse(at, r, r), _brush!);
                SetColour(EffectStyle.PointerRing, 0.9f * opacity);
                dc.DrawEllipse(new Ellipse(at, r, r), _brush!, Math.Max(2f, h * 0.003f));
            }
        }
        finally
        {
            dc.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// The top layer, over everything on the canvas and whatever the picture is doing (it's what
    /// the presenter chose to say): the name banner, a QR code, live captions.
    /// </summary>
    /// <param name="visibility">
    /// How much of the TV is showing: the picture's brightness, or the black screen's message's.
    /// On plain black (blank without a message, or held black for privacy) nothing is drawn.
    /// </param>
    /// <param name="pictureVisibility">
    /// How much of the picture itself is showing. Live captions follow it, not the message: during
    /// a break (blank with a message) what's said in the room never appears on the TV.
    /// </param>
    public void RenderOverlay(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite, SizeF canvas, float visibility, float pictureVisibility)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || visibility <= 0.001f) return;
        _alphaScale = Math.Clamp(visibility, 0, 1);
        try
        {
            DrawOverlay(dc, factory, dwrite, canvas, Math.Clamp(pictureVisibility, 0, 1) / _alphaScale);
        }
        finally
        {
            _alphaScale = 1;
        }
    }

    private float _alphaScale = 1;

    /// <param name="liveAlpha">Live captions' brightness relative to the rest (they show only with the picture).</param>
    private void DrawOverlay(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite, SizeF canvas, float liveAlpha)
    {
        (string Name, string Second)? caption;
        (string Name, string Second) captionShown;
        long changed, now = Now;
        (string Text, InsetCorner Corner, bool[,] Modules)? qr;
        string? live, keys;
        CaptionLook look;
        (string Text, InsetCorner Corner, bool Up)? timer;
        lock (_sync)
        {
            timer = _timer;
            caption = _caption;
            captionShown = _captionShown;
            changed = _captionChanged;
            qr = _qr;
            live = _live;
            look = _look;
            keys = _keys;
        }
        if (liveAlpha <= 0.001f) live = keys = null; // with the picture only, never on a break screen
        float fade = Math.Clamp((float)(Age(changed, now) / BannerFade), 0, 1);
        float bannerAlpha = caption is not null ? fade : 1 - fade;
        if (bannerAlpha <= 0.001f && qr is null && live is null && keys is null && timer is null) return;
        EnsureResources(dc, factory);
        float h = canvas.Height, w = canvas.Width;

        float bannerTop = h;
        if (bannerAlpha > 0.001f && captionShown.Name.Length > 0)
        {
            bool twoLines = captionShown.Second.Length > 0;
            IDWriteTextFormat nameFormat = Format(dwrite, h * 0.042f, bold: true);
            IDWriteTextFormat secondFormat = Format(dwrite, h * 0.028f, bold: false);
            float textWidth = Measure(dwrite, captionShown.Name, nameFormat, w);
            if (twoLines) textWidth = Math.Max(textWidth, Measure(dwrite, captionShown.Second, secondFormat, w));
            RectangleF banner = Layouts.Banner(canvas, twoLines, textWidth);
            bannerTop = banner.Top;
            SetColour(Color.Black, 0.62f * bannerAlpha);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(banner.X, banner.Y, banner.Width, banner.Height), h * 0.008f, h * 0.008f), _brush!);
            SetColour(EffectStyle.ClickPrimary, bannerAlpha);
            dc.FillRectangle(new Vortice.RawRectF(banner.Left, banner.Top, banner.Left + h * 0.006f, banner.Bottom), _brush!);
            float pad = h * 0.022f, x = banner.Left + h * 0.006f + pad;
            SetColour(Color.White, bannerAlpha);
            dc.DrawText(captionShown.Name, nameFormat, new Rect(x, banner.Top + (twoLines ? pad * 0.6f : (banner.Height - h * 0.052f) / 2), banner.Right - x - pad, h * 0.056f), _brush!);
            if (twoLines)
            {
                SetColour(Color.FromArgb(0xD8, 0xD8, 0xDE), bannerAlpha);
                dc.DrawText(captionShown.Second, secondFormat, new Rect(x, banner.Top + pad * 0.6f + h * 0.056f, banner.Right - x - pad, h * 0.04f), _brush!);
            }
        }

        float bottomLimit = bannerTop, topLimit = 0;
        if (live is { Length: > 0 })
        {
            RectangleF strip = Layouts.LiveStrip(canvas, bannerTop, look);
            if (look.Place == CaptionPlace.Top) topLimit = strip.Bottom;
            else bottomLimit = strip.Top;
            SetColour(Color.Black, 0.7f * liveAlpha);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(strip.X, strip.Y, strip.Width, strip.Height), h * 0.01f, h * 0.01f), _brush!);
            IDWriteTextFormat format = Format(dwrite, h * look.TextHeight, bold: false, centred: true);
            SetColour(Color.White, liveAlpha);
            dc.DrawText(live, format, new Rect(strip.X + h * 0.02f, strip.Y + h * 0.012f, strip.Width - h * 0.04f, strip.Height - h * 0.024f), _brush!);
        }

        if (keys is not null)
        {
            IDWriteTextFormat format = Format(dwrite, h * 0.04f, bold: true, centred: true);
            RectangleF chip = Layouts.KeyChip(canvas, Measure(dwrite, keys, format, w), bottomLimit);
            SetColour(Color.Black, 0.72f * liveAlpha);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(chip.X, chip.Y, chip.Width, chip.Height), h * 0.012f, h * 0.012f), _brush!);
            SetColour(EffectStyle.ClickPrimary, liveAlpha);
            dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(chip.X, chip.Y, chip.Width, chip.Height), h * 0.012f, h * 0.012f), _brush!, Math.Max(1.5f, h * 0.003f));
            SetColour(Color.White, liveAlpha);
            dc.DrawText(keys, format, new Rect(chip.X, chip.Y + (chip.Height - h * 0.052f) / 2, chip.Width, h * 0.056f), _brush!);
        }

        if (timer is { } t)
        {
            // A timer chip (FR-120): white on black while it runs, amber words when time's up.
            IDWriteTextFormat format = Format(dwrite, h * 0.045f, bold: true, centred: true);
            RectangleF chip = ActivityTimer.Chip(canvas, t.Corner, Measure(dwrite, t.Text, format, w), bottomLimit, topLimit);
            SetColour(Color.Black, 0.74f);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(chip.X, chip.Y, chip.Width, chip.Height), h * 0.014f, h * 0.014f), _brush!);
            SetColour(t.Up ? EffectStyle.ClickPrimary : Color.White, 1);
            dc.DrawText(t.Text, format, new Rect(chip.X, chip.Y + (chip.Height - h * 0.058f) / 2, chip.Width, h * 0.062f), _brush!);
        }

        if (qr is { } code)
        {
            RectangleF card = Layouts.QrCard(canvas, code.Corner, bottomLimit, topLimit);
            int n = code.Modules.GetLength(0);
            float module = Layouts.QrModule(card.Width, n); // whole pixels, with four light modules round it
            float side = module * (n + 8);
            card = new RectangleF(MathF.Round(card.X + (card.Width - side) / 2), MathF.Round(card.Y + (card.Height - side) / 2), side, side);
            SetColour(Color.White, 1);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(card.X, card.Y, card.Width, card.Height), module, module), _brush!);
            float left = card.X + 4 * module, top = card.Y + 4 * module;
            // One pixel a module, made once per code, drawn with whole-pixel scaling: one draw call
            // a frame instead of one per dark module, and crisp edges.
            dc.DrawBitmap(QrBitmap(dc, code.Modules), new Rect(left, top, n * module, n * module), _alphaScale, InterpolationMode.NearestNeighbor, null, null);
            string label;
            lock (_sync) label = _qrLabel;
            IDWriteTextFormat format = Format(dwrite, h * 0.024f, bold: false, centred: true);
            float labelTop = card.Bottom + h * 0.008f;
            if (labelTop + h * 0.04f > h) labelTop = card.Top - h * 0.048f; // a card at the bottom: its label above it
            SetColour(Color.Black, 0.6f);
            dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(card.X, labelTop, card.Width, h * 0.04f), h * 0.006f, h * 0.006f), _brush!);
            SetColour(Color.White, 1);
            dc.DrawText(label, format, new Rect(card.X, labelTop + h * 0.005f, card.Width, h * 0.032f), _brush!);
        }
    }

    private ID2D1Bitmap1? _qrBitmap;
    private bool[,]? _qrBitmapOf;

    /// <summary>The code as a bitmap of one pixel per module (render thread), made again only for a new code.</summary>
    private ID2D1Bitmap1 QrBitmap(ID2D1DeviceContext dc, bool[,] modules)
    {
        if (_qrBitmap is not null && ReferenceEquals(_qrBitmapOf, modules)) return _qrBitmap;
        _qrBitmap?.Dispose();
        int n = modules.GetLength(0);
        var pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            byte v = modules[y, x] ? (byte)0 : (byte)255;
            int i = (y * n + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = 255;
        }
        System.Runtime.InteropServices.GCHandle pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            _qrBitmap = dc.CreateBitmap(new SizeI(n, n), pin.AddrOfPinnedObject(), (uint)(n * 4),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        finally
        {
            pin.Free();
        }
        _qrBitmapOf = modules;
        return _qrBitmap;
    }

    /// <summary>What's written under a QR code: the link, the Wi-Fi network's name (never its password), or the contact's name.</summary>
    internal static string QrLabel(string text)
    {
        if (text.StartsWith("MECARD:N:", StringComparison.Ordinal))
            return Field(text, "MECARD:N:".Length) is { Length: > 0 } person ? "Contact: " + person : "Contact";
        if (!text.StartsWith("WIFI:", StringComparison.Ordinal)) return text;
        int s = text.IndexOf("S:", StringComparison.Ordinal);
        return s < 0 ? "Wi-Fi" : "Wi-Fi: " + Field(text, s + 2);

        // Up to the next unescaped ";", escaped characters as themselves.
        static string Field(string text, int start)
        {
            var field = new System.Text.StringBuilder();
            for (int i = start; i < text.Length && text[i] != ';'; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length) i++;
                field.Append(text[i]);
            }
            return field.ToString();
        }
    }

    /// <summary>How wide words are in a format (remembered: the banner is measured every frame it shows).</summary>
    private float Measure(IDWriteFactory dwrite, string text, IDWriteTextFormat format, float maxWidth)
    {
        if (_widths.TryGetValue((text, format), out float known)) return Math.Min(known, maxWidth);
        using IDWriteTextLayout layout = dwrite.CreateTextLayout(text, format, maxWidth, format.FontSize * 2);
        float width = layout.Metrics.WidthIncludingTrailingWhitespace;
        if (_widths.Count > 64) _widths.Clear();
        _widths[(text, format)] = width;
        return width;
    }

    /// <summary>
    /// Draws a pen or highlighter line from its cached shape. A finished highlighter line is also
    /// cut into triangles once (Windows 8.1 and later): being see-through, it must not darken where
    /// it crosses itself, and Direct2D would otherwise work out its outline afresh every frame. An
    /// opaque pen line can overlap itself freely, so Direct2D's direct path is already fast for it.
    /// </summary>
    private void DrawLine(ID2D1DeviceContext dc, ID2D1Factory1 factory, InkStroke stroke, PointF[] points, Matrix3x2 place, float width, bool cache)
    {
        ID2D1Geometry onCanvas = LineShape(factory, stroke, points, place);
        StrokeShape shape = _shapes[stroke.Id];
        if (!cache)
        {
            dc.DrawGeometry(onCanvas, _brush!, width, _round);
            return;
        }
        if (!_askedForRealizations)
        {
            _dc1 = dc.QueryInterfaceOrNull<ID2D1DeviceContext1>();
            _askedForRealizations = true;
        }
        if (_dc1 is not { } dc1)
        {
            dc.DrawGeometry(onCanvas, _brush!, width, _round);
            return;
        }
        if (shape.Realized is null || Math.Abs(shape.RealizedWidth - width) > 0.01f)
        {
            shape.Realized?.Dispose();
            shape.Realized = dc1.CreateStrokedGeometryRealization(onCanvas, 0.25f, width, _round);
            shape.RealizedWidth = width;
        }
        dc1.DrawGeometryRealization(shape.Realized, _brush!);
    }

    private ID2D1DeviceContext1? _dc1;
    private bool _askedForRealizations; // asked once: a Windows without them draws the shapes instead

    /// <summary>A pen or highlighter line on the canvas: its shape from the cache, rebuilt only when it grew or moved.</summary>
    private ID2D1Geometry LineShape(ID2D1Factory1 factory, InkStroke stroke, PointF[] points, Matrix3x2 place)
    {
        if (!_shapes.TryGetValue(stroke.Id, out StrokeShape? shape)) _shapes[stroke.Id] = shape = new StrokeShape();
        if (shape.Path is null || shape.Count != points.Length || shape.First != points[0] || shape.Last != points[^1])
        {
            shape.Dispose();
            shape.Path = factory.CreatePathGeometry();
            using (ID2D1GeometrySink sink = shape.Path.Open())
            {
                sink.BeginFigure(new Vector2(points[0].X, points[0].Y), FigureBegin.Hollow);
                sink.AddLines([.. points.Skip(1).Select(p => new Vector2(p.X, p.Y))]);
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            shape.Count = points.Length;
            shape.First = points[0];
            shape.Last = points[^1];
        }
        if (shape.OnCanvas is null || shape.Placed != place)
        {
            shape.Realized?.Dispose();
            shape.Realized = null;
            shape.OnCanvas?.Dispose();
            shape.OnCanvas = factory.CreateTransformedGeometry(shape.Path, place);
            shape.Placed = place;
        }
        return shape.OnCanvas;
    }

    /// <summary>
    /// A stable copy of a line's points for this frame (under the lock): the last copy when the
    /// line hasn't changed, a fresh one when it grew or was reshaped. Lines only grow at the end or
    /// are replaced whole, so the count and the two ends tell.
    /// </summary>
    private PointF[] PointsOf(InkStroke stroke)
    {
        List<PointF> points = stroke.Points;
        if (_pointCopies.TryGetValue(stroke.Id, out PointF[]? copy) && copy.Length == points.Count
            && (copy.Length == 0 || copy[0] == points[0] && copy[^1] == points[^1])) return copy;
        copy = [.. points];
        _pointCopies[stroke.Id] = copy;
        return copy;
    }

    private void ForgetPointCopies(List<(InkStroke Stroke, PointF[] Points)> strokes)
    {
        var live = new HashSet<int>(strokes.Select(s => s.Stroke.Id));
        foreach (int id in _pointCopies.Keys.Where(id => !live.Contains(id)).ToList()) _pointCopies.Remove(id);
    }

    /// <summary>Lines no longer in the drawing (undone, cleared) give their shapes back.</summary>
    private void ForgetShapes(List<(InkStroke Stroke, PointF[] Points)> strokes)
    {
        if (_shapes.Count <= strokes.Count) return;
        var live = strokes.Select(s => s.Stroke.Id).ToHashSet();
        foreach (int id in _shapes.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _shapes[id].Dispose();
            _shapes.Remove(id);
        }
    }

    private void DrawStroke(ID2D1DeviceContext dc, ID2D1Factory1 factory, IDWriteFactory dwrite, InkStroke stroke, PointF[] points, Func<PointF, Vector2> map, Matrix3x2 place, float contentHeight, float opacity, bool growing)
    {
        if (points.Length == 0) return;
        Color c = Color.FromArgb(stroke.Argb);
        float alpha = (stroke.Tool == InkTool.Highlighter ? 0.4f : 1f) * opacity;
        float w = Math.Max(1.5f, stroke.Width * contentHeight);
        Vector2 first = map(points[0]);
        switch (stroke.Tool)
        {
            case InkTool.Text when stroke.Text is { Length: > 0 } text:
                IDWriteTextFormat format = Format(dwrite, Math.Max(10f, w), bold: true);
                var box = new Rect(first.X, first.Y, Math.Max(1, contentHeight * 1.6f), w * 1.6f);
                SetColour(Color.Black, 0.55f * opacity); // a soft shadow: readable on light and dark pages
                dc.DrawText(text, format, new Rect(box.X + w * 0.06f, box.Y + w * 0.06f, box.Width, box.Height), _brush!);
                SetColour(c, opacity);
                dc.DrawText(text, format, box, _brush!);
                return;
            case InkTool.Arrow when points.Length >= 2:
                Vector2 tip = map(points[^1]);
                SetColour(c, alpha);
                dc.DrawLine(first, tip, _brush!, w, _round);
                if (Layouts.ArrowHead(first, tip, Math.Max(w * 4, 10)) is { } head)
                {
                    dc.DrawLine(head.Tip, head.Left, _brush!, w, _round);
                    dc.DrawLine(head.Tip, head.Right, _brush!, w, _round);
                }
                return;
            case InkTool.Box when points.Length >= 2:
                Vector2 end = map(points[^1]);
                SetColour(c, alpha);
                dc.DrawRectangle(new Vortice.RawRectF(Math.Min(first.X, end.X), Math.Min(first.Y, end.Y), Math.Max(first.X, end.X), Math.Max(first.Y, end.Y)), _brush!, w, _round);
                return;
            case InkTool.Pen or InkTool.Highlighter:
                SetColour(c, alpha);
                if (points.Length == 1)
                {
                    dc.FillEllipse(new Ellipse(first, w / 2, w / 2), _brush!);
                    return;
                }
                // The line's width stays in canvas pixels: the shape is moved into place, the pen isn't scaled.
                DrawLine(dc, factory, stroke, points, place, w, cache: stroke.Tool == InkTool.Highlighter && !growing);
                return;
        }
    }

    /// <summary>Everything dimmed but a soft-edged circle around the pointer.</summary>
    private void DrawSpotlight(ID2D1DeviceContext dc, Vector2 centre, RectangleF dest, float h, float opacity)
    {
        float radius = h * EffectStyle.SpotlightRadius;
        // One brush, moved with the pointer: nothing made per frame.
        _spotBrush ??= dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(centre, Vector2.Zero, radius, radius), _spotStops!);
        _spotBrush.Center = centre;
        _spotBrush.RadiusX = _spotBrush.RadiusY = radius;
        _spotBrush.Opacity = opacity;
        dc.FillRectangle(new Vortice.RawRectF(dest.Left, dest.Top, dest.Right, dest.Bottom), _spotBrush);
    }

    /// <summary>
    /// Covers over another picture on the canvas (a small picture, a split-screen pane): its hidden
    /// parts and what automatic hiding found in it, exactly as over the main picture.
    /// </summary>
    public void RenderCovers(ID2D1DeviceContext dc, ID2D1Factory1 factory, ID2D1Bitmap1 picture, RectangleF area, RectangleF shown, RectangleF dest,
        IReadOnlyList<(CropRegion Region, RedactStyle Style)> covers, float opacity)
    {
        if (covers.Count == 0 || opacity <= 0.001f || dest.Width <= 0 || dest.Height <= 0 || shown.Width <= 0 || shown.Height <= 0) return;
        EnsureResources(dc, factory);
        dc.PushAxisAlignedClip(new Vortice.RawRectF(dest.Left, dest.Top, dest.Right, dest.Bottom), AntialiasMode.PerPrimitive);
        try
        {
            DrawRedactions(dc, picture, area, shown, dest, covers, opacity);
        }
        finally
        {
            dc.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// The boxes: blurred ones from the picture itself, solid ones fully opaque. During a fade
    /// they're solid: a blurred copy would be brighter than the fading picture for a moment.
    /// </summary>
    private void DrawRedactions(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, RectangleF area, RectangleF shown, RectangleF dest,
        IReadOnlyList<(CropRegion Region, RedactStyle Style)> redactions, float opacity)
    {
        foreach ((CropRegion r, RedactStyle style) in redactions)
        {
            PointF a = EffectStyle.Map(new PointF((float)r.X, (float)r.Y), area, shown, dest);
            PointF b = EffectStyle.Map(new PointF((float)(r.X + r.Width), (float)(r.Y + r.Height)), area, shown, dest);
            var box = new Vortice.RawRectF(a.X, a.Y, b.X, b.Y);
            if (style == RedactStyle.Blurred && opacity >= 0.999f)
            {
                DrawBlurred(dc, picture, area, shown, dest, r);
                SetColour(Color.Black, 0.35f);
                dc.FillRectangle(box, _brush!);
            }
            else
            {
                // Fully opaque, in the colour the box has at this brightness: during a fade
                // nothing of the picture under it shows through, not even faintly.
                SetColour(EffectStyle.SolidOver(UI.Theme.OutputBlack, EffectStyle.RedactFill, opacity), 1);
                dc.FillRectangle(box, _brush!);
            }
        }
    }

    /// <summary>A hidden part, heavily blurred: drawn from the picture itself through a blur, scaled like the picture.</summary>
    private void DrawBlurred(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, RectangleF area, RectangleF shown, RectangleF dest, CropRegion r)
    {
        _blur ??= new Vortice.Direct2D1.Effects.GaussianBlur(dc) { BorderMode = BorderMode.Hard }; // hard: no fading to transparent at the edges
        _blur.SetInput(0, picture, true);
        _blur.StandardDeviation = Math.Max(18f, area.Height / 36f); // nothing stays readable
        var frame = new Vortice.RawRectF(area.X + (float)r.X * area.Width, area.Y + (float)r.Y * area.Height,
            area.X + (float)(r.X + r.Width) * area.Width, area.Y + (float)(r.Y + r.Height) * area.Height);
        float sx = dest.Width / shown.Width, sy = dest.Height / shown.Height;
        Matrix3x2 before = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(dest.X - shown.X * sx, dest.Y - shown.Y * sy);
        try
        {
            using ID2D1Image output = _blur.Output;
            dc.DrawImage(output, new Vector2(frame.Left, frame.Top), frame, InterpolationMode.Linear, CompositeMode.SourceOver);
        }
        finally
        {
            dc.Transform = before;
        }
    }

    private void EnsureResources(ID2D1DeviceContext dc, ID2D1Factory1 factory)
    {
        _brush ??= dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _round ??= factory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
        _spotStops ??= dc.CreateGradientStopCollection(
        [
            new GradientStop(0, new Color4(0, 0, 0, 0)),
            new GradientStop(0.75f, new Color4(0, 0, 0, 0)),
            new GradientStop(1, new Color4(0, 0, 0, EffectStyle.SpotlightDim)),
        ]);
    }

    private IDWriteTextFormat Format(IDWriteFactory dwrite, float size, bool bold, bool centred = false)
    {
        int key = (int)Math.Round(size) * (centred ? -1 : 1);
        if (!_formats.TryGetValue((key, bold), out IDWriteTextFormat? format))
        {
            format = dwrite.CreateTextFormat("Segoe UI", bold ? FontWeight.SemiBold : FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, Math.Max(8, (float)Math.Round(size)));
            format.WordWrapping = WordWrapping.Wrap;
            if (centred) format.TextAlignment = TextAlignment.Center;
            _formats[(key, bold)] = format;
        }
        return format;
    }

    private void SetColour(Color c, float alpha) => _brush!.Color = new Color4(c.R / 255f, c.G / 255f, c.B / 255f, Math.Clamp(alpha * _alphaScale, 0, 1));

    private static TimeSpan Age(long start, long now) => Stopwatch.GetElapsedTime(start, now);

    /// <summary>Releases the render thread's Direct2D objects (call on the render thread, before the device goes).</summary>
    public void Dispose()
    {
        _brush?.Dispose();
        _round?.Dispose();
        _blur?.Dispose();
        _spotBrush?.Dispose();
        _spotBrush = null;
        _spotStops?.Dispose();
        foreach (IDWriteTextFormat format in _formats.Values) format.Dispose();
        _formats.Clear();
        _widths.Clear();
        _pointCopies.Clear();
        _qrBitmap?.Dispose();
        _qrBitmap = null;
        foreach (StrokeShape shape in _shapes.Values) shape.Dispose();
        _shapes.Clear();
        _dc1?.Dispose();
        _dc1 = null;
        _askedForRealizations = false;
        _brush = null;
        _round = null;
        _blur = null;
        _spotStops = null;
    }
}
