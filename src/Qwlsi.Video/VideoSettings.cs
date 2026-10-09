using Qwlsi.Core;

namespace Qwlsi.Video;

public enum PanelKind { Opd, FilteredOpd, RemovedZernike, Intensity, WrappedPhase, RawFrame, Surface3D }
/// <summary>Fixed = one range per panel over ALL frames (default); AutoPerFrame = min..max of the frame; PercentileClipped = per-frame lo/hi percentiles.</summary>
public enum RangeMode { FixedAcrossFrames, AutoPerFrame, PercentileClipped }
public enum ResolutionPreset { P720, P1080, NativeCropped }
public enum FrameOrder { ByName, ByTime }
public enum StripValue { PowerD, RmsNm, PvUm, AstigNm, ZernikeTerm }

/// <summary>One panel; Cmap null = default of its kind (OPD-like and 3-D: the global colormap, intensity and raw frame: gray, wrapped phase: twilight).</summary>
public sealed class PanelSpec
{
    public PanelKind Kind { get; set; }
    public ColormapKind? Cmap { get; set; }
    public PanelSpec() { }
    public PanelSpec(PanelKind kind, ColormapKind? cmap = null) { Kind = kind; Cmap = cmap; }
    public static bool IsOpdLike(PanelKind k) => k is PanelKind.Opd or PanelKind.FilteredOpd or PanelKind.RemovedZernike or PanelKind.Surface3D;
}

/// <summary>Zernike removal used by the Filtered / Removed panels (and the strip-plot term): selection by ANSI j (order 10), orientation, circle (null = per-frame circle, else a common circle).</summary>
public sealed class VideoZernikeSpec
{
    public bool[] Selection { get; set; } = ZernikeTools.Preset("piston+tilt");
    public ZernikeOrientation Orientation { get; set; } = new();
    public ZernikeCircle? Circle { get; set; }
}

public sealed class SurfaceSettings
{
    public MeshResolution Mesh { get; set; } = MeshResolution.N256;
    /// <summary>null = automatic (SurfaceMesh.AutoExaggeration over the global OPD range, so frames are comparable); otherwise a fixed factor (1 = true scale).</summary>
    public double? Exaggeration { get; set; }
    public double ElevationDeg { get; set; } = 30;
    public double AzimuthDeg { get; set; } = -45;
    public bool Axes { get; set; } = true;
    /// <summary>Show the Zernike-filtered OPD instead of the raw OPD.</summary>
    public bool Filtered { get; set; }
}

/// <summary>
/// Everything the renderer needs. Layout of 1..4 panels: 1 = one panel; 2 = side by side; 3 = the first panel spans the top row, the other two share the bottom row;
/// 4 = 2x2. Output = [header bar if overlays] + panel grid + [strip plot if enabled]; all dimensions are even.
/// </summary>
public sealed class VideoSettings
{
    public List<PanelSpec> Panels { get; set; } = new() { new PanelSpec(PanelKind.Opd) };
    public ColormapKind Cmap { get; set; } = ColormapKind.Viridis;
    public RangeMode Range { get; set; } = RangeMode.FixedAcrossFrames;
    public double PctLo { get; set; } = 1;
    public double PctHi { get; set; } = 99;
    /// <summary>Optional manual range in the DISPLAY unit, applied to the OPD-like panels (OPD, filtered, removed, 3-D) when RangeMode is Fixed.</summary>
    public double? ManualMin { get; set; }
    public double? ManualMax { get; set; }
    /// <summary>Unit of the OPD-like panels: Nm, Um or Waves (waves use the lambda of the provider's parameters).</summary>
    public Units Unit { get; set; } = Units.Nm;
    public bool Colorbar { get; set; } = true;

    public bool OverlayName { get; set; } = true;
    public bool OverlayStats { get; set; } = true;
    public bool StripPlot { get; set; }
    public StripValue Strip { get; set; } = StripValue.PowerD;
    /// <summary>ANSI j of the term for StripValue.ZernikeTerm [nm].</summary>
    public int StripTerm { get; set; } = 4;

    public VideoZernikeSpec Zernike { get; set; } = new();
    public SurfaceSettings Surface { get; set; } = new();

    /// <summary>Turntable: ONE frame (index into the provider) of the 3-D surface rotated 360 degrees about the vertical axis over TurntableSeconds at the fixed Surface.ElevationDeg.</summary>
    public bool Turntable { get; set; }
    public double TurntableSeconds { get; set; } = 10;
    public int TurntableFrame { get; set; }

