using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qwlsi.Core;

namespace Qwlsi.App.Controls;

/// <summary>PNG preview with colour map, colour bar and a caption (HANDOFF §6). Must be called on the UI thread.</summary>
public static class PngPreview
{
    public static void Save(string path, float[] map, int M, ColormapKind kind, float vmin, float vmax, string unit, string title, string subtitle)
    {
        // crop to the valid region (+margin)
        int x0 = M, x1 = -1, y0 = M, y1 = -1;
        for (int y = 0; y < M; y++) for (int x = 0; x < M; x++) if (!float.IsNaN(map[(long)y * M + x])) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) { x0 = y0 = 0; x1 = y1 = M - 1; }
        x0 = Math.Max(0, x0 - 20); y0 = Math.Max(0, y0 - 20); x1 = Math.Min(M - 1, x1 + 20); y1 = Math.Min(M - 1, y1 + 20);
        int w = x1 - x0 + 1, h = y1 - y0 + 1; var crop = new float[w * h];
        for (int y = 0; y < h; y++) Array.Copy(map, (long)(y + y0) * M + x0, crop, (long)y * w, w);
        var px = new uint[w * h]; Colormap.Render(crop, vmin, vmax, kind, px, 0xFFFFFFFF);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);

        const int mx = 24, top = 62, bottom = 78; int W = w + 2 * mx, H = h + top + bottom;
        var dv = new DrawingVisual(); var inv = CultureInfo.InvariantCulture; var typeface = new Typeface("Segoe UI");
        double dpi = 1.0;
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            FormattedText T(string s, double size, Brush b) => new(s, inv, FlowDirection.LeftToRight, typeface, size, b, dpi);
            dc.DrawText(T(title, 17, Brushes.Black), new Point(mx, 10)); dc.DrawText(T(subtitle, 13, Brushes.DimGray), new Point(mx, 34));
            dc.DrawImage(bmp, new Rect(mx, top, w, h));
            // colour bar
            double bw = Math.Min(w, 700), bx = mx + (w - bw) / 2, by = top + h + 14; var lut = Colormap.Lut(kind); var barPx = new uint[256]; Array.Copy(lut, barPx, 256);
            dc.DrawImage(BitmapSource.Create(256, 1, 96, 96, PixelFormats.Bgra32, null, barPx, 256 * 4), new Rect(bx, by, bw, 16));
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(bx, by, bw, 16));
            for (int i = 0; i <= 4; i++)
            {
                double fx = bx + bw * i / 4.0; dc.DrawLine(new Pen(Brushes.Black, 1), new Point(fx, by + 16), new Point(fx, by + 21));
                var ft = T((vmin + (vmax - vmin) * i / 4.0).ToString("0.##", inv), 12, Brushes.Black); dc.DrawText(ft, new Point(fx - ft.Width / 2, by + 22));
            }
            var ut = T(unit, 12, Brushes.Black); dc.DrawText(ut, new Point(bx + bw + 8, by));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); using var fs = File.Create(path); enc.Save(fs);
    }
}
