using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qwlsi.Core;

/// <summary>Polls the process working set while a frame is processed (peak memory per frame).</summary>
public sealed class MemoryWatch : IDisposable
{
    readonly CancellationTokenSource cts = new(); readonly Task task; long peakWs, peakPrivate;
    public MemoryWatch()
    {
        var p = Process.GetCurrentProcess();
        task = Task.Run(() => { while (!cts.IsCancellationRequested) { p.Refresh(); peakWs = Math.Max(peakWs, p.WorkingSet64); peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64); Thread.Sleep(15); } });
    }
    public double PeakWorkingSetMb => peakWs / 1048576.0;
    public double PeakPrivateMb => peakPrivate / 1048576.0;
    public void Dispose() { cts.Cancel(); task.Wait(); }
}

public sealed class ReconOutcome
{
    public string File = ""; public string Mode = ""; public ReconResult Result = new();
    public double Seconds; public double PeakWorkingSetMb, PeakPrivateMb; public Calibration CarriersUsed = new();
    public string? SystemWavefrontFile; public string? ReferenceFile; public int Width;
    public List<string> Warnings = new();
}

/// <summary>
/// One processing context for the offline app: parameters, calibration, optional reference, optional system wavefront.
/// Handles frame-size changes (N), caches the reference lobes and the Processor, and reports peak memory per frame.
/// </summary>
public sealed class ReconSession : IDisposable
{
    public QwlsiParams Par { get; set; } = new();
    public Calibration? Calibration { get; set; }
    public string? CalibrationPath { get; set; }
    /// <summary>With-reference mode when set.</summary>
    public string? ReferencePath { get; set; }
    /// <summary>Without-reference mode only: path of a .npy system wavefront (OPD [m], NaN outside) to subtract.</summary>
    public string? SystemWavefrontPath { get; set; }
    public SolveOptions Solve { get; set; } = new();

    Processor? proc; int procN; string? procKey; ReferenceData? refData; string? refKey; float[]? sysW; string? sysKey;

    static string ParKey(QwlsiParams p) => string.Join("|", p.Gamma, p.D, p.P, p.Z, p.Lambda, p.R, p.N, p.Dec);

    QwlsiParams ParamsForSize(int w)
    {
        if (w == Par.N) return Par;
        if (w % Par.Dec != 0) throw new InvalidDataException($"frame is {w} px wide; not divisible by decimation {Par.Dec}");
        return new QwlsiParams { Gamma = Par.Gamma, D = Par.D, P = Par.P, Z = Par.Z, Lambda = Par.Lambda, R = Par.R, N = w, Dec = Par.Dec };
    }

    Processor GetProcessor(QwlsiParams p)
    {
        string k = ParKey(p);
        if (proc == null || procKey != k) { proc?.Dispose(); proc = new Processor(p); procKey = k; procN = p.N; refData = null; refKey = null; }
        return proc;
    }

