using System.Text;

namespace Qwlsi.Core;

/// <summary>One OSA/ANSI Zernike term. J = (n(n+2)+m)/2; unit rms over the unit disk.</summary>
public sealed record ZTerm(int J, int N, int M, int Noll, string Name);

/// <summary>Coordinate convention of the fit: theta is counter-clockwise from +x, x to the right, y UP (image rows are flipped). The flags undo that.</summary>
public sealed class ZernikeOrientation
{
    /// <summary>Mirror x (use x to the left).</summary>
    public bool FlipX { get; set; }
    /// <summary>Mirror y: false = y up (image rows flipped, the OSA/ANSI default); true = y down (image row order, as in the Python reference).</summary>
    public bool FlipY { get; set; }
}

/// <summary>
/// OSA/ANSI Z80.28 Zernike polynomials to order 10 (66 terms):
///   Z_n^m = N_n^m R_n^|m|(rho) * (cos(m theta) for m &gt;= 0, sin(|m| theta) for m &lt; 0),  N = sqrt((2 - delta_m0)(n+1)),
///   R_n^|m| = sum_s (-1)^s (n-s)! / (s! ((n+|m|)/2 - s)! ((n-|m|)/2 - s)!) rho^(n-2s),  j = (n(n+2)+m)/2.
/// </summary>
public static class ZernikeBasis
{
    public const int MaxOrder = 10;
    static readonly string[] Foil = { "", "coma", "astigmatism", "trefoil", "quatrefoil", "pentafoil", "hexafoil", "heptafoil", "octafoil", "nonafoil", "decafoil" };
    static readonly string[] Level = { "", "secondary ", "tertiary ", "quaternary ", "quinary " };

    static readonly ZTerm[] All = Build();
    static readonly double[][] Rad = All.Select(t => Radial(t.N, Math.Abs(t.M))).ToArray();
    static readonly double[] Norm = All.Select(t => Math.Sqrt((t.M == 0 ? 1 : 2) * (t.N + 1))).ToArray();

    public static int Count(int order) => (order + 1) * (order + 2) / 2;
    public static int Ansi(int n, int m) => (n * (n + 2) + m) / 2;
    /// <summary>Noll (1976) single index of the same polynomial (starts at 1).</summary>
    public static int Noll(int n, int m)
    {
        if (m == 0) return n * (n + 1) / 2 + 1;
        int am = Math.Abs(m); bool cosine = m > 0; int r = n % 4;
        int delta = (cosine && (r == 0 || r == 1)) || (!cosine && (r == 2 || r == 3)) ? 0 : 1;
        return n * (n + 1) / 2 + am + delta;
    }
    public static IReadOnlyList<ZTerm> Terms(int order) => All.Take(Count(order)).ToArray();
    public static ZTerm Term(int j) => All[j];

    static double Fact(int k) { double f = 1; for (int i = 2; i <= k; i++) f *= i; return f; }

    static double[] Radial(int n, int am)
    {
        var c = new double[n + 1];
        for (int s = 0; s <= (n - am) / 2; s++)
            c[n - 2 * s] += ((s & 1) == 0 ? 1 : -1) * Fact(n - s) / (Fact(s) * Fact((n + am) / 2 - s) * Fact((n - am) / 2 - s));
        return c;
    }

    static ZTerm[] Build()
    {
        var l = new List<ZTerm>();
        for (int n = 0; n <= MaxOrder; n++) for (int m = -n; m <= n; m += 2) l.Add(new ZTerm(Ansi(n, m), n, m, Noll(n, m), NameOf(n, m)));
        return l.ToArray();
    }

    /// <summary>Standard names: j=0 piston, 1 vertical tilt, 2 horizontal tilt, 3 oblique astigmatism, 4 defocus, 5 vertical astigmatism, 6 oblique trefoil, ...</summary>
    static string NameOf(int n, int m)
    {
        int am = Math.Abs(m), k = (n - am) / 2;
        if (n == 0) return "piston";
        if (n == 1) return m < 0 ? "vertical tilt" : "horizontal tilt";
        if (n == 2 && m == 0) return "defocus";
        if (m == 0) return (k == 2 ? "" : Level[Math.Min(k - 2, 4)]) + "spherical aberration";
        string orient = am == 1 ? (m < 0 ? "vertical " : "horizontal ") : am == 2 ? (m < 0 ? "oblique " : "vertical ") : (m < 0 ? "oblique " : "horizontal ");
        int lvl = k - (am == 1 ? 1 : 0);   // coma starts at n=3, so primary coma has k=1
        return Level[Math.Min(lvl, 4)] + orient + Foil[am];
    }

