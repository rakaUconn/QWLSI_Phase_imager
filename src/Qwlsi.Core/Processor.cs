using System.Diagnostics;

namespace Qwlsi.Core;

public sealed class ReconResult
{
    public float[] Opd = Array.Empty<float>();        // [m], NaN outside the mask, piston removed (M*M)
    public float[] Field = Array.Empty<float>();      // complex64 interleaved U = sqrt(I) exp(i 2 pi W / lambda) * mask
    public float[] Intensity = Array.Empty<float>();
    public byte[] Mask = Array.Empty<byte>();
    public float[] RawW = Array.Empty<float>();       // solver output (before piston removal / NaN) — reusable as warm start
    public WavefrontStats Stats = new(0, 0, 0, 0, 0, 0, 0);
    public SolveInfo Solve = new(Array.Empty<int>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
    public double BlackLevel;
    public Dictionary<string, double> TimingsMs = new();
}

/// <summary>Port of reconstruct() from qwlsi_single_frame.py. Owns FFT plans and all large buffers (reuse across frames).</summary>
public sealed class Processor : IDisposable
{
    readonly QwlsiParams par; readonly int N, M, Dec; readonly long nM;
    readonly RealFft2D bigFft; readonly ComplexFft2D smallFft;
    readonly float[] work, spectrum, zbuf;
    ShearSolver? solver;

    public Processor(QwlsiParams p)
    {
        par = p; N = p.N; M = p.M; Dec = p.Dec; nM = (long)M * M;
        bigFft = new RealFft2D(N); smallFft = new ComplexFft2D(M);
        work = new float[(long)N * N]; spectrum = new float[2L * N * (N / 2 + 1)]; zbuf = new float[2 * nM];
    }

    /// <summary>Spectrum value F[ky,kx] (any integer bin, wrapped) from the r2c half spectrum via conjugate symmetry.</summary>
    (float re, float im) F(int ky, int kx)
    {
        ky = ((ky % N) + N) % N; kx = ((kx % N) + N) % N; int C = N / 2 + 1;
        if (kx < C) { long i = 2 * ((long)ky * C + kx); return (spectrum[i], spectrum[i + 1]); }
        long j = 2 * ((long)((N - ky) % N) * C + (N - kx)); return (spectrum[j], -spectrum[j + 1]);
    }

    /// <summary>window(): M x M spectral window around integer bin kc=(kx,ky), smooth circular taper, inverse FFT, scale 1/N^2 (= M^2/N^2 * numpy's 1/M^2).</summary>
    float[] Window(int kcx, int kcy, float[] dest)
    {
        double R = par.R, taper = 0.08; Array.Clear(dest);
        Parallel.For(-M / 2, M / 2, my =>
        {
            for (int mx = -M / 2; mx < M / 2; mx++)
            {
                double r = Math.Sqrt((double)mx * mx + (double)my * my); double w = Math.Clamp((R - r) / (taper * R), 0, 1);
                if (w <= 0) continue; w = 0.5 - 0.5 * Math.Cos(Math.PI * w);
                var (re, im) = F(kcy + my, kcx + mx); float wf = (float)w;
                long o = 2 * ((long)((my + M) % M) * M + (mx + M) % M); dest[o] = re * wf; dest[o + 1] = im * wf;
            }
        });
        smallFft.Backward(dest);
        float s = 1f / ((float)N * N); Parallel.For(0, M, y => { long o = (long)y * M * 2; for (int i = 0; i < 2 * M; i++) dest[o + i] *= s; });
        return dest;
    }

    /// <summary>baseband(): window around the nearest integer bin, then the exact sub-bin phase ramp exp(-2 pi i d.n).</summary>
    float[] Baseband(double[] f, float[] dest)
    {
        int kx = (int)Math.Round(f[0] * N), ky = (int)Math.Round(f[1] * N);
        Window(kx, ky, dest);
        double dx = f[0] - (double)kx / N, dy = f[1] - (double)ky / N;
        Parallel.For(0, M, y =>
        {
            for (int x = 0; x < M; x++)
            {
                double ph = -2 * Math.PI * (dx * (x * Dec) + dy * (y * Dec)); float cs = (float)Math.Cos(ph), sn = (float)Math.Sin(ph);
                long i = 2 * ((long)y * M + x); float re = dest[i], im = dest[i + 1]; dest[i] = re * cs - im * sn; dest[i + 1] = re * sn + im * cs;
            }
        });
        return dest;
    }

