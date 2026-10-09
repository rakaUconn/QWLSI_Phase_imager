using System.Globalization;
using System.Text.Json;
using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>All-frames Zernike table, common circle, CSV, mask-only cache read and "export all filtered maps" (1.1.0).</summary>
[Collection("heavy")]
public class ZernikeTableTests
{
    readonly GoldenFixture fx;
    public ZernikeTableTests(GoldenFixture f) { fx = f; }
    static readonly string[] Names = { "concave_1_img", "convex_2_img", "concave_2_img", "convex_1_img", "convex_1-5_img" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const int M = 1504;

    ReconOutcome Oc(string n) => new() { File = Path.Combine(fx.Root, n + ".tif"), Mode = "without reference", Result = fx.Recon(n), CarriersUsed = fx.Calib };
    List<(string name, Func<CancellationToken, ReconOutcome> load, Func<CancellationToken, byte[]?>? loadMask)> Frames(int count = 5) =>
        Names.Take(count).Select(n => (n, (Func<CancellationToken, ReconOutcome>)(_ => Oc(n)), (Func<CancellationToken, byte[]?>?)(_ => fx.Recon(n).Mask))).ToList();

    static ReconOutcome Synth(Func<int, int, double> f, byte[] mask, int m) { var o = new float[m * m]; for (int y = 0; y < m; y++) for (int x = 0; x < m; x++) o[y * m + x] = mask[y * m + x] != 0 ? (float)f(x, y) : float.NaN; return new ReconOutcome { File = "synthetic.tif", Result = new ReconResult { M = m, Opd = o, Mask = mask, Intensity = new float[m * m] } }; }
    static byte[] Disc(int m, double cx, double cy, double r) { var k = new byte[m * m]; for (int y = 0; y < m; y++) for (int x = 0; x < m; x++) k[y * m + x] = (byte)((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r ? 1 : 0); return k; }

    static double[] Col(ZernikeTable t, string header) { int k = t.Columns.ToList().FindIndex(c => c.Header == header); return t.Rows.Select(r => t.Columns[k].Get(r)).ToArray(); }

    // ---------------------------------------------------------------------------------------------------------------- (a) rows == single-frame results
    [Theory]
    [InlineData(false, false, "+defocus")]
    [InlineData(false, true, "order")]
    public void Every_row_equals_the_independent_single_frame_result(bool flipX, bool flipY, string preset)
    {
        var orient = new ZernikeOrientation { FlipX = flipX, FlipY = flipY }; var sel = preset == "order" ? ZernikeTools.Preset("order", 4) : ZernikeTools.Preset(preset);
        var t = ZernikeTableBuilder.Build(Frames(4), sel, orient, ZernikePupil.PerFrame, fx.Par.PitchM);
        Assert.Equal(4, t.Rows.Count);
        for (int i = 0; i < 4; i++)
        {
            var r = fx.Recon(Names[i]); var fit = ZernikeTools.Fit(r.Opd, r.Mask, M, fx.Par.PitchM, 10, orient); var row = t.Rows[i];
            Assert.Equal(i + 1, row.Number); Assert.Equal(Names[i], row.Name);
            Assert.True(Enumerable.Range(0, 66).All(j => Math.Abs(fit.Coeff[j] - row.CoeffM[j]) <= 1e-18 + 1e-12 * Math.Abs(fit.Coeff[j])));
            Assert.Equal(fit.Diopters(4), row.PowerD, 9);
            Assert.Equal(Math.Sqrt(fit.Coeff[3] * fit.Coeff[3] + fit.Coeff[5] * fit.Coeff[5]) * 1e9, row.AstigRmsNm, 6);
            double ax = 0.5 * Math.Atan2(fit.Coeff[3], fit.Coeff[5]) * 180 / Math.PI; if (ax < 0) ax += 180; Assert.Equal(ax, row.AstigAxisDeg, 6);
            var zr = ZernikeRemoval.Make(r.Opd, r.Mask, fit, sel); var (r0, p0) = ZernikeTools.RmsPv(r.Opd, r.Mask); var (r1, p1) = ZernikeTools.RmsPv(ZernikeTools.Subtract(r.Opd, r.Mask, fit, sel), r.Mask);
            Assert.Equal(r0 * 1e9, row.RmsBeforeNm, 6); Assert.Equal(p0 * 1e6, row.PvBeforeUm, 9); Assert.Equal(r1 * 1e9, row.RmsAfterNm, 6); Assert.Equal(p1 * 1e6, row.PvAfterUm, 9);
            Assert.Equal(zr.RmsAfterM * 1e9, row.RmsAfterNm, 6); Assert.Equal(fit.FitPixels, row.FitPixels); Assert.Equal(fit.Cx, row.CircleCx, 9); Assert.Equal(fit.RadiusPx, row.CircleRadiusPx, 9);
            Assert.True(row.AstigAxisDeg >= 0 && row.AstigAxisDeg < 180);
        }
        // mean / sample std of a plain column
        var rms = Col(t, "rms_before_nm"); Assert.Equal(rms.Average(), t.Mean[t.Columns.ToList().FindIndex(c => c.Header == "rms_before_nm")], 9);
        Assert.Equal(Math.Sqrt(rms.Sum(v => (v - rms.Average()) * (v - rms.Average())) / 3), t.Std[t.Columns.ToList().FindIndex(c => c.Header == "rms_before_nm")], 9);
        var one = ZernikeTableBuilder.Build(Frames(1), sel, orient, ZernikePupil.PerFrame, fx.Par.PitchM); Assert.All(one.Std, s => Assert.Equal(0.0, s));   // n == 1: std 0
    }

    [Fact]
    public void Order6_report_power_and_table_power_agree()
    {
        var t = ZernikeTableBuilder.Build(Frames(3), ZernikeTools.Preset("piston+tilt"), new ZernikeOrientation(), ZernikePupil.PerFrame, fx.Par.PitchM);
        for (int i = 0; i < 3; i++) Assert.InRange(Math.Abs(t.Rows[i].PowerD - fx.Recon(Names[i]).Stats.PowerD), 0, 0.002);
        Assert.InRange(t.Rows[0].PowerD, 0.70, 0.72);       // concave_1: +0.711 D
    }

    // ---------------------------------------------------------------------------------------------------------------- (b) CSV
    static (List<string> comments, string[] header, List<string[]> rows, string[] mean, string[] std) ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path); var comments = lines.Where(l => l.StartsWith("# ")).ToList(); var body = lines.Where(l => !l.StartsWith("#") && l.Length > 0).ToList();
        var header = body[0].Split(','); var rest = body.Skip(1).Select(l => l.Split(',')).ToList();
        return (comments, header, rest.Where(r => r[0] != "\"mean\"" && r[0] != "\"std\"").ToList(), rest.First(r => r[0] == "\"mean\""), rest.First(r => r[0] == "\"std\""));
    }

