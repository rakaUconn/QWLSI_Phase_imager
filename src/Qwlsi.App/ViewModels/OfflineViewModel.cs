using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Qwlsi.App.Controls;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

public partial class FileItem : ObservableObject
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    [ObservableProperty] string status = "pending";
    [ObservableProperty] string power = "";
    [ObservableProperty] string rms = "";
    [ObservableProperty] string seconds = "";
    public FileItem(string path) { Path = path; }
}

public enum DisplayKind { OPD, Intensity, Mask, Amplitude, Phase }

public partial class OfflineViewModel : ObservableObject
{
    readonly MainViewModel main;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ObservableCollection<FileItem> Files { get; } = new();
    [ObservableProperty] FileItem? selectedFile;

    // mode
    [ObservableProperty] bool withReference;
    [ObservableProperty] string? referencePath;
    [ObservableProperty] string? systemWavefrontPath;
    [ObservableProperty] bool includeSubfolders;

    // run state
    [ObservableProperty] double progress;
    [ObservableProperty] string progressText = "";
    [ObservableProperty] bool canCancel;
    [ObservableProperty] bool isRunning;

    // display
    [ObservableProperty] DisplayKind kind = DisplayKind.OPD;
    [ObservableProperty] ColormapKind cmap = ColormapKind.Viridis;
    [ObservableProperty] Units displayUnit = Units.Nm;
    [ObservableProperty] bool autoRange = true;
    [ObservableProperty] string vminText = "", vmaxText = "";
    [ObservableProperty] MapSpec? spec;
    [ObservableProperty] string statsText = "No result yet.";
    [ObservableProperty] string warningText = "";
    [ObservableProperty] bool hasWarning;
    [ObservableProperty] string infoText = "";

    // export
    [ObservableProperty] string exportFolder = "";
    [ObservableProperty] string exportMessage = "";
    [ObservableProperty] int decimals = 3;
    [ObservableProperty] Units exportUnit = Units.Nm;
    public ExportOptions Export => main.Settings.Export;

    public IEnumerable<DisplayKind> Kinds { get; } = Enum.GetValues<DisplayKind>();
    public IEnumerable<ColormapKind> Colormaps { get; } = Enum.GetValues<ColormapKind>();
    public IEnumerable<Units> UnitChoices { get; } = Enum.GetValues<Units>();
    public IEnumerable<Delimiter> Delimiters { get; } = Enum.GetValues<Delimiter>();
    public IEnumerable<FieldLayout> FieldLayouts { get; } = Enum.GetValues<FieldLayout>();

    public double LastPeakMb { get; private set; }
    public string? CurrentImageFolder => SelectedFile != null ? System.IO.Path.GetDirectoryName(SelectedFile.Path) : Files.Count > 0 ? System.IO.Path.GetDirectoryName(Files[0].Path) : null;

    ReconOutcome? shown; readonly List<(string path, ReconOutcome oc)> cache = new(); const int CacheSize = 3;
    readonly List<SummaryRow> summary = new(); CancellationTokenSource? cts;

    public OfflineViewModel(MainViewModel m)
    {
        main = m; Cmap = Enum.TryParse<ColormapKind>(m.Settings.Colormap, out var ck) ? ck : ColormapKind.Viridis;
        ExportFolder = m.Settings.LastExportFolder ?? ""; WithReference = false; ReferencePath = m.Settings.LastReferencePath; SystemWavefrontPath = m.Settings.SystemWavefrontPath;
        ExportUnit = Export.Matrix.Unit; Decimals = Export.Matrix.Decimals;
    }

    partial void OnExportUnitChanged(Units value) { Export.Matrix.Unit = value; Decimals = value switch { Units.Nm => 3, Units.Um => 6, Units.M => 12, _ => 6 }; }
    partial void OnDecimalsChanged(int value) { Export.Matrix.Decimals = Math.Clamp(value, 0, 15); }
    partial void OnKindChanged(DisplayKind value) { RefreshMap(true); }
    partial void OnCmapChanged(ColormapKind value) { main.Settings.Colormap = value.ToString(); RefreshMap(false); }
    partial void OnDisplayUnitChanged(Units value) { RefreshMap(true); }
    partial void OnAutoRangeChanged(bool value) { RefreshMap(value); }
    partial void OnSelectedFileChanged(FileItem? value) { if (value != null) ShowCached(value); }

