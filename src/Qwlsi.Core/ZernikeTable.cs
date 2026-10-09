using System.Globalization;
using System.Text;

namespace Qwlsi.Core;

/// <summary>Pupil used for the all-frames Zernike fits: each frame's own mask-fitted circle, or ONE circle fitted to the intersection of all masks.</summary>
public enum ZernikePupil { PerFrame, CommonCircle }

/// <summary>
/// One processed frame of the all-frames table. CoeffM = all ANSI terms of the order-10 joint fit in metres; PowerD = defocus dioptres of that fit (the Zernike tab's value);
/// AstigRmsNm = hypot(c3, c5); AstigAxisDeg = axis of MAXIMUM OPD of the astigmatic term c5 cos 2t + c3 sin 2t = A cos(2 (t - t0)): t0 = 0.5 atan2(c3, c5) in degrees in [0, 180),
/// t counter-clockwise from +x with y up and the orientation flags applied. Before/After = rms / PV of the raw / filtered (selected terms removed) map over the valid mask pixels.
/// </summary>
public sealed record ZernikeFrameRow(string Name, int Number, double[] CoeffM, double PowerD, double AstigRmsNm, double AstigAxisDeg, double RmsBeforeNm, double PvBeforeUm, double RmsAfterNm, double PvAfterUm,
    long FitPixels, double CircleCx, double CircleCy, double CircleRadiusPx);

/// <summary>A numeric table column: header, value of a row, and whether it is an angle (axis, mod 180 deg) that needs circular statistics.</summary>
public sealed record ZernikeColumn(string Header, Func<ZernikeFrameRow, double> Get, bool AxisAngle = false);

/// <summary>Per-frame Zernike results for all processed frames, with mean / std summary rows. Build with <see cref="ZernikeTableBuilder"/>.</summary>
public sealed class ZernikeTable
{
    public List<ZernikeFrameRow> Rows { get; } = new();
    public bool[] Selection { get; init; } = Array.Empty<bool>();
    public ZernikeOrientation Orient { get; init; } = new();
    public ZernikePupil Pupil { get; init; }
    public ZernikeCircle? CommonCircle { get; init; }
    public long CommonCirclePixels { get; init; }
    public int Order { get; init; } = 10;
    public double PitchM { get; init; }
    public double[] Mean { get; private set; } = Array.Empty<double>();
    public double[] Std { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<ZernikeColumn> Columns { get; private set; } = Array.Empty<ZernikeColumn>();

    public string Convention => new ZernikeFit { Orient = Orient }.Convention;

    /// <summary>Columns in CSV order: the selected terms (ANSI order, nm), power_D, astig_rms_nm, astig_axis_deg, rms_before_nm, pv_before_um, rms_after_nm, pv_after_um.</summary>
    public static List<ZernikeColumn> MakeColumns(bool[] selection, int order = 10)
    {
        var c = new List<ZernikeColumn>(); int K = ZernikeBasis.Count(order);
        for (int j = 0; j < K && j < selection.Length; j++) if (selection[j]) { int jj = j; var t = ZernikeBasis.Term(j); c.Add(new ZernikeColumn($"Z{j} {t.Name} [nm]", r => r.CoeffM[jj] * 1e9)); }
        c.Add(new ZernikeColumn("power_D", r => r.PowerD)); c.Add(new ZernikeColumn("astig_rms_nm", r => r.AstigRmsNm)); c.Add(new ZernikeColumn("astig_axis_deg", r => r.AstigAxisDeg, true));
        c.Add(new ZernikeColumn("rms_before_nm", r => r.RmsBeforeNm)); c.Add(new ZernikeColumn("pv_before_um", r => r.PvBeforeUm)); c.Add(new ZernikeColumn("rms_after_nm", r => r.RmsAfterNm)); c.Add(new ZernikeColumn("pv_after_um", r => r.PvAfterUm));
        return c;
    }

    /// <summary>Axis statistics: mean = half the mean direction of the doubled angles, std = half the circular std sqrt(-2 ln R) of the doubled angle (capped at 90 deg), both in degrees.</summary>
    public static (double mean, double std) AxisStats(IReadOnlyList<double> axesDeg)
    {
        if (axesDeg.Count == 0) return (double.NaN, double.NaN);
        if (axesDeg.Count == 1) return (axesDeg[0], 0);
        double cs = 0, sn = 0; foreach (var a in axesDeg) { double p = 2 * a * Math.PI / 180; cs += Math.Cos(p); sn += Math.Sin(p); }
        cs /= axesDeg.Count; sn /= axesDeg.Count; double mean = 0.5 * Math.Atan2(sn, cs) * 180 / Math.PI; if (mean < 0) mean += 180; if (mean >= 180) mean -= 180;
        double R = Math.Min(1.0, Math.Sqrt(cs * cs + sn * sn)), std = R <= 1e-12 ? 90 : Math.Min(90, 0.5 * Math.Sqrt(Math.Max(0, -2 * Math.Log(R))) * 180 / Math.PI);
        return (mean, std);
    }

    /// <summary>(Re)computes Columns, Mean and Std from Rows (sample std n-1, 0 for one row; the axis uses circular statistics).</summary>
    public void ComputeSummary()
    {
        Columns = MakeColumns(Selection, Order); int n = Rows.Count; Mean = new double[Columns.Count]; Std = new double[Columns.Count];
        for (int k = 0; k < Columns.Count; k++)
        {
            var v = Rows.Select(Columns[k].Get).ToList(); if (n == 0) { Mean[k] = Std[k] = double.NaN; continue; }
            if (Columns[k].AxisAngle) { (Mean[k], Std[k]) = AxisStats(v); continue; }
            double m = v.Average(); Mean[k] = m; Std[k] = n > 1 ? Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (n - 1)) : 0;
        }
    }

