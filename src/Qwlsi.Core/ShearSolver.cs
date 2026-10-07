namespace Qwlsi.Core;

public sealed class SolveOptions
{
    public int Iters { get; set; } = 60;
    public int UnwrapPasses { get; set; } = 2;
    public double Rtol { get; set; } = 1e-7;
    /// <summary>Initial W [m] for the first pass (warm start). null = Fourier-preconditioned initial guess (reference behaviour).</summary>
    public float[]? WarmW { get; set; }
    /// <summary>Also warm-start the re-wrap passes from the current W instead of the preconditioned guess (NOT in the reference).</summary>
    public bool WarmAllPasses { get; set; }
    /// <summary>Keep the raw solver output (ReconResult.RawW) for warm-starting a following frame. Off by default (saves memory).</summary>
    public bool KeepRawW { get; set; }
    /// <summary>Cancels the solve (OperationCanceledException), checked every CG iteration.</summary>
    public CancellationToken Cancel { get; set; }
    /// <summary>Progress 0..1 over passes x iterations (called from the solver thread).</summary>
    public Action<double>? Progress { get; set; }
}

public sealed record SolveInfo(int[] CgIterations, double[] ResidualRad, double[] DataRmsRad, double[] FinalRelResidual);

/// <summary>
/// Masked least-squares inversion of the two shear phases (solve_opd of the reference). The operators are the exact
/// Fourier shear operators H_j = 2i sin(pi k.s_j). Two real FFT pairs are packed into one complex FFT using
/// conjugate symmetry — an exact algebraic identity, so results equal the reference up to float rounding.
/// Memory-lean: the filters are derived on the fly from y_j = 2 sin(pi k.s_j) (two float arrays), scratch arrays are aliased.
/// One instance is not thread-safe.
/// </summary>
public sealed class ShearSolver : IDisposable
{
    readonly int M; readonly long n; readonly double lam; readonly int dec;
    readonly ComplexFft2D cfft; readonly RealFft2D rfft;
    readonly float[] Y0, Y1;            // y_j = 2 sin(pi k.s_j) (H_j = i*y_j)
    readonly float[] Pk;                // real, half spectrum M x (M/2+1), 1/M^2 folded in
    readonly float invS;
    readonly float[] bufA, bufB;        // complex scratch
    readonly float[] spec;              // half-spectrum scratch for the preconditioner
    readonly float[] x, r, z, p, q, bvec;
    readonly float[] ph0, ph1;          // wrapped / re-wrapped phases
    byte[] mk = Array.Empty<byte>();
    public double[]? FaKey, FbKey;

    public ShearSolver(QwlsiParams par, double[] fa, double[] fb)
    {
        M = par.M; n = (long)M * M; dec = par.Dec; lam = par.Lambda; invS = 1f / ((float)M * M);
        cfft = new ComplexFft2D(M); rfft = new RealFft2D(M);
        Y0 = new float[n]; Y1 = new float[n]; Pk = new float[(long)M * (M / 2 + 1)];
        bufA = new float[2 * n]; bufB = new float[2 * n]; spec = new float[2L * M * (M / 2 + 1)];
        x = new float[n]; r = new float[n]; z = new float[n]; p = new float[n]; q = new float[n]; bvec = new float[n];
        ph0 = new float[n]; ph1 = new float[n];
        BuildFilters(par, fa, fb);
    }

    public static double[] Shear(QwlsiParams par, double[] f) => new[] { par.Lambda * par.D * f[0] / (par.P * par.P), par.Lambda * par.D * f[1] / (par.P * par.P) };

