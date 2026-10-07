namespace Qwlsi.Core;

/// <summary>
/// Live transmittance pipeline: DC lobe only (no carriers, no calibration). One full-frame r2c FFT, DC window,
/// 1504^2 inverse FFT, black-level removal, normalisation. Same maths as the intensity path of Processor.
/// Preallocated; one instance is not thread-safe.
/// </summary>
public sealed class TransmittanceProcessor : IDisposable
{
    readonly QwlsiParams par; readonly int N, M; readonly long nM;
    readonly RealFft2D bigFft; readonly ComplexFft2D smallFft;
    readonly float[] work, spectrum, zbuf, tmp;
    readonly float[] window;      // precomputed taper over the M x M grid (0 outside radius R)
    readonly float[] raw;         // I0 (DC lobe + mean)

    public TransmittanceProcessor(QwlsiParams p)
    {
        par = p; N = p.N; M = p.M; nM = (long)M * M;
        bigFft = new RealFft2D(N); smallFft = new ComplexFft2D(M);
        work = new float[(long)N * N]; spectrum = new float[2L * N * (N / 2 + 1)]; zbuf = new float[2 * nM];
        tmp = new float[nM]; raw = new float[nM]; window = new float[nM];
        double R = p.R, taper = 0.08;
        for (int my = -M / 2; my < M / 2; my++) for (int mx = -M / 2; mx < M / 2; mx++)
        {
            double r = Math.Sqrt((double)mx * mx + (double)my * my), w = Math.Clamp((R - r) / (taper * R), 0, 1);
            window[(long)((my + M) % M) * M + (mx + M) % M] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * w));
        }
    }

    /// <summary>Black-level-subtracted DC-lobe intensity (unnormalised, M*M, clipped at 0). Output reuses `dest` (allocated if null).</summary>
    public float[] DcIntensity(float[] frame, float[]? dest = null)
    {
        dest ??= new float[nM];
        double s = 0; object lk = new();
        Parallel.For(0, N, () => 0.0, (y, st, acc) => { long o = (long)y * N; for (int x = 0; x < N; x++) acc += frame[o + x]; return acc; }, acc => { lock (lk) s += acc; });
        float mu = (float)(s / ((double)N * N));
        Parallel.For(0, N, y => { long o = (long)y * N; for (int x = 0; x < N; x++) work[o + x] = frame[o + x] - mu; });
        bigFft.Forward(work, spectrum);
        int C = N / 2 + 1; float sc = 1f / ((float)N * N);
        Array.Clear(zbuf);
        Parallel.For(-M / 2, M / 2, my =>
        {
            for (int mx = -M / 2; mx < M / 2; mx++)
            {
                long wi = (long)((my + M) % M) * M + (mx + M) % M; float w = window[wi]; if (w <= 0) continue;
                int ky = (my + N) % N, kx = (mx + N) % N; float re, im;
                if (kx < C) { long i = 2 * ((long)ky * C + kx); re = spectrum[i]; im = spectrum[i + 1]; }
                else { long j = 2 * ((long)((N - ky) % N) * C + (N - kx)); re = spectrum[j]; im = -spectrum[j + 1]; }
                zbuf[2 * wi] = re * w; zbuf[2 * wi + 1] = im * w;
            }
        });
        smallFft.Backward(zbuf);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) raw[o + x] = zbuf[2 * (o + x)] * sc + mu; });
        Array.Copy(raw, tmp, nM); double lo = ImageOps.Percentile(tmp, (int)nM, 10);
        double bs = 0; long bc = 0; double th = 1.5 * lo + 1;
        for (long i = 0; i < nM; i++) if (raw[i] < th) { bs += raw[i]; bc++; }
        float black = (float)(bs / bc);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) dest[o + x] = Math.Max(raw[o + x] - black, 0f); });
        return dest;
    }

    /// <summary>Relative intensity normalised so the 95th percentile of pixels above `maskFrac`*p95 is 1 (no reference).</summary>
    public float[] Normalised(float[] frame, float[]? dest = null, double maskFrac = 0.15)
    {
        dest = DcIntensity(frame, dest);
        Array.Copy(dest, tmp, nM); double p95 = ImageOps.Percentile(tmp, (int)nM, 95);
        float inv = (float)(1.0 / p95); Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) dest[o + x] *= inv; });
        return dest;
    }

    /// <summary>T = I / I_ref where both are black-subtracted DC intensities; pixels with I_ref below `floorFrac` of its p95 are 0.</summary>
    public static void Ratio(float[] I, float[] Iref, float[] dest, int M, float[] scratch, double floorFrac = 0.05)
    {
        long n = (long)M * M; Array.Copy(Iref, scratch, n);
        float floor = (float)(floorFrac * ImageOps.Percentile(scratch, (int)n, 95));
        Parallel.For(0, M, y => { long o = (long)y * M; for (int x = 0; x < M; x++) dest[o + x] = Iref[o + x] > floor ? I[o + x] / Iref[o + x] : 0f; });
    }

    public void Dispose() { bigFft.Dispose(); smallFft.Dispose(); }
}
