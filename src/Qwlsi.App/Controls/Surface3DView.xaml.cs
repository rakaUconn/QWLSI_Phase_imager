using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using Qwlsi.Core;
using SharpDX;
using HxMesh = HelixToolkit.SharpDX.Core.MeshGeometry3D;
using HxTextInfo = HelixToolkit.SharpDX.Core.TextInfo;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

namespace Qwlsi.App.Controls;

/// <summary>
/// Viewport3DX whose mouse-down handler cannot throw. Helix hit-tests its (hidden) view box on every mouse-down and that code raises a
/// NullReferenceException in this configuration; the app never needs the hit test, so the exception is swallowed (first few are logged).
/// </summary>
public sealed class SafeViewport3DX : Viewport3DX
{
    public static int Swallowed;
    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        try { base.OnPreviewMouseDown(e); }
        catch (NullReferenceException ex)
        {
            if (System.Threading.Interlocked.Increment(ref Swallowed) <= 3)
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "qwlsi_error.log"), DateTime.Now + " [swallowed in SafeViewport3DX.OnPreviewMouseDown]" + Environment.NewLine + ex.StackTrace?.Split('\n').FirstOrDefault() + Environment.NewLine + Environment.NewLine); } catch { }
        }
    }
}

public sealed class SceneOptions
{
    public double Exaggeration = 1000;
    public ColormapKind Cmap = ColormapKind.Viridis;
    public float ColorLo, ColorHi = 1;
    public bool Wireframe, ShowAxes = true;
}

/// <summary>3-D height-field viewer (HelixToolkit, SharpDX / Direct3D 11 backend). Units: x,y in mm, z drawn as OPD[nm] * 1e-6 * exaggeration.</summary>
public partial class Surface3DView : UserControl
{
    MeshGeometryModel3D? surface; LineGeometryModel3D? frame; BillboardTextModel3D? labels; DirectionalLight3D? l1, l2; AmbientLight3D? amb;
    SafeViewport3DX Vp = null!; EffectsManager? em;
    Point3D sceneCenter; double sceneRadius = 5;
    SurfaceMeshData? mesh; SceneOptions opt = new(); bool software; string rendererNote = "";
    public event Action<string>? RendererFallback;
    public bool HasScene => surface != null;
    public bool IsSoftware => software;

    public Surface3DView()
    {
        InitializeComponent();
        CreateViewport(false);
    }

    /// <summary>
    /// Hardware Direct3D 11 (default) or WARP software rasteriser. The viewport is RE-CREATED on every renderer change: replacing the
    /// EffectsManager of a live Viewport3DX leaves Helix's view-box node stale and every later mouse-down throws a NullReferenceException.
    /// </summary>
    public void UseEffectsManager(bool useSoftware)
    {
        if (Vp != null && software == useSoftware) return;
        CreateViewport(useSoftware); if (mesh != null) { Rebuild(); SetView("Isometric"); }
    }

