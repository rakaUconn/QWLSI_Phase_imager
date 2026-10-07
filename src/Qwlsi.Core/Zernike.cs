namespace Qwlsi.Core;

public sealed record WavefrontStats(double PowerD, double AstigRmsNm, double ComaRmsNm, double SphericalRmsNm, double PupilRadiusMm, double RmsNm, double PvUm);

/// <summary>Zernike fit + report (zern_basis / zfit / report of the Python reference). W in metres, mask byte per pixel.</summary>
public static class Zernike
{
    const int NMax = 6;
    static readonly (int n, int m)[] Terms = Enumerate();
    static (int, int)[] Enumerate() { var l = new List<(int, int)>(); for (int n = 0; n <= NMax; n++) for (int m = -n; m <= n; m += 2) l.Add((n, m)); return l.ToArray(); }
    static double Fact(int k) { double f = 1; for (int i = 2; i <= k; i++) f *= i; return f; }

    // radial coefficients: Rad[t][e] multiplies rho^e
    static readonly double[][] Rad = Terms.Select(tm =>
    {
        var (n, m) = tm; int am = Math.Abs(m); var c = new double[n + 1];
        for (int s = 0; s <= (n - am) / 2; s++)
            c[n - 2 * s] += ((s & 1) == 0 ? 1 : -1) * Fact(n - s) / (Fact(s) * Fact((n + am) / 2 - s) * Fact((n - am) / 2 - s));
        return c;
    }).ToArray();

    static void Basis(double rho, double th, double[] outv)
    {
        Span<double> pw = stackalloc double[NMax + 1]; pw[0] = 1; for (int e = 1; e <= NMax; e++) pw[e] = pw[e - 1] * rho;
        for (int t = 0; t < Terms.Length; t++)
        {
            var (n, m) = Terms[t]; int am = Math.Abs(m); double R = 0; var c = Rad[t];
            for (int e = 0; e <= n; e++) if (c[e] != 0) R += c[e] * pw[e];
            outv[t] = R * (m >= 0 ? Math.Cos(am * th) : Math.Sin(am * th)) * Math.Sqrt((m == 0 ? 1 : 2) * (n + 1));
        }
    }

    public static WavefrontStats Report(float[] W, byte[] mask, int M, double pitch)
    {
        double sy = 0, sx = 0; long cnt = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) if (mask[(long)y * M + x] != 0) { sy += y; sx += x; cnt++; }
        double cy = sy / cnt, cx = sx / cnt, r = Math.Sqrt(cnt / Math.PI);
        int K = Terms.Length; var ata = new double[K, K]; var atb = new double[K];
        double sw = 0, sw2 = 0, wmin = double.MaxValue, wmax = double.MinValue; long cm = 0; object lk = new();
        Parallel.For(0, M, () => (new double[K * K], new double[K], new double[K], new double[5] { 0, 0, double.MaxValue, double.MinValue, 0 }),
            (y, st, loc) =>
            {
                var (la, lb, bb, sc) = loc;
                for (int x = 0; x < M; x++)
                {
                    long i = (long)y * M + x; if (mask[i] == 0) continue;
                    double rho = Math.Sqrt((y - cy) * (y - cy) + (x - cx) * (x - cx)) / r; if (rho > 1) continue;
                    Basis(rho, Math.Atan2(y - cy, x - cx), bb); double w = W[i];
                    for (int p = 0; p < K; p++) { lb[p] += bb[p] * w; for (int q = p; q < K; q++) la[p * K + q] += bb[p] * bb[q]; }
                    sc[0] += w; sc[1] += w * w; if (w < sc[2]) sc[2] = w; if (w > sc[3]) sc[3] = w; sc[4]++;
                }
                return loc;
            },
            loc =>
            {
                lock (lk)
                {
                    for (int p = 0; p < K; p++) { atb[p] += loc.Item2[p]; for (int q = p; q < K; q++) ata[p, q] += loc.Item1[p * K + q]; }
                    sw += loc.Item4[0]; sw2 += loc.Item4[1]; wmin = Math.Min(wmin, loc.Item4[2]); wmax = Math.Max(wmax, loc.Item4[3]); cm += (long)loc.Item4[4];
                }
            });
        for (int p = 0; p < K; p++) for (int q = 0; q < p; q++) ata[p, q] = ata[q, p];
        var c = Solve(ata, atb, K);
        int Ix(int n, int m) => Array.IndexOf(Terms, (n, m));
        double Rm = r * pitch;
        double power = 2 * (2 * Math.Sqrt(3) * c[Ix(2, 0)] / (Rm * Rm));
        double ast = Math.Sqrt(c[Ix(2, 2)] * c[Ix(2, 2)] + c[Ix(2, -2)] * c[Ix(2, -2)]);
        double coma = Math.Sqrt(c[Ix(3, 1)] * c[Ix(3, 1)] + c[Ix(3, -1)] * c[Ix(3, -1)]);
        double mean = sw / cm, std = Math.Sqrt(Math.Max(0, sw2 / cm - mean * mean));
        return new WavefrontStats(power, ast * 1e9, coma * 1e9, c[Ix(4, 0)] * 1e9, Rm * 1e3, std * 1e9, (wmax - wmin) * 1e6);
    }

    static double[] Solve(double[,] A, double[] b, int n)   // Gaussian elimination with partial pivoting
    {
        var a = (double[,])A.Clone(); var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (x[c], x[piv]) = (x[piv], x[c]); }
            for (int r = c + 1; r < n; r++) { double f = a[r, c] / a[c, c]; for (int k = c; k < n; k++) a[r, k] -= f * a[c, k]; x[r] -= f * x[c]; }
        }
        for (int r = n - 1; r >= 0; r--) { double s = x[r]; for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k]; x[r] = s / a[r, r]; }
        return x;
    }
}
