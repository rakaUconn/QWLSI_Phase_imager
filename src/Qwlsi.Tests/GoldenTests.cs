using System.Text.Json;
using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>HANDOFF §10 acceptance tests against the Python golden results.</summary>
[Collection("heavy")]
public class GoldenTests
{
    readonly GoldenFixture fx;
    public GoldenTests(GoldenFixture f) { fx = f; }
    public static IEnumerable<object[]> AllFrames() => GoldenFixture.Frames.Select(n => new object[] { n });

    [Theory, MemberData(nameof(AllFrames))]
    public void Opd_matches_golden_within_5nm(string name)
    {
        var r = fx.Recon(name); var g = fx.GoldenOpd(name);
        double rms = GoldenFixture.RmsDiffNm(r.Opd, g, fx.Par.M, out long common, out long onlyCs, out long onlyPy);
        Assert.True(rms <= 5.0, $"{name}: OPD rms difference {rms:F3} nm (limit 5 nm)");
        Assert.True(onlyCs + onlyPy <= common / 1000, $"{name}: masks differ ({onlyCs} / {onlyPy} px)");
    }

    [Theory, MemberData(nameof(AllFrames))]
    public void Intensity_matches_python_within_0_01_percent(string name)
    {
        var r = fx.Recon(name); var gf = fx.GoldenField(name); long n = (long)fx.Par.M * fx.Par.M; double num = 0, den = 0;
        for (long i = 0; i < n; i++)
        {
            double ig = gf[2 * i] * (double)gf[2 * i] + gf[2 * i + 1] * (double)gf[2 * i + 1]; if (ig == 0 && r.Mask[i] == 0) continue;
            num += (r.Intensity[i] - ig) * (r.Intensity[i] - ig); den += ig * ig;
        }
        double rel = Math.Sqrt(num / den);
        Assert.True(rel <= 1e-4, $"{name}: intensity relative L2 difference {rel * 100:F5} % (limit 0.01 %)");
    }

    /// <summary>Raw frame smoothed with a separable Gaussian (sigma in sensor px) and sampled on the decimated grid (x*Dec, y*Dec).</summary>
    static float[] SmoothedRaw(float[] raw, int N, int M, int D, double sigma)
    {
        int h = (int)Math.Ceiling(3 * sigma); var k = new double[2 * h + 1]; double ks = 0;
        for (int i = -h; i <= h; i++) { k[i + h] = Math.Exp(-0.5 * i * i / (sigma * sigma)); ks += k[i + h]; }
        for (int i = 0; i < k.Length; i++) k[i] /= ks;
        var tmp = new float[(long)N * M];   // horizontal pass at the M sample columns, all N rows
        Parallel.For(0, N, y =>
        {
            for (int xs = 0; xs < M; xs++) { double s = 0; for (int i = -h; i <= h; i++) { int x = Math.Clamp(xs * D + i, 0, N - 1); s += k[i + h] * raw[(long)y * N + x]; } tmp[(long)y * M + xs] = (float)s; }
        });
        var o = new float[(long)M * M];
        Parallel.For(0, M, ys => { for (int xs = 0; xs < M; xs++) { double s = 0; for (int i = -h; i <= h; i++) { int y = Math.Clamp(ys * D + i, 0, N - 1); s += k[i + h] * tmp[(long)y * M + xs]; } o[(long)ys * M + xs] = (float)s; } });
        return o;
    }