    public ReconResult Process(float[] frame, Calibration calib, SolveOptions? opt = null)
    {
        opt ??= new SolveOptions(); var res = new ReconResult(); var sw = Stopwatch.StartNew();
        void Lap(string k) { res.TimingsMs[k] = sw.Elapsed.TotalMilliseconds; sw.Restart(); }
        var fa = calib.Fa; var fb = calib.Fb;

        // mean + FFT of (img - mu)
        double s = 0; object lk = new();
        Parallel.For(0, N, () => 0.0, (y, st, acc) => { long o = (long)y * N; for (int x = 0; x < N; x++) acc += frame[o + x]; return acc; }, acc => { lock (lk) s += acc; });
        float mu = (float)(s / ((double)N * N));
        Parallel.For(0, N, y => { long o = (long)y * N; for (int x = 0; x < N; x++) work[o + x] = frame[o + x] - mu; });
        bigFft.Forward(work, spectrum); Lap("fft_full_r2c");

        var A1 = new float[2 * nM]; var A2 = new float[2 * nM];
        Baseband(fa, A1); Baseband(fb, A2);
        var I0 = new float[nM]; Window(0, 0, zbuf);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) I0[o + x] = zbuf[2 * (o + x)] + mu; });
        Lap("demod_lobes");

        var mask = ImageOps.MakeMask(A1, A2, M); res.Mask = mask; Lap("mask");

        if (solver == null || !solver.FaKey!.SequenceEqual(fa) || !solver.FbKey!.SequenceEqual(fb)) { solver?.Dispose(); solver = new ShearSolver(par, fa, fb); Lap("solver_setup"); }
        var W = solver.Solve(A1, A2, mask, opt, out var info); res.Solve = info; res.RawW = W; Lap("solve");

        // piston removal, NaN outside mask
        double sm = 0; long cnt = 0; for (long i = 0; i < nM; i++) if (mask[i] != 0) { sm += W[i]; cnt++; }
        float mean = (float)(sm / cnt); var opd = new float[nM]; var wz = new float[nM];
        for (long i = 0; i < nM; i++) { if (mask[i] != 0) { wz[i] = W[i] - mean; opd[i] = wz[i]; } else opd[i] = float.NaN; }
        res.Opd = opd;

        // intensity: black level from the DC lobe
        var tmp = (float[])I0.Clone(); double lo = ImageOps.Percentile(tmp, (int)nM, 10);
        double bs = 0; long bc = 0; double thr = 1.5 * lo + 1; for (long i = 0; i < nM; i++) if (I0[i] < thr) { bs += I0[i]; bc++; }
        float black = (float)(bs / bc); res.BlackLevel = black;
        var I = new float[nM]; var vals = new float[cnt]; long k = 0;
        for (long i = 0; i < nM; i++) { I[i] = Math.Max(I0[i] - black, 0f); if (mask[i] != 0) vals[k++] = I[i]; }
        double p95 = ImageOps.Percentile(vals, (int)cnt, 95);
        var field = new float[2 * nM];
        for (long i = 0; i < nM; i++)
        {
            if (mask[i] == 0) { I[i] = 0; continue; }
            I[i] = (float)(I[i] / p95); double ph = 2 * Math.PI * wz[i] / par.Lambda, a = Math.Sqrt(I[i]);
            field[2 * i] = (float)(a * Math.Cos(ph)); field[2 * i + 1] = (float)(a * Math.Sin(ph));
        }
        res.Intensity = I; res.Field = field; Lap("intensity_field");

        res.Stats = Zernike.Report(wz, mask, M, par.PitchM); Lap("zernike");
        return res;
    }

    public void Dispose() { bigFft.Dispose(); smallFft.Dispose(); solver?.Dispose(); }
}