    public static (int w, int h) ReadSize(string tiffPath)
    {
        using var t = BitMiracle.LibTiff.Classic.Tiff.Open(tiffPath, "r") ?? throw new IOException("cannot open " + tiffPath);
        return (t.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH)[0].ToInt(), t.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH)[0].ToInt());
    }

    public ReconOutcome ProcessFile(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew(); using var mem = new MemoryWatch();
        var (w, h) = ReadSize(path); if (w != h) throw new InvalidDataException($"{Path.GetFileName(path)}: frame is {w}x{h}, square frames expected");
        var p = ParamsForSize(w); var pr = GetProcessor(p); var oc = new ReconOutcome { File = path, Width = w };
        if (w != Par.N) oc.Warnings.Add($"frame size {w} differs from the configured sensor size {Par.N}; using {w}. Carriers are in cyc/px and stay valid only for the same binning/relay.");

        ReferenceData? rd = null;
        if (ReferencePath != null)
        {
            string rk = ReferencePath + "|" + new FileInfo(ReferencePath).LastWriteTimeUtc.Ticks + "|" + procKey;
            if (refData == null || refKey != rk) { progress?.Report(0.0); float[]? rf = TiffIO.ReadFrame(ReferencePath, out _, out _); refData = pr.MakeReferenceConsuming(ref rf, null, Path.GetFileName(ReferencePath)); refKey = rk; }
            rd = refData; oc.Mode = "with reference"; oc.ReferenceFile = ReferencePath; oc.CarriersUsed = rd.Carriers;
        }
        else
        {
            if (Calibration == null) throw new InvalidOperationException("no calibration loaded (browse / auto-find / make one from a flat frame)");
            oc.Mode = "without reference"; oc.CarriersUsed = Calibration;
        }
        ct.ThrowIfCancellationRequested();

        var opt = new SolveOptions { Iters = Solve.Iters, UnwrapPasses = Solve.UnwrapPasses, Rtol = Solve.Rtol, Cancel = ct, Progress = pp => progress?.Report(pp) };
        float[]? frame = TiffIO.ReadFrame(path, out _, out _);
        var res = pr.ProcessConsuming(ref frame, Calibration, opt, rd);

        if (rd == null && !string.IsNullOrEmpty(SystemWavefrontPath))
        {
            string sk = SystemWavefrontPath + "|" + new FileInfo(SystemWavefrontPath).LastWriteTimeUtc.Ticks;
            if (sysW == null || sysKey != sk) { sysW = Npy.ReadFloat32(SystemWavefrontPath, out var shp, out var cplx); if (cplx || shp.Length != 2 || shp[0] != p.M || shp[1] != p.M) { sysW = null; throw new InvalidDataException($"system wavefront must be a {p.M}x{p.M} float32 .npy"); } sysKey = sk; }
            res = SystemWavefront.Subtract(res, sysW, p); oc.SystemWavefrontFile = SystemWavefrontPath; oc.Mode = "without reference, system wavefront subtracted";
        }
        else if (rd == null) oc.Warnings.Add("No reference: the OPD includes the static system/beam wavefront (~0.3-0.4 um rms). Load a reference or a system-wavefront file to remove it.");

        oc.Result = res; oc.Seconds = sw.Elapsed.TotalSeconds; oc.PeakWorkingSetMb = mem.PeakWorkingSetMb; oc.PeakPrivateMb = mem.PeakPrivateMb;
        return oc;
    }

    /// <summary>Calibrate from one or more flat frames (averaged). Returns the new calibration (not yet set as current).</summary>
    public CalibrationDiagnostics? LastCalibrationDiagnostics => proc?.LastCalibrationDiagnostics;

    public Calibration CalibrateFromFlat(IReadOnlyList<string> files, IProgress<string>? msg = null, CancellationToken ct = default)
    {
        if (files.Count == 0) throw new ArgumentException("no flat frames");
        float[]? avg = null; int w = 0;
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); msg?.Report($"reading {Path.GetFileName(files[i])} ({i + 1}/{files.Count})");
            var f = TiffIO.ReadFrame(files[i], out w, out _);
            if (avg == null) avg = f; else { if (f.Length != avg.Length) throw new InvalidDataException("flat frames differ in size"); for (long k = 0; k < f.Length; k++) avg[k] += f[k]; }
        }
        if (files.Count > 1) { float s = 1f / files.Count; for (long k = 0; k < avg!.Length; k++) avg[k] *= s; }
        var pr = GetProcessor(ParamsForSize(w)); msg?.Report("finding carriers and refining (3 passes)...");
        return pr.CalibrateFromFlatConsuming(ref avg, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} frames averaged, first: {Path.GetFileName(files[0])}");
    }

    /// <summary>Residual of the loaded calibration on a flat frame: carriers re-estimated from the frame vs the loaded ones [cyc/px], and the Zernike power of the flat frame (should be ~0.00 D).</summary>
    public (double dFaX, double dFaY, double dFbX, double dFbY) CarrierDifference(Calibration other, Calibration baseline) =>
        (other.Fa[0] - baseline.Fa[0], other.Fa[1] - baseline.Fa[1], other.Fb[0] - baseline.Fb[0], other.Fb[1] - baseline.Fb[1]);

    public void Dispose() { proc?.Dispose(); proc = null; refData = null; sysW = null; GC.Collect(); Mkl.FreeBuffers(); }
}

