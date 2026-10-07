using System.Text.Json;
using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>OSA/ANSI Zernike basis, joint fit, orientation flags, filtering — and agreement with the Python reference fit on all 7 frames.</summary>
[Collection("heavy")]
public class ZernikeTests
{
    readonly GoldenFixture fx;
    public ZernikeTests(GoldenFixture f) { fx = f; }

    static double[] Values(double rho, double th) { var v = new double[66]; ZernikeBasis.Eval(66, rho, th, v); return v; }

    [Fact]
    public void Index_names_and_closed_forms_j0_to_j14()
    {
        // j = (n(n+2)+m)/2 and the names / closed forms for 0..14 (OSA/ANSI Z80.28)
        string[] names = { "piston", "vertical tilt", "horizontal tilt", "oblique astigmatism", "defocus", "vertical astigmatism", "oblique trefoil", "vertical coma", "horizontal coma",
                           "horizontal trefoil", "oblique quatrefoil", "secondary oblique astigmatism", "spherical aberration", "secondary vertical astigmatism", "horizontal quatrefoil" };
        for (int j = 0; j < 15; j++) Assert.Equal(names[j], ZernikeBasis.Term(j).Name);
        for (int j = 0; j < 66; j++) { var t = ZernikeBasis.Term(j); Assert.Equal(j, t.J); Assert.Equal(j, (t.N * (t.N + 2) + t.M) / 2); }
        double r = 0.63, th = 0.9;
        static double C(double x) => Math.Cos(x);
        static double S(double x) => Math.Sin(x);
        var z = Values(r, th);
        Assert.Equal(1.0, z[0], 12);
        Assert.Equal(2 * r * S(th), z[1], 12);
        Assert.Equal(2 * r * C(th), z[2], 12);
        Assert.Equal(Math.Sqrt(6) * r * r * S(2 * th), z[3], 12);
        Assert.Equal(Math.Sqrt(3) * (2 * r * r - 1), z[4], 12);
        Assert.Equal(Math.Sqrt(6) * r * r * C(2 * th), z[5], 12);
        Assert.Equal(Math.Sqrt(8) * Math.Pow(r, 3) * S(3 * th), z[6], 12);
        Assert.Equal(Math.Sqrt(8) * (3 * Math.Pow(r, 3) - 2 * r) * S(th), z[7], 12);
        Assert.Equal(Math.Sqrt(8) * (3 * Math.Pow(r, 3) - 2 * r) * C(th), z[8], 12);
        Assert.Equal(Math.Sqrt(8) * Math.Pow(r, 3) * C(3 * th), z[9], 12);
        Assert.Equal(Math.Sqrt(10) * Math.Pow(r, 4) * S(4 * th), z[10], 12);
        Assert.Equal(Math.Sqrt(10) * (4 * Math.Pow(r, 4) - 3 * r * r) * S(2 * th), z[11], 12);
        Assert.Equal(Math.Sqrt(5) * (6 * Math.Pow(r, 4) - 6 * r * r + 1), z[12], 12);
        Assert.Equal(Math.Sqrt(10) * (4 * Math.Pow(r, 4) - 3 * r * r) * C(2 * th), z[13], 12);
        Assert.Equal(Math.Sqrt(10) * Math.Pow(r, 4) * C(4 * th), z[14], 12);
    }

    [Fact]
    public void Noll_index_mapping_known_values()
    {
        // Noll: 1 piston, 2 x-tilt (cos), 3 y-tilt (sin), 4 defocus, 5 oblique astig (sin2t), 6 vertical astig (cos2t), 7 vertical coma (sin), 8 horizontal coma (cos), 9 oblique trefoil (sin3t), 10 trefoil (cos3t), 11 spherical
        var expect = new (int n, int m, int noll)[] { (0, 0, 1), (1, 1, 2), (1, -1, 3), (2, 0, 4), (2, -2, 5), (2, 2, 6), (3, -1, 7), (3, 1, 8), (3, -3, 9), (3, 3, 10), (4, 0, 11), (4, 2, 12), (4, -2, 13), (4, 4, 14), (4, -4, 15) };
        foreach (var e in expect) Assert.Equal(e.noll, ZernikeBasis.Noll(e.n, e.m));
        var all = Enumerable.Range(0, 66).Select(j => ZernikeBasis.Term(j).Noll).OrderBy(x => x).ToArray(); Assert.Equal(Enumerable.Range(1, 66), all);   // a permutation of 1..66
    }

