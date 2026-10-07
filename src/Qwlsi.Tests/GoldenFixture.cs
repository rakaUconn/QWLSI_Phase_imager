using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>Shared, lazily computed reconstructions of the real frames (each takes ~5 s) + golden data helpers.</summary>
public sealed class GoldenFixture : IDisposable
{
    public readonly QwlsiParams Par = new();
    public readonly string Root, Results, CalibPath;
    public readonly Calibration Calib;
    readonly Processor proc;
    readonly Dictionary<string, ReconResult> cache = new();
    readonly Dictionary<string, ReferenceData> refCache = new();

    public static readonly string[] Frames = { "concave_1_img", "concave_2_img", "concave_1-5_img", "convex_1_img", "convex_2_img", "convex_1-5_img", "ref" };

    public GoldenFixture()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); string? root = Environment.GetEnvironmentVariable("QWLSI_DATA");
        while (root == null && d != null) { if (Directory.Exists(Path.Combine(d.FullName, "recon_single_frame"))) root = d.FullName; d = d.Parent; }
        Root = root ?? @"H:\caliberation\Phase_imager_cal\New";
        Results = Path.Combine(Root, "recon_single_frame", "results"); CalibPath = Path.Combine(Root, "recon_single_frame", "calibration.json");
        Calib = Calibration.Load(CalibPath); proc = new Processor(Par);
    }

    public float[] Load(string name) => TiffIO.ReadFrame(Path.Combine(Root, name + ".tif"), out _, out _);
    public float[] GoldenOpd(string name) => Npy.ReadFloat32(Path.Combine(Results, name + "_OPD_m.npy"), out _, out _);
    public float[] GoldenField(string name) => Npy.ReadFloat32(Path.Combine(Results, name + "_field.npy"), out _, out _);

    /// <summary>Reconstruction with calibration.json (no reference), cached.</summary>
    public ReconResult Recon(string name) { lock (cache) { if (!cache.TryGetValue(name, out var r)) cache[name] = r = proc.Process(Load(name), Calib); return r; } }

    /// <summary>Reconstruction in with-reference mode (reference frame = refName), carriers detected from the reference.</summary>
    public ReconResult ReconWithRef(string name, string refName = "ref")
    {
        lock (cache)
        {
            string key = name + "|ref:" + refName;
            if (!refCache.TryGetValue(refName, out var rd)) refCache[refName] = rd = proc.MakeReference(Load(refName), null, refName + ".tif");
            if (!cache.TryGetValue(key, out var r)) cache[key] = r = proc.Process(Load(name), null, null, rd);
            return r;
        }
    }

    public Calibration CalibrateFromFlat(string name) => proc.CalibrateFromFlat(Load(name), name + ".tif");

    public void Dispose() => proc.Dispose();

    // ---- comparison helpers ----
    /// <summary>rms [nm] of (a-b) over pixels finite in both, after removing a best-fit plane (piston+tilt).</summary>
    public static double RmsDiffNm(float[] a, float[] b, int M, out long common, out long onlyA, out long onlyB)
    {
        long cnt = 0, oa = 0, ob = 0; double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; bool fa = !float.IsNaN(a[i]), fb = !float.IsNaN(b[i]);
            if (fa && !fb) oa++; if (fb && !fa) ob++; if (!(fa && fb)) continue;
            double d = (double)a[i] - b[i]; cnt++; sx += x; sy += y; sz += d; sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y; sxz += x * d; syz += y * d;
        }
        double[,] A = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, cnt } }; double[] r = { sxz, syz, sz };
        double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double D = Det(A); var co = new double[3];
        for (int k = 0; k < 3; k++) { var B = (double[,])A.Clone(); for (int j = 0; j < 3; j++) B[j, k] = r[j]; co[k] = Det(B) / D; }
        double ss = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; if (float.IsNaN(a[i]) || float.IsNaN(b[i])) continue;
            double e = (double)a[i] - b[i] - (co[0] * x + co[1] * y + co[2]); ss += e * e;
        }
        common = cnt; onlyA = oa; onlyB = ob; return Math.Sqrt(ss / cnt) * 1e9;
    }
}

[CollectionDefinition("heavy")]
public class HeavyCollection : ICollectionFixture<GoldenFixture> { }