    [Fact]
    public void Csv_matches_single_frame_results_and_summary_rows()
    {
        var sel = ZernikeTools.Preset("+astigmatism"); var orient = new ZernikeOrientation { FlipY = true }; var t = ZernikeTableBuilder.Build(Frames(4), sel, orient, ZernikePupil.PerFrame, fx.Par.PitchM);
        string dir = Path.Combine(Path.GetTempPath(), "qwlsi_zt_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string p = ZernikeTableCsv.Write(Path.Combine(dir, "t.csv"), t); var (cm, hd, rows, mean, std) = ReadCsv(p);
            Assert.Contains(cm, l => l.Contains("frames: 4")); Assert.Contains(cm, l => l.Contains("convention: OSA/ANSI") && l.Contains("y down (image rows)"));
            Assert.Contains(cm, l => l.StartsWith("# pupil: per-frame circle")); Assert.Contains(cm, l => l.Contains("j=4 defocus") && l.Contains("j=5 vertical astigmatism")); Assert.Contains(cm, l => l.Contains("sample standard deviation"));
            Assert.Contains(cm, l => l.Contains("circular statistics"));
            var expectHdr = new[] { "frame", "number" }.Concat(Enumerable.Range(0, 6).Select(j => $"\"Z{j} {ZernikeBasis.Term(j).Name} [nm]\"")).Concat(new[] { "power_D", "astig_rms_nm", "astig_axis_deg", "rms_before_nm", "pv_before_um", "rms_after_nm", "pv_after_um", "fit_pixels", "circle_cx_px", "circle_cy_px", "circle_radius_px" });
            Assert.Equal(expectHdr, hd); Assert.Equal(4, rows.Count);
            for (int i = 0; i < 4; i++)
            {
                var r = fx.Recon(Names[i]); var fit = ZernikeTools.Fit(r.Opd, r.Mask, M, fx.Par.PitchM, 10, orient); var zr = ZernikeRemoval.Make(r.Opd, r.Mask, fit, sel); var c = rows[i];
                Assert.Equal($"\"{Names[i]}\"", c[0]); Assert.Equal((i + 1).ToString(), c[1]);
                for (int j = 0; j < 6; j++) Assert.InRange(Math.Abs(fit.Coeff[j] * 1e9 - double.Parse(c[2 + j], Inv)), 0, 6e-5);
                Assert.InRange(Math.Abs(fit.Diopters(4) - double.Parse(c[8], Inv)), 0, 6e-6); Assert.InRange(Math.Abs(Math.Sqrt(fit.Coeff[3] * fit.Coeff[3] + fit.Coeff[5] * fit.Coeff[5]) * 1e9 - double.Parse(c[9], Inv)), 0, 6e-5);
                Assert.InRange(Math.Abs(zr.RmsBeforeM * 1e9 - double.Parse(c[11], Inv)), 0, 6e-5); Assert.InRange(Math.Abs(zr.PvBeforeM * 1e6 - double.Parse(c[12], Inv)), 0, 6e-6); Assert.InRange(Math.Abs(zr.RmsAfterM * 1e9 - double.Parse(c[13], Inv)), 0, 6e-5); Assert.InRange(Math.Abs(zr.PvAfterM * 1e6 - double.Parse(c[14], Inv)), 0, 6e-6);
                Assert.Equal(fit.FitPixels, long.Parse(c[15])); Assert.Equal(fit.RadiusPx, double.Parse(c[18], Inv), 2);
            }
            // summary rows from independently parsed single-frame values
            for (int k = 2; k < 15; k++)
            {
                var v = rows.Select(r => double.Parse(r[k], Inv)).ToArray(); double m = v.Average(), s = Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / 3);
                if (hd[k] == "astig_axis_deg") { var (am, asd) = ZernikeTable.AxisStats(v); Assert.InRange(Math.Abs(am - double.Parse(mean[k], Inv)), 0, 2e-3); Assert.InRange(Math.Abs(asd - double.Parse(std[k], Inv)), 0, 2e-3); continue; }
                Assert.InRange(Math.Abs(m - double.Parse(mean[k], Inv)), 0, 6e-5); Assert.InRange(Math.Abs(s - double.Parse(std[k], Inv)), 0, 1.2e-4);
            }
            Assert.All(new[] { 15, 16, 17, 18 }, k => { Assert.Equal("", mean[k]); Assert.Equal("", std[k]); });
            // 'none' selection: no term columns; summary can be left out
            var none = ZernikeTableBuilder.Build(Frames(2), ZernikeTools.Preset("none"), new ZernikeOrientation(), ZernikePupil.PerFrame, fx.Par.PitchM);
            string p2 = ZernikeTableCsv.Write(Path.Combine(dir, "n.csv"), none, includeSummary: false); var lines = File.ReadAllLines(p2).Where(l => !l.StartsWith("#")).ToList();
            Assert.StartsWith("frame,number,power_D,astig_rms_nm,astig_axis_deg,rms_before_nm", lines[0]); Assert.Equal(3, lines.Count); Assert.DoesNotContain(lines, l => l.StartsWith("\"mean\""));
            Assert.Contains(File.ReadAllLines(p2), l => l.Contains("removed terms") && l.EndsWith("none"));
            Assert.Equal(none.Rows[0].RmsBeforeNm, none.Rows[0].RmsAfterNm, 6);          // nothing removed: before == after
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------------------------------------------------------- (c) common circle
    [Fact]
    public void Common_circle_of_the_real_masks_is_the_circle_of_the_intersection()
    {
        var masks = Names.Select(n => fx.Recon(n).Mask).ToList(); var (c, cnt) = ZernikeTools.CommonCircle(masks, M);
        var and = new byte[M * M]; double sx = 0, sy = 0; long k = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) { bool all = masks.All(m => m[y * M + x] != 0); if (all) { and[y * M + x] = 1; sx += x; sy += y; k++; } }
        Assert.Equal(k, cnt); Assert.Equal(sx / k, c.Cx, 9); Assert.Equal(sy / k, c.Cy, 9); Assert.Equal(Math.Sqrt(k / Math.PI), c.RadiusPx, 9);
        foreach (var m in masks) for (int i = 0; i < m.Length; i++) if (and[i] != 0) Assert.NotEqual((byte)0, m[i]);               // intersection is a subset of every mask
        Assert.All(masks, m => Assert.True(k < m.Count(b => b != 0)));
        int differ = 0; foreach (var n in Names) { var f = ZernikeTools.Fit(fx.Recon(n).Opd, fx.Recon(n).Mask, M, fx.Par.PitchM, 6); if (Math.Abs(f.RadiusPx - c.RadiusPx) > 0.5 || Math.Abs(f.Cx - c.Cx) > 0.5 || Math.Abs(f.Cy - c.Cy) > 0.5) differ++; }
        Assert.True(differ >= 3, $"common circle differs from {differ}/5 per-frame circles");
        Assert.Equal(c, ZernikeTools.CommonCircle(masks.Select(m => m)).circle);                                                  // streaming overload
    }

