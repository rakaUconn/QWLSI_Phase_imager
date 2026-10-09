using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Qwlsi.Core;

namespace Qwlsi.Video;

/// <summary>Drawing helpers for one panel: map -> pixels (crop to the pupil), pixel buffer -> bitmap, colour bar, text.</summary>
internal static class PanelRenderer
{
    public const uint BackgroundArgb = 0xFF101010;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Crops the map to the plan's pupil rectangle and colours it (NaN = background).</summary>
    public static uint[] CropRender(float[] map, int M, ColorRangePlan plan, ColormapKind cmap, float lo, float hi, out int w, out int h)
    {
        w = plan.CropW; h = plan.CropH; var crop = new float[(long)w * h];
        for (int y = 0; y < h; y++) Array.Copy(map, (long)(y + plan.CropY0) * M + plan.CropX0, crop, (long)y * w, w);
        var px = new uint[(long)w * h]; Colormap.Render(crop, lo, hi, cmap, px, BackgroundArgb); return px;
    }

    public static Bitmap FromPixels(uint[] px, int w, int h, out GCHandle handle)
    {
        handle = GCHandle.Alloc(px, GCHandleType.Pinned); return new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
    }

    /// <summary>Draws a pixel buffer into `dest` keeping the aspect ratio (centred); nearest neighbour at 1:1.</summary>
    public static void DrawFitted(Graphics g, uint[] px, int w, int h, Rectangle dest)
    {
        using var bmp = FromPixels(px, w, h, out var hd);
        try
        {
            double s = Math.Min(dest.Width / (double)w, dest.Height / (double)h); int dw = Math.Max(1, (int)Math.Round(w * s)), dh = Math.Max(1, (int)Math.Round(h * s));
            var r = new Rectangle(dest.X + (dest.Width - dw) / 2, dest.Y + (dest.Height - dh) / 2, dw, dh);
            g.InterpolationMode = dw == w && dh == h ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, r, 0, 0, w, h, GraphicsUnit.Pixel);
        }
        finally { hd.Free(); }
    }

    public static Font MakeFont(float px, bool bold = false)
    {
        try { return new Font("Segoe UI", Math.Max(8, px), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, Math.Max(8, px), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
    }

    public static string Num(double v) { double a = Math.Abs(v); return a >= 1000 ? v.ToString("0", Inv) : a >= 100 ? v.ToString("0.#", Inv) : a >= 1 ? v.ToString("0.##", Inv) : v.ToString("0.###", Inv); }

    /// <summary>Colour bar with ticks and unit label inside `zone` (below the image).</summary>
    public static void DrawColorbar(Graphics g, Rectangle zone, ColormapKind cmap, double lo, double hi, string unit, float u, Brush text)
    {
        int bw = Math.Max(40, (int)(zone.Width * 0.8)), bx = zone.X + (zone.Width - bw) / 2, by = zone.Y + (int)(4 * u), bh = Math.Max(8, (int)(13 * u));
        var lut = Colormap.Lut(cmap); var row = new uint[256]; Array.Copy(lut, row, 256); using var bmp = FromPixels(row, 256, 1, out var hd);
        try { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(bmp, new Rectangle(bx, by, bw, bh), 0, 0, 256, 1, GraphicsUnit.Pixel); } finally { hd.Free(); }
        using var pen = new Pen(Color.Gray, 1); g.DrawRectangle(pen, bx, by, bw, bh);
        using var f = MakeFont(11 * u); var sf = new StringFormat { Alignment = StringAlignment.Center };
        foreach (double t in SurfaceMesh.NiceTicks(lo, hi, 5))
        {
            if (hi <= lo) break; float x = (float)(bx + bw * (t - lo) / (hi - lo)); g.DrawLine(pen, x, by + bh, x, by + bh + 4 * u); g.DrawString(Num(t), f, text, x, by + bh + 4 * u, sf);
        }
        g.DrawString(unit, f, text, bx + bw + 6 * u, by - 1 * u);
    }

    public static byte[] ToBgra(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height; var o = new byte[(long)w * h * 4]; var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, o, y * w * 4, w * 4); } finally { bmp.UnlockBits(d); }
        return o;
    }
}
