using System.Globalization;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Turning the TV on and off and choosing its input over the cable (FR-107), with DDC/CI, the
/// standard way a PC talks to a monitor through HDMI or DisplayPort. Most monitors take it; many TVs
/// don't, so the app asks the screen what it supports first and says so plainly. Pure: the codes,
/// the input names and the screen's own list of what it supports.
/// </summary>
internal static class TvControl
{
    /// <summary>MCCS power mode: 1 on, 4 standby, 5 off.</summary>
    public const byte PowerMode = 0xD6;

    public const uint PowerOn = 1, PowerStandby = 4, PowerOff = 5;

    /// <summary>MCCS input source.</summary>
    public const byte InputSource = 0x60;

    /// <summary>The usual MCCS input numbers and their names.</summary>
    public static IReadOnlyDictionary<uint, string> InputNames { get; } = new Dictionary<uint, string>
    {
        [0x01] = "VGA 1", [0x02] = "VGA 2", [0x03] = "DVI 1", [0x04] = "DVI 2",
        [0x0F] = "DisplayPort 1", [0x10] = "DisplayPort 2", [0x11] = "HDMI 1", [0x12] = "HDMI 2",
        [0x13] = "HDMI 3", [0x1B] = "USB-C",
    };

    public static string InputName(uint code) => InputNames.TryGetValue(code, out string? name) ? name : string.Create(CultureInfo.InvariantCulture, $"Input {code:X2}");

    /// <summary>"hdmi1", "HDMI 1", "dp2", "displayport 1", "usb-c" or a hex code ("11") to its code; null when it isn't one.</summary>
    public static uint? ParseInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        t = t.Replace("displayport", "dp", StringComparison.Ordinal).Replace("usbc", "usbc", StringComparison.Ordinal);
        foreach ((uint code, string name) in InputNames)
        {
            string n = new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
            n = n.Replace("displayport", "dp", StringComparison.Ordinal);
            if (n == t) return code;
        }
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) && hex is > 0 and < 0x100 ? hex : null;
    }

    /// <summary>
    /// The screen's capabilities string ("(prot(monitor)type(lcd)cmds(01 02)vcp(02 10 60(0F 11) D6(01 04)))")
    /// as the codes it supports, each with the values it lists (empty when it lists none). Null when it isn't readable.
    /// </summary>
    public static Dictionary<byte, List<uint>>? ParseVcp(string? capabilities)
    {
        if (string.IsNullOrEmpty(capabilities)) return null;
        int at = capabilities.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        // The vcp(...) group, matching brackets.
        int i = at + 4, depth = 1, start = i;
        while (i < capabilities.Length && depth > 0)
        {
            if (capabilities[i] == '(') depth++;
            else if (capabilities[i] == ')') depth--;
            i++;
        }
        if (depth != 0) return null;
        string body = capabilities[start..(i - 1)];
        var codes = new Dictionary<byte, List<uint>>();
        byte? last = null;
        int p = 0;
        while (p < body.Length)
        {
            char c = body[p];
            if (char.IsWhiteSpace(c))
            {
                p++;
                continue;
            }
            if (c == '(')
            {
                int close = body.IndexOf(')', p);
                if (close < 0) return null;
                if (last is { } code)
                    foreach (string v in body[(p + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) codes[code].Add(value);
                p = close + 1;
                continue;
            }
            int end = p;
            while (end < body.Length && Uri.IsHexDigit(body[end])) end++;
            if (end == p)
            {
                p++; // something unexpected: skipped
                continue;
            }
            if (byte.TryParse(body[p..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsed))
            {
                codes.TryAdd(parsed, []);
                last = parsed;
            }
            p = end;
        }
        return codes;
    }

    /// <summary>What a screen says it can do: power, and which inputs (empty when it doesn't list them).</summary>
    public static (bool Power, bool Input, List<uint> Inputs) Supports(Dictionary<byte, List<uint>>? vcp) =>
        vcp is null ? (false, false, []) : (vcp.ContainsKey(PowerMode), vcp.ContainsKey(InputSource), vcp.TryGetValue(InputSource, out List<uint>? inputs) ? inputs : []);
}