    static double Corr(float[] a, float[] b, byte[] mask)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; long c = 0;
        for (long i = 0; i < mask.Length; i++) { if (mask[i] == 0) continue; double x = a[i], y = b[i]; sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y; c++; }
        return (sab - sa * sb / c) / Math.Sqrt((saa - sa * sa / c) * (sbb - sb * sb / c));
    }

    [Theory, MemberData(nameof(AllFrames))]
    public void Complex_field_matches_python(string name)
    {
        // relative L2 of (U - U_python) over sum |U_python|^2 ; measured 0.001-0.009 % on all frames
        var r = fx.Recon(name); var g = fx.GoldenField(name); long n = (long)fx.Par.M * fx.Par.M; double num = 0, den = 0;
        for (long i = 0; i < n; i++)
        {
            double dr = r.Field[2 * i] - g[2 * i], di = r.Field[2 * i + 1] - g[2 * i + 1]; num += dr * dr + di * di;
            den += g[2 * i] * (double)g[2 * i] + g[2 * i + 1] * (double)g[2 * i + 1];
        }
        double rel = Math.Sqrt(num / den);
        Assert.True(rel <= 5e-4, $"{name}: complex field relative L2 difference {rel * 100:F5} % (limit 0.05 %)");
    }

    /// <summary>
    /// SANITY CHECK only (not an acceptance criterion): |U|^2 vs the smoothed raw frame. The smoothing is not defined in HANDOFF, so the
    /// result depends on it (see sigma note below). The real acceptance criteria for intensity/field are the Python-golden tests above
    /// (Intensity_matches_python_within_0_01_percent and the complex-field comparison).
    /// </summary>
    [Theory, MemberData(nameof(AllFrames))]
    public void Intensity_correlates_with_smoothed_raw_frame(string name)
    {
        // "smoothed raw frame" is not defined in HANDOFF; chosen here as a Gaussian of sigma = 3 sensor px (the smallest that removes the 3 px carrier
        // fringes: exp(-2 pi^2 sigma^2 f^2) ~ 3e-9), sampled on the decimated grid. The result depends on sigma (0.997 at 1.5 px ... 0.973 at 6 px).
        var r = fx.Recon(name); var sm = SmoothedRaw(fx.Load(name), fx.Par.N, fx.Par.M, fx.Par.Dec, 3.0);
        double corr = Corr(r.Intensity, sm, r.Mask);
        Assert.True(corr >= 0.98, $"{name}: |U|^2 vs smoothed raw correlation {corr:F4} (limit 0.98)");
    }

    [Theory]
    [InlineData("concave_1_img", 0.72, 0.7110144650969836)]
    [InlineData("convex_2_img", -1.47, -1.474516399505491)]
    [InlineData("ref", 0.00, -0.006840493407692861)]
    public void Lens_power(string name, double handoffNominal, double pythonExact)
    {
        double p = fx.Recon(name).Stats.PowerD;
        Assert.InRange(p, handoffNominal - 0.01, handoffNominal + 0.01);        // HANDOFF §10 (+-0.01 D)
        Assert.InRange(p, pythonExact - 0.001, pythonExact + 0.001);            // regenerated Python value
    }

    [Fact]
    public void Calibration_from_flat_reproduces_json_carriers()
    {
        var c = fx.CalibrateFromFlat("ref");
        for (int k = 0; k < 2; k++) { Assert.InRange(Math.Abs(c.Fa[k] - fx.Calib.Fa[k]), 0, 1e-5); Assert.InRange(Math.Abs(c.Fb[k] - fx.Calib.Fb[k]), 0, 1e-5); }
    }

    [Fact]
    public void Calibration_json_roundtrip_is_bit_exact_and_python_schema()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "qwlsi_calib_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            fx.Calib.Save(tmp); var back = Calibration.Load(tmp);
            Assert.Equal(fx.Calib.Fa, back.Fa); Assert.Equal(fx.Calib.Fb, back.Fb);     // exact double equality
            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            Assert.Equal(2, doc.RootElement.GetProperty("fa").GetArrayLength()); Assert.Equal(2, doc.RootElement.GetProperty("fb").GetArrayLength());
            // the original Python-written file parses to the same doubles
            using var orig = JsonDocument.Parse(File.ReadAllText(fx.CalibPath));
            Assert.Equal(orig.RootElement.GetProperty("fa")[0].GetDouble(), back.Fa[0]);
            Assert.Equal(orig.RootElement.GetProperty("fb")[1].GetDouble(), back.Fb[1]);
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData("concave_1_img")]
    [InlineData("concave_2_img")]
    [InlineData("convex_1_img")]
    [InlineData("convex_2_img")]
    public void With_reference_equals_single_frame_minus_ref_opd(string name)
    {
        // reference = ref.tif, carriers detected from the reference itself
        var r = fx.ReconWithRef(name); var single = fx.GoldenOpd(name); var sys = fx.GoldenOpd("ref");
        int M = fx.Par.M; var expect = new float[single.Length];
        for (int i = 0; i < expect.Length; i++) expect[i] = single[i] - sys[i];      // NaN where either mask is NaN
        double rms = GoldenFixture.RmsDiffNm(r.Opd, expect, M, out _, out _, out _);
        Assert.True(rms <= 10.0, $"{name}: with-ref OPD vs (single-frame - ref_OPD) rms {rms:F2} nm (limit 10 nm)");
    }
}