    void CreateViewport(bool useSoftware)
    {
        if (Vp != null)
        {
            VpHost.Children.Remove(Vp); surface = null; frame = null; labels = null; l1 = l2 = null; amb = null;
            try { Vp.Items.Clear(); } catch { }
            try { Vp.Dispose(); } catch { }
            try { em?.Dispose(); } catch { }   // release the Direct3D device of the old viewport (otherwise it leaks ~100s of MB per renderer switch)
            em = null;
        }
        software = useSoftware;
        var vp = new SafeViewport3DX
        {
            BackgroundColor = Color.FromRgb(0x20, 0x25, 0x2B), ShowCoordinateSystem = false, ShowViewCube = false, EnableSwapChainRendering = true, MSAA = MSAALevel.Four, FXAALevel = FXAALevel.None,
            CameraRotationMode = CameraRotationMode.Trackball, ZoomExtentsWhenLoaded = false, UseDefaultGestures = true, EnableDesignModeRendering = false,
            EnableMouseButtonHitTest = false, IsViewCubeEdgeClicksEnabled = false,
        };
        try { em = useSoftware ? new DefaultEffectsManager(new EffectsManagerConfiguration { EnableSoftwareRendering = true }) : new DefaultEffectsManager(); vp.EffectsManager = em; rendererNote = useSoftware ? "renderer: Direct3D 11 WARP (software)" : "renderer: Direct3D 11 hardware"; }
        catch (Exception e) { rendererNote = "renderer unavailable: " + e.Message; if (!useSoftware) { Vp = vp; CreateViewport(true); return; } }
        Vp = vp; VpHost.Children.Insert(0, Vp); RendererText.Text = rendererNote;
        Vp.RenderExceptionOccurred += (_, e) =>
        {
            if (!software) Dispatcher.BeginInvoke(() => { UseEffectsManager(true); RendererFallback?.Invoke("Direct3D hardware rendering failed (" + e.Exception.GetType().Name + ") — switched to software (WARP)."); });
            else Dispatcher.BeginInvoke(() => { Message.Text = "3-D rendering failed: " + e.Exception.Message; Message.Visibility = Visibility.Visible; });
        };
    }

    public void Clear(string message)
    {
        foreach (var m in new Element3D?[] { surface, frame, labels, l1, l2, amb }) if (m != null) Vp.Items.Remove(m);
        surface = null; frame = null; labels = null; l1 = l2 = null; amb = null; mesh = null; Message.Text = message; Message.Visibility = Visibility.Visible; MinText.Text = MaxText.Text = "";
    }

    public void SetScene(SurfaceMeshData m, SceneOptions o, bool resetCamera)
    {
        mesh = m; opt = o; Rebuild(); if (resetCamera) SetView("Isometric");
    }

    public void UpdateOptions(SceneOptions o)
    {
        opt = o; if (mesh == null) return; double oldR = sceneRadius; var oldC = sceneCenter; Rebuild();
        // bounds changed (height exaggeration): keep the viewing direction and zoom proportionally so the surface stays in view
        if (Vp.Camera is PerspectiveCamera cam && oldR > 0 && Math.Abs(sceneRadius / oldR - 1) > 0.02)
        {
            var dir = cam.LookDirection; dir.Normalize(); double dist = (cam.Position - oldC).Length * sceneRadius / oldR;
            Vp.Camera = new PerspectiveCamera { Position = sceneCenter - dir * dist, LookDirection = dir, UpDirection = cam.UpDirection, FieldOfView = cam.FieldOfView, NearPlaneDistance = dist * 0.01, FarPlaneDistance = dist * 10 };
        }
    }

    static Color4 C4(uint argb) => new Color4(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, 1f);

