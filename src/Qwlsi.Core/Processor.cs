using System.Diagnostics;

namespace Qwlsi.Core;

public sealed class ReconResult
{
    public float[] Opd = Array.Empty<float>();        // [m], NaN outside the mask, piston removed (M*M)
    public float[] Intensity = Array.Empty<float>();  // normalised, 0 outside the mask
    public byte[] Mask = Array.Empty<byte>();
    public float[]? RawW;                             // solver output (only with SolveOptions.KeepRawW) — reusable as warm start
    public WavefrontStats Stats = new(0, 0, 0, 0, 0, 0, 0);
    public SolveInfo Solve = new(Array.Empty<int>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
    public double BlackLevel;
    public int M;
    public Dictionary<string, double> TimingsMs = new();

    /// <summary>Complex field U = sqrt(I) exp(i 2 pi W / lambda) * mask, complex64 interleaved. Built on demand (not stored).</summary>
    public float[] BuildField(double lambda)
    {
        long n = Opd.Length; var f = new float[2 * n];
        Parallel.For(0, M, y =>
        {
            for (long i = (long)y * M; i < (long)(y + 1) * M; i++)
            {
                if (Mask[i] == 0 || float.IsNaN(Opd[i])) continue;
                double a = Math.Sqrt(Intensity[i]), ph = 2 * Math.PI * Opd[i] / lambda;
                f[2 * i] = (float)(a * Math.Cos(ph)); f[2 * i + 1] = (float)(a * Math.Sin(ph));
            }
        });
        return f;
    }
}

/// <summary>Port of reconstruct() from qwlsi_single_frame.py. Owns FFT plans; big buffers are allocated per call and released.</summary>
public sealed partial class Processor : IDisposable
{
    readonly QwlsiParams par; readonly int N, M, Dec; readonly long nM;
    readonly RealFft2D bigFft; readonly ComplexFft2D smallFft;
    float[]? work, spectrum;
    ShearSolver? solver;
    /// <summary>Run a blocking GC after releasing the 81 MB spectrum so the working set really drops (offline: true; live: false).</summary>
    public bool TrimAfterSpectrum { get; set; } = true;
    /// <summary>Keep the solver (~110 MB) between frames for speed (live capture loops). Default false: released after every frame (rebuild costs ~50 ms).</summary>
    public bool KeepSolver { get; set; }

