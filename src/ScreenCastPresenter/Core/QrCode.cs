using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// A QR code made on the PC (FR-81), no internet and no third-party code: ISO/IEC 18004 byte mode
/// (UTF-8), error correction level M (about 15% of the code can be damaged or covered), versions 1
/// to 10, so up to 213 bytes: plenty for a link or Wi-Fi details. The mask with the lowest penalty
/// is chosen, as the standard says. <see cref="Encode"/> returns the modules, true = dark, without
/// the quiet zone (leave 4 modules of white around it).
/// </summary>
internal static class QrCode
{
    public const int MaxVersion = 10;

    /// <summary>Per version (index 1–10): EC codewords per block, then (blocks, data codewords) for up to two groups. Level M.</summary>
    private static readonly (int Ec, int Blocks1, int Data1, int Blocks2, int Data2)[] LevelM =
    [
        default,
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
    ];

    private static readonly int[][] AlignmentCentres =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    /// <summary>The most bytes a code can hold (version 10-M).</summary>
    public static int MaxBytes => Capacity(MaxVersion) - 3; // mode (4 bits) + 16-bit count, rounded up

    /// <summary>Data codewords a version holds at level M.</summary>
    public static int DataCodewords(int version) => LevelM[version].Blocks1 * LevelM[version].Data1 + LevelM[version].Blocks2 * LevelM[version].Data2;

    private static int Capacity(int version) => DataCodewords(version);

    /// <summary>The smallest version that holds <paramref name="bytes"/> bytes, or 0 if none does.</summary>
    public static int VersionFor(int bytes)
    {
        for (int v = 1; v <= MaxVersion; v++)
        {
            int bits = 4 + CountBits(v) + bytes * 8;
            if (bits <= DataCodewords(v) * 8) return v;
        }
        return 0;
    }

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    /// <summary>The code for <paramref name="text"/>, or null when it's too long for a QR code here.</summary>
    public static bool[,]? Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = VersionFor(data.Length);
        if (version == 0) return null;
        byte[] codewords = Interleave(DataCodewordsFor(data, version), version);

