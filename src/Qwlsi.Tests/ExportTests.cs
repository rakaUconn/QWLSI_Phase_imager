using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>HANDOFF §10: matrix export 1504x1504, round trip to the printed precision; plus other formats and the sidecar.</summary>
[Collection("heavy")]
public class ExportTests : IDisposable
{
    readonly GoldenFixture fx; readonly string dir;
    public ExportTests(GoldenFixture f) { fx = f; dir = Path.Combine(Path.GetTempPath(), "qwlsi_exp_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    ReconOutcome Outcome()
    {
        var r = fx.Recon("concave_1_img");
        return new ReconOutcome { File = Path.Combine(fx.Root, "concave_1_img.tif"), Mode = "without reference", Result = r, CarriersUsed = fx.Calib, Seconds = 1 };
    }

    static float[][] ReadMatrix(string path, char delim)
    {
        var rows = new List<float[]>();
        foreach (var line in File.ReadLines(path)) { if (line.StartsWith('#')) continue; rows.Add(line.Split(delim).Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray()); }
        return rows.ToArray();
    }

    [Fact]
    public void Default_matrix_txt_is_1504x1504_nm_tab_no_header_and_roundtrips_to_printed_precision()
    {
        var oc = Outcome(); var files = ResultExporter.Export(oc, fx.Par, new ExportOptions(), dir);
        var f = files.Single(x => x.EndsWith("_OPD_nm.txt")); string first = File.ReadLines(f).First();
        Assert.DoesNotContain('#', first); Assert.Contains('\t', first);
        var m = ReadMatrix(f, '\t'); Assert.Equal(1504, m.Length); Assert.All(m, row => Assert.Equal(1504, row.Length));
        double maxErr = 0; int nan = 0, M = fx.Par.M;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++)
        {
            float v = oc.Result.Opd[y * M + x], t = m[y][x];
            if (float.IsNaN(v)) { Assert.True(float.IsNaN(t)); nan++; continue; }
            maxErr = Math.Max(maxErr, Math.Abs(t - v * 1e9));
        }
        Assert.True(nan > 0); Assert.True(maxErr <= 5.5e-4 + 1e-3 * 1e-3 * 4000 /* float32 parse of ~4000 nm */, $"max |printed - value| = {maxErr}");
        Assert.True(new FileInfo(f).Length < 30_000_000, "file size should be ~20-25 MB");
        Assert.Contains(files, x => x.EndsWith("_export.json"));
    }

    [Theory]
    [InlineData(Delimiter.Space, ' ', Units.Um, 6, true, true)]
    [InlineData(Delimiter.Comma, ',', Units.Waves, 5, false, true)]
    public void Matrix_options_delimiter_units_decimals_zero_header(Delimiter d, char ch, Units u, int dec, bool zero, bool header)
    {
        var oc = Outcome(); var o = new ExportOptions { Matrix = new MatrixOptions { Delim = d, Unit = u, Decimals = dec, OutsideAsZero = zero, Header = header } };
        var files = ResultExporter.Export(oc, fx.Par, o, dir); var f = files.Single(x => x.Contains("_OPD_") && !x.Contains("xyz") && (x.EndsWith(".txt") || x.EndsWith(".csv")));
        Assert.EndsWith(d == Delimiter.Comma ? ".csv" : ".txt", f); Assert.Contains(Writers.UnitName(u), f);
        var lines = File.ReadLines(f).ToList(); Assert.True(lines[0].StartsWith("# ")); int hdr = lines.Count(l => l.StartsWith('#'));
        var m = ReadMatrix(f, ch); Assert.Equal(1504, m.Length);
        int M = fx.Par.M; double sc = Writers.UnitScale(u, fx.Par.Lambda); double maxErr = 0;
        for (int y = 0; y < M; y += 7) for (int x = 0; x < M; x += 5)
        {
            float v = oc.Result.Opd[y * M + x];
            if (float.IsNaN(v)) { if (zero) Assert.Equal(0f, m[y][x]); else Assert.True(float.IsNaN(m[y][x])); continue; }
            maxErr = Math.Max(maxErr, Math.Abs(m[y][x] - v * sc) / Math.Max(1e-12, Math.Pow(10, -dec)));
        }
        Assert.True(maxErr <= 0.51 + 1e-3 * Math.Abs(oc.Result.Opd.Where(v => !float.IsNaN(v)).Max() * sc) / Math.Pow(10, -dec) * 1e-3 + 2, $"max error in last digits {maxErr}");
        Assert.True(hdr >= 5);
    }

    [Fact]
    public void Npy_exports_are_readable_and_equal()
    {
        var oc = Outcome(); var o = new ExportOptions { MatrixTxt = false, Npy = true, Intensity = true, Mask = true, Field = true };
        ResultExporter.Export(oc, fx.Par, o, dir); string b = Path.Combine(dir, "concave_1_img");
        var opd = Npy.ReadFloat32(b + "_OPD_m.npy", out var shp, out var cx); Assert.Equal(new[] { 1504, 1504 }, shp); Assert.False(cx);
        for (int i = 0; i < opd.Length; i += 331) Assert.Equal(oc.Result.Opd[i], opd[i]);   // NaN != NaN handled below
        var fld = Npy.ReadFloat32(b + "_field.npy", out _, out var isC); Assert.True(isC); Assert.Equal(2 * 1504L * 1504, fld.LongLength);
        var gold = fx.GoldenOpd("concave_1_img"); double rms = GoldenFixture.RmsDiffNm(opd, gold, 1504, out _, out _, out _); Assert.True(rms < 5);
    }

    [Fact]
    public void Xyz_list_drops_masked_pixels()
    {
        var oc = Outcome(); var o = new ExportOptions { MatrixTxt = false, XyzTxt = true };
        var f = ResultExporter.Export(oc, fx.Par, o, dir).Single(x => x.Contains("xyz")); var lines = File.ReadAllLines(f);
        long valid = oc.Result.Opd.LongCount(v => !float.IsNaN(v)); Assert.Equal(valid, lines.Length);
        var p = lines[lines.Length / 2].Split('\t'); Assert.Equal(3, p.Length);
        double x0 = double.Parse(lines[0].Split('\t')[0], CultureInfo.InvariantCulture), r = fx.Par.PitchM * 1e3 * 1504;   // within the grid extent
        Assert.True(Math.Abs(x0) < r);
    }

    /// <summary>Cross-checks the files with numpy / scipy / tifffile when Python is available (skipped otherwise).</summary>
    [Fact]
    public void Files_load_in_python_numpy_scipy_tifffile()
    {
        var oc = Outcome(); var o = new ExportOptions { Npy = true, Mat = true, Tiff32 = true, Intensity = true, Mask = true, Field = true, XyzTxt = true };
        ResultExporter.Export(oc, fx.Par, o, dir); string b = Path.Combine(dir, "concave_1_img");
        string script = Path.Combine(dir, "chk.py");
        File.WriteAllText(script, string.Join("\n",
            "import numpy as np, scipy.io as sio, tifffile, sys",
            "b = sys.argv[1]",
            "t = np.loadtxt(b + '_OPD_nm.txt', delimiter='\\t'); assert t.shape == (1504, 1504), t.shape",
            "n = np.load(b + '_OPD_m.npy'); assert n.shape == (1504, 1504) and n.dtype == np.float32",
            "assert np.nanmax(np.abs(t - n * 1e9)) < 1e-2",
            "f = np.load(b + '_field.npy'); assert f.dtype == np.complex64 and f.shape == (1504, 1504)",
            "m = sio.loadmat(b + '_recon.mat'); assert m['OPD_m'].shape == (1504, 1504) and m['OPD_m'].dtype == np.float32",
            "assert np.allclose(np.nan_to_num(m['OPD_m']), np.nan_to_num(n), atol=0) and m['field'].dtype == np.complex64",
            "assert np.allclose(m['field'], f) and m['mask'].dtype == np.uint8 and abs(float(m['lambda_m'][0][0]) - 650e-9) < 1e-15",
            "tf = tifffile.imread(b + '_OPD_nm.tif'); assert tf.dtype == np.float32 and np.nanmax(np.abs(tf - t)) < 1e-2",
            "xyz = np.loadtxt(b + '_OPD_xyz_nm.txt'); assert xyz.shape[1] == 3",
            "print('PYOK')"));
        var psi = new ProcessStartInfo("python", $"\"{script}\" \"{b}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        Process? p; try { p = Process.Start(psi); } catch { return; }   // no python
        string so = p!.StandardOutput.ReadToEnd(), se = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0 && so.Contains("PYOK"), "python check failed:\n" + se + so);
    }

    [Fact]
    public void Sidecar_json_records_parameters()
    {
        var oc = Outcome(); var f = ResultExporter.Export(oc, fx.Par, new ExportOptions(), dir).Single(x => x.EndsWith("_export.json"));
        using var d = JsonDocument.Parse(File.ReadAllText(f)); var r = d.RootElement;
        Assert.Equal(650e-9, r.GetProperty("lambda_m").GetDouble()); Assert.Equal("Tab", r.GetProperty("delimiter").GetString());
        Assert.Equal(2, r.GetProperty("carriers_fa").GetArrayLength()); Assert.Equal("without reference", r.GetProperty("mode").GetString());
    }

    [Fact]
    public void System_wavefront_subtraction_reproduces_with_reference_result()
    {
        // single-frame OPD - ref_OPD_m  ==  golden-derived expectation to ~5 nm; and the recomputed lens power ~ reference-based value
        var r = fx.Recon("concave_1_img"); var sys = fx.GoldenOpd("ref");
        var sub = SystemWavefront.Subtract(r, sys, fx.Par);
        var expect = fx.GoldenOpd("concave_1_img").Zip(sys, (a, b) => a - b).ToArray();
        double rms = GoldenFixture.RmsDiffNm(sub.Opd, expect, 1504, out _, out _, out _);
        Assert.True(rms <= 5.0, $"{rms:F2} nm"); Assert.InRange(sub.Stats.PowerD, 0.71, 0.73);
    }
}
