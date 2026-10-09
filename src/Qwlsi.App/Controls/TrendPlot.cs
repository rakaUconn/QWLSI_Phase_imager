using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Qwlsi.Core;

namespace Qwlsi.App.Controls;

/// <summary>One line of a <see cref="TrendPlot"/>: y values per frame (NaN = gap).</summary>
public sealed class TrendSeries
{
    public string Name = ""; public double[] Y = Array.Empty<double>();
}

/// <summary>Simple line plot of several series against the frame number (1-based) with a legend, nice ticks and a marker for the selected frame. Click = select the nearest frame.</summary>
public sealed class TrendPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<TrendSeries>), typeof(TrendPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkerFrameProperty = DependencyProperty.Register(nameof(MarkerFrame), typeof(int), typeof(TrendPlot), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YLabelProperty = DependencyProperty.Register(nameof(YLabel), typeof(string), typeof(TrendPlot), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty XLabelProperty = DependencyProperty.Register(nameof(XLabel), typeof(string), typeof(TrendPlot), new FrameworkPropertyMetadata("frame number", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The lines (all of equal length; x = 1..N).</summary>
    public IReadOnlyList<TrendSeries>? Series { get => (IReadOnlyList<TrendSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    /// <summary>1-based frame number of the selected frame (0 = none).</summary>
    public int MarkerFrame { get => (int)GetValue(MarkerFrameProperty); set => SetValue(MarkerFrameProperty, value); }
    public string YLabel { get => (string)GetValue(YLabelProperty); set => SetValue(YLabelProperty, value); }
    public string XLabel { get => (string)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }
    /// <summary>Raised with the 1-based frame number nearest to a click.</summary>
    public event Action<int>? FrameClicked;

    static readonly Color[] Palette = { Color.FromRgb(0x1F, 0x77, 0xB4), Color.FromRgb(0xD6, 0x27, 0x28), Color.FromRgb(0x2C, 0xA0, 0x2C), Color.FromRgb(0xFF, 0x7F, 0x0E), Color.FromRgb(0x94, 0x67, 0xBD), Color.FromRgb(0x8C, 0x56, 0x4B), Color.FromRgb(0xE3, 0x77, 0xC2), Color.FromRgb(0x17, 0xBE, 0xCF) };
    public static Color ColorOf(int i) => Palette[i % Palette.Length];
    Rect plot; int nFrames; double x0 = 1, x1 = 2;

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight; dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
        var inv = CultureInfo.InvariantCulture; var face = new Typeface("Segoe UI"); double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText T(string s, double size, Brush b) => new(s, inv, FlowDirection.LeftToRight, face, size, b, dpi);
        var ser = Series; nFrames = ser != null && ser.Count > 0 ? ser.Max(s => s.Y.Length) : 0;
        if (W < 120 || H < 80) return;
        if (ser == null || ser.Count == 0 || nFrames == 0) { var t = T("No data — press 'Apply selected removal to all frames'.", 12, Brushes.Gray); dc.DrawText(t, new Point((W - t.Width) / 2, H / 2 - 8)); return; }
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in ser) foreach (var v in s.Y) if (!double.IsNaN(v) && !double.IsInfinity(v)) { if (v < lo) lo = v; if (v > hi) hi = v; }
        if (lo > hi) { lo = 0; hi = 1; } if (hi - lo < 1e-12) { lo -= 0.5; hi += 0.5; } double pad = (hi - lo) * 0.06; lo -= pad; hi += pad;
        x0 = 1; x1 = Math.Max(2, nFrames);
        double legendH = 16 * ((ser.Count + 2) / 3), left = 62, right = 12, top = 8 + legendH, bottom = 38; plot = new Rect(left, top, Math.Max(10, W - left - right), Math.Max(10, H - top - bottom));
        double X(double v) => plot.Left + (v - x0) / (x1 - x0) * plot.Width; double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), 1); var axis = new Pen(Brushes.Gray, 1);
        foreach (var tv in SurfaceMesh.NiceTicks(lo, hi, 6)) { double y = Y(tv); if (y < plot.Top - 1 || y > plot.Bottom + 1) continue; dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y)); var ft = T(tv.ToString("0.##", inv), 11, Brushes.Black); dc.DrawText(ft, new Point(plot.Left - 6 - ft.Width, y - ft.Height / 2)); }
        foreach (var tv in SurfaceMesh.NiceTicks(x0, x1, 8)) { if (tv < x0 || tv > x1 || Math.Abs(tv - Math.Round(tv)) > 1e-9) continue; double x = X(tv); dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom)); var ft = T(((int)Math.Round(tv)).ToString(inv), 11, Brushes.Black); dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4)); }
        dc.DrawRectangle(null, axis, plot);
        dc.PushClip(new RectangleGeometry(plot));
        for (int k = 0; k < ser.Count; k++)
        {
            var pen = new Pen(new SolidColorBrush(ColorOf(k)), 1.6); var brush = new SolidColorBrush(ColorOf(k)); Point? prev = null; var y = ser[k].Y;
            for (int i = 0; i < y.Length; i++)
            {
                if (double.IsNaN(y[i])) { prev = null; continue; } var p = new Point(X(i + 1), Y(y[i])); if (prev != null) dc.DrawLine(pen, prev.Value, p); dc.DrawEllipse(brush, null, p, 2.6, 2.6); prev = p;
            }
        }
        if (MarkerFrame >= 1 && MarkerFrame <= nFrames) { double mx = X(MarkerFrame); dc.DrawLine(new Pen(Brushes.Black, 1) { DashStyle = DashStyles.Dash }, new Point(mx, plot.Top), new Point(mx, plot.Bottom)); }
        dc.Pop();
        for (int k = 0; k < ser.Count; k++)
        {
            double lx = left + (k % 3) * Math.Max(100, (W - left - right) / 3), ly = 4 + (k / 3) * 16; dc.DrawRectangle(new SolidColorBrush(ColorOf(k)), null, new Rect(lx, ly + 4, 14, 3)); dc.DrawText(T(ser[k].Name, 11, Brushes.Black), new Point(lx + 18, ly));
        }
        var xl = T(XLabel, 11, Brushes.DimGray); dc.DrawText(xl, new Point(plot.Left + (plot.Width - xl.Width) / 2, H - 17));
        if (!string.IsNullOrEmpty(YLabel)) { dc.PushTransform(new RotateTransform(-90, 14, plot.Top + plot.Height / 2)); var yl = T(YLabel, 11, Brushes.DimGray); dc.DrawText(yl, new Point(14 - yl.Width / 2, plot.Top + plot.Height / 2 - yl.Height / 2)); dc.Pop(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (nFrames == 0 || plot.Width <= 0) return;
        var p = e.GetPosition(this); if (!plot.Contains(p)) return;
        int f = (int)Math.Round(x0 + (p.X - plot.Left) / plot.Width * (x1 - x0)); FrameClicked?.Invoke(Math.Clamp(f, 1, nFrames));
    }
}
