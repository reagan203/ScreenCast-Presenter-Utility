namespace ScreenCastPresenter.Core;

/// <summary>
/// A small animated GIF writer (FR-131), written here so no third-party code is needed: each frame
/// gets its own 256-colour palette (median cut over a 15-bit histogram, which suits screens: flat
/// colours and text stay exact), pixels are matched through a lookup table, and the frames are LZW
/// compressed as the GIF89a specification describes. The animation loops for ever.
/// </summary>
internal sealed class GifEncoder : IDisposable
{
    private readonly Stream _out;
    private readonly int _width, _height;
    private bool _finished;

    /// <param name="output">Written to as frames are added; left open.</param>
    public GifEncoder(Stream output, int width, int height)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(width), "A GIF is 1 to 65,535 pixels a side.");
        _out = output;
        _width = width;
        _height = height;
        WriteAscii("GIF89a");
        WriteShort(width);
        WriteShort(height);
        _out.WriteByte(0x70); // no global colour table; colour resolution 8 bits
        _out.WriteByte(0);    // background colour index
        _out.WriteByte(0);    // pixel aspect ratio: square
        // NETSCAPE2.0: loop for ever.
        _out.Write([0x21, 0xFF, 0x0B]);
        WriteAscii("NETSCAPE2.0");
        _out.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    public int Frames { get; private set; }

    /// <summary>Adds a frame of 32-bit pixels (B, G, R, A in each int's bytes, as Windows bitmaps hold them), shown for <paramref name="delay"/>.</summary>
    public void AddFrame(ReadOnlySpan<int> pixels, TimeSpan delay)
    {
        if (_finished) throw new InvalidOperationException("The GIF is finished.");
        if (pixels.Length < _width * _height) throw new ArgumentException("Not enough pixels for a frame.", nameof(pixels));
        (byte[] palette, byte[] indices) = Quantize(pixels[..(_width * _height)]);
        int hundredths = (int)Math.Clamp(Math.Round(delay.TotalMilliseconds / 10), 2, 65535); // browsers treat 0–1 as 10: say what's meant
        _out.Write([0x21, 0xF9, 0x04, 0x04]); // graphic control: disposal "leave in place", no transparency
        WriteShort(hundredths);
        _out.Write([0x00, 0x00]);
        _out.WriteByte(0x2C); // image descriptor
        WriteShort(0);
        WriteShort(0);
        WriteShort(_width);
        WriteShort(_height);
        _out.WriteByte(0x87); // a local colour table of 2^(7+1) = 256 entries
        _out.Write(palette);
        Lzw(indices, 8);
        Frames++;
    }

    /// <summary>Ends the file (the trailer). Safe to call twice.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    public void Dispose() => Finish();

    // ───────────────────────────── colours ─────────────────────────────

    /// <summary>A 256-colour palette for the frame (768 bytes, RGB) and each pixel's entry in it.</summary>
    internal static (byte[] Palette, byte[] Indices) Quantize(ReadOnlySpan<int> pixels)
    {
        // A histogram of 5 bits a channel: 32,768 colours.
        var counts = new int[32768];
        foreach (int p in pixels) counts[Key(p)]++;
        var used = new List<int>();
        for (int k = 0; k < counts.Length; k++) if (counts[k] > 0) used.Add(k);

        var boxes = new List<List<int>> { used };
        while (boxes.Count < 256)
        {
            // Split the box with the widest spread of colour (weighted by how many pixels it holds).
            int pick = -1;
            long best = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                long score = Spread(boxes[i], out _) * (long)Math.Max(1, boxes[i].Sum(k => counts[k]));
                if (score > best)
                {
                    best = score;
                    pick = i;
                }
            }
            if (pick < 0) break; // every box holds one colour: the palette is exact
            List<int> box = boxes[pick];
            Spread(box, out int channel);
            box.Sort((a, b) => Channel(a, channel).CompareTo(Channel(b, channel)));
            // Split at the median pixel, so busy colours get their own entries.
            long total = box.Sum(k => (long)counts[k]), half = total / 2, run = 0;
            int cut = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                run += counts[box[i]];
                if (run >= half)
                {
                    cut = i + 1;
                    break;
                }
            }
            boxes[pick] = box[..cut];
            boxes.Add(box[cut..]);
        }

        var palette = new byte[768];
        var lookup = new byte[32768];
        for (int i = 0; i < boxes.Count; i++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (int k in boxes[i])
            {
                int c = counts[k];
                r += ((k >> 10) & 31) * c;
                g += ((k >> 5) & 31) * c;
                b += (k & 31) * c;
                n += c;
                lookup[k] = (byte)i;
            }
            if (n == 0) continue;
            palette[i * 3] = Expand((int)(r / n));
            palette[i * 3 + 1] = Expand((int)(g / n));
            palette[i * 3 + 2] = Expand((int)(b / n));
        }
        var indices = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) indices[i] = lookup[Key(pixels[i])];
        return (palette, indices);
    }

    private static int Key(int bgra) => ((bgra >> 19) & 31) << 10 | ((bgra >> 11) & 31) << 5 | ((bgra >> 3) & 31);

    private static int Channel(int key, int channel) => channel switch { 0 => (key >> 10) & 31, 1 => (key >> 5) & 31, _ => key & 31 };

    private static byte Expand(int five) => (byte)(five << 3 | five >> 2);

    private static int Spread(List<int> box, out int channel)
    {
        int best = -1;
        channel = 0;
        for (int c = 0; c < 3; c++)
        {
            int lo = 31, hi = 0;
            foreach (int k in box)
            {
                int v = Channel(k, c);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (hi - lo > best)
            {
                best = hi - lo;
                channel = c;
            }
        }
        return best;
    }

    // ───────────────────────────── LZW ─────────────────────────────

    private void Lzw(byte[] indices, int minCodeSize)
    {
        _out.WriteByte((byte)minCodeSize);
        var block = new List<byte>(255);
        int bitBuffer = 0, bitCount = 0;
        int clear = 1 << minCodeSize, end = clear + 1;
        int codeSize = minCodeSize + 1, next = end + 1;
        var table = new Dictionary<int, int>(4096);

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
                if (block.Count == 255) FlushBlock();
            }
        }
        void FlushBlock()
        {
            if (block.Count == 0) return;
            _out.WriteByte((byte)block.Count);
            _out.Write(block.ToArray());
            block.Clear();
        }

        Emit(clear);
        int prefix = indices.Length > 0 ? indices[0] : 0;
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = prefix << 8 | k;
            if (table.TryGetValue(key, out int code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                // The table is full: start again, as decoders expect.
                Emit(clear);
                table.Clear();
                codeSize = minCodeSize + 1;
                next = end + 1;
            }
            prefix = k;
        }
        if (indices.Length > 0) Emit(prefix);
        Emit(end);
        if (bitCount > 0) block.Add((byte)(bitBuffer & 0xFF));
        FlushBlock();
        _out.WriteByte(0); // block terminator
    }

    private void WriteShort(int value)
    {
        _out.WriteByte((byte)(value & 0xFF));
        _out.WriteByte((byte)(value >> 8 & 0xFF));
    }

    private void WriteAscii(string text)
    {
        foreach (char c in text) _out.WriteByte((byte)c);
    }

    /// <summary>The size to make frames: at most <paramref name="maxWidth"/> wide, keeping the shape, even numbers.</summary>
    public static (int Width, int Height) FitWidth(int width, int height, int maxWidth)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        if (width <= maxWidth) return (width, height);
        double scale = (double)maxWidth / width;
        return (maxWidth, Math.Max(1, (int)Math.Round(height * scale)));
    }
}
