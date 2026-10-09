using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Qwlsi.App.Controls;
using Qwlsi.Core;

namespace Qwlsi.App.ViewModels;

/// <summary>One row of the all-frames table: a frame, or the "mean" / "std" summary line (always last). Cells follow <see cref="ZernikeAllViewModel.ColumnDefs"/>.</summary>
public sealed class AllRow
{
    public string Name { get; init; } = ""; public int Number { get; init; } public double[] Cells { get; init; } = Array.Empty<double>(); public bool IsSummary { get; init; }
    public string NumberText => IsSummary ? "" : Number.ToString(CultureInfo.InvariantCulture);
    public double Sort(int col) => col >= 0 && col < Cells.Length ? Cells[col] : double.NaN;
}

/// <summary>A quantity of the trend plot: one Zernike term (ANSI j, nm) or rms before / after / astigmatism.</summary>
public sealed partial class TrendChoice : ObservableObject
{
    public string Name { get; init; } = ""; public int J { get; init; } = -1; public string Kind { get; init; } = "term";
    [ObservableProperty] bool isChecked;
}

/// <summary>
/// "All frames" part of the Zernike tab: one row per processed frame (selected terms [nm], power, astigmatism, rms/PV before and after the removal of the Zernike tab's selection),
/// sortable, with mean / std rows, CSV, a trend plot, the original / removed / filtered maps of the clicked frame, "apply to all frames" and "export all filtered maps".
/// Pupil: per-frame circle (default) or ONE common circle fitted to the intersection of all masks. Results are read frame by frame from the result cache.
/// </summary>
public partial class ZernikeAllViewModel : ObservableObject
{
    readonly MainViewModel main; CancellationTokenSource? cts; List<FileItem> items = new(); string tableSig = ""; ZernikeCircle? circleCache; string circleKey = "";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ObservableCollection<AllRow> Rows { get; } = new();
    public ObservableCollection<TrendChoice> TrendChoices { get; } = new();
    public ZernikeTable? Table { get; private set; }
    public IReadOnlyList<FileItem> Items => items;
    /// <summary>(header, format) of the numeric columns, in the order of AllRow.Cells.</summary>
    public List<(string header, string format)> ColumnDefs { get; private set; } = new();
    public event Action? TableBuilt;

    [ObservableProperty] bool commonCircle;
    [ObservableProperty] bool isBusy;
    [ObservableProperty] bool isStale;
    [ObservableProperty] double progress;
    [ObservableProperty] string progressText = "";
    [ObservableProperty] string message = "No table yet: process frames on the Offline tab, choose the terms on the left of the 'Single frame' page, then press 'Apply selected removal to all frames'.";
    [ObservableProperty] string circleInfo = "";
    [ObservableProperty] IReadOnlyList<TrendSeries>? trendSeries;
    [ObservableProperty] int markerFrame;
    [ObservableProperty] AllRow? selectedRow;
    [ObservableProperty] MapSpec? originalSpec;
    [ObservableProperty] MapSpec? removedSpec;
    [ObservableProperty] MapSpec? filteredSpec;
    [ObservableProperty] string mapsCaption = "Click a row (or a point of the trend plot) to see that frame's original, removed and filtered maps.";
    public string? SortHeader { get; private set; } public bool SortAscending { get; private set; } = true;
    public ZernikePupil Pupil => CommonCircle ? ZernikePupil.CommonCircle : ZernikePupil.PerFrame;
    public OfflineViewModel Offline => main.Offline;

    public ZernikeAllViewModel(MainViewModel m)
    {
        main = m;
        m.ZernikeVm.RemovalChanged += () => { if (Table != null) IsStale = Signature() != tableSig; };
        m.Offline.PropertyChanged += OnOfflineChanged;
    }

    partial void OnCommonCircleChanged(bool value) { if (Table != null) IsStale = true; }
    partial void OnSelectedRowChanged(AllRow? value) { if (value == null || value.IsSummary) return; SelectFrameNumber(value.Number); }

    string Signature() { var s = main.ZernikeVm.Selection(); var o = main.ZernikeVm.Orientation; return string.Concat(s.Select(b => b ? '1' : '0')) + (o.FlipX ? "x" : "-") + (o.FlipY ? "y" : "-") + (CommonCircle ? "c" : "f"); }