    [Fact]
    public void Basis_is_orthonormal_on_the_unit_disk_to_order_10()
    {
        const int G = 700; var gram = new double[66, 66]; var v = new double[66]; double area = 0;
        for (int iy = 0; iy < G; iy++) for (int ix = 0; ix < G; ix++)
        {
            double x = (ix + 0.5) / G * 2 - 1, y = (iy + 0.5) / G * 2 - 1, rho = Math.Sqrt(x * x + y * y); if (rho > 1) continue;
            ZernikeBasis.Eval(66, rho, Math.Atan2(y, x), v); area++;
            for (int a = 0; a < 66; a++) for (int b = a; b < 66; b++) gram[a, b] += v[a] * v[b];
        }
        double worst = 0; for (int a = 0; a < 66; a++) for (int b = a; b < 66; b++) worst = Math.Max(worst, Math.Abs(gram[a, b] / area - (a == b ? 1 : 0)));
        Assert.True(worst < 0.01, $"max |<Zi,Zj> - delta| = {worst:G3}");   // unit rms over the disk
    }

    static (float[] w, byte[] mask) Synthetic(GoldenFixture fx, double[] coeffM, ZernikeOrientation o, ZernikeFit geom)
    {
        int M = fx.Par.M; var r = fx.Recon("concave_1_img"); var mask = r.Mask; var w = new float[M * M]; var v = new double[66];
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            long i = (long)y * M + x; if (mask[i] == 0) { w[i] = float.NaN; continue; }
            var (rho, th) = ZernikeTools.Polar(x, y, geom); ZernikeBasis.Eval(66, rho, th, v); double s = 0; for (int j = 0; j < 66; j++) s += coeffM[j] * v[j]; w[i] = (float)s;
        }
        return (w, mask);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Fit_recovers_known_coefficients_on_the_real_mask_for_every_orientation(bool flipX, bool flipY)
    {
        var o = new ZernikeOrientation { FlipX = flipX, FlipY = flipY }; var rnd = new Random(7); var c = new double[66];
        for (int j = 0; j < 66; j++) c[j] = (rnd.NextDouble() - 0.5) * 2e-7 / (1 + ZernikeBasis.Term(j).N);   // up to ~100 nm
        var geom = ZernikeTools.Fit(fx.Recon("concave_1_img").Opd, fx.Recon("concave_1_img").Mask, 1504, fx.Par.PitchM, 2, o);   // only for circle geometry + orientation
        var (w, mask) = Synthetic(fx, c, o, geom);
        var fit = ZernikeTools.Fit(w, mask, 1504, fx.Par.PitchM, 10, o);
        double worst = 0; for (int j = 0; j < 66; j++) worst = Math.Max(worst, Math.Abs(fit.Coeff[j] - c[j]));
        Assert.True(worst < 2e-9 + 0, $"max coefficient error {worst * 1e9:F4} nm (float32 map quantisation only)");   // float32 storage of the synthetic map
    }