    // ------------------------------------------------------------------------------------------------ files
    static bool IsTiff(string p) => p.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { foreach (var f in Directory.EnumerateFiles(p, "*.*", IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(IsTiff).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) AddOne(f); }
            else if (File.Exists(p) && IsTiff(p)) AddOne(p);
        }
        if (SelectedFile == null && Files.Count > 0) SelectedFile = Files[0];
        if (Files.Count > 0) { main.Settings.LastImageFolder = System.IO.Path.GetDirectoryName(Files[0].Path); if (main.Current == null) main.AutoFindCalibration(CurrentImageFolder, true); main.StatusMessage = $"{Files.Count} frame(s) in the list."; }
    }

    void AddOne(string f) { if (!Files.Any(x => string.Equals(x.Path, f, StringComparison.OrdinalIgnoreCase))) Files.Add(new FileItem(f)); }

    [RelayCommand]
    void OpenFiles()
    {
        var dlg = new OpenFileDialog { Filter = "TIFF frames|*.tif;*.tiff|All files|*.*", Multiselect = true, Title = "Open raw QWLSI frame(s)", InitialDirectory = Dir(main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    [RelayCommand]
    void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Open a folder of raw frames", InitialDirectory = Dir(main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() == true) AddPaths(new[] { dlg.FolderName });
    }

    [RelayCommand] void ClearList() { if (IsRunning) return; Files.Clear(); cache.Clear(); shown = null; Spec = null; StatsText = "No result yet."; summary.Clear(); SelectedFile = null; }

    [RelayCommand]
    void BrowseReference()
    {
        var dlg = new OpenFileDialog { Filter = "TIFF frames|*.tif;*.tiff", Title = "Reference (flat) frame", InitialDirectory = Dir(ReferencePath ?? main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() != true) return; ReferencePath = dlg.FileName; main.Settings.LastReferencePath = ReferencePath; WithReference = true; main.Settings.Save();
    }

    [RelayCommand] void ClearReference() { ReferencePath = null; WithReference = false; }

    [RelayCommand]
    void BrowseSystemWavefront()
    {
        var dlg = new OpenFileDialog { Filter = "System wavefront (.npy, OPD in metres, NaN outside)|*.npy", Title = "System wavefront file", InitialDirectory = Dir(SystemWavefrontPath ?? main.Settings.LastImageFolder) };
        if (dlg.ShowDialog() != true) return; SystemWavefrontPath = dlg.FileName; main.Settings.SystemWavefrontPath = SystemWavefrontPath; main.Settings.Save();
    }

    [RelayCommand] void ClearSystemWavefront() { SystemWavefrontPath = null; main.Settings.SystemWavefrontPath = null; main.Settings.Save(); }

    // ------------------------------------------------------------------------------------------------ processing
    [RelayCommand] Task ProcessAll() => Run(Files.ToList());
    [RelayCommand] Task ProcessSelected() => SelectedFile == null ? Task.CompletedTask : Run(new List<FileItem> { SelectedFile });
    [RelayCommand] void Cancel() { cts?.Cancel(); ProgressText = "Cancelling…"; }

    async Task Run(List<FileItem> items)
    {
        if (items.Count == 0) { main.StatusMessage = "Open a frame or folder first."; return; }
        if (main.IsBusy) return;
        if (WithReference && (string.IsNullOrEmpty(ReferencePath) || !File.Exists(ReferencePath))) { main.StatusMessage = "With-reference mode: choose a reference frame."; return; }
        if (!WithReference && main.Current == null) { main.AutoFindCalibration(CurrentImageFolder, true); if (main.Current == null) { main.StatusMessage = "No calibration loaded: use the Calibration tab (browse / auto-find / make from a flat frame)."; main.SelectedTab = 3; return; } }
        main.IsBusy = true; IsRunning = true; CanCancel = true; cts = new CancellationTokenSource(); var ct = cts.Token;
        foreach (var it in items) { it.Status = "queued"; it.Power = it.Rms = it.Seconds = ""; }
        summary.Clear(); main.Settings.Save();
        AppLog.Info($"batch start: {items.Count} frame(s), {(WithReference ? "with reference " + ReferencePath : "without reference")}, lambda {main.Settings.Params.Lambda * 1e9:G6} nm");
        var session = new ReconSession { Par = main.Settings.Params.Clone(), Calibration = main.Current, CalibrationPath = main.CurrentPath, ReferencePath = WithReference ? ReferencePath : null, SystemWavefrontPath = WithReference ? null : SystemWavefrontPath };
        bool filtered = main.Settings.ExportFilteredOpd; var zsel = main.ZernikeVm.Selection(); var paths = items.Select(i => i.Path).ToList(); var dispatcher = Application.Current.Dispatcher; var export = Export; bool auto = main.Settings.AutoExportBatch; string folder = EffectiveExportFolder();
        var prog = new Progress<BatchProgress>(bp =>
        {
            var it = items[bp.Index]; AppState.CurrentFile = bp.File; if (bp.State == "processing") it.Status = bp.FileFraction <= 0 && WithReference && bp.Index == 0 ? "building reference…" : $"processing {bp.FileFraction * 100:F0} %";
            Progress = (bp.Index + Math.Clamp(bp.FileFraction, 0, 1)) / bp.Count; ProgressText = $"File {bp.Index + 1}/{bp.Count} — {System.IO.Path.GetFileName(bp.File)} — {bp.State}" + (bp.State == "processing" ? $" {bp.FileFraction * 100:F0} %" : "");
        });
        List<SummaryRow> rows = new(); var errors = new List<string>(); var sw = Stopwatch.StartNew();
        try
        {
            rows = await Task.Run(() => BatchRunner.Run(session, paths, oc =>
            {
                var it = items[paths.IndexOf(oc.File)];
                if (auto) { try { var zr = filtered ? main.ZernikeVm.MakeFor(oc, zsel, session.Par) : null; ResultExporter.Export(oc, session.Par, export, folder, null, (f, r) => dispatcher.Invoke(() => SavePng(f, oc)), zr); } catch (Exception e) { dispatcher.Invoke(() => ExportMessage = "Auto-export failed: " + e.Message); } }
                dispatcher.Invoke(() => OnResult(it, oc));
            }, (f, e) => dispatcher.Invoke(() => { var it = items[paths.IndexOf(f)]; it.Status = "error: " + e.Message; errors.Add(it.Name + ": " + e.Message); AppLog.Error("processing failed: " + f, e); }), prog, ct));
        }
        catch (Exception e) { main.StatusMessage = "Batch failed: " + e.Message; }
        finally { session.Dispose(); }
        foreach (var it in items) if (it.Status is "queued" or "building reference…" || it.Status.StartsWith("processing")) it.Status = "cancelled";
        summary.AddRange(rows);
        if (auto && rows.Count > 0) try { Directory.CreateDirectory(folder); SummaryCsv.Write(System.IO.Path.Combine(folder, "summary.csv"), rows); } catch { }
        AppState.CurrentFile = null; IsRunning = false; CanCancel = false; main.IsBusy = false; Progress = ct.IsCancellationRequested ? Progress : 1;
        ProgressText = ct.IsCancellationRequested ? "Cancelled." : $"Done: {rows.Count(r => r.Error == "")}/{items.Count} frame(s) in {sw.Elapsed.TotalSeconds:F1} s" + (errors.Count > 0 ? $", {errors.Count} error(s)" : "");
        main.StatusMessage = ProgressText + (auto ? $"  Exports in {folder}" : "");
        Processor.TrimProcessMemory();
    }

    void OnResult(FileItem it, ReconOutcome oc)
    {
        var st = oc.Result.Stats; it.Status = "done"; it.Power = st.PowerD.ToString("+0.000;-0.000", Inv) + " D"; it.Rms = st.RmsNm.ToString("F0", Inv) + " nm"; it.Seconds = oc.Seconds.ToString("F1", Inv) + " s";
        LastPeakMb = oc.PeakWorkingSetMb;
        AppState.LastCarriers = $"fa=[{oc.CarriersUsed.Fa[0]:R}, {oc.CarriersUsed.Fa[1]:R}] fb=[{oc.CarriersUsed.Fb[0]:R}, {oc.CarriersUsed.Fb[1]:R}] ({oc.Mode})";
        AppState.LastResult = $"{it.Name}: power {st.PowerD:+0.000;-0.000} D, astig {st.AstigRmsNm:F1} nm, rms {st.RmsNm:F1} nm, PV {st.PvUm:F3} um, {oc.Seconds:F1} s, peak {oc.PeakWorkingSetMb:F0} MB ({oc.Mode})";
        AppLog.Info("processed " + AppState.LastResult);
        cache.RemoveAll(c => c.path == it.Path); cache.Add((it.Path, oc)); while (cache.Count > CacheSize) cache.RemoveAt(0);
        if (SelectedFile == null || SelectedFile == it || Files.Count(f => f.Status == "done") == 1) { if (SelectedFile != it) SelectedFile = it; else ShowOutcome(oc); }
    }

    void ShowCached(FileItem it)
    {
        var c = cache.FirstOrDefault(x => x.path == it.Path); if (c.oc != null) { ShowOutcome(c.oc); return; }
        shown = null; Spec = null; ResultShown?.Invoke(null); StatsText = it.Status == "done" ? "Result no longer cached (only the last " + CacheSize + " are kept in memory to save RAM) — press 'Process selected' to recompute." : "Not processed yet."; WarningText = ""; HasWarning = false; InfoText = "";
    }

    void ShowOutcome(ReconOutcome oc)
    {
        shown = oc; var s = oc.Result.Stats; var t = oc.Result.TimingsMs;
        StatsText = string.Join("\n", new[]
        {
            $"Lens power     {s.PowerD.ToString("+0.000;-0.000", Inv)} D   (+ = concave / bowl)",
            $"Astigmatism    {s.AstigRmsNm:F1} nm rms", $"Coma           {s.ComaRmsNm:F1} nm rms", $"Spherical      {s.SphericalRmsNm:F1} nm rms",
            $"RMS            {s.RmsNm:F1} nm", $"PV             {s.PvUm:F3} µm", $"Pupil radius   {s.PupilRadiusMm:F3} mm",
        });
        InfoText = $"Mode: {oc.Mode}\nλ = {main.Settings.Params.Lambda * 1e9:G6} nm, grid {oc.Result.M}×{oc.Result.M}, pitch {main.Settings.Params.PitchM * 1e6:F3} µm\nCarriers fa = [{oc.CarriersUsed.Fa[0]:F6}, {oc.CarriersUsed.Fa[1]:F6}]  fb = [{oc.CarriersUsed.Fb[0]:F6}, {oc.CarriersUsed.Fb[1]:F6}]\n"
            + $"CG iterations {string.Join("/", oc.Result.Solve.CgIterations)}, LS residual {oc.Result.Solve.ResidualRad[0]:F3}/{oc.Result.Solve.ResidualRad[1]:F3} rad\nTime {oc.Seconds:F2} s (solve {t.GetValueOrDefault("solve") / 1000:F2} s), peak memory {oc.PeakWorkingSetMb:F0} MB";
        WarningText = string.Join("\n", oc.Warnings); HasWarning = oc.Warnings.Count > 0;
        RefreshMap(true); ResultShown?.Invoke(oc);
    }

    // ------------------------------------------------------------------------------------------------ display
    (float[] data, string unit, float lo, float hi, ColormapKind? forced) Build(ReconOutcome oc, DisplayKind k)
    {
        var r = oc.Result; int M = r.M; long n = (long)M * M; var d = new float[n];
        switch (k)
        {
            case DisplayKind.OPD:
                { double sc = Writers.UnitScale(DisplayUnit, main.Settings.Params.Lambda); for (long i = 0; i < n; i++) d[i] = (float)(r.Opd[i] * sc); var (lo, hi) = Colormap.AutoRange(d); return (d, Writers.UnitName(DisplayUnit) + " (OPD)", lo, hi, null); }
            case DisplayKind.Intensity: for (long i = 0; i < n; i++) d[i] = r.Mask[i] != 0 ? r.Intensity[i] : float.NaN; return (d, "relative intensity", 0, 1.2f, null);
            case DisplayKind.Amplitude: for (long i = 0; i < n; i++) d[i] = r.Mask[i] != 0 ? MathF.Sqrt(r.Intensity[i]) : float.NaN; return (d, "|U| (normalised)", 0, 1.1f, null);
            case DisplayKind.Mask: for (long i = 0; i < n; i++) d[i] = r.Mask[i]; return (d, "mask", 0, 1, ColormapKind.Gray);
            default:
                { double lam = main.Settings.Params.Lambda; for (long i = 0; i < n; i++) { if (float.IsNaN(r.Opd[i])) { d[i] = float.NaN; continue; } double ph = 2 * Math.PI * r.Opd[i] / lam; d[i] = (float)Math.Atan2(Math.Sin(ph), Math.Cos(ph)); } return (d, "arg U [rad]", -MathF.PI, MathF.PI, ColormapKind.Twilight); }
        }
    }

    void RefreshMap(bool resetRange)
    {
        if (shown == null) return;
        var (data, unit, lo, hi, forced) = Build(shown, Kind);
        if (!resetRange && !AutoRange && float.TryParse(VminText, NumberStyles.Float, Inv, out var a) && float.TryParse(VmaxText, NumberStyles.Float, Inv, out var b)) { lo = a; hi = b; }
        else if (AutoRange || resetRange) { VminText = lo.ToString("0.###", Inv); VmaxText = hi.ToString("0.###", Inv); }
        Spec = new MapSpec { Data = data, Width = shown.Result.M, Height = shown.Result.M, Cmap = forced ?? Cmap, VMin = lo, VMax = hi, Unit = unit, Title = SelectedFile?.Name ?? "" };
    }

    [RelayCommand] void ApplyRange() { AutoRange = false; RefreshMap(false); }

    // ------------------------------------------------------------------------------------------------ export
    string EffectiveExportFolder()
    {
        if (!string.IsNullOrWhiteSpace(ExportFolder)) return ExportFolder;
        var f = CurrentImageFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); return System.IO.Path.Combine(f, "qwlsi_out");
    }

    [RelayCommand]
    void BrowseExportFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Export folder", InitialDirectory = Dir(ExportFolder) }; if (dlg.ShowDialog() != true) return;
        ExportFolder = dlg.FolderName; main.Settings.LastExportFolder = ExportFolder; main.Settings.Save();
    }

    void SavePng(string file, ReconOutcome oc)
    {
        var r = oc.Result; double sc = Writers.UnitScale(Units.Nm, main.Settings.Params.Lambda); var d = new float[r.Opd.Length]; for (int i = 0; i < d.Length; i++) d[i] = (float)(r.Opd[i] * sc);
        var (lo, hi) = Colormap.AutoRange(d); var s = r.Stats;
        PngPreview.Save(file, d, r.M, Cmap, lo, hi, "nm (OPD)", System.IO.Path.GetFileName(oc.File) + "  —  " + oc.Mode,
            $"power {s.PowerD.ToString("+0.000;-0.000", Inv)} D   astigmatism {s.AstigRmsNm:F0} nm   RMS {s.RmsNm:F0} nm   PV {s.PvUm:F2} µm   λ {main.Settings.Params.Lambda * 1e9:G6} nm");
    }

    [RelayCommand]
    async Task ExportCurrent() { await ExportShownAsync(main.Settings.ExportFilteredOpd); }

    public string ExportFolderPath() => EffectiveExportFolder();

    /// <summary>Exports the displayed result with the formats / units chosen in the export panel. filtered = OPD with the Zernike terms removed (Zernike tab selection). Returns (and shows) a message.</summary>
    public async Task<string> ExportShownAsync(bool filtered, bool forceOpd = false)
    {
        if (shown == null) return ExportMessage = "Nothing to export: process a frame first.";
        var zr = filtered ? main.ZernikeVm.Current : null; if (filtered && zr == null) return ExportMessage = "Zernike fit not ready yet.";
        string folder = EffectiveExportFolder(); var oc = shown; var par = main.Settings.Params.Clone(); var disp = Application.Current.Dispatcher; string? err = null; List<string> files = new();
        var o = Export; if (forceOpd && !o.Opd) { o = System.Text.Json.JsonSerializer.Deserialize<ExportOptions>(System.Text.Json.JsonSerializer.Serialize(Export))!; o.Opd = true; }
        ExportMessage = "Exporting…"; main.Settings.LastExportFolder = ExportFolder; main.Settings.Save();
        await Task.Run(() => { try { files = ResultExporter.Export(oc, par, o, folder, null, (f, r) => disp.Invoke(() => SavePng(f, oc)), zr); } catch (Exception e) { err = e.Message; } });
        ExportMessage = err != null ? "Export failed: " + err : $"Exported {files.Count} file(s) to {folder}\n" + string.Join("\n", files.Select(System.IO.Path.GetFileName));
        if (err != null) AppLog.Error("export failed: " + err); else AppLog.Info($"export: {files.Count} file(s) to {folder} ({(filtered ? "Zernike-filtered" : "raw")} OPD)");
        Processor.TrimProcessMemory(); return ExportMessage;
    }

    [RelayCommand]
    void OpenExportFolder() { var f = EffectiveExportFolder(); Directory.CreateDirectory(f); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = true }); }

    [RelayCommand]
    void SaveSummary()
    {
        if (summary.Count == 0) { ExportMessage = "No batch results yet."; return; }
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "summary.csv", InitialDirectory = Dir(EffectiveExportFolder()) }; if (dlg.ShowDialog() != true) return;
        SummaryCsv.Write(dlg.FileName, summary); ExportMessage = "Summary saved: " + dlg.FileName;
    }

    [RelayCommand]
    void SaveAsSystemWavefront()
    {
        if (shown == null) { ExportMessage = "Process a flat frame (without reference) first."; return; }
        var dlg = new SaveFileDialog { Filter = "NumPy|*.npy", FileName = System.IO.Path.GetFileNameWithoutExtension(shown.File) + "_system_OPD_m.npy", InitialDirectory = Dir(EffectiveExportFolder()) }; if (dlg.ShowDialog() != true) return;
        Writers.WriteNpyF32(dlg.FileName, shown.Result.Opd, shown.Result.M, shown.Result.M); SystemWavefrontPath = dlg.FileName; main.Settings.SystemWavefrontPath = dlg.FileName; main.Settings.Save();
        ExportMessage = "Saved system wavefront and selected it for 'without reference' mode: " + dlg.FileName;
    }

    static string? Dir(string? p) { try { if (string.IsNullOrEmpty(p)) return null; var d = Directory.Exists(p) ? p : System.IO.Path.GetDirectoryName(p); return d != null && Directory.Exists(d) ? d : null; } catch { return null; } }

    // used by the self-test
    public ReconOutcome? Shown => shown;
    /// <summary>Raised on the UI thread whenever a different result is displayed (Zernike / 3D tabs follow it).</summary>
    public event Action<ReconOutcome?>? ResultShown;
}
