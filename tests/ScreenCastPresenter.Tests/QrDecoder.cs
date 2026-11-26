using System.Text;

namespace ScreenCastPresenter.Tests;

/// <summary>
/// A small QR decoder written from ISO/IEC 18004 for the tests, sharing no code with the app's
/// encoder: it reads the format information, unmasks, reads the zigzag, de-interleaves the blocks,
/// checks every block's Reed–Solomon syndromes are zero, and parses byte mode. If the app's codes
/// decode here, phones will read them too.
/// </summary>
internal static class QrDecoder
{
    // Level M: EC codewords per block; (blocks, data codewords) for the two groups. Versions 1–10.
    private static readonly (int Ec, int B1, int D1, int B2, int D2)[] M =
    [
        default,
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
    ];

    private static readonly int[][] Align =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];

    static QrDecoder()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    public sealed record Result(string Text, int Version, int Mask, int Level);

    /// <summary>Decodes modules [row, column] (true = dark, no quiet zone). Throws when anything is wrong.</summary>
    public static Result Decode(bool[,] m)
    {
        int size = m.GetLength(0);
        if (m.GetLength(1) != size || (size - 17) % 4 != 0) throw new InvalidDataException("not a QR size");
        int version = (size - 17) / 4;
        if (version is < 1 or > 10) throw new InvalidDataException("version out of range");

        // Format information, both copies, bit 14 first.
        (int r, int c)[] first = [.. Enumerable.Range(0, 6).Select(i => (i, 8)), (7, 8), (8, 8), (8, 7), .. Enumerable.Range(9, 6).Select(i => (8, 14 - i))];
        (int r, int c)[] second = [.. Enumerable.Range(0, 8).Select(i => (8, size - 1 - i)), .. Enumerable.Range(8, 7).Select(i => (size - 15 + i, 8))];
        int Read((int r, int c)[] at)
        {
            int bits = 0;
            for (int i = 0; i < 15; i++) if (m[at[i].r, at[i].c]) bits |= 1 << i;
            return bits ^ 0x5412;
        }
        int format = Read(first);
        if (Read(second) != format) throw new InvalidDataException("the two format copies differ");
        int data5 = format >> 10;
        if (Bch(data5, 0x537, 10) != (format & 0x3FF)) throw new InvalidDataException("format BCH check failed");
        int level = data5 >> 3, mask = data5 & 7;
        if (!m[size - 8, 8]) throw new InvalidDataException("dark module missing");

        if (version >= 7)
        {
            int v1 = 0, v2 = 0;
            for (int i = 0; i < 18; i++)
            {
                int a = size - 11 + i % 3, b = i / 3;
                if (m[b, a]) v1 |= 1 << i;
                if (m[a, b]) v2 |= 1 << i;
            }
            if (v1 != v2 || v1 >> 12 != version || Bch(version, 0x1F25, 12) != (v1 & 0xFFF)) throw new InvalidDataException("version information wrong");
        }
        CheckFinders(m);
        CheckTiming(m);

        bool[,] reserved = Reserved(version, size);
        var bits = new List<bool>();
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int v = 0; v < size; v++)
            {
                int row = upward ? size - 1 - v : v;
                for (int j = 0; j < 2; j++)
                {
                    int col = right - j;
                    if (reserved[row, col]) continue;
                    bits.Add(m[row, col] ^ Masked(mask, row, col));
                }
            }
        }

        var (ec, b1, d1, b2, d2) = M[version];
        int blocks = b1 + b2, total = b1 * (d1 + ec) + b2 * (d2 + ec);
        var codewords = new int[total];
        for (int i = 0; i < total; i++)
        {
            int b = 0;
            for (int k = 0; k < 8; k++) b = (b << 1) | (bits[i * 8 + k] ? 1 : 0);
            codewords[i] = b;
        }
        // De-interleave: data column by column (short blocks run out first), then EC column by column.
        var blockData = Enumerable.Range(0, blocks).Select(i => new List<int>()).ToList();
        var blockEc = Enumerable.Range(0, blocks).Select(i => new List<int>()).ToList();
        int p = 0;
        for (int i = 0; i < Math.Max(d1, d2); i++)
        for (int b = 0; b < blocks; b++)
            if (i < (b < b1 ? d1 : d2)) blockData[b].Add(codewords[p++]);
        for (int i = 0; i < ec; i++)
        for (int b = 0; b < blocks; b++)
            blockEc[b].Add(codewords[p++]);

        for (int b = 0; b < blocks; b++)
        {
            int[] all = [.. blockData[b], .. blockEc[b]];
            for (int s = 0; s < ec; s++)
            {
                int syndrome = 0, alpha = Exp[s];
                foreach (int c in all) syndrome = Mul(syndrome, alpha) ^ c;
                if (syndrome != 0) throw new InvalidDataException($"block {b} syndrome {s} is not zero");
            }
        }

        var stream = blockData.SelectMany(d => d).ToList();
        int bit = 0;
        int Take(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, bit++) v = (v << 1) | ((stream[bit >> 3] >> (7 - (bit & 7))) & 1);
            return v;
        }
        if (Take(4) != 0b0100) throw new InvalidDataException("not byte mode");
        int count = Take(version <= 9 ? 8 : 16);
        var bytes = new byte[count];
        for (int i = 0; i < count; i++) bytes[i] = (byte)Take(8);
        return new Result(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), version, mask, level);
    }

    private static int Bch(int data, int poly, int degree)
    {
        int rem = data << degree;
        int top = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)poly);
        for (int i = 31; i >= top; i--)
            if ((rem >> i & 1) != 0) rem ^= poly << (i - top);
        return rem;
    }

    private static bool Masked(int mask, int i, int j) => mask switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => i * j % 2 + i * j % 3 == 0,
        6 => (i * j % 2 + i * j % 3) % 2 == 0,
        _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
    };

    private static bool[,] Reserved(int version, int size)
    {
        var r = new bool[size, size];
        void Box(int row, int col, int h, int w)
        {
            for (int y = row; y < row + h; y++)
            for (int x = col; x < col + w; x++)
                if (y >= 0 && x >= 0 && y < size && x < size) r[y, x] = true;
        }
        Box(0, 0, 9, 9);             // top-left finder, separator and format
        Box(0, size - 8, 9, 8);      // top-right finder, separator and format
        Box(size - 8, 0, 8, 9);      // bottom-left finder, separator, format and the dark module
        for (int i = 0; i < size; i++)
        {
            r[6, i] = true;
            r[i, 6] = true;
        }
        int[] a = Align[version];
        foreach (int y in a)
        foreach (int x in a)
        {
            bool onFinder = y == 6 && x == 6 || y == 6 && x == a[^1] || y == a[^1] && x == 6;
            if (!onFinder) Box(y - 2, x - 2, 5, 5);
        }
        if (version >= 7)
        {
            Box(0, size - 11, 6, 3);
            Box(size - 11, 0, 3, 6);
        }
        return r;
    }

    private static void CheckFinders(bool[,] m)
    {
        int size = m.GetLength(0);
        foreach ((int top, int left) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
        {
            for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
            {
                int d = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                if (m[top + y, left + x] != (d != 2)) throw new InvalidDataException($"finder at ({top},{left}) is wrong");
            }
        }
    }

    private static void CheckTiming(bool[,] m)
    {
        int size = m.GetLength(0);
        for (int i = 8; i < size - 8; i++)
            if (m[6, i] != (i % 2 == 0) || m[i, 6] != (i % 2 == 0)) throw new InvalidDataException("timing pattern wrong");
    }

    /// <summary>
    /// Reads a code back from a picture of it: finds the dark bounding box of the code itself
    /// (inside its white card), divides it into <paramref name="modules"/> squares and samples
    /// each centre. Pixels are BGRA rows of <paramref name="stride"/> bytes.
    /// </summary>
    public static bool[,] Sample(byte[] bgra, int stride, Rectangle card, int modules)
    {
        static bool Dark(byte[] px, int stride, int x, int y)
        {
            int i = y * stride + x * 4;
            return (px[i] + px[i + 1] + px[i + 2]) / 3 < 128;
        }
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = card.Top; y < card.Bottom; y++)
        for (int x = card.Left; x < card.Right; x++)
        {
            if (!Dark(bgra, stride, x, y)) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        if (right < 0) throw new InvalidDataException("no dark modules found");
        float cellW = (right - left + 1) / (float)modules, cellH = (bottom - top + 1) / (float)modules;
        var m = new bool[modules, modules];
        for (int y = 0; y < modules; y++)
        for (int x = 0; x < modules; x++)
            m[y, x] = Dark(bgra, stride, (int)(left + (x + 0.5f) * cellW), (int)(top + (y + 0.5f) * cellH));
        return m;
    }
}
