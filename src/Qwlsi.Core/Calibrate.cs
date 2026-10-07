namespace Qwlsi.Core;

/// <summary>Reference-frame data for with-reference mode: carriers detected from the reference + its demodulated lobes.</summary>
public sealed class ReferenceData
{
    public Calibration Carriers = new();
    public float[] A1 = Array.Empty<float>(), A2 = Array.Empty<float>();   // complex interleaved M x M
}

public sealed partial class Processor
{
    // ---- nominal carriers + DFT-peak refinement (nominal_carriers / dft_peak of the reference) --------------------

    /// <summary>|sum img*exp(-2 pi i (p0 x + p1 y))| of `work` (= img - mean), double accumulation.</summary>
    double DftMagnitude(double p0, double p1)
    {
        var cx = new double[N]; var sx = new double[N]; var cy = new double[N]; var sy = new double[N];
        for (int i = 0; i < N; i++) { double a = -2 * Math.PI * p0 * i, b = -2 * Math.PI * p1 * i; cx[i] = Math.Cos(a); sx[i] = Math.Sin(a); cy[i] = Math.Cos(b); sy[i] = Math.Sin(b); }
        double re = 0, im = 0; object lk = new(); var wk = work!;
        Parallel.For(0, N, () => (0.0, 0.0), (y, st, acc) =>
        {
            double rr = 0, ri = 0; long o = (long)y * N;
            for (int x = 0; x < N; x++) { double v = wk[o + x]; rr += v * cx[x]; ri += v * sx[x]; }
            acc.Item1 += rr * cy[y] - ri * sy[y]; acc.Item2 += rr * sy[y] + ri * cy[y]; return acc;
        }, acc => { lock (lk) { re += acc.Item1; im += acc.Item2; } });
        return Math.Sqrt(re * re + im * im);
    }