    [Fact]
    public void Common_circle_of_synthetic_masks_empty_intersection_and_no_masks()
    {
        const int m = 100; var a = Disc(m, 40, 50, 20); var b = Disc(m, 50, 50, 20); var (c, cnt) = ZernikeTools.CommonCircle(new[] { a, b }, m);
        Assert.Equal(45.0, c.Cx, 9); Assert.Equal(50.0, c.Cy, 9);                                                                    // symmetric lens: centroid exactly midway
        long brute = 0; for (int i = 0; i < a.Length; i++) if (a[i] != 0 && b[i] != 0) brute++;
        Assert.Equal(brute, cnt); Assert.Equal(Math.Sqrt(brute / Math.PI), c.RadiusPx, 9);
        double d = 10, r = 20, lens = 2 * r * r * Math.Acos(d / (2 * r)) - d / 2 * Math.Sqrt(4 * r * r - d * d);                    // analytic lens area
        Assert.InRange(brute / lens, 0.98, 1.02); Assert.InRange(c.RadiusPx / Math.Sqrt(lens / Math.PI), 0.99, 1.01);
        Assert.Throws<InvalidOperationException>(() => ZernikeTools.CommonCircle(new[] { Disc(m, 20, 50, 10), Disc(m, 80, 50, 10) }, m));
        var ex = Assert.Throws<InvalidOperationException>(() => ZernikeTools.CommonCircle(new[] { Disc(m, 20, 50, 10), Disc(m, 80, 50, 10) })); Assert.Contains("intersection", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ZernikeTools.CommonCircle(Array.Empty<byte[]>()));
        Assert.Throws<ArgumentException>(() => ZernikeTools.CommonCircle(new[] { a }, 99));
    }