    [Fact]
    public void Orientation_flags_change_only_the_expected_signs()
    {
        var r = fx.Recon("convex_2_img"); int M = 1504; double p = fx.Par.PitchM;
        var up = ZernikeTools.Fit(r.Opd, r.Mask, M, p, 10, new ZernikeOrientation());                          // y up, x right (default)
        var down = ZernikeTools.Fit(r.Opd, r.Mask, M, p, 10, new ZernikeOrientation { FlipY = true });         // y down (image rows) = Python
        var left = ZernikeTools.Fit(r.Opd, r.Mask, M, p, 10, new ZernikeOrientation { FlipX = true });
        for (int j = 0; j < 66; j++)
        {
            var t = ZernikeBasis.Term(j); double sY = t.M < 0 ? -1 : 1;                                        // y -> -y : theta -> -theta : sin terms change sign
            Assert.Equal(up.Coeff[j] * sY, down.Coeff[j], 12);
            int am = Math.Abs(t.M); double sX = t.M == 0 ? 1 : (t.M > 0 ? (am % 2 == 0 ? 1 : -1) : (am % 2 == 0 ? -1 : 1));   // x -> -x : theta -> pi - theta
            // cos(m(pi-t)) = (-1)^m cos(mt) ; sin(m(pi-t)) = -(-1)^m sin(mt)
            double expect = t.M >= 0 ? ((am % 2 == 0) ? 1 : -1) : ((am % 2 == 0) ? -1 : 1); _ = sX;
            Assert.Equal(up.Coeff[j] * expect, left.Coeff[j], 12);
        }
        Assert.Equal(up.Diopters(4), down.Diopters(4), 12); Assert.Equal(up.Diopters(4), left.Diopters(4), 12);   // power is orientation independent
    }

    [Fact]
    public void Existing_report_mapped_onto_osa_gives_the_same_lens_power()
    {
        var r = fx.Recon("concave_1_img");
        Assert.InRange(r.Stats.PowerD, 0.7110 - 0.0005, 0.7110 + 0.0005);                                      // +0.7110 D (Python: 0.71101)
        Assert.InRange(r.Stats.AstigRmsNm, 34.0, 35.0); Assert.InRange(r.Stats.ComaRmsNm, 198.0, 199.2); Assert.InRange(r.Stats.SphericalRmsNm, 28.0, 28.8);   // Python: 34.52 / 198.65 / 28.40
        var f10 = ZernikeTools.Fit(r.Opd, r.Mask, 1504, fx.Par.PitchM, 10);
        Console.WriteLine($"ZPOWER order-6 report {r.Stats.PowerD:F4} D; order-10 joint fit {f10.Diopters(4):F4} D");
        Assert.InRange(f10.Diopters(4), 0.70, 0.72);
    }

