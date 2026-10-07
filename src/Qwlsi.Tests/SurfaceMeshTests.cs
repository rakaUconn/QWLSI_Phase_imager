using Qwlsi.Core;

namespace Qwlsi.Tests;

/// <summary>3-D viewer data preparation (no GPU needed): block averaging, holes, triangulation, colour ranges, axis ticks.</summary>
public class SurfaceMeshTests
{
    static float[] Disk(int M, double r, Func<double, double, double> f)
    {
        var a = new float[M * M]; double c = (M - 1) / 2.0;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) a[y * M + x] = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) <= r ? (float)f(x, y) : float.NaN;
        return a;
    }

    [Theory]
    [InlineData(MeshResolution.N512, 512)] [InlineData(MeshResolution.N256, 256)] [InlineData(MeshResolution.Full, 1504)]
    public void Resolution_and_cell_size(MeshResolution res, int n)
    {
        var m = SurfaceMesh.Build(Disk(1504, 500, (x, y) => 7), 1504, 2.74e-6 * 3, res);
        Assert.Equal(n, m.N); Assert.Equal(1504.0 / n * 8.22e-6 * 1e3, m.CellMm, 6);          // vertex spacing in mm
        Assert.Equal(1504 * 8.22e-3, m.ExtentMm, 3);
    }

    [Fact]
    public void Block_average_preserves_constants_and_means_and_leaves_holes_outside_the_pupil()
    {
        var m = SurfaceMesh.Build(Disk(1504, 600, (x, y) => 123.5), 1504, 8.22e-6, MeshResolution.N512);
        foreach (var v in m.Value) if (!float.IsNaN(v)) Assert.Equal(123.5f, v, 3);
        Assert.True(float.IsNaN(m.Value[0])); Assert.True(float.IsNaN(m.Value[^1]));                // corners = holes
        Assert.False(float.IsNaN(m.Value[256 * 512 + 256]));                                         // centre valid
        double expectArea = Math.PI * 600 * 600 / (1504.0 / 512 * 1504.0 / 512); Assert.InRange(m.ValidVertices, expectArea * 0.97, expectArea * 1.03);
        // linear ramp: area-weighted box average of a linear function equals the function at the box centre
        var ramp = new float[1504 * 1504]; for (int y = 0; y < 1504; y++) for (int x = 0; x < 1504; x++) ramp[y * 1504 + x] = x;
        var r = SurfaceMesh.Build(ramp, 1504, 8.22e-6, MeshResolution.N512); double s = 1504.0 / 512;
        for (int ix = 0; ix < 512; ix += 37) Assert.InRange(r.Value[100 * 512 + ix], (ix + 0.5) * s - 0.5 - 0.05, (ix + 0.5) * s - 0.5 + 0.05);   // piecewise-constant pixels: small edge deviation
    }

    [Fact]
    public void Triangles_only_where_all_four_corners_are_valid()
    {
        var m = SurfaceMesh.Build(Disk(1504, 600, (x, y) => 1), 1504, 8.22e-6, MeshResolution.N256); int n = m.N;
        Assert.Equal(0, m.Triangles.Length % 3); Assert.All(m.Triangles, i => Assert.False(float.IsNaN(m.Value[i])));
        for (int t = 0; t < m.Triangles.Length; t += 3) { int a = m.Triangles[t], b = m.Triangles[t + 1], c = m.Triangles[t + 2]; Assert.InRange(Math.Abs(a % n - b % n) + Math.Abs(a / n - b / n), 1, 2); _ = c; }
        long quads = m.Triangles.Length / 6; Assert.InRange(quads, m.ValidVertices * 0.9, m.ValidVertices);   // ~ one quad per interior vertex
        // fully valid map -> (n-1)^2 quads
        var full = SurfaceMesh.Build(Disk(1504, 5000, (x, y) => 1), 1504, 8.22e-6, MeshResolution.N256); Assert.Equal(2L * 255 * 255 * 3, full.Triangles.Length);
    }

    [Fact]
    public void Positions_are_mm_with_y_up_and_origin_at_the_pupil_centre()
    {
        var m = SurfaceMesh.Build(Disk(1504, 500, (x, y) => 0), 1504, 8.22e-6 / 3, MeshResolution.N512);
        var (x0, y0) = m.Position((int)Math.Round(m.CxIndex), (int)Math.Round(m.CyIndex)); Assert.True(Math.Abs(x0) < m.CellMm && Math.Abs(y0) < m.CellMm);
        var (_, yTop) = m.Position(256, 100); var (_, yBot) = m.Position(256, 400); Assert.True(yTop > yBot);   // row 0 is the top of the image -> larger y
    }

    [Fact]
    public void Colour_ranges_auto_manual_percentile()
    {
        var v = new float[100 * 100]; for (int i = 0; i < v.Length; i++) v[i] = i; v[0] = -1e6f; v[1] = 1e6f;      // two outliers
        var m = new SurfaceMeshData { N = 100, Value = v, MinNm = -1e6, MaxNm = 1e6 };
        Assert.Equal((-1e6f, 1e6f), SurfaceMesh.ColorRange(m, "Auto"));
        Assert.Equal((-50f, 75f), SurfaceMesh.ColorRange(m, "Manual", -50, 75));
        var (lo, hi) = SurfaceMesh.ColorRange(m, "Percentile", 0, 0, 1, 99); Assert.InRange(lo, 90, 110); Assert.InRange(hi, 9850, 9950);   // outliers clipped
    }

    [Fact]
    public void Nice_ticks_and_auto_exaggeration()
    {
        Assert.Equal(new[] { -4.0, -2.0, 0.0, 2.0, 4.0 }, SurfaceMesh.NiceTicks(-4.2, 4.2, 5));
        var t = SurfaceMesh.NiceTicks(-3423, 3961, 5); Assert.Equal(-3000, t[0]); Assert.Equal(3000, t[^1]); Assert.Contains(0.0, t);
        var m = new SurfaceMeshData { N = 512, CellMm = 0.0241, MinNm = -3400, MaxNm = 3900 };                       // extent 12.3 mm, range 7.3 um
        double e = SurfaceMesh.AutoExaggeration(m); Assert.InRange(e * (m.MaxNm - m.MinNm) * 1e-6 / m.ExtentMm, 0.2, 0.3);   // OPD drawn as ~24 % of the lateral extent
    }
}