    [Fact]
    public void Common_circle_fit_recovers_known_coefficients_on_the_intersection()
    {
        const int m = 200; var a = Disc(m, 90, 100, 60); var b = Disc(m, 110, 100, 60); var (c, _) = ZernikeTools.CommonCircle(new[] { a, b }, m);
        var geo = new ZernikeFit { Cx = c.Cx, Cy = c.Cy, RadiusPx = c.RadiusPx, Orient = new ZernikeOrientation() }; var truth = new double[66]; truth[1] = 50e-9; truth[2] = -30e-9; truth[4] = 200e-9; truth[7] = 40e-9; truth[12] = 10e-9; truth[20] = 5e-9;
        var buf = new double[66]; var oc = Synth((x, y) => { var (rho, th) = ZernikeTools.Polar(x, y, geo); ZernikeBasis.Eval(66, rho, th, buf); double s = 0; for (int j = 0; j < 66; j++) s += truth[j] * buf[j]; return s; }, a, m);
        var fit = ZernikeTools.Fit(oc.Result.Opd, oc.Result.Mask, m, 8e-6, 10, null, c); Assert.True(fit.CommonCircle);
        for (int j = 0; j < 66; j++) Assert.InRange(Math.Abs(fit.Coeff[j] - truth[j]) * 1e9, 0, 2e-3);      // float32 map storage limits this to ~1e-5 nm
        Assert.Contains("common circle", fit.PupilDescription);
        var per = ZernikeTools.Fit(oc.Result.Opd, oc.Result.Mask, m, 8e-6, 10); Assert.False(per.CommonCircle); Assert.Contains("this frame", per.PupilDescription);    // per-frame circle differs, coefficients are not recovered there
        Assert.True(Enumerable.Range(0, 66).Max(j => Math.Abs(per.Coeff[j] - truth[j])) > 1e-9);
    }

