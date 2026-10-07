namespace Qwlsi.Core;

/// <summary>Percentile + binary morphology equivalents of numpy / scipy.ndimage calls used by the reference code.</summary>
public static class ImageOps
{
    /// <summary>np.percentile (linear interpolation) of data[0..n). Destroys `scratch` (copy of the data).</summary>
    public static double Percentile(float[] scratch, int n, double q)
    {
        double pos = q / 100.0 * (n - 1); int lo = (int)Math.Floor(pos); double frac = pos - lo;
        Select(scratch, n, lo);
        double v = scratch[lo];
        if (frac > 0 && lo + 1 < n)
        {
            float hi = float.MaxValue; for (int i = lo + 1; i < n; i++) if (scratch[i] < hi) hi = scratch[i];
            v += frac * (hi - v);
        }
        return v;
    }

    /// <summary>Quickselect: afterwards a[k] is the k-th smallest and a[k+1..n) >= a[k].</summary>
    static void Select(float[] a, int n, int k)
    {
        int l = 0, r = n - 1;
        while (l < r)
        {
            float pivot = a[l + ((r - l) >> 1)]; int i = l, j = r;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
            }
            if (k <= j) r = j; else if (k >= i) l = i; else return;
        }
    }

    // 4-connected (cross) structuring element, border value 0 (scipy defaults)
    static void Erode(byte[] src, byte[] dst, int h, int w)
    {
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                dst[i] = (byte)((src[i] != 0 && y > 0 && src[i - w] != 0 && y < h - 1 && src[i + w] != 0 && x > 0 && src[i - 1] != 0 && x < w - 1 && src[i + 1] != 0) ? 1 : 0);
            }
        });
    }

    static void Dilate(byte[] src, byte[] dst, int h, int w)
    {
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                bool v = src[i] != 0 || (y > 0 && src[i - w] != 0) || (y < h - 1 && src[i + w] != 0) || (x > 0 && src[i - 1] != 0) || (x < w - 1 && src[i + 1] != 0);
                dst[i] = (byte)(v ? 1 : 0);
            }
        });
    }

    /// <summary>binary_opening(iterations=it): erosion x it then dilation x it.</summary>
    public static byte[] Open(byte[] m, int h, int w, int it)
    {
        var a = (byte[])m.Clone(); var b = new byte[m.Length];
        for (int k = 0; k < it; k++) { Erode(a, b, h, w); (a, b) = (b, a); }
        for (int k = 0; k < it; k++) { Dilate(a, b, h, w); (a, b) = (b, a); }
        return a;
    }

    public static byte[] Erode(byte[] m, int h, int w, int it)
    {
        var a = (byte[])m.Clone(); var b = new byte[m.Length];
        for (int k = 0; k < it; k++) { Erode(a, b, h, w); (a, b) = (b, a); }
        return a;
    }

    /// <summary>binary_fill_holes: background pixels (4-connected) not reachable from the border become foreground.</summary>
    public static byte[] FillHoles(byte[] m, int h, int w)
    {
        var reached = new byte[m.Length]; var stack = new Stack<int>();
        void Seed(int y, int x) { int i = y * w + x; if (m[i] == 0 && reached[i] == 0) { reached[i] = 1; stack.Push(i); } }
        for (int x = 0; x < w; x++) { Seed(0, x); Seed(h - 1, x); }
        for (int y = 0; y < h; y++) { Seed(y, 0); Seed(y, w - 1); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), y = i / w, x = i % w;
            if (y > 0) Seed(y - 1, x); if (y < h - 1) Seed(y + 1, x); if (x > 0) Seed(y, x - 1); if (x < w - 1) Seed(y, x + 1);
        }
        var o = new byte[m.Length]; for (int i = 0; i < o.Length; i++) o[i] = (byte)((m[i] != 0 || reached[i] == 0) ? 1 : 0);
        return o;
    }

    /// <summary>make_mask(A1, A2, frac=0.15, erode=4) on interleaved complex lobes.</summary>
    public static byte[] MakeMask(float[] A1, float[] A2, int M, double frac = 0.15, int erode = 4)
    {
        long n = (long)M * M; var a = new float[n];
        Parallel.For(0, M, y =>
        {
            for (long i = (long)y * M; i < (long)(y + 1) * M; i++)
            {
                float m1 = MathF.Sqrt(A1[2 * i] * A1[2 * i] + A1[2 * i + 1] * A1[2 * i + 1]);
                float m2 = MathF.Sqrt(A2[2 * i] * A2[2 * i] + A2[2 * i + 1] * A2[2 * i + 1]);
                a[i] = Math.Min(m1, m2);
            }
        });
        var tmp = (float[])a.Clone(); float thr = (float)(frac * (float)Percentile(tmp, (int)n, 95));
        var m = new byte[n]; for (long i = 0; i < n; i++) m[i] = (byte)(a[i] > thr ? 1 : 0);
        m = Open(m, M, M, 3); m = FillHoles(m, M, M);
        return erode > 0 ? Erode(m, M, M, erode) : m;
    }
}
