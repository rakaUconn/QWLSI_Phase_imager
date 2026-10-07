using Qwlsi.Core;

namespace Qwlsi.Tests;

[Collection("heavy")]
public class SessionTests
{
    readonly GoldenFixture fx;
    public SessionTests(GoldenFixture f) { fx = f; }
    string P(string n) => Path.Combine(fx.Root, n + ".tif");

    [Fact]
    public void Session_without_reference_matches_golden_and_reports_peak_memory()
    {
        using var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib };
        var oc = s.ProcessFile(P("convex_2_img"));
        Assert.True(GoldenFixture.RmsDiffNm(oc.Result.Opd, fx.GoldenOpd("convex_2_img"), 1504, out _, out _, out _) < 5);
        Assert.Equal("without reference", oc.Mode); Assert.Contains(oc.Warnings, w => w.Contains("static system"));
        Console.WriteLine($"PEAKMEM working set {oc.PeakWorkingSetMb:F0} MB, private {oc.PeakPrivateMb:F0} MB, {oc.Seconds:F2} s");
        Assert.InRange(oc.PeakWorkingSetMb, 50, 2500);
    }

    [Fact]
    public void Session_with_reference_and_with_system_wavefront_agree()
    {
        using var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib, ReferencePath = P("ref") };
        var a = s.ProcessFile(P("concave_1_img")); Assert.Equal("with reference", a.Mode);
        s.ReferencePath = null; s.SystemWavefrontPath = Path.Combine(fx.Results, "ref_OPD_m.npy");
        var b = s.ProcessFile(P("concave_1_img")); Assert.Contains("system wavefront", b.Mode);
        double rms = GoldenFixture.RmsDiffNm(a.Result.Opd, b.Result.Opd, 1504, out _, out _, out _);
        Assert.True(rms < 10, $"with-ref vs system-wavefront-subtracted: {rms:F2} nm");
    }

    [Fact]
    public void Wavelength_is_editable_OPD_nearly_independent_field_phase_scales()
    {
        // arg A = (2 pi / lambda) [W(x+s/2) - W(x-s/2)] with shear s = lambda d f / p^2: lambda cancels to first order, so the OPD is almost
        // independent of the lambda setting (only a second-order finite-difference effect); lambda matters for the field phase 2 pi W / lambda.
        using var s650 = new ReconSession { Par = fx.Par, Calibration = fx.Calib };
        var par = new QwlsiParams { Lambda = 650e-9 * 1.05 }; using var s683 = new ReconSession { Par = par, Calibration = fx.Calib };
        var a = s650.ProcessFile(P("concave_1_img")); var b = s683.ProcessFile(P("concave_1_img"));
        double rms = GoldenFixture.RmsDiffNm(a.Result.Opd, b.Result.Opd, 1504, out _, out _, out _);
        Console.WriteLine($"LAMBDA +5%: OPD rms change {rms:F2} nm, power {a.Result.Stats.PowerD:F4} -> {b.Result.Stats.PowerD:F4} D");
        Assert.True(rms < 0.01 * a.Result.Stats.RmsNm, $"OPD changed by {rms:F1} nm");
        // the field phase uses lambda: same OPD, phase ratio 1/1.05
        int M = 1504, i = (M / 2) * M + M / 2; var fa = a.Result.BuildField(650e-9); var fb = a.Result.BuildField(650e-9 * 1.05);
        double pa = Math.Atan2(fa[2 * i + 1], fa[2 * i]), pb = Math.Atan2(fb[2 * i + 1], fb[2 * i]);
        double expect = 2 * Math.PI * a.Result.Opd[i] / (650e-9 * 1.05); double wrapped = Math.Atan2(Math.Sin(expect), Math.Cos(expect));
        Assert.Equal(wrapped, pb, 3); Assert.NotEqual(pa, pb);
    }

    [Fact]
    public void Batch_processes_files_reports_progress_and_can_be_cancelled()
    {
        using var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib }; var done = new List<string>(); var files = new[] { P("concave_1_img"), P("convex_1_img"), P("concave_2_img") };
        var rows = BatchRunner.Run(s, files, oc => done.Add(Path.GetFileName(oc.File)), null, null, default);
        Assert.Equal(3, rows.Count); Assert.Equal(3, done.Count); Assert.All(rows, r => Assert.True(r.Stats.PupilRadiusMm > 4));
        var path = Path.Combine(Path.GetTempPath(), "qwlsi_sum_" + Guid.NewGuid().ToString("N") + ".csv"); SummaryCsv.Write(path, rows);
        var lines = File.ReadAllLines(path); Assert.Equal(4, lines.Length); Assert.StartsWith("file,mode,power_D", lines[0]); File.Delete(path);

        using var cts = new CancellationTokenSource(); int n = 0; var t0 = DateTime.UtcNow;
        var task = Task.Run(() => BatchRunner.Run(s, files, _ => n++, null, null, cts.Token)); Thread.Sleep(1500); cts.Cancel(); var r2 = task.Result;
        Assert.True((DateTime.UtcNow - t0).TotalSeconds < 4.5, "cancel must stop promptly (within a CG iteration)"); Assert.True(n == 0 && r2.Count == 0);
    }

    [Fact]
    public void Batch_continues_after_a_bad_file()
    {
        var bad = Path.Combine(Path.GetTempPath(), "qwlsi_bad_" + Guid.NewGuid().ToString("N") + ".tif"); File.WriteAllText(bad, "not a tiff");
        using var s = new ReconSession { Par = fx.Par, Calibration = fx.Calib }; int errs = 0, ok = 0;
        var rows = BatchRunner.Run(s, new[] { bad, P("ref") }, _ => ok++, (f, e) => errs++); File.Delete(bad);
        Assert.Equal(1, errs); Assert.Equal(1, ok); Assert.Equal(2, rows.Count); Assert.NotEqual("", rows[0].Error);
    }

    [Fact]
    public void Calibration_auto_find_order_and_settings_roundtrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "qwlsi_cal_" + Guid.NewGuid().ToString("N")); string img = Path.Combine(root, "img"), app = Path.Combine(root, "app"), appdata = Path.Combine(root, "appdata"), last = Path.Combine(root, "last", "calibration.json");
        foreach (var d in new[] { img, app, appdata, Path.GetDirectoryName(last)! }) Directory.CreateDirectory(d);
        var searched = new List<string>(); Assert.Null(CalibrationLocator.Find(img, app, last, appdata, searched)); Assert.Equal(4, searched.Count);
        fx.Calib.Save(Path.Combine(appdata, "calibration.json")); Assert.Equal(Path.Combine(appdata, "calibration.json"), CalibrationLocator.Find(img, app, last, appdata));
        fx.Calib.Save(last); Assert.Equal(last, CalibrationLocator.Find(img, app, last, appdata));
        fx.Calib.Save(Path.Combine(app, "calibration.json")); Assert.Equal(Path.Combine(app, "calibration.json"), CalibrationLocator.Find(img, app, last, appdata));
        fx.Calib.Save(Path.Combine(img, "calibration.json")); Assert.Equal(Path.Combine(img, "calibration.json"), CalibrationLocator.Find(img, app, last, appdata));
        var st = new AppSettings { LastCalibrationPath = last }; st.Params.Lambda = 532e-9; st.Export.Matrix.Delim = Delimiter.Comma; var sp = Path.Combine(root, "settings.json"); st.Save(sp);
        var b = AppSettings.Load(sp); Assert.Equal(532e-9, b.Params.Lambda); Assert.Equal(Delimiter.Comma, b.Export.Matrix.Delim); Assert.Equal(last, b.LastCalibrationPath);
        var def = new AppSettings(); Assert.Equal(650e-9, def.Params.Lambda); Assert.Equal(Units.Nm, def.Export.Matrix.Unit); Assert.Equal(Delimiter.Tab, def.Export.Matrix.Delim); Assert.False(def.Export.Matrix.Header);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Calibrate_from_flat_via_session_with_frame_average()
    {
        using var s = new ReconSession { Par = fx.Par }; var c = s.CalibrateFromFlat(new[] { P("ref"), P("ref") });
        for (int k = 0; k < 2; k++) { Assert.InRange(Math.Abs(c.Fa[k] - fx.Calib.Fa[k]), 0, 1e-5); Assert.InRange(Math.Abs(c.Fb[k] - fx.Calib.Fb[k]), 0, 1e-5); }
    }
}