    public int Fps { get; set; } = 10;
    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.P1080;
    /// <summary>0 = no blending between frames (default); n > 0 inserts n linearly blended frames between consecutive frames.</summary>
    public int BlendSteps { get; set; }

    public int FpsClamped => Math.Clamp(Fps, 1, 60);
    public static int TurntableFrames(double seconds, int fps) => Math.Max(1, (int)Math.Round(seconds * Math.Clamp(fps, 1, 60)));
    /// <summary>Number of output frames for a sequence of `n` source frames (turntable: seconds * fps).</summary>
    public int OutputFrames(int n) => Turntable ? TurntableFrames(TurntableSeconds, Fps) : n + Math.Max(0, n - 1) * Math.Max(0, BlendSteps);
    /// <summary>Azimuth of turntable step k of `count`: start + 360 k / count (the loop closes seamlessly).</summary>
    public double TurntableAzimuth(int k, int count) => Surface.AzimuthDeg + 360.0 * k / Math.Max(1, count);
    public ColormapKind CmapOf(PanelSpec p) => p.Cmap ?? p.Kind switch { PanelKind.Intensity or PanelKind.RawFrame => ColormapKind.Gray, PanelKind.WrappedPhase => ColormapKind.Twilight, _ => Cmap };
}

/// <summary>Source of frames for rendering (implemented by the app over the result cache). Called from the render thread, one frame at a time; a frame may be loaded twice (analysis pass + render pass).</summary>
public interface IFrameProvider
{
    int Count { get; }
    string NameOf(int i);
    DateTime TimeOf(int i);
    /// <summary>The processed result of frame i (RAM / disk cache / recompute: may be slow).</summary>
    ReconOutcome Load(int i, CancellationToken ct);
    /// <summary>Raw TIFF box-averaged by Dec to the MxM result grid (see <see cref="RawFrameLoader"/>); null if unavailable.</summary>
    float[]? LoadRawDownsampled(int i);
    QwlsiParams Par { get; }
}

/// <summary>Frame ordering / range helpers (the caller owns the frame list).</summary>
public static class FrameList
{
    /// <summary>Natural, case-insensitive name comparison ("f2" &lt; "f10").</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j; while (i < a.Length && char.IsDigit(a[i])) i++; while (j < b.Length && char.IsDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0'); if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                int c = string.CompareOrdinal(x, y); if (c != 0) return c;
            }
            else { int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j])); if (c != 0) return c; i++; j++; }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static List<T> Order<T>(IEnumerable<T> items, Func<T, string> name, Func<T, DateTime> time, FrameOrder order)
    {
        var l = items.ToList();
        var cmp = order == FrameOrder.ByName ? Comparer<T>.Create((a, b) => NaturalCompare(name(a), name(b))) : Comparer<T>.Create((a, b) => { int c = time(a).CompareTo(time(b)); return c != 0 ? c : NaturalCompare(name(a), name(b)); });
        // stable sort
        return l.Select((v, i) => (v, i)).OrderBy(t => t.v, cmp).ThenBy(t => t.i).Select(t => t.v).ToList();
    }

    /// <summary>1-based inclusive range [first..last] (clamped; last &lt;= 0 = to the end).</summary>
    public static List<T> Range<T>(IReadOnlyList<T> items, int first, int last)
    {
        int a = Math.Clamp(first, 1, Math.Max(1, items.Count)), b = last <= 0 ? items.Count : Math.Clamp(last, 1, items.Count);
        var r = new List<T>(); for (int i = a; i <= b; i++) r.Add(items[i - 1]); return r;
    }
}

/// <summary>Reads a raw frame and box-averages it by `dec` to the result grid (frees the 81 MB frame immediately).</summary>
public static class RawFrameLoader
{
    public static float[] LoadDownsampled(string path, int dec)
    {
        var raw = TiffIO.ReadFrame(path, out int w, out int h); int M = w / dec, Mh = h / dec; var o = new float[(long)M * Mh]; float inv = 1f / (dec * dec);
        Parallel.For(0, Mh, y => { for (int x = 0; x < M; x++) { float s = 0; for (int yy = 0; yy < dec; yy++) { long b = (long)(y * dec + yy) * w + x * dec; for (int xx = 0; xx < dec; xx++) s += raw[b + xx]; } o[(long)y * M + x] = s * inv; } });
        return o;
    }
}