    [Fact]
    public void Per_frame_mode_reproduces_the_single_frame_fit_and_common_mode_has_identical_circles()
    {
        var sel = ZernikeTools.Preset("+defocus"); var o = new ZernikeOrientation();
        var per = ZernikeTableBuilder.Build(Frames(3), sel, o, ZernikePupil.PerFrame, fx.Par.PitchM); Assert.Null(per.CommonCircle);
        for (int i = 0; i < 3; i++) { var f = ZernikeTools.Fit(fx.Recon(Names[i]).Opd, fx.Recon(Names[i]).Mask, M, fx.Par.PitchM, 10); Assert.True(Enumerable.Range(0, 66).All(j => Math.Abs(f.Coeff[j] - per.Rows[i].CoeffM[j]) <= 1e-18 + 1e-12 * Math.Abs(f.Coeff[j]))); Assert.Equal(f.Cx, per.Rows[i].CircleCx); }
        var com = ZernikeTableBuilder.Build(Frames(3), sel, o, ZernikePupil.CommonCircle, fx.Par.PitchM); Assert.NotNull(com.CommonCircle); Assert.Equal(ZernikePupil.CommonCircle, com.Pupil);
        Assert.All(com.Rows, r => { Assert.Equal(com.CommonCircle!.Cx, r.CircleCx); Assert.Equal(com.CommonCircle.Cy, r.CircleCy); Assert.Equal(com.CommonCircle.RadiusPx, r.CircleRadiusPx); });
        Assert.NotEqual(per.Rows[0].CircleRadiusPx, com.Rows[0].CircleRadiusPx);
        for (int i = 0; i < 3; i++) { var f = ZernikeTools.Fit(fx.Recon(Names[i]).Opd, fx.Recon(Names[i]).Mask, M, fx.Par.PitchM, 10, o, com.CommonCircle); Assert.Equal(f.Diopters(4), com.Rows[i].PowerD, 9); Assert.Equal(f.FitPixels, com.Rows[i].FitPixels); }
        Assert.True(com.Rows.All(r => r.FitPixels < per.Rows[0].FitPixels));
        string dir = Path.Combine(Path.GetTempPath(), "qwlsi_zc_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { var cm = File.ReadAllLines(ZernikeTableCsv.Write(Path.Combine(dir, "c.csv"), com)).First(l => l.StartsWith("# pupil")); Assert.Contains("common circle", cm); Assert.Contains("intersection of all masks", cm); Assert.Contains(com.CommonCirclePixels.ToString(), cm); }
        finally { Directory.Delete(dir, true); }
        // the clicked-frame removal uses the table's pupil
        var rem = com.MakeRemoval(Oc(Names[1]), fx.Par.PitchM); Assert.True(rem.Fit.CommonCircle); Assert.Equal(com.CommonCircle!.RadiusPx, rem.Fit.RadiusPx);
    }

    // ---------------------------------------------------------------------------------------------------------------- (d) mask-only cache read
    static ReconOutcome Tiny(int seed, int m = 128) { var mask = Disc(m, 60 + seed, 64, 40); var oc = Synth((x, y) => (x + y) * 1e-9, mask, m); oc.Result.Stats = new WavefrontStats(1, 2, 3, 4, 5, 6, 7); return oc; }

    [Fact]
    public void TryGetMask_returns_exactly_the_cached_mask_and_null_for_missing_or_truncated()
    {
        string root = Path.Combine(Path.GetTempPath(), "qwlsi_zm_" + Guid.NewGuid().ToString("N"));
        using var cache = new ResultDiskCache(root, 1L << 30); var a = Tiny(1); var b = Tiny(2); cache.Put("a", a); cache.Put("b", b); cache.Put("c", Tiny(3));
        Assert.Equal(cache.TryGet("a")!.Result.Mask, cache.TryGetMask("a")); Assert.Equal(a.Result.Mask, cache.TryGetMask("a")); Assert.Equal(b.Result.Mask, cache.TryGetMask("b"));
        Assert.Null(cache.TryGetMask("nope"));
        using (var fs = new FileStream(Path.Combine(cache.Dir, "b.qrc"), FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - 1);
        Assert.Null(cache.TryGetMask("b")); Assert.False(cache.Contains("b"));
        File.Delete(Path.Combine(cache.Dir, "c.qrc")); Assert.Null(cache.TryGetMask("c"));
        using var store = new ResultStore(new ResultDiskCache(root + "_s", 1L << 30), 1); store.Add("x", Tiny(4)); store.Add("y", Tiny(5));     // x only on disk now (RAM holds y)
        Assert.Equal(Tiny(4).Result.Mask, store.FindMask("x")); Assert.Equal(Tiny(5).Result.Mask, store.FindMask("y")); Assert.Null(store.FindMask("zz"));
        cache.Dispose(); try { Directory.Delete(root, true); } catch { }
    }

    // ---------------------------------------------------------------------------------------------------------------- (e) one frame at a time, (f) cancellation
    [Fact]
    public void Build_holds_at_most_one_outcome_at_a_time()
    {
        var refs = new List<WeakReference>(); int maxAlive = 0;
        var frames = Names.Select(n => (n, (Func<CancellationToken, ReconOutcome>)(_ =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); int alive = refs.Count(w => w.IsAlive); maxAlive = Math.Max(maxAlive, alive);
            var oc = Oc(n); refs.Add(new WeakReference(oc)); return oc;
        }), (Func<CancellationToken, byte[]?>?)null)).ToList();
        var t = ZernikeTableBuilder.Build(frames, ZernikeTools.Preset("piston+tilt"), new ZernikeOrientation(), ZernikePupil.PerFrame, fx.Par.PitchM);
        Assert.Equal(5, t.Rows.Count); Assert.Equal(5, refs.Count); Assert.Equal(0, maxAlive);          // when frame i is loaded, frame i-1 has been dropped
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.DoesNotContain(refs, w => w.IsAlive);
        // common circle: the mask pass uses loadMask only (no full result loaded for it)
        int fullLoads = 0; var f2 = Names.Take(3).Select(n => (n, (Func<CancellationToken, ReconOutcome>)(_ => { fullLoads++; return Oc(n); }), (Func<CancellationToken, byte[]?>?)(_ => fx.Recon(n).Mask))).ToList();
        ZernikeTableBuilder.Build(f2, ZernikeTools.Preset("none"), new ZernikeOrientation(), ZernikePupil.CommonCircle, fx.Par.PitchM); Assert.Equal(3, fullLoads);
    }