    void BuildFilters(QwlsiParams par, double[] fa, double[] fb)
    {
        FaKey = (double[])fa.Clone(); FbKey = (double[])fb.Clone();
        var sa = Shear(par, fa); var sb = Shear(par, fb);
        var f1 = new double[M]; for (int i = 0; i < M; i++) f1[i] = (i < M / 2 ? i : i - M) / (double)(M * dec);   // numpy fftfreq(M, d=DEC)
        Parallel.For(0, M, iy =>
        {
            for (int ix = 0; ix < M; ix++)
            {
                long i = (long)iy * M + ix; double kx = f1[ix], ky = f1[iy];
                Y0[i] = (float)(2 * Math.Sin(Math.PI * (kx * sa[0] + ky * sa[1])));
                Y1[i] = (float)(2 * Math.Sin(Math.PI * (kx * sb[0] + ky * sb[1])));
            }
        });
        float smax = 0;
        for (long i = 0; i < n; i++) { float s = Y0[i] * Y0[i] + Y1[i] * Y1[i]; if (s > smax) smax = s; }
        int C = M / 2 + 1;
        Parallel.For(0, M, iy =>
        {
            int my = (M - iy) % M;
            for (int ix = 0; ix < C; ix++)
            {
                int mx = (M - ix) % M; long i = (long)iy * M + ix, m = (long)my * M + mx;
                float Si = Y0[i] * Y0[i] + Y1[i] * Y1[i], Sm = Y0[m] * Y0[m] + Y1[m] * Y1[m];
                float pi_ = 1f / (Si + smax * 1e-6f + 1e-12f), pm = 1f / (Sm + smax * 1e-6f + 1e-12f);
                if (i == 0) pi_ = 0; if (m == 0) pm = 0;
                Pk[(long)iy * C + ix] = 0.5f * (pi_ + pm) * invS;
            }
        });
    }

    public void Rebuild(QwlsiParams par, double[] fa, double[] fb) => BuildFilters(par, fa, fb);

