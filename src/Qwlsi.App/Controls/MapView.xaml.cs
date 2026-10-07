using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qwlsi.Core;

namespace Qwlsi.App.Controls;

/// <summary>What to show: a float map + colour range. Rendering is done by MapView (bind <see cref="MapView.Spec"/>).</summary>
public sealed class MapSpec
{
    public float[] Data = Array.Empty<float>(); public int Width, Height;
    public ColormapKind Cmap = ColormapKind.Viridis; public float VMin, VMax = 1; public string Unit = ""; public string Title = "";
}

/// <summary>Zoom / pan image viewer with colour bar and pixel read-out.</summary>
public partial class MapView : UserControl
{
    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(nameof(Spec), typeof(MapSpec), typeof(MapView), new PropertyMetadata(null, (d, e) => ((MapView)d).Render()));
    public MapSpec? Spec { get => (MapSpec?)GetValue(SpecProperty); set => SetValue(SpecProperty, value); }

    WriteableBitmap? bmp; bool fit = true; double zoom = 1; Point dragStart; bool dragging; double dragH, dragV;

    public MapView() { InitializeComponent(); }

    /// <summary>Current rendering as an image source (used by the PNG preview export).</summary>
    public BitmapSource? Bitmap => bmp;

    void Render()
    {
        var s = Spec;
        if (s == null || s.Data.Length == 0) { Img.Source = null; Empty.Visibility = Visibility.Visible; Bar.Source = null; MinText.Text = MaxText.Text = UnitText.Text = ""; return; }
        Empty.Visibility = Visibility.Collapsed;
        if (bmp == null || bmp.PixelWidth != s.Width || bmp.PixelHeight != s.Height) { bmp = new WriteableBitmap(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null); Img.Source = bmp; if (fit) ApplyFit(); }
        var px = new uint[s.Width * s.Height]; Colormap.Render(s.Data, s.VMin, s.VMax, s.Cmap, px);
        bmp.WritePixels(new Int32Rect(0, 0, s.Width, s.Height), px, s.Width * 4, 0);
        var lut = Colormap.Lut(s.Cmap); var bar = new WriteableBitmap(256, 1, 96, 96, PixelFormats.Bgra32, null); bar.WritePixels(new Int32Rect(0, 0, 256, 1), lut, 256 * 4, 0); Bar.Source = bar;
        MinText.Text = Fmt(s.VMin); MaxText.Text = Fmt(s.VMax); UnitText.Text = s.Unit;
    }

    static string Fmt(float v) => Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : Math.Abs(v) >= 1 ? v.ToString("0.##", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);

    void SetZoom(double z) { zoom = Math.Clamp(z, 0.05, 40); Zoom.ScaleX = Zoom.ScaleY = zoom; RenderOptions.SetBitmapScalingMode(Img, zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality); }

    void ApplyFit()
    {
        if (Spec == null || Spec.Width == 0 || Scroll.ViewportWidth <= 0) return;
        double vw = Scroll.ActualWidth - 4, vh = Scroll.ActualHeight - 4; if (vw <= 0 || vh <= 0) return;
        SetZoom(Math.Min(vw / Spec.Width, vh / Spec.Height));
    }

    void OnViewportSized(object s, SizeChangedEventArgs e) { if (fit) ApplyFit(); }
    void OnFit(object s, RoutedEventArgs e) { fit = true; ApplyFit(); }
    void On11(object s, RoutedEventArgs e) { fit = false; SetZoom(1); }
    void OnWheel(object s, MouseWheelEventArgs e) { fit = false; SetZoom(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15)); e.Handled = true; }

    void OnDown(object s, MouseButtonEventArgs e) { dragging = true; dragStart = e.GetPosition(Scroll); dragH = Scroll.HorizontalOffset; dragV = Scroll.VerticalOffset; Img.CaptureMouse(); }
    void OnUp(object s, MouseButtonEventArgs e) { dragging = false; Img.ReleaseMouseCapture(); }
    void OnLeave(object s, MouseEventArgs e) { Readout.Visibility = Visibility.Collapsed; }

    void OnMove(object s, MouseEventArgs e)
    {
        if (dragging) { var p = e.GetPosition(Scroll); Scroll.ScrollToHorizontalOffset(dragH - (p.X - dragStart.X)); Scroll.ScrollToVerticalOffset(dragV - (p.Y - dragStart.Y)); return; }
        var sp = Spec; if (sp == null || sp.Data.Length == 0) return;
        var q = e.GetPosition(Img); int x = (int)Math.Floor(q.X), y = (int)Math.Floor(q.Y);
        if (x < 0 || y < 0 || x >= sp.Width || y >= sp.Height) { Readout.Visibility = Visibility.Collapsed; return; }
        float v = sp.Data[y * sp.Width + x];
        Readout.Text = $"x={x}  y={y}  {(float.IsNaN(v) ? "outside pupil" : v.ToString("0.###", CultureInfo.InvariantCulture) + " " + sp.Unit)}"; Readout.Visibility = Visibility.Visible;
    }
}