    void Rebuild()
    {
        var m = mesh; if (m == null) return; int n = m.N; double zs = 1e-6 * opt.Exaggeration;
        var pos = new Vector3Collection(n * n); var col = new Color4Collection(n * n); var lut = Colormap.Lut(opt.Cmap); float scale = opt.ColorHi > opt.ColorLo ? 255f / (opt.ColorHi - opt.ColorLo) : 0;
        for (int iy = 0; iy < n; iy++) for (int ix = 0; ix < n; ix++)
        {
            float v = m.Value[iy * n + ix]; var (x, y) = m.Position(ix, iy);
            if (float.IsNaN(v)) { pos.Add(new Vector3((float)x, (float)y, 0)); col.Add(new Color4(0, 0, 0, 1)); continue; }
            pos.Add(new Vector3((float)x, (float)y, (float)(v * zs))); col.Add(C4(lut[Math.Clamp((int)((v - opt.ColorLo) * scale + 0.5f), 0, 255)]));
        }
        var idx = new IntCollection(m.Triangles);
        var nrm = new Vector3[n * n];
        for (int t = 0; t < m.Triangles.Length; t += 3)
        {
            int a = m.Triangles[t], b = m.Triangles[t + 1], c = m.Triangles[t + 2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); nrm[a] += fn; nrm[b] += fn; nrm[c] += fn;
        }
        for (int i = 0; i < nrm.Length; i++) { if (nrm[i].LengthSquared() > 0) nrm[i].Normalize(); else nrm[i] = Vector3.UnitZ; }
        var geo = new HxMesh { Positions = pos, Indices = idx, Colors = col, Normals = new Vector3Collection(nrm) };

        if (surface == null)
        {
            surface = new MeshGeometryModel3D { Material = new PhongMaterial { DiffuseColor = new Color4(1, 1, 1, 1), AmbientColor = new Color4(0.30f, 0.30f, 0.30f, 1), SpecularColor = new Color4(0.15f, 0.15f, 0.15f, 1), SpecularShininess = 30f, VertexColorBlendingFactor = 1f }, CullMode = SharpDX.Direct3D11.CullMode.None };
            Vp.Items.Add(surface);
            amb = new AmbientLight3D { Color = Color.FromRgb(90, 90, 90) }; l1 = new DirectionalLight3D { Direction = new Vector3D(-1, 1, -1.4), Color = Colors.White }; l2 = new DirectionalLight3D { Direction = new Vector3D(1, -0.6, -0.8), Color = Color.FromRgb(150, 160, 175) };
            Vp.Items.Add(amb); Vp.Items.Add(l1); Vp.Items.Add(l2);
        }
        surface.Geometry = geo; surface.RenderWireframe = opt.Wireframe; surface.WireframeColor = Color.FromRgb(235, 240, 245);
        BuildAxes(m, zs); UpdateBounds(m, zs);
        Message.Visibility = Visibility.Collapsed;
        MinText.Text = Fmt(opt.ColorLo); MaxText.Text = Fmt(opt.ColorHi);
        var bar = new WriteableBitmap(256, 1, 96, 96, PixelFormats.Bgra32, null); bar.WritePixels(new Int32Rect(0, 0, 256, 1), lut, 256 * 4, 0); Bar.Source = bar;
    }