    // ---- operators ---------------------------------------------------------------------------------------------
    /// <summary>D0 + i D1 = Inv(G * Fwd(w))  -> in bufA (complex), G = Hh0 + i Hh1 (Hermitian-projected, scale 1/M^2 folded in).</summary>
    void D2(float[] w)
    {
        Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) { bufA[2 * (o + xx)] = w[o + xx]; bufA[2 * (o + xx) + 1] = 0; } });
        cfft.Forward(bufA);
        Parallel.For(0, M, y =>
        {
            int my = (M - y) % M; long o = (long)y * M, om = (long)my * M;
            for (int xx = 0; xx < M; xx++)
            {
                int mx = (M - xx) % M; long i = o + xx, m = om + mx;
                float yh0 = 0.5f * (Y0[i] - Y0[m]), yh1 = 0.5f * (Y1[i] - Y1[m]);   // Hh_j = i*(y_j(k) - y_j(-k))/2
                float gr = -invS * yh1, gi = invS * yh0;                              // G = Hh0 + i Hh1
                long k = 2 * i; float re = bufA[k], im = bufA[k + 1];
                bufA[k] = re * gr - im * gi; bufA[k + 1] = re * gi + im * gr;
            }
        });
        cfft.Backward(bufA);
    }

    /// <summary>out = -Re Inv(H0 Fwd(r0) + H1 Fwd(r1)), with r0 + i r1 supplied in bufA. Destroys bufA/bufB.</summary>
    void DTsumFromBufA(float[] outv)
    {
        cfft.Forward(bufA);   // Tr = R0 + i R1
        Parallel.For(0, M, y =>
        {
            int my = (M - y) % M; long o = (long)y * M, om = (long)my * M;
            for (int xx = 0; xx < M; xx++)
            {
                int mx = (M - xx) % M; long i = o + xx, m = 2 * (om + mx), k = 2 * i;
                float tr = bufA[k], ti = bufA[k + 1], cr = bufA[m], ci = -bufA[m + 1];   // conj(Tr(-k))
                float h0 = 0.5f * invS * Y0[i], h1 = 0.5f * invS * Y1[i];
                // Ha = (H0 - iH1)/2 = (y1 + i y0)/2 ; Hb = (H0 + iH1)/2 = (-y1 + i y0)/2   (scale folded in)
                float ar = h1, ai = h0, br = -h1, bi = h0;
                bufB[k] = tr * ar - ti * ai + cr * br - ci * bi;
                bufB[k + 1] = tr * ai + ti * ar + cr * bi + ci * br;
            }
        });
        cfft.Backward(bufB);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) outv[o + xx] = -bufB[2 * (o + xx)]; });
    }

    void Aop(float[] w, float[] outv)
    {
        D2(w);
        var m = mk;
        Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) if (m[o + xx] == 0) { bufA[2 * (o + xx)] = 0; bufA[2 * (o + xx) + 1] = 0; } });
        DTsumFromBufA(outv);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) outv[o + xx] += 1e-9f * w[o + xx]; });
    }

    void Pop(float[] rin, float[] outv)
    {
        rfft.Forward(rin, spec); int C = M / 2 + 1;
        Parallel.For(0, M, y => { long o = (long)y * C; for (int xx = 0; xx < C; xx++) { float k = Pk[o + xx]; spec[2 * (o + xx)] *= k; spec[2 * (o + xx) + 1] *= k; } });
        rfft.Backward(spec, outv);
    }

    double Dot(float[] a, float[] b)
    {
        double s = 0; object lk = new();
        Parallel.For(0, M, () => 0.0, (y, st, acc) => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) acc += (double)a[o + xx] * b[o + xx]; return acc; }, acc => { lock (lk) s += acc; });
        return s;
    }

    /// <summary>x0 may be `z` (it is copied into x before z is used as scratch).</summary>
    void Cg(float[] bvec_, float[] x0, int maxIter, double rtol, SolveOptions o, int pass, int passes, out int iters, out double relRes)
    {
        Array.Copy(x0, x, n);
        Aop(x, q);   // q = A x0
        Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) r[off + xx] = bvec_[off + xx] - q[off + xx]; });
        double bnorm = Math.Sqrt(Dot(bvec_, bvec_)), atol = rtol * bnorm, rho = 0, rhoPrev = 0; iters = 0;
        for (int it = 0; it < maxIter; it++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            o.Progress?.Invoke((pass + (double)it / maxIter) / passes);
            if (Math.Sqrt(Dot(r, r)) < atol) break;
            Pop(r, z); rho = Dot(r, z);
            if (it > 0) { float beta = (float)(rho / rhoPrev); Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) p[off + xx] = p[off + xx] * beta + z[off + xx]; }); }
            else Array.Copy(z, p, n);
            Aop(p, q);
            float alpha = (float)(rho / Dot(p, q));
            Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) { x[off + xx] += alpha * p[off + xx]; r[off + xx] -= alpha * q[off + xx]; } });
            rhoPrev = rho; iters++;
        }
        relRes = Math.Sqrt(Dot(r, r)) / Math.Max(bnorm, 1e-300);
    }

    // ---- solve_opd ---------------------------------------------------------------------------------------------
    /// <summary>
    /// A1,A2: interleaved complex lobes (M x M); they are modified IN PLACE (piston removal). mask: byte.
    /// Returns W [m] (M*M, unmasked, piston/tilt NOT yet removed).
    /// </summary>
    public float[] Solve(float[] A1, float[] A2, byte[] mask, SolveOptions o, out SolveInfo info)
    {
        float c = (float)(-2 * Math.PI / lam);   // sigma = -1
        mk = mask;
        RemovePiston(A1, mask); RemovePiston(A2, mask);
        Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) { long i = off + xx; ph0[i] = MathF.Atan2(A1[2 * i + 1], A1[2 * i]); ph1[i] = MathF.Atan2(A2[2 * i + 1], A2[2 * i]); } });
        var W = new float[n]; int passes = 1 + o.UnwrapPasses;
        var iterL = new int[passes]; var relL = new double[passes];
        float[] pred0 = p, pred1 = q;   // CG scratch is free once a pass has finished
        for (int pass = 0; pass < passes; pass++)
        {
            Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) { long i = off + xx; float v = mask[i] != 0 ? 1f : 0f; bufA[2 * i] = v * ph0[i] / c; bufA[2 * i + 1] = v * ph1[i] / c; } });
            DTsumFromBufA(bvec);
            if (pass == 0 && o.WarmW != null) Array.Copy(o.WarmW, z, n);
            else if (pass > 0 && o.WarmAllPasses) Array.Copy(W, z, n);
            else Pop(bvec, z);
            Cg(bvec, z, o.Iters, o.Rtol, o, pass, passes, out iterL[pass], out relL[pass]);
            Array.Copy(x, W, n);
            D2(W);
            Parallel.For(0, M, y => { long off = (long)y * M; for (int xx = 0; xx < M; xx++) { long i = off + xx; pred0[i] = c * bufA[2 * i]; pred1[i] = c * bufA[2 * i + 1]; } });
            if (pass < o.UnwrapPasses) { Rewrap(A1, pred0, ph0, mask); Rewrap(A2, pred1, ph1, mask); }
        }
        double r0 = 0, r1 = 0; long cnt = 0; double d0 = 0, d1 = 0, m0 = 0, m1 = 0;
        for (long i = 0; i < n; i++) if (mask[i] != 0) { double e0 = pred0[i] - ph0[i], e1 = pred1[i] - ph1[i]; r0 += e0 * e0; r1 += e1 * e1; m0 += ph0[i]; m1 += ph1[i]; d0 += (double)ph0[i] * ph0[i]; d1 += (double)ph1[i] * ph1[i]; cnt++; }
        info = new SolveInfo(iterL, new[] { Math.Sqrt(r0 / cnt), Math.Sqrt(r1 / cnt) },
            new[] { Math.Sqrt(Math.Max(0, d0 / cnt - (m0 / cnt) * (m0 / cnt))), Math.Sqrt(Math.Max(0, d1 / cnt - (m1 / cnt) * (m1 / cnt))) }, relL);
        mk = Array.Empty<byte>();
        return W;
    }

    void RemovePiston(float[] A, byte[] mask)
    {
        double sr = 0, si = 0; for (long i = 0; i < n; i++) if (mask[i] != 0) { sr += A[2 * i]; si += A[2 * i + 1]; }
        double ang = Math.Atan2(si, sr); float cr = (float)Math.Cos(-ang), ci = (float)Math.Sin(-ang);
        Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) { long i = 2 * (o + xx); float re = A[i], im = A[i + 1]; A[i] = re * cr - im * ci; A[i + 1] = re * ci + im * cr; } });
    }

    /// <summary>ph = pred + angle(Ap * exp(-i pred)), then subtract the masked mean.</summary>
    void Rewrap(float[] Ap, float[] pred, float[] ph, byte[] mask)
    {
        Parallel.For(0, M, y =>
        {
            long o = (long)y * M;
            for (int xx = 0; xx < M; xx++)
            {
                long i = o + xx; float pr = pred[i], cs = MathF.Cos(pr), sn = MathF.Sin(pr);
                float re = Ap[2 * i] * cs + Ap[2 * i + 1] * sn, im = Ap[2 * i + 1] * cs - Ap[2 * i] * sn;   // Ap * exp(-i pred)
                ph[i] = pr + MathF.Atan2(im, re);
            }
        });
        double s = 0; long cnt = 0; for (long i = 0; i < n; i++) if (mask[i] != 0) { s += ph[i]; cnt++; }
        float mean = (float)(s / cnt); Parallel.For(0, M, y => { long o = (long)y * M; for (int xx = 0; xx < M; xx++) ph[o + xx] -= mean; });
    }

    public void Dispose() { cfft.Dispose(); rfft.Dispose(); }
}