    /// <summary>Compacting blocking GC + MKL pool release + working-set trim: call when the app goes idle.</summary>
    public static void TrimProcessMemory()
    {
        Compact();
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

    static void Compact()
    {
        Mkl.FreeBuffers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    public Processor(QwlsiParams p)
    {
        par = p; N = p.N; M = p.M; Dec = p.Dec; nM = (long)M * M;
        bigFft = new RealFft2D(N); smallFft = new ComplexFft2D(M);
    }

    /// <summary>Spectrum value F[ky,kx] (any integer bin, wrapped) from the r2c half spectrum via conjugate symmetry.</summary>
    (float re, float im) F(int ky, int kx)
    {
        var sp = spectrum!;
        ky = ((ky % N) + N) % N; kx = ((kx % N) + N) % N; int C = N / 2 + 1;
        if (kx < C) { long i = 2 * ((long)ky * C + kx); return (sp[i], sp[i + 1]); }
        long j = 2 * ((long)((N - ky) % N) * C + (N - kx)); return (sp[j], -sp[j + 1]);
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

    /// <summary>
    /// mean of the frame and r2c spectrum of (frame - mean). inPlace: the frame buffer itself is overwritten with frame - mean
    /// (no extra 81 MB copy); otherwise `work` holds the mean-removed copy (needed by the DFT-peak search).
    /// </summary>
    float ComputeSpectrum(float[] frame, bool inPlace)
    {
        double s = 0; object lk = new();
        Parallel.For(0, N, () => 0.0, (y, st, acc) => { long o = (long)y * N; for (int x = 0; x < N; x++) acc += frame[o + x]; return acc; }, acc => { lock (lk) s += acc; });
        float mu = (float)(s / ((double)N * N)); float[] src;
        if (inPlace) { Parallel.For(0, N, y => { long o = (long)y * N; for (int x = 0; x < N; x++) frame[o + x] -= mu; }); src = frame; }
        else { work ??= new float[(long)N * N]; var w = work; Parallel.For(0, N, y => { long o = (long)y * N; for (int x = 0; x < N; x++) w[o + x] = frame[o + x] - mu; }); src = w; }
        spectrum ??= new float[2L * N * (N / 2 + 1)];
        bigFft.Forward(src, spectrum); return mu;
    }

    void ReleaseBigBuffers() { work = null; spectrum = null; if (TrimAfterSpectrum) Compact(); }

    /// <summary>Frees the solver buffers (~110 MB). Call at the end of a batch / when idle.</summary>
    public void TrimMemory() { solver?.Dispose(); solver = null; work = null; spectrum = null; Compact(); }

    /// <summary>
    /// Reconstruct one frame. Without `reference`: carriers from `calib` (static system wavefront is included in the OPD).
    /// With `reference`: carriers come from the reference, sample lobes are divided by the reference lobes (A/A_ref -> the
    /// phase of A*conj(A_ref)), giving the sample-only OPD; `calib` is ignored. `frame` is not modified.
    /// </summary>
    public ReconResult Process(float[] frame, Calibration? calib, SolveOptions? opt = null, ReferenceData? reference = null)
    {
        float[]? f = frame; return ProcessCore(ref f, false, calib, opt, reference);
    }

    /// <summary>Same as Process but consumes the frame: it is overwritten and the caller's reference is set to null as soon as
    /// the spectrum is computed, so the 81 MB frame is freed before the solve (lowest peak memory).</summary>
    public ReconResult ProcessConsuming(ref float[]? frame, Calibration? calib, SolveOptions? opt = null, ReferenceData? reference = null)
        => ProcessCore(ref frame, true, calib, opt, reference);

    ReconResult ProcessCore(ref float[]? frameRef, bool consume, Calibration? calib, SolveOptions? opt, ReferenceData? reference)
    {
        opt ??= new SolveOptions(); var res = new ReconResult { M = M }; var sw = Stopwatch.StartNew();
        void Lap(string k) { res.TimingsMs[k] = sw.Elapsed.TotalMilliseconds; sw.Restart(); }
        var cal = reference?.Carriers ?? calib ?? throw new ArgumentException("calibration or reference required");
        var fa = cal.Fa; var fb = cal.Fb;
        if (frameRef == null || frameRef.LongLength != (long)N * N) throw new ArgumentException($"frame must be {N}x{N}");

        float mu = ComputeSpectrum(frameRef, consume); if (consume) frameRef = null; Lap("fft_full_r2c");

        var A1 = new float[2 * nM]; var A2 = new float[2 * nM]; var I = new float[nM];
        Window(0, 0, A1);                                   // zero-order lobe (A1 used as scratch before it holds lobe A)
        Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) I[o + x] = A1[2 * (o + x)] + mu; });
        Baseband(fa, A1); Baseband(fb, A2);
        ReleaseBigBuffers(); Lap("demod_lobes");

        var mask = ImageOps.MakeMask(A1, A2, M); res.Mask = mask; Lap("mask");
        if (reference != null) { MulConj(A1, reference.A1); MulConj(A2, reference.A2); Lap("ref_divide"); }
        if (TrimAfterSpectrum) GC.Collect(2, GCCollectionMode.Forced, true);   // drop mask-stage temporaries before the solver allocates

        if (solver == null || !solver.FaKey!.SequenceEqual(fa) || !solver.FbKey!.SequenceEqual(fb)) { solver?.Dispose(); solver = new ShearSolver(par, fa, fb); Lap("solver_setup"); }
        var W = solver.Solve(A1, A2, mask, opt, out var info); res.Solve = info; Lap("solve");
        A1 = null!; A2 = null!;
        if (opt.KeepRawW) res.RawW = (float[])W.Clone();

        // piston removal, NaN outside mask (in place on W)
        double sm = 0; long cnt = 0; for (long i = 0; i < nM; i++) if (mask[i] != 0) { sm += W[i]; cnt++; }
        float mean = (float)(sm / cnt);
        for (long i = 0; i < nM; i++) W[i] = mask[i] != 0 ? W[i] - mean : float.NaN;
        res.Opd = W;

        // intensity: black level from the DC lobe, normalised by the 95th percentile inside the mask
        var tmp = (float[])I.Clone(); double lo = ImageOps.Percentile(tmp, (int)nM, 10);
        double bs = 0; long bc = 0; double thr = 1.5 * lo + 1; for (long i = 0; i < nM; i++) if (I[i] < thr) { bs += I[i]; bc++; }
        float black = (float)(bs / bc); res.BlackLevel = black;
        long k = 0; for (long i = 0; i < nM; i++) { I[i] = Math.Max(I[i] - black, 0f); if (mask[i] != 0) tmp[k++] = I[i]; }
        double p95 = ImageOps.Percentile(tmp, (int)cnt, 95); tmp = null!;
        for (long i = 0; i < nM; i++) I[i] = mask[i] != 0 ? (float)(I[i] / p95) : 0f;
        res.Intensity = I; Lap("intensity");

        res.Stats = Zernike.Report(W, mask, M, par.PitchM); Lap("zernike");
        if (!KeepSolver) { solver?.Dispose(); solver = null; if (TrimAfterSpectrum) Compact(); }
        return res;
    }

    /// <summary>a *= conj(b) (phase of A/A_ref; amplitude weighting is irrelevant to the solver).</summary>
    void MulConj(float[] a, float[] b)
    {
        Parallel.For(0, M, y => { long o = 2L * y * M; for (int i = 0; i < 2 * M; i += 2) { float ar = a[o + i], ai = a[o + i + 1], br = b[o + i], bi = -b[o + i + 1]; a[o + i] = ar * br - ai * bi; a[o + i + 1] = ar * bi + ai * br; } });
    }

    public void Dispose() { bigFft.Dispose(); smallFft.Dispose(); solver?.Dispose(); }
}
