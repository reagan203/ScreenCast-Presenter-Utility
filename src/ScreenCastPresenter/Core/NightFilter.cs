namespace ScreenCastPresenter.Core;

/// <summary>How the TV's picture is toned for a dark room (FR-123).</summary>
internal enum NightMode
{
    /// <summary>The picture as it is.</summary>
    Off,

    /// <summary>White pages softened to a warm grey; dark parts stay as they are.</summary>
    Soften,

    /// <summary>Light pages turned dark and dark ones light, keeping colours' hues roughly as they were.</summary>
    Invert,
}

/// <summary>
/// The night filter (FR-123): dimming (FR-20) darkens everything, which makes dark slides
/// unreadable; this tones down only what's bright (Soften), or turns bright pages dark (Invert). It
/// applies to what's shown and the pictures beside it, never to the name banner, captions, the QR
/// code (which must stay scannable) or the small picture (a face inverted looks wrong). The maths is
/// here, as colour tables and matrices the GPU applies, so it can be checked without one.
/// </summary>
internal static class NightFilter
{
    /// <summary>Soften's tone curve: evenly spaced inputs from black to white and what each becomes.</summary>
    public static IReadOnlyList<float> SoftenCurve { get; } = [0f, 0.25f, 0.48f, 0.62f, 0.72f];

    /// <summary>Soften's warmth: blue is kept to this much, so the grey is warm rather than blue-white.</summary>
    public const float SoftenBlue = 0.88f;

    public static NightMode Parse(string? text) => Enum.TryParse(text, ignoreCase: true, out NightMode m) && Enum.IsDefined(m) ? m : NightMode.Off;

    /// <summary>Soften's curve at <paramref name="value"/> (0–1), straight lines between its points.</summary>
    public static float Soften(float value)
    {
        value = Math.Clamp(value, 0, 1);
        float at = value * (SoftenCurve.Count - 1);
        int i = Math.Min((int)at, SoftenCurve.Count - 2);
        return SoftenCurve[i] + (SoftenCurve[i + 1] - SoftenCurve[i]) * (at - i);
    }

    /// <summary>
    /// The colour matrix for the GPU (row vectors: [R G B A 1] × M, 5 rows of 4) that finishes
    /// <paramref name="mode"/> at <paramref name="opacity"/>: over the TV's black, a picture at some
    /// brightness is its colour times that brightness, so fading is folded in.
    /// </summary>
    public static float[] Matrix(NightMode mode, float opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        float[] m = new float[20];
        switch (mode)
        {
            case NightMode.Invert:
            {
                // 1 − c, then the hue turned half way round (so red text stays reddish), then the brightness.
                float[,] hue = { { -0.574f, 1.430f, 0.144f }, { 0.426f, 0.430f, 0.144f }, { 0.426f, 1.430f, -0.856f } }; // W3C hueRotate(180°): rows are outputs
                for (int output = 0; output < 3; output++)
                {
                    float offset = 0;
                    for (int input = 0; input < 3; input++)
                    {
                        m[input * 4 + output] = -hue[output, input] * opacity;
                        offset += hue[output, input];
                    }
                    m[16 + output] = offset * opacity;
                }
                break;
            }
            case NightMode.Soften:
                m[0] = opacity;
                m[5] = opacity;
                m[10] = SoftenBlue * opacity;
                break;
            default:
                m[0] = m[5] = m[10] = opacity;
                break;
        }
        m[15] = 1; // alpha unchanged
        return m;
    }

    /// <summary>A colour (0–1 each) through <paramref name="mode"/> at full brightness: what the TV shows for it.</summary>
    public static (float R, float G, float B) Apply(NightMode mode, float r, float g, float b, float opacity = 1)
    {
        if (mode == NightMode.Soften)
        {
            r = Soften(r);
            g = Soften(g);
            b = Soften(b);
        }
        float[] m = Matrix(mode, opacity);
        float Out(int c) => Math.Clamp(r * m[c] + g * m[4 + c] + b * m[8 + c] + m[16 + c], 0, 1);
        return (Out(0), Out(1), Out(2));
    }

    /// <summary>"Off", "Soften white pages", "Turn light pages dark", for Settings and the tray.</summary>
    public static string Words(NightMode mode) => mode switch
    {
        NightMode.Soften => "Soften white pages",
        NightMode.Invert => "Turn light pages dark",
        _ => "Off",
    };
}
