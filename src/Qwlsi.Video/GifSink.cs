namespace Qwlsi.Video;

/// <summary>Native animated GIF89a: per-frame 256-colour local palette (median cut on a 15-bit histogram, light ordered dithering), LZW, Netscape loop extension, streamed frame by frame.</summary>
sealed class GifSink : SinkBase
{
    readonly string path; readonly FileStream fs; readonly int delay; readonly byte[] idx; readonly int[] hist = new int[32768]; readonly byte[] lut = new byte[32768]; readonly byte[] pal = new byte[768];
    static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    public GifSink(string file, int w, int h, int fps, int loops) : base(w, h)
    {
        path = file; delay = Math.Max(2, (int)Math.Round(100.0 / fps)); idx = new byte[w * h]; fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        fs.Write("GIF89a"u8); W16(w); W16(h); fs.WriteByte(0x70); fs.WriteByte(0); fs.WriteByte(0);        // no global colour table (each frame carries its own)
        fs.Write(new byte[] { 0x21, 0xFF, 0x0B }); fs.Write("NETSCAPE2.0"u8); fs.Write(new byte[] { 3, 1 }); W16(loops); fs.WriteByte(0);
    }

    public override string Description => $"GIF ({Width}x{Height}, 256 colours per frame, {delay} cs per frame)";
    void W16(int v) { fs.WriteByte((byte)v); fs.WriteByte((byte)(v >> 8)); }

    public override void WriteFrame(ReadOnlySpan<byte> bgra)
    {
        Check(bgra); Quantize(bgra);
        fs.Write(new byte[] { 0x21, 0xF9, 4, 0x04 }); W16(delay); fs.WriteByte(0); fs.WriteByte(0);          // graphic control: do not dispose, no transparency
        fs.WriteByte(0x2C); W16(0); W16(0); W16(Width); W16(Height); fs.WriteByte(0x87); fs.Write(pal);        // image descriptor with a 256-entry local colour table
        fs.WriteByte(8); Lzw(); fs.WriteByte(0);
    }

    protected override void Finish() { fs.WriteByte(0x3B); fs.Flush(); }
    protected override void Cleanup(bool keep) { try { fs.Dispose(); } catch { } if (!keep) try { File.Delete(path); } catch { } }