    void UpdateBounds(SurfaceMeshData m, double zs)
    {
        int n = m.N; double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        for (int iy = 0; iy < n; iy += Math.Max(1, n / 128)) for (int ix = 0; ix < n; ix += Math.Max(1, n / 128)) if (!float.IsNaN(m.Value[iy * n + ix])) { var (x, y) = m.Position(ix, iy); xmin = Math.Min(xmin, x); xmax = Math.Max(xmax, x); ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y); }
        if (xmin > xmax) return; double z0 = m.MinNm * zs, z1 = m.MaxNm * zs;
        sceneCenter = new Point3D((xmin + xmax) / 2, (ymin + ymax) / 2, (z0 + z1) / 2);
        sceneRadius = 0.5 * Math.Sqrt((xmax - xmin) * (xmax - xmin) + (ymax - ymin) * (ymax - ymin) + (z1 - z0) * (z1 - z0)) * 1.25;   // + margin for axes / labels
    }

    static string Fmt(double v) => Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);

    void BuildAxes(SurfaceMeshData m, double zs)
    {
        if (frame != null) { Vp.Items.Remove(frame); frame = null; }
        if (labels != null) { Vp.Items.Remove(labels); labels = null; }
        if (!opt.ShowAxes) return;
        int n = m.N; double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        for (int iy = 0; iy < n; iy += Math.Max(1, n / 256)) for (int ix = 0; ix < n; ix += Math.Max(1, n / 256)) if (!float.IsNaN(m.Value[iy * n + ix])) { var (x, y) = m.Position(ix, iy); xmin = Math.Min(xmin, x); xmax = Math.Max(xmax, x); ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y); }
        if (xmin > xmax) return;
        var xt = SurfaceMesh.NiceTicks(xmin, xmax, 5); var yt = SurfaceMesh.NiceTicks(ymin, ymax, 5); var zt = SurfaceMesh.NiceTicks(m.MinNm, m.MaxNm, 5);
        double x0 = xt[0], x1 = xt[^1], y0 = yt[0], y1 = yt[^1], zb = m.MinNm * zs, zt1 = m.MaxNm * zs; if (zt.Length > 0) { zb = Math.Min(zb, zt[0] * zs); zt1 = Math.Max(zt1, zt[^1] * zs); }
        var lb = new LineBuilder(); Vector3 P(double x, double y, double z) => new((float)x, (float)y, (float)z);
        // floor rectangle, back vertical edges and a top rectangle outline
        lb.AddLine(P(x0, y0, zb), P(x1, y0, zb)); lb.AddLine(P(x1, y0, zb), P(x1, y1, zb)); lb.AddLine(P(x1, y1, zb), P(x0, y1, zb)); lb.AddLine(P(x0, y1, zb), P(x0, y0, zb));
        lb.AddLine(P(x0, y0, zb), P(x0, y0, zt1)); lb.AddLine(P(x1, y1, zb), P(x1, y1, zt1)); lb.AddLine(P(x0, y1, zb), P(x0, y1, zt1)); lb.AddLine(P(x1, y0, zb), P(x1, y0, zt1));
        double tk = 0.04 * (x1 - x0);
        var bt = new BillboardText3D(); var fg = new Color4(0.86f, 0.89f, 0.92f, 1f); var bg = new Color4(0, 0, 0, 0); float sc = 0.55f;
        void Lbl(string s, Vector3 p) => bt.TextInfo.Add(new HxTextInfo(s, p) { Foreground = fg, Background = bg, Scale = sc });
        foreach (var t in xt) { lb.AddLine(P(t, y0, zb), P(t, y0 - tk, zb)); Lbl(Fmt(t), P(t, y0 - 3 * tk, zb)); }
        foreach (var t in yt) { lb.AddLine(P(x0, t, zb), P(x0 - tk, t, zb)); Lbl(Fmt(t), P(x0 - 3.2 * tk, t, zb)); }
        foreach (var t in zt) { double z = t * zs; lb.AddLine(P(x0, y0, z), P(x0 - tk, y0 - tk, z)); Lbl(Fmt(t), P(x0 - 3.4 * tk, y0 - 3.4 * tk, z)); }
        Lbl("x (mm)", P((x0 + x1) / 2, y0 - 7 * tk, zb)); Lbl("y (mm)", P(x0 - 8 * tk, (y0 + y1) / 2, zb)); Lbl("OPD (nm)", P(x0 - 7 * tk, y0 - 7 * tk, zt1 + 2 * tk));
        frame = new LineGeometryModel3D { Geometry = lb.ToLineGeometry3D(), Color = Color.FromRgb(190, 198, 206), Thickness = 1.2 };
        labels = new BillboardTextModel3D { Geometry = bt };
        Vp.Items.Add(frame); Vp.Items.Add(labels);
    }

    /// <summary>Preset camera directions: Isometric, Top, Front, Side, Back. z is up.</summary>
    public void SetView(string name)
    {
        if (mesh == null) return;
        Vector3D dir = name switch { "Top" => new Vector3D(0, 0.0001, -1), "Front" => new Vector3D(0, 1, -0.02), "Side" => new Vector3D(-1, 0.0001, -0.02), "Back" => new Vector3D(0, -1, -0.02), _ => new Vector3D(-0.75, 1, -0.8) };
        var up = name == "Top" ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
        dir.Normalize(); double fov = 40, dist = sceneRadius / Math.Sin(fov / 2 * Math.PI / 180);
        Vp.Camera = new PerspectiveCamera { Position = sceneCenter - dir * dist, LookDirection = dir, UpDirection = up, FieldOfView = fov, NearPlaneDistance = dist * 0.01, FarPlaneDistance = dist * 10 };
    }

    /// <summary>Renders the current view to a PNG file.</summary>
    public void SavePng(string path, int width = 0, int height = 0)
    {
        BitmapSource bmp = width > 0 ? Vp.RenderBitmap(width, height) : Vp.RenderBitmap();
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(path); enc.Save(fs);
    }
}
