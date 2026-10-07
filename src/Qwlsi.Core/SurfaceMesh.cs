namespace Qwlsi.Core;

public enum MeshResolution { Full = 0, N512 = 512, N256 = 256 }

/// <summary>Height-field mesh prepared for a 3-D viewer (no GPU / UI dependency).</summary>
public sealed class SurfaceMeshData
{
    public int N;                       // grid is N x N vertices
    public float[] Value = Array.Empty<float>();   // height in nm per vertex, NaN = hole (outside the pupil)
    public int[] Triangles = Array.Empty<int>();   // vertex indices, 3 per triangle, only where all quad corners are valid
    public double CellMm;               // vertex spacing in mm (x and y)
    public double CxIndex, CyIndex;     // pupil centre in vertex index units
    public double MinNm, MaxNm; public long ValidVertices;
    public double ExtentMm => CellMm * N;
    /// <summary>x [mm] to the right, y [mm] UP (image rows flipped), origin at the pupil centroid.</summary>
    public (double x, double y) Position(int ix, int iy) => ((ix - CxIndex) * CellMm, (CyIndex - iy) * CellMm);
}

public static class SurfaceMesh
{
    /// <summary>
    /// Area-weighted block average of an M x M map (NaN outside) onto an n x n grid: each output vertex averages the valid input pixels under
    /// its (fractional) M/n box; a vertex is valid when at least half of the box area is valid. n = M gives the full-resolution mesh.
    /// </summary>
    public static SurfaceMeshData Build(float[] mapNm, int M, double pitchM, MeshResolution res)
    {
        int n = res == MeshResolution.Full ? M : Math.Min((int)res, M); double s = (double)M / n; var v = new float[(long)n * n];
        Parallel.For(0, n, iy =>
        {
            double y0 = iy * s, y1 = y0 + s; int ya = (int)Math.Floor(y0), yb = Math.Min(M - 1, (int)Math.Ceiling(y1) - 1);
            for (int ix = 0; ix < n; ix++)
            {
                double x0 = ix * s, x1 = x0 + s; int xa = (int)Math.Floor(x0), xb = Math.Min(M - 1, (int)Math.Ceiling(x1) - 1);
                double sw = 0, sv = 0, tot = 0;
                for (int y = ya; y <= yb; y++)
                {
                    double wy = Math.Min(y + 1, y1) - Math.Max(y, y0); if (wy <= 0) continue;
                    for (int x = xa; x <= xb; x++)
                    {
                        double wx = Math.Min(x + 1, x1) - Math.Max(x, x0); if (wx <= 0) continue; double w = wx * wy; tot += w;
                        float f = mapNm[(long)y * M + x]; if (float.IsNaN(f)) continue; sw += w; sv += w * f;
                    }
                }
                v[(long)iy * n + ix] = tot > 0 && sw >= 0.5 * tot ? (float)(sv / sw) : float.NaN;
            }
        });
        var d = new SurfaceMeshData { N = n, Value = v, CellMm = s * pitchM * 1e3 };
        double mn = double.MaxValue, mx = double.MinValue, sx = 0, sy = 0; long c = 0;
        for (int iy = 0; iy < n; iy++) for (int ix = 0; ix < n; ix++) { float f = v[(long)iy * n + ix]; if (float.IsNaN(f)) continue; c++; sx += ix; sy += iy; if (f < mn) mn = f; if (f > mx) mx = f; }
        d.ValidVertices = c; d.MinNm = c > 0 ? mn : 0; d.MaxNm = c > 0 ? mx : 0; d.CxIndex = c > 0 ? sx / c : n / 2.0; d.CyIndex = c > 0 ? sy / c : n / 2.0;
        // triangles only where all four corners are valid -> real holes outside the pupil
        var tri = new List<int>((int)Math.Min(c * 2L * 3, int.MaxValue / 2));
        for (int iy = 0; iy < n - 1; iy++) for (int ix = 0; ix < n - 1; ix++)
        {
            int a = iy * n + ix, b = a + 1, cc = a + n, dd = cc + 1;
            if (float.IsNaN(v[a]) || float.IsNaN(v[b]) || float.IsNaN(v[cc]) || float.IsNaN(v[dd])) continue;
            tri.Add(a); tri.Add(cc); tri.Add(b); tri.Add(b); tri.Add(cc); tri.Add(dd);   // counter-clockwise seen from +z with y up (rows run downward)
        }
        d.Triangles = tri.ToArray(); return d;
    }

    /// <summary>Colour range: Auto = min..max, Manual = given, Percentile = lo/hi percentiles of the valid vertices (clipping outliers).</summary>
    public static (float lo, float hi) ColorRange(SurfaceMeshData d, string mode, double manualLo = 0, double manualHi = 1, double pctLo = 1, double pctHi = 99)
    {
        if (mode == "Manual") return ((float)manualLo, (float)(manualHi > manualLo ? manualHi : manualLo + 1));
        if (mode == "Percentile")
        {
            var a = d.Value.Where(f => !float.IsNaN(f)).ToArray(); if (a.Length == 0) return (0, 1); Array.Sort(a);
            float lo = a[(int)(Math.Clamp(pctLo, 0, 100) / 100 * (a.Length - 1))], hi = a[(int)(Math.Clamp(pctHi, 0, 100) / 100 * (a.Length - 1))]; return hi > lo ? (lo, hi) : (lo, lo + 1);
        }
        return d.MaxNm > d.MinNm ? ((float)d.MinNm, (float)d.MaxNm) : ((float)d.MinNm, (float)d.MinNm + 1);
    }

    /// <summary>"Nice" 1-2-5 tick positions covering [lo, hi] with about `target` ticks.</summary>
    public static double[] NiceTicks(double lo, double hi, int target = 5)
    {
        if (!(hi > lo)) return new[] { lo }; double raw = (hi - lo) / Math.Max(1, target), p = Math.Pow(10, Math.Floor(Math.Log10(raw))), f = raw / p;
        double step = (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * p; var t = new List<double>();
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + step * 1e-9; v += step) t.Add(Math.Abs(v) < step * 1e-9 ? 0 : v); return t.ToArray();
    }

    /// <summary>Default height exaggeration: the OPD range (nm) is drawn as `fraction` of the lateral extent (mm). 1 = true scale (nm-heights are ~1e-6 of mm).</summary>
    public static double AutoExaggeration(SurfaceMeshData d, double fraction = 0.3)
    {
        double rangeMm = (d.MaxNm - d.MinNm) * 1e-6; return rangeMm > 0 ? fraction * d.ExtentMm * 0.8 / rangeMm : 1;
    }
}