    sealed class PyFrame { public double cx { get; set; } public double cy { get; set; } public double radius_px { get; set; } public long n_fit_pixels { get; set; } public double[] coeff_m { get; set; } = Array.Empty<double>(); }
    Dictionary<string, PyFrame> Py() => JsonSerializer.Deserialize<Dictionary<string, PyFrame>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "python_zernike_order10.json")))!;

    public static IEnumerable<object[]> Frames() => GoldenFixture.Frames.Select(n => new object[] { n });

    [Theory, MemberData(nameof(Frames))]
    public void Fit_of_the_golden_opd_matches_the_python_fit_coefficient_by_coefficient(string name)
    {
        // identical input (Python's own OPD) -> isolates the fitting code. Python convention = y down, so FlipY = true; both run order 10.
        var py = Py()[name]; var gold = fx.GoldenOpd(name); var mask = gold.Select(v => (byte)(float.IsNaN(v) ? 0 : 1)).ToArray();
        var fit = ZernikeTools.Fit(gold, mask, 1504, fx.Par.PitchM, 10, new ZernikeOrientation { FlipY = true });
        Assert.Equal(py.cx, fit.Cx, 6); Assert.Equal(py.cy, fit.Cy, 6); Assert.Equal(py.radius_px, fit.RadiusPx, 6); Assert.Equal(py.n_fit_pixels, fit.FitPixels);
        double worst = 0; int wj = 0; for (int j = 0; j < 66; j++) { double d = Math.Abs(fit.Coeff[j] - py.coeff_m[j]) * 1e9; if (d > worst) { worst = d; wj = j; } }
        Console.WriteLine($"ZPY {name}: golden-OPD fit vs Python max |dc| = {worst:G3} nm (j={wj})");
        Assert.True(worst < 0.01, $"{name}: max coefficient difference {worst:G3} nm at j={wj}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void Osa_coefficients_of_the_csharp_pipeline_match_python_in_the_default_orientation(string name)
    {
        // C# pipeline OPD (default OSA orientation: y up) vs Python fit (y down): sine terms (m<0) differ in sign only.
        var py = Py()[name]; var r = fx.Recon(name);
        var fit = ZernikeTools.Fit(r.Opd, r.Mask, 1504, fx.Par.PitchM, 10);
        double worst = 0; int wj = 0;
        for (int j = 0; j < 66; j++)
        {
            double sign = ZernikeBasis.Term(j).M < 0 ? -1 : 1; double d = Math.Abs(fit.Coeff[j] - sign * py.coeff_m[j]) * 1e9; if (d > worst) { worst = d; wj = j; }
        }
        Console.WriteLine($"ZPY2 {name}: C# pipeline fit (y up) vs Python (sign-mapped) max |dc| = {worst:G3} nm (j={wj}); defocus {fit.Coeff[4] * 1e9:F2} vs {py.coeff_m[4] * 1e9:F2} nm");
        Assert.True(worst < 0.1, $"{name}: max coefficient difference {worst:G3} nm at j={wj}");
    }

    [Fact]
    public void Removal_is_non_destructive_and_only_removes_ticked_terms()
    {
        var r = fx.Recon("concave_1_img"); var before = (float[])r.Opd.Clone(); int M = 1504;
        var fit = ZernikeTools.Fit(r.Opd, r.Mask, M, fx.Par.PitchM, 10); var sel = ZernikeTools.Preset("piston+tilt");
        var filt = ZernikeTools.Subtract(r.Opd, r.Mask, fit, sel);
        Assert.Equal(before, r.Opd);                                                                    // input untouched
        var refit = ZernikeTools.Fit(filt, r.Mask, M, fx.Par.PitchM, 10);
        for (int j = 0; j < 3; j++) Assert.True(Math.Abs(refit.Coeff[j]) < 3e-9, $"j={j} still {refit.Coeff[j] * 1e9:F3} nm");   // removed terms vanish
        for (int j = 3; j < 66; j++) Assert.True(Math.Abs(refit.Coeff[j] - fit.Coeff[j]) < 3e-9, $"j={j} changed {(refit.Coeff[j] - fit.Coeff[j]) * 1e9:F3} nm");   // others unchanged (orthogonality on the fit region)
        // all terms removed -> residual far below the original
        var all = ZernikeTools.Subtract(r.Opd, r.Mask, fit, ZernikeTools.Preset("order", 10)); var (rmsAll, _) = ZernikeTools.RmsPv(all, r.Mask); var (rms0, pv0) = ZernikeTools.RmsPv(r.Opd, r.Mask);
        Assert.True(rmsAll < 0.06 * rms0, $"residual after removing all terms {rmsAll * 1e9:F1} nm vs {rms0 * 1e9:F1} nm");
        var (rmsDef, pvDef) = ZernikeTools.RmsPv(ZernikeTools.Subtract(r.Opd, r.Mask, fit, ZernikeTools.Preset("+defocus")), r.Mask);
        Assert.True(rmsDef < 0.35 * rms0 && pvDef < pv0, $"defocus removal: rms {rms0 * 1e9:F0} -> {rmsDef * 1e9:F0} nm");
    }

    [Fact]
    public void Presets_and_warnings()
    {
        Assert.Equal(new[] { 0, 1, 2 }, Idx(ZernikeTools.Preset("piston+tilt")));
        Assert.Equal(new[] { 0, 1, 2, 4 }, Idx(ZernikeTools.Preset("+defocus")));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Idx(ZernikeTools.Preset("+astigmatism")));
        Assert.Equal(15, Idx(ZernikeTools.Preset("order", 4)).Length); Assert.Equal(66, Idx(ZernikeTools.Preset("order", 10)).Length);
        Assert.Empty(ZernikeTools.Warnings(ZernikeTools.Preset("piston+tilt")));
        Assert.Contains(ZernikeTools.Warnings(ZernikeTools.Preset("+defocus")), w => w.Contains("Defocus"));
        Assert.Equal(2, ZernikeTools.Warnings(ZernikeTools.Preset("+astigmatism")).Count);
        static int[] Idx(bool[] f) => Enumerable.Range(0, f.Length).Where(i => f[i]).ToArray();
    }

    [Fact]
    public void Diopter_conversion_of_defocus_matches_report()
    {
        var r = fx.Recon("convex_2_img"); var f6 = ZernikeTools.Fit(r.Opd, r.Mask, 1504, fx.Par.PitchM, 6);
        Assert.Equal(r.Stats.PowerD, f6.Diopters(4), 9); Assert.True(double.IsNaN(f6.Diopters(7)));
    }
    [Fact]
    public void Export_of_the_filtered_map_records_removed_terms_in_header_and_sidecar()
    {
        var r = fx.Recon("concave_1_img"); var oc = new ReconOutcome { File = Path.Combine(fx.Root, "concave_1_img.tif"), Mode = "without reference", Result = r, CarriersUsed = fx.Calib };
        var fit = ZernikeTools.Fit(r.Opd, r.Mask, 1504, fx.Par.PitchM, 10); var zr = ZernikeRemoval.Make(r.Opd, r.Mask, fit, ZernikeTools.Preset("+defocus"));
        string dir = Path.Combine(Path.GetTempPath(), "qwlsi_zexp_" + Guid.NewGuid().ToString("N")); var before = (float[])r.Opd.Clone();
        try
        {
            var o = new ExportOptions { Npy = true, Matrix = new MatrixOptions { Header = true } };
            var files = ResultExporter.Export(oc, fx.Par, o, dir, null, null, zr);
            Assert.Equal(before, r.Opd);                                                                  // raw result untouched
            var txt = files.Single(f => f.EndsWith("_OPD_filtered_nm.txt")); var lines = File.ReadLines(txt).Take(14).ToList();
            Assert.Contains(lines, l => l.StartsWith("# zernike removal: OSA/ANSI to order 10"));
            Assert.Contains(lines, l => l.Contains("zernike removed:") && l.Contains("j=4 (n=2,m=0, Noll 4, defocus)") && l.Contains("j=0 (n=0,m=0"));
            Assert.Contains(lines, l => l.Contains("rms before/after"));
            var npy = Npy.ReadFloat32(files.Single(f => f.EndsWith("_OPD_filtered_m.npy")), out _, out _); Assert.Equal(zr.Filtered[800 * 1504 + 750], npy[800 * 1504 + 750]);
            using var d = JsonDocument.Parse(File.ReadAllText(files.Single(f => f.EndsWith("_export.json")))); var z = d.RootElement.GetProperty("zernike_removal");
            Assert.Equal(10, z.GetProperty("fit_order").GetInt32()); Assert.Equal(4, z.GetProperty("removed").GetArrayLength());
            var def = z.GetProperty("removed").EnumerateArray().Single(e => e.GetProperty("ansi_j").GetInt32() == 4);
            Assert.Equal(4, def.GetProperty("noll_j").GetInt32()); Assert.Equal("defocus", def.GetProperty("name").GetString()); Assert.InRange(def.GetProperty("diopters").GetDouble(), 0.70, 0.72);
            Assert.True(z.GetProperty("rms_after_nm").GetDouble() < z.GetProperty("rms_before_nm").GetDouble());
            Assert.Contains("Defocus", z.GetProperty("warnings")[0].GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
