using System.Diagnostics;
using Qwlsi.Core;

static class Program
{
    const string Root = @"H:\caliberation\Phase_imager_cal\New";

    static void Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "golden";
        if (cmd == "fft") FftBench.Run(args);
        else if (cmd == "golden") Golden(args.Skip(1).ToArray());
        else if (cmd == "fftcheck") FftCheck();
        else if (cmd == "warm") Warm();
        else if (cmd == "live") Live();
        else if (cmd == "calib") CalibRef();
        else Console.WriteLine("commands: fft | fftcheck | golden [names...]");
    }

    /// <summary>Round-trip + analytic check of the MKL wrappers.</summary>
    static void FftCheck()
    {
        int n = 64; var rnd = new Random(3); var x = new float[n * n]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble();
        using var r = new RealFft2D(n); var s = new float[2 * n * (n / 2 + 1)]; var y = new float[n * n];
        r.Forward(x, s); r.Backward(s, y); double e = 0; for (int i = 0; i < x.Length; i++) e = Math.Max(e, Math.Abs(y[i] / (n * n) - x[i]));
        Console.WriteLine($"r2c->c2r roundtrip max err {e:g3}");
        // compare r2c with c2c on the same data (e^{-i} forward)
        using var c = new ComplexFft2D(n); var z = new float[2 * n * n]; for (int i = 0; i < x.Length; i++) z[2 * i] = x[i]; c.Forward(z);
        double e2 = 0; int C = n / 2 + 1; for (int ky = 0; ky < n; ky++) for (int kx = 0; kx < C; kx++) { e2 = Math.Max(e2, Math.Abs(z[2 * (ky * n + kx)] - s[2 * (ky * C + kx)])); e2 = Math.Max(e2, Math.Abs(z[2 * (ky * n + kx) + 1] - s[2 * (ky * C + kx) + 1])); }
        Console.WriteLine($"r2c vs c2c half-spectrum max err {e2:g3}");
        c.Backward(z); double e3 = 0; for (int i = 0; i < x.Length; i++) e3 = Math.Max(e3, Math.Abs(z[2 * i] / (n * n) - x[i])); Console.WriteLine($"c2c roundtrip {e3:g3}");
    }

    static string[] Frames = { "concave_1_img", "concave_2_img", "concave_1-5_img", "convex_1_img", "convex_2_img", "convex_1-5_img", "ref" };

    /// <summary>rms of (a-b) over pixels finite in both, after removing a best-fit plane (piston + tilt) from the difference.</summary>
    static (double rmsNm, long n, long onlyA, long onlyB) Compare(float[] a, float[] b, int M)
    {
        long cnt = 0, oa = 0, ob = 0; double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; bool fa = !float.IsNaN(a[i]), fb = !float.IsNaN(b[i]);
            if (fa && !fb) oa++; if (fb && !fa) ob++; if (!(fa && fb)) continue;
            double d = a[i] - b[i]; cnt++; sx += x; sy += y; sz += d; sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y; sxz += x * d; syz += y * d;
        }
        // solve [sxx sxy sx; sxy syy sy; sx sy cnt] [a b c] = [sxz syz sz]
        double[,] A = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, cnt } }; double[] r = { sxz, syz, sz };
        double det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double D = det(A); var co = new double[3];
        for (int k = 0; k < 3; k++) { var B = (double[,])A.Clone(); for (int j = 0; j < 3; j++) B[j, k] = r[j]; co[k] = det(B) / D; }
        double ss = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; if (float.IsNaN(a[i]) || float.IsNaN(b[i])) continue;
            double e = a[i] - b[i] - (co[0] * x + co[1] * y + co[2]); ss += e * e;
        }
        return (Math.Sqrt(ss / cnt) * 1e9, cnt, oa, ob);
    }

    static void Warm()
    {
        var par = new QwlsiParams(); int M = par.M;
        var calib = Calibration.Load(Path.Combine(Root, @"recon_single_frame\calibration.json"));
        using var proc = new Processor(par);
        float[] Load(string n) => TiffIO.ReadFrame(Path.Combine(Root, n + ".tif"), out _, out _);
        float[] Gold(string n) => Npy.ReadFloat32(Path.Combine(Root, @"recon_single_frame\results", n + "_OPD_m.npy"), out _, out _);
        var f1 = Load("concave_1_img"); var f2 = Load("concave_2_img"); var g1 = Gold("concave_1_img"); var g2 = Gold("concave_2_img");
        proc.Process(f1, calib);   // JIT / plan warm-up
        void Run(string label, float[] frame, float[] gold, SolveOptions o)
        {
            var sw = Stopwatch.StartNew(); var r = proc.Process(frame, calib, o); double t = sw.Elapsed.TotalSeconds;
            var (rms, _, _, _) = Compare(r.Opd, gold, M);
            Console.WriteLine($"{label,-58} total {t,5:F2}s solve {r.TimingsMs["solve"] / 1000,5:F2}s  CG [{string.Join(",", r.Solve.CgIterations)}]  vs golden {rms,9:F3} nm  LSres {r.Solve.ResidualRad[0]:F3}/{r.Solve.ResidualRad[1]:F3}");
        }
        Console.WriteLine("--- cold start (preconditioned initial guess), concave_1, varying iterations/pass");
        foreach (int it in new[] { 60, 30, 20, 15, 10, 5 }) Run($"cold iters={it}", f1, g1, new SolveOptions { Iters = it });
        Console.WriteLine("--- cold, passes");
        Run("cold iters=60 unwrap=0", f1, g1, new SolveOptions { UnwrapPasses = 0 });
        Run("cold iters=60 unwrap=1", f1, g1, new SolveOptions { UnwrapPasses = 1 });
        var Wsame = proc.Process(f1, calib).RawW;
        Console.WriteLine("--- warm start from the SAME frame's converged W (static scene)");
        foreach (int it in new[] { 60, 20, 10, 5, 2 }) Run($"warm(same) iters={it}", f1, g1, new SolveOptions { Iters = it, WarmW = Wsame });
        foreach (int it in new[] { 20, 10, 5 }) Run($"warm(same) iters={it} allpasses", f1, g1, new SolveOptions { Iters = it, WarmW = Wsame, WarmAllPasses = true });
        Console.WriteLine("--- warm start from a DIFFERENT frame (concave_1 W -> concave_2; lens power 0.71 -> 1.41 D, worst-case scene change)");
        foreach (int it in new[] { 60, 30, 20, 10 }) Run($"warm(c1->c2) iters={it}", f2, g2, new SolveOptions { Iters = it, WarmW = Wsame });
        foreach (int it in new[] { 30, 20, 10 }) Run($"warm(c1->c2) iters={it} allpasses", f2, g2, new SolveOptions { Iters = it, WarmW = Wsame, WarmAllPasses = true });
        Console.WriteLine("--- warm + rtol early exit (1e-4 / 1e-3), same frame");
        foreach (double rt in new[] { 1e-4, 1e-3 }) Run($"warm(same) iters=60 rtol={rt}", f1, g1, new SolveOptions { Iters = 60, Rtol = rt, WarmW = Wsame, WarmAllPasses = true });
    }

    static void Live()
    {
        var par = new QwlsiParams(); int M = par.M; long n = (long)M * M;
        using var tp = new TransmittanceProcessor(par);
        float[] Load(string nm) => TiffIO.ReadFrame(Path.Combine(Root, nm + ".tif"), out _, out _);
        var f = Load("concave_1_img"); var fr = Load("ref");
        var I = new float[n]; var Iref = new float[n]; var T = new float[n]; var scratch = new float[n];
        tp.Normalised(f, I);
        // accuracy: scale to the golden |U|^2 inside its mask (golden normalises by the masked p95; ours by the frame p95)
        var gf = Npy.ReadFloat32(Path.Combine(Root, @"recon_single_frame\results\concave_1_img_field.npy"), out _, out _);
        double sxy = 0, sxx = 0; for (long i = 0; i < n; i++) { double g = gf[2 * i] * (double)gf[2 * i] + gf[2 * i + 1] * (double)gf[2 * i + 1]; if (g == 0) continue; sxy += I[i] * g; sxx += (double)I[i] * I[i]; }
        double k = sxy / sxx, num = 0, den = 0; for (long i = 0; i < n; i++) { double g = gf[2 * i] * (double)gf[2 * i] + gf[2 * i + 1] * (double)gf[2 * i + 1]; if (g == 0) continue; num += (k * I[i] - g) * (k * I[i] - g); den += g * g; }
        Console.WriteLine($"transmittance (no ref) vs golden intensity inside mask, after one global scale: relL2 {Math.Sqrt(num / den) * 100:F4}%");
        tp.DcIntensity(fr, Iref); TransmittanceProcessor.Ratio(tp.DcIntensity(f), Iref, T, M, scratch);
        double ts = 0; long tc = 0; for (long i = 0; i < n; i++) if (T[i] > 0) { ts += T[i]; tc++; }
        Console.WriteLine($"T = I/Iref (concave_1 / ref): mean over valid px {ts / tc:F3}");
        foreach (var mode in new[] { "normalised intensity (no ref)", "T = I/Iref (ref cached)" })
        {
            for (int w = 0; w < 5; w++) { tp.Normalised(f, I); }
            const int reps = 40; var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++)
            {
                if (mode.StartsWith("norm")) tp.Normalised(f, I);
                else { tp.DcIntensity(f, I); TransmittanceProcessor.Ratio(I, Iref, T, M, scratch); }
            }
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            Console.WriteLine($"{mode,-32} {ms,6:F1} ms/frame = {1000 / ms,5:F1} fps  (in-memory frame; excludes camera readout and display)");
        }
    }

    static void CalibRef()
    {
        var par = new QwlsiParams(); int M = par.M; var calib = Calibration.Load(Path.Combine(Root, "recon_single_frame", "calibration.json"));
        using var proc = new Processor(par); var sw = Stopwatch.StartNew();
        var c = proc.CalibrateFromFlat(TiffIO.ReadFrame(Path.Combine(Root, "ref.tif"), out _, out _), "ref.tif");
        Console.WriteLine($"calibrate_from_flat(ref.tif): {sw.Elapsed.TotalSeconds:F2} s");
        Console.WriteLine($"  fa = [{c.Fa[0]:R}, {c.Fa[1]:R}]  json [{calib.Fa[0]:R}, {calib.Fa[1]:R}]  |diff| {Math.Abs(c.Fa[0] - calib.Fa[0]):E2} {Math.Abs(c.Fa[1] - calib.Fa[1]):E2}");
        Console.WriteLine($"  fb = [{c.Fb[0]:R}, {c.Fb[1]:R}]  json [{calib.Fb[0]:R}, {calib.Fb[1]:R}]  |diff| {Math.Abs(c.Fb[0] - calib.Fb[0]):E2} {Math.Abs(c.Fb[1] - calib.Fb[1]):E2}");
        var rd = proc.MakeReference(TiffIO.ReadFrame(Path.Combine(Root, "ref.tif"), out _, out _), c);
        var sys = Npy.ReadFloat32(Path.Combine(Root, @"recon_single_frame\results", "ref_OPD_m.npy"), out _, out _);
        foreach (var n in new[] { "concave_1_img", "concave_2_img", "concave_1-5_img", "convex_1_img", "convex_2_img", "convex_1-5_img" })
        {
            sw.Restart(); var r = proc.Process(TiffIO.ReadFrame(Path.Combine(Root, n + ".tif"), out _, out _), null, null, rd); double t = sw.Elapsed.TotalSeconds;
            var g = Npy.ReadFloat32(Path.Combine(Root, @"recon_single_frame\results", n + "_OPD_m.npy"), out _, out _);
            var e = new float[g.Length]; for (int i = 0; i < e.Length; i++) e[i] = g[i] - sys[i];
            var (rms, _, _, _) = Compare(r.Opd, e, M);
            Console.WriteLine($"with-ref {n,-16} {t:F2}s  vs (single - ref_OPD): {rms:F2} nm rms | power {r.Stats.PowerD:+0.0000;-0.0000} D astig {r.Stats.AstigRmsNm:F1} nm rms {r.Stats.RmsNm:F0} nm");
        }
    }

    static void Golden(string[] names)
    {
        var par = new QwlsiParams(); int M = par.M;
        var calib = Calibration.Load(Path.Combine(Root, @"recon_single_frame\calibration.json"));
        Console.WriteLine($"calibration fa=[{calib.Fa[0]:R},{calib.Fa[1]:R}] fb=[{calib.Fb[0]:R},{calib.Fb[1]:R}]");
        using var proc = new Processor(par);
        var list = names.Length > 0 ? names : Frames;
        float[]? prevW = null;
        foreach (var name in list)
        {
            var sw = Stopwatch.StartNew();
            var img = TiffIO.ReadFrame(Path.Combine(Root, name + ".tif"), out int w, out int h); double tLoad = sw.Elapsed.TotalSeconds;
            if (w != par.N || h != par.N) throw new InvalidDataException($"unexpected size {w}x{h}");
            sw.Restart(); var res = proc.Process(img, calib); double tCold = sw.Elapsed.TotalSeconds;
            // golden
            string g = Path.Combine(Root, @"recon_single_frame\results", name + "_OPD_m.npy");
            var gold = Npy.ReadFloat32(g, out var shp, out _);
            var (rms, n, oa, ob) = Compare(res.Opd, gold, M);
            var goldF = Npy.ReadFloat32(Path.Combine(Root, @"recon_single_frame\results", name + "_field.npy"), out _, out _);
            double num = 0, den = 0, cov = 0, sa = 0, sb = 0, fnum = 0, fden = 0;
            for (long i = 0; i < (long)M * M; i++)
            {
                double Ig = goldF[2 * i] * (double)goldF[2 * i] + goldF[2 * i + 1] * (double)goldF[2 * i + 1], Ic = res.Intensity[i];
                if (res.Mask[i] == 0 || Ig == 0) continue;
                num += (Ic - Ig) * (Ic - Ig); den += Ig * Ig; sa += Ic; sb += Ig; cov += Ic * Ig;
                double dr = res.Field[2 * i] - goldF[2 * i], di = res.Field[2 * i + 1] - goldF[2 * i + 1]; fnum += dr * dr + di * di; fden += Ig;
            }
            var st = res.Stats;
            Console.WriteLine($"{name,-16} cold {tCold,6:F2}s (load {tLoad:F2}s) | OPD-vs-golden rms {rms,7:F2} nm over {n} px (mask only-cs {oa}, only-py {ob}) | I relL2 {Math.Sqrt(num / den) * 100:F4}% | field relL2 {Math.Sqrt(fnum / fden) * 100:F4}%");
            Console.WriteLine($"{"",-16} power {st.PowerD:+0.0000;-0.0000} D  astig {st.AstigRmsNm:F1} nm  rms {st.RmsNm:F1} nm  PV {st.PvUm:F3} um  pupil R {st.PupilRadiusMm:F3} mm | CG iters [{string.Join(",", res.Solve.CgIterations)}] LS resid rad [{res.Solve.ResidualRad[0]:F3},{res.Solve.ResidualRad[1]:F3}]");
            Console.WriteLine($"{"",-16} stages ms: " + string.Join("  ", res.TimingsMs.Select(kv => $"{kv.Key}={kv.Value:F0}")));
            prevW = res.RawW;
        }
    }
}
