using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Qwlsi.Core;

namespace Qwlsi.Video;

/// <summary>One rendered output frame: top-down BGRA plus what was used (ranges / units per panel) so callers and tests can check it.</summary>
public sealed class RenderedFrame
{
    public byte[] Bgra = Array.Empty<byte>(); public int Width, Height;
    public double[] RangeLo = Array.Empty<double>(), RangeHi = Array.Empty<double>(); public string[] Units = Array.Empty<string>(); public string[] Titles = Array.Empty<string>();
    public string HeaderLeft = "", HeaderRight = ""; public double AzimuthDeg;
}

/// <summary>Output geometry: all rectangles in output pixels (Panels = outer panel rectangles with title and colour bar; Images = where the picture goes).</summary>
public sealed class LayoutInfo
{
    public int Width, Height; public Rectangle Header, Strip, StripPlot; public Rectangle[] Panels = Array.Empty<Rectangle>(), Images = Array.Empty<Rectangle>(); public float Scale = 1;
}

public static class VideoComposer
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>Colour of the moving marker in the strip plot (drawn without anti-aliasing).</summary>
    public static readonly Color MarkerColor = Color.FromArgb(255, 255, 64, 64);
    static int Even(int v) => (v + 1) & ~1;

    // ------------------------------------------------------------------------------------------------ layout
    public static LayoutInfo Layout(VideoSettings s, ColorRangePlan plan)
    {
        int np = Math.Clamp(s.Panels.Count, 1, 4); bool hdr = s.OverlayName || s.OverlayStats, strip = s.StripPlot && !s.Turntable;
        int cols = np == 1 ? 1 : 2, rows = np <= 2 ? 1 : 2; int W, H; float u; int titleH, barH, pad, headerH, stripH;
        if (s.Resolution == ResolutionPreset.NativeCropped)
        {
            u = 1; titleH = 24; barH = s.Colorbar ? 46 : 0; pad = 6; headerH = hdr ? 34 : 0; stripH = strip ? 130 : 0;
            int pw = plan.CropW + 2 * pad, ph = titleH + plan.CropH + barH + pad; W = Even(cols * pw); H = Even(headerH + rows * ph + stripH);
        }
        else
        {
            W = s.Resolution == ResolutionPreset.P720 ? 1280 : 1920; H = s.Resolution == ResolutionPreset.P720 ? 720 : 1080; u = H / 1080f;
            titleH = Math.Max(16, (int)Math.Round(26 * u)); barH = s.Colorbar ? Math.Max(34, (int)Math.Round(54 * u)) : 0; pad = Math.Max(4, (int)Math.Round(8 * u)); headerH = hdr ? Even(Math.Max(24, (int)Math.Round(40 * u))) : 0; stripH = strip ? Even((int)Math.Round(170 * u)) : 0;
        }
        var L = new LayoutInfo { Width = W, Height = H, Scale = u, Header = new Rectangle(0, 0, W, headerH), Strip = new Rectangle(0, H - stripH, W, stripH) };
        int gy = headerH, gh = H - headerH - stripH, cw = W / cols, ch = gh / rows; var pr = new Rectangle[np]; var ir = new Rectangle[np];
        for (int i = 0; i < np; i++)
        {
            int c = i % 2, r = i / 2;
            if (np == 3) { if (i == 0) pr[i] = new Rectangle(0, gy, W, ch); else pr[i] = new Rectangle((i - 1) * cw, gy + ch, cw, gh - ch); }
            else if (np == 1) pr[i] = new Rectangle(0, gy, W, gh);
            else pr[i] = new Rectangle(c * cw, gy + r * ch, c == cols - 1 ? W - c * cw : cw, r == rows - 1 ? gh - r * ch : ch);
            var p = pr[i]; ir[i] = new Rectangle(p.X + pad, p.Y + titleH, Math.Max(2, p.Width - 2 * pad), Math.Max(2, p.Height - titleH - barH - pad));
        }
        L.Panels = pr; L.Images = ir;
        if (stripH > 0) { int ml = (int)(78 * u), mr = (int)(24 * u), mt = (int)(24 * u), mb = (int)(22 * u); L.StripPlot = new Rectangle(ml, L.Strip.Y + mt, Math.Max(10, W - ml - mr), Math.Max(10, stripH - mt - mb)); }
        return L;
    }

    // ------------------------------------------------------------------------------------------------ rendering
    /// <summary>Renders source frame `index` (sequence) or turntable step `index` (turntable: the one frame is loaded for each call: use <see cref="Render"/> with a kept FrameContext in loops).</summary>
    public static RenderedFrame RenderFrame(IFrameProvider p, VideoSettings s, ColorRangePlan plan, int index, CancellationToken ct = default)
    {
        if (s.Turntable) { var fc = FrameContext.Create(p, Math.Clamp(s.TurntableFrame, 0, p.Count - 1), s, ct); int cnt = VideoSettings.TurntableFrames(s.TurntableSeconds, s.Fps); return Render(fc, s, plan, 1, 1, s.TurntableAzimuth(index, cnt)); }
        return Render(FrameContext.Create(p, index, s, ct), s, plan, index + 1, p.Count, s.Surface.AzimuthDeg);
    }

    /// <summary>Convenience for the live preview: the frame at `index` exactly as the video would show it.</summary>
    public static RenderedFrame RenderPreview(IFrameProvider p, VideoSettings s, ColorRangePlan plan, int index, CancellationToken ct = default) => RenderFrame(p, s, plan, index, ct);

    public static RenderedFrame Render(FrameContext fc, VideoSettings s, ColorRangePlan plan, int frameNumber, int frameCount, double azimuthDeg)
    {
        var L = Layout(s, plan); float u = L.Scale; int np = Math.Min(4, s.Panels.Count); double lam = fc.Provider.Par.Lambda;
        var rf = new RenderedFrame { Width = L.Width, Height = L.Height, RangeLo = new double[np], RangeHi = new double[np], Units = new string[np], Titles = new string[np], AzimuthDeg = azimuthDeg };
        using var canvas = new Bitmap(L.Width, L.Height, PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(unchecked((int)PanelRenderer.BackgroundArgb))); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var white = new SolidBrush(Color.FromArgb(235, 235, 235)); using var gray = new SolidBrush(Color.FromArgb(170, 170, 170)); using var fTitle = PanelRenderer.MakeFont(14 * u, true);
        PanelKind primary = s.Panels.Select(x => x.Kind).FirstOrDefault(k => PanelSpec.IsOpdLike(k), PanelKind.Opd);
        for (int i = 0; i < np; i++)
        {
            var sp = s.Panels[i]; var cmap = s.CmapOf(sp); var map = PanelData.Map(fc, sp, s, out string unit, out string title); int M = fc.M;
            rf.Units[i] = unit; rf.Titles[i] = title;
            if (map.Length == 0) { rf.RangeLo[i] = 0; rf.RangeHi[i] = 1; g.DrawString(title + ": not available", fTitle, gray, L.Images[i].X + 8, L.Images[i].Y + 8); continue; }
            var (lo, hi) = Resolve(i, sp, map, s, plan); rf.RangeLo[i] = lo; rf.RangeHi[i] = hi;
            g.DrawString($"{title} [{unit}]", fTitle, white, L.Panels[i].X + 8 * u, L.Panels[i].Y + 3 * u);
            var ir = L.Images[i];
            if (sp.Kind == PanelKind.Surface3D)
            {
                double sc = Writers.UnitScale(s.Unit, lam); var mesh = fc.Mesh(s.Surface.Filtered, s.Surface.Mesh); double zhalf = (plan.GlobalMaxNm - plan.GlobalMinNm) / 2 * 1e-6 * plan.Exaggeration;
                var view = new SurfaceView { AzimuthDeg = azimuthDeg, ElevationDeg = s.Surface.ElevationDeg, Exaggeration = plan.Exaggeration, ZCenterNm = (plan.GlobalMinNm + plan.GlobalMaxNm) / 2, Cmap = cmap, LoNm = (float)(lo / sc * 1e9), HiNm = (float)(hi / sc * 1e9),
                    FitRadiusMm = Math.Sqrt(plan.PupilRadiusMm * plan.PupilRadiusMm + zhalf * zhalf) };
                var px = SoftwareSurface.Render(mesh, view, ir.Width, ir.Height, out var cam);
                using (var bmp = PanelRenderer.FromPixels(px, ir.Width, ir.Height, out var hd)) { try { g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(bmp, ir.X, ir.Y, ir.Width, ir.Height); } finally { hd.Free(); } }
                if (s.Surface.Axes) DrawAxes(g, cam, ir, s, unit, sc, u, gray);
            }
            else { var px = PanelRenderer.CropRender(map, M, plan, cmap, (float)lo, (float)hi, out int w, out int h); PanelRenderer.DrawFitted(g, px, w, h, ir); }
            if (s.Colorbar) PanelRenderer.DrawColorbar(g, new Rectangle(ir.X, ir.Bottom, ir.Width, L.Panels[i].Bottom - ir.Bottom), cmap, lo, hi, unit, u, white);
        }
        // overlays
        string left = "", right = "";
        if (s.OverlayName) left = s.Turntable ? $"{fc.Name}   azimuth {Norm360(azimuthDeg):0}°" : $"{fc.Name}   {frameNumber} / {frameCount}";
        if (s.OverlayStats) { var (pw, rms, pv) = fc.StatsOf(primary, s.Surface.Filtered); right = $"power {pw.ToString("+0.000;-0.000", Inv)} D    rms {rms.ToString("0", Inv)} nm    PV {pv.ToString("0.00", Inv)} µm"; }
        rf.HeaderLeft = left; rf.HeaderRight = right;
        if (L.Header.Height > 0)
        {
            using var hb = new SolidBrush(Color.FromArgb(28, 28, 28)); g.FillRectangle(hb, L.Header); using var f = PanelRenderer.MakeFont(L.Header.Height * 0.52f, true);
            if (left.Length > 0) g.DrawString(left, f, white, 10 * u, L.Header.Height * 0.2f);
            if (right.Length > 0) { var sz = g.MeasureString(right, f); g.DrawString(right, f, white, L.Width - sz.Width - 10 * u, L.Header.Height * 0.2f); }
        }
        if (L.Strip.Height > 0 && plan.Strip.Length > 0) DrawStrip(g, L, plan, frameNumber - 1, u, white, gray);
        g.Flush(); rf.Bgra = PanelRenderer.ToBgra(canvas); return rf;
    }

    static double Norm360(double a) => ((a % 360) + 360) % 360;

    static (double lo, double hi) Resolve(int idx, PanelSpec p, float[] map, VideoSettings s, ColorRangePlan plan)
    {
        double lo, hi;
        if (p.Kind == PanelKind.WrappedPhase) { lo = -Math.PI; hi = Math.PI; }
        else if (s.Range == RangeMode.FixedAcrossFrames && idx < plan.Lo.Length && !double.IsNaN(plan.Lo[idx])) { lo = plan.Lo[idx]; hi = plan.Hi[idx]; }
        else if (s.Range == RangeMode.PercentileClipped) { var (a, b) = Colormap.AutoRange(map, s.PctLo, s.PctHi); lo = a; hi = b; }
        else { var (a, b) = PanelData.MinMax(map); lo = a; hi = b; }
        if (!(hi > lo)) hi = lo + 1; return (lo, hi);
    }

    static void DrawAxes(Graphics g, SurfaceCamera c, Rectangle ir, VideoSettings s, string unit, double sc, float u, Brush text)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias; using var pen = new Pen(Color.FromArgb(140, 140, 150), Math.Max(1f, u));
        var xs = new[] { c.XMin, c.XMax }; var ys = new[] { c.YMin, c.YMax }; var zs = new[] { c.ZMin, c.ZMax }; PointF P(double x, double y, double z) { var (px, py) = c.Project(x, y, z); return new PointF((float)(ir.X + px), (float)(ir.Y + py)); }
        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++)
        {
            g.DrawLine(pen, P(xs[0], ys[a], zs[b]), P(xs[1], ys[a], zs[b])); g.DrawLine(pen, P(xs[a], ys[0], zs[b]), P(xs[a], ys[1], zs[b])); g.DrawLine(pen, P(xs[a], ys[b], zs[0]), P(xs[a], ys[b], zs[1]));
        }
        using var f = PanelRenderer.MakeFont(11 * u); double zlo = (c.ZMin / (c.ExaggerationUsed * 1e-6) + c.ZCenterNm) / 1e9 * sc, zhi = (c.ZMax / (c.ExaggerationUsed * 1e-6) + c.ZCenterNm) / 1e9 * sc;
        g.DrawString($"x {PanelRenderer.Num(c.XMin)}…{PanelRenderer.Num(c.XMax)} mm   y {PanelRenderer.Num(c.YMin)}…{PanelRenderer.Num(c.YMax)} mm   OPD {PanelRenderer.Num(zlo)}…{PanelRenderer.Num(zhi)} {unit}   height ×{c.ExaggerationUsed:0}", f, text, ir.X + 6 * u, ir.Bottom - 18 * u);
    }

    static void DrawStrip(Graphics g, LayoutInfo L, ColorRangePlan plan, int cur, float u, Brush white, Brush gray)
    {
        var r = L.StripPlot; var v = plan.Strip; int n = v.Length; float lo = float.MaxValue, hi = float.MinValue; foreach (var x in v) if (!float.IsNaN(x)) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
        if (lo > hi) return; if (hi - lo < 1e-9f) { lo -= 0.5f; hi += 0.5f; } float pad = (hi - lo) * 0.08f; lo -= pad; hi += pad;
        using var f = PanelRenderer.MakeFont(11 * u); using var axis = new Pen(Color.FromArgb(120, 120, 120), 1); using var line = new Pen(Color.FromArgb(120, 190, 255), Math.Max(1.5f, 2 * u));
        g.DrawRectangle(axis, r.X, r.Y, r.Width, r.Height); g.DrawString(plan.StripLabel, f, white, r.X, L.Strip.Y + 3 * u);
        g.DrawString(PanelRenderer.Num(hi), f, gray, 4 * u, r.Y - 2 * u); g.DrawString(PanelRenderer.Num(lo), f, gray, 4 * u, r.Bottom - 12 * u);
        g.DrawString("1", f, gray, r.X - 2 * u, r.Bottom + 3 * u); var nstr = n.ToString(Inv); var nsz = g.MeasureString(nstr, f); g.DrawString(nstr, f, gray, r.Right - nsz.Width + 2 * u, r.Bottom + 3 * u);
        PointF At(int i) => new(r.X + (n <= 1 ? r.Width / 2f : (float)i / (n - 1) * r.Width), r.Bottom - (v[i] - lo) / (hi - lo) * r.Height);
        PointF? prev = null; for (int i = 0; i < n; i++) { if (float.IsNaN(v[i])) { prev = null; continue; } var q = At(i); if (prev != null) g.DrawLine(line, prev.Value, q); else g.FillEllipse(Brushes.SteelBlue, q.X - 2, q.Y - 2, 4, 4); prev = q; }
        if (cur >= 0 && cur < n)
        {
            float x = n <= 1 ? r.X + r.Width / 2f : r.X + (float)cur / (n - 1) * r.Width; var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
            using var mp = new Pen(MarkerColor, Math.Max(2f, 2 * u)); using var mb = new SolidBrush(MarkerColor); g.DrawLine(mp, (int)x, r.Y, (int)x, r.Bottom);
            if (!float.IsNaN(v[cur])) { var q = At(cur); int rr = Math.Max(3, (int)(4 * u)); g.FillEllipse(mb, q.X - rr, q.Y - rr, 2 * rr, 2 * rr); }
            g.SmoothingMode = old;
        }
    }

    // ------------------------------------------------------------------------------------------------ blending
    /// <summary>out = (1-t) a + t b per byte (same size frames), into `dest` (allocated if null).</summary>
    public static byte[] Blend(byte[] a, byte[] b, double t, byte[]? dest = null)
    {
        dest ??= new byte[a.Length]; int ti = (int)Math.Round(Math.Clamp(t, 0, 1) * 256); Parallel.For(0, (a.Length + 65535) / 65536, blk => { int s0 = blk * 65536, e = Math.Min(a.Length, s0 + 65536); for (int i = s0; i < e; i++) dest[i] = (byte)((a[i] * (256 - ti) + b[i] * ti + 128) >> 8); });
        return dest;
    }
}