    /// <summary>The removal of ONE frame with this table's pupil (per-frame or common circle), orientation and selection (for the clicked-row view and the export).</summary>
    public ZernikeRemoval MakeRemoval(ReconOutcome oc, double pitchM)
    {
        var r = oc.Result; var fit = ZernikeTools.Fit(r.Opd, r.Mask, r.M, pitchM, Order, Orient, Pupil == ZernikePupil.CommonCircle ? CommonCircle : null);
        return ZernikeRemoval.Make(r.Opd, r.Mask, fit, Selection);
    }
}

/// <summary>One frame source of the table / export: a name, a loader of the full result and optionally a cheap loader of the mask only.</summary>
public static class ZernikeTableBuilder
{
    /// <summary>
    /// Fits every frame (ONE result in memory at a time: load -> fit -> row -> drop) with the order-10 joint masked least squares, removes the selected terms and collects the row.
    /// CommonCircle: a first pass over the masks (loadMask, else the full result) fits ONE circle to the intersection of all masks; every frame is then fitted over that circle
    /// (pixels of the frame's own mask with rho &lt;= 1). Cancellation is checked between frames.
    /// </summary>
    public static ZernikeTable Build(IReadOnlyList<(string name, Func<CancellationToken, ReconOutcome> load, Func<CancellationToken, byte[]?>? loadMask)> frames, bool[] selection, ZernikeOrientation orient,
        ZernikePupil pupil, double pitchM, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default, int order = 10)
    {
        ZernikeCircle? circle = null; long inter = 0;
        if (pupil == ZernikePupil.CommonCircle)
        {
            IEnumerable<byte[]> Masks() { foreach (var f in frames) { ct.ThrowIfCancellationRequested(); yield return f.loadMask?.Invoke(ct) ?? f.load(ct).Result.Mask; } }
            (circle, inter) = ZernikeTools.CommonCircle(Masks());
        }
        var t = new ZernikeTable { Selection = (bool[])selection.Clone(), Orient = new ZernikeOrientation { FlipX = orient.FlipX, FlipY = orient.FlipY }, Pupil = pupil, CommonCircle = circle, CommonCirclePixels = inter, Order = order, PitchM = pitchM };
        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var oc = frames[i].load(ct);
            t.Rows.Add(MakeRow(frames[i].name, i + 1, oc, pitchM, t.Selection, t.Orient, circle, order)); oc = null!;
            progress?.Report((i + 1, frames.Count));
        }
        t.ComputeSummary(); return t;
    }

    public static ZernikeFrameRow MakeRow(string name, int number, ReconOutcome oc, double pitchM, bool[] selection, ZernikeOrientation orient, ZernikeCircle? circle, int order = 10)
    {
        var r = oc.Result; var fit = ZernikeTools.Fit(r.Opd, r.Mask, r.M, pitchM, order, orient, circle); var zr = ZernikeRemoval.Make(r.Opd, r.Mask, fit, selection); var c = fit.Coeff;
        double axis = 0.5 * Math.Atan2(c[3], c[5]) * 180 / Math.PI; if (axis < 0) axis += 180; if (axis >= 180) axis -= 180;
        return new ZernikeFrameRow(name, number, (double[])c.Clone(), fit.Diopters(4), Math.Sqrt(c[3] * c[3] + c[5] * c[5]) * 1e9, axis, zr.RmsBeforeM * 1e9, zr.PvBeforeM * 1e6, zr.RmsAfterM * 1e9, zr.PvAfterM * 1e6, fit.FitPixels, fit.Cx, fit.Cy, fit.RadiusPx);
    }
}