    /// <summary>Evaluates the first `count` terms at (rho, theta) into `outv` (theta in the fit's own convention).</summary>
    public static void Eval(int count, double rho, double theta, Span<double> outv)
    {
        Span<double> pw = stackalloc double[MaxOrder + 1]; pw[0] = 1; for (int e = 1; e <= MaxOrder; e++) pw[e] = pw[e - 1] * rho;
        Span<double> cs = stackalloc double[MaxOrder + 1]; Span<double> sn = stackalloc double[MaxOrder + 1];
        int mmax = 0; for (int t = 0; t < count; t++) mmax = Math.Max(mmax, Math.Abs(All[t].M));
        for (int a = 0; a <= mmax; a++) { cs[a] = Math.Cos(a * theta); sn[a] = Math.Sin(a * theta); }
        for (int t = 0; t < count; t++)
        {
            var T = All[t]; double R = 0; var c = Rad[t]; for (int e = T.M < 0 ? -T.M : T.M; e <= T.N; e += 2) R += c[e] * pw[e];
            int am = Math.Abs(T.M); outv[t] = Norm[t] * R * (T.M >= 0 ? cs[am] : sn[am]);
        }
    }
}

/// <summary>An explicit fit circle in pixel coordinates (x right, y down, pixel centres at integers) used instead of the circle fitted to one frame's mask (common-circle option).</summary>
public sealed record ZernikeCircle(double Cx, double Cy, double RadiusPx);

/// <summary>Result of a joint masked least-squares fit of the OSA/ANSI basis over the mask-fitted circle.</summary>
public sealed class ZernikeFit
{
    public int Order; public ZTerm[] Terms = Array.Empty<ZTerm>(); public double[] Coeff = Array.Empty<double>();   // same unit as the fitted map (metres)
    public double Cx, Cy, RadiusPx, PitchM; public int M; public long FitPixels; public ZernikeOrientation Orient = new();
    /// <summary>True when the circle was given explicitly (common circle) instead of being fitted to this frame's mask.</summary>
    public bool CommonCircle;
    public double RadiusM => RadiusPx * PitchM;
    public string Convention => $"OSA/ANSI, theta ccw from +x, x {(Orient.FlipX ? "left" : "right")}, y {(Orient.FlipY ? "down (image rows)" : "up (rows flipped)")}";
    public string PupilDescription => CommonCircle ? "common circle (given)" : "circle fitted to this frame's mask (centroid, radius sqrt(area/pi))";

    /// <summary>Dioptric equivalent [D] of a second-order term (+ = concave): defocus 4*sqrt(3) c / R^2, astigmatism power-vector components 2*sqrt(6) c / R^2. NaN for other terms.</summary>
    public double Diopters(int j) => j switch { 4 => 4 * Math.Sqrt(3) * Coeff[4] / (RadiusM * RadiusM), 3 or 5 => 2 * Math.Sqrt(6) * Coeff[j] / (RadiusM * RadiusM), _ => double.NaN };
}

/// <summary>A non-destructive Zernike removal: the fit, which terms were removed, and the filtered map (the raw map is never modified).</summary>
public sealed class ZernikeRemoval
{
    public ZernikeFit Fit = new(); public bool[] Removed = Array.Empty<bool>(); public float[] Filtered = Array.Empty<float>();
    public double RmsBeforeM, PvBeforeM, RmsAfterM, PvAfterM;
    public string Description => ZernikeTools.Describe(Fit, Removed);
    public static ZernikeRemoval Make(float[] raw, byte[] mask, ZernikeFit fit, bool[] removed)
    {
        var f = ZernikeTools.Subtract(raw, mask, fit, removed); var (r0, p0) = ZernikeTools.RmsPv(raw, mask); var (r1, p1) = ZernikeTools.RmsPv(f, mask);
        return new ZernikeRemoval { Fit = fit, Removed = (bool[])removed.Clone(), Filtered = f, RmsBeforeM = r0, PvBeforeM = p0, RmsAfterM = r1, PvAfterM = p1 };
    }
}

public static class ZernikeTools
{
    public static (double rho, double th) Polar(int x, int y, ZernikeFit f)
    {
        double dx = x - f.Cx, dy = f.Cy - y;                 // y up
        if (f.Orient.FlipX) dx = -dx; if (f.Orient.FlipY) dy = -dy;
        return (Math.Sqrt(dx * dx + dy * dy) / f.RadiusPx, Math.Atan2(dy, dx));
    }

