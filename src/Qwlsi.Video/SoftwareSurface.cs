using Qwlsi.Core;

namespace Qwlsi.Video;

/// <summary>View of a height-field mesh. Heights are the mesh values (nm); world z = (nm - ZCenterNm) * 1e-6 * Exaggeration [mm] (x, y in mm, y up, origin = pupil centroid).</summary>
public sealed class SurfaceView
{
    public double AzimuthDeg = -45, ElevationDeg = 30;
    public double Exaggeration = 1;
    /// <summary>Radius [mm] of the sphere that must fit the panel (0 = computed from the mesh). Fixed for a whole video so the scale does not jump between frames / azimuths.</summary>
    public double FitRadiusMm;
    public double ZCenterNm;
    public ColormapKind Cmap = ColormapKind.Viridis;
    /// <summary>Colour range in mesh units (nm).</summary>
    public float LoNm, HiNm = 1;
    public bool Shading = true;
    public uint Background = 0xFF101010;
}

/// <summary>Orthographic projection used by the rasteriser, to draw the axes box over the result (pixel coordinates of the final image).</summary>
public sealed class SurfaceCamera
{
    public double Cx, Cy, Scale; public double RX, RY, UX, UY, UZ, CX, CY, CZ;
    public double XMin, XMax, YMin, YMax, ZMin, ZMax;      // box of the valid vertices [mm]
    public double ExaggerationUsed, ZCenterNm;
    public (double x, double y) Project(double x, double y, double z) => (Cx + Scale * (x * RX + y * RY), Cy - Scale * (x * UX + y * UY + z * UZ));
}