    [Fact]
    public void Cancellation_stops_between_frames_and_progress_is_reported()
    {
        using var cts = new CancellationTokenSource(); int loads = 0; var done = new List<int>();
        var frames = Names.Select(n => (n, (Func<CancellationToken, ReconOutcome>)(_ => { if (++loads == 2) cts.Cancel(); return Oc(n); }), (Func<CancellationToken, byte[]?>?)null)).ToList();
        Assert.ThrowsAny<OperationCanceledException>(() => ZernikeTableBuilder.Build(frames, ZernikeTools.Preset("none"), new ZernikeOrientation(), ZernikePupil.PerFrame, fx.Par.PitchM, new Progress<(int, int)>(p => done.Add(p.Item1)), cts.Token));
        Assert.Equal(2, loads);
        var prog = new List<(int, int)>(); var sync = new SyncProgress<(int, int)>(prog.Add); ZernikeTableBuilder.Build(Frames(2), ZernikeTools.Preset("none"), new ZernikeOrientation(), ZernikePupil.PerFrame, fx.Par.PitchM, sync);
        Assert.Equal(new[] { (1, 2), (2, 2) }, prog);
    }
    sealed class SyncProgress<T> : IProgress<T> { readonly Action<T> a; public SyncProgress(Action<T> a) { this.a = a; } public void Report(T v) => a(v); }