/// <summary>Subtract a static system wavefront (OPD [m], NaN outside) from a single-frame result; re-removes piston/tilt and recomputes the report.</summary>
public static class SystemWavefront
{
    public static ReconResult Subtract(ReconResult r, float[] sys, QwlsiParams par)
    {
        int M = r.M; long n = (long)M * M; var opd = new float[n]; var mask = new byte[n];
        for (long i = 0; i < n; i++) { bool ok = r.Mask[i] != 0 && !float.IsNaN(r.Opd[i]) && !float.IsNaN(sys[i]); mask[i] = (byte)(ok ? 1 : 0); opd[i] = ok ? r.Opd[i] - sys[i] : float.NaN; }
        // remove best-fit plane (piston + tilt) over the valid region
        double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, c = 0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) { long i = (long)y * M + x; if (mask[i] == 0) continue; double d = opd[i]; c++; sx += x; sy += y; sz += d; sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y; sxz += x * d; syz += y * d; }
        double[,] A = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, c } }; double[] rhs = { sxz, syz, sz };
        double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double D = Det(A); var co = new double[3];
        for (int k = 0; k < 3; k++) { var B = (double[,])A.Clone(); for (int j = 0; j < 3; j++) B[j, k] = rhs[j]; co[k] = Det(B) / D; }
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) { long i = (long)y * M + x; if (mask[i] != 0) opd[i] = (float)(opd[i] - (co[0] * x + co[1] * y + co[2])); }
        var inten = new float[n]; for (long i = 0; i < n; i++) inten[i] = mask[i] != 0 ? r.Intensity[i] : 0f;
        return new ReconResult { Opd = opd, Mask = mask, Intensity = inten, M = M, Stats = Zernike.Report(opd, mask, M, par.PitchM), Solve = r.Solve, BlackLevel = r.BlackLevel, TimingsMs = r.TimingsMs };
    }
}

public sealed class SummaryRow
{
    public string File = "", Mode = ""; public WavefrontStats Stats = new(0, 0, 0, 0, 0, 0, 0); public double Seconds, PeakMb; public string Error = "";
}

public static class SummaryCsv
{
    public static void Write(string path, IEnumerable<SummaryRow> rows)
    {
        var inv = CultureInfo.InvariantCulture; var sb = new StringBuilder();
        sb.AppendLine("file,mode,power_D,astig_rms_nm,coma_rms_nm,spherical_rms_nm,rms_nm,pv_um,pupil_radius_mm,seconds,peak_working_set_mb,error");
        foreach (var r in rows)
        {
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            sb.AppendLine(string.Join(",", Q(r.File), Q(r.Mode), r.Stats.PowerD.ToString("F4", inv), r.Stats.AstigRmsNm.ToString("F2", inv), r.Stats.ComaRmsNm.ToString("F2", inv), r.Stats.SphericalRmsNm.ToString("F2", inv),
                r.Stats.RmsNm.ToString("F2", inv), r.Stats.PvUm.ToString("F4", inv), r.Stats.PupilRadiusMm.ToString("F4", inv), r.Seconds.ToString("F2", inv), r.PeakMb.ToString("F0", inv), Q(r.Error)));
        }
        File.WriteAllText(path, sb.ToString());
    }
}

