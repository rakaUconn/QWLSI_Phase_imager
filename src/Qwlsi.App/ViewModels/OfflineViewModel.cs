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
    [ObservableProperty] bool isRecomputing;
    /// <summary>Shown after a batch that was not exported automatically: nothing is on disk yet except the temporary viewer cache.</summary>
    [ObservableProperty] string batchNote = "";

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

    ReconOutcome? shown;
    /// <summary>Last 3 results in RAM, every processed result on disk (%LOCALAPPDATA%\QwlsiApp\cache, size-limited, removed on exit); missing ones are recomputed.</summary>
    readonly ResultStore store;
    readonly List<SummaryRow> summary = new(); CancellationTokenSource? cts;
    CancellationTokenSource? showCts; int showVersion; Task recomputeTask = Task.CompletedTask;
    /// <summary>The latest "show the selected frame" job (load from cache or recompute); awaited by the self-test.</summary>
    public Task ShowTask { get; private set; } = Task.CompletedTask;
    public ResultStore Store => store;

    public OfflineViewModel(MainViewModel m)
    {
        main = m; Cmap = Enum.TryParse<ColormapKind>(m.Settings.Colormap, out var ck) ? ck : ColormapKind.Viridis;
        store = new ResultStore(new ResultDiskCache(AppSettings.CacheDir, (long)Math.Max(64, m.Settings.CacheLimitMb) << 20));
        ExportFolder = SavedExportFolder(m.Settings.LastExportFolder) ?? AppSettings.DefaultOutputDir; WithReference = false; ReferencePath = m.Settings.LastReferencePath; SystemWavefrontPath = m.Settings.SystemWavefrontPath;
        ExportUnit = Export.Matrix.Unit; Decimals = Export.Matrix.Decimals;
    }

    partial void OnExportUnitChanged(Units value) { Export.Matrix.Unit = value; Decimals = value switch { Units.Nm => 3, Units.Um => 6, Units.M => 12, _ => 6 }; }
    partial void OnDecimalsChanged(int value) { Export.Matrix.Decimals = Math.Clamp(value, 0, 15); }
    partial void OnKindChanged(DisplayKind value) { RefreshMap(true); }
    partial void OnCmapChanged(ColormapKind value) { main.Settings.Colormap = value.ToString(); RefreshMap(false); }
    partial void OnDisplayUnitChanged(Units value) { RefreshMap(true); }
    bool suppressAutoRange;
    partial void OnAutoRangeChanged(bool value) { if (!suppressAutoRange) RefreshMap(value); }
    partial void OnSelectedFileChanged(FileItem? value) { ShowTask = ShowSelectedAsync(value); }

    /// <summary>A folder remembered from an earlier session, unless it is a temporary one (old builds / tests left those in settings.json).</summary>
    static string? SavedExportFolder(string? f)
    {
        if (string.IsNullOrWhiteSpace(f)) return null;
        try { var tmp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\'); if (System.IO.Path.GetFullPath(f).StartsWith(tmp, StringComparison.OrdinalIgnoreCase)) return null; } catch { }
        return f;
    }

    /// <summary>Called on exit: stops background work and deletes the temporary result cache.</summary>
    public void Shutdown() { try { showCts?.Cancel(); cts?.Cancel(); } catch { } store.Dispose(); }

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

    [RelayCommand] void ClearList() { if (IsRunning) return; showCts?.Cancel(); Files.Clear(); store.Clear(); BatchNote = ""; summary.Clear(); SelectedFile = null; ClearView("No result yet."); }

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
        showCts?.Cancel(); await recomputeTask;      // a recompute for the viewer must not run beside the batch (memory)
        if (main.IsBusy) return;
        if (WithReference && (string.IsNullOrEmpty(ReferencePath) || !File.Exists(ReferencePath))) { main.StatusMessage = "With-reference mode: choose a reference frame."; return; }
        if (!WithReference && main.Current == null) { main.AutoFindCalibration(CurrentImageFolder, true); if (main.Current == null) { main.StatusMessage = "No calibration loaded: use the Calibration tab (browse / auto-find / make from a flat frame)."; main.SelectedTab = 3; return; } }
        main.IsBusy = true; IsRunning = true; CanCancel = true; cts = new CancellationTokenSource(); var ct = cts.Token;
        foreach (var it in items) { it.Status = "queued"; it.Power = it.Rms = it.Seconds = ""; }
        summary.Clear(); BatchNote = ""; main.Settings.Save();
        AppLog.Info($"batch start: {items.Count} frame(s), {(WithReference ? "with reference " + ReferencePath : "without reference")}, lambda {main.Settings.Params.Lambda * 1e9:G6} nm");
        var session = MakeSession();
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
                store.Add(session.CacheKey(oc.File), oc);      // disk + last-3 RAM: every processed frame stays viewable
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
        if (!auto && rows.Any(r => r.Error == ""))
        {
            BatchNote = $"Nothing has been saved yet. The results are only shown here (kept in a temporary cache that is deleted when you quit). Use 'Export' / 'Save summary' below, or tick auto-export before processing. Output folder: {folder}";
            main.StatusMessage += "  — nothing saved yet (auto-export is off).";
        }
        Processor.TrimProcessMemory();
        // show the last processed frame (the list selection would otherwise stay on frame 1)
        var last = items.LastOrDefault(i => i.Status == "done");
        if (last != null) { if (SelectedFile != last) SelectedFile = last; else ShowTask = ShowSelectedAsync(last); await ShowTask; }
    }

    /// <summary>Same settings the batch uses; also used to compute cache keys and to recompute a missing result. Holds no unmanaged state until a frame is processed.</summary>
    ReconSession MakeSession() => new() { Par = main.Settings.Params.Clone(), Calibration = main.Current, CalibrationPath = main.CurrentPath, ReferencePath = WithReference ? ReferencePath : null, SystemWavefrontPath = WithReference ? null : SystemWavefrontPath };

    void FillRow(FileItem it, ReconOutcome oc)
    {
        var st = oc.Result.Stats; it.Status = "done"; it.Power = st.PowerD.ToString("+0.000;-0.000", Inv) + " D"; it.Rms = st.RmsNm.ToString("F0", Inv) + " nm"; it.Seconds = oc.Seconds.ToString("F1", Inv) + " s";
    }

    void OnResult(FileItem it, ReconOutcome oc)
    {
        FillRow(it, oc); var st = oc.Result.Stats;
        LastPeakMb = oc.PeakWorkingSetMb;
        AppState.LastCarriers = $"fa=[{oc.CarriersUsed.Fa[0]:R}, {oc.CarriersUsed.Fa[1]:R}] fb=[{oc.CarriersUsed.Fb[0]:R}, {oc.CarriersUsed.Fb[1]:R}] ({oc.Mode})";
        AppState.LastResult = $"{it.Name}: power {st.PowerD:+0.000;-0.000} D, astig {st.AstigRmsNm:F1} nm, rms {st.RmsNm:F1} nm, PV {st.PvUm:F3} um, {oc.Seconds:F1} s, peak {oc.PeakWorkingSetMb:F0} MB ({oc.Mode})";
        AppLog.Info("processed " + AppState.LastResult);
        // live preview: the frame that is selected is shown as soon as it is done (the last frame is selected at the end of the batch)
        if (SelectedFile == null) SelectedFile = it; else if (SelectedFile == it) { showVersion++; ShowOutcome(oc); }
    }

    void ClearView(string message)
    {
        shown = null; Spec = null; StatsText = message; WarningText = ""; HasWarning = false; InfoText = ""; VminText = VmaxText = ""; ResultShown?.Invoke(null);
    }

    /// <summary>
    /// Shows the result of a list row. Order: RAM (last 3) -> disk cache -> recompute with a progress indicator. Processed rows are never "lost";
    /// rows that were not processed say so. The Zernike and 3-D tabs follow through <see cref="ResultShown"/> / <see cref="ResultPending"/>.
    /// </summary>
    async Task ShowSelectedAsync(FileItem? it)
    {
        showCts?.Cancel(); int v = ++showVersion; IsRecomputing = false;
        if (it == null) { ClearView("No result yet."); return; }
        if (it.Status != "done") { ClearView(it.Status.StartsWith("error") ? "Processing this frame failed: " + it.Status : it.Status is "pending" ? "Not processed yet." : $"Not processed yet ({it.Status})."); return; }
        string key = MakeSession().CacheKey(it.Path);
        var hit = await Task.Run(() => store.Find(key)); if (v != showVersion) return;
        if (hit != null) { ShowOutcome(hit); return; }
        if (main.IsBusy) { ClearView("This result is not cached any more and the app is busy — select the frame again when the current job has finished."); return; }
        var myCts = showCts = new CancellationTokenSource(); var prev = recomputeTask; var task = RecomputeAsync(it, key, v, prev, myCts.Token); recomputeTask = task; await task;
    }

    async Task RecomputeAsync(FileItem it, string key, int v, Task prev, CancellationToken ct)
    {
        try { await prev; } catch { }
        if (v != showVersion || ct.IsCancellationRequested) return;
        string msg = $"Recomputing {it.Name} (its result is not in the cache any more)…";
        ClearView(msg); ResultPending?.Invoke(msg); IsRecomputing = true; Progress = 0; ProgressText = msg; main.StatusMessage = msg; AppLog.Info("recompute: " + it.Path);
        var prog = new Progress<double>(p => { if (v == showVersion) { Progress = p; main.StatusMessage = $"Recomputing {it.Name}: {p * 100:F0} %"; } });
        var session = MakeSession();
        try
        {
            var (oc, _) = await store.GetOrComputeAsync(key, c => session.ProcessFile(it.Path, prog, c), ct);
            if (v != showVersion) return;
            FillRow(it, oc); LastPeakMb = oc.PeakWorkingSetMb; ShowOutcome(oc); main.StatusMessage = $"Recomputed {it.Name} in {oc.Seconds:F1} s."; ProgressText = main.StatusMessage; Progress = 1;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (v == showVersion) { ClearView("Recompute failed: " + e.Message); main.StatusMessage = "Recompute failed: " + e.Message; } AppLog.Error("recompute failed: " + it.Path, e); }
        finally { session.Dispose(); if (v == showVersion) IsRecomputing = false; }
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
        if (!AutoRange) { suppressAutoRange = true; AutoRange = true; suppressAutoRange = false; }   // a new frame starts with the automatic colour range
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
        return AppSettings.DefaultOutputDir;
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
        if (err == null) BatchNote = "";
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
    /// <summary>Raised on the UI thread with a message while a missing result is being recomputed (the other tabs show it instead of a stale map).</summary>
    public event Action<string>? ResultPending;
}