/// <summary>
/// Deterministic software rasteriser of the SurfaceMesh height field: orthographic camera (azimuth about the vertical axis, elevation), z-buffer, Gouraud colours from the colormap x Lambert shading
/// (per-vertex normals from the exaggerated heights), NaN vertices = holes (a quad is drawn only if all four corners are valid), 2x2 supersampling. Output is top-down BGRA (0xAARRGGBB).
/// </summary>
public static class SoftwareSurface
{
    public static uint[] Render(SurfaceMeshData mesh, SurfaceView v, int width, int height, out SurfaceCamera cam, int supersample = 2)
    {
        int S = Math.Max(1, supersample), W = width * S, H = height * S, n = mesh.N; var vals = mesh.Value;
        double az = ((v.AzimuthDeg % 360) + 360) % 360 * Math.PI / 180, el = Math.Clamp(v.ElevationDeg, -90, 90) * Math.PI / 180;
        double ca = Math.Cos(az), sa = Math.Sin(az), ce = Math.Cos(el), se = Math.Sin(el);
        double rx = -sa, ry = ca, ux = -se * ca, uy = -se * sa, uz = ce, cx = ce * ca, cy = ce * sa, cz = se;      // right, up, towards-camera
        double k = v.Exaggeration * 1e-6, cell = mesh.CellMm;
        // vertex world positions + valid box
        var wx = new float[(long)n * n]; var wy = new float[(long)n * n]; var wz = new float[(long)n * n];
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue, rmax2 = 0;
        for (int iy = 0; iy < n; iy++) for (int ix = 0; ix < n; ix++)
        {
            long i = (long)iy * n + ix; float f = vals[i]; if (float.IsNaN(f)) continue;
            var (x, y) = mesh.Position(ix, iy); double z = (f - v.ZCenterNm) * k; wx[i] = (float)x; wy[i] = (float)y; wz[i] = (float)z;
            if (x < xmin) xmin = x; if (x > xmax) xmax = x; if (y < ymin) ymin = y; if (y > ymax) ymax = y; if (z < zmin) zmin = z; if (z > zmax) zmax = z; rmax2 = Math.Max(rmax2, x * x + y * y);
        }
        if (xmin > xmax) { xmin = xmax = ymin = ymax = zmin = zmax = 0; }
        double zhalf = Math.Max(Math.Abs(zmin), Math.Abs(zmax)), R = v.FitRadiusMm > 0 ? v.FitRadiusMm : Math.Max(1e-6, Math.Sqrt(rmax2 + zhalf * zhalf));
        double scale = Math.Min(W, H) / (2 * R) * 0.92;
        cam = new SurfaceCamera { Cx = width / 2.0, Cy = height / 2.0, Scale = scale / S, RX = rx, RY = ry, UX = ux, UY = uy, UZ = uz, CX = cx, CY = cy, CZ = cz, XMin = xmin, XMax = xmax, YMin = ymin, YMax = ymax, ZMin = zmin, ZMax = zmax, ExaggerationUsed = v.Exaggeration, ZCenterNm = v.ZCenterNm };
        // screen positions, depth, colour (with shading)
        var sx = new float[(long)n * n]; var sy = new float[(long)n * n]; var sd = new float[(long)n * n]; var cr = new float[(long)n * n]; var cg = new float[(long)n * n]; var cb = new float[(long)n * n];
        var lut = Colormap.Lut(v.Cmap); float inv = v.HiNm > v.LoNm ? 1f / (v.HiNm - v.LoNm) : 0f;
        double lx = -0.30 * rx + 0.50 * ux + 0.80 * cx, ly = -0.30 * ry + 0.50 * uy + 0.80 * cy, lz = 0.50 * uz + 0.80 * cz; double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
        Parallel.For(0, n, iy =>
        {
            for (int ix = 0; ix < n; ix++)
            {
                long i = (long)iy * n + ix; float f = vals[i]; if (float.IsNaN(f)) continue;
                double x = wx[i], y = wy[i], z = wz[i];
                sx[i] = (float)(W / 2.0 + scale * (x * rx + y * ry)); sy[i] = (float)(H / 2.0 - scale * (x * ux + y * uy + z * uz)); sd[i] = (float)(x * cx + y * cy + z * cz);
                uint c = lut[Math.Clamp((int)((f - v.LoNm) * inv * 255f + 0.5f), 0, 255)]; float r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                if (v.Shading)
                {
                    // central differences of the exaggerated height (one-sided at holes / borders); y_world = -row
                    double dzdx = Diff(vals, wz, n, ix, iy, 1, 0, cell), dzdy = -Diff(vals, wz, n, ix, iy, 0, 1, cell);
                    double nx = -dzdx, ny = -dzdy, nz = 1, nl = Math.Sqrt(nx * nx + ny * ny + 1); nx /= nl; ny /= nl; nz /= nl;
                    if (nx * cx + ny * cy + nz * cz < 0) { nx = -nx; ny = -ny; nz = -nz; }
                    double sh = 0.30 + 0.70 * Math.Max(0, nx * lx + ny * ly + nz * lz); r = (float)(r * sh); g = (float)(g * sh); b = (float)(b * sh);
                }
                cr[i] = r; cg[i] = g; cb[i] = b;
            }
        });
        // rasterise (bands in parallel, every band scans all quads: no write races)
        var px = new uint[(long)W * H]; var zb = new float[(long)W * H]; Array.Fill(px, v.Background); Array.Fill(zb, float.NegativeInfinity);
        int bands = Math.Min(64, Math.Max(1, H / 8)); int bh = (H + bands - 1) / bands;
        Parallel.For(0, bands, bnd =>
        {
            int y0 = bnd * bh, y1 = Math.Min(H, y0 + bh);
            for (int iy = 0; iy < n - 1; iy++)
            {
                for (int ix = 0; ix < n - 1; ix++)
                {
                    long a = (long)iy * n + ix, b = a + 1, c = a + n, d = c + 1;
                    if (float.IsNaN(vals[a]) || float.IsNaN(vals[b]) || float.IsNaN(vals[c]) || float.IsNaN(vals[d])) continue;
                    float ymin2 = MathF.Min(MathF.Min(sy[a], sy[b]), MathF.Min(sy[c], sy[d])), ymax2 = MathF.Max(MathF.Max(sy[a], sy[b]), MathF.Max(sy[c], sy[d]));
                    if (ymax2 < y0 || ymin2 >= y1) continue;
                    Tri(a, c, b, sx, sy, sd, cr, cg, cb, px, zb, W, y0, y1); Tri(b, c, d, sx, sy, sd, cr, cg, cb, px, zb, W, y0, y1);
                }
            }
        });
        if (S == 1) return px;
        var o = new uint[(long)width * height]; int ss = S * S;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int yy = 0; yy < S; yy++) { long bs = (long)(y * S + yy) * W + x * S; for (int xx = 0; xx < S; xx++) { uint c = px[bs + xx]; r += (int)((c >> 16) & 255); g += (int)((c >> 8) & 255); b += (int)(c & 255); } }
                o[(long)y * width + x] = 0xFF000000u | (uint)((r + ss / 2) / ss) << 16 | (uint)((g + ss / 2) / ss) << 8 | (uint)((b + ss / 2) / ss);
            }
        });
        return o;
    }

    static double Diff(float[] vals, float[] wz, int n, int ix, int iy, int dx, int dy, double cell)
    {
        long i = (long)iy * n + ix; int xa = ix - dx, ya = iy - dy, xb = ix + dx, yb = iy + dy;
        bool ha = xa >= 0 && ya >= 0 && !float.IsNaN(vals[(long)ya * n + xa]), hb = xb < n && yb < n && !float.IsNaN(vals[(long)yb * n + xb]);
        double za = ha ? wz[(long)ya * n + xa] : wz[i], zb = hb ? wz[(long)yb * n + xb] : wz[i]; double span = (ha ? 1 : 0) + (hb ? 1 : 0);
        return span == 0 ? 0 : (zb - za) / (span * cell);
    }

    static void Tri(long a, long b, long c, float[] sx, float[] sy, float[] sd, float[] cr, float[] cg, float[] cb, uint[] px, float[] zb, int W, int y0, int y1)
    {
        float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], cx = sx[c], cy = sy[c];
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax); if (area == 0) return;
        float minY = MathF.Min(ay, MathF.Min(by, cy)), maxY = MathF.Max(ay, MathF.Max(by, cy)), minX = MathF.Min(ax, MathF.Min(bx, cx)), maxX = MathF.Max(ax, MathF.Max(bx, cx));
        int ya = Math.Max(y0, (int)MathF.Floor(minY - 0.5f)), yb = Math.Min(y1 - 1, (int)MathF.Ceiling(maxY - 0.5f)), xa = Math.Max(0, (int)MathF.Floor(minX - 0.5f)), xb = Math.Min(W - 1, (int)MathF.Ceiling(maxX - 0.5f));
        if (ya > yb || xa > xb) return; float ia = 1f / area; float ad = sd[a], bd = sd[b], cd = sd[c];
        for (int y = ya; y <= yb; y++)
        {
            float py = y + 0.5f;
            for (int x = xa; x <= xb; x++)
            {
                float pxx = x + 0.5f;
                float w0 = ((bx - pxx) * (cy - py) - (by - py) * (cx - pxx)) * ia, w1 = ((cx - pxx) * (ay - py) - (cy - py) * (ax - pxx)) * ia, w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float d = w0 * ad + w1 * bd + w2 * cd; long o = (long)y * W + x; if (d <= zb[o]) continue; zb[o] = d;
                float r = w0 * cr[a] + w1 * cr[b] + w2 * cr[c], g = w0 * cg[a] + w1 * cg[b] + w2 * cg[c], bl = w0 * cb[a] + w1 * cb[b] + w2 * cb[c];
                px[o] = 0xFF000000u | (uint)Math.Clamp((int)(r + 0.5f), 0, 255) << 16 | (uint)Math.Clamp((int)(g + 0.5f), 0, 255) << 8 | (uint)Math.Clamp((int)(bl + 0.5f), 0, 255);
            }
        }
    }
}
