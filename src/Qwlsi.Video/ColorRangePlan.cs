using Qwlsi.Core;

namespace Qwlsi.Video;

/// <summary>Quantity of a panel for one frame in DISPLAY units (full MxM map, NaN outside the pupil), its unit label and title.</summary>
internal static class PanelData
{
    public static string UnitLabel(Units u) => u switch { Units.Nm => "nm", Units.Um => "µm", Units.Waves => "waves", _ => "m" };

    public static float[] Map(FrameContext fc, PanelSpec p, VideoSettings s, out string unit, out string title)
    {
        double sc = Writers.UnitScale(s.Unit, fc.Provider.Par.Lambda); string ou = UnitLabel(s.Unit); var r = fc.Oc.Result; long n = r.Opd.LongLength; float[] o;
        switch (p.Kind)
        {
            case PanelKind.Opd: unit = ou; title = "OPD"; return Scaled(r.Opd, sc);
            case PanelKind.FilteredOpd: unit = ou; title = "Filtered OPD"; return Scaled(fc.Filtered, sc);
            case PanelKind.RemovedZernike: unit = ou; title = "Removed Zernike surface"; return Scaled(fc.Removed, sc);
            case PanelKind.Surface3D: unit = ou; title = "OPD surface"; return Scaled(s.Surface.Filtered ? fc.Filtered : r.Opd, sc);
            case PanelKind.Intensity:
                o = new float[n]; for (long i = 0; i < n; i++) o[i] = r.Mask[i] != 0 ? r.Intensity[i] : float.NaN; unit = "relative"; title = "Intensity / transmittance"; return o;
            case PanelKind.WrappedPhase:
                { double lam = fc.Provider.Par.Lambda; o = new float[n]; for (long i = 0; i < n; i++) { if (float.IsNaN(r.Opd[i])) { o[i] = float.NaN; continue; } double ph = 2 * Math.PI * r.Opd[i] / lam; o[i] = (float)Math.Atan2(Math.Sin(ph), Math.Cos(ph)); } unit = "rad"; title = "Wrapped phase arg U"; return o; }
            default:
                { var raw = fc.Raw; unit = "counts"; title = "Raw frame"; if (raw == null) return Array.Empty<float>(); return raw; }
        }
    }

    static float[] Scaled(float[] src, double sc) { var o = new float[src.Length]; float f = (float)sc; for (long i = 0; i < o.Length; i++) o[i] = src[i] * f; return o; }

    public static (float lo, float hi) MinMax(float[] a)
    {
        float lo = float.MaxValue, hi = float.MinValue; foreach (var v in a) { if (float.IsNaN(v) || float.IsInfinity(v)) continue; if (v < lo) lo = v; if (v > hi) hi = v; }
        return lo > hi ? (0, 1) : (lo, hi);
    }
}

public sealed record VideoProgress(string Phase, int Done, int Total, TimeSpan Elapsed, TimeSpan Eta, string Message);

/// <summary>
/// Result of the analysis pass over all frames: one colour range per panel (Fixed mode), the global OPD range and exaggeration for the 3-D panels, the pupil crop (union of all masks),
/// and the strip-plot series. Built once per settings change (cancellable); the live preview and the render pass reuse it.
/// </summary>
public sealed class ColorRangePlan
{
    public int FrameCount { get; private set; }
    public int M { get; private set; }
    public double PitchM { get; private set; }
    public double LambdaM { get; private set; }
    /// <summary>Per panel: range in display units (NaN when the panel has no fixed range).</summary>
    public double[] Lo { get; private set; } = Array.Empty<double>();
    public double[] Hi { get; private set; } = Array.Empty<double>();
    /// <summary>Global OPD range [nm] of the 3-D panel source over all analysed frames, centre, and the exaggeration factor used by all 3-D panels.</summary>
    public double GlobalMinNm { get; private set; }
    public double GlobalMaxNm { get; private set; }
    public double Exaggeration { get; private set; } = 1;
    /// <summary>Pupil crop in result-grid pixels (x0, y0 inclusive, x1, y1 inclusive): union of all frames' mask bounding boxes + margin.</summary>
    public int CropX0 { get; private set; }
    public int CropY0 { get; private set; }
    public int CropX1 { get; private set; }
    public int CropY1 { get; private set; }
    public int CropW => CropX1 - CropX0 + 1;
    public int CropH => CropY1 - CropY0 + 1;
    public double PupilRadiusMm { get; private set; }
    public float[] Strip { get; private set; } = Array.Empty<float>();
    public string StripLabel { get; private set; } = "";
    public bool Turntable { get; private set; }

    public static string StripName(VideoSettings s) => s.Strip switch { StripValue.PowerD => "power [D]", StripValue.RmsNm => "rms [nm]", StripValue.PvUm => "PV [µm]", StripValue.AstigNm => "astigmatism [nm rms]", _ => $"Zernike j={s.StripTerm} {(s.StripTerm >= 0 && s.StripTerm < 66 ? ZernikeBasis.Term(s.StripTerm).Name : "")} [nm]" };