/// <summary>Writes the selected exports of one result + always a sidecar JSON with every parameter used.</summary>
public static class ResultExporter
{
    public static List<string> Export(ReconOutcome oc, QwlsiParams par, ExportOptions o, string outDir, string? baseName = null, Action<string, ReconResult>? pngHook = null, ZernikeRemoval? zr = null)
    {
        var r = oc.Result; int M = r.M; string tag = "OPD";
        if (zr != null)   // export the FILTERED map; the raw result object is not modified
        {
            tag = "OPD_filtered";
            r = new ReconResult { Opd = zr.Filtered, Intensity = r.Intensity, Mask = r.Mask, M = r.M, Solve = r.Solve, BlackLevel = r.BlackLevel, TimingsMs = r.TimingsMs, Stats = Zernike.Report(zr.Filtered, r.Mask, r.M, par.PitchM) };
            oc = new ReconOutcome { File = oc.File, Mode = oc.Mode + ", Zernike terms removed", Result = r, Seconds = oc.Seconds, PeakWorkingSetMb = oc.PeakWorkingSetMb, PeakPrivateMb = oc.PeakPrivateMb, CarriersUsed = oc.CarriersUsed, SystemWavefrontFile = oc.SystemWavefrontFile, ReferenceFile = oc.ReferenceFile, Width = oc.Width, Warnings = oc.Warnings };
        } var files = new List<string>(); Directory.CreateDirectory(outDir);
        baseName ??= Path.GetFileNameWithoutExtension(oc.File); string bp = Path.Combine(outDir, baseName); var mo = o.Matrix;
        double sc = Writers.UnitScale(mo.Unit, par.Lambda); string un = Writers.UnitName(mo.Unit); string ext = mo.Delim == Delimiter.Comma ? ".csv" : ".txt";
        var hdr = HeaderLines(oc, par, mo);
        if (zr != null) { hdr.Add($"zernike removal: OSA/ANSI to order {zr.Fit.Order}, joint masked least squares, {zr.Fit.Convention}"); hdr.Add("zernike removed: " + zr.Description); hdr.Add($"rms before/after: {zr.RmsBeforeM * 1e9:F2} / {zr.RmsAfterM * 1e9:F2} nm; PV before/after: {zr.PvBeforeM * 1e6:F4} / {zr.PvAfterM * 1e6:F4} um"); }
        else hdr.Add("zernike removal: none (raw OPD, piston removed by the solver)");
        void Add(string f) => files.Add(f);

        if (o.MatrixTxt)
        {
            if (o.Opd) { var f = $"{bp}_{tag}_{un}{ext}"; Writers.WriteMatrixText(f, r.Opd, M, M, sc, mo, hdr.Append($"quantity: {tag} [{un}], rows = image rows, outside pupil = {(mo.OutsideAsZero ? "0" : "NaN")}")); Add(f); }
            if (o.Intensity) { var f = $"{bp}_intensity{ext}"; Writers.WriteMatrixText(f, r.Intensity, M, M, 1, mo, hdr.Append("quantity: normalised intensity (p95 inside pupil = 1)"), applyScale: false); Add(f); }
            if (o.Mask) { var f = $"{bp}_mask{ext}"; Writers.WriteMaskText(f, r.Mask, M, M, mo, hdr.Append("quantity: pupil mask (1 = valid)")); Add(f); }
            if (o.Field)
            {
                var u = r.BuildField(par.Lambda); long n = (long)M * M; var a = new float[n]; var b = new float[n]; bool ri = o.FieldPair == FieldLayout.ReIm;
                for (long i = 0; i < n; i++) { float re = u[2 * i], im = u[2 * i + 1]; if (ri) { a[i] = re; b[i] = im; } else { a[i] = MathF.Sqrt(re * re + im * im); b[i] = MathF.Atan2(im, re); } }
                var mf = new MatrixOptions { Delim = mo.Delim, Decimals = Math.Max(mo.Decimals, 6), Header = mo.Header, OutsideAsZero = true };
                string n1 = ri ? "field_re" : "field_amp", n2 = ri ? "field_im" : "field_phase_rad";
                Writers.WriteMatrixText($"{bp}_{n1}{ext}", a, M, M, 1, mf, hdr.Append("quantity: " + n1), applyScale: false); Add($"{bp}_{n1}{ext}");
                Writers.WriteMatrixText($"{bp}_{n2}{ext}", b, M, M, 1, mf, hdr.Append("quantity: " + n2), applyScale: false); Add($"{bp}_{n2}{ext}");
            }
        }
        if (o.XyzTxt && o.Opd) { var f = $"{bp}_{tag}_xyz_{un}{ext}"; Writers.WriteXyz(f, r.Opd, r.Mask, M, par.PitchM, sc, mo, o.XyzDropMasked, hdr); Add(f); }
        if (o.Npy)
        {
            if (o.Opd) { Writers.WriteNpyF32($"{bp}_{tag}_m.npy", r.Opd, M, M); Add($"{bp}_{tag}_m.npy"); }
            if (o.Intensity) { Writers.WriteNpyF32($"{bp}_intensity.npy", r.Intensity, M, M); Add($"{bp}_intensity.npy"); }
            if (o.Mask) { Writers.WriteNpyU8($"{bp}_mask.npy", r.Mask, M, M); Add($"{bp}_mask.npy"); }
            if (o.Field) { Writers.WriteNpyC64($"{bp}_field.npy", r.BuildField(par.Lambda), M, M); Add($"{bp}_field.npy"); }
        }
        if (o.Mat)
        {
            var f = $"{bp}_recon.mat"; using (var m = new Writers.MatFile(f))
            {
                if (o.Opd) m.Single("OPD_m", r.Opd, M, M, nanToZero: mo.OutsideAsZero);
                if (o.Mask || o.Opd) m.UInt8("mask", r.Mask, M, M);
                if (o.Intensity) m.Single("intensity", r.Intensity, M, M);
                if (o.Field) m.ComplexSingle("field", r.BuildField(par.Lambda), M, M);
                if (zr != null) m.Double("zernike_removed_ansi_j", zr.Removed.Select((b, i) => (b, i)).Where(x => x.b).Select(x => (double)x.i).ToArray());
                m.Double("pitch_m", par.PitchM); m.Double("lambda_m", par.Lambda); m.Double("carrier_a", oc.CarriersUsed.Fa); m.Double("carrier_b", oc.CarriersUsed.Fb);
            }
            Add(f);
        }
        if (o.Tiff32)
        {
            if (o.Opd) { var f = $"{bp}_{tag}_{un}.tif"; Writers.WriteTiffF32(f, r.Opd, M, M, sc); Add(f); }
            if (o.Intensity) { var f = $"{bp}_intensity.tif"; Writers.WriteTiffF32(f, r.Intensity, M, M); Add(f); }
        }
        if (o.Png && pngHook != null) { var f = $"{bp}_preview.png"; pngHook(f, r); Add(f); }

        var side = new
        {
            software = "QwlsiApp", date = DateTime.Now.ToString("s"), source_frame = oc.File, mode = oc.Mode, reference_file = oc.ReferenceFile, system_wavefront_file = oc.SystemWavefrontFile,
            lambda_m = par.Lambda, pitch_m = par.PitchM, parameters = par, carriers_fa = oc.CarriersUsed.Fa, carriers_fb = oc.CarriersUsed.Fb, calibration_source = oc.CarriersUsed.Source,
            grid = new { rows = M, cols = M, orientation = "row = image row (y down), column = x" },
            units = new { matrix_txt = un, npy = "m (OPD)", mat = "m (OPD_m)", tiff32 = un }, delimiter = mo.Delim.ToString(), decimals = mo.Decimals, outside_pupil = mo.OutsideAsZero ? "0" : "NaN",
            stats = r.Stats, solver = new { cg_iterations = r.Solve.CgIterations, residual_rad = r.Solve.ResidualRad }, seconds = oc.Seconds, peak_working_set_mb = oc.PeakWorkingSetMb,
            zernike_removal = zr == null ? null : new
            {
                convention = zr.Fit.Convention, fit_order = zr.Fit.Order, fit = "joint masked least squares over the mask-fitted circle (centroid, radius = sqrt(area/pi)), pixels with rho<=1",
                circle = new { cx_px = zr.Fit.Cx, cy_px = zr.Fit.Cy, radius_px = zr.Fit.RadiusPx, radius_mm = zr.Fit.RadiusM * 1e3 },
                removed = Enumerable.Range(0, zr.Removed.Length).Where(j => zr.Removed[j]).Select(j => new { ansi_j = j, noll_j = zr.Fit.Terms[j].Noll, n = zr.Fit.Terms[j].N, m = zr.Fit.Terms[j].M, name = zr.Fit.Terms[j].Name, coefficient_nm = zr.Fit.Coeff[j] * 1e9, diopters = double.IsNaN(zr.Fit.Diopters(j)) ? (double?)null : zr.Fit.Diopters(j) }).ToArray(),
                rms_before_nm = zr.RmsBeforeM * 1e9, rms_after_nm = zr.RmsAfterM * 1e9, pv_before_um = zr.PvBeforeM * 1e6, pv_after_um = zr.PvAfterM * 1e6,
                warnings = ZernikeTools.Warnings(zr.Removed),
            },
            export_options = o, files = files.Select(Path.GetFileName).ToArray(),
        };
        var sf = bp + "_export.json"; File.WriteAllText(sf, JsonSerializer.Serialize(side, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); files.Add(sf);
        return files;
    }

    static List<string> HeaderLines(ReconOutcome oc, QwlsiParams par, MatrixOptions mo) => new()
    {
        $"QwlsiApp export {DateTime.Now:s}", $"source: {Path.GetFileName(oc.File)}", $"mode: {oc.Mode}", $"lambda_m: {par.Lambda:G}", $"pitch_m: {par.PitchM:G}",
        $"calibration: fa=[{oc.CarriersUsed.Fa[0]:G},{oc.CarriersUsed.Fa[1]:G}] fb=[{oc.CarriersUsed.Fb[0]:G},{oc.CarriersUsed.Fb[1]:G}] ({oc.CarriersUsed.Source})",
    };
}

static class EnumerableExt { public static List<string> Append(this List<string> l, string s) { var c = new List<string>(l) { s }; return c; } }
