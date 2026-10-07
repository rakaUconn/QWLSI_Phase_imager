using System.IO;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Qwlsi.App.Controls;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

public enum SurfaceSource { Raw, ZernikeFiltered }

/// <summary>Settings and mesh preparation for the 3-D tab (rendering itself is in <see cref="Surface3DView"/>).</summary>
public partial class Surface3DViewModel : ObservableObject
{
    readonly MainViewModel main; static readonly CultureInfo Inv = CultureInfo.InvariantCulture; int version;
    public IEnumerable<SurfaceSource> Sources { get; } = Enum.GetValues<SurfaceSource>();
    public IEnumerable<MeshResolution> Resolutions { get; } = new[] { MeshResolution.N512, MeshResolution.N256, MeshResolution.Full };
    public IEnumerable<string> ColorModes { get; } = new[] { "Auto", "Manual", "Percentile" };
    public IEnumerable<ColormapKind> Colormaps { get; } = Enum.GetValues<ColormapKind>();

    [ObservableProperty] SurfaceSource source = SurfaceSource.Raw;
    [ObservableProperty] MeshResolution resolution = MeshResolution.N512;
    [ObservableProperty] double exaggerationLog = 3;            // log10 of the factor
    [ObservableProperty] string exaggerationText = "";
    [ObservableProperty] string colorMode = "Percentile";
    [ObservableProperty] string manualMin = "-3000", manualMax = "3000";
    [ObservableProperty] double pctLo = 1, pctHi = 99;
    [ObservableProperty] ColormapKind cmap = ColormapKind.Viridis;
    [ObservableProperty] bool wireframe;
    [ObservableProperty] bool showAxes = true;
    [ObservableProperty] bool software;
    [ObservableProperty] string info = "";
    [ObservableProperty] bool busy;

    public SurfaceMeshData? Mesh { get; private set; }
    public SceneOptions Options { get; private set; } = new();
    /// <summary>Raised on the UI thread: (full rebuild of the mesh, reset camera).</summary>
    public event Action<bool, bool>? SceneChanged;
    public event Action<string>? ViewRequested; public event Action<string>? ScreenshotRequested;

    ReconOutcome? outcome; bool autoExagDone;

    public Surface3DViewModel(MainViewModel m)
    {
        main = m; Cmap = main.Offline.Cmap;
        m.Offline.ResultShown += oc => { outcome = oc; autoExagDone = false; _ = BuildAsync(true); };
        m.ZernikeVm.RemovalChanged += () => { if (Source == SurfaceSource.ZernikeFiltered) _ = BuildAsync(false); };
        UpdateExagText();
    }

    double Factor => Math.Pow(10, ExaggerationLog);
    void UpdateExagText() => ExaggerationText = $"×{Factor:N0}  (1 = true scale: OPD in nm vs lateral mm)";
    partial void OnSourceChanged(SurfaceSource value) { _ = BuildAsync(false); }
    partial void OnResolutionChanged(MeshResolution value) { _ = BuildAsync(false); }
    partial void OnExaggerationLogChanged(double value) { UpdateExagText(); PushOptions(); }
    partial void OnColorModeChanged(string value) { PushOptions(); }
    partial void OnManualMinChanged(string value) { if (ColorMode == "Manual") PushOptions(); }
    partial void OnManualMaxChanged(string value) { if (ColorMode == "Manual") PushOptions(); }
    partial void OnPctLoChanged(double value) { if (ColorMode == "Percentile") PushOptions(); }
    partial void OnPctHiChanged(double value) { if (ColorMode == "Percentile") PushOptions(); }
    partial void OnCmapChanged(ColormapKind value) { PushOptions(); }
    partial void OnWireframeChanged(bool value) { PushOptions(); }
    partial void OnShowAxesChanged(bool value) { PushOptions(); }

    SceneOptions MakeOptions()
    {
        var o = new SceneOptions { Exaggeration = Factor, Cmap = Cmap, Wireframe = Wireframe, ShowAxes = ShowAxes };
        if (Mesh != null)
        {
            double.TryParse(ManualMin, NumberStyles.Float, Inv, out var a); double.TryParse(ManualMax, NumberStyles.Float, Inv, out var b);
            var (lo, hi) = SurfaceMesh.ColorRange(Mesh, ColorMode, a, b, PctLo, PctHi); o.ColorLo = lo; o.ColorHi = hi;
        }
        return o;
    }

    void PushOptions() { if (Mesh == null) return; Options = MakeOptions(); SceneChanged?.Invoke(false, false); }

    async Task BuildAsync(bool resetCamera)
    {
        int v = ++version; var oc = outcome;
        if (oc == null) { Mesh = null; Info = ""; SceneChanged?.Invoke(true, false); return; }
        float[] opd = oc.Result.Opd; if (Source == SurfaceSource.ZernikeFiltered && main.ZernikeVm.Current != null) opd = main.ZernikeVm.Current.Filtered;
        int M = oc.Result.M; double pitch = main.Settings.Params.PitchM; var res = Resolution; Busy = true; var sw = Stopwatch.StartNew();
        var mesh = await Task.Run(() => { var nm = new float[opd.Length]; for (int i = 0; i < nm.Length; i++) nm[i] = opd[i] * 1e9f; return SurfaceMesh.Build(nm, M, pitch, res); });
        if (v != version) return; Mesh = mesh; Busy = false;
        if (!autoExagDone) { ExaggerationLog = Math.Clamp(Math.Log10(SurfaceMesh.AutoExaggeration(mesh)), 0, 5); autoExagDone = true; }
        Options = MakeOptions();
        Info = $"{mesh.N}×{mesh.N} mesh, {mesh.ValidVertices:N0} valid vertices, {mesh.Triangles.Length / 3:N0} triangles, cell {mesh.CellMm * 1e3:F1} µm, built in {sw.ElapsedMilliseconds} ms\nOPD range {mesh.MinNm:F0} … {mesh.MaxNm:F0} nm ({(Source == SurfaceSource.Raw ? "raw" : "Zernike-filtered")})";
        SceneChanged?.Invoke(true, resetCamera);
    }

    [RelayCommand] void AutoExaggeration() { if (Mesh != null) ExaggerationLog = Math.Clamp(Math.Log10(SurfaceMesh.AutoExaggeration(Mesh)), 0, 5); }
    [RelayCommand] void SetView(string name) => ViewRequested?.Invoke(name);
    [RelayCommand]
    void Screenshot()
    {
        var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = (outcome != null ? Path.GetFileNameWithoutExtension(outcome.File) : "surface") + "_3d.png" }; if (dlg.ShowDialog() != true) return;
        ScreenshotRequested?.Invoke(dlg.FileName);
    }
    public void NotifyFallback(string msg) { Software = true; Info = msg + "\n" + Info; AppLog.Warn("3-D renderer fallback: " + msg); }
}