    void OnOfflineChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OfflineViewModel.SelectedFile) || Table == null) return;
        int i = main.Offline.SelectedFile == null ? -1 : items.FindIndex(f => ReferenceEquals(f, main.Offline.SelectedFile)); MarkerFrame = i + 1;
        if (i >= 0 && !(SelectedRow != null && SelectedRow.Number == i + 1)) { SelectedRow = Rows.FirstOrDefault(r => !r.IsSummary && r.Number == i + 1); }
    }

    // ------------------------------------------------------------------------------------------------ build
    /// <summary>The common circle for the given frames (null for the per-frame option). Cached until the frame set changes. Throws InvalidOperationException when the masks do not intersect.</summary>
    public ZernikeCircle? GetPupilCircle(IReadOnlyList<FileItem> files, CancellationToken ct = default)
    {
        if (!CommonCircle) return null; string key = string.Join("|", files.Select(f => f.Path)) + "#" + main.Settings.Params.Lambda;
        if (circleCache != null && key == circleKey) return circleCache;
        IEnumerable<byte[]> Masks() { foreach (var f in files) { ct.ThrowIfCancellationRequested(); var m = main.Offline.LoadMask(f, ct); if (m != null) yield return m; } }
        var (c, n) = ZernikeTools.CommonCircle(Masks(), main.Settings.Params.M); circleCache = c; circleKey = key; return c;
    }

    [RelayCommand]
    async Task ApplyAll() => await BuildAsync();

    async Task<bool> BuildAsync()
    {
        if (IsBusy) return false; var off = main.Offline;
        if (off.IsRunning || main.IsBusy) { Message = "Another job is running: wait for it to finish."; return false; }
        var files = off.ProcessedFiles(); if (files.Count == 0) { Message = "No processed frames: press 'Process all' on the Offline tab first."; return false; }
        var sel = main.ZernikeVm.Selection(); var o0 = main.ZernikeVm.Orientation; var orient = new ZernikeOrientation { FlipX = o0.FlipX, FlipY = o0.FlipY };
        double pitch = main.Settings.Params.PitchM; var pupil = Pupil; string sig = Signature();
        IsBusy = true; main.IsBusy = true; cts = new CancellationTokenSource(); var ct = cts.Token; Progress = 0; ProgressText = "Starting…"; Message = "";
        var prog = new Progress<(int done, int total)>(p => { Progress = p.total == 0 ? 0 : p.done / (double)p.total; ProgressText = $"Frame {p.done}/{p.total}"; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var loaders = off.FrameLoaders(files);
            var table = await Task.Run(() => ZernikeTableBuilder.Build(loaders, sel, orient, pupil, pitch, prog, ct));
            Table = table; items = files; tableSig = sig; circleCache = table.CommonCircle; circleKey = table.CommonCircle != null ? string.Join("|", files.Select(f => f.Path)) + "#" + main.Settings.Params.Lambda : "";
            FillRows(); IsStale = false;
            CircleInfo = table.CommonCircle != null ? $"Common circle: centre ({table.CommonCircle.Cx:F1}, {table.CommonCircle.Cy:F1}) px, radius {table.CommonCircle.RadiusPx:F1} px = {table.CommonCircle.RadiusPx * pitch * 1e3:F3} mm, fitted to the intersection of all masks ({table.CommonCirclePixels:N0} px)." : "Per-frame circle: each frame's own mask-fitted circle (centroid, radius √(area/π)).";
            Message = $"{table.Rows.Count} frame(s) in {sw.Elapsed.TotalSeconds:F1} s — {table.Convention}; removed: {string.Join(", ", Enumerable.Range(0, sel.Length).Where(j => sel[j]).Select(j => "Z" + j))}.";
            AppLog.Info("zernike all-frames table: " + Message);
            TableBuilt?.Invoke(); BuildTrendChoices(sel);
            int cur = off.SelectedFile == null ? -1 : items.FindIndex(f => ReferenceEquals(f, off.SelectedFile)); MarkerFrame = cur + 1;
            return true;
        }
        catch (OperationCanceledException) { Message = "Cancelled."; return false; }
        catch (Exception e) { Message = "Failed: " + e.Message; AppLog.Error("zernike all-frames table failed", e); return false; }
        finally { IsBusy = false; main.IsBusy = false; ProgressText = ""; Processor.TrimProcessMemory(); }
    }

    [RelayCommand] void Cancel() { cts?.Cancel(); ProgressText = "Cancelling…"; }

    void FillRows()
    {
        var t = Table!; ColumnDefs = t.Columns.Select(c => (c.Header, Format(c.Header))).ToList(); AllRowsSource = new();
        foreach (var r in t.Rows) AllRowsSource.Add(new AllRow { Name = r.Name, Number = r.Number, Cells = t.Columns.Select(c => c.Get(r)).ToArray() });
        AllRowsSource.Add(new AllRow { Name = "mean", Number = 0, Cells = (double[])t.Mean.Clone(), IsSummary = true }); AllRowsSource.Add(new AllRow { Name = "std", Number = 0, Cells = (double[])t.Std.Clone(), IsSummary = true });
        SortHeader = null; Resort();
    }

    List<AllRow> AllRowsSource = new();
    static string Format(string h) => h.StartsWith("Z") ? "0.00" : h == "power_D" ? "0.0000" : h == "astig_axis_deg" ? "0.0" : h.StartsWith("pv_") ? "0.000" : h.StartsWith("rms_") ? "0.0" : "0.00";

    // ------------------------------------------------------------------------------------------------ sorting (summary rows always stay last)
    /// <summary>Sort by a column header ("frame", "#", or a column of <see cref="ColumnDefs"/>); a second call with the same header reverses the order.</summary>
    public void SortBy(string header)
    {
        if (header == SortHeader) SortAscending = !SortAscending; else { SortHeader = header; SortAscending = true; }
        Resort();
    }

    void Resort()
    {
        var data = AllRowsSource.Where(r => !r.IsSummary).ToList(); var sum = AllRowsSource.Where(r => r.IsSummary).ToList();
        if (SortHeader != null)
        {
            int ci = ColumnDefs.FindIndex(c => c.header == SortHeader); IEnumerable<AllRow> q;
            if (SortHeader == "frame") q = data.OrderBy(r => r.Name, Comparer<string>.Create(Qwlsi.Video.FrameList.NaturalCompare));
            else if (SortHeader == "#") q = data.OrderBy(r => r.Number);
            else q = data.OrderBy(r => double.IsNaN(r.Sort(ci)) ? double.MaxValue : r.Sort(ci));
            data = SortAscending ? q.ToList() : q.Reverse().ToList();
        }
        var keep = SelectedRow; Rows.Clear(); foreach (var r in data) Rows.Add(r); foreach (var r in sum) Rows.Add(r); if (keep != null && Rows.Contains(keep)) SelectedRow = keep;
    }

    // ------------------------------------------------------------------------------------------------ trend plot
    void BuildTrendChoices(bool[] sel)
    {
        foreach (var c in TrendChoices) c.PropertyChanged -= OnChoiceChanged; TrendChoices.Clear();
        void Add(TrendChoice c, bool on) { c.IsChecked = on; c.PropertyChanged += OnChoiceChanged; TrendChoices.Add(c); }
        Add(new TrendChoice { Name = "rms before removal [nm]", Kind = "rms0" }, false); Add(new TrendChoice { Name = "rms after removal [nm]", Kind = "rms1" }, true); Add(new TrendChoice { Name = "astigmatism [nm rms]", Kind = "astig" }, false);
        int defJ = sel.Length > 4 && sel[4] ? 4 : Enumerable.Range(0, sel.Length).FirstOrDefault(j => sel[j], -1);       // default term: defocus if it is selected, else the first selected one
        foreach (var t in ZernikeBasis.Terms(10).OrderByDescending(t => t.J < sel.Length && sel[t.J]).ThenBy(t => t.J)) Add(new TrendChoice { Name = $"Z{t.J} {t.Name} [nm]", J = t.J }, t.J == defJ);
        RebuildTrend();
    }

    void OnChoiceChanged(object? s, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(TrendChoice.IsChecked)) RebuildTrend(); }

    void RebuildTrend()
    {
        if (Table == null) { TrendSeries = null; return; }
        var list = new List<TrendSeries>();
        foreach (var c in TrendChoices.Where(c => c.IsChecked))
        {
            double[] y = c.Kind switch { "rms0" => Table.Rows.Select(r => r.RmsBeforeNm).ToArray(), "rms1" => Table.Rows.Select(r => r.RmsAfterNm).ToArray(), "astig" => Table.Rows.Select(r => r.AstigRmsNm).ToArray(), _ => Table.Rows.Select(r => r.CoeffM[c.J] * 1e9).ToArray() };
            list.Add(new TrendSeries { Name = c.Name, Y = y });
        }
        TrendSeries = list;
    }

    // ------------------------------------------------------------------------------------------------ clicked frame
    int showVersion;
    /// <summary>Selects frame `number` (1-based in the table) everywhere (the Offline selection drives the single-frame Zernike view and the 3-D tab) and shows its original / removed / filtered maps.</summary>
    public void SelectFrameNumber(int number)
    {
        if (Table == null || number < 1 || number > items.Count) return; var it = items[number - 1]; MarkerFrame = number;
        if (!ReferenceEquals(main.Offline.SelectedFile, it)) main.Offline.SelectedFile = it;
        if (SelectedRow == null || SelectedRow.Number != number) SelectedRow = Rows.FirstOrDefault(r => !r.IsSummary && r.Number == number);
        _ = ShowMapsAsync(it, number);
    }

    async Task ShowMapsAsync(FileItem it, int number)
    {
        int v = ++showVersion; var table = Table!; double pitch = main.Settings.Params.PitchM; var cmap = main.Offline.Cmap; MapsCaption = $"Frame {number}: {it.Name} — loading…";
        try
        {
            var (a, b, c, text) = await Task.Run(() =>
            {
                var oc = main.Offline.LoadResult(it); var zr = table.MakeRemoval(oc, pitch); int M = oc.Result.M; var raw = oc.Result.Opd; var fil = zr.Filtered; int n = raw.Length;
                var o = new float[n]; var f = new float[n]; var r = new float[n]; for (int i = 0; i < n; i++) { o[i] = raw[i] * 1e9f; f[i] = fil[i] * 1e9f; r[i] = (raw[i] - fil[i]) * 1e9f; }
                MapSpec S(float[] d, string title) { var (lo, hi) = Colormap.AutoRange(d); return new MapSpec { Data = d, Width = M, Height = M, Cmap = cmap, VMin = lo, VMax = hi, Unit = "nm", Title = title }; }
                return (S(o, "original"), S(r, "removed Zernike surface"), S(f, "filtered"), $"Frame {number}: {it.Name} — rms {zr.RmsBeforeM * 1e9:F1} → {zr.RmsAfterM * 1e9:F1} nm, PV {zr.PvBeforeM * 1e6:F3} → {zr.PvAfterM * 1e6:F3} µm ({zr.Fit.PupilDescription})");
            });
            if (v != showVersion) return; OriginalSpec = a; RemovedSpec = b; FilteredSpec = c; MapsCaption = text;
        }
        catch (Exception e) { if (v == showVersion) MapsCaption = "Could not show the maps: " + e.Message; AppLog.Error("all-frames maps failed", e); }
    }

    // ------------------------------------------------------------------------------------------------ exports
    [RelayCommand]
    void ExportCsv()
    {
        if (Table == null) { Message = "Nothing to export: press 'Apply selected removal to all frames' first."; return; }
        string dir = main.Offline.ExportFolderPath();
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "zernike_table.csv", InitialDirectory = Directory.Exists(dir) ? dir : null }; if (dlg.ShowDialog() != true) return;
        try { ZernikeTableCsv.Write(dlg.FileName, Table); Message = "Table saved: " + dlg.FileName; } catch (Exception e) { Message = "Save failed: " + e.Message; }
    }

    [RelayCommand]
    async Task ExportAllFiltered()
    {
        if (IsBusy) return;
        if (Table == null || IsStale) { if (!await BuildAsync()) return; }
        var table = Table!; var files = items; var off = main.Offline; var par = main.Settings.Params.Clone(); var opts = off.Export; string folder = off.ExportFolderPath();
        IsBusy = true; main.IsBusy = true; cts = new CancellationTokenSource(); var ct = cts.Token; Progress = 0; Message = "";
        var prog = new Progress<(int done, int total)>(p => { Progress = p.total == 0 ? 0 : p.done / (double)p.total; ProgressText = $"Exporting frame {p.done}/{p.total}"; });
        try
        {
            var loaders = off.FrameLoaders(files); var written = await Task.Run(() => ZernikeBatchExport.Run(loaders, table, par, opts, folder, prog, ct));
            Message = $"Exported {written.Count} file(s) for {files.Count} frame(s) to {folder} (filtered OPD, table zernike_table.csv)."; AppLog.Info("zernike all-frames export: " + Message);
        }
        catch (OperationCanceledException) { Message = "Cancelled."; }
        catch (Exception e) { Message = "Export failed: " + e.Message; AppLog.Error("zernike all-frames export failed", e); }
        finally { IsBusy = false; main.IsBusy = false; ProgressText = ""; Processor.TrimProcessMemory(); }
    }

    [RelayCommand] void OpenExportFolder() => main.Offline.OpenExportFolderCommand.Execute(null);
}
