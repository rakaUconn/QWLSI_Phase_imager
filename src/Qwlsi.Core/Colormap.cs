namespace Qwlsi.Core;

public enum ColormapKind { Viridis, Gray, Jet, Diverging, Twilight }

/// <summary>256-entry colour LUTs (piecewise-linear through key colours) and float-map -> BGRA rendering. UI independent.</summary>
public static class Colormap
{
    static readonly Dictionary<ColormapKind, (byte r, byte g, byte b)[]> Stops = new()
    {
        [ColormapKind.Viridis] = new (byte, byte, byte)[] { (68, 1, 84), (72, 40, 120), (62, 74, 137), (49, 104, 142), (38, 130, 142), (31, 158, 137), (53, 183, 121), (109, 205, 89), (180, 222, 44), (253, 231, 37) },
        [ColormapKind.Gray] = new (byte, byte, byte)[] { (0, 0, 0), (255, 255, 255) },
        [ColormapKind.Jet] = new (byte, byte, byte)[] { (0, 0, 143), (0, 0, 255), (0, 255, 255), (255, 255, 0), (255, 0, 0), (128, 0, 0) },
        [ColormapKind.Diverging] = new (byte, byte, byte)[] { (59, 76, 192), (141, 176, 254), (221, 221, 221), (245, 156, 125), (180, 4, 38) },
        [ColormapKind.Twilight] = new (byte, byte, byte)[] { (226, 217, 226), (140, 164, 200), (82, 100, 175), (62, 40, 100), (50, 18, 59), (100, 30, 70), (170, 70, 70), (206, 150, 120), (226, 217, 226) },
    };
    static readonly Dictionary<ColormapKind, uint[]> Luts = new();

    public static uint[] Lut(ColormapKind k)
    {
        lock (Luts)
        {
            if (Luts.TryGetValue(k, out var l)) return l;
            var s = Stops[k]; l = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0 * (s.Length - 1); int j = Math.Min((int)t, s.Length - 2); double f = t - j;
                byte R = (byte)Math.Round(s[j].r + f * (s[j + 1].r - s[j].r)), G = (byte)Math.Round(s[j].g + f * (s[j + 1].g - s[j].g)), B = (byte)Math.Round(s[j].b + f * (s[j + 1].b - s[j].b));
                l[i] = 0xFF000000u | (uint)R << 16 | (uint)G << 8 | B;   // BGRA32 little-endian = 0xAARRGGBB
            }
            return Luts[k] = l;
        }
    }

    /// <summary>Maps data to colours; NaN / non-finite -> `nanColor` (0xAARRGGBB). dest length = data length.</summary>
    public static void Render(float[] data, float vmin, float vmax, ColormapKind kind, uint[] dest, uint nanColor = 0xFF202020)
    {
        var lut = Lut(kind); float scale = vmax > vmin ? 255f / (vmax - vmin) : 0f;
        Parallel.For(0, (data.Length + 65535) / 65536, blk =>
        {
            int a = blk * 65536, b = Math.Min(data.Length, a + 65536);
            for (int i = a; i < b; i++) { float v = data[i]; if (float.IsNaN(v) || float.IsInfinity(v)) { dest[i] = nanColor; continue; } int idx = (int)((v - vmin) * scale + 0.5f); dest[i] = lut[Math.Clamp(idx, 0, 255)]; }
        });
    }

    /// <summary>Robust display range: percentiles of the finite values (e.g. 1 and 99).</summary>
    public static (float lo, float hi) AutoRange(float[] data, double loPct = 1, double hiPct = 99)
    {
        var v = new List<float>(data.Length / 2); long stride = Math.Max(1, data.Length / 400000);
        for (long i = 0; i < data.Length; i += stride) if (!float.IsNaN(data[i]) && !float.IsInfinity(data[i])) v.Add(data[i]);
        if (v.Count == 0) return (0, 1); v.Sort();
        float lo = v[(int)(loPct / 100 * (v.Count - 1))], hi = v[(int)(hiPct / 100 * (v.Count - 1))];
        if (hi <= lo) { lo = v[0]; hi = v[^1]; if (hi <= lo) hi = lo + 1; }
        return (lo, hi);
    }
}