public static class ZernikeTableCsv
{
    static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>CSV with a '# ' comment header, one row per frame and (optionally) "mean" / "std" rows. Invariant culture.</summary>
    public static string Write(string path, ZernikeTable t, bool includeSummary = true)
    {
        var inv = CultureInfo.InvariantCulture; var sb = new StringBuilder(); int n = t.Rows.Count;
        sb.AppendLine($"# QwlsiApp Zernike all-frames table (OSA/ANSI, order {t.Order}, joint masked least squares per frame)");
        sb.AppendLine($"# frames: {n}");
        sb.AppendLine("# convention: " + t.Convention);
        sb.AppendLine("# pupil: " + (t.Pupil == ZernikePupil.CommonCircle && t.CommonCircle != null
            ? $"common circle fitted to the intersection of all masks ({t.CommonCirclePixels} px): centre ({t.CommonCircle.Cx.ToString("F3", inv)}, {t.CommonCircle.Cy.ToString("F3", inv)}) px, radius {t.CommonCircle.RadiusPx.ToString("F3", inv)} px = {(t.CommonCircle.RadiusPx * t.PitchM * 1e3).ToString("F4", inv)} mm"
            : "per-frame circle (mask centroid, radius sqrt(area/pi) of each frame's own mask)"));
        var removed = Enumerable.Range(0, Math.Min(t.Selection.Length, ZernikeBasis.Count(t.Order))).Where(j => t.Selection[j]).Select(j => $"j={j} {ZernikeBasis.Term(j).Name}").ToList();
        sb.AppendLine("# removed terms (columns Z<j>, in nm; rms/pv 'after' columns are after removing them): " + (removed.Count == 0 ? "none" : string.Join("; ", removed)));
        sb.AppendLine("# units: power_D dioptres (defocus of the order-10 fit, + = concave); astig_rms_nm = hypot(c3,c5); rms/pv before/after in nm / um over the valid mask pixels");
        sb.AppendLine("# astig_axis_deg: axis of maximum OPD of the astigmatic term, 0.5*atan2(c3,c5), degrees ccw from +x (y up, orientation flags applied), in [0,180)");
        sb.AppendLine("# summary rows: mean and sample standard deviation (n-1; 0 for one frame); for astig_axis_deg circular statistics of the doubled angle (mean = half the mean direction, std = half sqrt(-2 ln R), capped at 90)");
        sb.AppendLine(string.Join(",", new[] { "frame", "number" }.Concat(t.Columns.Select(c => c.Header.StartsWith("Z") && c.Header.Contains('[') ? Q(c.Header) : c.Header)).Concat(new[] { "fit_pixels", "circle_cx_px", "circle_cy_px", "circle_radius_px" })));
        string Fmt(ZernikeColumn c, double v) => double.IsNaN(v) ? "" : v.ToString(c.Header.StartsWith("power") ? "F5" : c.Header.StartsWith("pv_") ? "F5" : c.Header == "astig_axis_deg" ? "F3" : "F4", inv);
        foreach (var r in t.Rows)
            sb.AppendLine(string.Join(",", new[] { Q(r.Name), r.Number.ToString(inv) }.Concat(t.Columns.Select(c => Fmt(c, c.Get(r)))).Concat(new[] { r.FitPixels.ToString(inv), r.CircleCx.ToString("F3", inv), r.CircleCy.ToString("F3", inv), r.CircleRadiusPx.ToString("F3", inv) })));
        if (includeSummary && n > 0)
            foreach (var (label, v) in new[] { ("mean", t.Mean), ("std", t.Std) })
                sb.AppendLine(string.Join(",", new[] { Q(label), "" }.Concat(t.Columns.Select((c, k) => Fmt(c, v[k]))).Concat(new[] { "", "", "", "" })));
        File.WriteAllText(path, sb.ToString()); return path;
    }
}

/// <summary>"Export all filtered maps": the Zernike-filtered OPD of every frame with the table's selection and pupil, with the existing matrix / npy / mat ... export options, plus zernike_table.csv.</summary>
public static class ZernikeBatchExport
{
    public static List<string> Run(IReadOnlyList<(string name, Func<CancellationToken, ReconOutcome> load, Func<CancellationToken, byte[]?>? loadMask)> frames, ZernikeTable table, QwlsiParams par, ExportOptions options,
        string folder, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder); var files = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var oc = frames[i].load(ct); var zr = table.MakeRemoval(oc, par.PitchM);
            files.AddRange(ResultExporter.Export(oc, par, options, folder, null, null, zr)); oc = null!; zr = null!; progress?.Report((i + 1, frames.Count));
        }
        files.Add(ZernikeTableCsv.Write(Path.Combine(folder, "zernike_table.csv"), table)); return files;
    }
}