    /// <summary>Nelder-Mead maximisation (scipy defaults: initial simplex +5 %, xatol 1e-9, fatol 1e-3, maxiter 400).</summary>
    double[] DftPeak(double[] f0)
    {
        double F(double[] p) => -DftMagnitude(p[0], p[1]);
        const int n = 2; var sim = new double[n + 1][]; var fs = new double[n + 1];
        sim[0] = (double[])f0.Clone(); for (int k = 0; k < n; k++) { var y = (double[])f0.Clone(); y[k] = y[k] != 0 ? 1.05 * y[k] : 0.00025; sim[k + 1] = y; }
        for (int i = 0; i <= n; i++) fs[i] = F(sim[i]);
        for (int iter = 0; iter < 400; iter++)
        {
            var idx = Enumerable.Range(0, n + 1).OrderBy(i => fs[i]).ToArray(); sim = idx.Select(i => sim[i]).ToArray(); fs = idx.Select(i => fs[i]).ToArray();
            double dx = 0, df = 0; for (int i = 1; i <= n; i++) { for (int k = 0; k < n; k++) dx = Math.Max(dx, Math.Abs(sim[i][k] - sim[0][k])); df = Math.Max(df, Math.Abs(fs[0] - fs[i])); }
            if (dx <= 1e-9 && df <= 1e-3) break;
            var xbar = new double[n]; for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) xbar[k] += sim[i][k] / n;
            double[] Pt(double c) { var p = new double[n]; for (int k = 0; k < n; k++) p[k] = (1 + c) * xbar[k] - c * sim[n][k]; return p; }
            var xr = Pt(1); double fr = F(xr);
            if (fr < fs[0]) { var xe = Pt(2); double fe = F(xe); if (fe < fr) { sim[n] = xe; fs[n] = fe; } else { sim[n] = xr; fs[n] = fr; } }
            else if (fr < fs[n - 1]) { sim[n] = xr; fs[n] = fr; }
            else
            {
                bool shrink = false;
                if (fr < fs[n]) { var xc = Pt(0.5); double fc = F(xc); if (fc <= fr) { sim[n] = xc; fs[n] = fc; } else shrink = true; }
                else { var xcc = Pt(-0.5); double fcc = F(xcc); if (fcc < fs[n]) { sim[n] = xcc; fs[n] = fcc; } else shrink = true; }
                if (shrink) for (int i = 1; i <= n; i++) { for (int k = 0; k < n; k++) sim[i][k] = sim[0][k] + 0.5 * (sim[i][k] - sim[0][k]); fs[i] = F(sim[i]); }
            }
        }
        int best = 0; for (int i = 1; i <= n; i++) if (fs[i] < fs[best]) best = i; return sim[best];
    }

    /// <summary>Nominal carriers: |f| = 2p/Gamma, orthogonal, orientation measured from the frame. Needs `spectrum`/`work` filled.</summary>
    (double[] fa, double[] fb, double th) NominalCarriers()
    {
        double f0 = 2 * par.P / par.Gamma; int C = N / 2 + 1; double bestMag = -1; int bi = 0, bj = 0;
        object lk = new();
        Parallel.For(0, N, () => (-1.0, 0, 0), (iy, st, acc) =>
        {
            double ky = (iy < N / 2 ? iy : iy - N) / (double)N;
            for (int ix = 1; ix < C - 1; ix++)
            {
                double kx = ix / (double)N; if (Math.Abs(Math.Sqrt(kx * kx + ky * ky) - f0) >= 20.0 / N) continue;
                long i = 2 * ((long)iy * C + ix); double m = Math.Sqrt((double)spectrum![i] * spectrum[i] + (double)spectrum[i + 1] * spectrum[i + 1]);
                if (m > acc.Item1) acc = (m, iy, ix);
            }
            return acc;
        }, acc => { lock (lk) if (acc.Item1 > bestMag) { bestMag = acc.Item1; bi = acc.Item2; bj = acc.Item3; } });
        double[] fa = DftPeak(new[] { bj / (double)N, (bi < N / 2 ? bi : bi - N) / (double)N });
        double tha = Math.Atan2(fa[1], fa[0]);
        double[] fb = DftPeak(new[] { f0 * Math.Cos(tha - Math.PI / 2), f0 * Math.Sin(tha - Math.PI / 2) });
        double thb = Math.Atan2(fb[1], fb[0]) + Math.PI / 2;
        double th = Math.Atan2(Math.Sin(tha) + Math.Sin(thb), Math.Cos(tha) + Math.Cos(thb));
        return (new[] { f0 * Math.Cos(th), f0 * Math.Sin(th) }, new[] { f0 * Math.Cos(th - Math.PI / 2), f0 * Math.Sin(th - Math.PI / 2) }, th);
    }

    /// <summary>
    /// calibrate_from_flat: refine the carrier vectors from a frame of a flat/collimated wave (3 passes of demodulate ->
    /// fit the phase plane -> update f). `frame` may be an average of several flat frames.
    /// </summary>
    public Calibration CalibrateFromFlat(float[] frame, string? source = null)
    {
        ComputeSpectrum(frame, false);
        var (fa0, fb0, _) = NominalCarriers();
        var outf = new List<double[]>(); var A = new float[2 * nM];
        foreach (var f0 in new[] { fa0, fb0 })
        {
            var f = (double[])f0.Clone();
            for (int pass = 0; pass < 3; pass++)
            {
                Baseband(f, A);
                var mag = new float[nM]; for (long i = 0; i < nM; i++) mag[i] = MathF.Sqrt(A[2 * i] * A[2 * i] + A[2 * i + 1] * A[2 * i + 1]);
                var tmp = (float[])mag.Clone(); float thr = (float)(0.5 * (float)ImageOps.Percentile(tmp, (int)nM, 95));
                var m = new byte[nM]; for (long i = 0; i < nM; i++) m[i] = (byte)(mag[i] > thr ? 1 : 0);
                m = ImageOps.Erode(ImageOps.FillHoles(ImageOps.Open(m, M, M, 3), M, M), M, M, 6);
                double sr = 0, si = 0; for (long i = 0; i < nM; i++) if (m[i] != 0) { sr += A[2 * i]; si += A[2 * i + 1]; }
                double ang = Math.Atan2(si, sr), cr = Math.Cos(-ang), ci = Math.Sin(-ang);
                // least-squares plane  ph = a X + b Y + c  (X,Y in sensor px, centred for conditioning)
                double cx = (M - 1) * Dec / 2.0, cy = cx; double[,] G = new double[3, 3]; double[] rhs = new double[3];
                for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
                {
                    long i = (long)y * M + x; if (m[i] == 0) continue;
                    double re = A[2 * i] * cr - A[2 * i + 1] * ci, im = A[2 * i] * ci + A[2 * i + 1] * cr, ph = Math.Atan2(im, re);
                    double X = x * Dec - cx, Y = y * Dec - cy; double[] g = { X, Y, 1 };
                    for (int p = 0; p < 3; p++) { rhs[p] += g[p] * ph; for (int q = 0; q < 3; q++) G[p, q] += g[p] * g[q]; }
                }
                var cc = Solve3(G, rhs);
                f[0] += cc[0] / (2 * Math.PI); f[1] += cc[1] / (2 * Math.PI);
            }
            outf.Add(f);
        }
        ReleaseBigBuffers();
        return new Calibration { Fa = outf[0], Fb = outf[1], Source = source, Note = "carrier vectors [cyc/px] such that a flat wave gives zero differential-phase gradient" };
    }

    static double[] Solve3(double[,] A, double[] b)
    {
        double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = Det(A); var x = new double[3];
        for (int k = 0; k < 3; k++) { var B = (double[,])A.Clone(); for (int j = 0; j < 3; j++) B[j, k] = b[j]; x[k] = Det(B) / d; }
        return x;
    }

    /// <summary>Builds the reference data from a flat/reference frame. Carriers: `carriers` if given, otherwise detected from the reference itself.</summary>
    public ReferenceData MakeReference(float[] frame, Calibration? carriers = null, string? source = null)
    {
        var cal = carriers ?? CalibrateFromFlat(frame, source);
        ComputeSpectrum(frame, false);
        var r = new ReferenceData { Carriers = cal, A1 = new float[2 * nM], A2 = new float[2 * nM] };
        Baseband(cal.Fa, r.A1); Baseband(cal.Fb, r.A2);
        ReleaseBigBuffers();
        return r;
    }
}