    // ---------------------------------------------------------------------------------------------------------------- (g) export all filtered maps
    [Fact]
    public void Export_all_filtered_writes_one_file_per_frame_plus_the_table_and_states_the_pupil()
    {
        var sel = ZernikeTools.Preset("+defocus"); var o = new ZernikeOrientation(); var frames = Frames(3); var t = ZernikeTableBuilder.Build(frames, sel, o, ZernikePupil.CommonCircle, fx.Par.PitchM);
        string dir = Path.Combine(Path.GetTempPath(), "qwlsi_zb_" + Guid.NewGuid().ToString("N")); var prog = new List<(int, int)>();
        try
        {
            var opts = new ExportOptions { Matrix = new MatrixOptions { Header = true } };
            var files = ZernikeBatchExport.Run(frames, t, fx.Par, opts, dir, new SyncProgress<(int, int)>(prog.Add));
            Assert.Equal(3, files.Count(f => f.EndsWith("_OPD_filtered_nm.txt"))); Assert.Equal(3, files.Count(f => f.EndsWith("_export.json"))); Assert.Contains(files, f => f.EndsWith("zernike_table.csv")); Assert.True(File.Exists(Path.Combine(dir, "zernike_table.csv")));
            Assert.Equal((3, 3), prog.Last());
            var txt = files.Single(f => f.EndsWith("concave_1_img_OPD_filtered_nm.txt")); var lines = File.ReadLines(txt).Take(14).ToList();
            Assert.Contains(lines, l => l.StartsWith("# zernike pupil: common circle")); Assert.Contains(lines, l => l.Contains("zernike removed:") && l.Contains("j=4 (n=2,m=0, Noll 4, defocus)"));
            var r = fx.Recon("concave_1_img"); var fit = ZernikeTools.Fit(r.Opd, r.Mask, M, fx.Par.PitchM, 10, o, t.CommonCircle); var expect = ZernikeRemoval.Make(r.Opd, r.Mask, fit, sel).Filtered;
            var mat = File.ReadLines(txt).Where(l => !l.StartsWith("#")).ToList(); Assert.Equal(M, mat.Count); double worst = 0;
            for (int y = 0; y < M; y += 7) { var c = mat[y].Split('\t'); Assert.Equal(M, c.Length); for (int x = 0; x < M; x++) { float e = expect[y * M + x]; if (float.IsNaN(e)) { Assert.Equal("NaN", c[x]); continue; } worst = Math.Max(worst, Math.Abs(double.Parse(c[x], Inv) - e * 1e9)); } }
            Assert.InRange(worst, 0, 0.001);
            using var d = JsonDocument.Parse(File.ReadAllText(files.Single(f => f.EndsWith("concave_1_img_export.json")))); Assert.Contains("common circle", d.RootElement.GetProperty("zernike_removal").GetProperty("pupil").GetString());
            // the exported table CSV equals a freshly written one
            Assert.Equal(File.ReadAllText(ZernikeTableCsv.Write(Path.Combine(dir, "again.csv"), t)), File.ReadAllText(Path.Combine(dir, "zernike_table.csv")));
            // cancellation
            using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => ZernikeBatchExport.Run(frames, t, fx.Par, opts, dir + "_c", null, cts.Token));
        }
        finally { try { Directory.Delete(dir, true); } catch { } try { Directory.Delete(dir + "_c", true); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------------------------- (h) astigmatism axis
    [Fact]
    public void Astigmatism_axis_follows_the_documented_definition_and_flags()
    {
        const int m = 200; var mask = Disc(m, 100, 100, 80); var geo = new ZernikeFit { Cx = 100, Cy = 100, RadiusPx = Math.Sqrt(mask.Count(b => b != 0) / Math.PI), Orient = new ZernikeOrientation() }; var buf = new double[66]; var none = new bool[66];
        ReconOutcome Make(double c3, double c5) => Synth((x, y) => { var (rho, th) = ZernikeTools.Polar(x, y, geo); ZernikeBasis.Eval(66, rho, th, buf); return c3 * buf[3] + c5 * buf[5]; }, mask, m);
        double Axis(ReconOutcome oc, bool fx_ = false, bool fy_ = false) => ZernikeTableBuilder.MakeRow("s", 1, oc, 8e-6, none, new ZernikeOrientation { FlipX = fx_, FlipY = fy_ }, null).AstigAxisDeg;
        Assert.Equal(45.0, Axis(Make(100e-9, 0)), 3);          // only c3 (sin 2t): maximum OPD along 45 deg
        Assert.Equal(0.0, Axis(Make(0, 100e-9)), 3);           // only c5 (cos 2t): along +x
        Assert.Equal(90.0, Axis(Make(0, -100e-9)), 3);         // c5 < 0: maximum along +y
        Assert.Equal(135.0, Axis(Make(-100e-9, 0)), 3);
        double A = 150e-9, ang = 30 * Math.PI / 180; var oc30 = Make(A * Math.Sin(2 * ang), A * Math.Cos(2 * ang));
        Assert.Equal(30.0, Axis(oc30), 3); Assert.Equal(150.0, Axis(oc30, true, false), 3); Assert.Equal(150.0, Axis(oc30, false, true), 3); Assert.Equal(30.0, Axis(oc30, true, true), 3);   // one flip mirrors the axis, two restore it
        var row = ZernikeTableBuilder.MakeRow("s", 1, oc30, 8e-6, none, new ZernikeOrientation(), null); Assert.Equal(A * 1e9, row.AstigRmsNm, 3);
        // circular statistics: axes 179 and 1 degrees average to 0, not 90
        var (mean, std) = ZernikeTable.AxisStats(new[] { 179.0, 1.0 }); Assert.True(mean < 0.001 || mean > 179.999); Assert.InRange(std, 0.5, 1.5); Assert.Equal(0.0, ZernikeTable.AxisStats(new[] { 33.0 }).std, 9); Assert.Equal(33.0, ZernikeTable.AxisStats(new[] { 33.0 }).mean, 9);
    }
}