        int size = Size(version);
        bool[,]? best = null;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var modules = new bool[size, size];
            bool[,] function = FunctionPatterns(version, modules);
            PlaceData(modules, function, codewords);
            ApplyMask(modules, function, mask);
            PlaceFormat(modules, mask);
            if (version >= 7) PlaceVersion(modules, version);
            int penalty = Penalty(modules);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = modules;
            }
        }
        return best;
    }

    public static int Size(int version) => 17 + 4 * version;

    // ───────────────────────────── data ─────────────────────────────

    /// <summary>Mode, count, the bytes, terminator, padding to the version's data codewords.</summary>
    internal static byte[] DataCodewordsFor(byte[] data, int version)
    {
        var bits = new List<bool>();
        void Add(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
        Add(0b0100, 4); // byte mode
        Add(data.Length, CountBits(version));
        foreach (byte b in data) Add(b, 8);
        int capacity = DataCodewords(version) * 8;
        Add(0, Math.Min(4, capacity - bits.Count)); // terminator
        while (bits.Count % 8 != 0) bits.Add(false);
        var bytes = new List<byte>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            int b = 0;
            for (int j = 0; j < 8; j++) b = (b << 1) | (bits[i + j] ? 1 : 0);
            bytes.Add((byte)b);
        }
        for (int pad = 0; bytes.Count < DataCodewords(version); pad++) bytes.Add(pad % 2 == 0 ? (byte)0xEC : (byte)0x11);
        return [.. bytes];
    }

    /// <summary>Splits into blocks, adds each block's error correction, and interleaves them, as the standard orders them.</summary>
    internal static byte[] Interleave(byte[] data, int version)
    {
        var (ec, b1, d1, b2, d2) = LevelM[version];
        var blocks = new List<byte[]>();
        int offset = 0;
        for (int i = 0; i < b1 + b2; i++)
        {
            int length = i < b1 ? d1 : d2;
            blocks.Add(data[offset..(offset + length)]);
            offset += length;
        }
        var ecBlocks = blocks.Select(b => ReedSolomon(b, ec)).ToList();
        var result = new List<byte>();
        for (int i = 0; i < Math.Max(d1, d2); i++)
        {
            foreach (byte[] block in blocks)
            {
                if (i < block.Length) result.Add(block[i]);
            }
        }
        for (int i = 0; i < ec; i++)
        {
            foreach (byte[] block in ecBlocks) result.Add(block[i]);
        }
        return [.. result];
    }

    // ───────────────────────────── Reed–Solomon over GF(256), polynomial 0x11D ─────────────────────────────

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    internal static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>The <paramref name="count"/> error-correction codewords for <paramref name="data"/>.</summary>
    internal static byte[] ReedSolomon(byte[] data, int count)
    {
        // The generator (x - α^0)(x - α^1)…(x - α^(count-1)), without its leading 1, highest power first.
        var divisor = new byte[count];
        divisor[count - 1] = 1;
        byte root = 1;
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < count; j++)
            {
                divisor[j] = Multiply(divisor[j], root);
                if (j + 1 < count) divisor[j] ^= divisor[j + 1];
            }
            root = Multiply(root, 2);
        }
        // The remainder of data × x^count divided by it.
        var remainder = new byte[count];
        foreach (byte d in data)
        {
            byte factor = (byte)(d ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, count - 1);
            remainder[count - 1] = 0;
            for (int j = 0; j < count; j++) remainder[j] ^= Multiply(divisor[j], factor);
        }
        return remainder;
    }

    // ───────────────────────────── layout ─────────────────────────────

    /// <summary>Draws the finder, separator, timing and alignment patterns and the dark module; returns which modules are reserved.</summary>
    private static bool[,] FunctionPatterns(int version, bool[,] m)
    {
        int size = m.GetLength(0);
        var f = new bool[size, size];
        void Set(int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            m[y, x] = dark;
            f[y, x] = true;
        }
        foreach ((int cx, int cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(cx + dx, cy + dy, d != 2 && d != 4); // 3×3 core, white ring, dark ring, white separator
            }
        }
        for (int i = 8; i < size - 8; i++)
        {
            Set(i, 6, i % 2 == 0);
            Set(6, i, i % 2 == 0);
        }
        int[] centres = AlignmentCentres[version];
        int last = centres.Length - 1;
        for (int i = 0; i < centres.Length; i++)
        for (int j = 0; j < centres.Length; j++)
        {
            if (i == 0 && j == 0 || i == 0 && j == last || i == last && j == 0) continue; // where the finder patterns are
            int cx = centres[i], cy = centres[j];
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
        // Format areas (written later) and the dark module.
        for (int i = 0; i < 9; i++)
        {
            f[8, i] = true;
            f[i, 8] = true;
        }
        for (int i = 0; i < 8; i++)
        {
            f[8, size - 1 - i] = true;
            f[size - 1 - i, 8] = true;
        }
        Set(8, size - 8, true); // the dark module, at (4V+9, 8) in row-major terms
        if (version >= 7)
        {
            for (int i = 0; i < 6; i++)
            for (int j = 0; j < 3; j++)
            {
                f[i, size - 11 + j] = true;
                f[size - 11 + j, i] = true;
            }
        }
        return f;
    }

    /// <summary>The data bits in the standard's zigzag: two columns at a time from the right, up then down, skipping the timing column.</summary>
    private static void PlaceData(bool[,] m, bool[,] function, byte[] codewords)
    {
        int size = m.GetLength(0);
        int bit = 0, total = codewords.Length * 8;
        bool upward = true;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                int y = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (function[y, x]) continue;
                    if (bit < total)
                    {
                        m[y, x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        bit++;
                    }
                    // Remainder bits are left light.
                }
            }
            upward = !upward;
        }
    }

    internal static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    private static void ApplyMask(bool[,] m, bool[,] function, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (!function[y, x] && MaskBit(mask, x, y)) m[y, x] = !m[y, x];
        }
    }

    /// <summary>The 15 format bits for level M and <paramref name="mask"/>: BCH(15,5) with 0x537, XORed with 0x5412.</summary>
    internal static int FormatBits(int levelBits, int mask)
    {
        int data = (levelBits << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return ((data << 10) | rem) ^ 0x5412;
    }

    /// <summary>The 18 version bits (versions 7 and up): BCH(18,6) with 0x1F25.</summary>
    internal static int VersionBits(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return (version << 12) | rem;
    }

    private const int LevelMBits = 0b00;

    private static void PlaceFormat(bool[,] m, int mask)
    {
        int size = m.GetLength(0);
        int bits = FormatBits(LevelMBits, mask);
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        // First copy, around the top-left finder.
        for (int i = 0; i <= 5; i++) m[i, 8] = Bit(i);
        m[7, 8] = Bit(6);
        m[8, 8] = Bit(7);
        m[8, 7] = Bit(8);
        for (int i = 9; i < 15; i++) m[8, 14 - i] = Bit(i);
        // Second copy, split under the top-right and beside the bottom-left finder.
        for (int i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(i);
        for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(i);
        m[size - 8, 8] = true; // the dark module stays dark
    }

    private static void PlaceVersion(bool[,] m, int version)
    {
        int size = m.GetLength(0);
        int bits = VersionBits(version);
        for (int i = 0; i < 18; i++)
        {
            bool dark = ((bits >> i) & 1) != 0;
            int a = size - 11 + i % 3, b = i / 3;
            m[b, a] = dark;
            m[a, b] = dark;
        }
    }

    /// <summary>The standard's four penalty rules (runs, 2×2 blocks, finder-like patterns, dark balance).</summary>
    internal static int Penalty(bool[,] m)
    {
        int size = m.GetLength(0), penalty = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool same = pass == 0 ? m[a, b] == m[a, b - 1] : m[b, a] == m[b - 1, a];
                    if (same) run++;
                    else
                    {
                        if (run >= 5) penalty += run - 2;
                        run = 1;
                    }
                }
                if (run >= 5) penalty += run - 2;
            }
        }
        for (int y = 0; y < size - 1; y++)
        for (int x = 0; x < size - 1; x++)
        {
            bool c = m[y, x];
            if (m[y, x + 1] == c && m[y + 1, x] == c && m[y + 1, x + 1] == c) penalty += 3;
        }
        bool[] pattern = [true, false, true, true, true, false, true, false, false, false, false];
        for (int a = 0; a < size; a++)
        for (int b = 0; b <= size - 11; b++)
        {
            bool row = true, rowRev = true, col = true, colRev = true;
            for (int k = 0; k < 11; k++)
            {
                row &= m[a, b + k] == pattern[k];
                rowRev &= m[a, b + k] == pattern[10 - k];
                col &= m[b + k, a] == pattern[k];
                colRev &= m[b + k, a] == pattern[10 - k];
            }
            penalty += 40 * ((row ? 1 : 0) + (rowRev ? 1 : 0) + (col ? 1 : 0) + (colRev ? 1 : 0));
        }
        int dark = 0;
        foreach (bool b in m) if (b) dark++;
        int percent = dark * 100 / (size * size);
        penalty += Math.Abs(percent - 50) / 5 * 10;
        return penalty;
    }

    /// <summary>Wi-Fi details in the form phones read: WIFI:T:WPA;S:name;P:password;; (special characters escaped).</summary>
    public static string WiFi(string network, string password, bool open)
    {
        static string Escape(string s) => new([.. s.SelectMany(c => c is '\\' or ';' or ',' or ':' or '"' ? new[] { '\\', c } : [c])]);
        return open ? $"WIFI:T:nopass;S:{Escape(network)};;" : $"WIFI:T:WPA;S:{Escape(network)};P:{Escape(password)};;";
    }
}