    /// <summary>Joint fit of all terms up to `order` to W (NaN or mask==0 outside) over mask pixels with rho &lt;= 1; circle = mask centroid and radius sqrt(area/pi).</summary>
    public static ZernikeFit Fit(float[] W, byte[] mask, int M, double pitchM, int order = 10, ZernikeOrientation? orient = null, ZernikeCircle? circle = null)
    {
        int K = ZernikeBasis.Count(order); var f = new ZernikeFit { Order = order, Terms = ZernikeBasis.Terms(order).ToArray(), M = M, PitchM = pitchM, Orient = orient ?? new ZernikeOrientation() };
        double sy = 0, sx = 0; long cnt = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) if (mask[(long)y * M + x] != 0) { sy += y; sx += x; cnt++; }
        if (cnt == 0) throw new InvalidOperationException("empty mask");
        f.Cx = sx / cnt; f.Cy = sy / cnt; f.RadiusPx = Math.Sqrt(cnt / Math.PI);
        if (circle != null) { f.Cx = circle.Cx; f.Cy = circle.Cy; f.RadiusPx = circle.RadiusPx; f.CommonCircle = true; }
        var ata = new double[K * K]; var atb = new double[K]; long used = 0; object lk = new();
        Parallel.For(0, M, () => (new double[K * K], new double[K], new double[K], new long[1]), (y, st, loc) =>
        {
            var (la, lb, bb, lc) = loc;
            for (int x = 0; x < M; x++)
            {
                long i = (long)y * M + x; if (mask[i] == 0 || float.IsNaN(W[i])) continue;
                var (rho, th) = Polar(x, y, f); if (rho > 1) continue;
                ZernikeBasis.Eval(K, rho, th, bb); double w = W[i];
                for (int p = 0; p < K; p++) { lb[p] += bb[p] * w; for (int q = p; q < K; q++) la[p * K + q] += bb[p] * bb[q]; }
                lc[0]++;
            }
            return loc;
        }, loc => { lock (lk) { for (int p = 0; p < K; p++) { atb[p] += loc.Item2[p]; for (int q = p; q < K; q++) ata[p * K + q] += loc.Item1[p * K + q]; } used += loc.Item4[0]; } });
        for (int p = 0; p < K; p++) for (int q = 0; q < p; q++) ata[p * K + q] = ata[q * K + p];
        f.Coeff = SolveSym(ata, atb, K); f.FitPixels = used; return f;
    }

    /// <summary>
    /// The common circle of a set of masks: centroid and sqrt(area/pi) of the INTERSECTION (logical AND) of all masks (same rule as the per-frame circle). Streaming: `masks` may yield one
    /// mask at a time. M is taken from the first mask (must be square). Throws InvalidOperationException when there is no mask or the intersection is empty.
    /// </summary>
    public static (ZernikeCircle circle, long intersectionPixels) CommonCircle(IEnumerable<byte[]> masks)
    {
        byte[]? acc = null; int M = 0;
        foreach (var m in masks)
        {
            if (acc == null) { M = (int)Math.Round(Math.Sqrt(m.Length)); if ((long)M * M != m.Length) throw new ArgumentException("mask is not square"); acc = new byte[m.Length]; for (long i = 0; i < m.Length; i++) acc[i] = (byte)(m[i] != 0 ? 1 : 0); }
            else { if (m.Length != acc.Length) throw new ArgumentException("masks differ in size"); for (long i = 0; i < m.Length; i++) if (m[i] == 0) acc[i] = 0; }
        }
        if (acc == null) throw new InvalidOperationException("common circle: no masks");
        double sy = 0, sx = 0; long cnt = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) if (acc[(long)y * M + x] != 0) { sy += y; sx += x; cnt++; }
        if (cnt == 0) throw new InvalidOperationException("common circle: the intersection of all masks is empty (the pupils do not overlap)");
        return (new ZernikeCircle(sx / cnt, sy / cnt, Math.Sqrt(cnt / Math.PI)), cnt);
    }

    /// <summary>Overload with an explicit grid size (validates every mask against M).</summary>
    public static (ZernikeCircle circle, long intersectionPixels) CommonCircle(IEnumerable<byte[]> masks, int M)
        => CommonCircle(masks.Select(m => m.Length == (long)M * M ? m : throw new ArgumentException($"mask length {m.Length} != {M}x{M}")));

    static double[] SolveSym(double[] A, double[] b, int n)   // Gaussian elimination with partial pivoting
    {
        var a = (double[])A.Clone(); var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r * n + c]) > Math.Abs(a[piv * n + c])) piv = r;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c * n + k], a[piv * n + k]) = (a[piv * n + k], a[c * n + k]); (x[c], x[piv]) = (x[piv], x[c]); }
            for (int r = c + 1; r < n; r++) { double fct = a[r * n + c] / a[c * n + c]; for (int k = c; k < n; k++) a[r * n + k] -= fct * a[c * n + k]; x[r] -= fct * x[c]; }
        }
        for (int r = n - 1; r >= 0; r--) { double s = x[r]; for (int k = r + 1; k < n; k++) s -= a[r * n + k] * x[k]; x[r] = s / a[r * n + r]; }
        return x;
    }

    /// <summary>Non-destructive: returns a NEW map = W minus the sum of the terms flagged in `remove` (evaluated at every valid pixel, also where rho &gt; 1). W is untouched.</summary>
    public static float[] Subtract(float[] W, byte[] mask, ZernikeFit fit, bool[] remove)
    {
        int M = fit.M, K = fit.Terms.Length; var idx = Enumerable.Range(0, K).Where(j => j < remove.Length && remove[j]).ToArray(); var o = (float[])W.Clone();
        if (idx.Length == 0) return o; int top = idx.Max() + 1;
        Parallel.For(0, M, () => new double[K], (y, st, bb) =>
        {
            for (int x = 0; x < M; x++)
            {
                long i = (long)y * M + x; if (mask[i] == 0 || float.IsNaN(W[i])) continue;
                var (rho, th) = Polar(x, y, fit); ZernikeBasis.Eval(top, rho, th, bb); double s = 0; foreach (int j in idx) s += fit.Coeff[j] * bb[j];
                o[i] = (float)(W[i] - s);
            }
            return bb;
        }, _ => { });
        return o;
    }

    /// <summary>rms (standard deviation) and peak-to-valley of the valid pixels of a map.</summary>
    public static (double rms, double pv) RmsPv(float[] W, byte[] mask)
    {
        double s = 0, s2 = 0, mn = double.MaxValue, mx = double.MinValue; long n = 0;
        for (long i = 0; i < W.LongLength; i++) { if (mask[i] == 0 || float.IsNaN(W[i])) continue; double v = W[i]; s += v; s2 += v * v; n++; if (v < mn) mn = v; if (v > mx) mx = v; }
        if (n == 0) return (0, 0); double mean = s / n; return (Math.Sqrt(Math.Max(0, s2 / n - mean * mean)), mx - mn);
    }

    /// <summary>Preset selections (flags indexed by ANSI j).</summary>
    public static bool[] Preset(string name, int upToOrder = 2, int order = 10)
    {
        var f = new bool[ZernikeBasis.Count(order)];
        void Set(params int[] js) { foreach (var j in js) f[j] = true; }
        switch (name)
        {
            case "piston+tilt": Set(0, 1, 2); break;
            case "+defocus": Set(0, 1, 2, 4); break;
            case "+astigmatism": Set(0, 1, 2, 3, 4, 5); break;
            case "none": break;
            case "order": for (int j = 0; j < f.Length; j++) f[j] = ZernikeBasis.Term(j).N <= upToOrder; break;
            default: throw new ArgumentException(name);
        }
        return f;
    }

    /// <summary>Warnings to show when low-order terms that carry the lens power are removed.</summary>
    public static List<string> Warnings(bool[] remove)
    {
        var w = new List<string>();
        if (remove.Length > 4 && remove[4]) w.Add("Defocus is removed: the filtered map no longer shows the lens power, and the removed power is NOT in the displayed OPD.");
        if ((remove.Length > 3 && remove[3]) || (remove.Length > 5 && remove[5])) w.Add("Astigmatism is removed: cylinder/axis of the sample are no longer visible in the filtered map.");
        return w;
    }

    /// <summary>One-line description of the removed terms (for export headers).</summary>
    public static string Describe(ZernikeFit fit, bool[] remove)
    {
        var sb = new StringBuilder();
        for (int j = 0; j < remove.Length && j < fit.Terms.Length; j++) if (remove[j]) { var t = fit.Terms[j]; if (sb.Length > 0) sb.Append("; "); sb.Append($"j={j} (n={t.N},m={t.M}, Noll {t.Noll}, {t.Name}) c={fit.Coeff[j] * 1e9:F3} nm"); }
        return sb.Length == 0 ? "none" : sb.ToString();
    }
}