    // ---------------------------------------------------------------- quantiser
    void Quantize(ReadOnlySpan<byte> bgra)
    {
        Array.Clear(hist); int n = Width * Height;
        for (int i = 0; i < n; i++) { int o = i * 4; hist[(bgra[o + 2] >> 3) << 10 | (bgra[o + 1] >> 3) << 5 | (bgra[o] >> 3)]++; }
        var first = new Box(Enumerable.Range(0, 32768).Where(c => hist[c] > 0).ToArray()); first.Measure(hist); var boxes = new List<Box> { first };
        while (boxes.Count < 256)
        {
            int bi = -1; long best = 0; for (int i = 0; i < boxes.Count; i++) { var b = boxes[i]; if (b.Colors.Length > 1 && b.Score > best) { best = b.Score; bi = i; } }
            if (bi < 0) break; var (a, c) = boxes[bi].Split(hist); a.Measure(hist); c.Measure(hist); boxes[bi] = a; boxes.Add(c);
        }
        Array.Clear(pal); var known = new bool[32768];
        for (int k = 0; k < boxes.Count; k++)
        {
            double r = 0, g = 0, b = 0, t = 0; foreach (int c in boxes[k].Colors) { int cnt = hist[c]; r += cnt * ((c >> 10) & 31); g += cnt * ((c >> 5) & 31); b += cnt * (c & 31); t += cnt; }
            pal[3 * k] = (byte)Math.Clamp((int)(r / t * 8 + 4), 0, 255); pal[3 * k + 1] = (byte)Math.Clamp((int)(g / t * 8 + 4), 0, 255); pal[3 * k + 2] = (byte)Math.Clamp((int)(b / t * 8 + 4), 0, 255);
            foreach (int c in boxes[k].Colors) { lut[c] = (byte)k; known[c] = true; }
        }
        int pc = boxes.Count;                                   // dithering can land in a 15-bit bin without histogram entry: nearest palette entry, cached
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4, d = (Bayer[(y & 3) * 4 + (x & 3)] - 8) / 2;           // about +-0.5 of a 5-bit step: light ordered dither
                int r5 = Math.Clamp((bgra[o + 2] + d) >> 3, 0, 31), g5 = Math.Clamp((bgra[o + 1] + d) >> 3, 0, 31), b5 = Math.Clamp((bgra[o] + d) >> 3, 0, 31), c = r5 << 10 | g5 << 5 | b5;
                if (!known[c]) { lut[c] = Nearest(r5 * 8 + 4, g5 * 8 + 4, b5 * 8 + 4, pc); known[c] = true; }
                idx[y * Width + x] = lut[c];
            }
    }

    byte Nearest(int r, int g, int b, int count)
    {
        int best = 0, bd = int.MaxValue; for (int k = 0; k < count; k++) { int dr = pal[3 * k] - r, dg = pal[3 * k + 1] - g, db = pal[3 * k + 2] - b, d = dr * dr * 3 + dg * dg * 4 + db * db * 2; if (d < bd) { bd = d; best = k; } }
        return (byte)best;
    }

    sealed class Box
    {
        public int[] Colors; public long Score; public int Axis;
        public Box(int[] colors) { Colors = colors; }
        public void Measure(int[] hist)
        {
            int lo0 = 31, lo1 = 31, lo2 = 31, hi0 = 0, hi1 = 0, hi2 = 0; long pix = 0;
            foreach (int c in Colors) { int r = (c >> 10) & 31, g = (c >> 5) & 31, b = c & 31; pix += hist[c]; if (r < lo0) lo0 = r; if (r > hi0) hi0 = r; if (g < lo1) lo1 = g; if (g > hi1) hi1 = g; if (b < lo2) lo2 = b; if (b > hi2) hi2 = b; }
            int e0 = (hi0 - lo0) * 3, e1 = (hi1 - lo1) * 4, e2 = (hi2 - lo2) * 2; Axis = e0 >= e1 && e0 >= e2 ? 0 : e1 >= e2 ? 1 : 2; Score = pix * Math.Max(1, Math.Max(e0, Math.Max(e1, e2)));
        }
        public (Box, Box) Split(int[] hist)
        {
            int sh = Axis == 0 ? 10 : Axis == 1 ? 5 : 0; var sorted = Colors.OrderBy(c => (c >> sh) & 31).ToArray(); long total = 0; foreach (int c in sorted) total += hist[c];
            long acc = 0; int cut = 1; for (int i = 0; i < sorted.Length - 1; i++) { acc += hist[sorted[i]]; cut = i + 1; if (acc * 2 >= total) break; }
            return (new Box(sorted[..cut]), new Box(sorted[cut..]));
        }
    }

    // ---------------------------------------------------------------- LZW (variable code size 9..12, clear code on a full table)
    readonly byte[] block = new byte[255]; int blen, cur, shift;
    void Emit(int code, int size) { cur |= code << shift; shift += size; while (shift >= 8) { PutByte((byte)cur); cur >>= 8; shift -= 8; } }
    void PutByte(byte b) { block[blen++] = b; if (blen == 255) FlushBlock(); }
    void FlushBlock() { if (blen == 0) return; fs.WriteByte((byte)blen); fs.Write(block, 0, blen); blen = 0; }

    void Lzw()
    {
        const int clear = 256, eoi = 257; blen = 0; cur = 0; shift = 0; int size = 9, next = 258; var keys = new int[8192]; var vals = new short[8192]; Array.Fill(keys, -1);
        Emit(clear, size); int prefix = idx[0];
        for (int i = 1; i < idx.Length; i++)
        {
            int k = idx[i], key = prefix << 8 | k, h = (int)(((uint)key * 2654435761u) >> 19);
            while (keys[h] != -1 && keys[h] != key) h = (h + 1) & 8191;
            if (keys[h] == key) { prefix = vals[h]; continue; }
            Emit(prefix, size);
            if (next == 4096) { Emit(clear, size); next = 258; size = 9; Array.Fill(keys, -1); }
            else { if (next >= 1 << size) size++; keys[h] = key; vals[h] = (short)next++; }
            prefix = k;
        }
        Emit(prefix, size); Emit(eoi, size); if (shift > 0) { PutByte((byte)cur); cur = 0; shift = 0; } FlushBlock();
    }
}