    public static ColorRangePlan Build(IFrameProvider p, VideoSettings s, IProgress<VideoProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var plan = new ColorRangePlan { FrameCount = p.Count, LambdaM = p.Par.Lambda, PitchM = p.Par.PitchM, Turntable = s.Turntable };
        int np = s.Panels.Count; var lo = Enumerable.Repeat(double.PositiveInfinity, np).ToArray(); var hi = Enumerable.Repeat(double.NegativeInfinity, np).ToArray();
        var frames = s.Turntable ? new[] { Math.Clamp(s.TurntableFrame, 0, Math.Max(0, p.Count - 1)) } : Enumerable.Range(0, p.Count).ToArray();
        var strip = new float[p.Count]; Array.Fill(strip, float.NaN); int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1; double gmin = double.MaxValue, gmax = double.MinValue; int M = 0;
        for (int k = 0; k < frames.Length; k++)
        {
            ct.ThrowIfCancellationRequested(); int i = frames[k]; var fc = FrameContext.Create(p, i, s, ct); M = fc.M;
            var mk = fc.Mask; int m = fc.M; for (int y = 0; y < m; y++) { long o = (long)y * m; for (int x = 0; x < m; x++) if (mk[o + x] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; } }
            for (int pi = 0; pi < np; pi++)
            {
                var sp = s.Panels[pi]; if (sp.Kind == PanelKind.WrappedPhase) continue;
                if (s.Range == RangeMode.FixedAcrossFrames || sp.Kind == PanelKind.Surface3D)
                {
                    var map = PanelData.Map(fc, sp, s, out _, out _); if (map.Length == 0) continue; var (a, b) = PanelData.MinMax(map);
                    if (sp.Kind == PanelKind.Surface3D) { double sc = Writers.UnitScale(s.Unit, p.Par.Lambda); gmin = Math.Min(gmin, a / sc * 1e9); gmax = Math.Max(gmax, b / sc * 1e9); }
                    if (a < lo[pi]) lo[pi] = a; if (b > hi[pi]) hi[pi] = b;
                }
            }
            if (s.StripPlot && !s.Turntable) strip[i] = StripOf(fc, s);
            progress?.Report(new VideoProgress("Analysing frames", k + 1, frames.Length, sw.Elapsed, Eta(sw, k + 1, frames.Length), $"{fc.Name}"));
        }
        plan.M = M;
        plan.Lo = new double[np]; plan.Hi = new double[np];
        for (int pi = 0; pi < np; pi++)
        {
            var kind = s.Panels[pi].Kind; bool have = lo[pi] <= hi[pi]; plan.Lo[pi] = have && s.Range == RangeMode.FixedAcrossFrames ? lo[pi] : double.NaN; plan.Hi[pi] = have && s.Range == RangeMode.FixedAcrossFrames ? hi[pi] : double.NaN;
            if (kind == PanelKind.WrappedPhase) { plan.Lo[pi] = -Math.PI; plan.Hi[pi] = Math.PI; }
            else if (s.Range == RangeMode.FixedAcrossFrames && PanelSpec.IsOpdLike(kind) && s.ManualMin.HasValue && s.ManualMax.HasValue) { plan.Lo[pi] = s.ManualMin.Value; plan.Hi[pi] = s.ManualMax.Value; }
            if (plan.Hi[pi] <= plan.Lo[pi]) plan.Hi[pi] = plan.Lo[pi] + 1;
        }
        if (x1 < 0) { x0 = y0 = 0; x1 = y1 = Math.Max(0, M - 1); }
        double pupilPx = 0.5 * Math.Max(x1 - x0 + 1, y1 - y0 + 1); plan.PupilRadiusMm = pupilPx * p.Par.PitchM * 1e3 * 1.05;
        const int margin = 20; plan.CropX0 = Math.Max(0, x0 - margin); plan.CropY0 = Math.Max(0, y0 - margin); plan.CropX1 = Math.Min(M - 1, x1 + margin); plan.CropY1 = Math.Min(M - 1, y1 + margin);
        // keep the crop even-sized so native output is even
        if (plan.CropW % 2 != 0) { if (plan.CropX1 < M - 1) plan.CropX1++; else if (plan.CropX0 > 0) plan.CropX0--; }
        if (plan.CropH % 2 != 0) { if (plan.CropY1 < M - 1) plan.CropY1++; else if (plan.CropY0 > 0) plan.CropY0--; }
        plan.GlobalMinNm = gmin <= gmax ? gmin : 0; plan.GlobalMaxNm = gmin <= gmax ? gmax : 1;
        var d = new SurfaceMeshData { N = Math.Max(1, M), CellMm = p.Par.PitchM * 1e3, MinNm = plan.GlobalMinNm, MaxNm = plan.GlobalMaxNm };
        plan.Exaggeration = s.Surface.Exaggeration ?? SurfaceMesh.AutoExaggeration(d);
        plan.Strip = strip; plan.StripLabel = StripName(s);
        return plan;
    }

    static TimeSpan Eta(System.Diagnostics.Stopwatch sw, int done, int total) => done <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / done * (total - done));

    /// <summary>Strip value of the RAW OPD of a frame: app report values, or an order-10 Zernike coefficient [nm] with the video's orientation / circle.</summary>
    static float StripOf(FrameContext fc, VideoSettings s)
    {
        var st = fc.Oc.Result.Stats;
        switch (s.Strip)
        {
            case StripValue.PowerD: return (float)st.PowerD;
            case StripValue.RmsNm: return (float)st.RmsNm;
            case StripValue.PvUm: return (float)st.PvUm;
            case StripValue.AstigNm: return (float)st.AstigRmsNm;
            default:
                { var f = ZernikeTools.Fit(fc.Opd, fc.Mask, fc.M, fc.PitchM, 10, s.Zernike.Orientation, s.Zernike.Circle); int j = Math.Clamp(s.StripTerm, 0, f.Coeff.Length - 1); return (float)(f.Coeff[j] * 1e9); }
        }
    }
}
